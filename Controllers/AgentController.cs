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
    private const int ContentTokenBudget = 2800;
    private const int CompactThreshold75 = 2100;
    private const int CompactThreshold90 = 2520;
    private bool _lastConnectionCheckResult = true;
    private static DateTime _nextConnectivityCheck = DateTime.MinValue;
    private static TimeSpan _infiniteTimeout = Timeout.InfiniteTimeSpan;
    private static readonly ConcurrentDictionary<string, PendingQuestion> _pendingQuestions = new();
    private static readonly ConcurrentDictionary<string, PendingContextReview> _pendingContextReviews = new();

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

    /// <summary>
    /// After the LLM produces a plan, scans each planned file for imports,
    /// service calls, and type references to discover cross-file dependencies.
    /// Reads discovered files and asks the LLM whether the plan is complete
    /// or needs additional files. Time-boxed to prevent runaway exploration.
    /// </summary>
    private async Task<(AgentPlan plan, string expandedContext)> ExpandDiscoveryFromPlan(
        AgentPlan plan, string prompt, string projectRoot,
        string discoveryContext, bool emitSse, CancellationToken ct)
    {
        var planFiles = plan.Plan
            .Select(p => p.File)
            .Where(f => !string.IsNullOrWhiteSpace(f) && !AgentUtilities.IsSpecialMarker(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (planFiles.Count == 0)
            return (plan, discoveryContext);

        await EmitLog(emitSse, "info",
            $"Phase 2.5 — CROSS-FILE DISCOVERY: scanning {planFiles.Count} planned file(s) for references", ct: ct);

        // ── Step 1: read all planned files, extract imports / service refs ──
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "node_modules", ".git", "bin", "obj", "dist", ".angular" };

        foreach (var relFile in planFiles)
        {
            ct.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relFile.Replace('/', Path.DirectorySeparatorChar)));
            if (!System.IO.File.Exists(fullPath)) continue;

            try
            {
                var content = await System.IO.File.ReadAllTextAsync(fullPath, ct);

                // C# using statements
                foreach (Match m in Regex.Matches(content, @"^using\s+([\w.]+)", RegexOptions.Multiline))
                    references.Add(m.Groups[1].Value.Split('.').Last());

                // JS/TS imports
                foreach (Match m in Regex.Matches(content, @"(?:from|require)\s*['""]([^'""]+)['""]"))
                {
                    var last = m.Groups[1].Value.Split('/').Last().Split('.').First();
                    if (!string.IsNullOrWhiteSpace(last)) references.Add(last);
                }

                // Service/Provider/Repository/Helper/Manager/Factory method calls
                foreach (Match m in Regex.Matches(content,
                    @"(?:this\.|private\s+\w+\s+)?(\w+(?:Service|Repository|Manager|Provider|Factory|Helper|Controller|Handler|Store|Api|Client))\s*\.\s*\w+\s*\("))
                    references.Add(m.Groups[1].Value);
            }
            catch { }
        }

        if (references.Count == 0)
        {
            await EmitLog(emitSse, "info", "Phase 2.5 — no cross-file references found", ct: ct);
            return (plan, discoveryContext);
        }

        // ── Step 2: grep for each reference (limited to 8) ─────────────────
        var candidateFiles = new List<string>();
        foreach (var refName in references.Take(8))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var file in Directory.EnumerateFiles(projectRoot, "*", SearchOption.AllDirectories))
                {
                    if (!AgentUtilities.IsPathUnderRoot(file, projectRoot)) continue;
                    if (skipDirs.Any(d => file.Contains(Path.DirectorySeparatorChar + d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    try
                    {
                        var fi = new System.IO.FileInfo(file);
                        if (fi.Length > 200_000) continue;
                        var lines = System.IO.File.ReadAllLines(file);
                        var found = lines.Any(l => l.Contains(refName, StringComparison.OrdinalIgnoreCase) &&
                            (l.TrimStart().StartsWith("class ") || l.TrimStart().StartsWith("interface ") ||
                             l.TrimStart().StartsWith("function ") || l.TrimStart().StartsWith("export ") ||
                             l.Contains(" enum ") || l.Contains(" record ") || l.Contains(" struct ")));
                        if (found)
                        {
                            var rel = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
                            if (!planFiles.Contains(rel, StringComparer.OrdinalIgnoreCase) &&
                                !candidateFiles.Contains(rel, StringComparer.OrdinalIgnoreCase))
                                candidateFiles.Add(rel);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Remove duplicates and cap
        candidateFiles = candidateFiles.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        if (candidateFiles.Count == 0)
        {
            await EmitLog(emitSse, "info", "Phase 2.5 — no new files discovered", ct: ct);
            return (plan, discoveryContext);
        }

        await EmitLog(emitSse, "info",
            $"Phase 2.5 — discovered {candidateFiles.Count} cross-file reference(s): {string.Join(", ", candidateFiles)}", ct: ct);

        // ── Step 3: read discovered files and build expanded context ──────
        var expandSb = new StringBuilder();
        expandSb.AppendLine("## Cross-file references discovered from plan");
        expandSb.AppendLine();
        foreach (var cf in candidateFiles)
        {
            ct.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, cf.Replace('/', Path.DirectorySeparatorChar)));
            if (!System.IO.File.Exists(fullPath)) continue;
            try
            {
                var content = await System.IO.File.ReadAllTextAsync(fullPath, ct);
                expandSb.AppendLine($"### {cf}");
                expandSb.AppendLine(content);
                expandSb.AppendLine();
            }
            catch { }
        }

        var expandedContext = discoveryContext + "\n\n" + expandSb.ToString();

        // ── Step 4: ask LLM if plan needs updating ─────────────────────────
        var completenessPrompt = new StringBuilder();
        completenessPrompt.AppendLine("You are validating a code-change plan for completeness.");
        completenessPrompt.AppendLine("Below is the original task, the existing plan, and newly discovered cross-file references that may be related.");
        completenessPrompt.AppendLine();
        completenessPrompt.AppendLine("## Original Task");
        completenessPrompt.AppendLine(prompt);
        completenessPrompt.AppendLine();
        completenessPrompt.AppendLine("## Current Plan");
        foreach (var p in plan.Plan)
            completenessPrompt.AppendLine($"  - {p.File}: {p.Change}");
        completenessPrompt.AppendLine();
        completenessPrompt.AppendLine("## Newly Discovered Files (may or may not be relevant)");
        completenessPrompt.AppendLine(string.Join("\n", candidateFiles));
        completenessPrompt.AppendLine();
        completenessPrompt.AppendLine(expandSb.ToString());
        completenessPrompt.AppendLine();
        completenessPrompt.AppendLine("Decide: Is the current plan complete, or do any of the newly discovered files need editing too?");
        completenessPrompt.AppendLine("Output ONLY valid JSON — no markdown, no extra text:");
        completenessPrompt.AppendLine(@"{""complete"": true, ""confidence"": ""high"", ""reasoning"": ""...""}");
        completenessPrompt.AppendLine("OR if incomplete:");
        completenessPrompt.AppendLine(@"{""complete"": false, ""confidence"": ""low"", ""reasoning"": ""..."", ""additions"": [{""file"": ""path"", ""change"": ""what to do""}]}");

        const string completenessSystem = "You are a plan validation specialist. Output only JSON.";

        var (raw, _, err) = await CallLlmRaw(completenessSystem, completenessPrompt.ToString(), ct,
            requestTimeout: TimeSpan.FromSeconds(30));

        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                var cleaned = raw.Trim();
                if (cleaned.StartsWith("```"))
                {
                    var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                    if (m.Success) cleaned = m.Groups[1].Value.Trim();
                }
                var objStart = cleaned.IndexOf('{');
                var objEnd = cleaned.LastIndexOf('}');
                if (objStart >= 0 && objEnd > objStart)
                    cleaned = cleaned.Substring(objStart, objEnd - objStart + 1);

                using var doc = JsonDocument.Parse(cleaned);
                var root = doc.RootElement;

                var isComplete = root.TryGetProperty("complete", out var c) && c.ValueKind == JsonValueKind.True && c.GetBoolean();
                var reasoning = root.TryGetProperty("reasoning", out var r) ? r.GetString() : "";

                if (!isComplete && root.TryGetProperty("additions", out var adds) && adds.ValueKind == JsonValueKind.Array)
                {
                    var newSteps = new List<PlanStep>();
                    foreach (var add in adds.EnumerateArray())
                    {
                        var file = add.TryGetProperty("file", out var f) ? f.GetString() : "";
                        var change = add.TryGetProperty("change", out var ch) ? ch.GetString() : "";
                        if (!string.IsNullOrWhiteSpace(file) && !string.IsNullOrWhiteSpace(change))
                        {
                            newSteps.Add(new PlanStep
                            {
                                File = file.Replace('\\', '/'),
                                Change = change,
                                Priority = plan.Plan.Count + newSteps.Count + 1
                            });
                        }
                    }
                    if (newSteps.Count > 0)
                    {
                        plan.Plan.AddRange(newSteps);
                        await EmitLog(emitSse, "info",
                            $"Phase 2.5 — added {newSteps.Count} file(s) to plan: {string.Join(", ", newSteps.Select(s => s.File))}",
                            ct: ct);
                    }
                }

                await EmitLog(emitSse, "info",
                    $"Phase 2.5 — completeness check: complete={isComplete}, confidence={reasoning}", ct: ct);
            }
            catch (JsonException) { }
        }

        return (plan, expandedContext);
    }

    /// <summary>
    /// Scans generated edits for references to methods/types that might belong
    /// to other files not included in the current edit set. Returns a list of
    /// candidate file paths that may also need editing.
    /// </summary>
    private async Task<List<string>> DetectCrossFileReferences(
        List<AgentStep> edits, string projectRoot, string currentRelPath, CancellationToken ct)
    {
        if (edits.Count == 0) return new List<string>();

        // Extract potential service/object names from newStrings
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in edits)
        {
            var ns = edit.NewString ?? "";
            if (string.IsNullOrWhiteSpace(ns)) continue;

            // Match patterns like "this.someService.someMethod(", "someService.someMethod(", "_someService.method("
            var refMatches = System.Text.RegularExpressions.Regex.Matches(ns,
                @"(?:this\.|private\s+\w+\s+)?(\w+)\s*\.\s*\w+\s*\(");
            foreach (System.Text.RegularExpressions.Match m in refMatches)
            {
                var candidate = m.Groups[1].Value;
                // Filter out common noise
                if (candidate.Length > 1 &&
                    !"this,that,it,is,are,was,not,if,else,for,while,case,new,var,let,const,function,async,await,return,true,false,null,undefined".Split(',').Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    identifiers.Add(candidate);
                }
            }
        }

        if (identifiers.Count == 0) return new List<string>();

        var candidateFiles = new List<string>();
        var searchRoots = new[] { projectRoot };
        if (!string.IsNullOrWhiteSpace(currentRelPath))
        {
            var currentDir = Path.GetDirectoryName(Path.Combine(projectRoot, currentRelPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!string.IsNullOrWhiteSpace(currentDir))
                searchRoots = new[] { currentDir, projectRoot };
        }

        foreach (var id in identifiers.Take(5))
        {
            ct.ThrowIfCancellationRequested();

            // Search for class/interface definitions matching the identifier
            try
            {
                var allFiles = Directory.EnumerateFiles(projectRoot, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".cs") || f.EndsWith(".ts") || f.EndsWith(".js"))
                    .Where(f => !f.Contains("node_modules", StringComparison.OrdinalIgnoreCase) &&
                                !f.Contains(".git", StringComparison.OrdinalIgnoreCase) &&
                                !f.Contains("bin\\", StringComparison.OrdinalIgnoreCase) &&
                                !f.Contains("obj\\", StringComparison.OrdinalIgnoreCase) &&
                                !f.Contains("dist\\", StringComparison.OrdinalIgnoreCase))
                    .Take(200);

                foreach (var file in allFiles)
                {
                    // Skip the file being edited
                    var relFile = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
                    if (string.Equals(relFile, currentRelPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Quick content scan for class/interface definition or method with this name
                    var content = await System.IO.File.ReadAllTextAsync(file, System.Text.Encoding.UTF8, ct);
                    if (content.Contains(id, StringComparison.OrdinalIgnoreCase))
                    {
                        candidateFiles.Add(relFile);
                        break; // Found in this file, move to next identifier
                    }
                }
            }
            catch { }
        }

        return candidateFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<AgentPlan?> AnalyzePromptAndPlanCodeChanges(
        string prompt, string discoveryContext, string projectRoot, bool emitSse, CancellationToken ct = default)
    {
        const string systemPrompt = @"You are a coding specialist agent.

Given a task and the contents of project files, output a structured plan. 
For EDITING EXISTING FILES: use the actual relative file path (e.g. ""src/app.js"") in the file field.
When describing changes, be very specific and detailed. Include line numbers if possible. The more precise you are, the better the agent can execute the plan.

OUTPUT FORMAT — respond with ONLY this JSON object, no markdown, no extra text:
{
  ""thinking"": ""Task Summary: <one-paragraph summary of what the user asked for, in your own words, referencing specific files, lines, or behaviors. Do NOT copy the user's prompt verbatim.>"",
  ""summary"":  ""<concrete description of what changes will be made, to which files>"",
  ""plan"": [
    {
      ""file"": ""relative/path/to/file"",
      ""change"": ""description of what to do. Be very detailed."",
      ""priority"": 1
    }
  ]
}

The ""change"" field is CRITICAL — it will be passed directly to the handler so it knows exactly what to do.
Make it specific and accurate.

FILE EDIT RULES (only when NOT using a special marker): 
- Priority 1 = most important file. Sort by priority ascending.
- When describing changes, quote exact existing code to modify. Include line numbers if possible.
- DO NOT write any code yet. DO NOT include oldString or newString.
- CRITICAL: Only reference code that actually exists in the provided file contents.
- Every change in the plan must be a change. Do not include a change unless there is something to change in the file. If the file is perfect as-is, do not include it in the plan.
- Each change must be a unique change. No other change in the plan should have the same change description, or you should make it more specific until they are all unique.
- If you're unsure about exact code, describe the location and intent clearly.";

        var analysisPrompt = new StringBuilder();
        analysisPrompt.AppendLine("## Task");
        analysisPrompt.AppendLine(prompt);
        analysisPrompt.AppendLine();
        analysisPrompt.AppendLine("## Project root");
        analysisPrompt.AppendLine(projectRoot);
        analysisPrompt.AppendLine();
        analysisPrompt.AppendLine("## Project Discovery (ONLY use paths listed here)");
        analysisPrompt.AppendLine(discoveryContext);

        var (raw, _, llmError) = await CallLlmRaw(systemPrompt, analysisPrompt.ToString(), ct, requestTimeout: _infiniteTimeout);

        // Only abort if we have NO content. "JSON parse failed" just means CallLlmRaw couldn't
        // deserialise the response as AgentResponse — which is expected here because the planning
        // prompt returns AgentPlan format (thinking/summary/plan), not AgentResponse format
        // (thinking/summary/complete/steps). The raw string is the real content we need.
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
        // Strip markdown fences the LLM sometimes wraps its JSON in
        var cleanedRaw = raw.Trim();
        if (cleanedRaw.StartsWith("```"))
        {
            var fenceMatch = Regex.Match(cleanedRaw, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            cleanedRaw = fenceMatch.Success ? fenceMatch.Groups[1].Value.Trim() : cleanedRaw.TrimStart('`');
        }
        // Ensure we only hand ParsePlan a JSON object, not surrounding prose
        var objStart = cleanedRaw.IndexOf('{');
        var objEnd = cleanedRaw.LastIndexOf('}');
        if (objStart >= 0 && objEnd > objStart)
            cleanedRaw = cleanedRaw.Substring(objStart, objEnd - objStart + 1);

        var parsedPlan = ParsePlan(cleanedRaw);
        if (parsedPlan == null)
        {
            await EmitLog(emitSse, "error", "Failed to parse plan.", ct: ct);
            return null;
        }

        return parsedPlan;
    }

    private async Task<(string discoveryText, List<object> steps)> RunBootstrapDiscovery(
    string prompt, string projectRoot, bool emitSse, List<string>? attachedFiles = null,
    CancellationToken ct = default)
    {
        if (attachedFiles != null && attachedFiles.Count > 0)
            return await RunLightBootstrap(attachedFiles, projectRoot, emitSse);

        await EmitLog(emitSse, "info", "Phase 1 — DISCOVER: enumerating project files…", ct: ct);
        var allSteps = new List<object>();

        // List root
        var listStep = new AgentStep { Index = 0, Type = "list", Path = "", Description = "Auto: list project root" };
        allSteps.AddRange(await ExecuteDiscoveryStepsConcurrent(new List<AgentStep> { listStep }, projectRoot, 0, emitSse));

        if (!Directory.Exists(projectRoot)) return ("", allSteps);

        // Enumerate all files (skip noise dirs)
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "node_modules", ".git", "bin", "obj", "dist", ".angular", "packages", ".vs", ".idea" };

        var allFiles = Directory.EnumerateFiles(projectRoot, "*.*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/'))
            .Where(rel => !skipDirs.Any(d =>
                rel.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase) ||
                rel.Contains("/" + d + "/", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (allFiles.Count == 0) return ("", allSteps);

        // Hinted files (pre-learned, highest trust)
        var hintedFiles = _fileHints.GetFilesForPrompt(prompt, projectRoot)
            .Where(f => allFiles.Any(a => string.Equals(a, f, StringComparison.OrdinalIgnoreCase)))
            .Take(4).ToList();

        // Score by task type (no LLM)
        var heuristicCandidates = AgentUtilities.ApplyTaskTypeHeuristics(prompt, allFiles);

        var candidatePool = hintedFiles
            .Concat(heuristicCandidates)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(60).ToList();

        // One LLM call to pick which files to read
        List<string> toRead;
        if (candidatePool.Count <= 6)
        {
            toRead = candidatePool;
            await EmitLog(emitSse, "info", $"Phase 1 — {candidatePool.Count} candidate(s), reading all", ct: ct);
        }
        else
        {
            await EmitLog(emitSse, "info", $"Phase 1 — selecting from {candidatePool.Count} candidates (1 LLM call)…", candidatePool, ct: ct);
            var selected = await SelectRelevantFilesWithLlm(prompt, candidatePool, emitSse, ct);
            toRead = hintedFiles.Concat(selected).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        }

        // Verify files exist
        toRead = toRead.Where(f =>
        {
            var full = Path.GetFullPath(Path.Combine(projectRoot, f.Replace('/', Path.DirectorySeparatorChar)));
            return System.IO.File.Exists(full) && AgentUtilities.IsPathUnderRoot(full, projectRoot);
        }).ToList();

        await EmitLog(emitSse, "info", $"Phase 1 — reading {toRead.Count} file(s): {string.Join(", ", toRead)}", ct: ct);

        // Read in parallel
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

            allSteps.AddRange(await ExecuteDiscoveryStepsConcurrent(readPlan, projectRoot, allSteps.Count, emitSse));

            foreach (var f in toRead)
                _fileHints.LearnFromGrepOutput(prompt, f, projectRoot);
        }

        await EmitLog(emitSse, "info", $"Phase 1 complete — {allSteps.Count} steps, {toRead.Count} file(s) read", ct: ct);
        return (AgentUtilities.BuildDiscoveryTextFromSteps(allSteps), allSteps);
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

        // ── Step 2: extract first balanced JSON object ─────────────────────────
        var objStart = cleaned.IndexOf('{');
        var objEnd = cleaned.LastIndexOf('}');
        if (objStart >= 0 && objEnd > objStart)
            cleaned = cleaned.Substring(objStart, objEnd - objStart + 1);

        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        // ── Step 3: try candidates with progressively more aggressive repair ───
        foreach (var candidate in AgentUtilities.GeneratePlanJsonCandidates(cleaned))
        {
            try
            {
                var result = JsonSerializer.Deserialize<AgentPlan>(candidate, opts);
                if (result?.Plan != null) return result;
            }
            catch { /* try next */ }
        }

        Console.Error.WriteLine($"[ParsePlan] All repair strategies failed. Raw snippet: {cleaned[..Math.Min(200, cleaned.Length)]}");
        return null;
    }


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
    private async Task<(List<object> allSteps, string summary, bool complete, string thinking)> Orchestrate(
     string prompt, string projectRoot, bool emitSse, CancellationToken ct = default,
     List<string>? attachedFiles = null)
    {
        // ── Connectivity check ────────────────────────────────────────────
        if (!await CheckLlmConnectivity(projectRoot, emitSse, ct))
            throw new InvalidOperationException("LLM connectivity check failed.");

        PipelineType? pipelineType = await TryClassifyWithLlm(prompt, emitSse, ct);
        if (pipelineType == null)
        {
            await EmitLog(emitSse, "warn", "LLM failed to classify the prompt. Attempting to classify manually.", ct: ct);
            pipelineType = AgentUtilities.ClassifyTask(prompt);
        }

        await EmitLog(emitSse, "info", $"Router → {pipelineType} pipeline", ct: ct);

        // ── Route to the right pipeline ───────────────────────────────────
        var (allSteps, summary, thinking) = pipelineType switch
        {
            PipelineType.CommandExecution => await CommandExecutionPipeline(prompt, projectRoot, emitSse, ct),
            PipelineType.CodeEdit => await CodeEditPipeline(prompt, projectRoot, emitSse, ct, attachedFiles: attachedFiles),
            _ => await CodeEditPipeline(prompt, projectRoot, emitSse, ct, attachedFiles: attachedFiles),
        };

        // ── Verification Pipeline ─────────────────────────────────────────
        var (complete, feedback) = await VerificationPipeline(
            prompt, allSteps, projectRoot, emitSse, ct);

        // ── Reprisal Pipeline ─────────────────────────────────────────
        if (!complete && !string.IsNullOrEmpty(feedback))
        {
            var isBuildFailure = Regex.IsMatch(feedback, @"\berror\s+CS\d+\b", RegexOptions.IgnoreCase) ||
                                 feedback.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase);

            if (isBuildFailure)
            {
                await EmitLog(emitSse, "info", "Build failure detected — starting debug session", ct: ct);
                var (debugSteps, debugSummary, debugComplete, debugThinking) =
                    await DebugBuildPipeline(prompt, feedback, projectRoot, emitSse, ct);
                allSteps.AddRange(debugSteps);
                summary = debugSummary ?? summary;
                thinking = debugThinking ?? thinking;
                complete = debugComplete;
            }
            else
            {
                await EmitLog(emitSse, "info", "Starting reprisal attempt based on feedback.", feedback, ct: ct);
                int attempt = 0;
                while (attempt < 5 && !complete)
                {
                    string reprisalPrompt = $@"The previous coding attempt was not successful. 
                    Please try again, taking this feedback into account.  
                    ## Original Task: ```{prompt}```.
                    ## Feedback: ```{feedback}```. ";

                    attempt++;
                    await EmitLog(emitSse, "info", $"Reprisal attempt #{attempt}", feedback, ct: ct);

                    // Refresh context: re-read edited files from disk so LLM sees current state
                    var refreshedSteps = await RefreshEditedFilesFromDisk(allSteps, projectRoot, emitSse, ct);
                    var reprisalContext = AgentUtilities.BuildDiscoveryTextFromSteps(refreshedSteps);

                    var (reprisalSteps, reprisalSummary, reprisalThinking) = await CodeEditPipeline(
                        reprisalPrompt, projectRoot, emitSse, ct,
                        prebuiltDiscoveryContext: reprisalContext,
                        prebuiltDiscoverySteps: refreshedSteps.Where(s =>
                        {
                            if (s is not Dictionary<string, object?> r) return false;
                            var t = r.TryGetValue("type", out var tv) ? tv?.ToString() : "";
                            return t is "list" or "grep" or "glob" or "read";
                        }).ToList());
                    allSteps.AddRange(reprisalSteps);
                    var (reprisalComplete, _) = await VerificationPipeline(reprisalPrompt, allSteps, projectRoot, emitSse, ct);
                    if (!reprisalComplete)
                    {
                        await EmitLog(emitSse, "info", "Reprisal attempt failed.", new { reprisalSteps, reprisalSummary, reprisalComplete, reprisalThinking }, ct: ct);
                    }
                    summary = reprisalSummary;
                    thinking = reprisalThinking;
                    complete = reprisalComplete;
                }
            }
        }

        return (allSteps, summary, complete, thinking);
    }

    /// <summary>
    /// Re-reads edited files from disk so reprisal attempts see current file state,
    /// not stale newContent captured from a previous edit step.
    /// </summary>
    private async Task<List<object>> RefreshEditedFilesFromDisk(List<object> steps, string projectRoot, bool emitSse, CancellationToken ct)
    {
        // Collect unique paths from edit/rename/create steps
        var editedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in steps)
        {
            if (item is not Dictionary<string, object?> r) continue;
            var type = r.TryGetValue("type", out var t) ? t?.ToString() : "";
            if (type is not ("edit" or "rename" or "create" or "write")) continue;
            var path = r.TryGetValue("path", out var p) ? p?.ToString() : "";
            if (!string.IsNullOrWhiteSpace(path))
                editedPaths.Add(path.Replace('\\', '/'));
        }

        if (editedPaths.Count == 0)
            return steps;

        await EmitLog(emitSse, "info", $"Reprisal: refreshing {editedPaths.Count} edited file(s) from disk", editedPaths, ct: ct);

        // Build a result list: copy over all non-read steps, then append fresh reads
        var result = new List<object>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in steps)
        {
            // Include discovery steps as-is; skip old edit outputs with stale newContent
            if (item is Dictionary<string, object?> r)
            {
                var type = r.TryGetValue("type", out var t) ? t?.ToString() : "";
                if (type is "list" or "grep" or "glob" or "read")
                {
                    result.Add(item);
                    if (r.TryGetValue("path", out var p) && p?.ToString() is string rp && !string.IsNullOrWhiteSpace(rp))
                        seenPaths.Add(rp.Replace('\\', '/'));
                }
                // Skip old edit/create/write/rename steps — we'll re-read current content
            }
        }

        // Re-read edited files from disk to get current state
        var freshReads = new List<object>();
        foreach (var relPath in editedPaths)
        {
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!System.IO.File.Exists(fullPath)) continue; // file was deleted

            string content;
            try
            {
                content = await System.IO.File.ReadAllTextAsync(fullPath, ct);
            }
            catch { continue; }

            var readStep = new Dictionary<string, object?>
            {
                ["type"] = "read",
                ["path"] = relPath,
                ["description"] = $"Auto: re-read {relPath} (post-edit)",
                ["output"] = content,
                ["index"] = result.Count
            };
            // If this path was already read in discovery, overwrite with fresh data
            var existingIdx = result.FindIndex(e =>
                e is Dictionary<string, object?> er &&
                er.TryGetValue("type", out var et) && et?.ToString() == "read" &&
                er.TryGetValue("path", out var ep) && ep?.ToString()?.Replace('\\', '/') == relPath);
            if (existingIdx >= 0)
                result[existingIdx] = readStep;
            else
                freshReads.Add(readStep);
        }

        result.AddRange(freshReads);

        // Log summary
        var freshCount = result.Count(e =>
            e is Dictionary<string, object?> er &&
            er.TryGetValue("type", out var et) && et?.ToString() == "read" &&
            er.TryGetValue("path", out var ep) && editedPaths.Contains(ep?.ToString()?.Replace('\\', '/') ?? ""));
        await EmitLog(emitSse, "info", $"Reprisal: {freshCount}/{editedPaths.Count} edited files refreshed from disk", ct: ct);

        return result;
    }

    private async Task<(List<object> steps, string summary, string thinking)> CommandExecutionPipeline(
        string prompt, string projectRoot, bool emitSse, CancellationToken ct)
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
            return (steps, fastPlan.Summary, fastPlan.Thinking ?? "");
        }

        await EmitLog(emitSse, "info", "CommandExecution (agentic): LLM has terminal control", ct: ct);
        _terminal.Start();

        var isWindows = OperatingSystem.IsWindows();
        var shellName = isWindows ? "PowerShell" : "Bash";
        var desktopPath = isWindows ? "$env:USERPROFILE\\Desktop" : "$HOME/Desktop";
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
        conversation.AppendLine("  - To read emails: {\"email\": \"inbox\"} or {\"email\": \"validate\"}");
        conversation.AppendLine("  - To show a result to the user: {\"message\": \"your answer here\"}");
        conversation.AppendLine("  - When done: {\"done\": true, \"summary\": \"what was accomplished\"}");
        conversation.AppendLine($"  - Desktop path: {desktopPath}");
        conversation.AppendLine($"  - WRITE FILE: {fileCmd} -Path \"<path>\" -Value \"<content>\"");
        conversation.AppendLine("  - CREATE FILE: New-Item -ItemType File -Path \"<path>\" -Force");
        conversation.AppendLine("  - NEVER use mkdir, curl, wget, jq, python, Set-Location, cd, or bash syntax — they do NOT work here");
        conversation.AppendLine("  - If a command shows ⚠ Error:, read it and try a DIFFERENT command");
        conversation.AppendLine("  - web_search uses DuckDuckGo — if it returns empty, try web_fetch with a direct API URL");
        conversation.AppendLine("  - To read emails, email must be configured in maestroconfig.json (imap server, username, password)");
        conversation.AppendLine("  - Write all files directly to the target path. Never create folders or extra files unless the user explicitly asks for them.");
        conversation.AppendLine("  - Max 15 iterations");
        conversation.AppendLine();
        conversation.AppendLine($"Task: {prompt}");
        conversation.AppendLine();

        const int maxIterations = 15;
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
            try
            {
                using var doc = JsonDocument.Parse(cleaned);
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

                    await EmitLog(emitSse, "step", $"▶ cmd[{i + 1}]: {cmd}", ct: ct);
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

                    await EmitLog(emitSse, "step", $"▶ email[{i + 1}]: {action}", ct: ct);

                    // Try to auto-configure before proceeding
                    var cfgCheck = await _emailService.CheckAndAutoConfigureAsync();
                    if (!cfgCheck.IsConfigured && action != "validate" && action != "test")
                    {
                        // Ask the user for missing credentials
                        var questionFields = new List<QuestionField>();
                        if (string.IsNullOrWhiteSpace(cfgCheck.ExistingUsername))
                        {
                            questionFields.Add(new QuestionField
                            {
                                Key = "emailUsername",
                                Label = "Email username",
                                Type = "text",
                                DefaultValue = ""
                            });
                        }
                        if (cfgCheck.MissingField == "emailImapServer" || string.IsNullOrWhiteSpace(cfgCheck.ExistingServer))
                        {
                            questionFields.Add(new QuestionField
                            {
                                Key = "emailImapServer",
                                Label = "IMAP server",
                                Type = "text",
                                DefaultValue = cfgCheck.AutoServer ?? "imap.gmail.com"
                            });
                            questionFields.Add(new QuestionField
                            {
                                Key = "emailImapPort",
                                Label = "IMAP port",
                                Type = "text",
                                DefaultValue = (cfgCheck.AutoPort ?? 993).ToString()
                            });
                        }
                        // always ask password if missing
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
                            Question = "Email is not configured. Please enter your IMAP credentials:",
                            Fields = questionFields,
                            CreatedUtc = DateTime.UtcNow
                        };
                        _pendingQuestions[pendingId] = pending;

                        // Send SSE question event
                        await SendSse(Response, "question", new
                        {
                            id = pending.Id,
                            question = pending.Question,
                            fields = pending.Fields
                        }, ct);

                        // Emit log
                        await EmitLog(emitSse, "info", $"⏳ Waiting for email credentials from user...", ct: ct);

                        // Block until user answers (with timeout)
                        string? qError = null;
                        try
                        {
                            var timeout = TimeSpan.FromMinutes(5);
                            var answer = await pending.Answer.Task.WaitAsync(timeout, ct);

                            // Save the answers to config
                            var hasCreds = answer.Any(a => !string.IsNullOrWhiteSpace(a.Value));
                            if (hasCreds)
                            {
                                var cfg = await _configFile.LoadConfigAsync();
                                if (answer.TryGetValue("emailImapServer", out var server) && !string.IsNullOrWhiteSpace(server))
                                    cfg.emailImapServer = server;
                                if (answer.TryGetValue("emailImapPort", out var portStr) && int.TryParse(portStr, out var port))
                                    cfg.emailImapPort = port;
                                if (answer.TryGetValue("emailUsername", out var username) && !string.IsNullOrWhiteSpace(username))
                                    cfg.emailUsername = username;
                                if (answer.TryGetValue("emailPassword", out var password) && !string.IsNullOrWhiteSpace(password))
                                    cfg.emailPassword = password;

                                await _configFile.WriteConfigAsync(cfg);

                                await EmitLog(emitSse, "info", "✓ Email credentials saved — retrying...", ct: ct);
                                conversation.AppendLine("Email credentials have been saved. The user entered their credentials. Retry the email command now — it should work.");
                            }
                            else
                            {
                                // User cancelled (empty answers)
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

                    try
                    {
                        if (action == "validate")
                        {
                            var status = await _emailService.ValidateConfigAsync();
                            var validateResult = new Dictionary<string, object?>
                            {
                                ["index"] = stepIndex++,
                                ["type"] = "email",
                                ["action"] = action,
                                ["status"] = status == "ok" ? "done" : "error",
                                ["command"] = status,
                                ["output"] = status
                            };
                            steps.Add(validateResult);
                            if (emitSse) await SendSse(Response, "step", validateResult, ct);
                            if (status == "ok")
                            {
                                conversation.AppendLine($"Email validate [{i + 1}]: ok — IMAP connection works");
                            }
                            else if (status == "not_configured")
                            {
                                conversation.AppendLine("⚠ Error: Email is NOT configured. The user must set emailImapServer, emailUsername, and emailPassword in Settings or maestroconfig.json. Do NOT retry — tell the user to configure email first, then call {\"done\": true}.");
                            }
                            else
                            {
                                var statusLower = status.ToLowerInvariant();
                                if (statusLower.Contains("authentication") || statusLower.Contains("invalid credentials") || statusLower.Contains("app"))
                                {
                                    var emailCfg = await _configFile.LoadConfigAsync();
                                    var server = (emailCfg.emailImapServer ?? "").ToLowerInvariant();
                                    if (server.Contains("gmail") || server.Contains("google"))
                                        conversation.AppendLine("⚠ Error: Gmail rejected the login. Google requires an App Password (not your regular password) when 2-factor authentication is enabled. Generate one at https://myaccount.google.com/apppasswords and save it as emailPassword in Settings. Then call {\"done\": true}.");
                                    else if (server.Contains("outlook") || server.Contains("office") || server.Contains("live") || server.Contains("hotmail") || server.Contains("msn"))
                                        conversation.AppendLine("⚠ Error: Outlook/Hotmail rejected the login. Microsoft requires an App Password (not your regular password) when 2-factor authentication is enabled. Generate one at https://account.live.com/password/apppasswords and save it as emailPassword in Settings. Then call {\"done\": true}.");
                                    else
                                        conversation.AppendLine("⚠ Error: The email server rejected the login. Check your username and password. Some providers require an App Password when 2-factor authentication is enabled. Then call {\"done\": true}.");
                                }
                                else
                                    conversation.AppendLine($"⚠ Error: Email validation failed — {status} This is NOT a transient error; retrying will not fix it. Do NOT retry — tell the user the email issue, then call {{\"done\": true}}.");
                            }
                            conversation.AppendLine();
                            continue;
                        }

                        var unreadOnly = action == "inbox" || action == "unread";
                        var emails = await _emailService.FetchLatestEmailsAsync(10, unreadOnly);
                        var emailOutput = new StringBuilder();
                        if (emails.Count == 0)
                        {
                            emailOutput.AppendLine("No emails found.");
                        }
                        else
                        {
                            emailOutput.AppendLine($"Found {emails.Count} email(s):");
                            emailOutput.AppendLine();
                            for (var ei = 0; ei < emails.Count; ei++)
                            {
                                var e = emails[ei];
                                emailOutput.AppendLine($"--- Email {ei + 1} ---");
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
                        var emailStr = emailOutput.ToString();
                        var emailResult = new Dictionary<string, object?>
                        {
                            ["index"] = stepIndex++,
                            ["type"] = "email",
                            ["action"] = action,
                            ["status"] = "done",
                            ["command"] = $"fetched {emails.Count} email(s)",
                            ["output"] = emailStr
                        };
                        steps.Add(emailResult);
                        if (emitSse) await SendSse(Response, "step", emailResult, ct);
                        conversation.AppendLine($"Email [{i + 1}]: fetched {emails.Count} email(s)");
                        conversation.AppendLine(emailStr);
                    }
                    catch (Exception ex)
                    {
                        var errMsg = $"⚠ Error: Email failed — {ex.Message} This is NOT a transient error; retrying will not fix it. Do NOT retry — tell the user the email issue, then call {{\"done\": true}}.";
                        var exLower = ex.Message.ToLowerInvariant();
                        if (ex.Message.Contains("not configured", StringComparison.OrdinalIgnoreCase))
                            errMsg = "⚠ Error: Email is NOT configured. The user must set emailImapServer, emailUsername, and emailPassword in Settings or maestroconfig.json. Do NOT retry — tell the user to configure email first, then call {\"done\": true}.";
                        else if (exLower.Contains("authentication") || exLower.Contains("invalid credentials") || exLower.Contains("app") || exLower.Contains("password"))
                        {
                            var emailCfg = await _configFile.LoadConfigAsync();
                            var server = (emailCfg.emailImapServer ?? "").ToLowerInvariant();
                            if (server.Contains("gmail") || server.Contains("google"))
                                errMsg = "⚠ Error: Gmail rejected the login. Google requires an App Password (not your regular password) when 2-factor authentication is enabled. Generate one at https://myaccount.google.com/apppasswords and save it as emailPassword in Settings.";
                            else if (server.Contains("outlook") || server.Contains("office") || server.Contains("live") || server.Contains("hotmail") || server.Contains("msn"))
                                errMsg = "⚠ Error: Outlook/Hotmail rejected the login. Microsoft requires an App Password (not your regular password) when 2-factor authentication is enabled. Generate one at https://account.live.com/password/apppasswords and save it as emailPassword in Settings. If your account uses modern auth, enable 'Allow less secure apps' or use SMTP submission.";
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
                        if (emitSse) await SendSse(Response, "step", errResult, ct);
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
            }
            catch (JsonException)
            {
                // Not valid JSON — treat raw as a command
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
        return (steps, summary, "");
    } 
    
    // ═════════════════════════════════════════════════════════════════════════
    //  CODE EDIT PIPELINE  —  discover → plan → edit → review loop
    // ═════════════════════════════════════════════════════════════════════════

    private async Task<(List<object> steps, string summary, string thinking)> CodeEditPipeline(
        string prompt, string projectRoot, bool emitSse, CancellationToken ct,
        List<string>? attachedFiles = null,
        string? prebuiltDiscoveryContext = null,
        List<object>? prebuiltDiscoverySteps = null)
    {
        var allSteps = new List<object>();

        // ── Phase 1: DISCOVER (skip if prebuilt context provided) ────────
        string discoveryContext;
    
        await EmitLog(emitSse, "info", "CodeEdit: Phase 1 — DISCOVER", ct: ct);
        var (dc, ds) = await RunBootstrapDiscovery(prompt, projectRoot, emitSse, attachedFiles, ct);
        discoveryContext = dc;
        allSteps.AddRange(ds);

        // ── Context Review: let user confirm / remove files ──────────────
        if (emitSse && prebuiltDiscoveryContext == null && prebuiltDiscoverySteps == null)
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
                    files = readFiles.Select(f => new { path = f }).ToList()
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

        // ── Phase 2: PLAN ─────────────────────────────────────────────────
        await EmitLog(emitSse, "info", "CodeEdit: Phase 2 — PLAN", ct: ct);
        if (emitSse)
            await SendSse(Response, "phase", new { phase = "plan", message = "Planning...", contextSize = discoveryContext.Length }, ct);

        AgentPlan? plan = await AnalyzePromptAndPlanCodeChanges(prompt, discoveryContext, projectRoot, emitSse, ct);
        if (plan == null || plan.Plan.Count == 0)
        {
            await EmitLog(emitSse, "warn", $"Plan phase produced no items. Context length: {discoveryContext.Length} characters.", new { plan }, ct: ct);
            throw new InvalidOperationException("LLM returned an empty or unparseable plan.");
        }

        // Emit thinking immediately
        if (emitSse && !string.IsNullOrWhiteSpace(plan.Thinking))
            await SendSse(Response, "thinking", new { text = plan.Thinking }, ct);

        await EmitLog(emitSse, "info",
            $"Plan: {plan.Plan.Count} step(s) — {string.Join(", ", plan.Plan.Select(p => p.File))}. Context length: {discoveryContext.Length} characters.",
            new { plan },
            ct: ct);

        if (emitSse)
            await SendSse(Response, "plan",
                new { thinking = plan.Thinking, summary = plan.Summary, items = plan.Plan, contextSize = discoveryContext.Length }, ct);

        // ── Phase 2.5: CROSS-FILE DISCOVERY ───────────────────────────────
        // After planning, scan each planned file for imports, service calls,
        // and type references to find cross-file dependencies.  If new files
        // are discovered, the LLM decides whether the plan needs updating.
        await EmitLog(emitSse, "info", "CodeEdit: Phase 2.5 — CROSS-FILE DISCOVERY", ct: ct);
        if (emitSse)
            await SendSse(Response, "phase", new { phase = "cross-file-discovery", message = "Scanning for cross-file references...", contextSize = discoveryContext.Length }, ct);

        var (expandedPlan, expandedContext) = await ExpandDiscoveryFromPlan(
            plan, prompt, projectRoot, discoveryContext, emitSse, ct);

        // Re-emit plan if it was amended
        if (expandedPlan.Plan.Count != plan.Plan.Count && emitSse)
            await SendSse(Response, "plan",
                new { thinking = expandedPlan.Thinking, summary = expandedPlan.Summary, items = expandedPlan.Plan }, ct);

        plan = expandedPlan;
        discoveryContext = expandedContext;

        // ── Compact discovery context: keep full content only for plan files ──
        var beforeTokens = AgentUtilities.EstimateTokens(discoveryContext);
        var planFiles = new HashSet<string?>(plan.Plan
            .Select(p => p.File?.Replace('\\', '/'))
            .Where(f => !string.IsNullOrWhiteSpace(f)), StringComparer.OrdinalIgnoreCase);
        discoveryContext = AgentUtilities.CompactDiscoveryContext(discoveryContext, planFiles);
        var afterTokens = AgentUtilities.EstimateTokens(discoveryContext);
        if (afterTokens < beforeTokens)
            await EmitLog(emitSse, "info",
                $"Compacted discovery context: {beforeTokens} → {afterTokens} tokens ({beforeTokens - afterTokens} saved)", ct: ct);

        // ── Phase 3: EXECUTE PLAN ─────────────────────────────────────────
        await ExecutePlan(prompt, projectRoot, emitSse, discoveryContext, plan, ct, allSteps);

        return (allSteps, plan.Summary, plan.Thinking ?? "");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  VERIFICATION PIPELINE  —  shared by all pipelines
    // ═════════════════════════════════════════════════════════════════════════

    private async Task<(bool complete, string? feedback)> VerificationPipeline(
        string prompt, List<object> allSteps, string projectRoot,
        bool emitSse, CancellationToken ct)
    {
        var steps = allSteps.OfType<Dictionary<string, object?>>().ToList();

        // Rename / delete operations are self-verifying — if the step succeeded the task is done
        var hasSuccessfulRenames = steps.Any(s =>
            s.TryGetValue("type", out var t) && t?.ToString() == "rename" &&
            s.TryGetValue("status", out var st) && st?.ToString() == "done");

        var hasFailedRenames = steps.Any(s =>
            s.TryGetValue("type", out var t) && t?.ToString() == "rename" &&
            s.TryGetValue("status", out var st) && st?.ToString() == "error");

        var hasFileEdits = steps.Any(s =>
            s.TryGetValue("type", out var t) && t?.ToString() == "edit" &&
            s.TryGetValue("status", out var st) && st?.ToString() == "done");

        // Successful rename with no code edits → immediately complete, no LLM review
        if (hasSuccessfulRenames && !hasFileEdits)
        {
            var renamedPaths = steps
                .Where(s => s.TryGetValue("type", out var t) && t?.ToString() == "rename"
                         && s.TryGetValue("status", out var st) && st?.ToString() == "done")
                .Select(s =>
                {
                    s.TryGetValue("path", out var src);
                    s.TryGetValue("toPath", out var dst);
                    return $"{src} → {dst}";
                });
            await EmitLog(emitSse, "success", $"Verification ✓ rename completed: {string.Join(", ", renamedPaths)}", ct: ct);
            return (true, $"File renamed successfully");
        }

        if (hasFailedRenames && !hasSuccessfulRenames)
            return (false, "Rename operation failed — check the error in the step output");

        // Command-only operations (git, ping, show, package_install) — no file changes expected
        var hasSuccessfulCommands = steps.Any(s =>
            s.TryGetValue("type", out var t) && t?.ToString() == "command" &&
            s.TryGetValue("status", out var st) && st?.ToString() == "done");

        var editsApplied = AgentUtilities.HasSuccessfulEdits(allSteps);
        if (!editsApplied && !hasSuccessfulRenames)
        {
            if (!AgentUtilities.TaskExpectsFileChanges(prompt) || hasSuccessfulCommands)
                return (true, "Task completed (no file changes needed)");
            return (false, "No file edits were applied");
        }

        // Content review for code edits
        await EmitLog(emitSse, "info", "Verification: reviewing edited files…", ct: ct);
        var (complete, feedback) = await RunContentReview(prompt, allSteps, projectRoot, emitSse, ct);

        // Build check
        if (complete)
        {
            var config = await _configFile.LoadConfigAsync();
            var buildCmd = config.buildCommands;
            if (!string.IsNullOrWhiteSpace(buildCmd))
            {
                var (buildOk, buildOutput) = await RunQuickBuild(projectRoot, buildCmd, emitSse, ct);
                if (!buildOk)
                {
                    await EmitLog(emitSse, "warn",
                        "Verification: build failed after content review — re-planning with build errors", ct: ct);
                    return (false, buildOutput);
                }
            }
        }

        return (complete, feedback);
    }

    private async Task<(List<object> allSteps, string summary, bool complete, string thinking)> DebugBuildPipeline(
        string originalPrompt, string buildOutput, string projectRoot, bool emitSse, CancellationToken ct)
    {
        var allSteps = new List<object>();
        var lastSummary = "";
        var lastThinking = "";
        var maxAttempts = 3;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var debugPrompt = attempt == 0
                ? $"The build failed after applying the user's request. User request: {originalPrompt}\n\nBuild errors:\n{buildOutput}\n\nFix these build errors. Do NOT change the logic or feature — only fix build/compilation errors."
                : $"The build is still failing after fix attempt {attempt} of {maxAttempts}. Build errors:\n{buildOutput}\n\nFix these build errors. Do NOT change the logic or feature — only fix build/compilation errors.";

            await EmitLog(emitSse, "info", $"Debug build attempt {attempt + 1}/{maxAttempts}", ct: ct);

            var (steps, summary, thinking) = await CodeEditPipeline(debugPrompt, projectRoot, emitSse, ct);
            allSteps.AddRange(steps);
            lastSummary = summary ?? lastSummary;
            lastThinking = thinking ?? lastThinking;

            var config = await _configFile.LoadConfigAsync();
            var buildCmd = config.buildCommands;
            if (string.IsNullOrWhiteSpace(buildCmd))
            {
                await EmitLog(emitSse, "info", "No build command configured — debug session complete", ct: ct);
                return (allSteps, summary ?? lastSummary, true, thinking ?? lastThinking);
            }

            var (buildOk, freshOutput) = await RunQuickBuild(projectRoot, buildCmd, emitSse, ct);
            if (buildOk)
            {
                await EmitLog(emitSse, "success", $"Debug build passes after attempt {attempt + 1}", ct: ct);
                return (allSteps, lastSummary, true, lastThinking);
            }

            buildOutput = freshOutput;
            await EmitLog(emitSse, "warn", $"Debug build still failing after attempt {attempt + 1}", ct: ct);
        }

        await EmitLog(emitSse, "warn", "Debug build exhausted all attempts", ct: ct);
        return (allSteps, lastSummary, false, lastThinking);
    }

    private async Task ExecutePlan(string prompt, string projectRoot, bool emitSse, string discoveryContext, AgentPlan plan, CancellationToken ct, List<object> allResults)
    {
        //  string File, string ChangeDescription, int Priority
        var stepIndex = 0;
        var planItems = plan.Plan.ToList();
        foreach (var item in planItems)
        {
            var planFile = item.File;
            var changeDesc = item.Change;
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

            else if (changeDesc.StartsWith("rename", StringComparison.OrdinalIgnoreCase)
                || changeDesc.StartsWith("move", StringComparison.OrdinalIgnoreCase))
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
                await RunEditingPipeline(item, discoveryContext, projectRoot, prompt, stepIndex, allResults, emitSse, ct);
            }

            else if (string.IsNullOrWhiteSpace(planFile))
            {
                await EmitLog(emitSse, "warn", $"Plan item with empty file field — skipping", new { item }, ct: ct);
                continue;
            }
        }
    }

    private async Task RunEditingPipeline(
        PlanStep item,
        string discoveryContext,
        string projectRoot,
        string originalPrompt,
        int idx,
        List<object> allResults,
        bool emitSse,
        CancellationToken ct)
    {
        // Combine the original prompt with the file-specific change description
        var relPath = item.File;
        var fileTask = string.IsNullOrWhiteSpace(item.Change)
            ? originalPrompt
            : $"{originalPrompt}\n\nSpecific change needed in {relPath}: {item.Change}";

        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));

        if (!AgentUtilities.IsPathUnderRoot(fullPath, projectRoot))
        {
            await EmitLog(emitSse, "warn", $"Skipping {relPath} — outside project root", ct: ct);
            return;
        }
        if (emitSse)
        {
            await SendSse(Response, "phase", new { phase = "edit-file", message = $"Editing {relPath}…" }, ct);
        }

        var fileContent = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8);

        List<AgentStep> editSteps = new();
        var timedOut = false;
        var noEditsNeeded = false;
        var editHistory = new List<(string path, string preContent)>();

        // ── Multi-Phase Robust Editing Pipeline ─────────────────────────────
        // Phase 1: Deep Analysis, Phase 2: Detailed Plan, Phase 3: Code Gen,
        // Phase 4: Review & Refine, Phase 5: Apply
        // ─────────────────────────────────────────────────────────────────────
        editSteps = await RunMultiPhasePipeline(
            fileTask, relPath, fileContent, discoveryContext, projectRoot, emitSse, ct);

        // ── Fallback: direct single-call edit if multi-phase produced nothing ──
        if (editSteps.Count == 0)
        {
            await EmitLog(emitSse, "info",
                $"Multi-phase pipeline produced no edits for {relPath} — falling back to direct LLM edit", ct: ct);

            var infoRequestCount = 0;
            var attempt = 0;
            while (attempt < 4 && editSteps.Count == 0)
            {
                await EmitLog(emitSse, "info",
                    $"LLM edit call: {relPath} (attempt {attempt + 1})",
                    new { chars = fileContent.Length, taskSummary = item.Change }, ct: ct);

                var (raw, _, err) = await CallLlmSingleFileEdit(
                    fileTask, relPath, fileContent, projectRoot, attempt, discoveryContext, ct);

                if (err != null && err.StartsWith("Timed out", StringComparison.Ordinal))
                {
                    timedOut = true;
                    await EmitLog(emitSse, "error", $"Timed out editing {relPath} — skipping", ct: ct);
                    break;
                }

                await EmitLog(emitSse, "debug",
                    $"LLM raw ({raw?.Length ?? 0} chars)",
                    new { raw }, ct: ct);

                var needMoreInfoRequest = "";
                editSteps = ParseEditsFromLlmRaw(raw, relPath, out var noEditsOuter, out needMoreInfoRequest);
                if (noEditsOuter)
                {
                    noEditsNeeded = true;
                    await EmitLog(emitSse, "info", $"No edits needed for {relPath} — skipping", ct: ct);
                    break;
                }

                if (!string.IsNullOrWhiteSpace(needMoreInfoRequest))
                {
                    infoRequestCount++;
                    if (infoRequestCount > 3)
                    {
                        await EmitLog(emitSse, "warn",
                            $"Too many info requests for {relPath} — giving up", ct: ct);
                        break;
                    }
                    await EmitLog(emitSse, "info",
                        $"Info request #{infoRequestCount}: {needMoreInfoRequest}", ct: ct);
                    var extraContext = await ExecuteInfoRequest(needMoreInfoRequest, projectRoot, relPath, emitSse, ct);
                    if (!string.IsNullOrWhiteSpace(extraContext))
                    {
                        discoveryContext += "\n" + extraContext;
                        await EmitLog(emitSse, "info",
                            $"Discovery expanded — retrying {relPath} with new context", ct: ct);
                    }
                    continue;
                }

                attempt++;

                string? rejectReason = null;
                if (editSteps.Count > 0)
                {
                    var missingNew = editSteps.Where(e => string.IsNullOrWhiteSpace(e.NewString)).ToList();
                    if (missingNew.Count > 0)
                        rejectReason = "newString is empty — model returned oldString without replacement (treated as deletion)";

                    var identical = editSteps.Where(e => !string.IsNullOrEmpty(e.OldString) && string.Equals(
                        AgentUtilities.NormalizeLineEndings(e.OldString ?? ""),
                        AgentUtilities.NormalizeLineEndings(e.NewString ?? ""),
                        StringComparison.Ordinal)).ToList();
                    if (identical.Count > 0)
                    {
                        if (rejectReason == null)
                            rejectReason = "oldString and newString are identical";
                        else
                            rejectReason += "; some edits are identical oldString/newString (no-op)";
                    }
                }

                editSteps = editSteps
                    .Where(e => !string.Equals(
                        AgentUtilities.NormalizeLineEndings(e.OldString ?? ""),
                        AgentUtilities.NormalizeLineEndings(e.NewString ?? ""),
                        StringComparison.Ordinal))
                    .ToList();

                if (editSteps.Count == 0 && (rejectReason ?? err) != null)
                {
                    await EmitLog(emitSse,
                    "warn",
                    $"No valid edits parsed from attempt {attempt + 1} for {relPath}. Error: {rejectReason ?? err ?? "No edits in response"}",
                    editSteps, ct: ct);
                }
            }
        }

        if (timedOut || noEditsNeeded) { return; }
        if (editSteps.Count == 0)
        {
            await EmitLog(emitSse, "error", $"Could not produce edits for {relPath} — skipping", ct: ct);
            return;
        }

        for (var i = 0; i < editSteps.Count; i++)
        {
            editSteps[i].Index = i;
        }

        // ── Cross-file reference detection ─────────────────────────────────────
        var crossFileRefs = await DetectCrossFileReferences(editSteps, projectRoot, relPath, ct);
        if (crossFileRefs.Count > 0)
        {
            await EmitLog(emitSse, "info",
                $"Cross-file references detected — consider editing: {string.Join(", ", crossFileRefs)}", ct: ct);
        }

        var batchResults = await ExecuteSteps(editSteps, projectRoot, idx, emitSse, ct);
        idx += batchResults.Count;
        allResults.AddRange(batchResults);

        var fileEdited = batchResults.Any(r =>
            r is Dictionary<string, object?> d &&
            d.TryGetValue("status", out var st) && st?.ToString() == "done");

        var appliedEdit = fileEdited;
        for (var retryAttempt = 0; !appliedEdit && retryAttempt < 2 && System.IO.File.Exists(fullPath); retryAttempt++)
        {
            await EmitLog(emitSse, "warn",
                $"All edits failed for {relPath} — apply retry {retryAttempt + 1}", ct: ct);

            var freshContent = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8);

            // Collect near-match snippets reported by TryReplace failures.
            var nearMatches = batchResults
                .OfType<Dictionary<string, object?>>()
                .Where(r => r.TryGetValue("status", out var st) && st?.ToString() == "error")
                .Select(r => r.TryGetValue("snippet", out var sn) ? sn?.ToString() : null)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            var (retryRaw, _, _) = await CallLlmSingleFileEdit(
                fileTask, relPath, freshContent, projectRoot,
                attempt: 2 + retryAttempt,      // signals RETRY to the prompt
                discoveryContext,
                ct,
                nearMatchSnippets: nearMatches!);

            var retrySteps = ParseEditsFromLlmRaw(retryRaw, relPath, out var _, out var _)
                .Where(e => !string.Equals(
                    AgentUtilities.NormalizeLineEndings(e.OldString ?? ""),
                    AgentUtilities.NormalizeLineEndings(e.NewString ?? ""),
                    StringComparison.Ordinal))
                .ToList();

            if (retrySteps.Count > 0)
            {
                for (var i = 0; i < retrySteps.Count; i++) retrySteps[i].Index = i;
                var retryResults = await ExecuteSteps(retrySteps, projectRoot, idx, emitSse, ct);
                idx += retryResults.Count;
                allResults.AddRange(retryResults);
                appliedEdit = retryResults.Any(r =>
                    r is Dictionary<string, object?> rd &&
                    rd.TryGetValue("status", out var rs) && rs?.ToString() == "done");
                batchResults = retryResults; // feed back for near-match collection
            }
        }

        // ── Build verification after each successful edit ──────────────
        if (appliedEdit)
        {
            editHistory.Add((relPath, fileContent));

            var config = await _configFile.LoadConfigAsync();
            var buildCmd = config.buildCommands;
            if (!string.IsNullOrWhiteSpace(buildCmd))
            {
                var (buildOk, _) = await RunQuickBuild(projectRoot, buildCmd, emitSse, ct);

                // for (var retryLoop = 0; retryLoop < 2 && !buildOk && editHistory.Count > 0; retryLoop++)
                // {
                //     // Undo edits (up to 2) until build passes
                //     var undoneCount = 0;
                //     while (undoneCount < 2 && editHistory.Count > 0 && !buildOk)
                //     {
                //         var (undoPath, undoContent) = editHistory[^1];
                //         editHistory.RemoveAt(editHistory.Count - 1);
                //         var undoFullPath = Path.GetFullPath(
                //             Path.Combine(projectRoot, undoPath.Replace('/', Path.DirectorySeparatorChar)));
                //         if (System.IO.File.Exists(undoFullPath))
                //         {
                //             await System.IO.File.WriteAllTextAsync(undoFullPath, undoContent, Encoding.UTF8);
                //             await EmitLog(emitSse, "warn", $"Undid edit: {undoPath}", ct: ct);
                //         }
                //         undoneCount++;
                //         (buildOk, _) = await RunQuickBuild(projectRoot, buildCmd, emitSse, ct);
                //     }

                //     if (buildOk && undoneCount > 0)
                //     {
                //         // Build passes with undo — retry the original file edit
                //         await EmitLog(emitSse, "info", $"Build passes after undo — retrying edit for {relPath}", ct: ct);

                //         var currentContent = System.IO.File.Exists(fullPath)
                //             ? await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8)
                //             : fileContent;

                //         var (retryRaw2, _, _) = await CallLlmSingleFileEdit(
                //             fileTask, relPath, currentContent, projectRoot, 2, discoveryContext, ct);

                //         var retrySteps2 = ParseEditsFromLlmRaw(retryRaw2, relPath, out var _, out var _)
                //             .Where(e => !string.Equals(
                //                 AgentUtilities.NormalizeLineEndings(e.OldString ?? ""),
                //                 AgentUtilities.NormalizeLineEndings(e.NewString ?? ""),
                //                 StringComparison.Ordinal))
                //             .ToList();

                //         if (retrySteps2.Count > 0)
                //         {
                //             for (var i = 0; i < retrySteps2.Count; i++) retrySteps2[i].Index = i;
                //             var retryResults2 = await ExecuteSteps(retrySteps2, projectRoot, idx, emitSse, ct);
                //             idx += retryResults2.Count;
                //             allResults.AddRange(retryResults2);

                //             (buildOk, _) = await RunQuickBuild(projectRoot, buildCmd, emitSse, ct);
                //         }
                //     }
                // }

                // if (!buildOk)
                // {
                //     await EmitLog(emitSse, "warn",
                //         $"Build failing after edit/retry for {relPath} — restoring and skipping", ct: ct);
                //     // Restore current file to pre-edit state
                //     await System.IO.File.WriteAllTextAsync(fullPath, fileContent, Encoding.UTF8);
                //     editHistory.RemoveAll(e => e.path == relPath);
                //     return;
                // }
            }
        }
    }


    [HttpPost("execute")]
    public async Task<IActionResult> Execute([FromBody] AgentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt))
            return BadRequest("Prompt is required");

        var projectRoot = GetProjectRoot(req.Project);

        var (allSteps, summary, complete, thinking) = await Orchestrate(req.Prompt, projectRoot, emitSse: false);

        return Ok(new
        {
            summary,
            thinking,
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
            string summary, thinking;
            bool complete;
            // ── Full phased pipeline ────────────────────────────────────
            (allSteps, summary, complete, thinking) =
                await Orchestrate(req.Prompt, projectRoot, emitSse: true, ct: Response.HttpContext.RequestAborted,
                    attachedFiles: req.Files?.Count > 0 ? req.Files : null);

            var filesEdited = ExtractFilesEdited(allSteps);
            var editsApplied = AgentUtilities.HasSuccessfulEdits(allSteps);

            await SendSse(Response, "done", new
            {
                summary,
                thinking,
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
        TimeSpan? requestTimeout = null)
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
        return await CallLlmNonStreaming(client, baseUrl + "/v1/chat/completions", model, messages, linkedCts.Token);
    }

    private async Task<(string raw, AgentResponse? parsed, string? error)> CallLlmSingleFileEdit(
         string taskPrompt, string relativePath, string fileContent,
         string projectRoot, int attempt = 0, string? discoveryContext = null,
         CancellationToken ct = default,
         IEnumerable<string>? nearMatchSnippets = null)
    {
        // ── Use full file content so LLM can locate the right code ──────────
        var fileForLlm = fileContent;

        // ── Build user message ─────────────────────────────────────────────────
        var user = new StringBuilder();
        user.AppendLine($"Task: {taskPrompt}");
        user.AppendLine($"File: {relativePath}");
        user.AppendLine();
        user.AppendLine("Below is the ENTIRE file. Find the exact code that needs to change and plan small targeted edits.");
        user.AppendLine();
        user.AppendLine("## FULL FILE CONTENT:");
        user.AppendLine("```");
        user.AppendLine(fileForLlm);
        user.AppendLine("```");

        if (attempt > 0)
        {
            user.AppendLine();
            user.AppendLine("## RETRY — your previous attempt failed because:");
            user.AppendLine("  • The edits you returned could not be matched in the file.");
            user.AppendLine("  • oldString was too long or didn't exist literally in the code.");
            user.AppendLine();
            user.AppendLine("Fix:");
            user.AppendLine("  1. Each 'oldString' must be SHORT (1-5 lines max) and literally present in the code above.");
            user.AppendLine("  2. Prefer MANY small edits over one large edit.");
            user.AppendLine("  3. Double-check spelling and whitespace in oldString.");

            var hints = nearMatchSnippets?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (hints?.Count > 0)
            {
                user.AppendLine();
                user.AppendLine("## Context lines near where a previous edit failed:");
                foreach (var h in hints.Take(3))
                {
                    user.AppendLine("```");
                    user.AppendLine(h.Trim());
                    user.AppendLine("```");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(discoveryContext))
        {
            user.AppendLine();
            user.AppendLine("## Project context");
            user.AppendLine(discoveryContext);
        }

        // ── LLM call ───────────────────────────────────────────────────────────
        try
        {
            var baseUrl = await GetLlamaBaseUrl();
            var model = _config.GetValue<string>("Ai:Model") ?? "medgemma:4b";
            var client = _clientFactory.CreateClient("llama");
            client.Timeout = _infiniteTimeout;

            var systemMsg = @"You are a precise code modifier. Given code and a task, return a JSON object with an 'edits' array.
Each edit must have:
  - 'oldString': the EXACT existing code to replace (1-5 lines, must literally appear in the file)
  - 'newString': the replacement code
  - 'path' (optional): file path (omit unless different from context)

RULES:
  - oldString MUST be SHORT (1-5 lines max) and literally present in the code shown.
  - oldString and newString must NOT be the same.
  - CRITICAL: Prefer MANY small targeted edits over one large edit. Add more edits if needed to cover the change. Make code edits as small as possible 1-5 lines MAX.
  - Preserve indentation exactly in both oldString and newString.
  - Return ONLY valid JSON, no markdown fences, no explanation.
  - If no changes are needed, return: {""edits"": []}

Example:
{""edits"":[
    {""oldString"":""<button class=\""foo\"">"",""newString"":""<button class=\""foo bar\"">""},
    {""oldString"":""<div class=\""foo2\"">"",""newString"":""<div class=\""foo2 bar2\"">""}
]}";

            var messages = new object[]
            {
                new { role = "system", content = systemMsg },
                new { role = "user",   content = user.ToString() }
            };

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var reqBody = new { model, messages, stream = false, temperature = 0.0, max_tokens = MaxFileContextChars / 2 };
            var httpContent = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");

            var resp = await client.PostAsync(baseUrl + "/v1/chat/completions", httpContent, linkedCts.Token);
            var respText = await resp.Content.ReadAsStringAsync(linkedCts.Token);
            var raw = ExtractLlmContent(respText);

            if (string.IsNullOrWhiteSpace(raw))
                return (respText, null, "Empty LLM response");

            var edited = raw.Trim();
            if (edited.StartsWith("```"))
            {
                var m = Regex.Match(edited, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (m.Success) edited = m.Groups[1].Value.Trim();
            }

            return (edited, null, null);
        }
        catch (TaskCanceledException)
        {
            return ("", null, $"Timed out editing {relativePath}");
        }
        catch (Exception ex)
        {
            return ("", null, ex.Message);
        }
    }

    private async Task<(string raw, AgentResponse? parsed, string? error)> CallLlmNonStreaming(
        HttpClient client, string target, string model, object messages, CancellationToken ct = default)
    {
        var requestBody = new { model, messages, stream = false, temperature = 0.05, max_tokens = 2048 };
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

        var (replaced, newContent, matchError, snippet) = TryReplace(content, oldString, newString);
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

    /// <summary>
    /// Quick build check — runs the build command and returns success/failure.
    /// Does NOT attempt LLM-based fix analysis (unlike RunBuildVerification).
    /// </summary>
    private async Task<(bool success, string freshOutput)> RunQuickBuild(string projectRoot, string buildCmd, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(buildCmd)) return (true, "");
        _terminal.Start();
        var beforeLen = _terminal.ReadAll().Length;
        await EmitLog(emitSse, "info", $"Build check: {buildCmd}", ct: ct);
        await _terminal.SendCommandAsync(buildCmd, projectRoot);
        var prevLen = beforeLen;
        var stableMs = 0;
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            var curLen = _terminal.ReadAll().Length;
            if (curLen == prevLen) { stableMs += 500; if (stableMs >= 2000) break; }
            else { stableMs = 0; prevLen = curLen; }
        }
        var output = _terminal.ReadAll();
        var fresh = beforeLen >= 0 && beforeLen < output.Length
            ? output.Substring(beforeLen) : "";
        var success = fresh.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase) ||
                      fresh.Contains("0 Error(s)", StringComparison.Ordinal) ||
                      fresh.Contains("0 errors", StringComparison.OrdinalIgnoreCase) ||
                      fresh.Contains("successfully", StringComparison.OrdinalIgnoreCase) ||
                      !fresh.Contains("error", StringComparison.OrdinalIgnoreCase);
        await EmitLog(emitSse, success ? "success" : "warn",
            success ? "Build passes" : "Build failed",
            new { output = fresh }, ct: ct);
        return (success, fresh);
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


    /// <summary>
    /// Executes a "need more info" request from the LLM during file editing.
    /// Supports free-text requests, "grep: ..." and "read: ..." prefixes for precision.
    /// Returns formatted output to append to the discovery context.
    /// </summary>
    private async Task<string> ExecuteInfoRequest(string request, string projectRoot,
        string currentFile, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request)) return "";

        request = request.Trim();
        var result = new Dictionary<string, object?>();
        var step = new AgentStep { Index = 0 };

        // Parse structured prefixes
        if (request.StartsWith("grep:", StringComparison.OrdinalIgnoreCase))
        {
            var query = request.Substring(5).Trim();
            step.Type = "grep";
            step.Query = query;
            step.Description = $"Info request: grep for '{query}'";
            await EmitLog(emitSse, "step", $"🔍 {step.Description}", ct: ct);
            await ExecuteGrepStep(step, projectRoot, result);
        }
        else if (request.StartsWith("read:", StringComparison.OrdinalIgnoreCase))
        {
            var path = request.Substring(5).Trim();
            step.Type = "read";
            step.Path = path;
            step.Description = $"Info request: read '{path}'";
            await EmitLog(emitSse, "step", $"📖 {step.Description}", ct: ct);
            await ExecuteReadStep(step, projectRoot, result);
        }
        else
        {
            // Free-text: treat as grep query
            step.Type = "grep";
            step.Query = request;
            step.Description = $"Info request: search for '{request}'";
            await EmitLog(emitSse, "step", $"🔍 {step.Description}", ct: ct);
            await ExecuteGrepStep(step, projectRoot, result);
        }

        // Format output for discovery context
        var output = result.TryGetValue("output", out var o) ? o?.ToString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(output))
            output = result.TryGetValue("error", out var e) ? e?.ToString() ?? "" : "";

        if (!string.IsNullOrWhiteSpace(output))
        {
            // Emit the result so the user can see what was found
            await EmitLog(emitSse, "debug",
                $"Info request result ({output.Length} chars)", new { output }, ct: ct);
            var label = $"## Additional context (info request: {request})";
            return $"\n{label}\n{output}";
        }

        return "";
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
                if (!fileExists && string.IsNullOrEmpty(edit.OldString)) { content = edit.NewString ?? ""; continue; }
                if (string.IsNullOrEmpty(edit.OldString)) { content += edit.NewString ?? ""; continue; }
                var (ok, newContent, err, _) = TryReplace(content, edit.OldString, edit.NewString ?? "");
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
 
    /// <summary>
    /// Reads back edited files and asks the LLM whether the task is truly complete.
    /// This lets the agent review its work and either conclude or loop back for more edits.
    /// </summary>
    private async Task<(bool complete, string? feedback)> RunContentReview(
        string prompt, List<object> allSteps, string projectRoot,
        bool emitSse, CancellationToken ct = default)
    {
        var editedPaths = allSteps
            .OfType<Dictionary<string, object?>>()
            .Where(s =>
            {
                if (!s.TryGetValue("type", out var t) || t?.ToString() != "edit") return false;
                if (!s.TryGetValue("status", out var st) || st?.ToString() != "done") return false;
                return s.TryGetValue("path", out var p) && p != null && !string.IsNullOrWhiteSpace(p?.ToString());
            })
            .Select(s => s["path"]?.ToString() ?? "")
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        if (editedPaths.Count == 0)
            return (false, "No files were edited");

        var sb = new StringBuilder();
        sb.AppendLine("## Task");
        sb.AppendLine(prompt);
        sb.AppendLine();
        sb.AppendLine("## Current file contents after edits");
        foreach (var relPath in editedPaths)
        {
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
            if (System.IO.File.Exists(fullPath))
            {
                var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8);
                sb.AppendLine($"### {relPath}");
                sb.AppendLine("```");
                sb.AppendLine(content);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        await EmitLog(emitSse, "info", "Reviewing edited files for completeness…", ct: ct);

        const string reviewSystemPrompt = @"You are a code review agent. Given a task and the current file contents, determine if the task is COMPLETE.

Output ONLY valid JSON (no other text, no markdown fences):
{
  ""complete"": true/false,
  ""feedback"": ""If incomplete, describe exactly what still needs to be changed and in which file. Be specific.""
}

CRITICAL: Property names MUST be double-quoted (like ""complete"": true), not bare names.
Do NOT use trailing commas.

Rules:
- If ALL changes described in the task have been made, complete=true
- If the task is ONLY partially implemented or incorrectly implemented, complete=false and explain what's missing
- Do NOT suggest new features or improvements beyond the original task
- Only review what the task explicitly asks for
- Be honest — if the task is not done, say so";

        var (raw, _, error) = await CallLlmRaw(reviewSystemPrompt, sb.ToString(), ct);

        if (string.IsNullOrWhiteSpace(raw))
            return (false, error ?? "Verification call returned empty");

        var (complete, feedback) = AgentUtilities.TryParseReviewResponse(raw);
        if (complete == null)
        {
            await EmitLog(emitSse, "error", $"Review parse failed — {feedback}", ct: ct);
            return (false, feedback ?? "Failed to parse review response");
        }

        if (complete.Value)
            await EmitLog(emitSse, "info", "Content review: task is complete ✓", ct: ct);
        else
            await EmitLog(emitSse, "warn", $"Content review: incomplete — {feedback}", ct: ct);

        return (complete.Value, feedback);
    }

    private static bool IsTaskAlreadySatisfied(string prompt, string relativePath, string content)
    {
        var task = prompt.ToLowerInvariant();
        if (!task.Contains("terminal") && !task.Contains("show") && !task.Contains("hide")) return false;
        var path = relativePath.Replace('\\', '/').ToLowerInvariant();
        if (path.EndsWith("index.html"))
            return content.Contains("vm.showTerminal", StringComparison.OrdinalIgnoreCase) &&
                   content.Contains("ng-if=\"vm.showTerminal\"", StringComparison.OrdinalIgnoreCase);
        if (path.EndsWith("app.js"))
            return content.Contains("vm.showTerminal", StringComparison.OrdinalIgnoreCase) &&
                   content.Contains("cfg.showTerminal", StringComparison.OrdinalIgnoreCase);
        if (path.EndsWith("maestroconfig.json"))
            return content.Contains("showTerminal", StringComparison.OrdinalIgnoreCase);
        return false;
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
            var parsed = JsonSerializer.Deserialize<AgentResponse>(jsonStr,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed != null && parsed.Steps.Count > 0) return parsed;
        }
        catch { }

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

    private static string? EscapeUnescapedQuotesInStrings(string json)
    {
        var sb = new StringBuilder(json.Length);
        bool inString = false;
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];

            if (c == '"')
            {
                // Check if this quote is escaped
                bool escaped = i > 0 && json[i - 1] == '\\';
                if (!escaped)
                    inString = !inString;
                sb.Append(c);
            }
            else if (c == '"' && inString)
            {
                // Inside a string, check for unescaped quotes that need escaping
                if (i + 1 < json.Length && json[i + 1] == '"')
                {
                    // Double quotes inside string, escape them
                    sb.Append('\\').Append('"');
                    i++; // skip next quote
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
    private static List<AgentStep> ParseEditsFromLlmRaw(string? raw, string defaultPath, out bool noEditsSignal, out string needMoreInfo)
    {
        noEditsSignal = false;
        needMoreInfo = "";
        var steps = new List<AgentStep>();
        if (string.IsNullOrWhiteSpace(raw)) return steps;
        var processedRaw = raw;
        try
        {
            var escapedRaw = EscapeUnescapedQuotesInStrings(raw);
            if (!string.IsNullOrEmpty(escapedRaw))
                processedRaw = escapedRaw;
        }
        catch
        {
            // fallback: ignore errors
        }

        var jsonStr = processedRaw.Trim();
        var agent = ParseAgentResponse(raw);
        if (agent?.Steps != null)
        {
            foreach (var s in agent.Steps)
            {
                if (!string.Equals(s.Type, "edit", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrEmpty(s.OldString) && string.IsNullOrEmpty(s.NewString)) continue;
                s.Type = "edit";
                s.Path = string.IsNullOrWhiteSpace(s.Path) ? defaultPath : s.Path;
                steps.Add(s);
            }
            if (steps.Count > 0) return steps;
        }

        if (jsonStr.StartsWith("```"))
        {
            var m = Regex.Match(jsonStr, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (m.Success) jsonStr = m.Groups[1].Value.Trim();
        }

        var jsonOptions = new JsonDocumentOptions { AllowTrailingCommas = true };
        var repaired = AgentUtilities.RepairJsonString(jsonStr) ?? jsonStr;
        var blocks = AgentUtilities.ExtractJsonBlocks(repaired);

        foreach (var block in blocks)
        {
            foreach (var candidate in new[] { block, AgentUtilities.RepairJsonString(block) })
            {
                if (candidate == null) continue;
                try
                {
                    using var doc = JsonDocument.Parse(candidate, jsonOptions);
                    var root = doc.RootElement;

                    // LLM can request more info instead of making edits
                    if (root.TryGetProperty("needMoreInfo", out var nmiEl))
                    {
                        if (nmiEl.ValueKind == JsonValueKind.String)
                        {
                            needMoreInfo = nmiEl.GetString() ?? "";
                        }
                        else if (nmiEl.ValueKind == JsonValueKind.Object)
                        {
                            if (nmiEl.TryGetProperty("target", out var tEl))
                                needMoreInfo = tEl.GetString() ?? "";
                            if (string.IsNullOrWhiteSpace(needMoreInfo) && nmiEl.TryGetProperty("reason", out var rEl))
                                needMoreInfo = rEl.GetString() ?? "";
                        }
                        if (!string.IsNullOrWhiteSpace(needMoreInfo))
                            return steps;
                    }

                    if (root.TryGetProperty("edits", out var editsEl) && editsEl.ValueKind == JsonValueKind.Array)
                    {
                        // Explicit {"edits": []} means no changes needed — stop retrying
                        if (editsEl.GetArrayLength() == 0)
                        {
                            noEditsSignal = true;
                            return steps;
                        }

                        var envelope = JsonSerializer.Deserialize<MinimalEditsEnvelope>(candidate,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (envelope?.Edits != null)
                        {
                            var i = 0;
                            foreach (var foundEdit in envelope.Edits)
                            {
                                if (string.IsNullOrEmpty(foundEdit.OldString) && string.IsNullOrEmpty(foundEdit.NewString)) continue;
                                steps.Add(new AgentStep
                                {
                                    Index = i++,
                                    Type = "edit",
                                    Path = string.IsNullOrWhiteSpace(foundEdit.Path) ? defaultPath : foundEdit.Path,
                                    OldString = foundEdit.OldString,
                                    NewString = foundEdit.NewString,
                                    Description = "LLM edit"
                                });
                            }
                            if (steps.Count > 0) return steps;
                        }
                    } 
                    if (root.TryGetProperty("oldString", out var os) && root.TryGetProperty("newString", out var ns))
                    {
                        steps.Add(new AgentStep
                        {
                            Index = 0,
                            Type = "edit",
                            Path = defaultPath,
                            OldString = os.GetString() ?? "",
                            NewString = ns.GetString() ?? "",
                            Description = "LLM edit"
                        });
                        if (steps.Count > 0) return steps;
                    }
                }
                catch { }
            }
        }

        if (steps.Count == 0)
            steps = AgentUtilities.ExtractEditPairs(repaired, defaultPath);

        return steps;
    }

    private async Task<PipelineType?> TryClassifyWithLlm(string prompt, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return null;
        var systemPrompt = @"Classify the user request into exactly one pipeline type. Respond with JSON only: {""pipeline"": ""<type>""}

Types: 
- CommandExecution: anything that should be done in terminal, including git operations, directory listing, renames, system info queries, ping, network scanning, package installation, process management, file content display (cat/type), and any check/verify that does not imply file changes.
- CodeEdit: modify files, add features, fix bugs, refactor, implement (any content change)

If unsure, use CodeEdit. Pay special attention if the user pasted a diff, logs or code snippet — that strongly implies CodeEdit. Do NOT use CommandExecution for code snippets.";

        var (raw, _, err) = await CallLlmRaw(systemPrompt, prompt, ct, requestTimeout: _infiniteTimeout);
        if (string.IsNullOrWhiteSpace(raw))
        {
            await EmitLog(emitSse, "warn", "LLM failed to classify the prompt.", new { prompt, raw, err }, ct: ct);
            return null;
        }
        await EmitLog(emitSse, "info", "LLM classified the prompt.", new { prompt, raw, err }, ct: ct);

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var pipelineStr = doc.RootElement.TryGetProperty("pipeline", out var p) ? p.GetString() : null;
            return pipelineStr switch
            {
                "CommandExecution" => PipelineType.CommandExecution,
                "CodeEdit" => PipelineType.CodeEdit,
                _ => null
            };
        }
        catch { return null; }
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

    // ═════════════════════════════════════════════════════════════════════════
    //  MULTI-PHASE ROBUST EDITING PIPELINE
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs the multi-phase pipeline: Deep Analysis → Detailed Plan → Code Generation → Review → Conversion.
    /// Returns a list of AgentStep edits ready to execute, or empty list if the pipeline failed.
    /// </summary>
    private async Task<List<AgentStep>> RunMultiPhasePipeline(
        string taskPrompt, string relativePath, string fileContent,
        string discoveryContext, string projectRoot, bool emitSse,
        CancellationToken ct)
    {
        // ── Phase 1: Deep Analysis ─────────────────────────────────────────
        await SendSse(Response, "phase", new { phase = "deep-analyze", message = $"Analyzing {relativePath}…" }, ct);

        const string analysisSystemPrompt = @"You are a senior software engineer performing deep code analysis.

Analyze the following task and file content. Consider:
1. Architecture — how does this change fit into the existing code structure?
2. Design patterns — what patterns are appropriate?
3. Edge cases — what boundary conditions or error states must be handled?
4. Potential pitfalls — what could go wrong with a naive implementation?
5. Best practices — idiomatic code, naming, separation of concerns, error handling
6. Dependencies — what imports, services, or config changes are needed?

Output a concise analysis (2-5 paragraphs). Focus on what matters for implementing this specific change.";

        var analysisUser = $@"## Task
{taskPrompt}

## File
{relativePath}

## Full File Content
```
{fileContent}
```

## Project Context
{(string.IsNullOrWhiteSpace(discoveryContext) ? "(none)" : discoveryContext)}

Analyze deeply what needs to change and how to implement it correctly.";

        var (analysisRaw, analysisErr) = await CallLlmRawText(analysisSystemPrompt, analysisUser, ct);

        if (string.IsNullOrWhiteSpace(analysisRaw))
        {
            await EmitLog(emitSse, "warn",
                $"Phase 1 (Analysis) failed for {relativePath}: {analysisErr ?? "empty response"}", ct: ct);
            return new List<AgentStep>();
        }

        // Trim analysis to a reasonable context size
        var analysis = analysisRaw;
        await EmitLog(emitSse, "info", $"Phase 1 (Analysis) complete — {analysis.Length} chars", analysis, ct: ct);

        // ── Phase 2: Detailed Plan ─────────────────────────────────────────
        await SendSse(Response, "phase", new { phase = "detailed-plan", message = $"Planning changes for {relativePath}…" }, ct);

        const string planSystemPrompt = @"You are a senior software engineer creating a detailed implementation plan.

Based on the analysis provided, generate a step-by-step plan for implementing the required changes.
Each step should be a single, focused change (1-15 lines of code).

Output ONLY valid JSON with this exact structure (no markdown fences, no other text):
{
  ""thinking"": ""your reasoning about the overall approach"",
  ""steps"": [
    {
      ""description"": ""what this step does"",
      ""targetArea"": ""which part of the file to modify (method name, class, line range, or exact code context)"",
      ""changeType"": ""edit""
    }
  ]
}

Rules:
- changeType must be one of: ""edit"", ""append"" (add to end), ""prepend"" (add to beginning), ""create"" (new file content)
- Prefer MANY small focused steps (1-15 lines each) over one large step
- If any file creation is planned, create the file first before editing existing files. 
- Each step should be independently verifiable
- Keep descriptions clear and actionable";

        var planUser = $@"## Task
{taskPrompt}

## File
{relativePath}

## Full File Content
```
{fileContent}
```

## Deep Analysis
{analysis}

## Project Context
{(string.IsNullOrWhiteSpace(discoveryContext) ? "(none)" : discoveryContext)}

Generate a detailed implementation plan as JSON with a 'steps' array.";

        var (planRaw, planErr) = await CallLlmRawText(planSystemPrompt, planUser, ct,
            requestTimeout: TimeSpan.FromMinutes(10), maxTokens: MaxFileContextChars / 2);

        if (string.IsNullOrWhiteSpace(planRaw))
        {
            await EmitLog(emitSse, "warn",
                $"Phase 2 (Plan) failed for {relativePath}: {planErr ?? "empty response"}", ct: ct);
            return new List<AgentStep>();
        }

        var detailedSteps = ParseDetailedPlanSteps(planRaw);
        if (detailedSteps == null || detailedSteps.Count == 0)
        {
            await EmitLog(emitSse, "warn",
                $"Phase 2 (Plan) produced no steps for {relativePath}", ct: ct);
            return new List<AgentStep>();
        }

        await EmitLog(emitSse, "info",
            $"Phase 2 (Plan) complete — {detailedSteps.Count} steps", ct: ct);

        for (var i = 0; i < detailedSteps.Count; i++)
            detailedSteps[i].Index = i;

        // ── Build full plan overview for context chain ─────────────────────
        var planOverview = new StringBuilder();
        planOverview.AppendLine($"## Complete Plan ({detailedSteps.Count} steps)");
        for (var pi = 0; pi < detailedSteps.Count; pi++)
        {
            var ps = detailedSteps[pi];
            planOverview.AppendLine($"  Step {pi + 1}: {ps.Description} [{ps.TargetArea}] ({ps.ChangeType})");
        }
        var fullPlanText = planOverview.ToString();

        // ── Phase 3: Generate Code per Step ────────────────────────────────
        await SendSse(Response, "phase", new { phase = "generate-code", message = $"Generating code for {relativePath} ({detailedSteps.Count} steps)…" }, ct);

        const string codeGenSystemPrompt = @"You are generating ONE precise code edit for ONE step of a larger plan.

You are given:
- The FULL plan with ALL steps (so you know what other changes are being made)
- Which step THIS is (step N of M)
- What previous steps already changed
- The current state of the file

Your ONLY job: Output the oldString/newString for THIS step.

Output ONLY valid JSON (no markdown fences, no other text):
{
  ""oldString"": ""the EXACT code to replace (must literally appear in CURRENT file content)"",
  ""newString"": ""the replacement code""
}

CRITICAL — Do NOT violate these rules:
1. oldString MUST be 1-15 lines and LITERALLY appear in the CURRENT file content shown
2. Change ONLY the code in oldString — do NOT add/remove/modify anything else
3. Do NOT add imports, comments, error handling, or unrelated code outside oldString
4. oldString and newString must NOT be identical
5. If the step says ""edit"", oldString must be a real code block, not empty
6. For ""append"" changeType, set oldString to """" and newString to the code to append
7. For ""prepend"" changeType, set oldString to """" and newString to the code to prepend
8. For ""create"" changeType (new file), set oldString to """" and newString to the full file content
9. Preserve indentation exactly in both oldString and newString
10. If this step's change has already been applied by a previous step, return {""oldString"":"""",""newString"":""""} to skip";

        var snippets = new List<(DetailedPlanStep step, string? oldString, string? newString, string? error)>();
        var workingContent = fileContent;
        var changesLog = new StringBuilder();
        changesLog.AppendLine("## Changes Applied So Far");
        var changesApplied = 0;

        foreach (var step in detailedSteps)
        {
            await EmitLog(emitSse, "info", $"  Generating code: {step.Description}", ct: ct);

            var codeUser = $@"## Original Task
{taskPrompt}

## File
{relativePath}

## Analysis Context
{analysis}

{fullPlanText}

## This Step: Step {step.Index + 1} of {detailedSteps.Count}
Description: {step.Description}
Target Area: {step.TargetArea}
Change Type: {step.ChangeType}

{changesLog}

## Current File Content
```
{workingContent}
```

Generate the exact oldString and newString for THIS step only. Do NOT make changes belonging to other steps.";
 

            var (codeRaw, codeErr) = await CallLlmRawText(codeGenSystemPrompt, codeUser, ct,
                requestTimeout: TimeSpan.FromMinutes(10));

            if (string.IsNullOrWhiteSpace(codeRaw))
            {
                snippets.Add((step, null, null, codeErr ?? "Empty response"));
                await EmitLog(emitSse, "warn",
                    $"  Code gen failed for step {step.Index + 1}: {codeErr ?? "empty"}", ct: ct);
                continue;
            }

            var (oldStr, newStr, parseErr) = AgentUtilities.ExtractEditFromCodeGen(codeRaw);
            if (oldStr == null)
            {
                snippets.Add((step, null, null, parseErr));
                await EmitLog(emitSse, "warn",
                    $"  Code gen parse failed for step {step.Index + 1}: {parseErr}", ct: ct);
                continue;
            }

            // Skip if LLM returned empty (indicating nothing to do)
            if (string.IsNullOrEmpty(oldStr) && string.IsNullOrEmpty(newStr))
            {
                await EmitLog(emitSse, "info",
                    $"  Step {step.Index + 1}: no change needed (skipped)", ct: ct);
                continue;
            }

            // Validate the edit against current working content
            if (!string.IsNullOrEmpty(oldStr))
            {
                var (replaced, newContent, matchErr, _) = TryReplace(workingContent, oldStr, newStr ?? "");
                if (!replaced)
                {
                    snippets.Add((step, null, null, matchErr ?? "oldString not found in working content"));
                    await EmitLog(emitSse, "warn",
                        $"  Code gen step {step.Index + 1}: oldString does not match current content — {matchErr}", ct: ct);
                    continue;
                }
                workingContent = newContent;
            }
            else if (!string.IsNullOrEmpty(newStr))
            {
                workingContent = step.ChangeType == "prepend" ? newStr + workingContent : workingContent + newStr;
            }

            snippets.Add((step, oldStr, newStr, null));
            changesApplied++;
            changesLog.AppendLine($"  Step {step.Index + 1} ({step.Description}): replaced {oldStr?.Length ?? 0} chars with {newStr?.Length ?? 0} chars");
        }

        var successfulSnippets = snippets.Where(s => s.oldString != null).ToList();
        if (successfulSnippets.Count == 0)
        {
            await EmitLog(emitSse, "warn",
                $"Phase 3 (Code Gen) produced no valid code — all {snippets.Count} steps failed", ct: ct);
            return new List<AgentStep>();
        }

        await EmitLog(emitSse, "info",
            $"Phase 3 (Code Gen) complete — {successfulSnippets.Count}/{detailedSteps.Count} steps produced edits", ct: ct);

        // ── Phase 4: Review & Refine all edits together ─────────────────────
        await SendSse(Response, "phase", new { phase = "review-code", message = $"Reviewing {successfulSnippets.Count} edits for {relativePath}…" }, ct);

        const string reviewSystemPrompt = @"You are a meticulous code reviewer. Review ALL code changes for a file together.

Output ONLY valid JSON (no markdown fences, no other text):
{
  ""approved"": true/false,
  ""feedback"": ""if not approved, describe which edit needs fixing and why""
}

Check ALL edits collectively for:
1. Duplication — are any edits doing the same thing?
2. Conflicts — do any edits overlap or conflict with each other?
3. Correctness — does each edit do what the plan step describes?
4. oldString accuracy — will each oldString literally match the current file?
5. Scope — is each edit making ONLY the described change (no extra modifications)?
6. Integration — do the edits work together to complete the original task?";

        var finalEdits = new List<(string oldString, string newString, string description)>();
        var allEditsDesc = new StringBuilder();

        foreach (var (step, oldStr, newStr, _) in successfulSnippets)
        {
            allEditsDesc.AppendLine($"### Edit {step.Index + 1}: {step.Description}");
            allEditsDesc.AppendLine($"Target: {step.TargetArea}");
            allEditsDesc.AppendLine("oldString:");
            allEditsDesc.AppendLine("```");
            allEditsDesc.AppendLine(oldStr);
            allEditsDesc.AppendLine("```");
            allEditsDesc.AppendLine("newString:");
            allEditsDesc.AppendLine("```");
            allEditsDesc.AppendLine(newStr);
            allEditsDesc.AppendLine("```");
            allEditsDesc.AppendLine();
        }

        var reviewUser = $@"## Original Task
{taskPrompt}

## File
{relativePath}

## Complete Plan
{fullPlanText}

## All Generated Edits for This File
{allEditsDesc}

Review ALL edits above collectively. Are they correct, non-duplicating, and scoped to ONLY the described change?";

        var (reviewRaw, reviewErr) = await CallLlmRawText(reviewSystemPrompt, reviewUser, ct);

        if (!string.IsNullOrWhiteSpace(reviewRaw))
        {
            var (approved, feedback) = AgentUtilities.TryParseReviewResponse(reviewRaw);
            if (approved == true)
            {
                await EmitLog(emitSse, "info",
                    $"  Review approved all {successfulSnippets.Count} edits for {relativePath}", ct: ct);
            }
            else
            {
                await EmitLog(emitSse, "warn",
                    $"  Review feedback for {relativePath}: {feedback}", ct: ct);
            }
        }
        else if (!string.IsNullOrWhiteSpace(reviewErr))
        {
            await EmitLog(emitSse, "warn",
                $"  Review call failed for {relativePath}: {reviewErr}", ct: ct);
        }

        // Use all generated edits regardless of review outcome
        // (the editing pipeline's TryReplace will catch any literal mismatches)
        finalEdits = successfulSnippets
            .Where(s => s.oldString != null)
            .Select(s => (s.oldString ?? "", s.newString ?? "", s.step.Description))
            .ToList();

        await EmitLog(emitSse, "info",
            $"Phase 4 (Review) complete — {finalEdits.Count} edits approved", ct: ct);

        // ── Phase 5: Convert to AgentStep list ─────────────────────────────
        var editSteps = new List<AgentStep>();
        foreach (var (oldString, newString, description) in finalEdits)
        {
            if (string.IsNullOrEmpty(oldString) && string.IsNullOrEmpty(newString)) continue;
            editSteps.Add(new AgentStep
            {
                Type = "edit",
                Path = relativePath,
                OldString = oldString,
                NewString = newString,
                Description = description
            });
        }

        // ── Phase 6: Smart Build Validation ────────────────────────────────
        if (editSteps.Count > 0)
        {
            var cfg = await _configFile.LoadConfigAsync();
            var buildCmd = cfg.buildCommands;
            if (!string.IsNullOrWhiteSpace(buildCmd))
            {
                await SendSse(Response, "phase", new { phase = "build-check", message = $"Checking build for {relativePath}…" }, ct);
                await RunSmartBuildCheck(projectRoot, buildCmd, emitSse, ct);
            }
        }

        await EmitLog(emitSse, "info",
            $"Multi-phase pipeline complete: {editSteps.Count} edit(s) for {relativePath}", ct: ct);
        return editSteps;
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

    /// <summary>
    /// Parses a detailed plan JSON response into a list of DetailedPlanStep.
    /// </summary>
    private static List<DetailedPlanStep>? ParseDetailedPlanSteps(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Strip markdown fences
        var json = raw.Trim();
        if (json.StartsWith("```"))
        {
            var m = Regex.Match(json, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (m.Success) json = m.Groups[1].Value.Trim();
        }

        // Extract first JSON object
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        json = json.Substring(start, end - start + 1);

        // Try direct parse
        try
        {
            var deserialized = JsonSerializer.Deserialize<DetailedPlanDeserialized>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (deserialized?.steps != null && deserialized.steps.Count > 0)
            {
                return deserialized.steps.Select(s => new DetailedPlanStep
                {
                    Description = s.description ?? "",
                    TargetArea = s.targetArea ?? "",
                    ChangeType = string.IsNullOrWhiteSpace(s.changeType) ? "edit" : s.changeType
                }).ToList();
            }
        }
        catch { }

        // Fallback: try without the thinking wrapper — maybe they just gave steps array
        try
        {
            var steps = JsonSerializer.Deserialize<List<DetailedPlanStepDeserialized>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (steps != null && steps.Count > 0)
            {
                return steps.Select(s => new DetailedPlanStep
                {
                    Description = s.description ?? "",
                    TargetArea = s.targetArea ?? "",
                    ChangeType = string.IsNullOrWhiteSpace(s.changeType) ? "edit" : s.changeType
                }).ToList();
            }
        }
        catch { }

        // Fallback: repair and retry
        try
        {
            var repaired = AgentUtilities.RepairJsonString(json);
            if (repaired != null)
            {
                var deserialized = JsonSerializer.Deserialize<DetailedPlanDeserialized>(repaired,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (deserialized?.steps != null && deserialized.steps.Count > 0)
                {
                    return deserialized.steps.Select(s => new DetailedPlanStep
                    {
                        Description = s.description ?? "",
                        TargetArea = s.targetArea ?? "",
                        ChangeType = string.IsNullOrWhiteSpace(s.changeType) ? "edit" : s.changeType
                    }).ToList();
                }
            }
        }
        catch { }

        return null;
    } 
}
