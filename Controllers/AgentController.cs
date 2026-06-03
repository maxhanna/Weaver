using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MaestroBackend.Services;
using MaestroBackend;

[ApiController]
[Route("api/agent")]
public class AgentController : ControllerBase
{

    private readonly IHttpClientFactory _clientFactory;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly TerminalService _terminal;
    private readonly FileHintsManager _fileHints;
    private readonly ConfigFileService _configFile;
    private readonly EmailService _emailService;
    private const int MaxFileContextChars = 24_000;
    private const int MAX_COMMAND_ITERATIONS = 30;
    private bool _lastConnectionCheckResult = true;
    private static DateTime _nextConnectivityCheck = DateTime.MinValue;
    private static TimeSpan _infiniteTimeout = Timeout.InfiniteTimeSpan;
    private static readonly ConcurrentDictionary<string, PendingQuestion> _pendingQuestions = new();
    private static readonly ConcurrentDictionary<string, PendingContextReview> _pendingContextReviews = new();
    private static readonly string[] UnsafeEditMarkers =
    {
        "…(truncated)",
        "â€¦(truncated)",
        "...(truncated)"
    };

    public AgentController(
        IHttpClientFactory cf, IConfiguration config,
        IWebHostEnvironment env, TerminalService terminal, FileHintsManager fileHints,
        ConfigFileService configFile, EmailService emailService)
    {
        _clientFactory = cf;
        _config = config;
        _env = env;
        _terminal = terminal;
        _fileHints = fileHints;
        _configFile = configFile;
        _emailService = emailService;
    }
 
    private string ResolveWorkspaceRoot()
    {
        var configuredRoot = _config.GetValue<string>("Editor:WorkspaceRoot");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            return Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.GetFullPath(Path.Combine(_env.ContentRootPath, configuredRoot));
        return Path.GetFullPath(Path.Combine(_env.ContentRootPath, ".."));
    }

    private string GetProjectRoot(string project)
    {
        var workspaceRoot = ResolveWorkspaceRoot();
        var projectSegment = string.IsNullOrWhiteSpace(project) ? "" :
            project.Trim().TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(workspaceRoot, projectSegment));
    }

     // ═════════════════════════════════════════════════════════════════════════
    //  SSE / LOGGING
    // ═════════════════════════════════════════════════════════════════════════

    private async Task EmitLog(bool emit, string level, string message, object? detail = null, CancellationToken ct = default)
    {
        if (!emit) return;
        await SendSse(Response, "log", new { ts = DateTime.UtcNow.ToString("o"), level, message, detail }, ct);
    }

    private static async Task SendSse(HttpResponse response, string eventName, object data, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(data);
        await response.WriteAsync($"event: {eventName}\n" +
            $"data: {json}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
 
 
    private async Task<(string discoveryText, List<object> steps)> RunLightBootstrap(
        List<string> attachedFiles, string projectRoot, bool emitSse)
    {
        await EmitLog(emitSse, "info", "Fast-path bootstrap: reading attached files only");

        var plan = (attachedFiles ?? new List<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select((f, i) => new AgentStep { Index = i, Type = "read", Path = f.Replace('\\', '/'), Description = $"Read attached {f}" })
            .ToList();

        if (plan.Count == 0) return ("", new List<object>());

        var steps = await ExecuteSteps(plan, projectRoot, 0, emitSse);
        var sb = new StringBuilder();
        sb.AppendLine("Attached files (edit these paths only):");
        foreach (var f in attachedFiles ?? new List<string>()) sb.AppendLine($"  - {f.Replace('\\', '/')}");
        foreach (var item in steps)
        {
            if (item is Dictionary<string, object?> r && r.TryGetValue("output", out var o) && o != null)
                sb.AppendLine($"\n### {r.GetValueOrDefault("path")}\n{o.ToString()}");
        }
        return (sb.ToString(), steps);
    }
 

    private async Task<AgentPlan?> AnalyzePromptAndPlanCodeChanges(
        string prompt, string discoveryContext, string projectRoot, bool emitSse, CancellationToken ct = default,
        string? steeringContext = null)
    {
        string planningPrompt = "You are a software-engineering agent. This is a task with instructions on how to solve the task. Your goal is to produce a JSON plan of specific code changes to accomplish the task, with file paths and exact code edits or commands."
+ @"### AVAILABLE STEP TYPES (the ""file"" field) ###
  relative/path.ext     — Edit an existing file (must appear in discovery context below)
  _command              — Run any terminal command (PowerShell syntax)
  _create_file          — Create a new file: ""path.ext: describe what to generate""
  _web_search           — Search the web; put the query in ""change""
  _web_fetch            — Fetch a URL; put the full URL in ""change""
  _git                  — Git operation (commit, pull, push, revert, branch)
  _rename_file          — Rename a file: ""old → new""
  _move_file            — Move a file: ""old → new""
  _delete_file          — Delete a file: ""path.ext""
  _show                 — Display text to the user (Used for showing findings (usually terminal output); Must be used last if used at all.)
  _explore              — Explore deeper context: put a file path or glob pattern in ""change"". The system will read matching files and re-plan with the enriched context. Use this when you need to inspect files not yet in the discovery context. Output ONLY _explore steps when you need more context — no mixed steps.
  _done                 — Task is ALREADY complete; nothing needs changing.Use as the ONLY step
                          when the codebase already satisfies the requirement. Put the reason in ""change"".
  _checkpoint           — Divide a large refactor into phases. The system re-reads every file modified
                          so far and replans the remaining work with fresh file content. Insert between
                          independent phases when later steps depend on the result of earlier ones,
                          or when the plan spans >4 files and >8 steps total.
### GENERAL RULES ###
1. COMMANDS BEFORE EDITS — if you edit a file that doesn't exist yet, prepend _command (mkdir/New-Item) first
2. WEB BEFORE CODE — if you need current API docs/library versions, or recent information from the web, add _web_search at the TOP of the plan
3. EXACT LOCATIONS — for file edits, include exact line numbers or function/class names in ""change""
4. ATOMIC STEPS — one logical change per step, one step per file
5. REAL PATHS ONLY — only reference files that exist in the discovery context below
6. DO NOT RETURN ANYTHING ELSE BUT VALID JSON - no markdown fences, no explanation. No preambles. Do not return anything else besides the JSON object containing the plan. The JSON must be parseable by standard parsers without modification. If you cannot produce valid JSON, return an empty plan: { ""plan"": [] }
7. If the task cannot be fully completed yet with the given context information, output only 1 step type: _explore. 
8. Ignore oldString and newString in output for steps that are not relative/path.ext edits.
9. Always include oldString and newString for relative/path.ext edits (these are file editing steps).
10. CRITICAL — Include a ""score"" field (0-100) that represents how confident you are
    that this plan will accomplish 100% of the task. Be honest:
    - 100 = the plan will fully and correctly solve the task
    - 80-99 = minor concerns (edge cases, untested areas)
    - 50-79 = moderate concerns (missing steps, uncertain edits)
    - 0-49 = major gaps (insufficient context, unclear requirements)
11.SELF - STOP — If the existing code already satisfies the requirement, emit a single
   _done step. Never fabricate phantom edits.
12. PHASE CHECKPOINTS — For refactors touching >4 files or >8 steps, split into phases
    with _checkpoint between them. Each phase must produce a verifiable partial result."

+ @"### FILE EDIT RULES ###
EDIT SIZE LIMIT — strictly enforced, oversized edits score below 70 automatically:
  - oldString targets ONLY the line(s) that change. Hard limit: 5 lines OR 250 chars.
  - If a 30-line function has one line changing, oldString = that one line only.
  - newString is the replacement. Same 5-line / 250-char limit.
  - Never reproduce unchanged surrounding code in oldString.

CORRECT (small unique anchor):
  { ""file"": ""path.cs"", ""oldString"": ""return total;"", ""newString"": ""return total * taxRate;"" }

WRONG (large block where only one value changes — will be REJECTED):
  { ""oldString"": ""int Calculate() {\n  var x = 1;\n  var y = 2;\n  return total;"",
    ""newString"": ""int Calculate() {\n  var x = 1;\n  var y = 2;\n  return total * taxRate;"" }

SPLIT RULE: one file change = one step. Two separate change sites in the same file = two steps.
  Never expand oldString to cover multiple change sites — emit multiple steps instead.

For insertions: use an existing adjacent line as oldString anchor and include it unchanged in newString:
  { ""oldString"": ""var config = Load();"", ""newString"": ""var config = Load();\nValidate(config);"" }

- oldString must appear verbatim in the file (exact whitespace, exact indentation)
- oldString must never be blank for an existing file edit
- oldString and newString must NOT be identical
- For brand-new files use _create_file, not a file edit"

+ @"### EXAMPLE OUTPUT — respond with ONLY this type of JSON structure, no markdown, no extra text ###
{
  ""thinking"": ""<analyze the task; name files; state if commands or web searches are needed first>"",
  ""summary"":  ""<one sentence: what the completed plan will accomplish>"",
  ""score"": <0-100>,
  ""plan"": [
    { ""file"": ""_command"", ""change"": ""mkdir -p src/components/Button"", ""oldString"": """", ""newString"": """", ""priority"": 1 },
    { ""file"": ""relative/path"", ""change"": ""Line 5: add import ..."", ""oldString"": ""using System.Text;"", ""newString"": ""using System.Text;\nusing System.Text.Json;"", ""priority"": 2 }
  ]
}";

        var analysisPromptBuilder = new StringBuilder();
        analysisPromptBuilder.AppendLine("### TASK ###");
        analysisPromptBuilder.AppendLine(prompt);
        if (!string.IsNullOrWhiteSpace(steeringContext))
        {
            analysisPromptBuilder.AppendLine();
            analysisPromptBuilder.AppendLine("### USER STEERING INSTRUCTION ###");
            analysisPromptBuilder.AppendLine(steeringContext);
        }
        analysisPromptBuilder.AppendLine();
        analysisPromptBuilder.AppendLine("### END OF TASK ###");
        analysisPromptBuilder.AppendLine("### PROJECT ROOT ###");
        analysisPromptBuilder.AppendLine(projectRoot);
        analysisPromptBuilder.AppendLine("### PROJECT DISCOVERY (ONLY use paths listed here) ###");
        analysisPromptBuilder.AppendLine(discoveryContext);
        var analysisPrompt = analysisPromptBuilder.ToString();

        var (raw, _, llmError) = await CallLlmRaw(planningPrompt, analysisPrompt, ct, requestTimeout: _infiniteTimeout, maxTokens: 8192);

        if (string.IsNullOrWhiteSpace(raw))
        {
            await EmitLog(emitSse, "error", $"LLM returned empty response: {llmError ?? "no content"}", null, ct: ct);
            return null;
        }

        // Log non-trivial errors as warnings but keep going — we have content to parse.
        if (llmError != null && llmError != "JSON parse failed")
        {
            await EmitLog(emitSse, "warn", $"LLM warning during planning (proceeding anyway): {llmError}", raw, ct: ct);
        }

        var (bestPlan, parseError) = await ParseAndScore(raw, emitSse, ct);
        if (bestPlan == null)
        {
            await EmitLog(emitSse, "error", $"Failed to parse plan: {parseError ?? "unknown"}", raw, ct: ct);
            return null;
        }

        // ── Self-scoring retry loop: improve plan until score=100, max 3 replans ──
        var history = new List<(int score, string summary, string raw, string error)>();
        if (bestPlan.Score > 0)
            history.Add((bestPlan.Score, bestPlan.Summary, raw, ""));

        for (var attempt = 0; attempt < 3 && bestPlan.Score < 100; attempt++)
        {
            await EmitLog(emitSse, "info",
                $"Plan confidence score: {bestPlan.Score}/100 — attempt {attempt + 1}/3 to improve…", bestPlan, ct: ct);

            var retryPrompt = BuildScoreRetryPrompt(prompt, discoveryContext, projectRoot, history, steeringContext);
            var (retryRaw, _, _) = await CallLlmRaw(planningPrompt, retryPrompt, ct,
                requestTimeout: _infiniteTimeout, maxTokens: 8192);
            if (string.IsNullOrWhiteSpace(retryRaw))
            {
                history.Add((0, "", "[empty response]", "LLM returned empty response"));
                continue;
            }

            var (candidate, parseErr) = await ParseAndScore(retryRaw, emitSse, ct);
            if (candidate == null)
            {
                history.Add((0, "", retryRaw, parseErr ?? "Failed to parse as valid JSON"));
                continue;
            }

            history.Add((candidate.Score, candidate.Summary, retryRaw, ""));

            if (candidate.Score > bestPlan.Score)
            {
                bestPlan = candidate;
                await EmitLog(emitSse, "info", $"Improved to score {bestPlan.Score}/100", ct: ct);
            }

            if (bestPlan.Score >= 100) break;
        }

        if (bestPlan.Score < 100)
            await EmitLog(emitSse, "warn",
                $"Proceeding with plan score {bestPlan.Score}/100 after {history.Count} attempt(s)", ct: ct);
        else
            await EmitLog(emitSse, "info", $"Plan scores 100/100 — proceeding", ct: ct);

        return bestPlan;
    }

    private async Task<(AgentPlan? plan, string? error)> ParseAndScore(string raw, bool emitSse, CancellationToken ct)
    {
        // Strip markdown fences the LLM sometimes wraps its JSON in
        var cleanedRaw = raw.Trim();
        if (cleanedRaw.StartsWith("```"))
        {
            var fenceMatch = Regex.Match(cleanedRaw, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            cleanedRaw = fenceMatch.Success ? fenceMatch.Groups[1].Value.Trim() : cleanedRaw.TrimStart('`');
        }
        var parsedPlan = ParsePlan(cleanedRaw);
        if (parsedPlan == null)
        {
            await EmitLog(emitSse, "error", "Failed to parse plan.", cleanedRaw, ct: ct);
            return (null, "Failed to parse as valid JSON. Raw output contained no parseable plan block.");
        }
        var sizeViolations = GetPlanSizeViolations(parsedPlan);
        if (sizeViolations.Count > 0)
        {
            var penalty = Math.Min(sizeViolations.Count * 15, 45);
            parsedPlan.Score = Math.Max(0, parsedPlan.Score - penalty);
            // Embed violations in summary so BuildScoreRetryPrompt can surface them
            parsedPlan.Summary += "\n⚠ SIZE_VIOLATIONS:\n" +
                string.Join("\n", sizeViolations.Select(v => "  • " + v));
            await EmitLog(emitSse, "warn",
                $"Plan has {sizeViolations.Count} oversized edit(s) — score reduced to {parsedPlan.Score}", ct: ct);
        }
        return (parsedPlan, null);
    }

    private static string BuildScoreRetryPrompt(string originalPrompt, string discoveryContext, string projectRoot,
        List<(int score, string summary, string raw, string error)> history, string? steeringContext = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## ORIGINAL TASK");
        sb.AppendLine(originalPrompt);
        sb.AppendLine();
        
        if (!string.IsNullOrWhiteSpace(steeringContext))
        {
            sb.AppendLine("## USER STEERING INSTRUCTION");
            sb.AppendLine(steeringContext);
            sb.AppendLine();
        }

        var allViolations = history
            .SelectMany((h, i) => h.summary.Split('\n')
                .Where(l => l.TrimStart().StartsWith("• "))
                .Select(v => $"Attempt {i + 1}: {v.Trim()}"))
            .ToList();

        if (allViolations.Count > 0)
        {
            sb.AppendLine("### ⚠ EDIT SIZE FAILURES — fix these before anything else");
            sb.AppendLine();
            sb.AppendLine("WRONG — large anchor wrapping an unchanged block:");
            sb.AppendLine("  oldString: \"void Render() {\\n  Setup();\\n  Draw();\\n  return;\\n}\"");
            sb.AppendLine("  newString: \"void Render() {\\n  Setup();\\n  Draw();\\n  Flush();\\n  return;\\n}\"");
            sb.AppendLine();
            sb.AppendLine("CORRECT — anchor is only the line(s) that change:");
            sb.AppendLine("  oldString: \"  return;\"");
            sb.AppendLine("  newString: \"  Flush();\\n  return;\"");
            sb.AppendLine();
            sb.AppendLine("Specific violations to fix:");
            foreach (var v in allViolations) sb.AppendLine("  " + v);
            sb.AppendLine();
        }

        sb.AppendLine("## PREVIOUS PLAN ATTEMPTS AND FEEDBACK");
        sb.AppendLine("Your previous plan(s) did not score 100/100. Below is each attempt and what went wrong.");
        sb.AppendLine("Analyze the failures, then produce a NEW plan that fixes ALL issues.");
        sb.AppendLine();
        for (var i = 0; i < history.Count; i++)
        {
            var h = history[i];
            sb.AppendLine($"### Attempt {i + 1} — Score {h.score}/100");
            if (!string.IsNullOrEmpty(h.error))
            {
                sb.AppendLine($"⚠ ERROR: {h.error}");
            }
            if (!string.IsNullOrEmpty(h.summary))
            {
                sb.AppendLine($"Summary: {h.summary}");
            }
            sb.AppendLine(h.raw.Length > 3000
                ? "Raw output preview (first 3000 characters only; omitted remainder is not part of the output):"
                : "Raw output:");
            sb.AppendLine(h.raw.Length > 3000 ? h.raw[..3000] : h.raw);
            sb.AppendLine();
        }
        sb.AppendLine("### PROJECT ROOT ###");
        sb.AppendLine(projectRoot);
        sb.AppendLine("### PROJECT DISCOVERY (ONLY use paths listed here) ###");
        sb.AppendLine(discoveryContext);
        sb.AppendLine();
        sb.AppendLine("### RULES REMINDER ###");
        sb.AppendLine("1. Return ONLY valid JSON — no markdown fences, no extra text before/after");
        sb.AppendLine("2. Include a \"score\" field (0-100) that honestly reflects how confident you are this plan will fully solve the task");
        sb.AppendLine("3. Score must be 100 if you are confident the plan is correct and complete");
        sb.AppendLine("4. If previous attempts had parse errors, ensure your JSON is valid (no trailing commas, strings properly escaped)");
        sb.AppendLine("5. If previous attempts had low scores, identify the gaps and fix them");
        sb.AppendLine("6. oldString must be 1-5 lines of EXISTING code copied verbatim from the file — it must literally be in the file");
        sb.AppendLine("7. oldString and newString must NOT be identical");
        sb.AppendLine();
        sb.AppendLine("BEFORE writing the plan, think step by step: what was wrong with each previous attempt?");
        sb.AppendLine("Then produce a corrected plan that addresses every issue.");
        sb.AppendLine("If the plan is now complete and correct, set score to 100.");
        return sb.ToString();
    }

    private async Task<(string discoveryText, List<object> steps)> RunBootstrapDiscovery(
     string prompt, string projectRoot, bool emitSse, List<string>? attachedFiles = null,
     CancellationToken ct = default)
    {
        // Fast path: if files are already attached, skip full discovery
        if (attachedFiles != null && attachedFiles.Count > 0)
            return await RunLightBootstrap(attachedFiles, projectRoot, emitSse);

        await EmitLog(emitSse, "info", "Phase 1 — DISCOVER: enumerating project files…", ct: ct);

        var allSteps = new List<object>();

        // ── Step 1: List project root (fast, no LLM) ───────────────────────────
        var listStep = new AgentStep
        {
            Index = 0,
            Type = "list",
            Path = "",
            Description = "Auto: list project root"
        };
        var listResults = await ExecuteDiscoveryStepsConcurrent(
            new List<AgentStep> { listStep }, projectRoot, 0, emitSse);
        allSteps.AddRange(listResults);

        if (!Directory.Exists(projectRoot))
            return ("", allSteps);

        // ── Step 2: Enumerate all project files (fast, no LLM) ────────────────
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "node_modules", ".git", "bin", "obj", "dist", ".angular", "packages", ".vs", ".idea" };

        var allFiles = Directory.EnumerateFiles(projectRoot, "*.*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/'))
            .Where(rel => !skipDirs.Any(d =>
                rel.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase) ||
                rel.Contains("/" + d + "/", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (allFiles.Count == 0)
            return ("", allSteps);

        // ── Step 3: Fast heuristic pre-filter (zero LLM calls) ────────────────
        // File hints are learned associations from past runs — highest trust
        var hintedFiles = _fileHints.GetFilesForPrompt(prompt, projectRoot)
            .Where(f => allFiles.Any(a => string.Equals(a, f, StringComparison.OrdinalIgnoreCase)))
            .Take(4)
            .ToList();

        // Score all files by task type — returns ordered list, no LLM
        var heuristicCandidates = ApplyTaskTypeHeuristics(prompt, allFiles);

        // Candidate pool = hints (trusted) + top heuristic results, deduped, max 60
        var candidatePool = hintedFiles
            .Concat(heuristicCandidates)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(60)
            .ToList();

        // ── Step 4: ONE LLM call to pick which files to read ──────────────────
        List<string> toRead;
        if (candidatePool.Count <= 6)
        {
            // Small enough — just read them all, no LLM needed
            toRead = candidatePool;
            await EmitLog(emitSse, "info",
                $"Phase 1 — {candidatePool.Count} candidate(s), reading all directly", ct: ct);
        }
        else
        {
            await EmitLog(emitSse, "info",
                $"Phase 1 — selecting from {candidatePool.Count} candidates (1 LLM call)…", ct: ct);
            var selected = await SelectRelevantFilesWithLlm(prompt, candidatePool, emitSse, ct);

            // Always include hinted files even if LLM didn't select them
            toRead = hintedFiles
                .Concat(selected)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
        }

        // Verify files actually exist on disk (guard against hallucination)
        toRead = toRead
            .Where(f =>
            {
                var full = Path.GetFullPath(
                    Path.Combine(projectRoot, f.Replace('/', Path.DirectorySeparatorChar)));
                return System.IO.File.Exists(full) && AgentUtilities.IsPathUnderRoot(full, projectRoot);
            })
            .ToList();

        await EmitLog(emitSse, "info",
            $"Phase 1 — reading {toRead.Count} file(s): {string.Join(", ", toRead)}", ct: ct);

        // ── Step 5: Read selected files in PARALLEL ────────────────────────────
        if (toRead.Count > 0)
        {
            var readPlan = toRead.Select((f, i) => new AgentStep
            {
                Index = i,
                Type = "read",
                Path = f,
                Description = $"Auto: read {f}",
                Prompt = prompt
            }).ToList();

            var readResults = await ExecuteDiscoveryStepsConcurrent(
                readPlan, projectRoot, allSteps.Count, emitSse);
            allSteps.AddRange(readResults);

            // Teach file hints from confirmed reads so future runs skip the LLM call
            foreach (var f in toRead)
                _fileHints.LearnFromGrepOutput(prompt, f, projectRoot);
        }

        // ── Build discovery text ───────────────────────────────────────────────
        var sb = new StringBuilder();
        sb.AppendLine("ONLY use paths that appear below. Do NOT invent paths.");
        sb.AppendLine();
        foreach (var item in allSteps)
        {
            if (item is not Dictionary<string, object?> r) continue;
            var type = r.TryGetValue("type", out var t) ? t?.ToString() : "";
            if (!r.TryGetValue("output", out var output) ||
                output == null || string.IsNullOrEmpty(output.ToString())) continue;

            sb.AppendLine($"### {type} {r.GetValueOrDefault("path") ?? r.GetValueOrDefault("description")}");
            sb.AppendLine(output.ToString());
            sb.AppendLine();
        }

        await EmitLog(emitSse, "info",
            $"Phase 1 complete — {allSteps.Count} steps, {toRead.Count} file(s) read", ct: ct);
        return (sb.ToString(), allSteps);
    }


    // ─── NEW METHOD ───────────────────────────────────────────────────────────────
    /// <summary>
    /// Scores project files using task-type heuristics — no LLM required.
    /// Detects intent (styling, HTML, JS, backend, config) from prompt keywords,
    /// then assigns extension + filename scores. Returns ordered candidate list.
    /// </summary>
    private static List<string> ApplyTaskTypeHeuristics(string prompt, List<string> allFiles)
    {
        var lower = prompt.ToLowerInvariant();

        // Detect what kind of task this is (multiple can be true)
        var isStyleTask = Regex.IsMatch(lower, @"\b(style|css|color|theme|layout|spacing|font|design|ui|ux|look|appear|brand|visual|margin|padding|border|shadow|panel|card)\b");
        var isHtmlTask = Regex.IsMatch(lower, @"\b(html|template|page|view|markup|modal|popup|section|div)\b");
        var isJsTask = Regex.IsMatch(lower, @"\b(javascript|script|function|event|click|toggle|show|hide|angular|react|vue|component|state|behavior)\b");
        var isBackendTask = Regex.IsMatch(lower, @"\b(api|endpoint|controller|service|database|model|route|logic|backend|server|c#|csharp|dotnet)\b");
        var isConfigTask = Regex.IsMatch(lower, @"\b(config|setting|option|appsettings|environment|json)\b");

        var meaningfulKeywords = ExtractMeaningfulKeywords(lower);

        var scored = allFiles.Select(f =>
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            var nameLow = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
            var pathLow = f.ToLowerInvariant();
            var score = 0;

            // ── Extension scoring by task type ──────────────────────────────
            if (isStyleTask)
            {
                if (ext is ".css" or ".scss" or ".sass" or ".less") score += 120;
                else if (ext is ".html" or ".htm") score += 60;
                else if (ext is ".js" or ".ts") score += 20;
            }
            if (isHtmlTask)
            {
                if (ext is ".html" or ".htm") score += 120;
                else if (ext is ".css" or ".scss") score += 50;
                else if (ext is ".js" or ".ts") score += 30;
            }
            if (isJsTask)
            {
                if (ext is ".js" or ".ts" or ".jsx" or ".tsx") score += 120;
                else if (ext is ".html" or ".htm") score += 40;
            }
            if (isBackendTask)
            {
                if (ext == ".cs") score += 120;
                else if (ext == ".json") score += 30;
            }
            if (isConfigTask)
            {
                if (ext is ".json" or ".yaml" or ".yml") score += 120;
            }

            // ── Boost if the filename contains a meaningful prompt keyword ──
            foreach (var kw in meaningfulKeywords)
                if (nameLow.Contains(kw))
                    score += 50;

            // ── Frontend folder boost for frontend tasks ───────────────────
            if ((isStyleTask || isHtmlTask || isJsTask) && pathLow.StartsWith("wwwroot/"))
                score += 25;

            // ── Penalize known-large / known-noisy files ───────────────────
            // These are almost never the target of a specific edit request
            if (nameLow.Contains("agentcontroller")) score -= 200;
            if (nameLow == "filehints") score -= 200;
            if (pathLow.EndsWith(".min.js")) score -= 300;
            if (pathLow.EndsWith(".min.css")) score -= 300;

            // ── Penalize non-text / generated artifacts ────────────────────
            if (ext is ".dll" or ".exe" or ".pdb" or ".nupkg" or ".lock" or ".sum")
                score -= 1000;

            return (file: f, score);
        })
        .Where(x => x.score > 0)
        .OrderByDescending(x => x.score)
        .Take(50)
        .Select(x => x.file)
        .ToList();

        // Fallback: if no file scored positively (e.g. novel task type), include
        // common entry-point files so the LLM always has something to work with
        if (scored.Count == 0)
        {
            scored = allFiles
                .Where(f =>
                {
                    var name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return name is "index" or "app" or "main" or "program" or "startup"
                                or "styles" or "global" or "layout"
                        && ext is ".html" or ".js" or ".ts" or ".css" or ".cs";
                })
                .Take(10)
                .ToList();
        }

        return scored;
    }


    // ─── NEW METHOD ───────────────────────────────────────────────────────────────
    /// <summary>
    /// Strips stopwords and generic action verbs from a prompt, returning
    /// the domain-meaningful terms that are actually useful for file matching.
    /// Replaces the old ExtractSearchKeywords which included words like "Make", "more",
    /// "sensitive" that produce grep noise across every file in the codebase.
    /// </summary>
    private static List<string> ExtractMeaningfulKeywords(string lower)
    {
        var stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Articles, prepositions, conjunctions
        "the","a","an","and","or","but","in","on","at","to","for","of","with","from",
        "into","onto","upon","after","before","about","above","below","between",
        // Pronouns
        "this","that","it","its","their","our","my","your","his","her","we","they","i",
        // Auxiliary verbs
        "is","are","was","were","be","been","being","have","has","had",
        "do","does","did","will","would","should","could","may","might","shall",
        // Generic action verbs (too broad — match everything)
        "make","making","makes","made",
        "fix","fixing","fixes","fixed",
        "add","adding","adds","added",
        "change","changing","changes","changed",
        "update","updating","updates","updated",
        "edit","editing","edits","edited",
        "modify","modifying","modifies","modified",
        "create","creating","creates","created",
        "delete","deleting","deletes","deleted",
        "remove","removing","removes","removed",
        "set","get","put","use","using","used",
        "show","hide","display",
        // Vague adjectives / adverbs
        "more","less","some","any","all","no","not","also","very","just",
        "nice","nicely","good","better","best","new","old","right","left",
        "please","sure","now","then","when","where","how","why","what","which","who",
        "out","up","down","so","if","else","really","quite","bit","little","lot",
        // Common filler
        "need","want","should","must","can","let","help","try","look","see"
    };

        return Regex.Matches(lower, @"\b[a-z]{3,}\b")
            .Select(m => m.Value)
            .Where(w => !stopwords.Contains(w))
            .Distinct()
            .Take(10)
            .ToList();
    }


    /// <summary>
    /// Makes a single LLM call to select the most relevant files from a
    /// heuristic-filtered candidate list. Falls back to top candidates if the
    /// call fails or returns no parseable files.
    ///
    /// This replaces N sequential ScoreSourceMaterial calls (one per grep-matched file)
    /// with a single call that sees all candidates at once.
    /// </summary>
    private async Task<List<string>> SelectRelevantFilesWithLlm(
        string prompt, List<string> candidates, bool emitSse, CancellationToken ct)
    {
        if (candidates.Count == 0) return new List<string>();

        var fileList = string.Join("\n", candidates);

        const string system =
            "You are a file relevance selector for a code editor agent. " +
            "Given a task and a list of project files, pick the 3-7 files most likely to need reading or editing. " +
            "Output ONLY valid JSON with no markdown fences or extra text: {\"files\": [\"path1\", \"path2\"]}";

        var user = $"Task: {prompt}\n\nProject files:\n{fileList}\n\nSelect 3–7 files maximum.";

        var (raw, _, err) = await CallLlmRaw(system, user, ct,
            requestTimeout: TimeSpan.FromSeconds(25));

        if (string.IsNullOrWhiteSpace(raw))
        {
            await EmitLog(emitSse, "warn",
                $"File selection LLM failed ({err ?? "empty"}) — using top heuristic candidates", ct: ct);
            return candidates.Take(6).ToList();
        }

        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (m.Success) cleaned = m.Groups[1].Value.Trim();
            }
            var start = cleaned.IndexOf('{');
            var end = cleaned.LastIndexOf('}');
            if (start >= 0 && end > start)
                cleaned = cleaned.Substring(start, end - start + 1);

            using var doc = JsonDocument.Parse(cleaned);
            if (doc.RootElement.TryGetProperty("files", out var filesEl) &&
                filesEl.ValueKind == JsonValueKind.Array)
            {
                var selected = filesEl.EnumerateArray()
                    .Select(e => e.GetString()?.Replace('\\', '/') ?? "")
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    // Only accept files actually in the candidate list to prevent hallucination
                    .Where(f => candidates.Any(c =>
                        string.Equals(c, f, StringComparison.OrdinalIgnoreCase)))
                    .Take(7)
                    .ToList();

                if (selected.Count > 0)
                    return selected;
            }
        }
        catch { /* fall through to fallback */ }

        await EmitLog(emitSse, "warn",
            "File selection response unparseable — using top heuristic candidates", ct: ct);
        return candidates.Take(6).ToList();
    }

    /// <summary>
    /// Parses the LLM planning response into an AgentPlan.
    /// Handles: markdown fences, leading/trailing prose, missing quotes on keys,
    /// unescaped inner quotes, and trailing commas.
    /// </summary>
    public AgentPlan? ParsePlan(string jsonString)
    {
        if (string.IsNullOrWhiteSpace(jsonString)) return null;

        // ── Step 1: strip markdown fences ─────────────────────────────────────
        var cleaned = jsonString.Trim();
        if (cleaned.StartsWith("```"))
        {
            var fenceMatch = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            cleaned = fenceMatch.Success ? fenceMatch.Groups[1].Value.Trim() : cleaned.TrimStart('`');
        }

        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        // Step 2: locate complete JSON objects that contain a plan. Try the
        // outermost blocks first so we do not accidentally parse an inner step.
        var jsonBlocks = AgentUtilities.ExtractJsonBlocks(cleaned)
            .Where(LooksLikePlanJson)
            .OrderByDescending(b => b.Length)
            .ToList();

        if (LooksLikePlanJson(cleaned) && cleaned.StartsWith("{"))
            jsonBlocks.Insert(0, cleaned);

        var firstBrace = cleaned.IndexOf('{');
        var lastBrace = cleaned.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            var broadCandidate = cleaned.Substring(firstBrace, lastBrace - firstBrace + 1);
            if (LooksLikePlanJson(broadCandidate))
                jsonBlocks.Add(broadCandidate);
        }

        foreach (var candidate in jsonBlocks.Distinct())
        {
            foreach (var repaired in AgentUtilities.GeneratePlanJsonCandidates(candidate))
            {
                try
                {
                    var result = JsonSerializer.Deserialize<AgentPlan>(repaired, opts);
                    if (result?.Plan != null) return result;
                }
                catch { }
            }
        }

        // Fallback: some small models return the plan array directly.
        var arrayCandidates = new List<string> { cleaned };
        var firstBracket = cleaned.IndexOf('[');
        var lastBracket = cleaned.LastIndexOf(']');
        if (firstBracket >= 0 && lastBracket > firstBracket)
            arrayCandidates.Add(cleaned.Substring(firstBracket, lastBracket - firstBracket + 1));

        foreach (var block in arrayCandidates.Distinct())
        {
            try
            {
                var candidate = block.Trim();
                if (!candidate.StartsWith("[")) continue;
                var steps = JsonSerializer.Deserialize<List<PlanStep>>(candidate, opts);
                if (steps is { Count: > 0 })
                    return new AgentPlan { Summary = "Parsed direct plan array", Plan = steps };
            }
            catch { }
        }

        Console.Error.WriteLine($"[ParsePlan] All repair strategies failed. Raw snippet: {cleaned[..Math.Min(200, cleaned.Length)]}");
        return null;
    }

    private static bool LooksLikePlanJson(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        Regex.IsMatch(text, @"""?plan""?\s*:", RegexOptions.IgnoreCase);


    /// <summary>
    /// Create a new file with LLM-generated content.
    /// The changeDesc describes what to create; the LLM generates full file content.
    /// If explicitRelPath is provided, it overrides filename extraction from changeDesc.
    /// </summary>
    private async Task<(List<object> results, int stepsCount)> HandleCreateFile(
        string changeDesc, string projectRoot, string originalPrompt, string discoveryContext,
        int idx, bool emitSse, CancellationToken ct,
        string? explicitRelPath = null)
    {
        var results = new List<object>();

        // Extract the target filename from changeDesc or explicitRelPath
        var targetRelPath = explicitRelPath;
        if (string.IsNullOrWhiteSpace(targetRelPath))
        {
            // Strategy 1: "new file .editorconfig" or "file called filename.ext"
            var namedMatch = Regex.Match(changeDesc,
                @"(?:new\s+)?file\s+(?:called|named|`` `)?\s*['""`]?([\w./\\-]+\.[\w.-]+)['""`]?", RegexOptions.IgnoreCase);
            if (namedMatch.Success)
                targetRelPath = namedMatch.Groups[1].Value.Replace('\\', '/');
        }

        if (string.IsNullOrWhiteSpace(targetRelPath))
        {
            // Strategy 2: standard path like "path/to/file.ext" (check BEFORE bare dotfiles)
            var pathMatch = Regex.Match(changeDesc, @"[\w/\\]+\.[\w]+");
            if (pathMatch.Success)
                targetRelPath = pathMatch.Value.Replace('\\', '/');
        }

        if (string.IsNullOrWhiteSpace(targetRelPath))
        {
            // Strategy 3: dotfiles like .editorconfig, .gitignore, .env
            var dotMatch = Regex.Match(changeDesc, @"\.[\w-]+(?:\.[\w-]+)*");
            if (dotMatch.Success)
                targetRelPath = dotMatch.Value;
        }

        if (string.IsNullOrWhiteSpace(targetRelPath))
        {
            // Strategy 4: "file.ext" or "name.min.ext"
            var fileMatch = Regex.Match(changeDesc, @"(\.?[\w-]+(?:\.[\w]+)+)");
            if (fileMatch.Success)
                targetRelPath = fileMatch.Groups[1].Value;
        }

        // Still empty — use a safe fallback name
        if (string.IsNullOrWhiteSpace(targetRelPath))
            targetRelPath = "newfile.txt";

        // Infer folder placement when no directory is specified
        targetRelPath = targetRelPath.Replace('\\', '/');
        if (!targetRelPath.Contains('/'))
        {
            var folder = AgentUtilities.InferTargetFolder(targetRelPath, projectRoot);
            if (!string.IsNullOrWhiteSpace(folder))
                targetRelPath = folder + targetRelPath;
        }

        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, targetRelPath.Replace('/', Path.DirectorySeparatorChar)));

        if (!AgentUtilities.IsPathUnderRoot(fullPath, projectRoot))
        {
            await EmitLog(emitSse, "error", $"Create target {targetRelPath} is outside project root", ct: ct);
            return (results, 0);
        }

        await EmitLog(emitSse, "info", $"Generating content for new file: {targetRelPath}", ct: ct);

        // Ask LLM to generate the full file content
        var contentPrompt = $@"You are a file creation assistant. Generate the COMPLETE content for a new file based on the description below.

Target file: {targetRelPath}
Task: {originalPrompt}
Description: {changeDesc}

Project context:
{discoveryContext}

Respond with ONLY the raw file content — no markdown, no code fences, no explanation. The content will be written directly to the file.";

        var (content, _, err) = await CallLlmRaw(
            "You are a file creation assistant. Output ONLY the raw file content — no markdown, no code fences, no explanation.",
            contentPrompt, ct, requestTimeout: _infiniteTimeout);

        if (string.IsNullOrWhiteSpace(content) && err != null)
        {
            await EmitLog(emitSse, "error", $"Failed to generate content for {targetRelPath}: {err}", ct: ct);
            return (results, 0);
        }

        // Clean up LLM output — remove markdown code fences if present
        var cleaned = content?.Trim() ?? "";
        if (cleaned.StartsWith("```"))
        {
            var firstNewline = cleaned.IndexOf('\n');
            if (firstNewline > 0) cleaned = cleaned[(firstNewline + 1)..];
            if (cleaned.EndsWith("```")) cleaned = cleaned[..^3].TrimEnd();
        }

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            await EmitLog(emitSse, "warn", $"Generated empty content for {targetRelPath}", ct: ct);
            return (results, 0);
        }

        // Ensure parent directory exists
        var parentDir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
            Directory.CreateDirectory(parentDir);

        // Write the file
        await System.IO.File.WriteAllTextAsync(fullPath, cleaned, Encoding.UTF8);
        await EmitLog(emitSse, "success", $"Created {targetRelPath} ({cleaned.Length} chars)", ct: ct);

        if (emitSse)
            await SendSse(Response, "result", new
            {
                type = "create",
                path = targetRelPath,
                chars = cleaned.Length
            }, ct);

        // Record as a step result (mimics edit step format)
        var result = new Dictionary<string, object?>
        {
            ["status"] = "done",
            ["path"] = targetRelPath,
            ["output"] = cleaned,
            ["type"] = "create"
        };
        results.Add(result);

        return (results, 1);
    }

    /// <summary>
    /// Orchestration Router — classifies the task, routes to the appropriate
    /// pipeline, then feeds results through the Verification Pipeline.
    /// </summary>
    private async Task<(List<object> allSteps, AgentPlan? plan, bool complete)> Orchestrate(
     string prompt, string projectRoot, bool emitSse, CancellationToken ct = default,
     List<string>? attachedFiles = null, bool skipContextReview = false,
     string? steeringContext = null)
    { 
        if (!await CheckLlmConnectivity(projectRoot, emitSse, ct))
            throw new InvalidOperationException("LLM connectivity check failed.");
 
        var fastPlan = AgentUtilities.TryDetectSimpleIntent(prompt);
        if (fastPlan != null)
        {
            var steps = await QuickPipeline(prompt, projectRoot, emitSse, fastPlan, ct);
            return (steps, fastPlan, true);
        }
 
        var (pipelineType, cmdScore, editScore) = AgentUtilities.ClassifyTask(prompt);
        await EmitLog(emitSse, "info", $"Router → {pipelineType}", new { CommandScore = cmdScore, EditScore = editScore }, ct: ct);

        var (allSteps, plan) = pipelineType switch
        {
            PipelineType.CommandExecution => await CommandExecutionPipeline(prompt, projectRoot, emitSse, ct, steeringContext: steeringContext),
            _ => await UnifiedPipeline(prompt, projectRoot, emitSse, ct, attachedFiles: attachedFiles, skipContextReview: skipContextReview, steeringContext: steeringContext),
        };

        bool complete = true;
        bool requiresReplan = false;
        if (allSteps.Count > 0)
        { 
            await EmitLog(emitSse, "info", $"Initial pipeline complete with {allSteps.Count} steps. Starting verification.", allSteps, ct: ct);
            var editSteps =
                allSteps
                    .OfType<Dictionary<string, object?>>()
                    .Where(step => step.TryGetValue("type", out var t) && t?.ToString() == "edit")
                    .ToList();

            // If the LLM self-terminated with _done, the task is definitionally complete.
            var hasDoneSignal = allSteps
                .OfType<Dictionary<string, object?>>()
                .Any(step => step.TryGetValue("type", out var t) && t?.ToString() == "done_signal");

            if (!hasDoneSignal)
            {
                for (int i = 0; i < editSteps.Count; i++)
                {
                    var step = editSteps[i];
                    var hasStatus = step.TryGetValue("status", out var statusValue);
                    var status = statusValue?.ToString();

                    // "skipped" (oldString == newString) is a successful terminal state,
                    // not a failure. Only "error" or missing status warrants a replan.
                    var isTerminal = status is "done" or "skipped";
                    if (!hasStatus || !isTerminal)
                    {
                        requiresReplan = true;
                        await EmitLog(emitSse, "warn",
                            $"Edit step {i} has non-terminal status: {status}", step, ct: ct);
                        break;
                    }
                }
            }
 
            if (requiresReplan)
            {
                await EmitLog(emitSse, "info",
                    "Verifying task completion before replan…", ct: ct);

                var (alreadyDone, doneReason) =
                    await AssessCompletion(prompt, allSteps, projectRoot, ct);

                if (alreadyDone)
                {
                    await EmitLog(emitSse, "success",
                        $"Task verified complete — replan suppressed: {doneReason}", ct: ct);
                    requiresReplan = false;
                    complete = true;
                }
                else
                {
                    await EmitLog(emitSse, "warn",
                        $"Completion assessment: incomplete — {doneReason}", ct: ct);
                }
            }
            if (requiresReplan)
            {
                await EmitLog(emitSse, "warn", "One or more edit steps are not marked as done. This may indicate a failure in executing the plan. Consider triggering a replan or debug session.", allSteps, ct: ct);
            } 
        }
        
        if (requiresReplan)
        { 
            var history = new List<string>
            {
                BuildFailedEditHistory(allSteps)
            };

            for (var attempt = 0; attempt < 3; attempt++)
            {
                 
                await EmitLog(emitSse, "info", $"Replan attempt {attempt + 1}/3 — building enriched prompt…", ct: ct);
                var retryPrompt = BuildReplanPrompt(prompt, history, steeringContext);
                (allSteps, plan, complete) = await Orchestrate(retryPrompt, projectRoot, emitSse, ct, attachedFiles: attachedFiles, skipContextReview: true, steeringContext: null);
                
                if (!complete)
                { 
                    history.Add(plan?.Summary ?? $"Attempt {attempt + 1}");
                    await EmitLog(emitSse, "warn", $"Attempt {attempt + 1}/3 incomplete.", ct: ct);
                } else
                {
                    await EmitLog(emitSse, "success", $"Attempt {attempt + 1}/3 successful! Verification passed.", ct: ct);
                    break;
                }
            }

            if (!complete)
                await EmitLog(emitSse, "error", $"All 3 attempts exhausted.", ct: ct);
        }
         
        if (allSteps.Count > 0 || complete)
        {
            var cfg = await _configFile.LoadConfigAsync();
            var cmds = ParseBuildCommands(cfg.buildCommands);
            if (cmds.Count > 0)
            {
                await EmitLog(emitSse, "info", "Running build check…", ct: ct);
                if (emitSse)
                    await SendSse(Response, "phase", new { phase = "build", message = $"Running {cmds.Count} build command(s)" }, ct);
                foreach (var cmd in cmds)
                    await RunSmartBuildCheck(projectRoot, cmd, emitSse, ct);
            }
        }

        return (allSteps, plan, complete);  
    }

    private static string BuildFailedEditHistory(List<object> allSteps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Concrete verification failures from the previous execution:");
        var failures = allSteps
            .OfType<Dictionary<string, object?>>()
            .Where(step =>
                step.TryGetValue("type", out var t) && t?.ToString() == "edit" &&
                (!step.TryGetValue("status", out var st) || st?.ToString() != "done"))
            .ToList();

        if (failures.Count == 0)
        {
            sb.AppendLine("- No failed edit dictionaries were available.");
            return sb.ToString();
        }

        foreach (var failure in failures.Take(8))
        {
            sb.AppendLine($"- Path: {failure.GetValueOrDefault("path")}");
            sb.AppendLine($"  Status: {failure.GetValueOrDefault("status")}");
            if (failure.TryGetValue("error", out var error) && error != null)
                sb.AppendLine($"  Error: {error}");
            if (failure.TryGetValue("reason", out var reason) && reason != null)
                sb.AppendLine($"  Reason: {reason}");
            if (failure.TryGetValue("snippet", out var snippet) && snippet != null)
                sb.AppendLine($"  Nearby snippet: {snippet}");
            if (failure.TryGetValue("oldString", out var oldString) && oldString != null)
                sb.AppendLine($"  Failed oldString preview: {PreviewForPrompt(oldString.ToString() ?? "", 1200)}");
            else if (failure.TryGetValue("oldStringPreview", out var oldPreview) && oldPreview != null)
                sb.AppendLine($"  Failed oldString preview: {PreviewForPrompt(oldPreview.ToString() ?? "", 1200)}");
        }

        return sb.ToString();
    }

    private static string PreviewForPrompt(string value, int maxChars)
    {
        if (value.Length <= maxChars) return value;
        return value[..maxChars] + "\n[Preview ended; omitted remainder is not code.]";
    }

    /// <summary>
    /// Parses buildCommands from config, which can be a JSON array of strings
    /// (["cmd1", "cmd2"]) or a single command string ("dotnet build").
    /// Returns the list of commands, or empty if nothing is configured.
    /// </summary>
    private static List<string> ParseBuildCommands(string buildCommands)
    {
        if (string.IsNullOrWhiteSpace(buildCommands))
            return new List<string>();

        // Try JSON array parse: ["cmd1", "cmd2"]
        try
        {
            var arr = JsonSerializer.Deserialize<List<string>>(buildCommands);
            if (arr != null && arr.Count > 0)
                return arr.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        }
        catch { }

        // Fall back to single command string
        return new List<string> { buildCommands.Trim() };
    }

    /// <summary>
    /// Build a retry prompt that includes all previous plan attempts and their
    /// verification feedback so the LLM can learn from past failures.
    /// </summary>
    private static string BuildReplanPrompt(string originalPrompt, List<string> history, string? steeringContext = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("The previous plan did not pass verification. Review the feedback and produce a better plan.");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(steeringContext))
        {
            sb.AppendLine("## USER STEERING INSTRUCTION");
            sb.AppendLine(steeringContext);
            sb.AppendLine();
        }
        sb.AppendLine("## Original task");
        sb.AppendLine(originalPrompt);
        sb.AppendLine();
        sb.AppendLine("## Previous attempts");
        for (var i = 0; i < history.Count; i++)
        {
            var h = history[i];
            sb.AppendLine($"### Attempt {i + 1}: {h}"); 
            sb.AppendLine();
        }
        sb.AppendLine("Fix the issues described in the task above. Try a different approach if needed.");
        return sb.ToString();
    }
    private async Task<List<object>> QuickPipeline(string prompt, string projectRoot, bool emitSse, AgentPlan fastPlan, CancellationToken ct)
    {
        await EmitLog(emitSse, "info", $"Fast-path → {fastPlan.Summary}", ct: ct);
        if (emitSse)
            await SendSse(Response, "plan", new { thinking = fastPlan.Thinking, summary = fastPlan.Summary, items = fastPlan.Plan }, ct);
        
        var allResults = new List<object>();
        await ExecutePlan(prompt, projectRoot, emitSse, "", fastPlan, ct, allResults); 
        return allResults;
    }  

    private async Task<(List<object> steps, AgentPlan? plan)> CommandExecutionPipeline(
        string prompt, string projectRoot, bool emitSse, CancellationToken ct,
        string? steeringContext = null)
    {
        var steps = new List<object>();

        // Fast path for known simple intents (git, ping, package_install)
        var fastPlan = AgentUtilities.TryDetectSimpleIntent(prompt);
        if (fastPlan != null)
        {
            await EmitLog(emitSse, "info",
                $"CommandExecution (fast): {fastPlan.Plan.Count} step(s)", ct: ct);
            if (emitSse)
                await SendSse(Response, "plan",
                    new { thinking = fastPlan.Thinking, summary = fastPlan.Summary, items = fastPlan.Plan }, ct);
            await ExecutePlan(prompt, projectRoot, emitSse, "", fastPlan, ct, steps);
            return (steps, fastPlan);
        }

        await EmitLog(emitSse, "info", "CommandExecution (agentic): LLM has terminal control", ct: ct);
        _terminal.Start();

        var isWindows = OperatingSystem.IsWindows();
        var shellName = isWindows ? "PowerShell" : "Bash";
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var fileCmd = isWindows ? "Set-Content" : "tee";
        var redirectOp = isWindows ? "| Set-Content" : ">";

        var conversation = new StringBuilder();
        conversation.AppendLine("You are a terminal automation agent. You have full terminal access.");
        conversation.AppendLine($"You are running on {shellName} ({Environment.OSVersion}).");
        conversation.AppendLine("Run commands to accomplish the user's task.");
        conversation.AppendLine();
        conversation.AppendLine("CRITICAL RULES:");
        conversation.AppendLine("  - Output ONLY valid JSON, no other text, no markdown fences");
        conversation.AppendLine("  - To run a command: {\"cmd\": \"the full command\"}");
        conversation.AppendLine("  - To search the web: {\"web_search\": \"query\"}");
        conversation.AppendLine("  - To fetch a URL: {\"web_fetch\": \"url\"}");
        conversation.AppendLine("  - EMAIL ACTION (not a terminal command): {\"email\": \"inbox\"} — calls the built-in email reader. Checks ALL configured accounts. Do NOT try to run \"email\" as a terminal command.");
        conversation.AppendLine("  - EMAIL + FILE: {\"email\": \"inbox\", \"file\": \"C:\\\\path\\\\to\\\\file.txt\"} — fetches emails AND writes them directly to the file. The system handles the writing; you don't need a separate cmd.");
        conversation.AppendLine("  - To read a specific account: {\"email\": \"inbox\", \"account\": \"0\"} (use index or label)");
        conversation.AppendLine("  - To validate email: {\"email\": \"validate\"}");
        conversation.AppendLine("  - To show a result to the user: {\"message\": \"your answer here\"}");
        conversation.AppendLine("  - When done: {\"done\": true, \"summary\": \"what was accomplished\"}");
        conversation.AppendLine($"  - Desktop path: {desktopPath}");
        conversation.AppendLine($"  - WRITE FILE: {fileCmd} -Path \"<path>\" -Value \"<content>\"");
        conversation.AppendLine($"  - MULTI-LINE VALUES: use PowerShell here-string with actual line breaks inside the JSON string: {fileCmd} -Path \"path\" -Value @\"\\nline1\\nline2\\n\"@");
        conversation.AppendLine("  - CREATE FILE: New-Item -ItemType File -Path \"<path>\" -Force  (do NOT use mkdir)");
        conversation.AppendLine("  - NEVER use mkdir, curl, wget, jq, python, Set-Location, cd, or bash syntax — they do NOT work here");
        conversation.AppendLine("  - If a command shows ⚠ Error:, read it and try a DIFFERENT command");
        conversation.AppendLine("  - web_search uses DuckDuckGo — if it returns empty, try web_fetch with a direct API URL");
        conversation.AppendLine("  - PLAN before you act. Decide which commands to run and in what order before outputting JSON.");
        conversation.AppendLine("  - HUMAN-READABLE FIRST: When fetching data from an API or scraping, FIRST create a compact HTML summary with a table/list of all results, THEN save individual files if needed. The HTML file is the priority — it should be created early so the user can see data even if later steps fail.");
        conversation.AppendLine("  - AVOID LARGE LOOPS: Never loop over more than 30 items in a single command. If you need to process more, batch them: do 30, report progress, then continue. Large loops will timeout.");
        conversation.AppendLine("  - Write all files directly to the target path. Never create folders or extra files unless the user explicitly asks for them.");
        conversation.AppendLine("  - Complete the task as fast as possible — stop as soon as the user's request is fulfilled.");
        conversation.AppendLine();
        if (!string.IsNullOrWhiteSpace(steeringContext))
        {
            conversation.AppendLine("### USER STEERING INSTRUCTION ###");
            conversation.AppendLine(steeringContext);
            conversation.AppendLine();
        }
        conversation.AppendLine($"Task: {prompt}");
        conversation.AppendLine();

        const int maxIterations = MAX_COMMAND_ITERATIONS;
        var stepIndex = 0;
        string? summary = null;
        var webScrapeFailureCount = 0;
        var usedSearchQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateBlockCount = 0;

        for (var i = 0; i < maxIterations; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Compact conversation if over budget before sending to LLM
            AgentUtilities.CompactConversation(conversation);

            var systemMsg = "You are a terminal agent. Output only JSON.";
            var (raw, _, err) = await CallLlmRaw(systemMsg, conversation.ToString(), ct,
                requestTimeout: TimeSpan.FromSeconds(30));

            if (string.IsNullOrWhiteSpace(raw))
            {
                await EmitLog(emitSse, "warn", $"Agentic command: LLM returned empty — {err}", ct: ct);
                summary ??= "Command execution completed with issues";
                break;
            }

            // Strip markdown fences if present
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (m.Success) cleaned = m.Groups[1].Value.Trim();
            }

            // Try to parse — is it done or a command?
            var jsonOptions = new JsonDocumentOptions { AllowTrailingCommas = true };
            string? jsonToParse = null;

            // Build parse candidates: raw text, extracted JSON blocks, repaired versions
            var candidates = new List<string> { cleaned };
            foreach (var block in AgentUtilities.ExtractJsonBlocks(cleaned))
                if (!candidates.Contains(block)) candidates.Add(block);
            foreach (var c in candidates.ToList())
            {
                var repaired = AgentUtilities.RepairJsonString(c);
                if (repaired != null && !candidates.Contains(repaired)) candidates.Add(repaired);
            }

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try { JsonDocument.Parse(candidate, jsonOptions); jsonToParse = candidate; break; }
                catch (JsonException) { }
            }

            if (jsonToParse != null)
            {
                using var doc = JsonDocument.Parse(jsonToParse, jsonOptions);
                var root = doc.RootElement;

                // Check for "done" signal
                if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
                {
                    summary = root.TryGetProperty("summary", out var s) ? s.GetString() : "Task complete";
                    await EmitLog(emitSse, "success", $"Agentic command: {summary}", ct: ct);
                    break;
                }

                // Extract command
                if (root.TryGetProperty("cmd", out var cmdEl) || root.TryGetProperty("command", out cmdEl))
                {
                    var cmd = cmdEl.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(cmd))
                    {
                        conversation.AppendLine("Empty command — try again.");
                        continue;
                    }

                    // Sanitize: replace embedded newlines with PowerShell separators.
                    // JSON \n in the command becomes actual newlines in the C# string,
                    // which breaks WriteLineAsync to the terminal pipe.
                    // For file-write commands, convert to here-string to preserve content.
                    // For other commands, join with "; ".
                    if ((cmd.Contains('\n') || cmd.Contains('\r')) && !cmd.Contains("@\"") && !cmd.Contains("@'"))
                    {
                        var trimmedLower = cmd.TrimStart().ToLowerInvariant();
                        if (trimmedLower.StartsWith("set-content ") || trimmedLower.StartsWith("add-content ") ||
                            trimmedLower.StartsWith("out-file "))
                        {
                            // Convert to here-string: replace -Value "content" with -Value @"...@"
                            // so actual newlines in the content are preserved.
                            var valueMatch = Regex.Match(cmd, @"-Value\s+""(.*)""\s*$", RegexOptions.Singleline);
                            if (valueMatch.Success)
                            {
                                var content = valueMatch.Groups[1].Value;
                                var beforeValue = cmd[..valueMatch.Index];
                                cmd = beforeValue + "-Value @\"\n" + content + "\n\"@";
                                await EmitLog(emitSse, "info", $"⚠ cmd had newlines — converted to here-string", ct: ct);
                            }
                            else
                            {
                                var sanitized = cmd.Replace("\r\n", "; ").Replace("\r", "; ").Replace("\n", "; ");
                                await EmitLog(emitSse, "info", $"⚠ cmd contained newlines — joined: {sanitized}", ct: ct);
                                cmd = sanitized;
                            }
                        }
                        else
                        {
                            var sanitized = cmd.Replace("\r\n", "; ").Replace("\r", "; ").Replace("\n", "; ");
                            await EmitLog(emitSse, "info", $"⚠ cmd contained newlines — joined: {sanitized}", ct: ct);
                            cmd = sanitized;
                        }
                    }

                    // Validate: reject mkdir for file-like paths (creates directories not files)
                    var cmdLower = cmd.TrimStart().ToLowerInvariant();
                    if (cmdLower.StartsWith("mkdir") && Regex.IsMatch(cmd, @"\.\w{2,4}[""'\s]|\.\w{2,4}$"))
                    {
                        conversation.AppendLine($"❌ REJECTED: '{cmd}' — mkdir creates DIRECTORIES, not files. Use: New-Item -ItemType File -Path \"<path>\" -Force");
                        conversation.AppendLine();
                        await EmitLog(emitSse, "warn", $"Rejected mkdir for file-like path: {cmd}", ct: ct);
                        continue;
                    }

                    // Reject cd / Set-Location — terminal stays at project root
                    if (cmdLower == "cd" || cmdLower.StartsWith("cd ") || cmdLower.Contains("set-location") || cmdLower.StartsWith("sl ") || cmdLower == "sl")
                    {
                        conversation.AppendLine($"❌ REJECTED: '{cmd}' — cd/Set-Location is not supported. The terminal stays in the project root. Use absolute paths in file commands.");
                        conversation.AppendLine();
                        await EmitLog(emitSse, "warn", $"Rejected cd command: {cmd}", ct: ct);
                        continue;
                    }

                    await EmitLog(emitSse, "step", $"▶ cmd[{i + 1}]: {cmd}", new { conversation }, ct: ct);
                    var beforeLen = _terminal.ReadAll().Length;
                    await _terminal.SendCommandAsync(cmd, projectRoot);

                    // Adaptive wait: poll terminal output every 500ms.
                    // Stops when output is stable for 3s (network commands take time to produce output).
                    // Max 40 iterations = 20s total wait.
                    var prevLen = beforeLen;
                    var stableMs = 0;
                    for (var w = 0; w < 40; w++)
                    {
                        await Task.Delay(500);
                        var curLen = _terminal.ReadAll().Length;
                        if (curLen == prevLen) { stableMs += 500; if (stableMs >= 3000) break; }
                        else { stableMs = 0; prevLen = curLen; }
                    }

                    var fullOutput = _terminal.ReadAll();
                    var freshOutput = beforeLen < fullOutput.Length
                        ? fullOutput[beforeLen..] : "";
                    beforeLen = fullOutput.Length;
                    
                    // Detect errors in output
                    var isError = false;
                    if (!string.IsNullOrWhiteSpace(freshOutput))
                    {
                        var lowerOut = freshOutput.ToLowerInvariant();
                        isError = lowerOut.Contains("not recognized") || lowerOut.Contains("is not recognized") ||
                                  lowerOut.Contains("not found") || lowerOut.Contains("cannot find") ||
                                  lowerOut.Contains("terminate") || lowerOut.Contains("terminated") ||
                                  lowerOut.Contains("error") || lowerOut.Contains("exception") ||
                                  lowerOut.Contains("failed") || lowerOut.Contains("failure") ||
                                  lowerOut.Contains("access denied") || lowerOut.Contains("permission denied");
                    }

                    var result = new Dictionary<string, object?>
                    {
                        ["index"] = stepIndex++,
                        ["type"] = "command",
                        ["command"] = cmd,
                        ["status"] = isError ? "error" : "done",
                        ["output"] = freshOutput
                    };
                    steps.Add(result);

                    await EmitLog(emitSse, "step", $"🟦 cmd[{i + 1}]: {cmd}", new { result, cmd, fullOutput }, ct: ct);
                    if (emitSse)
                        await SendSse(Response, "step", result, ct);

                    var outputLabel = isError ? "⚠ Error:" : "Output:";
                    if (!string.IsNullOrWhiteSpace(freshOutput))
                    {
                        var lowerOut = freshOutput.ToLowerInvariant();
                        if (lowerOut.Contains("not recognized") || lowerOut.Contains("is not recognized") ||
                            lowerOut.Contains("not found") || lowerOut.Contains("cannot find") ||
                            lowerOut.Contains("terminate") || lowerOut.Contains("terminated") ||
                            lowerOut.Contains("error") || lowerOut.Contains("exception") ||
                            lowerOut.Contains("failed") || lowerOut.Contains("failure") ||
                            lowerOut.Contains("access denied") || lowerOut.Contains("permission denied"))
                        {
                            outputLabel = "⚠ Error:";
                        }
                    }

                    conversation.AppendLine($"Command [{i + 1}]: {cmd}");
                    conversation.AppendLine(outputLabel);
                    conversation.AppendLine(freshOutput);
                    conversation.AppendLine();

                    // Track repeated web scraping failures and intervene
                    if (isError && (cmdLower.Contains("invoke-webrequest") || cmdLower.Contains("curl ") ||
                        cmdLower.Contains("wget ") || cmdLower.Contains("select-string") ||
                        cmdLower.Contains("select -string") || cmdLower.Contains("regex")))
                    {
                        webScrapeFailureCount++;
                        if (webScrapeFailureCount >= 3)
                        {
                            var msg = "⚠ INTERVENTION: Web scraping has failed " + webScrapeFailureCount + " times. STOP trying to scrape websites. If web_search results exist, extract the data from them and write directly to the target file. Do NOT retry scraping.";
                            conversation.AppendLine(msg);
                            conversation.AppendLine();
                            await EmitLog(emitSse, "warn", msg, ct: ct);
                        }
                    }

                    continue;
                }

                // Web search
                if (root.TryGetProperty("web_search", out var searchEl))
                {
                    var query = searchEl.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(query))
                    { conversation.AppendLine("Empty web_search query — try again."); continue; }

                    // If the exact same query was already searched, skip and force LLM to use existing results
                    if (!usedSearchQueries.Add(query))
                    {
                        duplicateBlockCount++;
                        if (duplicateBlockCount >= 2)
                        {
                            summary = $"I searched for information but couldn't find specific results. Try refining your search or checking retailer websites directly.";
                            await EmitLog(emitSse, "warn", $"Duplicate web_search blocked twice — forcing done: {summary}", ct: ct);
                            break;
                        }
                        var dupMsg = $"⚠ You already searched for \"{query}\" — the results are above. Read them and answer the user using {{\"message\": \"...\"}}. Do NOT repeat the same web_search.";
                        conversation.AppendLine(dupMsg);
                        conversation.AppendLine();
                        await EmitLog(emitSse, "warn", $"Duplicate web_search[{i + 1}]: '{query}' — skipping", ct: ct);
                        continue;
                    }

                    // After 3+ different searches with no answer, suggest trying web_fetch on specific URLs
                    if (usedSearchQueries.Count >= 3)
                    {
                        conversation.AppendLine("ℹ HINT: If searches aren't giving specific results, try web_fetch with a retailer URL to look up inventory directly.");
                        conversation.AppendLine();
                    }

                    await EmitLog(emitSse, "step", $"▶ web_search[{i + 1}]: {query}", ct: ct);
                    var (searchOutput, searchErr) = await WebSearchAsync(query, ct);
                    var webResult = new Dictionary<string, object?>
                    {
                        ["index"] = stepIndex++,
                        ["type"] = "web_search",
                        ["query"] = query,
                        ["status"] = string.IsNullOrWhiteSpace(searchErr) ? "done" : "error",
                        ["output"] = searchOutput
                    };
                    steps.Add(webResult);
                    if (emitSse) await SendSse(Response, "step", webResult, ct);

                    conversation.AppendLine($"Web search [{i + 1}]: {query}");
                    conversation.AppendLine("Results:");
                    conversation.AppendLine(searchOutput);
                    conversation.AppendLine();

                    // Auto-extract phone numbers from search results
                    var phones = AgentUtilities.ExtractPhoneNumbers(searchOutput);
                    if (phones.Count > 0)
                    {
                        conversation.AppendLine("ℹ PHONE NUMBERS FOUND in search results — write these to the target file:");
                        foreach (var p in phones)
                            conversation.AppendLine("  " + p);
                        conversation.AppendLine();
                    }

                    continue;
                }

                // Email inbox
                if (root.TryGetProperty("email", out var emailEl))
                {
                    var action = emailEl.GetString() ?? "inbox";
                    // Optional account specifier: label or index (LLM may send string or number)
                    string? accountSpec = null;
                    if (root.TryGetProperty("account", out var acctEl))
                    {
                        if (acctEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                            accountSpec = acctEl.GetInt32().ToString();
                        else
                            accountSpec = acctEl.GetString();
                    }
                    // Optional file path: if set, write email data directly to this file
                    string? emailFilePath = null;
                    if (root.TryGetProperty("file", out var fileEl))
                        emailFilePath = fileEl.GetString();

                    await EmitLog(emitSse, "step", $"▶ email[{i + 1}]: {action}" +
                        (accountSpec != null ? $" (account: {accountSpec})" : " (all accounts)") +
                        (emailFilePath != null ? $" → {emailFilePath}" : ""), new { emailEl, accountSpec, emailFilePath }, ct: ct);

                    // Try to auto-configure before proceeding
                    var cfgCheck = await _emailService.CheckAndAutoConfigureAsync();
                    if (cfgCheck.AccountCount == 0 && action != "validate" && action != "test")
                    {
                        // Ask the user for missing credentials (first account)
                        var questionFields = new List<QuestionField>();
                        questionFields.Add(new QuestionField
                        {
                            Key = "emailLabel",
                            Label = "Label (e.g., Gmail, Work)",
                            Type = "text",
                            DefaultValue = ""
                        });
                        questionFields.Add(new QuestionField
                        {
                            Key = "emailUsername",
                            Label = "Email username",
                            Type = "text",
                            DefaultValue = ""
                        });
                        questionFields.Add(new QuestionField
                        {
                            Key = "emailImapServer",
                            Label = "IMAP server",
                            Type = "text",
                            DefaultValue = "imap.gmail.com"
                        });
                        questionFields.Add(new QuestionField
                        {
                            Key = "emailImapPort",
                            Label = "IMAP port",
                            Type = "text",
                            DefaultValue = "993"
                        });
                        questionFields.Add(new QuestionField
                        {
                            Key = "emailPassword",
                            Label = "Email password",
                            Type = "password",
                            DefaultValue = ""
                        });

                        var pendingId = Guid.NewGuid().ToString("N");
                        var pending = new PendingQuestion
                        {
                            Id = pendingId,
                            Question = "Email is not configured. Please enter IMAP credentials:",
                            Fields = questionFields,
                            CreatedUtc = DateTime.UtcNow
                        };
                        _pendingQuestions[pendingId] = pending;

                        await SendSse(Response, "question", new
                        {
                            id = pending.Id,
                            question = pending.Question,
                            fields = pending.Fields
                        }, ct);

                        await EmitLog(emitSse, "info", $"⏳ Waiting for email credentials from user...", ct: ct);

                        string? qError = null;
                        try
                        {
                            var timeout = TimeSpan.FromMinutes(5);
                            var answer = await pending.Answer.Task.WaitAsync(timeout, ct);

                            var hasCreds = answer.Any(a => !string.IsNullOrWhiteSpace(a.Value));
                            if (hasCreds)
                            {
                                var cfg = await _configFile.LoadConfigAsync();
                                var acct = new EmailAccountConfig
                                {
                                    label = answer.GetValueOrDefault("emailLabel") ?? "",
                                    imapServer = answer.GetValueOrDefault("emailImapServer") ?? "imap.gmail.com",
                                    username = answer.GetValueOrDefault("emailUsername") ?? "",
                                    password = answer.GetValueOrDefault("emailPassword") ?? ""
                                };
                                if (int.TryParse(answer.GetValueOrDefault("emailImapPort"), out var port))
                                    acct.imapPort = port;
                                cfg.emailAccounts.Add(acct);
                                await _configFile.WriteConfigAsync(cfg);

                                await EmitLog(emitSse, "info", "✓ Email credentials saved — retrying...", ct: ct);
                                conversation.AppendLine("Email credentials have been saved. The user entered their credentials. Retry the email command now — it should work.");
                            }
                            else
                            {
                                conversation.AppendLine("Email credential input was cancelled. Inform the user that email requires valid credentials, then call {\"done\": true}.");
                            }
                            conversation.AppendLine();
                            continue;
                        }
                        catch (TimeoutException)
                        {
                            qError = "User did not respond in time";
                        }
                        catch (OperationCanceledException)
                        {
                            qError = "User cancelled the question";
                        }
                        finally
                        {
                            _pendingQuestions.TryRemove(pendingId, out _);
                        }

                        if (qError != null)
                        {
                            var errResult = new Dictionary<string, object?>
                            {
                                ["index"] = stepIndex++, ["type"] = "email",
                                ["action"] = action, ["status"] = "error",
                                ["command"] = qError,
                                ["output"] = qError
                            };
                            steps.Add(errResult);
                            if (emitSse) await SendSse(Response, "step", errResult, ct);
                            conversation.AppendLine($"⚠ Error: {qError}");
                            conversation.AppendLine();
                            continue;
                        }
                    }

                    // Resolve account specifier to index (outside try so catch can reference it)
                    int? resolvedIndex = null;
                    try
                    {
                        if (accountSpec != null)
                        {
                            var cfg = await _configFile.LoadConfigAsync();
                            // Try exact label match first, then index
                            var matchIdx = cfg.emailAccounts
                                .Select((a, idx) => new { a, idx })
                                .FirstOrDefault(x =>
                                    string.Equals(x.a.label, accountSpec, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(x.a.username, accountSpec, StringComparison.OrdinalIgnoreCase))
                                ?.idx;
                            if (matchIdx.HasValue)
                            {
                                resolvedIndex = matchIdx.Value;
                            }
                            else if (int.TryParse(accountSpec, out var parsedIdx) && parsedIdx >= 0 && parsedIdx < cfg.emailAccounts.Count)
                            {
                                resolvedIndex = parsedIdx;
                            }
                            else
                            {
                                conversation.AppendLine($"⚠ Error: Account '{accountSpec}' not found. Configured accounts: {string.Join(", ", cfg.emailAccounts.Select((a, i) => $"{i}: {a.label ?? a.username ?? "unnamed"}"))}");
                                conversation.AppendLine();
                                continue;
                            }
                        }

                        if (action == "validate")
                        {
                            var status = await _emailService.ValidateConfigAsync(resolvedIndex);
                            var validateResult = new Dictionary<string, object?>
                            {
                                ["index"] = stepIndex++,
                                ["type"] = "email",
                                ["action"] = action,
                                ["status"] = status.StartsWith("ok") ? "done" : "error",
                                ["command"] = status,
                                ["output"] = status
                            };
                            steps.Add(validateResult);
                            if (emitSse) await SendSse(Response, "step", validateResult, ct);
                            if (status.StartsWith("ok"))
                            {
                                conversation.AppendLine($"Email validate [{i + 1}]: {status}");
                            }
                            else if (status == "not_configured")
                            {
                                conversation.AppendLine("⚠ Error: Email is NOT configured. The user must add at least one email account in Settings. Do NOT retry — tell the user to configure email first, then call {\"done\": true}.");
                            }
                            else
                            {
                                var statusLower = status.ToLowerInvariant();
                                if (statusLower.Contains("authentication") || statusLower.Contains("invalid credentials") || statusLower.Contains("app"))
                                {
                                    var emailCfg = await _configFile.LoadConfigAsync();
                                    var targetAccount = resolvedIndex.HasValue ? emailCfg.emailAccounts[resolvedIndex.Value] : emailCfg.emailAccounts.FirstOrDefault();
                                    var server = (targetAccount?.imapServer ?? "").ToLowerInvariant();
                                    if (server.Contains("gmail") || server.Contains("google"))
                                        conversation.AppendLine("⚠ Error: Gmail rejected the login. Google requires an App Password (not your regular password) when 2-factor authentication is enabled. Generate one at https://myaccount.google.com/apppasswords and save it in Settings.");
                                    else if (server.Contains("outlook") || server.Contains("office") || server.Contains("live") || server.Contains("hotmail") || server.Contains("msn"))
                                        conversation.AppendLine("⚠ Error: Outlook/Hotmail rejected the login. Microsoft requires an App Password (not your regular password) when 2-factor authentication is enabled. Generate one at https://account.live.com/password/apppasswords and save it in Settings.");
                                    else
                                        conversation.AppendLine("⚠ Error: The email server rejected the login. Check your username and password. Some providers require an App Password when 2-factor authentication is enabled.");
                                }
                                else
                                    conversation.AppendLine($"⚠ Error: Email validation failed — {status} This is NOT a transient error; retrying will not fix it. Do NOT retry — tell the user the email issue, then call {{\"done\": true}}.");
                            }
                            conversation.AppendLine();
                            continue;
                        }

                        var unreadOnly = action == "inbox" || action == "unread";
                        var emails = await _emailService.FetchLatestEmailsAsync(10, unreadOnly, resolvedIndex);

                        // Group by account for display
                        var emailOutput = new StringBuilder();
                        if (emails.Count == 0)
                        {
                            emailOutput.AppendLine("No emails found.");
                        }
                        else
                        {
                            var groupedEmails = emails.GroupBy(e => e.AccountLabel ?? "Email").ToList();
                            emailOutput.AppendLine($"Found {emails.Count} email(s)" +
                                (groupedEmails.Count > 1 ? $" across {groupedEmails.Count} account(s):" : ":"));
                            emailOutput.AppendLine();
                            foreach (var group in groupedEmails)
                            {
                                if (groupedEmails.Count > 1)
                                    emailOutput.AppendLine($"--- {group.Key} ---");
                                foreach (var e in group)
                                {
                                    emailOutput.AppendLine($"From: {e.From}");
                                    emailOutput.AppendLine($"Subject: {e.Subject}");
                                    emailOutput.AppendLine($"Date: {e.Date:yyyy-MM-dd HH:mm}");
                                    var bodyPreview = e.Body.Length > 500 ? e.Body[..500] + "…" : e.Body;
                                    if (!string.IsNullOrWhiteSpace(bodyPreview))
                                    {
                                        emailOutput.AppendLine("Body:");
                                        emailOutput.AppendLine(bodyPreview);
                                    }
                                    emailOutput.AppendLine();
                                }
                            }
                        }
                        var emailStr = emailOutput.ToString();

                        // If file path specified, write email data directly to file (no LLM round-trip)
                        if (!string.IsNullOrWhiteSpace(emailFilePath))
                        {
                            try
                            {
                                var dir = Path.GetDirectoryName(emailFilePath);
                                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                                    Directory.CreateDirectory(dir);
                                await System.IO.File.WriteAllTextAsync(emailFilePath, emailStr, Encoding.UTF8, ct);
                                await EmitLog(emitSse, "success", $"Email data written to {emailFilePath} ({emailStr.Length} chars)", ct: ct);
                            }
                            catch (Exception ex)
                            {
                                await EmitLog(emitSse, "error", $"Failed to write email file {emailFilePath}: {ex.Message}", ct: ct);
                            }
                        }

                        var emailResult = new Dictionary<string, object?>
                        {
                            ["index"] = stepIndex++,
                            ["type"] = "email",
                            ["action"] = action,
                            ["status"] = "done",
                            ["command"] = $"fetched {emails.Count} email(s)" + (resolvedIndex.HasValue ? $" from account [{resolvedIndex}]" : " from all accounts"),
                            ["output"] = emailFilePath != null ? $"Written to {emailFilePath}" : emailStr
                        };
                        steps.Add(emailResult);
                        if (emitSse) await SendSse(Response, "step", emailResult, ct);

                        // Truncate email data in conversation to avoid overwhelming the LLM.
                        // Full data is in the step result (and file if file path was given).
                        var emailSummary = new StringBuilder();
                        emailSummary.AppendLine(emailFilePath != null
                            ? $"Written {emails.Count} email(s) to {emailFilePath}"
                            : $"Fetched {emails.Count} email(s)");
                        emailSummary.AppendLine();
                        var grouped = emails.GroupBy(e => e.AccountLabel ?? "Email").ToList();
                        foreach (var g in grouped)
                        {
                            if (grouped.Count > 1)
                                emailSummary.AppendLine($"--- {g.Key} ---");
                            foreach (var e in g)
                            {
                                emailSummary.AppendLine($"From: {e.From} | Subject: {e.Subject} | Date: {e.Date:yyyy-MM-dd HH:mm}");
                            }
                            emailSummary.AppendLine();
                        }
                        var fileWritten = emailFilePath != null;
                        if (fileWritten)
                        {
                            summary = $"Fetched {emails.Count} email(s) and wrote to {emailFilePath}";
                            await EmitLog(emitSse, "success", $"✓ Task complete: {summary}", ct: ct);
                            // Auto-complete: email+file action fulfilled the task, no need to go back to LLM
                            break;
                        }
                        conversation.AppendLine($"Email [{i + 1}]: fetched {emails.Count} email(s)");
                        conversation.Append(emailSummary.ToString());
                    }
                    catch (Exception ex)
                    {
                        var errMsg = $"⚠ Error: Email failed — {ex.Message} This is NOT a transient error; retrying will not fix it. Do NOT retry — tell the user the email issue, then call {{\"done\": true}}.";
                        var exLower = ex.Message.ToLowerInvariant();
                        if (ex.Message.Contains("not configured", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("not fully configured", StringComparison.OrdinalIgnoreCase))
                            errMsg = "⚠ Error: Email is NOT configured. The user must add at least one email account in Settings. Do NOT retry — tell the user to configure email first, then call {\"done\": true}.";
                        else if (exLower.Contains("authentication") || exLower.Contains("invalid credentials") || exLower.Contains("app") || exLower.Contains("password"))
                        {
                            var emailCfg = await _configFile.LoadConfigAsync();
                            var targetAccount = resolvedIndex.HasValue && resolvedIndex.Value < emailCfg.emailAccounts.Count
                                ? emailCfg.emailAccounts[resolvedIndex.Value] : emailCfg.emailAccounts.FirstOrDefault();
                            var server = (targetAccount?.imapServer ?? "").ToLowerInvariant();
                            if (server.Contains("gmail") || server.Contains("google"))
                                errMsg = "⚠ Error: Gmail rejected the login. Google requires an App Password (not your regular password) when 2-factor authentication is enabled. Generate one at https://myaccount.google.com/apppasswords and save it in Settings.";
                            else if (server.Contains("outlook") || server.Contains("office") || server.Contains("live") || server.Contains("hotmail") || server.Contains("msn"))
                                errMsg = "⚠ Error: Outlook/Hotmail rejected the login. Microsoft requires an App Password (not your regular password) when 2-factor authentication is enabled. Generate one at https://account.live.com/password/apppasswords and save it in Settings.";
                            else
                                errMsg = "⚠ Error: The email server rejected the login. Check that your username and password are correct. Some providers require an App Password (not your regular password) when 2-factor authentication is enabled.";
                        }
                        var errResult = new Dictionary<string, object?>
                        {
                            ["index"] = stepIndex++, ["type"] = "email",
                            ["action"] = action, ["status"] = "error",
                            ["command"] = errMsg,
                            ["output"] = errMsg
                        };
                        steps.Add(errResult);
                        if (emitSse) await SendSse(Response, "step", new { errResult, errMsg }, ct);
                        conversation.AppendLine(errMsg);
                    }
                    conversation.AppendLine();
                    continue;
                }

                // Web fetch
                if (root.TryGetProperty("web_fetch", out var fetchEl))
                {
                    var url = fetchEl.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(url))
                    { conversation.AppendLine("Empty web_fetch URL — try again."); continue; }

                    await EmitLog(emitSse, "step", $"▶ web_fetch[{i + 1}]: {url}", ct: ct);
                    var (fetchOutput, fetchErr) = await WebFetchAsync(url, ct);
                    var fetchResult = new Dictionary<string, object?>
                    {
                        ["index"] = stepIndex++,
                        ["type"] = "web_fetch",
                        ["url"] = url,
                        ["status"] = string.IsNullOrWhiteSpace(fetchErr) ? "done" : "error",
                        ["output"] = fetchOutput
                    };
                    steps.Add(fetchResult);
                    if (emitSse) await SendSse(Response, "step", fetchResult, ct);

                    conversation.AppendLine($"Web fetch [{i + 1}]: {url}");
                    conversation.AppendLine("Content:");
                    conversation.AppendLine(fetchOutput);
                    conversation.AppendLine();

                    // Auto-extract phone numbers from fetched content
                    var fetchPhones = AgentUtilities.ExtractPhoneNumbers(fetchOutput);
                    if (fetchPhones.Count > 0)
                    {
                        conversation.AppendLine("ℹ PHONE NUMBERS FOUND in fetched content:");
                        foreach (var p in fetchPhones)
                            conversation.AppendLine("  " + p);
                        conversation.AppendLine();
                    }

                    // Detect empty or unhelpful fetch results and suggest web_search
                    var fetchLower = fetchOutput.ToLowerInvariant();
                    bool fetchEmpty = string.IsNullOrWhiteSpace(fetchOutput) ||
                        fetchLower.Contains("no results") ||
                        fetchLower.Contains("(no results") ||
                        fetchLower.Contains("could not reach") ||
                        fetchLower.Contains("error");
                    if (!string.IsNullOrWhiteSpace(fetchErr) || fetchEmpty)
                    {
                        conversation.AppendLine("ℹ HINT: This URL returned no useful data. Try web_search with a specific query instead of scraping raw websites.");
                        conversation.AppendLine();
                    }

                    continue;
                }

                // Message / result — display text to the user
                if (root.TryGetProperty("message", out var msgEl) || root.TryGetProperty("result", out msgEl))
                {
                    var msgText = msgEl.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(msgText))
                    { conversation.AppendLine("Empty message — try again."); continue; }

                    var msgResult = new Dictionary<string, object?>
                    {
                        ["index"] = stepIndex++,
                        ["type"] = "message",
                        ["output"] = msgText
                    };
                    steps.Add(msgResult);
                    if (emitSse) await SendSse(Response, "step", msgResult, ct);

                    conversation.AppendLine($"Message: {msgText}");
                    conversation.AppendLine();
                    continue;
                }

                // If we got valid JSON but no recognized property, tell LLM
                conversation.AppendLine("Could not parse response — try again with valid JSON using one of: cmd, web_search, web_fetch, email, message, or done.");
                conversation.AppendLine();
                continue;
            }

            // Fallback: treat raw text as command
            var fallbackCmd = cleaned.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(fallbackCmd) && fallbackCmd.Length < 500)
            {
                conversation.AppendLine($"Trying raw: {fallbackCmd}");
                var beforeLen2 = _terminal.ReadAll().Length;
                await _terminal.SendCommandAsync(fallbackCmd, projectRoot);

                // Adaptive wait (same pattern as above)
                var prevLen2 = beforeLen2;
                var stableMs2 = 0;
                for (var w2 = 0; w2 < 40; w2++)
                {
                    await Task.Delay(500);
                    var curLen2 = _terminal.ReadAll().Length;
                    if (curLen2 == prevLen2) { stableMs2 += 500; if (stableMs2 >= 3000) break; }
                    else { stableMs2 = 0; prevLen2 = curLen2; }
                }

                var output2 = _terminal.ReadAll();
                var fresh2 = beforeLen2 < output2.Length ? output2[beforeLen2..] : "";
                conversation.AppendLine("Output:");
                conversation.AppendLine(fresh2);
                conversation.AppendLine();
                steps.Add(new Dictionary<string, object?>
                {
                    ["index"] = stepIndex++,
                    ["type"] = "command",
                    ["command"] = fallbackCmd,
                    ["status"] = "done",
                    ["output"] = fresh2
                });
                continue;
            }

            conversation.AppendLine("Could not parse response — try again with valid JSON.");
        }

        summary ??= $"Command execution completed after {steps.Count} step(s)";
        await EmitLog(emitSse, "info", summary, steps, ct: ct);

        return (steps, null);
    } 
    
    // ═════════════════════════════════════════════════════════════════════════
    //  CODE EDIT PIPELINE  —  discover → plan → edit → review loop
    // ═════════════════════════════════════════════════════════════════════════

    private async Task<(List<object> steps, AgentPlan plan)> UnifiedPipeline(
        string prompt, string projectRoot, bool emitSse, CancellationToken ct,
        List<string>? attachedFiles = null,
        string? prebuiltDiscoveryContext = null,
        List<object>? prebuiltDiscoverySteps = null,
        bool skipContextReview = false,
        string? steeringContext = null)
    {
        var allSteps = new List<object>();
        string discoveryContext = string.Empty;

        await EmitLog(emitSse, "info", "CodeEdit: Phase 1 — DISCOVER", ct: ct);
        discoveryContext = await UnifiedDiscoveryPipeline(prompt, projectRoot, emitSse, attachedFiles, prebuiltDiscoveryContext, prebuiltDiscoverySteps, allSteps, discoveryContext, ct, skipContextReview);

        await EmitLog(emitSse, "info", "CodeEdit: Phase 2 — PLAN", ct: ct);
        if (emitSse)
            await SendSse(Response, "phase", new { phase = "plan", message = "Planning...", contextSize = discoveryContext.Length }, ct);

        AgentPlan? plan = await AnalyzePromptAndPlanCodeChanges(prompt, discoveryContext, projectRoot, emitSse, ct, steeringContext: steeringContext); 
        await EmitPlanningResults(emitSse, allSteps, discoveryContext, plan, ct);
        if (plan == null)  { throw new InvalidOperationException("LLM returned an empty or unparseable plan."); }

        //TODO : DO THIS CODE COMPACTION IN A SPERATE PROCESS 
        // Make sure this method does not return unless compaction finishes (if compaction needed)

        // ── Phase 2.5: EXPLORE — if plan has _explore steps, gather deeper context and re-plan ──
        var exploreSteps = plan.Plan.Where(p => p.File.Equals("_explore", StringComparison.OrdinalIgnoreCase)).ToList();
        if (exploreSteps.Count > 0)
        {
            await EmitLog(emitSse, "info", $"Phase 2.5 — EXPLORE: {exploreSteps.Count} step(s), gathering deeper context…", ct: ct);
            if (emitSse)
                await SendSse(Response, "phase", new { phase = "explore", message = $"Exploring {exploreSteps.Count} target(s)…" }, ct);

            discoveryContext = await ExplorationPipeline(exploreSteps, discoveryContext, projectRoot, emitSse, ct);

            // Re-plan with enriched context
            await EmitLog(emitSse, "info", "Re-planning with enriched context…", new { enrichedContext = discoveryContext, contextSize = discoveryContext.Length }, ct: ct);
            plan = await AnalyzePromptAndPlanCodeChanges(prompt, discoveryContext, projectRoot, emitSse, ct, steeringContext: steeringContext);
            await EmitPlanningResults(emitSse, allSteps, discoveryContext, plan, ct);
            if (plan == null) { throw new InvalidOperationException("Re-plan after exploration returned empty."); }
        }

        plan = await SplitOversizedEdits(plan, projectRoot, emitSse, ct);  // split first
        plan = await RepairPlanEditAnchors(prompt, plan, projectRoot, emitSse, ct);  // then validate anchors


        // ── Compact discovery context in background (don't block LLM) ──
        var ctxForCompaction = discoveryContext;
        var planFiles = new HashSet<string?>(plan.Plan
            .Select(p => p.File?.Replace('\\', '/'))
            .Where(f => !string.IsNullOrWhiteSpace(f)), StringComparer.OrdinalIgnoreCase);
        _ = Task.Run(async () =>
        {
            try
            {
                var beforeTokens = AgentUtilities.EstimateTokens(ctxForCompaction);
                var compacted = AgentUtilities.CompactDiscoveryContext(ctxForCompaction, planFiles);
                var afterTokens = AgentUtilities.EstimateTokens(compacted);
                if (afterTokens < beforeTokens)
                    await EmitLog(emitSse, "info",
                        $"Compacted discovery context: {beforeTokens} → {afterTokens} tokens ({beforeTokens - afterTokens} saved)", ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await EmitLog(emitSse, "warn", $"Background compaction failed: {ex.Message}", ct);
            }
        }, ct);

        await ExecutePlan(prompt, projectRoot, emitSse, discoveryContext, plan, ct, allSteps,
            steeringContext: steeringContext);
        return (allSteps, plan);
    }

    /// <summary>
    /// Exploration pipeline: reads files or glob patterns specified in _explore steps
    /// and returns enriched discovery context. Called before any edits are made.
    /// </summary>
    private async Task<string> ExplorationPipeline(List<PlanStep> exploreSteps, string discoveryContext,
        string projectRoot, bool emitSse, CancellationToken ct)
    {
        var enriched = new StringBuilder();
        enriched.AppendLine(discoveryContext);
        enriched.AppendLine();

        foreach (var step in exploreSteps)
        {
            var target = step.Change?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(target)) continue;

            await EmitLog(emitSse, "info", $"Exploring: {target}", ct: ct);

            // Glob pattern (contains * or ?)
            if (target.Contains('*') || target.Contains('?'))
            {
                var sep = Path.DirectorySeparatorChar;
                var pattern = target.Replace('/', sep);
                var dir = Path.GetDirectoryName(pattern) ?? ".";
                var searchDir = Path.GetFullPath(Path.Combine(projectRoot, dir));
                var fileName = Path.GetFileName(pattern);
                if (!Directory.Exists(searchDir)) continue;

                var matches = Directory.EnumerateFiles(searchDir, fileName, SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/'))
                    .Where(f => AgentUtilities.IsPathUnderRoot(
                        Path.GetFullPath(Path.Combine(projectRoot, f.Replace('/', sep))), projectRoot))
                    .Take(10)
                    .ToList();

                foreach (var match in matches)
                {
                    var fullPath = Path.GetFullPath(Path.Combine(projectRoot, match.Replace('/', sep)));
                    if (!System.IO.File.Exists(fullPath)) continue;
                    var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                    enriched.AppendLine($"### {match}");
                    enriched.AppendLine("```");
                    enriched.AppendLine(content);
                    enriched.AppendLine("```");
                    enriched.AppendLine();
                }
            }
            else
            {
                // Single file path
                var fullPath = Path.GetFullPath(Path.Combine(projectRoot, target.Replace('/', Path.DirectorySeparatorChar)));
                if (System.IO.File.Exists(fullPath) && AgentUtilities.IsPathUnderRoot(fullPath, projectRoot))
                {
                    var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                    enriched.AppendLine($"### {target}");
                    enriched.AppendLine("```");
                    enriched.AppendLine(content);
                    enriched.AppendLine("```");
                    enriched.AppendLine();
                }
                else
                {
                    await EmitLog(emitSse, "warn", $"Explore target not found: {target}", ct: ct);
                }
            }
        }

        await EmitLog(emitSse, "info", $"Exploration complete — context grew by {enriched.Length - discoveryContext.Length} chars", ct: ct);
        return enriched.ToString();
    }

    private async Task EmitPlanningResults(bool emitSse, List<object> allSteps, string discoveryContext, AgentPlan? plan, CancellationToken ct)
    {
        if (plan == null || plan.Plan.Count == 0)
        {
            await EmitLog(emitSse, "warn", $"Plan phase produced no items. Context length: {discoveryContext.Length} characters.", new { plan }, ct: ct);
            var result = new Dictionary<string, object?>
            {
                ["index"] = allSteps.Count,
                ["type"] = "plan",
                ["description"] = "LLM-generated plan of code changes",
                ["status"] = "error",
                ["error"] = "LLM returned an empty or unparseable plan."
            };
            allSteps.Add(result);
            throw new InvalidOperationException("LLM returned an empty or unparseable plan.");
        }
        else
        {
            var result = new Dictionary<string, object?>
            {
                ["index"] = allSteps.Count,
                ["type"] = "plan",
                ["description"] = "LLM-generated plan of code changes",
                ["status"] = "complete"
            };
            allSteps.Add(result);
        }
 
        if (emitSse && !string.IsNullOrWhiteSpace(plan.Thinking)) {
            await SendSse(Response, "thinking", new { text = plan.Thinking }, ct);
        }

        await EmitLog(emitSse, "info",
            $"Plan: {plan.Plan.Count} step(s) — {string.Join(", ", plan.Plan.Select(p => p.File))}. Context length: {discoveryContext.Length} characters.",
            new { plan },
            ct: ct);

        if (emitSse) {
            await SendSse(Response, "plan",
                new { thinking = plan.Thinking, summary = plan.Summary, items = plan.Plan, contextSize = discoveryContext.Length }, ct);
        }
    }

    private async Task<string> UnifiedDiscoveryPipeline(string prompt, string projectRoot, bool emitSse, List<string>? attachedFiles, string? prebuiltDiscoveryContext, List<object>? prebuiltDiscoverySteps, List<object> allSteps, string discoveryContext, CancellationToken ct, bool skipContextReview = false)
    {
        var (dc, ds) = await RunBootstrapDiscovery(prompt, projectRoot, emitSse, attachedFiles, ct);
        discoveryContext = dc;
        allSteps.AddRange(ds);

        // ── Context Review: let user confirm / remove files ──────────────
        if (emitSse && !skipContextReview && prebuiltDiscoveryContext == null && prebuiltDiscoverySteps == null)
        {
            var readFiles = ds
                .OfType<Dictionary<string, object?>>()
                .Where(s => s.TryGetValue("type", out var t) && t?.ToString() == "read")
                .Select(s => s.GetValueOrDefault("path")?.ToString())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (readFiles.Count > 0)
            {
                var reviewId = Guid.NewGuid().ToString();
                var review = new PendingContextReview
                {
                    Id = reviewId,
                    Files = readFiles.Where(f => f != null).ToList()!,
                    CreatedUtc = DateTime.UtcNow,
                    Answer = new TaskCompletionSource<List<string>>()
                };
                _pendingContextReviews[reviewId] = review;

                await SendSse(Response, "context-review", new
                {
                    id = reviewId,
                    files = readFiles.Select(f => new { path = f }).ToList(),
                    contextSize = discoveryContext.Length
                }, ct);

                await EmitLog(emitSse, "info", "⏳ Awaiting context review (will auto-confirm in 15s)...", ct: ct);

                try
                {
                    var timeout = TimeSpan.FromSeconds(30);
                    var confirmedFiles = await review.Answer.Task.WaitAsync(timeout, ct);

                    var confirmedSet = new HashSet<string>(confirmedFiles, StringComparer.OrdinalIgnoreCase);
                    var removedCount = readFiles.Count - confirmedFiles.Count;

                    if (removedCount > 0)
                    {
                        var filteredSteps = new List<object>();
                        foreach (var item in ds)
                        {
                            if (item is not Dictionary<string, object?> r) { filteredSteps.Add(item); continue; }
                            var type = r.TryGetValue("type", out var t) ? t?.ToString() : "";
                            if (type == "read")
                            {
                                var p = r.GetValueOrDefault("path")?.ToString();
                                if (!string.IsNullOrWhiteSpace(p) && confirmedSet.Contains(p))
                                    filteredSteps.Add(item);
                            }
                            else
                            {
                                filteredSteps.Add(item);
                            }
                        }
                        allSteps.Clear();
                        allSteps.AddRange(filteredSteps);
                        discoveryContext = AgentUtilities.BuildDiscoveryTextFromSteps(filteredSteps);
                        await EmitLog(emitSse, "info", $"Context review: {removedCount} file(s) removed, {confirmedFiles.Count} kept", ct: ct);
                    }
                    else
                    {
                        await EmitLog(emitSse, "info", "Context review: all files confirmed", ct: ct);
                    }
                }
                catch (TimeoutException)
                {
                    await EmitLog(emitSse, "info", "Context review timed out — proceeding with all files", ct: ct);
                }
                catch (OperationCanceledException) { }
                finally
                {
                    _pendingContextReviews.TryRemove(reviewId, out _);
                }
            }
        }

        return discoveryContext;
    }

    private async Task<AgentPlan> SplitOversizedEdits(
        AgentPlan plan, string projectRoot, bool emitSse, CancellationToken ct)
    {
        var oversized = plan.Plan
            .Where(s => AgentUtilities.IsRelativePath(s.File ?? "") &&
                        ((s.OldString ?? "").Split('\n').Length > 5 || (s.OldString ?? "").Length > 250))
            .ToList();

        if (oversized.Count == 0) return plan;

        await EmitLog(emitSse, "info",
            $"Split pass: {oversized.Count} oversized edit(s) found — atomizing…", ct: ct);

        var resultSteps = new List<PlanStep>();
        foreach (var step in plan.Plan)
        {
            var old = step.OldString ?? "";
            if (!AgentUtilities.IsRelativePath(step.File ?? "") ||
                (old.Split('\n').Length <= 5 && old.Length <= 250))
            {
                resultSteps.Add(step);
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(projectRoot,
                step.File.Replace('/', Path.DirectorySeparatorChar)));
            if (!System.IO.File.Exists(fullPath)) { resultSteps.Add(step); continue; }

            var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
            const string sys =
                "Split one oversized code edit into multiple small atomic edits (1-3 lines each). " +
                "Output ONLY valid JSON: {\"steps\":[{\"oldString\":\"...\",\"newString\":\"...\"}]} " +
                "Each step must have a unique oldString that exists verbatim in the file. No markdown.";

            var user = new StringBuilder();
            user.AppendLine($"File: {step.File}");
            user.AppendLine($"Intended change: {step.Change}");
            user.AppendLine($"\nOversize oldString ({old.Length} chars):\n{old}");
            user.AppendLine($"\nnewString:\n{step.NewString ?? ""}");
            user.AppendLine("\nFile content:");
            user.AppendLine("```");
            user.AppendLine(content.Length > 6000 ? content[..6000] + "\n…(truncated)" : content);
            user.AppendLine("```");
            user.AppendLine("\nSplit into 2-5 atomic steps targeting only what changes.");

            var (raw, _, _) = await CallLlmRaw(sys, user.ToString(), ct,
                TimeSpan.FromSeconds(25), maxTokens: 1024);

            if (string.IsNullOrWhiteSpace(raw)) { resultSteps.Add(step); continue; }

            try
            {
                var cleaned = raw.Trim();
                if (cleaned.StartsWith("```"))
                {
                    var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                    if (m.Success) cleaned = m.Groups[1].Value.Trim();
                }
                using var doc = JsonDocument.Parse(cleaned);
                if (doc.RootElement.TryGetProperty("steps", out var stepsEl) &&
                    stepsEl.ValueKind == JsonValueKind.Array)
                {
                    var newSteps = stepsEl.EnumerateArray()
                        .Select(el => new PlanStep
                        {
                            File = step.File,
                            Change = step.Change,
                            Priority = step.Priority,
                            OldString = el.TryGetProperty("oldString", out var os) ? os.GetString() ?? "" : "",
                            NewString = el.TryGetProperty("newString", out var ns) ? ns.GetString() ?? "" : ""
                        })
                        .Where(s => !string.IsNullOrWhiteSpace(s.OldString))
                        .ToList();

                    if (newSteps.Count > 0)
                    {
                        resultSteps.AddRange(newSteps);
                        await EmitLog(emitSse, "info",
                            $"Split {step.File}: 1 oversized edit → {newSteps.Count} atomic step(s)", ct: ct);
                        continue;
                    }
                }
            }
            catch { /* fall through */ }

            resultSteps.Add(step); // keep original if split fails
        }

        plan.Plan = resultSteps;
        return plan;
    }

    private async Task<AgentPlan> RepairPlanEditAnchors(
        string prompt, AgentPlan plan, string projectRoot, bool emitSse, CancellationToken ct)
    {
        var contentCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var repairedCount = 0;
        var checkedCount = 0;

        foreach (var item in plan.Plan.Where(p => AgentUtilities.IsRelativePath(p.File ?? "")))
        {
            checkedCount++;
            var relPath = item.File.Replace('\\', '/');
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));

            if (!AgentUtilities.IsPathUnderRoot(fullPath, projectRoot))
            {
                await EmitLog(emitSse, "warn", $"Plan edit path outside project root: {relPath}", ct: ct);
                continue;
            }

            if (!System.IO.File.Exists(fullPath))
            {
                var suggestions = AgentUtilities.FindSimilarFiles(relPath, projectRoot);
                var exactNameMatches = suggestions
                    .Where(s => string.Equals(Path.GetFileName(s), Path.GetFileName(relPath), StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (exactNameMatches.Count == 1)
                {
                    item.File = exactNameMatches[0].Replace('\\', '/');
                    relPath = item.File;
                    fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
                    await EmitLog(emitSse, "info", $"Corrected plan path to {relPath}", ct: ct);
                }
                else
                {
                    await EmitLog(emitSse, "warn", $"Plan edit file not found: {relPath}", new { suggestions }, ct: ct);
                    continue;
                }
            }

            if (!contentCache.TryGetValue(fullPath, out var content))
            {
                content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                contentCache[fullPath] = content;
            }

            var validation = ValidatePlannedEdit(content, item);
            if (validation.ok)
            {
                contentCache[fullPath] = validation.newContent!;
                continue;
            }

            var history = new List<(string oldString, string newString, int score, string reason)>
            {
                (item.OldString ?? "", item.NewString ?? "", validation.score, validation.reason)
            };

            for (var attempt = 0; attempt < 2 && !validation.ok; attempt++)
            {
                await EmitLog(emitSse, "info",
                    $"Repairing edit anchor for {relPath} ({attempt + 1}/2): {validation.reason}", ct: ct);
                var corrected = await CorrectEdit(prompt, relPath, content, item.Change, history, attempt, emitSse, ct);
                if (corrected == null) break;

                var originalOld = item.OldString ?? "";
                var originalNew = item.NewString ?? "";
                item.OldString = corrected.Value.oldString;
                item.NewString = corrected.Value.newString;

                validation = ValidatePlannedEdit(content, item);
                if (validation.ok)
                {
                    contentCache[fullPath] = validation.newContent!;
                    repairedCount++;
                    break;
                }

                history.Add((item.OldString ?? "", item.NewString ?? "", validation.score, validation.reason));
                item.OldString = originalOld;
                item.NewString = originalNew;
            }
        }

        if (checkedCount > 0)
        {
            await EmitLog(emitSse, repairedCount > 0 ? "success" : "info",
                repairedCount > 0
                    ? $"Preflight repaired {repairedCount} edit anchor(s)"
                    : $"Preflight checked {checkedCount} edit anchor(s)",
                ct: ct);
        }

        return plan;
    }

    private static (bool ok, string reason, int score, string? newContent) ValidatePlannedEdit(string content, PlanStep item)
    {
        var oldString = item.OldString ?? "";
        var newString = item.NewString ?? "";
        var unsafeReason = GetUnsafeEditPayloadReason(oldString, newString);
        if (unsafeReason != null)
            return (false, unsafeReason, 0, null);
        if (string.IsNullOrWhiteSpace(oldString))
            return (false, "No oldString provided", 0, null);
        if (oldString.Trim() == newString.Trim())
            return (false, "oldString and newString are identical", 3, null);

        var (replaced, newContent, matchError, snippet) = TryReplacePrecise(content, oldString, newString);
        if (!replaced)
        {
            var reason = matchError ?? "oldString not found in file";
            if (!string.IsNullOrWhiteSpace(snippet))
                reason += $". Nearby content: {snippet}";
            return (false, reason, 1, null);
        }

        var (approved, verifyReason, score) = VerifyEdit(oldString, newString, content, newContent);
        return approved
            ? (true, verifyReason, score, newContent)
            : (false, verifyReason, score, null);
    }

    private async Task ExecutePlan(string prompt, string projectRoot, bool emitSse, string discoveryContext, AgentPlan plan, CancellationToken ct, List<object> allResults, string? steeringContext = null)
    {
        //  string File, string ChangeDescription, int Priority
        var stepIndex = 0;
        var planItems = plan.Plan.ToList();
        var webCtx = new StringBuilder();
        var checkpointCount = 0;          // ← ADD
        const int MaxCheckpoints = 3;     // ← ADD
        for (var itemIdx = 0; itemIdx < planItems.Count; itemIdx++)
        {
            var item = planItems[itemIdx];
            var planFile = item.File;
            var changeDesc = item.Change;

            // ── _done: LLM signals task is already complete ───────────────────────────
            if (planFile.Equals("_done", StringComparison.OrdinalIgnoreCase))
            {
                await EmitLog(emitSse, "success",
                    $"Task self-reported complete: {changeDesc}", ct: ct);
                if (emitSse)
                    await SendSse(Response, "done_signal", new { message = changeDesc }, ct);
                allResults.Add(new Dictionary<string, object?>
                {
                    ["type"] = "done_signal",
                    ["status"] = "done",
                    ["output"] = changeDesc
                });
                return; // Short-circuit — no further steps needed
            }

            // ── _checkpoint: refresh context and replan remaining steps ───────────────
            if (planFile.Equals("_checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                if (++checkpointCount > MaxCheckpoints)
                {
                    await EmitLog(emitSse, "warn",
                        $"Max checkpoints ({MaxCheckpoints}) reached — continuing without replan", ct: ct);
                    continue;
                }

                await EmitLog(emitSse, "info",
                    $"Checkpoint {checkpointCount}/{MaxCheckpoints}: {changeDesc}", ct: ct);
                if (emitSse)
                    await SendSse(Response, "phase",
                        new { phase = "checkpoint", message = $"Checkpoint {checkpointCount}: refreshing context…" }, ct);

                allResults.Add(new Dictionary<string, object?>
                {
                    ["type"] = "checkpoint",
                    ["status"] = "done",
                    ["output"] = changeDesc
                });

                var remaining = planItems.Skip(itemIdx + 1).ToList();
                if (remaining.Count > 0)
                {
                    var newSteps = await CheckpointReplan(
                        prompt, discoveryContext, remaining, allResults,
                        projectRoot, emitSse, ct, steeringContext);

                    if (newSteps?.Count > 0)
                    {
                        // Replace the rest of the plan with freshly anchored steps
                        planItems = planItems.Take(itemIdx + 1).Concat(newSteps).ToList();
                        await EmitLog(emitSse, "info",
                            $"Checkpoint replan: {newSteps.Count} new step(s)", ct: ct);
                        if (emitSse)
                            await SendSse(Response, "plan",
                                new { summary = $"Phase {checkpointCount + 1} plan", items = newSteps }, ct);
                    }
                }
                continue;
            }
            // ── _rename marker ────────────────────────────────────────────────────
            if (planFile.Equals("_rename", StringComparison.OrdinalIgnoreCase))
            {
                // changeDesc is "src → dst"  or  "src to dst"
                string? renameSrc = null, renameDst = null;
                var arrowIdx = changeDesc.IndexOf('→');
                if (arrowIdx > 0)
                {
                    renameSrc = changeDesc[..arrowIdx].Trim();
                    renameDst = changeDesc[(arrowIdx + 1)..].Trim();
                }
                else
                {
                    var toIdx = changeDesc.LastIndexOf(" to ", StringComparison.OrdinalIgnoreCase);
                    if (toIdx > 0)
                    {
                        renameSrc = changeDesc[..toIdx].Trim();
                        renameDst = changeDesc[(toIdx + 4)..].Trim(' ', '"', '\'');
                    }
                }

                if (!string.IsNullOrWhiteSpace(renameSrc) && !string.IsNullOrWhiteSpace(renameDst))
                {
                    renameSrc = renameSrc.Replace('\\', '/').Trim('/');
                    // dst may be dotfile — preserve leading dot
                    renameDst = renameDst.Replace('\\', '/').TrimEnd('/');

                    // Inherit source directory when dst has no directory component
                    if (!renameDst.Contains('/') && renameSrc.Contains('/'))
                    {
                        var srcDir = renameSrc[..(renameSrc.LastIndexOf('/') + 1)];
                        renameDst = srcDir + renameDst;
                    }

                    await EmitLog(emitSse, "info", $"Rename: {renameSrc} → {renameDst}", ct: ct);
                    var renameStep2 = new AgentStep
                    {
                        Index = 0,
                        Type = "rename",
                        Path = renameSrc,
                        ToPath = renameDst,
                        Description = $"Rename {renameSrc} → {renameDst}"
                    };
                    var renameResults = await ExecuteSteps(new List<AgentStep> { renameStep2 }, projectRoot, stepIndex, emitSse, ct);
                    stepIndex += renameResults.Count;
                    allResults.AddRange(renameResults);
                    var renameFirst = renameResults.FirstOrDefault() as Dictionary<string, object?>;
                    var renameStatus = renameFirst?.TryGetValue("status", out var rs) == true ? rs?.ToString() : "";
                    await EmitLog(emitSse, renameStatus == "done" ? "success" : "error",
                        renameStatus == "done" ? $"Renamed {renameSrc} → {renameDst}" : $"Rename failed",
                        renameFirst?.TryGetValue("error", out var re) == true ? new { error = re } : null, ct: ct);
                }
                else
                {
                    await EmitLog(emitSse, "error", $"_rename: could not parse src/dst from: {changeDesc}", ct: ct);
                }
                continue;
            }

            // ── _delete_file marker ───────────────────────────────────────────────
            if (planFile.Equals("_delete_file", StringComparison.OrdinalIgnoreCase))
            {
                var deleteTarget = changeDesc.Trim().Trim('"', '\'').Replace('\\', '/');
                var deleteFullPath = Path.GetFullPath(Path.Combine(projectRoot, deleteTarget.Replace('/', Path.DirectorySeparatorChar)));
                if (AgentUtilities.IsPathUnderRoot(deleteFullPath, projectRoot) && System.IO.File.Exists(deleteFullPath))
                {
                    System.IO.File.Delete(deleteFullPath);
                    await EmitLog(emitSse, "success", $"Deleted {deleteTarget}", ct: ct);
                    allResults.Add(new Dictionary<string, object?> { ["type"] = "rename", ["status"] = "done", ["path"] = deleteTarget, ["editAction"] = "deleted" });
                }
                else
                {
                    await EmitLog(emitSse, "warn", $"Delete target not found or outside root: {deleteTarget}", ct: ct);
                }
                continue;
            }

            if (planFile.Equals("_git", StringComparison.OrdinalIgnoreCase))
            {
                var gitDesc = (changeDesc.Trim().Trim('`', '"', '\'') + " ").ToLowerInvariant();
                string gitCmd;
                var gitOp = "";

                if (gitDesc.StartsWith("commit") || gitDesc.Contains("commit all"))
                {
                    gitOp = "commit";
                    var msgMatch = Regex.Match(gitDesc, @"""[^""]+""");
                    var msg = msgMatch.Success
                        ? msgMatch.Value.Trim('"')
                        : $"Auto-commit {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                    gitCmd = $"git add -A && git commit -m \"{msg.Replace("\"", "\\\"")}\"";
                }
                else if (gitDesc.StartsWith("revert") || gitDesc.Contains("revert all") || gitDesc.Contains("discard"))
                {
                    gitOp = "revert";
                    gitCmd = "git checkout -- .";
                }
                else if (gitDesc.StartsWith("branch") || gitDesc.Contains("create branch") || gitDesc.Contains("new branch"))
                {
                    gitOp = "branch";
                    var branchMatch = Regex.Match(gitDesc, @"branch\s+(\S+)", RegexOptions.IgnoreCase);
                    var branchName = branchMatch.Success ? branchMatch.Groups[1].Value : $"feature/{DateTime.Now:yyyyMMdd-HHmmss}";
                    gitCmd = $"git checkout -b {branchName}";
                }
                else if (gitDesc.StartsWith("pull") || gitDesc.Contains("git pull"))
                {
                    gitOp = "pull";
                    gitCmd = "git pull";
                }
                else if (gitDesc.StartsWith("sync") || gitDesc.Contains("push") || gitDesc.Contains("git push"))
                {
                    gitOp = "sync";
                    gitCmd = "git pull && git push";
                }
                else
                {
                    // Treat as raw git command
                    gitOp = "exec";
                    gitCmd = changeDesc.Trim().Trim('`', '"', '\'');
                    if (!gitCmd.StartsWith("git ", StringComparison.OrdinalIgnoreCase))
                        gitCmd = "git " + gitCmd;
                }

                await EmitLog(emitSse, "info", $"Git {gitOp}: {gitCmd}", ct: ct);
                var gitStep = new AgentStep
                {
                    Index = 0,
                    Type = "command",
                    Command = gitCmd,
                    Description = $"git {gitOp}: {gitCmd}"
                };
                var gitResults = await ExecuteSteps(new List<AgentStep> { gitStep }, projectRoot, stepIndex, emitSse, ct);
                stepIndex += gitResults.Count;
                allResults.AddRange(gitResults);
                var gitFirst = gitResults.FirstOrDefault() as Dictionary<string, object?>;
                var gitOutput = gitFirst?.TryGetValue("output", out var go) == true ? go?.ToString() ?? "" : "";
                var gitStatus = gitFirst?.TryGetValue("status", out var gs) == true ? gs?.ToString() : "";

                // Extract commit hash for commit operations
                string? commitHash = null;
                if (gitOp == "commit")
                {
                    var hashMatch = Regex.Match(gitOutput, @"\[[\w/]+ ([a-f0-9]{7,40})\]", RegexOptions.IgnoreCase);
                    if (hashMatch.Success) commitHash = hashMatch.Groups[1].Value;
                }

                var gitSuccess = gitStatus == "done" && !gitOutput.Contains("fatal", StringComparison.OrdinalIgnoreCase);
                await EmitLog(emitSse, gitSuccess ? "success" : "warn",
                    gitSuccess
                        ? $"Git {gitOp} completed{(commitHash != null ? $" ({commitHash})" : "")}"
                        : $"Git {gitOp} may have failed",
                    new { output = gitOutput }, ct: ct);
                continue;
            }

            // Detect show/display — send text to frontend for user to see
            else if (planFile.Equals("_show", StringComparison.OrdinalIgnoreCase) ||
                planFile.Equals("_display", StringComparison.OrdinalIgnoreCase))
            {
                var showText = (changeDesc.Trim().Trim('`', '"', '\'') + " ")
                    .Replace("display", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("show", "", StringComparison.OrdinalIgnoreCase)
                    .Trim();
                if (string.IsNullOrWhiteSpace(showText)) showText = changeDesc.Trim().Trim('`', '"', '\'');

                await EmitLog(emitSse, "info", showText, ct: ct);
                if (emitSse)
                    await SendSse(Response, "show", new { text = showText }, ct);
                var showResult = new Dictionary<string, object?>
                {
                    ["status"] = "done",
                    ["type"] = "show",
                    ["output"] = showText
                };
                allResults.Add(showResult);
                continue;
            }

            // Detect file creation — create new file with LLM-generated content
            else if (planFile.Equals("_create_file", StringComparison.OrdinalIgnoreCase))
            {
                await EmitLog(emitSse, "info", $"Creating file: {changeDesc}", ct: ct);
                var createResult = await HandleCreateFile(changeDesc, projectRoot, prompt, discoveryContext, stepIndex, emitSse, ct);
                stepIndex += createResult.stepsCount;
                allResults.AddRange(createResult.results);
                continue;
            }

            else if (planFile.Equals("_ping", StringComparison.OrdinalIgnoreCase))
            {
                var pingCmd = changeDesc.Trim().Trim('`', '"', '\'');
                // Resolve <llamaUrl> placeholder or generic request to configured LLM URL
                if (pingCmd.Contains("<llamaUrl>", StringComparison.OrdinalIgnoreCase) ||
                    !pingCmd.Contains("ping", StringComparison.OrdinalIgnoreCase) &&
                    !pingCmd.Contains("Test-NetConnection", StringComparison.OrdinalIgnoreCase) &&
                    !pingCmd.Contains("nc ", StringComparison.OrdinalIgnoreCase) &&
                    !pingCmd.Contains("Invoke-WebRequest", StringComparison.OrdinalIgnoreCase) &&
                    !pingCmd.Contains("curl", StringComparison.OrdinalIgnoreCase))
                {
                    var baseUrl = await GetLlamaBaseUrl();
                    var uri = new Uri(baseUrl);
                    pingCmd = OperatingSystem.IsWindows()
                        ? $"powershell -Command \"Test-NetConnection {uri.Host} -Port {uri.Port} -WarningAction SilentlyContinue | Select-Object TcpTestSucceeded, SourceAddress, RemoteAddress | Format-List\""
                        : $"nc -zv -w 2 {uri.Host} {uri.Port} 2>&1";
                }
                await EmitLog(emitSse, "info", $"Ping: {pingCmd}", ct: ct);
                var cmdStep = new AgentStep
                {
                    Index = 0,
                    Type = "command",
                    Command = pingCmd,
                    Description = $"ping: {pingCmd}"
                };
                var cmdResults = await ExecuteSteps(new List<AgentStep> { cmdStep }, projectRoot, stepIndex, emitSse, ct);
                stepIndex += cmdResults.Count;
                allResults.AddRange(cmdResults);
                var firstResult = cmdResults.FirstOrDefault() as Dictionary<string, object?>;
                var output = firstResult?.TryGetValue("output", out var o) == true ? o?.ToString() ?? "" : "";
                var cmdStatus = firstResult?.TryGetValue("status", out var s) == true ? s?.ToString() : "";

                // Evaluate ping results with LLM
                try
                {
                    var evalPrompt = $@"You are a network diagnostics assistant. Analyze these ping results and provide a concise summary.

Ping command: {pingCmd}

Raw output:
```
{output}
```

Provide a brief analysis covering:
1. Is the host reachable? (packet loss)
2. What is the latency like? (min/max/avg)
3. Any notable patterns or issues?

Be concise — 2-4 sentences max.";
                    var (analysis, _, _) = await CallLlmRaw(
                        "You are a network diagnostics assistant. Respond concisely.",
                        evalPrompt, ct, TimeSpan.FromSeconds(15));
                    if (!string.IsNullOrWhiteSpace(analysis))
                    {
                        firstResult?["pingAnalysis"] = analysis.Trim();
                        await EmitLog(emitSse, "info", $"Ping analysis: {analysis.Trim()}", ct: ct);
                    }
                }
                catch { /* LLM evaluation is best-effort */ }

                var pingOk = cmdStatus == "done" && (
                    output.Contains("Reply from", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("TTL", StringComparison.Ordinal) ||
                    output.Contains("1 received", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("0% packet loss", StringComparison.OrdinalIgnoreCase));
                await EmitLog(emitSse, pingOk ? "success" : "warn",
                    pingOk ? "Host is reachable" : "Host may be unreachable — check output", ct: ct);
                continue;
            }

            else if (planFile.Equals("_package_install", StringComparison.OrdinalIgnoreCase))
            {
                var installCmd = changeDesc.Trim().Trim('`', '"', '\'');
                await EmitLog(emitSse, "info", $"Package install: {installCmd}", ct: ct);
                var cmdStep = new AgentStep
                {
                    Index = 0,
                    Type = "command",
                    Command = installCmd,
                    Description = $"install package: {installCmd}"
                };
                var cmdResults = await ExecuteSteps(new List<AgentStep> { cmdStep }, projectRoot, stepIndex, emitSse, ct);
                stepIndex += cmdResults.Count;
                allResults.AddRange(cmdResults);
                var firstResult = cmdResults.FirstOrDefault() as Dictionary<string, object?>;
                var output = firstResult?.TryGetValue("output", out var o) == true ? o?.ToString() ?? "" : "";
                var cmdStatus = firstResult?.TryGetValue("status", out var s) == true ? s?.ToString() : "";
                var installOk = cmdStatus == "done" && (
                    output.Contains("added", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("successfully installed", StringComparison.OrdinalIgnoreCase) ||
                    !output.Contains("error", StringComparison.OrdinalIgnoreCase));
                await EmitLog(emitSse, installOk ? "success" : "warn",
                    installOk ? "Package installed successfully" : "Package install may have failed — check output", ct: ct);
                continue;
            }

            // ── _command: arbitrary terminal command ──────────────────────────────
            else if (planFile.Equals("_command", StringComparison.OrdinalIgnoreCase))
            {
                var cmd = changeDesc.Trim().Trim('`', '"', '\'');
                if (string.IsNullOrWhiteSpace(cmd)) { continue; }
                await EmitLog(emitSse, "info", $"Command: {cmd}", ct: ct);
                _terminal.Start();
                var cs = new AgentStep { Index = 0, Type = "command", Command = cmd, Description = cmd };
                var cr = await ExecuteSteps(new List<AgentStep> { cs }, projectRoot, stepIndex, emitSse, ct);
                stepIndex += cr.Count; allResults.AddRange(cr);
                continue;
            }

            // ── _web_search / _web_fetch: search or fetch, then re-plan edits ─────
            else if (planFile.Equals("_web_search", StringComparison.OrdinalIgnoreCase) ||
                     planFile.Equals("_web_fetch", StringComparison.OrdinalIgnoreCase))
            {
                var isSearch = planFile.Equals("_web_search", StringComparison.OrdinalIgnoreCase);
                var query = changeDesc.Trim();
                if (string.IsNullOrWhiteSpace(query)) { continue; }
                await EmitLog(emitSse, "info", $"Web {(isSearch ? "search" : "fetch")}: {query}", ct: ct);
                var (outp, err) = isSearch ? await WebSearchAsync(query, ct) : await WebFetchAsync(query, ct);
                var wr = new Dictionary<string, object?>
                {
                    ["index"] = stepIndex++, ["type"] = planFile,
                    [isSearch ? "query" : "url"] = query,
                    ["status"] = err == null ? "done" : "error", ["output"] = outp
                };
                allResults.Add(wr);
                if (emitSse) await SendSse(Response, "step", wr, ct);
                if (!string.IsNullOrWhiteSpace(outp) && outp.Length > 80)
                    webCtx.AppendLine($"\n## Web {(isSearch ? "search" : "fetch")} [{query}]\n{outp}");

                // After consecutive web steps finish, re-plan remaining edit steps
                var nextIsWeb = itemIdx + 1 < planItems.Count &&
                    (planItems[itemIdx + 1].File.Equals("_web_search", StringComparison.OrdinalIgnoreCase) ||
                     planItems[itemIdx + 1].File.Equals("_web_fetch", StringComparison.OrdinalIgnoreCase));
                if (!nextIsWeb && webCtx.Length > 0)
                {
                    var remaining = planItems.Skip(itemIdx + 1).ToList();
                    if (remaining.Any(r => AgentUtilities.IsRelativePath(r.File ?? "") || r.File == "_create_file"))
                    {
                        var uctx = discoveryContext + "\n\n" + webCtx;
                        var rp = await ReplanRemainingSteps(prompt, remaining, uctx, emitSse, ct);
                        if (rp is { Count: > 0 })
                        {
                            planItems = planItems.Take(itemIdx + 1).Concat(rp).ToList();
                            discoveryContext = uctx;
                            if (emitSse)
                                await SendSse(Response, "plan",
                                    new { summary = "Plan updated after web results", items = planItems }, ct);
                        }
                        webCtx.Clear();
                    }
                }
                continue;
            }

            else if (planFile.Equals("_rename_file", StringComparison.OrdinalIgnoreCase) || planFile.Equals("_move_file", StringComparison.OrdinalIgnoreCase))
            {
                var dstPath = AgentUtilities.ExtractTargetPath(changeDesc, planFile, projectRoot);
                if (dstPath != null)
                {
                    var renameStep = new AgentStep
                    {
                        Index = 0,
                        Type = "rename",
                        Path = planFile,
                        ToPath = dstPath,
                        Description = $"Rename {planFile} → {dstPath}"
                    };
                    var results = await ExecuteSteps(new List<AgentStep> { renameStep }, projectRoot, stepIndex, emitSse, ct);
                    stepIndex += results.Count;
                    allResults.AddRange(results);
                    continue;
                }
            }

            else if (AgentUtilities.IsRelativePath(planFile))
            {
                // Skip edits where oldString equals newString — no actual change
                if (!string.IsNullOrWhiteSpace(item.OldString) &&
                    (item.OldString ?? "").Trim() == (item.NewString ?? "").Trim())
                {
                    await EmitLog(emitSse, "warn", $"Skipping {planFile}: oldString and newString are identical", ct: ct);
                    allResults.Add(new Dictionary<string, object?>
                    {
                        ["index"] = stepIndex,
                        ["type"] = "edit",
                        ["status"] = "skipped",
                        ["path"] = item.File.Replace('\\', '/'),
                        ["reason"] = "oldString and newString are identical"
                    });
                    stepIndex++;
                    continue;
                }

                if (await ApplyEditWithRetry(item, projectRoot, emitSse, ct))
                {
                    var editResult = new Dictionary<string, object?>
                    {
                        ["index"] = stepIndex,
                        ["type"] = "edit",
                        ["path"] = item.File.Replace('\\', '/'),
                        ["oldString"] = item.OldString,
                        ["newString"] = item.NewString
                    };
                    PopulateEditResult(editResult, "modified", item.File.Replace('\\', '/'), item.OldString, item.NewString ?? "", "");
                    if (emitSse)
                        await SendSse(Response, "step", editResult, ct);
                    stepIndex++;
                    allResults.Add(editResult);
                } 
                else
                {
                    var errorResult = new Dictionary<string, object?>
                    {
                        ["index"] = stepIndex,
                        ["type"] = "edit",
                        ["status"] = "error",
                        ["path"] = item.File.Replace('\\', '/'),
                        ["oldString"] = item.OldString,
                        ["newString"] = item.NewString
                    };
                    if (emitSse)
                        await SendSse(Response, "step", errorResult, ct);
                    stepIndex++;
                    allResults.Add(errorResult);
                }
            }

            else if (string.IsNullOrWhiteSpace(planFile))
            {
                await EmitLog(emitSse, "warn", $"Plan item with empty file field — skipping", new { item }, ct: ct);
                continue;
            }
        }
    }

    /// <summary>
    /// Apply a direct edit from a plan step that has OldString/NewString.
    /// Computes the edit in memory, verifies programmatically, then writes to disk only if valid.
    /// Returns (applied, rejectionReason, score) where score is 1-10 (10 = perfect).
    /// </summary>
    private async Task<(bool applied, string reason, int score)> ApplyEdit(PlanStep item, string projectRoot, bool emitSse, CancellationToken ct)
    {
        var relPath = item.File.Replace('\\', '/');
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!AgentUtilities.IsPathUnderRoot(fullPath, projectRoot)) return (false, "Path outside project root", 0);
        if (!System.IO.File.Exists(fullPath))
        {
            await EmitLog(emitSse, "warn", $"File not found: {relPath}", ct: ct);
            return (false, "File not found", 0);
        }

        if (string.IsNullOrWhiteSpace(item.OldString))
        {
            await EmitLog(emitSse, "warn", $"No oldString for {relPath} — skipping", ct: ct);
            return (false, "No oldString provided", 0);
        }

        if ((item.OldString ?? "").Trim() == (item.NewString ?? "").Trim())
        {
            await EmitLog(emitSse, "warn", $"oldString and newString are identical for {relPath} — skipping", ct: ct);
            return (false, "oldString and newString are identical — no change to apply", 3);
        }

        var unsafeReason = GetUnsafeEditPayloadReason(item.OldString ?? "", item.NewString ?? "");
        if (unsafeReason != null)
        {
            await EmitLog(emitSse, "warn", $"Edit rejected for {relPath}: {unsafeReason}", ct: ct);
            return (false, unsafeReason, 0);
        }

        var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8);
        var (replaced, newContent, matchError, snippet) = TryReplacePrecise(content, item.OldString!, item.NewString ?? "");
        if (!replaced)
        {
            var matchReason = matchError ?? "oldString not found in file";
            if (!string.IsNullOrWhiteSpace(snippet))
                matchReason += $". Nearby content: {snippet}";
            await EmitLog(emitSse, "warn", $"{matchReason} ({relPath})", ct: ct);
            return (false, matchReason, 1);
        }

        // Programmatic verification
        var (approved, reason, score) = VerifyEdit(item.OldString!, item.NewString ?? "", content, newContent);
        if (!approved)
        {
            await EmitLog(emitSse, "warn", $"Edit rejected (score {score}/10): {reason}", ct: ct);
            return (false, reason, score);
        }

        // Write to disk
        await System.IO.File.WriteAllTextAsync(fullPath, newContent, Encoding.UTF8);
        await EmitLog(emitSse, "success", $"✔ Edited {relPath}", ct: ct);
        return (true, "", 10);
    }

    /// <summary>
    /// Programmatic edit verification — no LLM call.
    /// Checks that oldString was replaced and newString was inserted,
    /// and does a basic structural integrity check.
    /// </summary>
    private static (bool approved, string reason, int score) VerifyEdit(
        string oldString, string newString, string oldContent, string newContent)
    {
        if (oldContent == newContent)
            return (false, "Edit produced no change — oldString and newString are identical?", 3);

        var normalizedNewString = AgentUtilities.NormalizeLineEndings(newString);
        var normalizedNewContent = AgentUtilities.NormalizeLineEndings(newContent);
        if (!string.IsNullOrEmpty(normalizedNewString) &&
            !normalizedNewContent.Contains(normalizedNewString, StringComparison.Ordinal))
            return (false, "newString not found after replacement", 4);

        return (true, "Programmatic check passed", 10);
    }

    /// <summary>
    /// Retry wrapper around ApplyEdit. Up to 3 attempts, each time showing the LLM
    /// all previous oldString/newString + score + reason so it can self-correct.
    /// </summary>
    private async Task<bool> ApplyEditWithRetry(PlanStep item, string projectRoot, bool emitSse, CancellationToken ct)
    {
        var relPath = item.File.Replace('\\', '/');
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!System.IO.File.Exists(fullPath)) return false;

        var history = new List<(string oldString, string newString, int score, string reason)>();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                var freshContent = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8);
                var corrected = await CorrectEdit("", relPath, freshContent, item.Change, history, attempt, emitSse, ct);
                if (corrected == null) return false;
                // Bail if CorrectEdit returned identical strings — LLM has nothing to fix
                if ((corrected.Value.oldString ?? "").Trim() == (corrected.Value.newString ?? "").Trim())
                {
                    await EmitLog(emitSse, "warn", $"CorrectEdit returned identical strings for {relPath} — aborting retries", ct: ct);
                    return false;
                }
                item.OldString = corrected.Value.oldString!;
                item.NewString = corrected.Value.newString!;
            }

            var (applied, reason, score) = await ApplyEdit(item, projectRoot, emitSse, ct);
            if (applied) return true;

            history.Add((item.OldString!, item.NewString ?? "", score, reason));

            if (attempt < 2)
                await EmitLog(emitSse, "warn",
                    $"Attempt {attempt + 1}/3 failed for {relPath} (score {score}/10) — retrying…", ct: ct);
            else
                await EmitLog(emitSse, "error",
                    $"All 3 attempts failed for {relPath}. Last reason: {reason}", ct: ct);
        }
        return false;
    }

    /// <summary>
    /// Ask the LLM to fix oldString/newString given the file context and ALL
    /// previous failed attempts with their scores and reasons.
    /// </summary>
    private async Task<(string oldString, string newString)?> CorrectEdit(
        string originalPrompt, string relPath, string fileContent, string changeDesc,
        List<(string oldString, string newString, int score, string reason)> history,
        int attempt, bool emitSse, CancellationToken ct)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(originalPrompt))
        {
            sb.AppendLine("## Original task");
            sb.AppendLine(originalPrompt);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(changeDesc))
        {
            sb.AppendLine("## Planned change for this file");
            sb.AppendLine(changeDesc);
            sb.AppendLine();
        }
        sb.AppendLine($"## File: {relPath}");
        sb.AppendLine();
        sb.AppendLine(BuildEditCorrectionContext(fileContent, changeDesc, history));
        sb.AppendLine();
        sb.AppendLine("### Previous failed attempts (oldest → newest):");
        for (var i = 0; i < history.Count; i++)
        {
            var h = history[i];
            sb.AppendLine($"--- Attempt {i + 1} — Score {h.score}/10 ---");
            sb.AppendLine($"Reason: {h.reason}");
            sb.AppendLine($"oldString:\n```\n{RemoveUnsafeEditMarkersForPrompt(h.oldString)}\n```");
            sb.AppendLine($"newString:\n```\n{RemoveUnsafeEditMarkersForPrompt(h.newString)}\n```");
            sb.AppendLine();
        }
        sb.AppendLine("Produce corrected oldString/newString that will pass verification.");
        sb.AppendLine("- oldString must be exact code that appears verbatim in the file");
        sb.AppendLine("- newString is the replacement (or anchor + insertion)");
        sb.AppendLine("- For insertions, oldString should be a small existing anchor and newString should contain that same anchor plus inserted code");
        sb.AppendLine("- Do not include line numbers or excerpt labels in oldString");
        sb.AppendLine("- Match indentation and spacing precisely");
        sb.AppendLine($"- Previous attempt scored {history.Last().score}/10 — improve on it");

        const string system = @"You are an edit-correction agent. Given a file and failed edit attempts with scores,
produce corrected oldString/newString that will apply correctly.

Output ONLY valid JSON:
{""oldString"": ""exact code from file to replace"", ""newString"": ""replacement code""}

Rules:
- oldString MUST exist verbatim in the file content shown above
- Both fields use escaped newlines (\\n) for multi-line values
- Preserve exact indentation and spacing. Escape newlines as \\n, but do not change indentation or spacing in oldString or newString.
- For insertions, use an existing anchor as oldString and include that same anchor in newString with the inserted code around it.
- Never return identical oldString and newString.
- Never output omitted-content placeholders such as ...(truncated), …(truncated), or â€¦(truncated).
- Learn from previous attempts — if score was low on indentation, fix that";


        var (raw, _, err) = await CallLlmRaw(system, sb.ToString(), ct, TimeSpan.FromSeconds(30));
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            var (os, ns, parseError) = AgentUtilities.ExtractEditFromCodeGen(raw);
            if (string.IsNullOrWhiteSpace(os)) return null;
            return (os, ns ?? "");
        }
        catch
        {
            await EmitLog(emitSse, "warn", $"Failed to parse correction for {relPath}", ct: ct);
            return null;
        }
    }

    private static string BuildEditCorrectionContext(
        string fileContent, string changeDesc,
        List<(string oldString, string newString, int score, string reason)> history)
    {
        var normalized = AgentUtilities.NormalizeLineEndings(fileContent);
        var sb = new StringBuilder();

        if (normalized.Length <= MaxFileContextChars)
        {
            sb.AppendLine("### Current file content");
            sb.AppendLine("```");
            sb.AppendLine(normalized);
            sb.AppendLine("```");
            return sb.ToString();
        }

        var tokens = ExtractCorrectionTokens(changeDesc, history);
        var lines = normalized.Split('\n');
        var hitLines = new SortedSet<int>();

        if (tokens.Count > 0)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (tokens.Any(t => line.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    hitLines.Add(i);
            }
        }

        var windows = new List<(int start, int end)>();
        foreach (var hit in hitLines.Take(12))
        {
            var start = Math.Max(0, hit - 30);
            var end = Math.Min(lines.Length - 1, hit + 30);
            if (windows.Count > 0 && start <= windows[^1].end + 5)
                windows[^1] = (windows[^1].start, Math.Max(windows[^1].end, end));
            else
                windows.Add((start, end));
        }

        if (windows.Count == 0)
        {
            var headLines = Math.Min(lines.Length, 180);
            var tailStart = Math.Max(headLines, lines.Length - 180);
            windows.Add((0, headLines - 1));
            if (tailStart < lines.Length)
                windows.Add((tailStart, lines.Length - 1));
        }

        sb.AppendLine("### Current file excerpts");
        sb.AppendLine("Copy only code from inside the fenced blocks. Do not copy excerpt labels.");
        var usedChars = 0;
        foreach (var window in windows)
        {
            var excerpt = string.Join('\n', lines.Skip(window.start).Take(window.end - window.start + 1));
            if (usedChars + excerpt.Length > MaxFileContextChars)
                excerpt = excerpt[..Math.Max(0, MaxFileContextChars - usedChars)];
            if (string.IsNullOrWhiteSpace(excerpt)) break;

            sb.AppendLine($"Lines {window.start + 1}-{window.end + 1}:");
            sb.AppendLine("```");
            sb.AppendLine(excerpt);
            sb.AppendLine("```");
            usedChars += excerpt.Length;
            if (usedChars >= MaxFileContextChars) break;
        }

        return sb.ToString();
    }

    private static HashSet<string> ExtractCorrectionTokens(
        string changeDesc,
        List<(string oldString, string newString, int score, string reason)> history)
    {
        var common = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "string", "public", "private", "protected", "internal", "static", "readonly",
            "return", "await", "async", "using", "namespace", "class", "void", "var",
            "new", "null", "true", "false", "this", "base", "file", "change", "oldString",
            "newString", "reason", "score"
        };

        var text = new StringBuilder(changeDesc ?? "");
        foreach (var h in history)
        {
            text.AppendLine(h.oldString);
            text.AppendLine(h.newString);
            text.AppendLine(h.reason);
        }

        return Regex.Matches(text.ToString(), @"\b[A-Za-z_][A-Za-z0-9_]{2,}\b")
            .Select(m => m.Value)
            .Where(t => !common.Contains(t))
            .OrderByDescending(t => t.Length)
            .Take(40)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// After web search/fetch, re-evaluate remaining plan steps with new context.
    /// </summary>
    private async Task<List<PlanStep>?> ReplanRemainingSteps(
        string originalPrompt, List<PlanStep> remaining,
        string updatedContext, bool emitSse, CancellationToken ct)
    {
        if (remaining.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("Revise these remaining steps given new context (web results).");
        sb.AppendLine("Original task: " + originalPrompt);
        sb.AppendLine("Remaining steps:");
        foreach (var s in remaining) sb.AppendLine($"  {s.File}: {s.Change}");
        sb.AppendLine("New context includes web results:");
        sb.AppendLine(updatedContext);

        const string sys = "Revise remaining execution steps given updated context. " +
            "Keep valid steps, update or replace steps the new context changes. " +
            "Output ONLY JSON: {\"plan\":[{\"file\":\"...\",\"change\":\"...\",\"oldString\":\"...\",\"newString\":\"...\",\"priority\":1}]}";

        var (raw, _, err) = await CallLlmRaw(sys, sb.ToString(), ct, _infiniteTimeout, maxTokens: 4096);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```"))
        {
            var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (m.Success) cleaned = m.Groups[1].Value.Trim();
        }
        var parsed = ParsePlan(cleaned);
        return parsed?.Plan?.Count > 0 ? parsed.Plan : null;
    }
 
    [HttpPost("execute")]
    public async Task<IActionResult> Execute([FromBody] AgentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt))
            return BadRequest("Prompt is required");

        var projectRoot = GetProjectRoot(req.Project);

        var (allSteps, plan, complete) = await Orchestrate(req.Prompt, projectRoot, emitSse: false);

        return Ok(new
        {
            summary = plan?.Summary ?? "",
            thinking = plan?.Thinking ?? "",
            complete,
            steps = allSteps,
            filesEdited = ExtractFilesEdited(allSteps)
        });
    }

    [HttpPost("apply")]
    public async Task<IActionResult> ApplyEdits([FromBody] ApplyEditsRequest req)
    {
        if (req.Edits == null || req.Edits.Count == 0)
            return BadRequest(new { error = "No edits provided" });

        var projectRoot = GetProjectRoot(req.Project);
        var editResults = await ApplyEditsDirect(req.Edits, projectRoot);
        var commandResults = new List<object>();

        if (req.Commands != null && req.Commands.Count > 0)
        {
            _terminal.Start();
            foreach (var cmd in req.Commands)
            {
                try
                {
                    await _terminal.SendCommandAsync(cmd.Command, projectRoot);
                    await Task.Delay(800);
                    commandResults.Add(new { command = cmd.Command, status = "done", output = _terminal.ReadLastLines(50) });
                }
                catch (Exception ex)
                {
                    commandResults.Add(new { command = cmd.Command, status = "error", error = ex.Message });
                }
            }
        }

        return Ok(new { edits = editResults, commands = commandResults });
    }

    [HttpPost("execute-stream")]
    public async Task ExecuteStream([FromBody] AgentRequest req)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        if (string.IsNullOrWhiteSpace(req.Prompt))
        {
            await SendSse(Response, "error", new { message = "Prompt is required" });
            await SendSse(Response, "done", new { });
            return;
        }

        try
        {
            var projectRoot = GetProjectRoot(req.Project);
            await SendSse(Response, "phase", new { phase = "start", projectRoot });
            await EmitLog(true, "info", "Agent run started",
                new { projectRoot, task = req.Prompt });

            List<object> allSteps;
            AgentPlan? plan;
            bool complete;
            // ── Full phased pipeline ────────────────────────────────────
            (allSteps, plan, complete) =
                await Orchestrate(req.Prompt, projectRoot, emitSse: true, ct: Response.HttpContext.RequestAborted,
                    attachedFiles: req.Files?.Count > 0 ? req.Files : null,
                    steeringContext: req.SteeringContext);

            var filesEdited = ExtractFilesEdited(allSteps);
            var editsApplied = AgentUtilities.HasSuccessfulEdits(allSteps);

            await SendSse(Response, "done", new
            {
                summary = plan?.Summary ?? "",
                thinking = plan?.Thinking ?? "",
                complete,
                editsApplied,
                incomplete = AgentUtilities.TaskExpectsFileChanges(req.Prompt) && !complete,
                warning = !complete && AgentUtilities.TaskExpectsFileChanges(req.Prompt)
                                 ? (editsApplied ? "Task may be incomplete. Please review."
                                                 : "No files were modified. Check failed steps below.")
                                 : (string?)null,
                steps = allSteps,
                filesEdited
            });
        }
        catch (Exception ex)
        {
            await SendSse(Response, "error", new { message = ex.Message });
            await SendSse(Response, "done", new { incomplete = true, summary = ex.Message });
        }
    }

    [HttpGet("questions/pending")]
    public IActionResult GetPendingQuestions()
    {
        var list = _pendingQuestions.Values
            .OrderBy(q => q.CreatedUtc)
            .Select(q => new
            {
                q.Id,
                q.Question,
                q.Fields,
                q.CreatedUtc
            })
            .ToList();
        return Ok(new { questions = list });
    }

    [HttpPost("questions/answer")]
    public async Task<IActionResult> AnswerQuestion([FromBody] QuestionAnswerRequest req)
    {
        if (!_pendingQuestions.TryRemove(req.Id, out var pending))
            return NotFound("Question not found or already answered");

        pending.Answer.TrySetResult(req.Answers);
        return Ok(new { status = "answered" });
    }

    [HttpPost("context-review/confirm")]
    public IActionResult ConfirmContextReview([FromBody] ContextReviewAnswer req)
    {
        if (!_pendingContextReviews.TryRemove(req.Id, out var pending))
            return NotFound("Context review not found or already answered");
        pending.Answer.TrySetResult(req.Files ?? pending.Files);
        return Ok(new { status = "confirmed" });
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  LLM CALL HELPERS
    // ═════════════════════════════════════════════════════════════════════════

    private async Task<bool> CheckLlmConnectivity(string projectRoot, bool emitSse, CancellationToken ct)
    {
        if (_nextConnectivityCheck != DateTime.MinValue)
        {
            DateTime now = DateTime.UtcNow;
            if (now - _nextConnectivityCheck < TimeSpan.FromMinutes(5))
            {
                await EmitLog(emitSse, "info", "Skipping connectivity check (last check was less than 5 minutes ago)", ct: ct);
                return _lastConnectionCheckResult;
            }
        }

        var baseUrl = await GetLlamaBaseUrl();
        _lastConnectionCheckResult = await CheckForConnectivity(projectRoot, emitSse, baseUrl, ct);
        _nextConnectivityCheck = DateTime.UtcNow.AddMinutes(5);

        return _lastConnectionCheckResult;
    }

    private async Task<bool> CheckForConnectivity(string projectRoot, bool emitSse, string baseUrl, CancellationToken ct)
    {
        var uri = new Uri(baseUrl);
        var host = uri.Host;
        var port = uri.Port;

        await EmitLog(emitSse, "info", $"Connectivity check: {host}:{port}", ct: ct);

        // Build platform-appropriate commands for each strategy
        var strategies = new List<(string name, string cmd)>();

        // 1. TCP port check via Test-NetConnection (Windows) or nc (Linux)
        var tcpCmd = OperatingSystem.IsWindows()
            ? $"powershell -Command \"Test-NetConnection {host} -Port {port} -WarningAction SilentlyContinue | Select-Object TcpTestSucceeded, SourceAddress, RemoteAddress | Format-List\""
            : $"nc -zv -w 2 {host} {port} 2>&1";
        strategies.Add(("tcp", tcpCmd));

        // 2. ICMP ping
        var pingCmd = OperatingSystem.IsWindows()
            ? $"ping {host} -n 1 -w 2000"
            : $"ping -c 1 -W 2 {host}";
        strategies.Add(("ping", pingCmd));

        // 3. HTTP GET via PowerShell or curl
        var httpCmd = OperatingSystem.IsWindows()
            ? $"powershell -Command \"try {{ $r = Invoke-WebRequest -Uri '{baseUrl}/api/tags' -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop; Write-Output ('HTTP ' + $r.StatusCode + ' OK') }} catch {{ Write-Output ('FAILED: ' + $_.Exception.Message) }}\""
            : $"curl -s -o /dev/null -w 'HTTP %{{http_code}}' --connect-timeout 5 {baseUrl}/api/tags";
        strategies.Add(("http", httpCmd));

        foreach (var (name, cmd) in strategies)
        {
            ct.ThrowIfCancellationRequested();
            await EmitLog(emitSse, "info", $"Trying {name}: {cmd}", ct: ct);

            var step = new AgentStep
            {
                Index = 0,
                Type = "command",
                Command = cmd,
                Description = $"{name}: {host}:{port}"
            };

            var results = await ExecuteSteps(new List<AgentStep> { step }, projectRoot, 0, emitSse, ct);
            var first = results.FirstOrDefault() as Dictionary<string, object?>;
            var output = first?.TryGetValue("output", out var o) == true ? o?.ToString() ?? "" : "";
            var status = first?.TryGetValue("status", out var s) == true ? s?.ToString() : "";

            var succeeded = status == "done" && (
                output.Contains("TcpTestSucceeded : True", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("succeeded", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("Reply from", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("TTL", StringComparison.Ordinal) ||
                output.Contains("1 packets received", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("1 received", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("HTTP 200", StringComparison.Ordinal) ||
                output.Contains("HTTP 20", StringComparison.Ordinal));

            if (succeeded)
            {
                await EmitLog(emitSse, "info", $"Host {host}:{port} is reachable via {name}", ct: ct);
                return true;
            }
        }

        await EmitLog(emitSse, "error", $"Host {host}:{port} is unreachable — aborting", ct: ct);
        return false;
    }

    private async Task<string> GetLlamaBaseUrl()
    {
        var cfg = await _configFile.LoadConfigAsync();
        return (cfg.llamaUrl ?? "http://localhost:8080").TrimEnd('/');
    }

    /// <summary>
    /// Low-level LLM call — returns raw content string, no parsing.
    /// </summary>
    private async Task<(string raw, AgentResponse? response, string? error)> CallLlmRaw(
        string systemPrompt, string userMessage, CancellationToken ct = default,
        TimeSpan? requestTimeout = null, int? maxTokens = null)
    {
        var baseUrl = await GetLlamaBaseUrl();
        var model = _config.GetValue<string>("Ai:Model") ?? "medgemma:4b";
        var client = _clientFactory.CreateClient("llama");
        client.Timeout = _infiniteTimeout;

        var messages = new object[]
        {
            new { role = "system",  content = systemPrompt },
            new { role = "user",    content = userMessage  }
        };

        var timeout = requestTimeout ?? TimeSpan.FromMinutes(30);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        return await CallLlmNonStreaming(client, baseUrl + "/v1/chat/completions", model, messages, linkedCts.Token, maxTokens);
    }
 
    private async Task<(string raw, AgentResponse? parsed, string? error)> CallLlmNonStreaming(
        HttpClient client, string target, string model, object messages, CancellationToken ct = default,
        int? maxTokens = null)
    {
        var mt = maxTokens ?? 2048;
        var requestBody = new { model, messages, stream = false, temperature = 0.05, max_tokens = mt };
        var contentJson = JsonSerializer.Serialize(requestBody);
        var httpContent = new StringContent(contentJson, Encoding.UTF8, "application/json");

        try
        {
            var resp = await client.PostAsync(target, httpContent, ct);
            var respText = await resp.Content.ReadAsStringAsync(ct);

            var llmContent = ExtractLlmContent(respText);
            if (string.IsNullOrWhiteSpace(llmContent))
                return (respText, null, "Empty LLM response");

            var parsed = ParseAgentResponse(llmContent);
            return (llmContent, parsed, parsed == null ? "JSON parse failed" : null);
        }
        catch (TaskCanceledException)
        {
            return ("", null, "LLM request timed out");
        }
        catch (Exception ex)
        {
            return ("", null, ex.Message);
        }
    }

    private static string ExtractLlmContent(string respText)
    {
        try
        {
            using var doc = JsonDocument.Parse(respText);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var c))
                    return c.GetString() ?? "";
            }
        }
        catch { }
        return "";
    }

    private static List<object> ExtractFilesEdited(List<object> steps)
    {
        // Primary: Dictionary-based extraction
        var result = steps.OfType<Dictionary<string, object?>>()
            .Where(s =>
                s.TryGetValue("type", out var t) &&
                (t?.ToString() == "edit" || t?.ToString() == "rename") &&
                s.TryGetValue("status", out var st) && st?.ToString() == "done")
            .Select(s => (object)new
            {
                path = s.GetValueOrDefault("path"),
                action = s.GetValueOrDefault("editAction"),
                toPath = s.GetValueOrDefault("toPath"),
                linesAdded = s.GetValueOrDefault("linesAdded"),
                linesRemoved = s.GetValueOrDefault("linesRemoved"),
                preview = s.GetValueOrDefault("diffPreview")
            })
            .ToList();

        if (result.Count > 0) return result;

        // Fallback: serialize non-Dictionary steps to JSON and re-parse
        foreach (var step in steps)
        {
            if (step is Dictionary<string, object?>) continue;
            try
            {
                var json = JsonSerializer.Serialize(step);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : "";
                var status = root.TryGetProperty("status", out var st) ? st.GetString() : "";
                if ((type == "edit" || type == "rename") && status == "done")
                {
                    result.Add(new
                    {
                        path = root.TryGetProperty("path", out var p) ? p.GetString() : null,
                        action = root.TryGetProperty("editAction", out var a) ? a.GetString() : null,
                        toPath = root.TryGetProperty("toPath", out var tp) ? tp.GetString() : null,
                        linesAdded = root.TryGetProperty("linesAdded", out var la) ? la.GetInt32() : 0,
                        linesRemoved = root.TryGetProperty("linesRemoved", out var lr) ? lr.GetInt32() : 0,
                        preview = root.TryGetProperty("diffPreview", out var dp) ? dp.GetString() : null
                    });
                }
            }
            catch { }
        }

        return result;
    }
    private async Task<List<object>> ExecuteSteps(
           List<AgentStep> steps, string projectRoot, int indexOffset, bool emitSse, CancellationToken ct = default)
    {
        var results = new List<object>();
        var terminalStarted = false;
        var editContentCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in steps)
        {
            var displayIndex = indexOffset + step.Index;
            var result = new Dictionary<string, object?>
            {
                ["index"] = displayIndex,
                ["type"] = step.Type,
                ["description"] = step.Description,
                ["status"] = "running"
            };

            if (emitSse)
            {
                var stepLabel = step.Description ?? step.Path ?? step.Command ?? step.Query ?? step.Pattern ?? "";
                await EmitLog(emitSse, "step",
                    $"▶ {step.Type}: {stepLabel}", ct: ct);
                await SendSse(Response, "step", result, ct);
            }

            try
            {
                switch (step.Type?.ToLowerInvariant())
                {
                    case "edit":
                        // Pass edit cache so consecutive edits to the same file
                        // use the in-memory content after the previous edit(s)
                        await ExecuteEditStep(step, projectRoot, result, editContentCache);
                        break;
                    case "command":
                        if (!terminalStarted) { _terminal.Start(); terminalStarted = true; }
                        await ExecuteCommandStep(step, projectRoot, result);
                        break;
                    case "rename":
                        await ExecuteRenameStep(step, projectRoot, result);
                        break;
                    case "read":
                        await ExecuteReadStep(step, projectRoot, result);
                        break;
                    case "list":
                        await ExecuteListStep(step, projectRoot, result);
                        break;
                    case "glob":
                        await ExecuteGlobStep(step, projectRoot, result);
                        break;
                    case "grep":
                        await ExecuteGrepStep(step, projectRoot, result);
                        break;
                    case "web":
                    case "web_search":
                    case "web_fetch":
                        await ExecuteWebStep(step, result);
                        break;
                    default:
                        result["status"] = "error";
                        result["error"] = $"Unknown step type: {step.Type}";
                        break;
                }
            }
            catch (Exception ex)
            {
                result["status"] = "error";
                result["error"] = ex.Message;
            }

            result["status"] = AgentUtilities.NormalizeUiStatus(result["status"]?.ToString());
            results.Add(result);

            if (emitSse)
            {
                var st = result["status"]?.ToString() ?? "?";
                var outputRaw = result.GetValueOrDefault("output")?.ToString();
                var outputPreview = outputRaw != null && outputRaw.Length > 200 ? outputRaw[..200] + "…" : outputRaw;
                await EmitLog(emitSse, st == "error" ? "error" : "info",
                    $"✓ {step.Type} finished ({st})",
                    new
                    {
                        path = result.GetValueOrDefault("path"),
                        query = result.GetValueOrDefault("query"),
                        error = result.GetValueOrDefault("error"),
                        output = outputPreview,
                        oldStringPreview = result.GetValueOrDefault("oldStringPreview"),
                        snippet = result.GetValueOrDefault("snippet"),
                        suggestions = result.GetValueOrDefault("suggestions")
                    }, ct: ct);
                await SendSse(Response, "step", result, ct);
            }
        }

        return results;
    }

    /// <summary>
    /// Runs discovery steps in parallel. Emits all "running" events first,
    /// executes I/O concurrently, then emits "done" events in original order.
    /// Only handles list/grep/read types used by bootstrap discovery.
    /// </summary>
    private async Task<List<object>> ExecuteDiscoveryStepsConcurrent(
        List<AgentStep> steps, string projectRoot, int indexOffset, bool emitSse)
    {
        var count = steps.Count;
        var results = new Dictionary<string, object?>[count];
        var sync = new object();

        // Phase 1: emit all "running" events synchronously
        for (var i = 0; i < count; i++)
        {
            var step = steps[i];
            var displayIndex = indexOffset + step.Index;
            var result = new Dictionary<string, object?>
            {
                ["index"] = displayIndex,
                ["type"] = step.Type,
                ["description"] = step.Description,
                ["status"] = "running"
            };
            results[i] = result;

            if (emitSse)
            {
                await EmitLog(emitSse, "step",
                    $"▶ {step.Type}: {step.Description ?? step.Path ?? step.Command ?? step.Query ?? ""}");
                await SendSse(Response, "step", result);
            }
        }

        // Phase 2: execute all I/O in parallel
        var tasks = steps.Select((step, i) => Task.Run(async () =>
        {
            var result = results[i];
            try
            {
                switch (step.Type?.ToLowerInvariant())
                {
                    case "list":
                        await ExecuteListStep(step, projectRoot, result);
                        break;
                    case "grep":
                        await ExecuteGrepStep(step, projectRoot, result);
                        break;
                    case "read":
                        await ExecuteReadStep(step, projectRoot, result);
                        break;
                    default:
                        result["status"] = "error";
                        result["error"] = $"Unknown step type: {step.Type}";
                        break;
                }
            }
            catch (Exception ex)
            {
                result["status"] = "error";
                result["error"] = ex.Message;
            }

            result["status"] = AgentUtilities.NormalizeUiStatus(result["status"]?.ToString());
        }));

        await Task.WhenAll(tasks);

        // Phase 3: emit all "done" events in original order
        for (var i = 0; i < count; i++)
        {
            var result = results[i];
            if (emitSse)
            {
                var st = result["status"]?.ToString() ?? "?";
                await EmitLog(emitSse, st == "error" ? "error" : "info",
                    $"✓ {steps[i].Type} finished ({st})",
                    new
                    {
                        path = result.GetValueOrDefault("path"),
                        error = result.GetValueOrDefault("error"),
                        oldStringPreview = result.GetValueOrDefault("oldStringPreview"),
                        snippet = result.GetValueOrDefault("snippet"),
                        suggestions = result.GetValueOrDefault("suggestions")
                    });
                await SendSse(Response, "step", result);
            }
        }

        return results.Cast<object>().ToList();
    }


    private async Task ExecuteEditStep(AgentStep step, string projectRoot, Dictionary<string, object?> result, Dictionary<string, string>? contentCache = null)
    {
        var rawPath = (step.Path ?? "").Replace('/', Path.DirectorySeparatorChar);
        // Absolute path (contains drive letter or starts with /) or relative to project
        var isAbsolute = rawPath.Contains(":\\") || rawPath.StartsWith('/') || rawPath.StartsWith('\\');
        var targetPath = isAbsolute
            ? Path.GetFullPath(rawPath)
            : Path.GetFullPath(Path.Combine(projectRoot, rawPath));
        if (!isAbsolute && !AgentUtilities.IsPathUnderRoot(targetPath, projectRoot))
        { result["status"] = "error"; result["error"] = "Path outside project root"; return; }

        result["path"] = step.Path;
        var oldString = step.OldString ?? "";
        var newString = step.NewString ?? "";

        var unsafeReason = GetUnsafeEditPayloadReason(oldString, newString);
        if (unsafeReason != null)
        {
            result["status"] = "error";
            result["error"] = unsafeReason;
            return;
        }

        // Use in-memory cache for consecutive edits to the same file
        // so each subsequent edit sees the content after prior edits
        string content;
        if (contentCache != null && contentCache.TryGetValue(targetPath, out var cached))
        {
            content = cached;
        }
        else
        {
            if (!System.IO.File.Exists(targetPath))
            {
                if (string.IsNullOrEmpty(oldString) && !string.IsNullOrEmpty(newString))
                {
                    var dir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    await System.IO.File.WriteAllTextAsync(targetPath, newString, Encoding.UTF8);
                    result["oldStartLine"] = 0;
                    PopulateEditResult(result, "created", step.Path!, null, newString, newString);
                    if (contentCache != null) contentCache[targetPath] = newString;
                    return;
                }
                var suggestions = AgentUtilities.FindSimilarFiles(step.Path ?? "", projectRoot);
                result["status"] = "error";
                result["error"] = $"File does not exist: {step.Path}. Use a path from discovery.";
                result["suggestions"] = suggestions;
                return;
            }
            content = await System.IO.File.ReadAllTextAsync(targetPath, Encoding.UTF8);
        }

        if (string.IsNullOrEmpty(oldString))
        {
            var startLine = content.Length > 0
                ? content.Count(c => c == '\n') + (content.EndsWith("\n") ? 0 : 1)
                : 0;
            result["oldStartLine"] = startLine;
            content += newString;
            await System.IO.File.WriteAllTextAsync(targetPath, content, Encoding.UTF8);
            if (contentCache != null) contentCache[targetPath] = content;
            PopulateEditResult(result, "modified", step.Path!, null, newString, newString);
            return;
        }

        var (replaced, newContent, matchError, snippet) = TryReplacePrecise(content, oldString, newString);
        if (!replaced)
        {
            // Enhanced error reporting with context
            result["status"] = "error";
            result["error"] = matchError ?? "oldString not found";
            if (snippet != null) result["snippet"] = snippet;
            result["oldStringPreview"] = oldString;

            // Add helpful context about file content for debugging
            var lines = content.Split('\n');
            result["fileLineCount"] = lines.Length;
            result["fileCharCount"] = content.Length;



            // If the file is small, show more context
            if (content.Length < 1000)
            {
                result["fileContentPreview"] = content;
            }

            return;
        }

        if (AgentUtilities.NormalizeLineEndings(newContent) == AgentUtilities.NormalizeLineEndings(content))
        { result["status"] = "skipped"; result["path"] = step.Path; return; }

        var normOldContent = AgentUtilities.NormalizeLineEndings(content);
        var normNewContent = AgentUtilities.NormalizeLineEndings(newContent);
        var minLen = Math.Min(normOldContent.Length, normNewContent.Length);
        var diffIdx = 0;
        while (diffIdx < minLen && normOldContent[diffIdx] == normNewContent[diffIdx])
            diffIdx++;
        result["oldStartLine"] = normOldContent[..diffIdx].Count(c => c == '\n');

        await System.IO.File.WriteAllTextAsync(targetPath, newContent, Encoding.UTF8);
        if (contentCache != null) contentCache[targetPath] = newContent;
        PopulateEditResult(result, "modified", step.Path!, oldString, newString, newContent);
    }
    
    private static List<string> GetPlanSizeViolations(AgentPlan plan)
    {
        var violations = new List<string>();
        for (var i = 0; i < plan.Plan.Count; i++)
        {
            var step = plan.Plan[i];
            if (!AgentUtilities.IsRelativePath(step.File ?? "")) continue;

            var old = step.OldString ?? "";
            var lines = old.Split('\n').Length;
            var chars = old.Length;

            if (lines > 5 || chars > 250)
                violations.Add(
                    $"Step {i + 1} ({step.File}): oldString is {lines} lines / {chars} chars — max is 5 lines / 250 chars. " +
                    "Split into multiple steps, each targeting only the lines that actually change.");

            // Detect bloated identity anchor: large block where only a tiny region differs
            if (chars > 120)
            {
                var newStr = step.NewString ?? "";
                var minLen = Math.Min(old.Length, newStr.Length);
                var prefix = 0;
                while (prefix < minLen && old[prefix] == newStr[prefix]) prefix++;
                var suffix = 0;
                while (suffix < minLen - prefix
                       && old[old.Length - 1 - suffix] == newStr[newStr.Length - 1 - suffix]) suffix++;
                var changedChars = chars - prefix - suffix;
                if (changedChars > 0 && changedChars < chars * 0.25)
                    violations.Add(
                        $"Step {i + 1} ({step.File}): only ~{changedChars} chars actually differ " +
                        $"but oldString wraps {chars} chars of surrounding context. " +
                        "Shrink oldString to just the changed region.");
            }
        }
        return violations;
    }

    /// <summary>
    /// Before triggering a replan, verify the task isn't already done.
    /// Reads the current state of modified files and asks the LLM for a binary verdict.
    /// This is the primary guard against hallucinated replan spirals.
    /// </summary>
    private async Task<(bool isComplete, string reason)> AssessCompletion(
        string prompt, List<object> executedSteps, string projectRoot, CancellationToken ct)
    {
        var editSteps = executedSteps
            .OfType<Dictionary<string, object?>>()
            .Where(s => s.TryGetValue("type", out var t) && t?.ToString() == "edit")
            .ToList();

        if (editSteps.Count == 0)
            return (true, "No edit steps — command-only task");

        var failed = editSteps
            .Where(s => !s.TryGetValue("status", out var st) || st?.ToString() is not ("done" or "skipped"))
            .ToList();

        // All steps succeeded — no need for an LLM call
        if (failed.Count == 0)
            return (true, $"All {editSteps.Count} edit step(s) succeeded or were intentionally skipped");

        // Build context: task + step results + current file content for up to 2 modified files
        var sb = new StringBuilder();
        sb.AppendLine("## Task");
        sb.AppendLine(prompt);
        sb.AppendLine();
        sb.AppendLine("## Edit step results");
        foreach (var s in editSteps.Take(12))
        {
            var path = s.GetValueOrDefault("path")?.ToString() ?? "?";
            var status = s.TryGetValue("status", out var st) ? st?.ToString() : "?";
            var error = s.TryGetValue("error", out var e) ? e?.ToString() : null;
            sb.AppendLine($"- {path}: {status}{(error != null ? $" → {error}" : "")}");
        }
        sb.AppendLine();

        var modifiedPaths = editSteps
            .Where(s => s.TryGetValue("status", out var st) && st?.ToString() == "done")
            .Select(s => s.GetValueOrDefault("path")?.ToString())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();

        foreach (var relPath in modifiedPaths)
        {
            var fullPath = Path.GetFullPath(
                Path.Combine(projectRoot, relPath!.Replace('/', Path.DirectorySeparatorChar)));
            if (!System.IO.File.Exists(fullPath)) continue;

            var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
            var preview = content.Length > 700 ? content[..700] + "\n…(truncated for preview)" : content;
            sb.AppendLine($"## Current content of {relPath}");
            sb.AppendLine("```");
            sb.AppendLine(preview);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("Is the task complete? A task is complete when the files now contain the");
        sb.AppendLine("required changes, even if some edit steps were skipped because the code");
        sb.AppendLine("was already in the correct state.");

        const string sys =
            "You are a task completion verifier. Output ONLY valid JSON with no markdown: " +
            "{\"complete\": true|false, \"reason\": \"one sentence\"}";

        var (raw, _, _) = await CallLlmRaw(sys, sb.ToString(), ct, TimeSpan.FromSeconds(20));
        if (string.IsNullOrWhiteSpace(raw))
            return (false, "Assessment timed out — assuming incomplete");

        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (m.Success) cleaned = m.Groups[1].Value.Trim();
            }
            var s2 = cleaned.IndexOf('{');
            var e2 = cleaned.LastIndexOf('}');
            if (s2 >= 0 && e2 > s2) cleaned = cleaned[s2..(e2 + 1)];

            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            var isComplete = root.TryGetProperty("complete", out var c) && c.ValueKind == JsonValueKind.True;
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            return (isComplete, reason);
        }
        catch
        {
            return (false, "Could not parse completion assessment");
        }
    }
    /// <summary>
    /// Called when a _checkpoint step is encountered mid-plan.
    /// Re-reads all files modified so far, builds an enriched discovery context,
    /// then runs a full planning cycle for the remaining steps.
    /// Returns a fresh list of plan steps, or null if no replan was possible.
    /// </summary>
    private async Task<List<PlanStep>?> CheckpointReplan(
        string originalPrompt,
        string currentDiscoveryContext,
        List<PlanStep> remainingSteps,
        List<object> completedResults,
        string projectRoot,
        bool emitSse,
        CancellationToken ct,
        string? steeringContext = null)
    {
        // Collect paths of every file successfully modified in the completed phase
        var modifiedPaths = completedResults
            .OfType<Dictionary<string, object?>>()
            .Where(r =>
                r.TryGetValue("type", out var t) && t?.ToString() is "edit" or "create" &&
                r.TryGetValue("status", out var s) && s?.ToString() == "done")
            .Select(r => r.GetValueOrDefault("path")?.ToString())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await EmitLog(emitSse, "info",
            $"Checkpoint: refreshing context from {modifiedPaths.Count} modified file(s)…", ct: ct);

        // Build enriched context: original discovery + current file states
        var enriched = new StringBuilder(currentDiscoveryContext);
        enriched.AppendLine();
        enriched.AppendLine("## CHECKPOINT — current file states after completed phase");
        enriched.AppendLine("These versions supersede any earlier content shown above.");

        foreach (var relPath in modifiedPaths)
        {
            var fullPath = Path.GetFullPath(
                Path.Combine(projectRoot, relPath!.Replace('/', Path.DirectorySeparatorChar)));
            if (!System.IO.File.Exists(fullPath)) continue;

            var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
            enriched.AppendLine($"\n### {relPath} (post-phase)\n```\n{content}\n```");
        }

        if (remainingSteps.Count == 0) return null;

        // Describe remaining intended work so the LLM can revise or confirm it
        var remainingDesc = new StringBuilder();
        remainingDesc.AppendLine("Intended remaining work (revise based on current file states above):");
        foreach (var step in remainingSteps)
            remainingDesc.AppendLine($"- {step.File}: {step.Change}");

        var replanPrompt =
            $"## Original task\n{originalPrompt}\n\n" +
            remainingDesc +
            (string.IsNullOrWhiteSpace(steeringContext) ? "" : $"\n## Steering\n{steeringContext}\n");

        var newPlan = await AnalyzePromptAndPlanCodeChanges(
            replanPrompt, enriched.ToString(), projectRoot, emitSse, ct, steeringContext);

        return newPlan?.Plan;
    }

    private async Task ExecuteRenameStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var srcRel = (step.Path ?? "").Replace('\\', '/');
        var dstRel = (step.ToPath ?? "").Replace('\\', '/');
        var srcPath = Path.GetFullPath(Path.Combine(projectRoot, srcRel.Replace('/', Path.DirectorySeparatorChar)));
        var dstPath = Path.GetFullPath(Path.Combine(projectRoot, dstRel.Replace('/', Path.DirectorySeparatorChar)));

        result["path"] = srcRel;
        result["toPath"] = dstRel;

        if (!AgentUtilities.IsPathUnderRoot(srcPath, projectRoot) || !AgentUtilities.IsPathUnderRoot(dstPath, projectRoot))
        { result["status"] = "error"; result["error"] = "Path outside project root"; return; }

        if (!System.IO.File.Exists(srcPath))
        { result["status"] = "error"; result["error"] = $"Source file not found: {srcRel}"; return; }

        if (System.IO.File.Exists(dstPath))
        { result["status"] = "error"; result["error"] = $"Destination already exists: {dstRel}"; return; }

        try
        {
            var dir = Path.GetDirectoryName(dstPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            System.IO.File.Move(srcPath, dstPath);
            result["status"] = "done";
            result["editAction"] = "renamed";
        }
        catch (Exception ex)
        {
            result["status"] = "error";
            result["error"] = ex.Message;
        }
    }

    private static void PopulateEditResult(
        Dictionary<string, object?> result, string action, string path,
        string? oldStr, string? newStr, string writtenContent)
    {
        result["status"] = "done";
        result["editAction"] = action;
        result["path"] = path;
        result["linesRemoved"] = (oldStr ?? "").Split('\n').Length;
        result["linesAdded"] = (newStr ?? "").Split('\n').Length;
        if (!string.IsNullOrEmpty(oldStr)) result["oldStringPreview"] = oldStr;
        if (!string.IsNullOrEmpty(newStr)) result["newStringPreview"] = newStr;
        result["diffPreview"] = AgentUtilities.BuildDiffPreview(oldStr, newStr);
        result["oldLines"] = (oldStr ?? "").Split('\n');
        result["newLines"] = (newStr ?? "").Split('\n');
    }


    /// <summary>
    /// Multi-pass string replacement with progressively looser matching.
    /// Pass 1 – exact (after CRLF normalisation).
    /// Pass 2 – per-line leading/trailing whitespace trim, ordinal.
    /// Pass 3 – per-line trim, case-insensitive.
    /// Pass 4 – skip blank lines while matching non-blank pattern lines.
    /// Pass 5 – single-line collapsed-whitespace match.
    /// Pass 6 – token-overlap similarity (≥85% of meaningful tokens match).
    /// </summary>
    private static (bool ok, string content, string? error, string? snippet) TryReplacePrecise(
        string content, string oldString, string newString)
    {
        content = AgentUtilities.NormalizeLineEndings(content);
        oldString = AgentUtilities.NormalizeLineEndings(oldString);
        newString = AgentUtilities.NormalizeLineEndings(newString);

        if (string.IsNullOrEmpty(oldString))
            return (false, content, "oldString is empty", null);

        var firstIdx = content.IndexOf(oldString, StringComparison.Ordinal);
        if (firstIdx < 0)
            return (false, content, "oldString not found exactly in file", BuildExactMatchHint(content, oldString));

        var secondIdx = content.IndexOf(oldString, firstIdx + oldString.Length, StringComparison.Ordinal);
        if (secondIdx >= 0)
            return (false, content, "oldString is ambiguous; it appears more than once. Use a longer unique anchor.", null);

        return (true, content[..firstIdx] + newString + content[(firstIdx + oldString.Length)..], null, null);
    }

    private static string? BuildExactMatchHint(string content, string oldString)
    {
        var patternLines = oldString.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .OrderByDescending(l => l.Length)
            .Take(3)
            .ToList();

        if (patternLines.Count == 0) return null;

        var fileLines = content.Split('\n');
        var hints = fileLines
            .Where(l => patternLines.Any(p => l.Contains(p, StringComparison.Ordinal)))
            .Take(3)
            .ToList();

        return hints.Count > 0 ? string.Join("\n", hints) : null;
    }

    private static string? GetUnsafeEditPayloadReason(string oldString, string newString)
    {
        foreach (var marker in UnsafeEditMarkers)
        {
            if (oldString.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                newString.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return $"Edit contains generated placeholder marker '{marker}'. Ask for the full exact code instead of applying omitted content.";
        }

        return null;
    }

    private static string RemoveUnsafeEditMarkersForPrompt(string value)
    {
        var sanitized = value;
        foreach (var marker in UnsafeEditMarkers)
            sanitized = sanitized.Replace(marker, "[placeholder marker removed]", StringComparison.OrdinalIgnoreCase);
        return sanitized;
    }

    private static (bool ok, string content, string? error, string? snippet) TryReplace(
        string content, string oldString, string newString)
    {
        content = AgentUtilities.NormalizeLineEndings(content);
        oldString = AgentUtilities.NormalizeLineEndings(oldString);

        // Pass 1: exact
        var idx = content.IndexOf(oldString, StringComparison.Ordinal);
        if (idx >= 0)
            return (true, content[..idx] + newString + content[(idx + oldString.Length)..], null, null);

        var fileLines = content.Split('\n');
        var rawOldLines = oldString.Split('\n');

        // Pass 1.5: whitespace-normalised (tabs→spaces, collapse runs) per each line
        if (rawOldLines.Length > 0)
        {
            var wsFileLines = fileLines.Select(l => string.Join(" ", l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).ToArray();
            var wsOldLines = rawOldLines.Select(l => string.Join(" ", l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).ToArray();
            for (var fi = 0; fi <= fileLines.Length - rawOldLines.Length; fi++)
            {
                var match = true;
                for (var li = 0; li < rawOldLines.Length; li++)
                {
                    if (!string.Equals(wsFileLines[fi + li], wsOldLines[li], StringComparison.OrdinalIgnoreCase))
                    { match = false; break; }
                }
                if (match)
                    return (true, ReplaceLineBlock(fileLines, fi, rawOldLines.Length, newString), null, null);
            }
        }

        // Strip leading/trailing blank lines from the pattern to get the "core".
        var coreFirst = 0;
        var coreLast = rawOldLines.Length - 1;
        while (coreFirst <= coreLast && string.IsNullOrWhiteSpace(rawOldLines[coreFirst])) coreFirst++;
        while (coreLast >= coreFirst && string.IsNullOrWhiteSpace(rawOldLines[coreLast])) coreLast--;
        var coreOld = (coreFirst <= coreLast) ? rawOldLines[coreFirst..(coreLast + 1)] : rawOldLines;

        // Pass 2: per-line trim, ordinal
        {
            var hit = FindTrimmedBlock(fileLines, coreOld, StringComparison.Ordinal);
            if (hit >= 0) return (true, ReplaceLineBlock(fileLines, hit, coreOld.Length, newString), null, null);
        }

        // Pass 3: per-line trim, case-insensitive
        {
            var hit = FindTrimmedBlock(fileLines, coreOld, StringComparison.OrdinalIgnoreCase);
            if (hit >= 0) return (true, ReplaceLineBlock(fileLines, hit, coreOld.Length, newString), null, null);
        }

        // Pass 4: skip blank lines
        {
            var nonBlankOld = coreOld.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            if (nonBlankOld.Length > 0)
                for (var fi = 0; fi <= fileLines.Length - nonBlankOld.Length; fi++)
                {
                    int matched = 0, scan = fi, firstHit = fi, lastHit = fi;
                    while (matched < nonBlankOld.Length && scan < fileLines.Length)
                    {
                        if (string.IsNullOrWhiteSpace(fileLines[scan])) { scan++; continue; }
                        if (!string.Equals(fileLines[scan].Trim(), nonBlankOld[matched].Trim(),
                                StringComparison.Ordinal)) break;
                        if (matched == 0) firstHit = scan;
                        lastHit = scan; matched++; scan++;
                    }
                    if (matched == nonBlankOld.Length)
                        return (true, ReplaceLineBlock(fileLines, firstHit, lastHit - firstHit + 1, newString), null, null);
                }
        }

        // Pass 5: collapsed whitespace single-line
        {
            static string Collapse(string s) => Regex.Replace(s.Trim(), @"\s+", " ");
            var collapsedOld = Collapse(oldString);
            if (!collapsedOld.Contains('\n'))
                for (var fi = 0; fi < fileLines.Length; fi++)
                    if (string.Equals(Collapse(fileLines[fi]), collapsedOld, StringComparison.OrdinalIgnoreCase))
                        return (true, ReplaceLineBlock(fileLines, fi, 1, newString), null, null);
        }

        // Pass 6: token-overlap similarity (new — catches minor whitespace/wording diffs)
        // Only triggers when the pattern has ≥5 meaningful tokens, to avoid false positives.
        {
            var patternTokens = new HashSet<string>(
                Regex.Matches(oldString, @"\b[a-zA-Z_$][\w$]*\b").Select(m => m.Value),
                StringComparer.OrdinalIgnoreCase);

            if (patternTokens.Count >= 5)
            {
                // Window size: number of non-blank pattern lines + small slack.
                var nonBlankOld = coreOld.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
                var windowLen = Math.Max(nonBlankOld.Length, 1);
                const double threshold = 0.65;

                for (var fi = 0; fi <= fileLines.Length - windowLen; fi++)
                {
                    // Gather the window's non-blank lines (up to windowLen of them).
                    var windowLines = new List<string>();
                    int lastWinIdx = fi;
                    for (int si = fi; si < fileLines.Length && windowLines.Count < windowLen; si++)
                    {
                        if (!string.IsNullOrWhiteSpace(fileLines[si]))
                        {
                            windowLines.Add(fileLines[si]);
                            lastWinIdx = si;
                        }
                    }
                    if (windowLines.Count < windowLen) break;

                    var windowTokens = new HashSet<string>(
                        Regex.Matches(string.Join("\n", windowLines), @"\b[a-zA-Z_$][\w$]*\b")
                             .Select(m => m.Value),
                        StringComparer.OrdinalIgnoreCase);

                    // Require high bidirectional overlap to avoid matching unrelated code.
                    double overlap = patternTokens.Count(t => windowTokens.Contains(t));
                    if (overlap / patternTokens.Count >= threshold &&
                        overlap / Math.Max(windowTokens.Count, 1) >= threshold * 0.7)
                    {
                        return (true,
                            ReplaceLineBlock(fileLines, fi, lastWinIdx - fi + 1, newString),
                            null, null);
                    }
                }
            }
        }

        // Build a hint showing lines that share at least one token with the pattern.
        var firstNonBlankPattern = coreOld
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";
        var hint = firstNonBlankPattern.Length > 0
            ? string.Join("\n", fileLines
                .Where(l => l.Contains(firstNonBlankPattern, StringComparison.OrdinalIgnoreCase))
                .Take(3))
            : null;

        return (false, content, "oldString not found in file",
            !string.IsNullOrEmpty(hint) ? hint : null);
    }

    private static int FindTrimmedBlock(string[] fileLines, string[] pattern, StringComparison cmp)
    {
        if (pattern.Length == 0 || fileLines.Length < pattern.Length) return -1;
        for (var i = 0; i <= fileLines.Length - pattern.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < pattern.Length; j++)
                if (!string.Equals(fileLines[i + j].Trim(), pattern[j].Trim(), cmp)) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private static string ReplaceLineBlock(string[] fileLines, int start, int count, string replacement)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < start; i++) { sb.Append(fileLines[i]); sb.Append('\n'); }
        sb.Append(replacement);
        for (var i = start + count; i < fileLines.Length; i++) { sb.Append('\n'); sb.Append(fileLines[i]); }
        return sb.ToString();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  INDIVIDUAL STEP EXECUTORS
    // ═════════════════════════════════════════════════════════════════════════

    private async Task ExecuteCommandStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var command = step.Command ?? "";
        if (string.IsNullOrWhiteSpace(command))
        { result["status"] = "error"; result["error"] = "No command provided"; return; }
        var beforeLen = _terminal.ReadAll().Length;
        await _terminal.SendCommandAsync(command, projectRoot);
        // Adaptive wait: poll terminal output every 500ms.
        // Stops when output is stable for 3s (network commands take time to produce output).
        // Max 40 iterations = 20s total wait.
        var prevLen = beforeLen;
        var stableMs = 0;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(500);
            var curLen = _terminal.ReadAll().Length;
            if (curLen == prevLen)
            {
                stableMs += 500;
                if (stableMs >= 3000) break;
            }
            else { stableMs = 0; prevLen = curLen; }
        }
        result["status"] = "done";
        result["command"] = command;
        var fullOutput = _terminal.ReadAll();
        result["output"] = beforeLen >= 0 && beforeLen < fullOutput.Length
            ? fullOutput.Substring(beforeLen)
            : "";
        result["snippet"] = (result["output"] as string) ?? "";
    }

    /// <summary>
    /// Web search via DuckDuckGo Instant Answer API.
    /// </summary>
    private async Task<(string output, string? error)> WebSearchAsync(string query, CancellationToken ct)
    {
        try
        {
            var client = _clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(1);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            var apiUrl = "https://api.duckduckgo.com/?q=" + Uri.EscapeDataString(query) + "&format=json&no_html=1&skip_disambig=1";
            var resp = await client.GetAsync(apiUrl, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var sb = new StringBuilder();

            if (root.TryGetProperty("AbstractText", out var abs) && abs.ValueKind == JsonValueKind.String)
            {
                var txt = abs.GetString();
                if (!string.IsNullOrWhiteSpace(txt))
                {
                    sb.AppendLine("## Summary");
                    sb.AppendLine(txt);
                    if (root.TryGetProperty("AbstractURL", out var url) && url.ValueKind == JsonValueKind.String)
                        sb.AppendLine($"Source: {url.GetString()}");
                    sb.AppendLine();
                }
            }
            if (root.TryGetProperty("Answer", out var ans) && ans.ValueKind == JsonValueKind.String)
            {
                var a = ans.GetString();
                if (!string.IsNullOrWhiteSpace(a)) sb.AppendLine($"Answer: {a}");
            }
            if (root.TryGetProperty("RelatedTopics", out var topics) && topics.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine("## Results");
                var count = 0;
                foreach (var topic in topics.EnumerateArray())
                {
                    if (count >= 10) break;
                    if (topic.TryGetProperty("Text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        var t = text.GetString();
                        var u = topic.TryGetProperty("FirstURL", out var fu) && fu.ValueKind == JsonValueKind.String ? fu.GetString() : "";
                        sb.AppendLine($"  - {t}{(string.IsNullOrWhiteSpace(u) ? "" : $" ({u})")}");
                        count++;
                    }
                    if (topic.TryGetProperty("Topics", out var subs) && subs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var sub in subs.EnumerateArray())
                        {
                            if (count >= 10) break;
                            if (sub.TryGetProperty("Text", out var st) && st.ValueKind == JsonValueKind.String)
                            {
                                var su = sub.TryGetProperty("FirstURL", out var suu) && suu.ValueKind == JsonValueKind.String ? suu.GetString() : "";
                                sb.AppendLine($"  - {st.GetString()}{(string.IsNullOrWhiteSpace(su) ? "" : $" ({su})")}");
                                count++;
                            }
                        }
                    }
                }
            }

            return (sb.Length > 0 ? sb.ToString() : "(no results found)", null);
        }
        catch (Exception ex) { return ("", ex.Message); }
    }

    /// <summary>
    /// Fetch a URL and return its readable text content.
    /// </summary>
    private async Task<(string output, string? error)> WebFetchAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = _clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            var resp = await client.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "text/plain";
            if (contentType.Contains("html"))
                body = Regex.Replace(body, "<[^>]+>", " ");
            return ($"HTTP {(int)resp.StatusCode} ({contentType})\n{body.Trim()}", null);
        }
        catch (Exception ex) { return ("", ex.Message); }
    } 

    private async Task ExecuteReadStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var relPath = (step.Path ?? "").Replace('/', Path.DirectorySeparatorChar);
        var targetPath = Path.GetFullPath(Path.Combine(projectRoot, relPath));
        if (!AgentUtilities.IsPathUnderRoot(targetPath, projectRoot))
        { result["status"] = "error"; result["error"] = "Path outside project root"; return; }
        if (!System.IO.File.Exists(targetPath))
        { result["status"] = "error"; result["error"] = "File not found"; return; }

        result["path"] = step.Path;
 
        var content = await System.IO.File.ReadAllTextAsync(targetPath, Encoding.UTF8);
        result["output"] = content;
       
        result["status"] = "done";
    }

    private Task ExecuteListStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var relPath = string.IsNullOrWhiteSpace(step.Path) ? "" : step.Path.Replace('/', Path.DirectorySeparatorChar);
        var targetPath = Path.GetFullPath(Path.Combine(projectRoot, relPath));
        if (!AgentUtilities.IsPathUnderRoot(targetPath, projectRoot))
        { result["status"] = "error"; result["error"] = "Path outside project root"; return Task.CompletedTask; }
        if (!Directory.Exists(targetPath))
        { result["status"] = "error"; result["error"] = "Directory not found"; return Task.CompletedTask; }

        var entries = Directory.GetFileSystemEntries(targetPath)
            .Select(e => (Directory.Exists(e) ? "[dir]  " : "[file] ") + Path.GetFileName(e))
            .OrderBy(x => x).Take(200);

        result["status"] = "done";
        result["path"] = step.Path ?? ".";
        result["output"] = string.Join("\n", entries);
        return Task.CompletedTask;
    }

    private Task ExecuteGlobStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var pattern = (step.Pattern ?? step.Path ?? "*").Replace('\\', '/');
        result["path"] = step.Pattern ?? step.Path ?? "*";
        try
        {
            IEnumerable<string> files;
            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                var parts = pattern.Split('/');
                var filePattern = parts[^1];
                var dirParts = parts.Length > 1 ? parts[..^1] : Array.Empty<string>();

                // Normalize "**" segments: treat them as recursive wildcards
                var hasRecursive = dirParts.Any(p => p == "**");
                var dirPartsClean = dirParts.Where(p => p != "**").ToList();

                if (dirPartsClean.Count == 0 || hasRecursive)
                {
                    // Search from project root recursively
                    var searchPattern = filePattern == "**" ? "*" : filePattern;
                    files = Directory.EnumerateFiles(projectRoot, searchPattern, SearchOption.AllDirectories);
                }
                else
                {
                    var dirPart = string.Join(Path.DirectorySeparatorChar, dirPartsClean);
                    var searchRoot = Path.GetFullPath(Path.Combine(projectRoot, dirPart));
                    if (!AgentUtilities.IsPathUnderRoot(searchRoot, projectRoot))
                        throw new InvalidOperationException("Pattern outside project root");
                    files = Directory.EnumerateFiles(searchRoot, filePattern, SearchOption.AllDirectories);
                }
            }
            else
            {
                var single = Path.GetFullPath(Path.Combine(projectRoot, pattern));
                files = System.IO.File.Exists(single) ? new[] { single } : Array.Empty<string>();
            }

            var list = files.Where(f => AgentUtilities.IsPathUnderRoot(f, projectRoot)).Take(100)
                .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/')).ToList();

            result["status"] = "done";
            result["output"] = list.Count == 0 ? "(no matches)" : string.Join("\n", list);
        }
        catch (Exception ex) { result["status"] = "error"; result["error"] = ex.Message; }
        return Task.CompletedTask;
    }

 
    private Task ExecuteGrepStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var query = step.Query ?? step.Pattern ?? "";
        result["path"] = step.Path ?? "";
        result["query"] = query;
        if (string.IsNullOrWhiteSpace(query))
        { result["status"] = "error"; result["error"] = "grep requires query"; return Task.CompletedTask; }

        var searchRoot = projectRoot;
        if (!string.IsNullOrWhiteSpace(step.Path))
        {
            searchRoot = Path.GetFullPath(Path.Combine(projectRoot, step.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!AgentUtilities.IsPathUnderRoot(searchRoot, projectRoot))
            { result["status"] = "error"; result["error"] = "Path outside project root"; return Task.CompletedTask; }
        }

        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "node_modules", ".git", "bin", "obj", "dist", ".angular" };
        var grepResult = new GrepResult { Query = query, Path = step.Path };

        try
        {
            foreach (var file in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories))
            {
                if (!AgentUtilities.IsPathUnderRoot(file, projectRoot)) continue;
                if (skipDirs.Any(d => file.Contains(Path.DirectorySeparatorChar + d + Path.DirectorySeparatorChar))) continue;
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > 500_000) continue;
                    var lines = System.IO.File.ReadAllLines(file);
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (!lines[i].Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                        grepResult.Matches.Add(new GrepMatch
                        {
                            FilePath = Path.GetRelativePath(projectRoot, file).Replace('\\', '/'),
                            LineNumber = i + 1,
                            Content = lines[i].Trim()
                        });
                        if (grepResult.Matches.Count >= 50) break;
                    }
                }
                catch { }
                if (grepResult.Matches.Count >= 50) break;
            }
            grepResult.Status = "done";
            result["status"] = "done";
            result["output"] = grepResult.Matches.Count == 0 ? "(no matches)" : string.Join("\n", grepResult.Matches.Select(m => $"{m.FilePath}:{m.LineNumber}: {m.Content}"));
            result["grepResult"] = grepResult;
        }
        catch (Exception ex)
        {
            grepResult.Status = "error";
            grepResult.Error = ex.Message;
            result["status"] = "error";
            result["error"] = ex.Message;
            result["grepResult"] = grepResult;
        }
        return Task.CompletedTask;
    }

    private async Task ExecuteWebStep(AgentStep step, Dictionary<string, object?> result)
    {
        var isFetch = step.Type is "web_fetch";
        var target = step.Url ?? step.Path ?? "";
        var query = step.Query ?? "";

        try
        {
            var client = _clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            if (isFetch || (!string.IsNullOrWhiteSpace(target) && Uri.TryCreate(target, UriKind.Absolute, out _)))
            {
                // Direct URL fetch
                var url = !string.IsNullOrWhiteSpace(target) && Uri.TryCreate(target, UriKind.Absolute, out var parsedUri)
                    ? parsedUri
                    : new Uri(target);
                var resp = await client.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();
                var contentType = resp.Content.Headers.ContentType?.MediaType ?? "text/plain";
                // Strip HTML tags for readability
                if (contentType.Contains("html"))
                    body = Regex.Replace(body, "<[^>]+>", " ");
                result["status"] = "done";
                result["url"] = url.ToString();
                result["output"] = $"HTTP {(int)resp.StatusCode} ({contentType})\n{body.Trim()}";
            }
            else
            {
                // Web search via DuckDuckGo Instant Answer JSON API
                var search = !string.IsNullOrWhiteSpace(query) ? query : target;
                if (string.IsNullOrWhiteSpace(search))
                { result["status"] = "error"; result["error"] = "web_search requires a query"; return; }

                var apiUrl = "https://api.duckduckgo.com/?q=" + Uri.EscapeDataString(search) + "&format=json&no_html=1&skip_disambig=1";
                var resp = await client.GetAsync(apiUrl);
                var json = await resp.Content.ReadAsStringAsync();

                // Parse the JSON response to extract meaningful text
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var output = new StringBuilder();

                    // Abstract (from DuckDuckGo's instant answer)
                    if (root.TryGetProperty("AbstractText", out var abstractText) && abstractText.ValueKind == JsonValueKind.String)
                    {
                        var txt = abstractText.GetString();
                        if (!string.IsNullOrWhiteSpace(txt))
                        {
                            output.AppendLine("## Summary");
                            output.AppendLine(txt);
                            if (root.TryGetProperty("AbstractURL", out var abstractUrl) && abstractUrl.ValueKind == JsonValueKind.String)
                                output.AppendLine($"Source: {abstractUrl.GetString()}");
                            output.AppendLine();
                        }
                    }

                    // Answer (direct answers like definitions, conversions)
                    if (root.TryGetProperty("Answer", out var answer) && answer.ValueKind == JsonValueKind.String)
                    {
                        var ans = answer.GetString();
                        if (!string.IsNullOrWhiteSpace(ans))
                            output.AppendLine($"Answer: {ans}");
                    }

                    // Definition
                    if (root.TryGetProperty("Definition", out var def) && def.ValueKind == JsonValueKind.String)
                    {
                        var defText = def.GetString();
                        if (!string.IsNullOrWhiteSpace(defText))
                        {
                            output.AppendLine("## Definition");
                            output.AppendLine(defText);
                            if (root.TryGetProperty("DefinitionURL", out var defUrl) && defUrl.ValueKind == JsonValueKind.String)
                                output.AppendLine($"Source: {defUrl.GetString()}");
                            output.AppendLine();
                        }
                    }

                    // Related topics (search results)
                    if (root.TryGetProperty("RelatedTopics", out var topics) && topics.ValueKind == JsonValueKind.Array)
                    {
                        output.AppendLine("## Results");
                        var count = 0;
                        foreach (var topic in topics.EnumerateArray())
                        {
                            if (count >= 10) break;
                            if (topic.TryGetProperty("Text", out var text) && text.ValueKind == JsonValueKind.String)
                            {
                                var topicText = text.GetString();
                                var topicUrl = "";
                                if (topic.TryGetProperty("FirstURL", out var url) && url.ValueKind == JsonValueKind.String)
                                    topicUrl = url.GetString();
                                output.AppendLine($"  - {topicText}{(string.IsNullOrWhiteSpace(topicUrl) ? "" : $" ({topicUrl})")}");
                                count++;
                            }
                            // Handle nested topics (some DDG results have sub-topics)
                            if (topic.TryGetProperty("Topics", out var subTopics) && subTopics.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var sub in subTopics.EnumerateArray())
                                {
                                    if (count >= 10) break;
                                    if (sub.TryGetProperty("Text", out var subText) && subText.ValueKind == JsonValueKind.String)
                                    {
                                        var st = subText.GetString();
                                        var su = "";
                                        if (sub.TryGetProperty("FirstURL", out var subUrl) && subUrl.ValueKind == JsonValueKind.String)
                                            su = subUrl.GetString();
                                        output.AppendLine($"  - {st}{(string.IsNullOrWhiteSpace(su) ? "" : $" ({su})")}");
                                        count++;
                                    }
                                }
                            }
                        }
                    }

                    if (output.Length == 0)
                        output.Append("(no results found)");

                    result["status"] = "done";
                    result["query"] = search;
                    result["output"] = output.ToString();
                }
                catch (JsonException)
                {
                    // Fallback: return raw API response
                    result["status"] = "done";
                    result["query"] = search;
                    result["output"] = $"(raw API response)\n{json}";
                }
            }
        }
        catch (Exception ex) { result["status"] = "error"; result["error"] = ex.Message; }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  APPLY EDITS DIRECTLY  (for the /apply endpoint)
    // ═════════════════════════════════════════════════════════════════════════

    private async Task<List<EditResult>> ApplyEditsDirect(List<EditAction> edits, string projectRoot)
    {
        var results = new List<EditResult>();
        var fileGroups = new Dictionary<string, List<EditAction>>(StringComparer.OrdinalIgnoreCase);
        var fileOrder = new List<string>();

        foreach (var edit in edits)
        {
            if (!fileGroups.ContainsKey(edit.Path)) { fileGroups[edit.Path] = new(); fileOrder.Add(edit.Path); }
            fileGroups[edit.Path].Add(edit);
        }

        foreach (var filePath in fileOrder)
        {
            var fileEdits = fileGroups[filePath];
            var targetPath = Path.GetFullPath(Path.Combine(projectRoot, filePath));
            if (!AgentUtilities.IsPathUnderRoot(targetPath, projectRoot))
            { foreach (var _ in fileEdits) results.Add(new EditResult { Path = filePath, Status = "skipped", Error = "Path outside project root" }); continue; }

            string content = "";
            var fileExists = System.IO.File.Exists(targetPath);
            if (fileExists)
                content = await System.IO.File.ReadAllTextAsync(targetPath, Encoding.UTF8);
            else if (fileEdits.Any(e => !string.IsNullOrEmpty(e.OldString)))
            { foreach (var e in fileEdits) results.Add(new EditResult { Path = filePath, Status = "skipped", Error = "File does not exist" }); continue; }

            var hasError = false;
            foreach (var edit in fileEdits)
            {
                var unsafeReason = GetUnsafeEditPayloadReason(edit.OldString, edit.NewString ?? "");
                if (unsafeReason != null)
                {
                    results.Add(new EditResult { Path = filePath, Status = "error", Error = unsafeReason });
                    hasError = true;
                    break;
                }
                if (!fileExists && string.IsNullOrEmpty(edit.OldString)) { content = edit.NewString ?? ""; continue; }
                if (string.IsNullOrEmpty(edit.OldString)) { content += edit.NewString ?? ""; continue; }
                var (ok, newContent, err, _) = TryReplacePrecise(content, edit.OldString, edit.NewString ?? "");
                if (!ok) { results.Add(new EditResult { Path = filePath, Status = "error", Error = err }); hasError = true; break; }
                content = newContent;
            }

            if (!hasError)
            {
                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                await System.IO.File.WriteAllTextAsync(targetPath, content, Encoding.UTF8);
                results.Add(new EditResult { Path = filePath, Status = "written" });
            }
        }
        return results;
    } 

    // ═════════════════════════════════════════════════════════════════════════
    //  JSON PARSING  (unchanged from original)
    // ═════════════════════════════════════════════════════════════════════════

    private static AgentResponse? ParseAgentResponse(string raw)
    {
        var jsonStr = raw.Trim();
        if (jsonStr.StartsWith("```"))
        {
            var m = Regex.Match(jsonStr, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (m.Success) jsonStr = m.Groups[1].Value.Trim();
        }
        var start = jsonStr.IndexOf('{');
        var end = jsonStr.LastIndexOf('}');
        if (start >= 0 && end > start) jsonStr = jsonStr.Substring(start, end - start + 1);  

        try
        {
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var steps = JsonSerializer.Deserialize<List<AgentStep>>(jsonStr,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (steps != null && steps.Count > 0)
                    return new AgentResponse { Steps = steps, Summary = "Parsed steps array" };
            }
            if (root.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
            {
                var steps = JsonSerializer.Deserialize<List<AgentStep>>(stepsEl.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (steps != null && steps.Count > 0)
                {
                    var thinking = root.TryGetProperty("thinking", out var th) ? th.GetString() ?? "" : "";
                    var summary = root.TryGetProperty("summary", out var sm) ? sm.GetString() ?? "" : "";
                    var complete = root.TryGetProperty("complete", out var cp) && cp.ValueKind == JsonValueKind.True;
                    return new AgentResponse { Thinking = thinking, Summary = summary, Complete = complete, Steps = steps };
                }
            }
        }
        catch { }

        return null;
    } 

    /// <summary>
    /// Low-level LLM call returning raw text. Unlike CallLlmRaw, this does NOT try to parse
    /// the response as AgentResponse JSON, so it works for any text or custom JSON output.
    /// Uses a generous max_tokens for code generation.
    /// </summary>
    private async Task<(string raw, string? error)> CallLlmRawText(
        string systemPrompt, string userMessage, CancellationToken ct = default,
        TimeSpan? requestTimeout = null, int? maxTokens = null)
    {
        try
        {
            var baseUrl = await GetLlamaBaseUrl();
            var model = _config.GetValue<string>("Ai:Model") ?? "medgemma:4b";
            var client = _clientFactory.CreateClient("llama");
            client.Timeout = _infiniteTimeout;

            var messages = new object[]
            {
                new { role = "system",  content = systemPrompt },
                new { role = "user",    content = userMessage  }
            };

            var timeout = requestTimeout ?? TimeSpan.FromMinutes(30);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var reqBody = new
            {
                model,
                messages,
                stream = false,
                temperature = 0.0,
                max_tokens = maxTokens ?? MaxFileContextChars / 2
            };
            var httpContent = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");

            var resp = await client.PostAsync(baseUrl + "/v1/chat/completions", httpContent, linkedCts.Token);
            var respText = await resp.Content.ReadAsStringAsync(linkedCts.Token);
            var raw = ExtractLlmContent(respText);

            if (string.IsNullOrWhiteSpace(raw))
                return (respText, "Empty LLM response");

            // Strip markdown fences if present
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("```"))
            {
                var m = Regex.Match(trimmed, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (m.Success) trimmed = m.Groups[1].Value.Trim();
            }

            return (trimmed, null);
        }
        catch (TaskCanceledException)
        {
            return ("", "LLM request timed out");
        }
        catch (Exception ex)
        {
            return ("", ex.Message);
        }
    }

     

    private async Task RunSmartBuildCheck(string projectRoot, string buildCmd, bool emitSse, CancellationToken ct)
    {
        const string systemPrompt = @"You are a smart build environment checker. Your job is to determine if the build environment is healthy and fix any issues.

Run the build command, analyze the output, and decide what to do next.

Output ONLY valid JSON (no markdown fences, no other text):
{
  ""decision"": ""done"" | ""command"" | ""ask_user"",
  ""summary"": ""brief explanation of what was found"",
  ""command"": ""the command to run next (only if decision=command)"",
  ""userQuestion"": ""question for the user (only if decision=ask_user)""
}

Rules:
- done: Build succeeded and environment is clean for editing
- command: Build failed but can be fixed by running another command (e.g., kill process on port, dotnet restore, npm install, clean build artifacts). Provide the exact command to run.
- ask_user: Need user input (e.g., port conflict — ask to kill, unclear error — ask for guidance)
- If build output shows a port conflict, suggest killing the process
- If packages are missing, suggest restore/install commands
- If the build error is pre-existing/unrelated, set decision to ""done"" and note it in summary";

        _terminal.Start();
        await EmitLog(emitSse, "info", $"Build check: {buildCmd}", ct: ct);

        var allOutput = new StringBuilder();
        var iteration = 0;
        const int maxIterations = 5;

        while (iteration < maxIterations)
        {
            iteration++;
            var beforeLen = _terminal.ReadAll().Length;
            await _terminal.SendCommandAsync(buildCmd, projectRoot);

            // Wait for output stability
            var prevLen = beforeLen;
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(500);
                var curLen = _terminal.ReadAll().Length;
                if (curLen == prevLen) break;
                prevLen = curLen;
            }

            var output = _terminal.ReadAll();
            var freshOutput = beforeLen < output.Length
                ? output.Substring(beforeLen)
                : output;

            allOutput.AppendLine($"--- Build iteration {iteration} ---");
            allOutput.AppendLine(freshOutput);

            var userPrompt = $@"## Build Command
{buildCmd}

## Build Output
```
{freshOutput}
```

## Iteration
{iteration} of {maxIterations}

Analyze the build output. Is the environment healthy? What should we do next?";

            var (raw, err) = await CallLlmRawText(systemPrompt, userPrompt, ct);
            if (string.IsNullOrWhiteSpace(raw))
            {
                await EmitLog(emitSse, "warn",
                    $"Build check LLM call failed: {err ?? "empty"}", ct: ct);
                break;
            }

            // Parse LLM decision
            var decision = ParseBuildCheckResponse(raw);
            if (decision == null)
            {
                await EmitLog(emitSse, "warn",
                    $"Could not parse build check response — proceeding", ct: ct);
                break;
            }

            switch (decision.Decision)
            {
                case "done":
                    await EmitLog(emitSse, "success",
                        $"Build check complete: {decision.Summary}", ct: ct);
                    return;

                case "command":
                    if (!string.IsNullOrWhiteSpace(decision.Command))
                    {
                        await EmitLog(emitSse, "info",
                            $"Build fix: {decision.Command} — {decision.Summary}", ct: ct);
                        await _terminal.SendCommandAsync(decision.Command, projectRoot);

                        // Wait for command output
                        var cmdPrevLen = _terminal.ReadAll().Length;
                        for (var i = 0; i < 30; i++)
                        {
                            await Task.Delay(500);
                            if (_terminal.ReadAll().Length == cmdPrevLen) break;
                            cmdPrevLen = _terminal.ReadAll().Length;
                        }
                    }
                    // Loop back to run build again
                    continue;

                case "ask_user":
                    await EmitLog(emitSse, "info",
                        $"Build check needs user input: {decision.Summary}", ct: ct);
                    if (!string.IsNullOrWhiteSpace(decision.UserQuestion))
                    {
                        await SendSse(Response, "log", new
                        {
                            ts = DateTime.UtcNow.ToString("o"),
                            level = "info",
                            message = $"Build question: {decision.UserQuestion}",
                            detail = new { buildOutput = allOutput.ToString() }
                        }, ct);
                    }
                    // Exit the build check loop — don't block the pipeline
                    return;

                default:
                    await EmitLog(emitSse, "warn",
                        $"Unknown build check decision '{decision.Decision}' — proceeding", ct: ct);
                    return;
            }
        }

        await EmitLog(emitSse, "warn",
            $"Build check reached max iterations ({maxIterations}) — results may be inconclusive", ct: ct);
    }


    private static BuildCheckDecision? ParseBuildCheckResponse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Strip markdown fences
        var json = raw.Trim();
        if (json.StartsWith("```"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(json,
                @"```(?:json)?\s*([\s\S]*?)```", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) json = m.Groups[1].Value.Trim();
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<BuildCheckDecision>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            // Try repair
            var repaired = AgentUtilities.RepairJsonString(json);
            if (repaired != null)
            {
                try
                {
                    return System.Text.Json.JsonSerializer.Deserialize<BuildCheckDecision>(repaired,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch { }
            }
            return null;
        }
    } 
}
