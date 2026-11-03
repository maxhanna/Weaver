using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Weaver.Services;
using static Weaver.Services.AgentTokenMetrics;
using static Weaver.Services.AgentEditHeuristics;
using static Weaver.Services.AgentPlanParsing;
using static Weaver.Services.AgentMethodInventory;
using static Weaver.Services.AgentProjectUtilities;
using static Weaver.Services.AgentDiscovery;
using static Weaver.Services.AgentTextUtilities;
using static Weaver.Services.AgentCodeFormatting;
using static Weaver.Services.AgentSkeleton;
using static Weaver.Services.AgentDiffUtilities;
using static Weaver.Services.AgentJsonUtilities;
using Weaver;
namespace Weaver.Controllers;

partial class AgentController
{
    private async Task<(List<object> steps, AgentPlan? plan)> CommandExecutionPipeline(
        string prompt, string projectRoot, bool emitSse, CancellationToken ct,
        string? steeringContext = null, string? cardId = null, AgentRunContext? runContext = null)
    {
        runContext ??= new AgentRunContext();
        var steps = new List<object>();
        var fastPlan = AgentPlanParsing.TryDetectSimpleIntent(prompt);
        if (fastPlan != null)
        {
            await EmitLog(emitSse, "info", $"CommandExecution (fast): {fastPlan.Plan.Count} step(s)", ct: ct);
            if (emitSse) await SendSse(Response, "plan", new { thinking = fastPlan.Thinking, summary = fastPlan.Summary, items = fastPlan.Plan }, ct);
            await ExecutePlan(prompt, projectRoot, emitSse, "", fastPlan, ct, steps);
            return (steps, fastPlan);
        }
        await EmitLog(emitSse, "info", "CommandExecution (agentic): LLM has terminal control", ct: ct);
        _terminal.Start();
        var isWindows = OperatingSystem.IsWindows();
        var shellName = isWindows ? "PowerShell" : "Bash";
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        // OS-filesystem tasks must NOT be redirected back into the repo — that is what made
        // desktop writes land inside the project folder. For those, keep absolute desktop paths.
        var osTaskPrompt = IsExternalFilesystemTask(prompt);
        var baseInstructions = new StringBuilder();
        baseInstructions.AppendLine("You are a senior terminal automation agent. You have full terminal access and must complete the user's task end-to-end.");
        baseInstructions.AppendLine($"You are running on {shellName} ({Environment.OSVersion}).");
        baseInstructions.AppendLine("Output ONLY valid JSON. Options:");
        baseInstructions.AppendLine("  {\"cmd\": \"the full command\"}        # PREFERRED — use curl/Invoke-WebRequest for API calls");
        baseInstructions.AppendLine("  {\"web_fetch\": \"url\"}               # PREFERRED — fetch a known URL directly");
        baseInstructions.AppendLine("  {\"web_search\": \"query\"}            # LAST RESORT — only if you don't know the URL");
        baseInstructions.AppendLine("  {\"message\": \"answer for user\"}");
        baseInstructions.AppendLine("  {\"plan\": [{\"file\": \"command/web_search/web_fetch\", \"change\": \"description\"}]}  # First: create a plan of steps");
        baseInstructions.AppendLine("  {\"done\": true, \"summary\": \"what was accomplished\"}");
        baseInstructions.AppendLine($"Desktop: {desktopPath}");
        baseInstructions.AppendLine($"Project: {projectRoot}");
        if (isWindows)
        {
            baseInstructions.AppendLine("CRITICAL: Each cmd runs in a separate PowerShell session — state does NOT persist between commands. If you read data in one cmd and need it in the next, save to a temp file: Get-Content ... | Set-Content _temp_step1.txt");
            baseInstructions.AppendLine("For files: New-Item -ItemType File -Path \"<path>\" -Force  (NOT mkdir)");
            baseInstructions.AppendLine("For folders: New-Item -ItemType Directory -Path \"<path>\" -Force");
            baseInstructions.AppendLine("Inspect before acting: for repository questions use fast file commands first. Prefer `rg --files` to enumerate files, `rg -n \"pattern\" <path>` to search text, and `Get-Content -TotalCount/-Tail` for bounded reads. If `rg` is unavailable, use PowerShell equivalents.");
            baseInstructions.AppendLine("For well-known REST APIs (pokeapi.co, jsonplaceholder, github api, etc.) use Invoke-RestMethod/curl via cmd — NOT web_search. web_search is only for finding URLs or info you don't already know.");
        }
        else
        {
            baseInstructions.AppendLine("CRITICAL: Each command runs in a fresh bash shell — state does NOT persist between commands. If you read data in one command and need it in the next, save to a temp file: cat _temp_step1.txt");
            baseInstructions.AppendLine("For files: use `touch \"<path>\"` or `echo \"<content>\" > \"<path>\"`");
            baseInstructions.AppendLine("For folders: use `mkdir -p \"<path>\"`");
            baseInstructions.AppendLine("Inspect before acting: for repository questions use fast file commands first. Prefer `rg --files` to enumerate files, `rg -n \"pattern\" <path>` to search text, and `head -n`/`tail -n` for bounded reads.");
            baseInstructions.AppendLine("For well-known REST APIs (pokeapi.co, jsonplaceholder, github api, etc.) use curl — NOT web_search. web_search is only for finding URLs or info you don't already know.");
        }
        baseInstructions.AppendLine("If this task's results will feed into a subsequent code-editing step, save output files INSIDE the project directory (use a temp path like \"_temp_data.json\") so the next pipeline can read them. The file will be attached to the card automatically.");
        baseInstructions.AppendLine("NEVER use cd — use absolute paths");
        baseInstructions.AppendLine("Keep outputs small and useful. Limit broad searches, exclude bin/obj/node_modules/.git/dist, and save large raw outputs to a project temp file instead of dumping them into the conversation.");
        baseInstructions.AppendLine("After every command, decide what new fact was learned and what exact next step follows. Do not repeat failed commands without changing the hypothesis or command.");
        baseInstructions.AppendLine("BEFORE planning the first step, assess the full task end-to-end. What data do you need? What files will be created? What merge/transform/verification steps are needed? Plan the smallest complete chain, usually 1-4 steps.");
        baseInstructions.AppendLine("KEEP THE ORIGINAL TASK AS YOUR NORTH STAR. After each step, check: does this complete the user's request yet? If the planned steps do not add up to finishing the task, add the remaining steps. If your plan covers the full task, execute the steps — do NOT keep planning new steps.");
        if (isWindows)
        {
            baseInstructions.AppendLine("You are on WINDOWS PowerShell. Platform differences:");
            baseInstructions.AppendLine("  - Use `Invoke-RestMethod <url>` NOT curl (curl is an alias for Invoke-WebRequest in PowerShell)");
            baseInstructions.AppendLine("  - Use `ConvertFrom-Json` ONLY with curl/Invoke-WebRequest (raw JSON). Invoke-RestMethod already parses JSON — do NOT pipe it to ConvertFrom-Json.");
            baseInstructions.AppendLine("  - Use `| Set-Content -Path <file>` or `| Out-File -FilePath <file>` NOT > redirect");
            baseInstructions.AppendLine("  - Working example: Invoke-RestMethod https://pokeapi.co/api/v2/pokemon?limit=1000 | Select-Object -ExpandProperty results | ForEach-Object { $_.name } | Set-Content C:\\Users\\Saint\\Desktop\\pokemon.csv");
        }
        else
        {
            baseInstructions.AppendLine("You are on LINUX/MAC Bash. Use curl + jq.");
        }
        if (!string.IsNullOrWhiteSpace(steeringContext)) { baseInstructions.AppendLine("### Steering ###"); baseInstructions.AppendLine(steeringContext); }
        baseInstructions.AppendLine($"Task: {prompt}");
        var stepIndex = 0; string? summary = null;
        var usedSearchQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var planSteps = new List<PlanStep>();
        var completedPlanSteps = new HashSet<int>();
        var totalPlanSteps = 0;
        var consecutiveErrors = 0;
        var conversation = new StringBuilder();
        conversation.Append(baseInstructions);
        conversation.AppendLine("\nPlan the smallest complete chain of remaining steps. Do NOT repeat steps already in the plan. Output:");
        conversation.AppendLine("  {\"plan\": [{\"file\": \"<output path or tool>\", \"change\": \"what to do and how you will verify it\"}]}  # add needed new steps");
        conversation.AppendLine("  {\"cmd\": \"...\"} / {\"web_fetch\": \"...\"} / {\"web_search\": \"...\"}  # execute directly");
        conversation.AppendLine("  {\"step\": N}  # explicitly mark step N done (if current approach failed but you want a different one)");
        conversation.AppendLine("  {\"done\": true, \"summary\": \"...\"}  # finish");
        conversation.AppendLine("After each action, verify if the step\'s objective was met using concrete output, file existence, or a bounded read. If a step errors, change approach or mark it done before trying a different route.");
        conversation.AppendLine("IMPORTANT: Check the PLAN section above before adding new steps. If a step is already in the plan, DO NOT add it again.");
        // The conversation-compaction threshold derives from the endpoint's context window
        // (config: contextWindowTokens) — load it once for the whole run instead of per-turn.
        var pipelineCfg = await LoadConfigAsync();
        for (var i = 0; i < MAX_COMMAND_ITERATIONS; i++)
        {
            ct.ThrowIfCancellationRequested();
            AgentTokenMetrics.CompactConversation(conversation, pipelineCfg.contextWindowTokens);
            var (raw, _, err) = await CallLlmRaw(
                "You are a terminal agent. Output only JSON.",
                conversation.ToString(), ct, _infiniteTimeout, llmRoundLabel: $"command step {i + 1}", runContext: runContext);
            if (string.IsNullOrWhiteSpace(raw)) { summary ??= "Completed with issues"; break; }
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```")) { var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) cleaned = m.Groups[1].Value.Trim(); }
            var jsonOpts = new JsonDocumentOptions { AllowTrailingCommas = true };
            string? jsonToParse = null;
            var candidates = new List<string> { cleaned };
            foreach (var block in AgentJsonUtilities.ExtractJsonBlocks(cleaned)) if (!candidates.Contains(block)) candidates.Add(block);
            foreach (var c in candidates.ToList()) { var rep = AgentJsonUtilities.RepairJsonString(c); if (rep != null && !candidates.Contains(rep)) candidates.Add(rep); }
            foreach (var candidate in candidates) { if (string.IsNullOrWhiteSpace(candidate)) continue; try { JsonDocument.Parse(candidate, jsonOpts); jsonToParse = candidate; break; } catch (JsonException) { } }
            if (jsonToParse == null) { conversation.AppendLine("Could not parse JSON."); continue; }
            using var doc = JsonDocument.Parse(jsonToParse, jsonOpts);
            var root = doc.RootElement;
            if (root.TryGetProperty("plan", out var pArr) && pArr.ValueKind == JsonValueKind.Array && pArr.GetArrayLength() > 0)
            {
                var newSteps = new List<PlanStep>();
                foreach (var item in pArr.EnumerateArray())
                    newSteps.Add(new PlanStep
                    {
                        File = item.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "",
                        Change = item.TryGetProperty("change", out var c) ? c.GetString() ?? "" : "",
                        LineNumber = item.TryGetProperty("line", out var ln) ? ln.GetInt32() : 0
                    });
                var deduped = newSteps.Where(ns =>
                    !planSteps.Any(ps =>
                        string.Equals(ps.File, ns.File, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals((ps.Change ?? "").Trim(), (ns.Change ?? "").Trim(), StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (deduped.Count == 0)
                {
                    if (totalPlanSteps > 0 && completedPlanSteps.Count >= totalPlanSteps)
                        conversation.AppendLine("All plan steps are already completed. If the task is done, respond with: {\"done\": true, \"summary\": \"...\"}");
                    else
                        conversation.AppendLine("Step(s) already in plan — not added. Execute existing steps or plan something new.");
                    continue;
                }
                planSteps.AddRange(deduped);
                totalPlanSteps = planSteps.Count;
                await EmitLog(emitSse, "info", $"Plan: +{deduped.Count} step(s) -- {string.Join(", ", deduped.Select(p => p.File))}", ct: ct);
                if (emitSse)
                    await SendSse(Response, "plan", new
                    {
                        thinking = "Planned steps",
                        summary = string.Join(" -> ", planSteps.Select(p => p.Change)),
                        items = planSteps.Select(p => new { file = p.File, change = p.Change, priority = 1, line = p.LineNumber }).ToList()
                    }, ct);
                conversation.AppendLine($"\n### PLAN UPDATED ({totalPlanSteps} total steps) ###");
                for (var pi = 0; pi < planSteps.Count; pi++)
                    conversation.AppendLine($"  Step {pi + 1}: [{planSteps[pi].File}] {planSteps[pi].Change}");
                conversation.AppendLine("### END PLAN ###");
                for (var pi = 0; pi < planSteps.Count; pi++)
                {
                    if (completedPlanSteps.Contains(pi)) continue;
                    var step = planSteps[pi];
                    var changeLower = (step.Change ?? "").Trim().ToLowerInvariant();
                    bool isVerification = changeLower.StartsWith("verify") ||
                        changeLower.StartsWith("check") ||
                        changeLower.StartsWith("test that") ||
                        changeLower.StartsWith("validate") ||
                        changeLower.StartsWith("confirm") ||
                        changeLower.StartsWith("ensure");
                    var pathRule = osTaskPrompt
                        ? $"The task targets the OS filesystem OUTSIDE the repo. Write to the location the task asks for using ABSOLUTE paths (Desktop: {desktopPath}). Do NOT redirect those paths into the project directory."
                        : "ALL files and folders must be created INSIDE this working directory — translate desktop paths to this directory.";
                    var translatePrompt = $"You are running on {shellName} ({Environment.OSVersion}).\nThe working directory (project root) is: {projectRoot}\n{pathRule}\n\nTranslate this task step into a SINGLE terminal command. Output ONLY the command, no explanations, no markdown:\n\nStep {pi + 1}: [{step.File}] {step.Change}";
                    var (cmdRaw, _, _) = await CallLlmRaw(
                        "You are a terminal command translator. Output only the command, no markdown, no explanation.",
                        translatePrompt, ct, _infiniteTimeout);
                    if (string.IsNullOrWhiteSpace(cmdRaw)) continue;
                    var cmdClean = cmdRaw.Trim();
                    if (cmdClean.StartsWith("```")) cmdClean = cmdClean.Split('\n').LastOrDefault()?.Replace("```", "").Trim() ?? cmdClean;
                    var freshOut = await RunMarkerCommandWithRetryAsync(cmdClean, projectRoot, emitSse, ct, waitMinutes: 5);
                    if (string.IsNullOrWhiteSpace(freshOut)) freshOut = "(ok)";
                    var isError = !string.IsNullOrWhiteSpace(freshOut) &&
                        Regex.IsMatch(freshOut.ToLowerInvariant(),
                            @"not recognized|not found|cannot find|terminate|error|exception|failed|access denied|permission denied");
                    if (isVerification)
                    {
                        conversation.AppendLine($"→ Verified step {pi + 1}: {cmdClean}");
                        conversation.AppendLine($"  Result: {AgentTextUtilities.Truncate(freshOut, 300)}");
                        if (!isError) completedPlanSteps.Add(pi);
                        else
                        {
                            conversation.AppendLine($"  Verification found issues — step {pi + 1} may need attention");
                            completedPlanSteps.Add(pi);
                        }
                        var vResult = new Dictionary<string, object?>
                        {
                            ["index"] = stepIndex++,
                            ["type"] = "command",
                            ["command"] = cmdClean,
                            ["status"] = isError ? "warning" : "done",
                            ["output"] = freshOut
                        };
                        steps.Add(vResult);
                        if (emitSse) await SendSse(Response, "step", vResult, ct);
                        continue;
                    }
                    if (isError)
                    {
                        var errorText = freshOut.ToLowerInvariant();
                        bool isBenign = errorText.Contains("already exists")
                            || (errorText.Contains("access denied") && errorText.Contains("already exists"));
                        if (isBenign)
                        {
                            completedPlanSteps.Add(pi);
                            var benignResult = new Dictionary<string, object?>
                            {
                                ["index"] = stepIndex++,
                                ["type"] = "plan_step",
                                ["planItemIndex"] = pi,
                                ["command"] = cmdClean,
                                ["status"] = "done",
                                ["output"] = freshOut
                            };
                            steps.Add(benignResult);
                            if (emitSse) await SendSse(Response, "step", benignResult, ct);
                            await PersistBoardDataPlanStepAsync(cardId, pi, emitSse, ct, projectRoot: projectRoot);
                            conversation.AppendLine($"→ Step {pi + 1} OK (already existed): {cmdClean}");
                            conversation.AppendLine($"  Output: {AgentTextUtilities.Truncate(freshOut, 300)}");
                        }
                        else
                        {
                            conversation.AppendLine($"→ Step {pi + 1} FAILED: {cmdClean}");
                            conversation.AppendLine($"  Error: {AgentTextUtilities.Truncate(freshOut, 500)}");
                            conversation.AppendLine("  The step above failed. If you know a different command or approach, output a new plan step to recover. Otherwise mark it done with {\"step\": " + (pi + 1) + "} and move on.");
                            var errResult = new Dictionary<string, object?>
                            {
                                ["index"] = stepIndex++,
                                ["type"] = "plan_step",
                                ["planItemIndex"] = pi,
                                ["command"] = cmdClean,
                                ["status"] = "error",
                                ["output"] = freshOut
                            };
                            steps.Add(errResult);
                            if (emitSse) await SendSse(Response, "step", errResult, ct);
                        }
                        continue;
                    }
                    completedPlanSteps.Add(pi);
                    var result = new Dictionary<string, object?>
                    {
                        ["index"] = stepIndex++,
                        ["type"] = "plan_step",
                        ["planItemIndex"] = pi,
                        ["command"] = cmdClean,
                        ["status"] = "done",
                        ["output"] = freshOut
                    };
                    steps.Add(result);
                    if (emitSse) await SendSse(Response, "step", result, ct);
                    await PersistBoardDataPlanStepAsync(cardId, pi, emitSse, ct, projectRoot: projectRoot);
                    conversation.AppendLine($"→ Auto-executed step {pi + 1}: {cmdClean}");
                    conversation.AppendLine($"  Output: {AgentTextUtilities.Truncate(freshOut, 500)}");
                }
                if (completedPlanSteps.Count >= totalPlanSteps)
                {
                    summary = "All plan steps completed";
                    break;
                }
                continue;
            }
            if (root.TryGetProperty("step", out var stepEl) && stepEl.ValueKind == JsonValueKind.Number)
            {
                var stepNum = stepEl.GetInt32();
                if (stepNum >= 1 && stepNum <= totalPlanSteps && completedPlanSteps.Add(stepNum - 1))
                {
                    conversation.AppendLine("-> Step " + stepNum + " marked done.");
                    if (emitSse)
                        await SendSse(Response, "step", new { index = stepIndex, type = "plan_step", planItemIndex = stepNum - 1, status = "done" }, ct);
                    await PersistBoardDataPlanStepAsync(cardId, stepNum - 1, emitSse, ct, projectRoot: projectRoot);
                }
                continue;
            }
            if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
            {
                summary = root.TryGetProperty("summary", out var s) ? s.GetString() : "Task complete";
                break;
            }
            if (root.TryGetProperty("cmd", out var cmdEl) || root.TryGetProperty("command", out cmdEl))
            {
                var cmd = cmdEl.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(cmd)) { conversation.AppendLine("Empty command - try again."); continue; }
                if ((cmd.Contains('\n') || cmd.Contains('\r')) && !cmd.Contains("@\""))
                {
                    var san = cmd.Replace("\r\n", "; ").Replace("\r", "; ").Replace("\n", "; ");
                    await EmitLog(emitSse, "info", "newlines in cmd - joined", ct: ct);
                    cmd = san;
                }
                var cmdLower = cmd.TrimStart().ToLowerInvariant();
                if (cmdLower.StartsWith("mkdir") && Regex.IsMatch(cmd, @"\.\w{2,4}[""'\s]|\.\w{2,4}$"))
                {
                    conversation.AppendLine(isWindows
                        ? "REJECTED: mkdir creates DIRECTORIES. Use: New-Item -ItemType File -Path \"<path>\" -Force"
                        : "REJECTED: mkdir creates DIRECTORIES. Use: touch \"<path>\" or echo \"<content>\" > \"<path>\"");
                    continue;
                }
                if (cmdLower == "cd" || cmdLower.StartsWith("cd ") || cmdLower.Contains("set-location"))
                { conversation.AppendLine("REJECTED: cd/Set-Location not supported. Use absolute paths."); continue; }
                var freshOut = await RunMarkerCommandWithRetryAsync(cmd, projectRoot, emitSse, ct, waitMinutes: 10);
                var isError = !string.IsNullOrWhiteSpace(freshOut) &&
                    Regex.IsMatch(freshOut.ToLowerInvariant(),
                        @"not recognized|not found|cannot find|terminate|error|exception|failed|access denied|permission denied");
                var result = new Dictionary<string, object?>
                { ["index"] = stepIndex++, ["type"] = "command", ["command"] = cmd, ["status"] = isError ? "error" : "done", ["output"] = freshOut };
                steps.Add(result);
                if (emitSse) await SendSse(Response, "step", result, ct);
                conversation.AppendLine("Command [" + (i + 1) + "]: " + cmd);
                conversation.AppendLine(isError ? "Error:" : "Output:");
                conversation.AppendLine(freshOut);
                if (isError && freshOut.Contains("ConvertFrom-Json"))
                    conversation.AppendLine("Hint: Invoke-RestMethod already parses JSON - remove ConvertFrom-Json from the pipeline.");
                if (isError && freshOut.Contains("already exists"))
                    conversation.AppendLine("Hint: The file already exists. Use -Force flag or a different path.");
                if (isError) consecutiveErrors++;
                else
                {
                    if (totalPlanSteps > 0 && completedPlanSteps.Count < totalPlanSteps)
                    {
                        var advStep = completedPlanSteps.Count;
                        if (completedPlanSteps.Add(advStep))
                        {
                            if (emitSse)
                                await SendSse(Response, "step", new { index = stepIndex, type = "plan_step", planItemIndex = advStep, status = "done" }, ct);
                            await PersistBoardDataPlanStepAsync(cardId, advStep, emitSse, ct, projectRoot: projectRoot);
                        }
                    }
                    consecutiveErrors = 0;
                }
                continue;
            }
            if (root.TryGetProperty("web_search", out var searchEl))
            {
                var query = searchEl.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(query)) { conversation.AppendLine("Empty query."); continue; }
                if (!usedSearchQueries.Add(query)) { conversation.AppendLine("Already searched for \"" + query + "\". Use the results above."); continue; }
                var (searchOut, _) = await ExecuteWebSearchAsync(query, null, ct);
                var wr = new Dictionary<string, object?> { ["index"] = stepIndex++, ["type"] = "web_search", ["query"] = query, ["status"] = "done", ["output"] = searchOut };
                var wrMetrics = TakeStepLlmMetricsForRun(runContext);
                if (wrMetrics != null) wr["llmTokens"] = wrMetrics;
                steps.Add(wr);
                if (emitSse)
                {
                    var (searchCapped, searchTrunc) = CapWebStepOutputForClient(searchOut);
                    await SendSse(Response, "step", new Dictionary<string, object?>(wr) { ["output"] = searchCapped, ["truncated"] = searchTrunc }, ct);
                }
                conversation.AppendLine("Web search [" + (i + 1) + "]: " + query + "\nResults:\n" + searchOut);
                continue;
            }
            if (root.TryGetProperty("web_fetch", out var fetchEl))
            {
                var url = fetchEl.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(url)) { conversation.AppendLine("Empty URL."); continue; }
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != "http" && uri.Scheme != "https"))
                {
                    conversation.AppendLine("Invalid URL: \"" + url + "\" - must be http/https. Provide a real URL.");
                    consecutiveErrors++;
                    continue;
                }
                var (fetchOut, fetchErr) = await WebFetchAsync(url, ct);
                var isFetchError = fetchOut.StartsWith("HTTP 4") || fetchOut.StartsWith("HTTP 5") ||
                    (!string.IsNullOrWhiteSpace(fetchErr) && (fetchErr.Contains("404") || fetchErr.Contains("500")));
                var fr = new Dictionary<string, object?> { ["index"] = stepIndex++, ["type"] = "web_fetch", ["url"] = url, ["status"] = isFetchError ? "error" : "done", ["output"] = fetchOut };
                var frMetrics = TakeStepLlmMetricsForRun(runContext);
                if (frMetrics != null) fr["llmTokens"] = frMetrics;
                steps.Add(fr);
                if (emitSse)
                {
                    var (fetchCapped, fetchTrunc) = CapWebStepOutputForClient(fetchOut);
                    await SendSse(Response, "step", new Dictionary<string, object?>(fr) { ["output"] = fetchCapped, ["truncated"] = fetchTrunc }, ct);
                }
                if (isFetchError)
                {
                    conversation.AppendLine("Fetch error [" + (i + 1) + "]: " + url + "\n" + fetchOut);
                    consecutiveErrors++;
                }
                else
                {
                    conversation.AppendLine("Fetch [" + (i + 1) + "]: " + url + "\n" + fetchOut);
                }
                continue;
            }
            if (root.TryGetProperty("message", out var msgEl) || root.TryGetProperty("result", out msgEl))
            {
                var msgText = msgEl.GetString() ?? "";
                var mr = new Dictionary<string, object?> { ["index"] = stepIndex++, ["type"] = "message", ["output"] = msgText };
                steps.Add(mr); if (emitSse) await SendSse(Response, "step", mr, ct);
                conversation.AppendLine("Message: " + msgText);
                continue;
            }
            conversation.AppendLine("Unrecognized JSON - use cmd, web_search, web_fetch, message, done, or plan.");
        }
        summary ??= "Command execution completed (" + steps.Count + " steps)";
        await EmitLog(emitSse, "info", summary, steps, ct: ct);
        steps.Add(new Dictionary<string, object?> { ["type"] = "done_signal", ["status"] = "done" });
        var agentPlan = planSteps != null && planSteps.Count > 0
            ? new AgentPlan { Plan = planSteps, Summary = summary, Thinking = "Command execution plan" }
            : null;
        return (steps, agentPlan);
    }
}
