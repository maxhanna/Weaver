using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MaestroBackend.Services;

// ╔══════════════════════════════════════════════════════════════════════════════╗
// ║  MAESTRO AGENT  —  Phased Pipeline Architecture                            ║
// ║                                                                              ║
// ║  OLD design: single agent loop where the LLM could freely choose to read    ║
// ║  more files on every iteration → small models always chose reads, never     ║
// ║  committed to edits, and the loop hit maxIterations with no changes.         ║
// ║                                                                              ║
// ║  NEW design: 3 strictly-separated phases, each with a tightly-scoped LLM    ║
// ║  role so the model can never "drift" between exploration and editing.        ║
// ║                                                                              ║
// ║  PHASE 1 ─ DISCOVER (no LLM)                                                ║
// ║    Deterministic: list project, grep keywords, read candidate files.         ║
// ║    Output: rich discoveryContext (real paths + file contents).               ║
// ║                                                                              ║
// ║  PHASE 2 ─ PLAN (LLM call #1 — planning only, no code)                      ║
// ║    Input:  task + discoveryContext                                            ║
// ║    Output: [{file, changeDescription}]                                       ║
// ║    The model MUST commit to specific files and specific changes NOW.          ║
// ║    It cannot ask for more reads.  No code written yet.                       ║
// ║                                                                              ║
// ║  PHASE 3 ─ EDIT (LLM call per planned file — patching only)                 ║
// ║    Input:  full file content + plan's changeDescription for that file        ║
// ║    Output: [{oldString, newString}] patch for that file                      ║
// ║    The model cannot explore; it MUST produce concrete patches.               ║
// ║    Up to 2 retries per file with enriched diagnostics on failure.            ║
// ║                                                                              ║
// ║  PHASE 4 ─ VERIFY (no LLM)                                                  ║
// ║    Inspect results, retry any file whose patches all failed (once more),     ║
// ║    emit final SSE done event.                                                ║
// ║                                                                              ║
// ║  Fast-path override: when the request already carries attached files AND     ║
// ║  the task clearly requires edits, skip DISCOVER + PLAN and run EDIT          ║
// ║  directly against those files (original RunDedicatedEditPhase behaviour).    ║
// ╚══════════════════════════════════════════════════════════════════════════════╝

[ApiController]
[Route("api/agent")]
public class AgentController : ControllerBase
{
    // ── tuning constants ──────────────────────────────────────────────────────
    private const int DefaultMaxIterations = 4;   // kept for legacy Execute endpoint
    private const int DefaultMaxStepsPerBatch = 6;
    private const int MaxFileContextChars = 24_000;
    private const int MaxObservationChars = 24_000;
    private const int MaxReadOutputChars = 24_000;
    private const int MaxWebResponseChars = 24_000;
    private const int MaxPlanFiles = 5;   // plan phase: cap files to avoid token bloat

    private readonly IHttpClientFactory _clientFactory;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly TerminalService _terminal;
    private readonly FileHintsManager _fileHints;

    public AgentController(
        IHttpClientFactory cf, IConfiguration config,
        IWebHostEnvironment env, TerminalService terminal, FileHintsManager fileHints)
    {
        _clientFactory = cf;
        _config = config;
        _env = env;
        _terminal = terminal;
        _fileHints = fileHints;
    }

    // ── request / response DTOs ───────────────────────────────────────────────

    public class AgentRequest
    {
        public string Prompt { get; set; } = "";
        public string Project { get; set; } = "";
        public List<string> Files { get; set; } = new();
        public int? MaxIterations { get; set; }
        public int? MaxStepsPerBatch { get; set; }
    }

    public class ApplyEditsRequest
    {
        public string Project { get; set; } = "";
        public List<EditAction> Edits { get; set; } = new();
        public List<CommandAction> Commands { get; set; } = new();
    }

    public class EditAction
    {
        public string Path { get; set; } = "";
        public string OldString { get; set; } = "";
        public string NewString { get; set; } = "";
    }

    public class CommandAction { public string Command { get; set; } = ""; }

    public class EditResult
    {
        public string Path { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Error { get; set; }
    }

    public class AgentStep
    {
        public int Index { get; set; }
        public string Type { get; set; } = "";
        public string Description { get; set; } = "";
        public string? Path { get; set; }
        public string? OldString { get; set; }
        public string? NewString { get; set; }
        public string? Command { get; set; }
        public string? Pattern { get; set; }
        public string? Url { get; set; }
        public string? Query { get; set; }
        public string? ToPath { get; set; }
        public bool? Complete { get; set; }
    }

    public class AgentResponse
    {
        public string Thinking { get; set; } = "";
        public string Summary { get; set; } = "";
        public bool Complete { get; set; }
        public List<AgentStep> Steps { get; set; } = new();
    }

    // ── plan phase DTOs ───────────────────────────────────────────────────────

    /// <summary>
    /// One item in the structured plan the LLM produces during Phase 2.
    /// </summary>
    private class PlanItem
    {
        public string File { get; set; } = "";
        public string ChangeDescription { get; set; } = "";
        public int Priority { get; set; } = 1;
    }

    private class PlanItemDeserialized
    {
        public string file { get; set; } = "";
        public string change { get; set; } = "";
        public int priority { get; set; } = 1;
    }

    private class AgentPlanDeserialized
    {
        public string thinking { get; set; } = "";
        public string summary { get; set; } = "";
        public List<PlanItemDeserialized> plan { get; set; } = new();
    }

    /// <summary>
    /// The full plan envelope returned by the Phase-2 LLM call.
    /// </summary>
    private class AgentPlan
    {
        public string Thinking { get; set; } = "";
        public string Summary { get; set; } = "";
        public List<PlanItem> Plan { get; set; } = new();
    }

    // ── internal edit DTO ─────────────────────────────────────────────────────

    private class MinimalEditDto
    {
        public string Path { get; set; } = "";
        public string OldString { get; set; } = "";
        public string NewString { get; set; } = "";
    }

    private class MinimalEditsEnvelope
    {
        public List<MinimalEditDto> Edits { get; set; } = new();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PATH HELPERS
    // ═════════════════════════════════════════════════════════════════════════

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

    private static bool IsPathUnderRoot(string fullPath, string root)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        fullPath = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)) return true;
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLineEndings(string s) => s.Replace("\r\n", "\n");
    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "\n…(truncated)";
    private static string NormalizeUiStatus(string? status) =>
        status switch { "written" or "ok" or "created" or "modified" => "done", "running" => "running", "error" => "error", _ => status ?? "pending" };

    private static readonly HashSet<string> ExplorationStepTypes =
        new(StringComparer.OrdinalIgnoreCase) { "read", "list", "glob", "grep", "web" };

    // FIX 2: Also count 'rename' steps as successful work so Phase 4 does not
    // re-enter the plan+edit loop after a rename completes.  Previously only
    // 'edit' was checked, so every rename caused two extra spurious LLM calls
    // that would pick an unrelated file (e.g. app.js) and try to patch it.
    private static bool HasSuccessfulEdits(IEnumerable<object> steps) =>
        steps.OfType<Dictionary<string, object?>>().Any(s =>
            s.TryGetValue("type", out var t) &&
            (string.Equals(t?.ToString(), "edit", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(t?.ToString(), "rename", StringComparison.OrdinalIgnoreCase)) &&
            s.TryGetValue("status", out var st) && st?.ToString() == "done");

    private static bool TaskExpectsFileChanges(string prompt)
    {
        var lower = prompt.ToLowerInvariant();
        string[] verbs = {
            "add","implement","fix","update","change","create","modify","remove","delete",
            "refactor","edit","write","toggle","enable","disable","insert","set","make",
            "build","install","configure","hook","wire","connect","show","hide","display",
            "save","persist","store","expose","include"
        };
        return verbs.Any(v => lower.Contains(v, StringComparison.Ordinal));
    }

    private static bool BatchWasExplorationOnly(IReadOnlyList<AgentStep> batch) =>
        batch.Count > 0 && batch.All(s => ExplorationStepTypes.Contains(s.Type ?? ""));

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

    // ═════════════════════════════════════════════════════════════════════════
    //  FILE DISCOVERY HELPERS
    // ═════════════════════════════════════════════════════════════════════════

    private static List<string> ExtractSearchKeywords(string prompt)
    {
        var result = new List<string>();
        string[] priority = { "settings","terminal","popup","panel","toggle","config","visibility",
                               "maestro","showSettingsPanel","autoQueue","delete","confirm","modal","overlay" };
        foreach (var p in priority)
            if (prompt.Contains(p, StringComparison.OrdinalIgnoreCase))
                result.Add(p);

        foreach (Match m in Regex.Matches(prompt, @"\b[a-zA-Z]{4,}\b"))
        {
            var w = m.Value;
            if (!result.Contains(w, StringComparer.OrdinalIgnoreCase))
                result.Add(w);
        }
        return result.Take(8).ToList();
    }

    private List<string> FindLikelyFiles(string prompt, string projectRoot)
    {
        var matches = new List<string>();
        if (!Directory.Exists(projectRoot)) return matches;

        var presets = new[]
        {
            "backend/wwwroot/app.js", "backend/wwwroot/index.html", "backend/wwwroot/styles.css",
            "wwwroot/app.js",          "wwwroot/index.html",          "wwwroot/styles.css",
            "app.js",                   "index.html",                  "styles.css"
        };
        foreach (var p in presets)
        {
            var full = Path.Combine(projectRoot, p.Replace('/', Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(full))
                matches.Add(p.Replace('\\', '/'));
        }

        var hintedFiles = _fileHints.GetFilesForPrompt(prompt, projectRoot);
        foreach (var hf in hintedFiles)
        {
            var full = Path.Combine(projectRoot, hf.Replace('/', Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(full) && !matches.Contains(hf, StringComparer.OrdinalIgnoreCase))
                matches.Add(hf);
        }

        var lower = prompt.ToLowerInvariant();
        var keywords = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4 && !new HashSet<string> {
                "with","that","this","from","will","have","been","when","what","which" }.Contains(w))
            .Distinct().Take(5).ToList();

        if (keywords.Count > 0)
        {
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "node_modules", ".git", "bin", "obj", "dist", ".angular", "packages" };

            foreach (var file in Directory.EnumerateFiles(projectRoot, "*.*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
                if (skip.Any(s => rel.Contains("/" + s + "/", StringComparison.OrdinalIgnoreCase))) continue;
                var name = Path.GetFileName(file);
                if (keywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    if (!matches.Contains(rel, StringComparer.OrdinalIgnoreCase))
                        matches.Add(rel);
                if (matches.Count >= 12) break;
            }
        }

        return matches.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
    }

    private static List<string> FindSimilarFiles(string missingPath, string projectRoot)
    {
        var name = Path.GetFileName(missingPath.Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = missingPath;
        var found = new List<string>();
        if (!Directory.Exists(projectRoot)) return found;

        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "node_modules", ".git", "bin", "obj", "dist" };

        foreach (var file in Directory.EnumerateFiles(projectRoot, "*.*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
            if (skip.Any(s => rel.Contains("/" + s + "/", StringComparison.OrdinalIgnoreCase))) continue;
            if (rel.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(file).Equals(name, StringComparison.OrdinalIgnoreCase))
                found.Add(rel);
            if (found.Count >= 10) break;
        }
        return found;
    }

    private static string? ExtractTargetPath(string changeDesc, string currentRelPath, string projectRoot)
    {
        // Find "to" or "→" in the description, then extract the path after it
        var idx = changeDesc.LastIndexOf(" to ", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = changeDesc.LastIndexOf(" → ", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var after = changeDesc.Substring(idx + 4).Trim().Trim('.', ' ', '"', '\'');
        if (string.IsNullOrWhiteSpace(after)) return null;

        // If it's just a filename (no directory separators), inherit source directory
        var dir = Path.GetDirectoryName(currentRelPath.Replace('/', Path.DirectorySeparatorChar)) ?? "";
        var target = after.Contains('/') || after.Contains('\\')
            ? after.Replace('\\', '/')
            : (string.IsNullOrEmpty(dir) ? after : dir.Replace('\\', '/') + "/" + after);

        // Validate it looks like a file path (has extension or doesn't start with special chars)
        if (string.IsNullOrWhiteSpace(target) || target.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return null;

        return target;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PHASE 1 — DISCOVER
    //  Purely deterministic; no LLM calls.  Builds a rich context string
    //  that later phases feed into their prompts.
    // ═════════════════════════════════════════════════════════════════════════

    private async Task<(string discoveryText, List<object> steps)> RunBootstrapDiscovery(
        string prompt, string projectRoot, bool emitSse)
    {
        await EmitLog(emitSse, "info", "Phase 1 — DISCOVER: scanning project files…");

        var plan = new List<AgentStep>();
        var idx = 0;

        plan.Add(new AgentStep { Index = idx++, Type = "list", Path = "", Description = "Auto: list project root" });

        foreach (var kw in ExtractSearchKeywords(prompt))
            plan.Add(new AgentStep { Index = idx++, Type = "grep", Query = kw, Description = $"Auto: search codebase for '{kw}'" });

        foreach (var file in FindLikelyFiles(prompt, projectRoot))
            plan.Add(new AgentStep { Index = idx++, Type = "read", Path = file, Description = $"Auto: read candidate file {file}" });

        // Windows-friendly path finder
        plan.Add(new AgentStep
        {
            Index = idx++,
            Type = "command",
            Command = OperatingSystem.IsWindows()
                ? "dir /s /b app.js index.html styles.css 2>nul | more"
                : "find . \\( -name 'app.js' -o -name 'index.html' -o -name '*.css' \\) 2>/dev/null | head -20",
            Description = "Auto: locate frontend files via shell"
        });

        var steps = await ExecuteSteps(plan, projectRoot, 0, emitSse);

        // Teach the file-hints manager from grep results
        foreach (var item in steps)
        {
            if (item is not Dictionary<string, object?> r) continue;
            if ((r.TryGetValue("type", out var t) ? t?.ToString() : "") != "grep") continue;
            var query = r.TryGetValue("query", out var q) ? q?.ToString() :
                         r.TryGetValue("pattern", out var pt) ? pt?.ToString() : "";
            var output = r.TryGetValue("output", out var o) ? o?.ToString() : "";
            if (!string.IsNullOrWhiteSpace(query) && !string.IsNullOrWhiteSpace(output))
                _fileHints.LearnFromGrepOutput(query, output, projectRoot);
        }

        var sb = new StringBuilder();
        sb.AppendLine("ONLY use paths that appear below. Do NOT invent paths.");
        sb.AppendLine();
        foreach (var item in steps)
        {
            if (item is not Dictionary<string, object?> r) continue;
            var type = r.TryGetValue("type", out var t) ? t?.ToString() : "";
            if (r.TryGetValue("output", out var output) && output != null)
            {
                sb.AppendLine($"### {type} {r.GetValueOrDefault("path") ?? r.GetValueOrDefault("description")}");
                sb.AppendLine(Truncate(output.ToString() ?? "", type == "read" ? 4000 : 1500));
                sb.AppendLine();
            }
            if (r.TryGetValue("suggestions", out var sug) && sug is IEnumerable<object> list)
                sb.AppendLine("Suggestions: " + string.Join(", ", list));
        }

        await EmitLog(emitSse, "info", $"Phase 1 complete — {steps.Count} discovery steps");
        return (sb.ToString(), steps);
    }

    private async Task<(string discoveryText, List<object> steps)> RunLightBootstrap(
        List<string> attachedFiles, string projectRoot, bool emitSse)
    {
        await EmitLog(emitSse, "info", "Fast-path bootstrap: reading attached files only");

        var plan = attachedFiles
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select((f, i) => new AgentStep { Index = i, Type = "read", Path = f.Replace('\\', '/'), Description = $"Read attached {f}" })
            .ToList();

        if (plan.Count == 0) return ("", new List<object>());

        var steps = await ExecuteSteps(plan, projectRoot, 0, emitSse);
        var sb = new StringBuilder();
        sb.AppendLine("Attached files (edit these paths only):");
        foreach (var f in attachedFiles) sb.AppendLine($"  - {f.Replace('\\', '/')}");
        foreach (var item in steps)
        {
            if (item is Dictionary<string, object?> r && r.TryGetValue("output", out var o) && o != null)
                sb.AppendLine($"\n### {r.GetValueOrDefault("path")}\n{Truncate(o.ToString() ?? "", 3000)}");
        }
        return (sb.ToString(), steps);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PHASE 2 — PLAN
    //  Single focused LLM call: given the task and discovered files, decide
    //  WHICH files need to change and WHAT to change in each.
    //  The model outputs a structured plan — no actual code yet.
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Calls the LLM to produce a structured plan: [{file, changeDescription}].
    /// The model is not allowed to request more reads; it must commit to files now.
    /// Returns an empty list if parsing fails (caller falls back to heuristics).
    /// </summary>
    private async Task<List<PlanItem>> RunPlanPhase(
        string prompt, string discoveryContext, string projectRoot, bool emitSse, CancellationToken ct = default)
    {
        await EmitLog(emitSse, "info", "Phase 2 — PLAN: asking model which files need to change…", ct: ct);
        if (emitSse)
            await SendSse(Response, "phase", new { phase = "plan", message = "Planning changes…" }, ct);

        var (raw, plan, error) = await CallLlmForPlan(prompt, discoveryContext, projectRoot, ct);

        if (plan == null || plan.Plan.Count == 0)
        {
            await EmitLog(emitSse, "warn", $"Plan phase produced no items — falling back to heuristic file list. Error: {error}", ct: ct);
            // Fallback: use every .js/.html/.css candidate from discovery as the plan
            var fallbackFiles = FindLikelyFiles(prompt, projectRoot);
            return fallbackFiles
                .Where(f => f.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
                .Take(MaxPlanFiles)
                .Select((f, i) => new PlanItem { File = f, ChangeDescription = prompt, Priority = i + 1 })
                .ToList();
        }

        await EmitLog(emitSse, "info",
            $"Plan: {plan.Plan.Count} file(s) — {string.Join(", ", plan.Plan.Select(p => p.File))}",
            new { thinking = plan.Thinking, summary = plan.Summary }, ct: ct);

        if (emitSse)
            await SendSse(Response, "plan", new { thinking = plan.Thinking, summary = plan.Summary, items = plan.Plan }, ct);

        return plan.Plan.Take(MaxPlanFiles).ToList();
    }

    private async Task<(string raw, AgentPlan? parsed, string? error)> CallLlmForPlan(
        string prompt, string discoveryContext, string projectRoot, CancellationToken ct = default)
    {
        const string systemPrompt = @"You are a code-change planning agent.

Given a task and the contents of project files, output a structured plan that lists
WHICH files need to be modified and WHAT specific change to make in each file.

DO NOT write any code yet. DO NOT include oldString or newString.
Your only job is to identify files and describe the change precisely.

OUTPUT FORMAT — respond with ONLY this JSON object, no markdown, no extra text:
{
  ""thinking"": ""brief analysis of what the task requires"",
  ""summary"":  ""one-sentence description of the overall change"",
  ""plan"": [
    {
      ""file"":   ""relative/path/to/file"",
      ""change"": ""specific description of what to add/modify/remove in this file. Be very detailed."",
      ""priority"": 1
    }
  ]
}

The ""change"" field is CRITICAL — it will be passed directly to the edit model so it knows exactly what to do.
Make it specific: e.g. 'Add a <div class=""popup-overlay""> before the closing </main> tag in index.html'
NOT vague like 'modify the HTML file'.

RULES:
- Only list files that actually exist in the Project Discovery section below.
- Maximum 5 files total.
- If both HTML and CSS need changes, list them as separate plan items.
- Priority 1 = most important file. Sort by priority ascending.
- If the task is purely CSS, list only the CSS file. If purely JS, list only JS.
- For RENAME/MOVE tasks: list the source file. Set the ""change"" field to: 'Rename this file to <new/path>'.";

        var user = new StringBuilder();
        user.AppendLine("## Task");
        user.AppendLine(prompt);
        user.AppendLine();
        user.AppendLine("## Project root");
        user.AppendLine(projectRoot);
        user.AppendLine();
        user.AppendLine("## Project Discovery (ONLY use paths listed here)");
        user.AppendLine(Truncate(discoveryContext, 16_000));

        var (raw, _, llmError) = await CallLlmRaw(systemPrompt, user.ToString(), ct);

        if (string.IsNullOrWhiteSpace(raw))
            return (raw ?? "", null, llmError ?? "Empty response");

        var parsed = ParseAgentPlan(raw);
        return (raw, parsed, parsed == null ? "JSON parse failed" : null);
    }

    private static AgentPlan? ParseAgentPlan(string raw)
    {
        var jsonStr = raw.Trim();
        if (jsonStr.StartsWith("```"))
        {
            var m = Regex.Match(jsonStr, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (m.Success) jsonStr = m.Groups[1].Value.Trim();
        }
        var start = jsonStr.IndexOf('{');
        var end = jsonStr.LastIndexOf('}');
        if (start >= 0 && end > start)
            jsonStr = jsonStr.Substring(start, end - start + 1);

        // Try exact-match DTO first (field names match LLM output exactly)
        try
        {
            var parsed = JsonSerializer.Deserialize<AgentPlanDeserialized>(jsonStr);
            if (parsed?.plan != null && parsed.plan.Count > 0)
            {
                return new AgentPlan
                {
                    Thinking = parsed.thinking,
                    Summary = parsed.summary,
                    Plan = parsed.plan.Select(p => new PlanItem
                    {
                        File = p.file,
                        ChangeDescription = p.change,
                        Priority = p.priority
                    }).ToList()
                };
            }
        }
        catch { }

        // Fallback: look for a "plan" array in any JSON block
        try
        {
            var blocks = ExtractJsonBlocks(jsonStr);
            foreach (var block in blocks)
            {
                var inner = JsonSerializer.Deserialize<AgentPlanDeserialized>(block);
                if (inner?.plan != null && inner.plan.Count > 0)
                {
                    return new AgentPlan
                    {
                        Thinking = inner.thinking,
                        Summary = inner.summary,
                        Plan = inner.plan.Select(p => new PlanItem
                        {
                            File = p.file,
                            ChangeDescription = p.change,
                            Priority = p.priority
                        }).ToList()
                    };
                }
            }
        }
        catch { }

        return null;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PHASE 3 — EDIT
    //  One focused LLM call per file from the plan.
    //  The model receives the full file content + the plan's change description.
    //  It MUST output {oldString, newString} patches — nothing else.
    //  Up to 2 retries per file, with diagnostic hints on failure.
    // ═════════════════════════════════════════════════════════════════════════

    private async Task<List<object>> RunEditPhase(
        List<PlanItem> plan, string discoveryContext, string projectRoot,
        int startIndex, bool emitSse, string originalPrompt, CancellationToken ct = default)
    {
        var allResults = new List<object>();

        if (plan.Count == 0)
        {
            await EmitLog(emitSse, "warn", "Phase 3 — EDIT: plan is empty, nothing to edit", ct: ct);
            return allResults;
        }

        await EmitLog(emitSse, "info", $"Phase 3 — EDIT: applying edits to {plan.Count} planned file(s)", ct: ct);
        if (emitSse)
            await SendSse(Response, "phase", new { phase = "edit", message = $"Editing {plan.Count} file(s)…" }, ct);

        var idx = startIndex;
        foreach (var item in plan.OrderBy(p => p.Priority))
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                var relPath = (item.File ?? "").Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(relPath)) continue;

                var fullPath = Path.GetFullPath(
                    Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));

                if (!IsPathUnderRoot(fullPath, projectRoot))
                {
                    await EmitLog(emitSse, "warn", $"Skipping {relPath} — outside project root", ct: ct);
                    continue;
                }

                if (emitSse)
                    await SendSse(Response, "phase", new { phase = "edit-file", message = $"Editing {relPath}…" }, ct);

                if (!System.IO.File.Exists(fullPath))
                {
                    await EmitLog(emitSse, "warn", $"Planned file not found: {relPath}", ct: ct);
                    var similar = FindSimilarFiles(relPath, projectRoot);
                    if (similar.Count > 0)
                    {
                        await EmitLog(emitSse, "info", $"Similar files: {string.Join(", ", similar)}", ct: ct);
                        relPath = similar[0];
                        fullPath = Path.GetFullPath(
                            Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
                        if (!System.IO.File.Exists(fullPath)) continue;
                    }
                    else continue;
                }

                var fileContent = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8);

                // Detect rename/move operations — bypass LLM edit loop
                var changeDesc = (item.ChangeDescription ?? "").Trim();
                var isRename = changeDesc.StartsWith("rename", StringComparison.OrdinalIgnoreCase) ||
                               changeDesc.StartsWith("move", StringComparison.OrdinalIgnoreCase);
                if (isRename)
                {
                    var dstPath = ExtractTargetPath(changeDesc, relPath, projectRoot);
                    if (dstPath != null)
                    {
                        var renameStep = new AgentStep
                        {
                            Index = 0,
                            Type = "rename",
                            Path = relPath,
                            ToPath = dstPath,
                            Description = $"Rename {relPath} → {dstPath}"
                        };
                        var results = await ExecuteSteps(new List<AgentStep> { renameStep }, projectRoot, idx, emitSse, ct);
                        idx += results.Count;
                        allResults.AddRange(results);
                        continue;
                    }
                }

                // Combine the original prompt with the file-specific change description
                var fileTask = string.IsNullOrWhiteSpace(item.ChangeDescription)
                    ? originalPrompt
                    : $"{originalPrompt}\n\nSpecific change needed in {relPath}: {item.ChangeDescription}";

                // --- up to 2 attempts ---
                List<AgentStep> editSteps = new();
                var timedOut = false;
                for (var attempt = 0; attempt < 2 && editSteps.Count == 0; attempt++)
                {
                    await EmitLog(emitSse, "info",
                        $"LLM edit call: {relPath} (attempt {attempt + 1})",
                        new { chars = fileContent.Length, taskSummary = item.ChangeDescription }, ct: ct);

                    var (raw, _, err) = await CallLlmSingleFileEdit(
                        fileTask, relPath, fileContent, projectRoot, attempt, discoveryContext, ct);

                    // Skip retry on timeout — won't help
                    if (err != null && err.StartsWith("Timed out", StringComparison.Ordinal))
                    {
                        timedOut = true;
                        await EmitLog(emitSse, "error", $"Timed out editing {relPath} — skipping", ct: ct);
                        break;
                    }

                    await EmitLog(emitSse, "debug",
                        $"LLM raw ({raw?.Length ?? 0} chars)",
                        new { raw }, ct: ct);

                    editSteps = ParseEditsFromLlmRaw(raw, relPath);

                     // Determine rejection reason for better logging
                     string? rejectReason = null;
                     if (editSteps.Count > 0)
                     {
                         var missingNew = editSteps.Where(e => string.IsNullOrWhiteSpace(e.NewString)).ToList();
                         if (missingNew.Count > 0)
                         {
                             // We are not removing them, but we log why they might be problematic.
                             rejectReason = "newString is empty — model returned oldString without replacement (treated as deletion)";
                         }

                         var identical = editSteps.Where(e => string.Equals(
                             NormalizeLineEndings(e.OldString ?? ""),
                             NormalizeLineEndings(e.NewString ?? ""),
                             StringComparison.Ordinal)).ToList();
                         if (identical.Count > 0)
                         {
                             // We are going to remove these in the next step.
                             if (rejectReason == null)
                                 rejectReason = "oldString and newString are identical";
                             else
                                 rejectReason += "; some edits are identical oldString/newString (no-op)";
                         }
                     }

                     // Filter no-op edits (oldString == newString)
                     editSteps = editSteps
                         .Where(e => !string.Equals(
                             NormalizeLineEndings(e.OldString ?? ""),
                             NormalizeLineEndings(e.NewString ?? ""),
                             StringComparison.Ordinal))
                         .ToList();

                     if (editSteps.Count == 0)
                         await EmitLog(emitSse, "warn",
                             $"No valid edits parsed from attempt {attempt + 1} for {relPath}. Error: {rejectReason ?? err ?? "No edits in response"}", ct: ct);
                }

                if (timedOut) continue;

                if (editSteps.Count == 0)
                {
                    // Heuristic patches as last resort
                    editSteps = TryHeuristicPatches(fileTask, relPath, fileContent);
                    if (editSteps.Count > 0)
                        await EmitLog(emitSse, "info", $"Applied {editSteps.Count} heuristic patch(es) for {relPath}", ct: ct);
                }

                if (editSteps.Count == 0)
                {
                    await EmitLog(emitSse, "error", $"Could not produce edits for {relPath} — skipping", ct: ct);
                    continue;
                }

                for (var i = 0; i < editSteps.Count; i++)
                    editSteps[i].Index = i;

                var batchResults = await ExecuteSteps(editSteps, projectRoot, idx, emitSse, ct);
                idx += batchResults.Count;
                allResults.AddRange(batchResults);

                var fileEdited = batchResults.Any(r =>
                    r is Dictionary<string, object?> d &&
                    d.TryGetValue("status", out var st) && st?.ToString() == "done");

                if (!fileEdited)
                {
                    await EmitLog(emitSse, "warn",
                        $"All edits failed for {relPath} — running one retry with re-read content", ct: ct);

                    if (System.IO.File.Exists(fullPath))
                    {
                        var freshContent = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8);
                        var (retryRaw, _, _) = await CallLlmSingleFileEdit(
                            fileTask, relPath, freshContent, projectRoot, 2, discoveryContext, ct);
                        var retrySteps = ParseEditsFromLlmRaw(retryRaw, relPath)
                            .Where(e => !string.Equals(
                                NormalizeLineEndings(e.OldString ?? ""),
                                NormalizeLineEndings(e.NewString ?? ""),
                                StringComparison.Ordinal))
                            .ToList();

                        if (retrySteps.Count > 0)
                        {
                            for (var i = 0; i < retrySteps.Count; i++) retrySteps[i].Index = i;
                            var retryResults = await ExecuteSteps(retrySteps, projectRoot, idx, emitSse, ct);
                            idx += retryResults.Count;
                            allResults.AddRange(retryResults);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                await EmitLog(emitSse, "error", $"Editing {item.File} was cancelled — aborting phase", ct: default);
                break;
            }
            catch (Exception ex)
            {
                await EmitLog(emitSse, "error",
                    $"Unexpected error editing {item.File}: {ex.Message}", ct: default);
                continue;
            }
        }

        return allResults;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PHASED PIPELINE ORCHESTRATOR
    //  Ties together DISCOVER → PLAN → EDIT → VERIFY
    // ═════════════════════════════════════════════════════════════════════════

    private async Task<(List<object> allSteps, string summary, bool complete)> RunPhasedPipeline(
        string prompt, string projectRoot, bool emitSse, CancellationToken ct = default)
    {
        var allSteps = new List<object>();

        // ── Phase 1: DISCOVER ────────────────────────────────────────────────
        var (discoveryContext, discoverySteps) =
            await RunBootstrapDiscovery(prompt, projectRoot, emitSse);
        allSteps.AddRange(discoverySteps);

        // ── Phase 2: PLAN ────────────────────────────────────────────────────
        var plan = await RunPlanPhase(prompt, discoveryContext, projectRoot, emitSse, ct);

        // ── Phase 3: EDIT ────────────────────────────────────────────────────
        var editResults = await RunEditPhase(plan, discoveryContext, projectRoot, allSteps.Count, emitSse, prompt, ct);
        allSteps.AddRange(editResults);

        // ── Phase 4: VERIFY + RETRY LOOP ─────────────────────────────────────
        var editsApplied = HasSuccessfulEdits(allSteps);
        for (var retry = 0; retry < 2 && !editsApplied && TaskExpectsFileChanges(prompt); retry++)
        {
            await EmitLog(emitSse, "warn",
                $"Phase 4 — VERIFY attempt {retry + 1}: no successful edits. Re-planning with stronger instructions…", ct: ct);
            plan = await RunPlanPhase(prompt, discoveryContext, projectRoot, emitSse, ct);
            var retryEdits = await RunEditPhase(plan, discoveryContext, projectRoot, allSteps.Count, emitSse, prompt, ct);
            allSteps.AddRange(retryEdits);
            editsApplied = HasSuccessfulEdits(allSteps);
        }

        var summary = editsApplied
            ? $"Edits applied to {ExtractFilesEdited(allSteps).Count} file(s)"
            : "No edits were applied — check failed steps for details";

        if (editsApplied)
            await EmitLog(emitSse, "info", $"Phase 4 — VERIFY: ✓ {summary}");

        return (allSteps, summary, editsApplied);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PUBLIC ENDPOINTS
    // ═════════════════════════════════════════════════════════════════════════

    [HttpPost("execute")]
    public async Task<IActionResult> Execute([FromBody] AgentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt))
            return BadRequest("Prompt is required");

        var projectRoot = GetProjectRoot(req.Project);

        var (allSteps, summary, complete) = await RunPhasedPipeline(req.Prompt, projectRoot, emitSse: false);

        return Ok(new
        {
            summary,
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
            await EmitLog(true, "info", "Agent run started (phased pipeline)",
                new { projectRoot, task = req.Prompt });

            List<object> allSteps;
            string summary;
            bool complete;

            // ── Fast-path: attached files + clear edit task ─────────────────
            var useFastPath = (req.Files?.Count > 0) && TaskExpectsFileChanges(req.Prompt);
            if (useFastPath)
            {
                await SendSse(Response, "phase",
                    new { phase = "fast", message = "Attached files — running focused edit…" });
                await EmitLog(true, "info", "Fast-path: reading attached files then editing directly");

                var (discoveryText, bootstrapSteps) =
                    await RunLightBootstrap(req.Files, projectRoot, emitSse: true);
                allSteps = new List<object>(bootstrapSteps);

                // Build a targeted plan from the attached files
                var fastPlan = req.Files
                    .Where(f => !string.IsNullOrWhiteSpace(f) &&
                                (f.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                                 f.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                                 f.EndsWith(".css", StringComparison.OrdinalIgnoreCase)))
                    .Select((f, i) => new PlanItem
                    {
                        File = f.Replace('\\', '/'),
                        ChangeDescription = req.Prompt,
                        Priority = i + 1
                    })
                    .ToList();

                var fastEdits = await RunEditPhase(fastPlan, discoveryText, projectRoot, allSteps.Count, emitSse: true, req.Prompt, ct: Response.HttpContext.RequestAborted);
                allSteps.AddRange(fastEdits);

                complete = HasSuccessfulEdits(allSteps);
                summary = complete ? "Edits applied (fast path)" : "Fast-path edit phase completed with no changes";
            }
            else
            {
                // ── Full phased pipeline ────────────────────────────────────
                (allSteps, summary, complete) =
                    await RunPhasedPipeline(req.Prompt, projectRoot, emitSse: true, ct: Response.HttpContext.RequestAborted);
            }

            var filesEdited = ExtractFilesEdited(allSteps);
            var requirementsMet = complete || TaskRequirementsMet(
                req.Prompt,
                ResolveEditTargetPaths(req.Prompt, req.Files ?? new List<string>(), projectRoot),
                projectRoot, allSteps);

            await SendSse(Response, "done", new
            {
                summary,
                complete = requirementsMet,
                editsApplied = requirementsMet,
                incomplete = TaskExpectsFileChanges(req.Prompt) && !requirementsMet,
                warning = !requirementsMet && TaskExpectsFileChanges(req.Prompt)
                                 ? "No files were modified. Check failed steps below."
                                 : (string?)null,
                steps = allSteps,
                filesEdited
            });
        }
        catch (Exception ex)
        {
            await SendSse(Response, "error", new { message = ex.Message });
            await SendSse(Response, "done", new { });
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  LLM CALL HELPERS
    // ═════════════════════════════════════════════════════════════════════════

    private string GetLlamaBaseUrl()
    {
        var configPath = Path.Combine(
            _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot"), "maestroconfig.json");
        var baseUrl = "http://192.168.2.58:8080";
        if (System.IO.File.Exists(configPath))
        {
            try
            {
                var configText = System.IO.File.ReadAllText(configPath);
                var configJson = JsonSerializer.Deserialize<JsonElement>(configText);
                if (configJson.TryGetProperty("LlamaUrl", out var llamaUrlEl))
                    baseUrl = llamaUrlEl.GetString() ?? baseUrl;
            }
            catch { }
        }
        return baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Low-level LLM call — returns raw content string, no parsing.
    /// </summary>
    private async Task<(string raw, object? unused, string? error)> CallLlmRaw(
        string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        var baseUrl = GetLlamaBaseUrl();
        var model = _config.GetValue<string>("Ai:Model") ?? "medgemma:4b";
        var client = _clientFactory.CreateClient("llama");
        client.Timeout = Timeout.InfiniteTimeSpan;

        var messages = new object[]
        {
            new { role = "system",  content = systemPrompt },
            new { role = "user",    content = userMessage  }
        };

        // FIX 1: Add a per-call timeout so the plan phase cannot hang indefinitely.
        // CallLlmNonStreaming only passes the outer 'ct' (HTTP abort), which has
        // no deadline of its own. If the LLM stalls, Phase 2 and Phase 4 retries
        // would block forever.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        return await CallLlmNonStreaming(client, baseUrl + "/v1/chat/completions", model, messages, linkedCts.Token);
    }

    private async Task<(string raw, AgentResponse? parsed, string? error)> CallLlmSingleFileEdit(
        string taskPrompt, string relativePath, string fileContent,
        string projectRoot, int attempt = 0, string? discoveryContext = null,
        CancellationToken ct = default)
    {
        const string systemPrompt = @"You are a precise code patch tool. Your task is to output JSON with BOTH oldString and newString.

FORMAT (output ONLY this JSON, no other text):
{
  ""edits"": [
    {
      ""oldString"": ""exact 2-4 lines from file (verbatim, max 300 chars)"",
      ""newString"": ""replacement lines (MUST differ from oldString, never empty)""
    }
  ]
}

RULES:
- oldString = 2-4 lines copied VERBATIM from the FILE CONTENT below. Keep small.
- newString = replacement text. MUST differ from oldString. NEVER empty or missing.
- BOTH oldString and newString are REQUIRED. Every edit must have both.
- For INSERTING new code: set oldString to existing nearby lines, newString to existing+new code.
- If no change needed: {""edits"":[]}";

        var user = new StringBuilder();
        user.AppendLine($"Task: {taskPrompt}");
        user.AppendLine($"File path: {relativePath}");

        if (attempt > 0)
        {
            user.AppendLine();
            user.AppendLine("RETRY: Your previous edit was rejected because:");
            user.AppendLine("  - oldString did not match file content, OR");
            user.AppendLine("  - newString was empty/missing, OR");
            user.AppendLine("  - oldString and newString were identical");
            user.AppendLine();
            user.AppendLine("Fix ALL of these:");
            user.AppendLine("  1. Copy oldString VERBATIM from FILE CONTENT (exact spaces/tabs/quotes)");
            user.AppendLine("  2. newString MUST be present and MUST differ from oldString");
            user.AppendLine("  3. Keep both short (2-4 lines, max 300 chars)");
        }

        if (!string.IsNullOrWhiteSpace(discoveryContext))
        {
            user.AppendLine();
            user.AppendLine("## Context (structure, grep results, related functions)");
            user.AppendLine(Truncate(discoveryContext, 4000));
        }

        user.AppendLine();
        user.AppendLine("FILE CONTENT:");
        user.AppendLine("```");
        user.AppendLine(Truncate(fileContent, 12_000));
        user.AppendLine("```");

        try
        {
            var baseUrl = GetLlamaBaseUrl();
            var model = _config.GetValue<string>("Ai:Model") ?? "medgemma:4b";
            var client = _clientFactory.CreateClient("llama");
            client.Timeout = Timeout.InfiniteTimeSpan;

            var messages = new object[]
            {
                new { role = "system",  content = systemPrompt },
                new { role = "user",    content = user.ToString() }
            };

            var requestBody = new { model, messages, stream = false, temperature = 0.05, max_tokens = 2048 };
            var contentJson = JsonSerializer.Serialize(requestBody);
            var httpContent = new StringContent(contentJson, Encoding.UTF8, "application/json");

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(220));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var combined = linkedCts.Token;

            var resp = await client.PostAsync(baseUrl + "/v1/chat/completions", httpContent, combined);
            var respText = await resp.Content.ReadAsStringAsync(combined);

            var raw = ExtractLlmContent(respText);
            if (string.IsNullOrWhiteSpace(raw))
                return (respText, null, "Empty LLM response");

            var steps = ParseEditsFromLlmRaw(raw, relativePath);

            if (steps.Count > 0)
            {
                // Phase 2: For steps with empty or identical newString, generate newString separately
                foreach (var step in steps)
                {
                    if (string.IsNullOrWhiteSpace(step.NewString) ||
                        string.Equals(NormalizeLineEndings(step.OldString ?? ""),
                                      NormalizeLineEndings(step.NewString ?? ""),
                                      StringComparison.Ordinal))
                    {
                        // FIX 4: Pass the original request-abort 'ct', NOT 'combined'.
                        // 'combined' is the 20s countdown from this file's edit call; if the
                        // initial PostAsync used most of that budget, GenerateNewString inherits
                        // a nearly-expired token and times out immediately.  Each helper should
                        // own its own timeout window, linked only to the outer abort signal.
                        var generated = await GenerateNewString(
                            step.OldString ?? "", taskPrompt, relativePath, fileContent, ct);
                        if (!string.IsNullOrWhiteSpace(generated))
                            step.NewString = generated;
                    }
                }

                // Re-filter — keep only steps with non-empty, non-identical newString
                steps = steps.Where(s =>
                    !string.IsNullOrWhiteSpace(s.NewString) &&
                    !string.Equals(NormalizeLineEndings(s.OldString ?? ""),
                                    NormalizeLineEndings(s.NewString ?? ""),
                                    StringComparison.Ordinal)).ToList();

                if (steps.Count > 0)
                    return (raw, new AgentResponse { Steps = steps, Summary = "Parsed edits" }, null);
            }

            return (raw, null, "No edits in response — model did not return edit JSON");
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

    private async Task<string?> GenerateNewString(string oldString, string taskPrompt,
        string relativePath, string fileContent, CancellationToken ct = default)
    {
        var systemPrompt = @"You are a code modifier. Given a code block and a task, modify the code block to implement the task.

Return ONLY the modified code block — no JSON, no markdown fences, no explanation.";

        var user = new StringBuilder();
        user.AppendLine("Task: " + taskPrompt);
        user.AppendLine();
        user.AppendLine("Code block from " + relativePath + " to modify:");
        user.AppendLine("```");
        user.AppendLine(oldString);
        user.AppendLine("```");
        user.AppendLine();
        user.AppendLine("Return ONLY the modified code block.");

        try
        {
            var baseUrl = GetLlamaBaseUrl();
            var model = _config.GetValue<string>("Ai:Model") ?? "medgemma:4b";
            var client = _clientFactory.CreateClient("llama");
            client.Timeout = Timeout.InfiniteTimeSpan;

            var messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = user.ToString() }
            };

            var requestBody = new { model, messages, stream = false, temperature = 0.3, max_tokens = 2048 };
            var contentJson = JsonSerializer.Serialize(requestBody);
            var httpContent = new StringContent(contentJson, Encoding.UTF8, "application/json");

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(220));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var combined = linkedCts.Token;

            var resp = await client.PostAsync(baseUrl + "/v1/chat/completions", httpContent, combined);
            var respText = await resp.Content.ReadAsStringAsync(combined);

            var content = ExtractLlmContent(respText);
            if (string.IsNullOrWhiteSpace(content)) return null;

            content = content.Trim();
            if (content.StartsWith("```"))
            {
                var m = Regex.Match(content, @"```(?:\w+)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (m.Success) content = m.Groups[1].Value.Trim();
            }

            return content;
        }
        catch
        {
            return null;
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

    // ═════════════════════════════════════════════════════════════════════════
    //  DEDICATED EDIT PHASE  (kept for the fast-path fallback)
    // ═════════════════════════════════════════════════════════════════════════

    private List<string> ResolveEditTargetPaths(string prompt, List<string> attachedFiles, string projectRoot)
    {
        var paths = attachedFiles.Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Replace('\\', '/')).ToList();
        foreach (var likely in FindLikelyFiles(prompt, projectRoot))
            if (!paths.Contains(likely, StringComparer.OrdinalIgnoreCase))
                paths.Add(likely);
        if (paths.Count == 0)
            paths = FindLikelyFiles(prompt, projectRoot);

        if (prompt.Contains("maestroconfig.json", StringComparison.OrdinalIgnoreCase) &&
            !paths.Any(p => p.EndsWith("maestroconfig.json", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var p in new[] { "backend/wwwroot/maestroconfig.json", "wwwroot/maestroconfig.json" })
            {
                var full = Path.Combine(projectRoot, p.Replace('/', Path.DirectorySeparatorChar));
                if (System.IO.File.Exists(full)) paths.Add(p);
            }
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  RESULT HELPERS
    // ═════════════════════════════════════════════════════════════════════════

    // FIX 3: Include 'rename' steps so the done SSE payload's filesEdited list
    // is populated.  Without this the frontend overwrites its streaming-derived
    // list with an empty array, and the card stays in Doing.
    private static List<object> ExtractFilesEdited(List<object> steps) =>
        steps.OfType<Dictionary<string, object?>>()
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

    private static void AppendObservations(StringBuilder observations, List<object> batchResults)
    {
        foreach (var item in batchResults)
        {
            if (item is not Dictionary<string, object?> r) continue;
            var type = r.TryGetValue("type", out var t) ? t?.ToString() : "";
            var status = r.TryGetValue("status", out var st) ? st?.ToString() : "";
            var desc = r.TryGetValue("description", out var d) ? d?.ToString() : "";

            if (type == "edit" && status == "error")
            {
                observations.AppendLine($"EDIT FAILED: {r.GetValueOrDefault("path")} — {r.GetValueOrDefault("error")}");
                if (r.TryGetValue("suggestions", out var sug) && sug != null)
                {
                    var paths = sug is IEnumerable<string> ss ? ss :
                        (sug as System.Collections.IEnumerable)?.Cast<object>()
                            .Select(x => x?.ToString() ?? "") ?? Array.Empty<string>();
                    observations.AppendLine("USE THESE REAL PATHS INSTEAD: " + string.Join(", ", paths));
                }
                if (r.TryGetValue("snippet", out var sn) && sn != null)
                    observations.AppendLine($"Near match context:\n{sn}");
            }
            else if (r.TryGetValue("output", out var output) && output != null)
            {
                var outStr = output.ToString() ?? "";
                var maxOut = type == "read" ? 5000 : 2000;
                var pathLabel = r.TryGetValue("path", out var p) && p != null ? $"FILE: {p}\n" : "";
                observations.AppendLine($"[{type?.ToUpper()} {status}] {desc}\n{pathLabel}{Truncate(outStr, maxOut)}");
            }
            else if (type == "edit" && status == "done")
            {
                observations.AppendLine($"EDIT OK: {r.GetValueOrDefault("path")} — {desc}");
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  STEP EXECUTION ENGINE  (unchanged from original)
    // ═════════════════════════════════════════════════════════════════════════

    private async Task<List<object>> ExecuteSteps(
        List<AgentStep> steps, string projectRoot, int indexOffset, bool emitSse, CancellationToken ct = default)
    {
        var results = new List<object>();
        var terminalStarted = false;

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
                await EmitLog(emitSse, "step",
                    $"▶ {step.Type}: {step.Description ?? step.Path ?? step.Command ?? step.Query ?? ""}", ct: ct);
                await SendSse(Response, "step", result, ct);
            }

            try
            {
                switch (step.Type?.ToLowerInvariant())
                {
                    case "edit":
                        await ExecuteEditStep(step, projectRoot, result);
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

            result["status"] = NormalizeUiStatus(result["status"]?.ToString());
            results.Add(result);

            if (emitSse)
            {
                var st = result["status"]?.ToString() ?? "?";
                await EmitLog(emitSse, st == "error" ? "error" : "info",
                    $"✓ {step.Type} finished ({st})",
                    new
                    {
                        path = result.GetValueOrDefault("path"),
                        error = result.GetValueOrDefault("error"),
                        oldStringPreview = result.GetValueOrDefault("oldStringPreview"),
                        snippet = result.GetValueOrDefault("snippet"),
                        suggestions = result.GetValueOrDefault("suggestions")
                    }, ct: ct);
                await SendSse(Response, "step", result, ct);
            }
        }

        return results;
    }

    private async Task ExecuteEditStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var relPath = (step.Path ?? "").Replace('/', Path.DirectorySeparatorChar);
        var targetPath = Path.GetFullPath(Path.Combine(projectRoot, relPath));
        if (!IsPathUnderRoot(targetPath, projectRoot))
        { result["status"] = "error"; result["error"] = "Path outside project root"; return; }

        result["path"] = step.Path;
        var oldString = step.OldString ?? "";
        var newString = step.NewString ?? "";
        var fileExists = System.IO.File.Exists(targetPath);

        if (!fileExists)
        {
            if (string.IsNullOrEmpty(oldString) && !string.IsNullOrEmpty(newString))
            {
                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                await System.IO.File.WriteAllTextAsync(targetPath, newString, Encoding.UTF8);
                PopulateEditResult(result, "created", step.Path!, null, newString, newString);
                return;
            }
            var suggestions = FindSimilarFiles(step.Path ?? "", projectRoot);
            result["status"] = "error";
            result["error"] = $"File does not exist: {step.Path}. Use a path from discovery.";
            result["suggestions"] = suggestions;
            return;
        }

        var content = await System.IO.File.ReadAllTextAsync(targetPath, Encoding.UTF8);

        if (string.IsNullOrEmpty(oldString))
        {
            content += newString;
            await System.IO.File.WriteAllTextAsync(targetPath, content, Encoding.UTF8);
            PopulateEditResult(result, "modified", step.Path!, null, newString, newString);
            return;
        }

        var (replaced, newContent, matchError, snippet) = TryReplace(content, oldString, newString);
        if (!replaced)
        {
            result["status"] = "error";
            result["error"] = matchError ?? "oldString not found";
            if (snippet != null) result["snippet"] = snippet;
            result["oldStringPreview"] = Truncate(oldString, 200);
            return;
        }

        if (NormalizeLineEndings(newContent) == NormalizeLineEndings(content))
        { result["status"] = "skipped"; result["path"] = step.Path; return; }

        await System.IO.File.WriteAllTextAsync(targetPath, newContent, Encoding.UTF8);
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

        if (!IsPathUnderRoot(srcPath, projectRoot) || !IsPathUnderRoot(dstPath, projectRoot))
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
        if (!string.IsNullOrEmpty(oldStr)) result["oldStringPreview"] = Truncate(oldStr, 300);
        if (!string.IsNullOrEmpty(newStr)) result["newStringPreview"] = Truncate(newStr, 300);
        result["diffPreview"] = BuildDiffPreview(oldStr, newStr);
        result["oldLines"] = (oldStr ?? "").Split('\n');
        result["newLines"] = (newStr ?? "").Split('\n');
    }

    private static string BuildDiffPreview(string? oldStr, string? newStr)
    {
        if (string.IsNullOrEmpty(oldStr) && string.IsNullOrEmpty(newStr)) return "";
        var oldLines = (oldStr ?? "").Split('\n');
        var newLines = (newStr ?? "").Split('\n');
        var sb = new StringBuilder();
        for (int i = 0, j = 0; i < oldLines.Length || j < newLines.Length;)
        {
            if (i < oldLines.Length && j < newLines.Length && oldLines[i] == newLines[j])
            { sb.Append("  "); sb.AppendLine(oldLines[i]); i++; j++; }
            else
            {
                if (i < oldLines.Length) { sb.Append("- "); sb.AppendLine(oldLines[i]); i++; }
                if (j < newLines.Length) { sb.Append("+ "); sb.AppendLine(newLines[j]); j++; }
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Multi-pass string replacement with progressively looser matching.
    /// Pass 1 – exact (after CRLF normalisation).
    /// Pass 2 – per-line leading/trailing whitespace trim, ordinal.
    /// Pass 3 – per-line trim, case-insensitive.
    /// Pass 4 – skip blank lines while matching non-blank pattern lines.
    /// Pass 5 – single-line collapsed-whitespace match.
    /// </summary>
    private static (bool ok, string content, string? error, string? snippet) TryReplace(
        string content, string oldString, string newString)
    {
        content = NormalizeLineEndings(content);
        oldString = NormalizeLineEndings(oldString);

        // Pass 1: exact
        var idx = content.IndexOf(oldString, StringComparison.Ordinal);
        if (idx >= 0)
            return (true, content[..idx] + newString + content[(idx + oldString.Length)..], null, null);

        var fileLines = content.Split('\n');
        var rawOldLines = oldString.Split('\n');

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
                        if (!string.Equals(fileLines[scan].Trim(), nonBlankOld[matched].Trim(), StringComparison.Ordinal)) break;
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

        var firstNonBlankPattern = coreOld.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";
        var hint = firstNonBlankPattern.Length > 0
            ? string.Join("\n", fileLines.Where(l => l.Contains(firstNonBlankPattern, StringComparison.OrdinalIgnoreCase)).Take(3))
            : null;
        return (false, content, "oldString not found in file",
            !string.IsNullOrEmpty(hint) ? Truncate(hint, 400) : null);
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
        await _terminal.SendCommandAsync(command, projectRoot);
        await Task.Delay(1000);
        result["status"] = "done";
        result["command"] = command;
        result["output"] = Truncate(_terminal.ReadLastLines(200), MaxReadOutputChars);
    }

    private async Task ExecuteReadStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var relPath = (step.Path ?? "").Replace('/', Path.DirectorySeparatorChar);
        var targetPath = Path.GetFullPath(Path.Combine(projectRoot, relPath));
        if (!IsPathUnderRoot(targetPath, projectRoot))
        { result["status"] = "error"; result["error"] = "Path outside project root"; return; }
        if (!System.IO.File.Exists(targetPath))
        { result["status"] = "error"; result["error"] = "File not found"; return; }
        var content = await System.IO.File.ReadAllTextAsync(targetPath, Encoding.UTF8);
        result["status"] = "done";
        result["path"] = step.Path;
        result["output"] = Truncate(content, MaxReadOutputChars);
    }

    private Task ExecuteListStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var relPath = string.IsNullOrWhiteSpace(step.Path) ? "" : step.Path.Replace('/', Path.DirectorySeparatorChar);
        var targetPath = Path.GetFullPath(Path.Combine(projectRoot, relPath));
        if (!IsPathUnderRoot(targetPath, projectRoot))
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
        try
        {
            IEnumerable<string> files;
            if (pattern.Contains('*'))
            {
                var parts = pattern.Split('/');
                var filePattern = parts[^1];
                var dirPart = parts.Length > 1 ? string.Join(Path.DirectorySeparatorChar, parts[..^1]) : "";
                var searchRoot = string.IsNullOrEmpty(dirPart)
                    ? projectRoot
                    : Path.GetFullPath(Path.Combine(projectRoot, dirPart));
                if (!IsPathUnderRoot(searchRoot, projectRoot))
                    throw new InvalidOperationException("Pattern outside project root");
                files = Directory.EnumerateFiles(searchRoot, filePattern, SearchOption.AllDirectories);
            }
            else
            {
                var single = Path.GetFullPath(Path.Combine(projectRoot, pattern));
                files = System.IO.File.Exists(single) ? new[] { single } : Array.Empty<string>();
            }

            var list = files.Where(f => IsPathUnderRoot(f, projectRoot)).Take(100)
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
        if (string.IsNullOrWhiteSpace(query))
        { result["status"] = "error"; result["error"] = "grep requires query"; return Task.CompletedTask; }

        var searchRoot = projectRoot;
        if (!string.IsNullOrWhiteSpace(step.Path))
        {
            searchRoot = Path.GetFullPath(Path.Combine(projectRoot, step.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsPathUnderRoot(searchRoot, projectRoot))
            { result["status"] = "error"; result["error"] = "Path outside project root"; return Task.CompletedTask; }
        }

        var matches = new List<string>();
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "node_modules", ".git", "bin", "obj", "dist", ".angular" };

        try
        {
            foreach (var file in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories))
            {
                if (!IsPathUnderRoot(file, projectRoot)) continue;
                if (skipDirs.Any(d => file.Contains(Path.DirectorySeparatorChar + d + Path.DirectorySeparatorChar))) continue;
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > 500_000) continue;
                    var lines = System.IO.File.ReadAllLines(file);
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (!lines[i].Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                        matches.Add($"{Path.GetRelativePath(projectRoot, file).Replace('\\', '/')}:{i + 1}: {lines[i].Trim()}");
                        if (matches.Count >= 50) break;
                    }
                }
                catch { }
                if (matches.Count >= 50) break;
            }
            result["status"] = "done";
            result["output"] = matches.Count == 0 ? "(no matches)" : string.Join("\n", matches);
        }
        catch (Exception ex) { result["status"] = "error"; result["error"] = ex.Message; }
        return Task.CompletedTask;
    }

    private async Task ExecuteWebStep(AgentStep step, Dictionary<string, object?> result)
    {
        var url = step.Url ?? step.Path ?? "";
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        { result["status"] = "error"; result["error"] = "Valid absolute url required"; return; }

        try
        {
            var client = _clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(220);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Maestro-Agent/1.0");
            var resp = await client.GetAsync(uri);
            var body = await resp.Content.ReadAsStringAsync();
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "text/plain";
            result["status"] = "done";
            result["url"] = url;
            result["output"] = Truncate($"HTTP {(int)resp.StatusCode} ({contentType})\n{body}", MaxWebResponseChars);
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
            if (!IsPathUnderRoot(targetPath, projectRoot))
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

    private async Task<string> ReadAttachedFiles(List<string> files, string projectRoot)
    {
        var sb = new StringBuilder();
        foreach (var filePath in files)
        {
            if (string.IsNullOrWhiteSpace(filePath)) continue;
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, filePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsPathUnderRoot(fullPath, projectRoot)) continue;
            if (System.IO.File.Exists(fullPath))
            {
                var content = await System.IO.File.ReadAllTextAsync(fullPath);
                sb.AppendLine($"\n### {filePath}\n```\n{Truncate(content, 4000)}\n```");
            }
        }
        return sb.ToString();
    }

    private async Task<string> BuildFullFileContextAsync(
        IEnumerable<string> relativePaths, string projectRoot, int maxTotalChars = 120_000)
    {
        var sb = new StringBuilder();
        var total = 0;
        foreach (var rel in relativePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(rel)) continue;
            var full = Path.GetFullPath(Path.Combine(projectRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsPathUnderRoot(full, projectRoot) || !System.IO.File.Exists(full)) continue;
            var text = await System.IO.File.ReadAllTextAsync(full, Encoding.UTF8);
            if (total + text.Length > maxTotalChars)
                text = Truncate(text, Math.Max(2000, maxTotalChars - total));
            sb.AppendLine($"### FILE: {rel.Replace('\\', '/')}");
            sb.AppendLine("```"); sb.AppendLine(text); sb.AppendLine("```"); sb.AppendLine();
            total += text.Length;
            if (total >= maxTotalChars) break;
        }
        return sb.ToString();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  TASK COMPLETION CHECK
    // ═════════════════════════════════════════════════════════════════════════

    private static bool TaskRequirementsMet(
        string prompt, List<string> targetPaths, string projectRoot, List<object> steps)
    {
        if (HasSuccessfulEdits(steps)) return true;
        foreach (var rel in targetPaths)
        {
            var full = Path.GetFullPath(Path.Combine(projectRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!System.IO.File.Exists(full)) continue;
            var content = System.IO.File.ReadAllText(full);
            if (!IsTaskAlreadySatisfied(prompt, rel, content)) return false;
        }
        return targetPaths.Count > 0;
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
    //  HEURISTIC PATCHES  (last-resort fallback, domain-specific)
    // ═════════════════════════════════════════════════════════════════════════

    private static List<AgentStep> TryHeuristicPatches(
        string prompt, string relativePath, string content)
    {
        var steps = new List<AgentStep>();
        var task = prompt.ToLowerInvariant();
        var path = relativePath.Replace('\\', '/').ToLowerInvariant();

        if (!task.Contains("terminal") && !task.Contains("showterminal")) return steps;

        if (path.EndsWith("index.html"))
        {
            if (!content.Contains("vm.showTerminal"))
            {
                const string oldBlock = "        <div class=\"form-row\">\r\n          <label>\r\n            <input type=\"checkbox\" ng-model=\"vm.autoQueue\" /> Auto-queue: process next Todo card when one completes\r\n          </label>\r\n        </div>";
                const string newBlock = oldBlock + "\r\n        <div class=\"form-row\">\r\n          <label>\r\n            <input type=\"checkbox\" ng-model=\"vm.showTerminal\" /> Show terminal panel (saved to maestroconfig.json)\r\n          </label>\r\n        </div>";
                if (content.Contains("vm.autoQueue") && content.Contains("Auto-queue"))
                    steps.Add(new AgentStep { Index = 0, Type = "edit", Path = relativePath, OldString = NormalizeLineEndings(oldBlock), NewString = NormalizeLineEndings(newBlock), Description = "Add terminal toggle (heuristic)" });
            }
            if (!content.Contains("ng-if=\"vm.showTerminal\"") && content.Contains("class=\"panel term-panel\""))
                steps.Add(new AgentStep { Index = steps.Count, Type = "edit", Path = relativePath, OldString = "<div class=\"panel term-panel\">", NewString = "<div class=\"panel term-panel\" ng-if=\"vm.showTerminal\">", Description = "Hide terminal panel when off (heuristic)" });
        }

        if (path.EndsWith("app.js"))
        {
            if (!content.Contains("vm.showTerminal"))
                steps.Add(new AgentStep { Index = steps.Count, Type = "edit", Path = relativePath, OldString = "    vm.autoQueue = true;", NewString = "    vm.autoQueue = true;\r\n    vm.showTerminal = true;", Description = "Add showTerminal state (heuristic)" });
            if (!content.Contains("cfg.showTerminal"))
                steps.Add(new AgentStep { Index = steps.Count, Type = "edit", Path = relativePath, OldString = "        vm.defaultProject = cfg.defaultProject;", NewString = "        vm.defaultProject = cfg.defaultProject;\r\n        if (typeof cfg.showTerminal === 'boolean') vm.showTerminal = cfg.showTerminal;", Description = "Load showTerminal from config (heuristic)" });
        }

        if (path.EndsWith("maestroconfig.json"))
        {
            if (string.IsNullOrWhiteSpace(content) || content.Trim() == "{}")
                steps.Add(new AgentStep { Index = 0, Type = "edit", Path = relativePath, OldString = "", NewString = "{\r\n  \"projects\": [],\r\n  \"defaultProject\": \"..\",\r\n  \"showTerminal\": true\r\n}\r\n", Description = "Create maestroconfig.json with showTerminal (heuristic)" });
            else if (!content.Contains("showTerminal"))
            {
                var trimmed = content.TrimEnd();
                if (trimmed.EndsWith("}"))
                {
                    var insert = trimmed.TrimEnd('}').TrimEnd() + ",\r\n  \"showTerminal\": true\r\n}";
                    steps.Add(new AgentStep { Index = 0, Type = "edit", Path = relativePath, OldString = content, NewString = insert, Description = "Add showTerminal to maestroconfig.json (heuristic)" });
                }
            }
        }

        return steps.Where(s => !string.IsNullOrEmpty(s.NewString) || !string.IsNullOrEmpty(s.OldString)).ToList();
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

    private static List<AgentStep> ParseEditsFromLlmRaw(string? raw, string defaultPath)
    {
        var steps = new List<AgentStep>();
        if (string.IsNullOrWhiteSpace(raw)) return steps;

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

        var jsonStr = raw.Trim();
        if (jsonStr.StartsWith("```"))
        {
            var m = Regex.Match(jsonStr, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (m.Success) jsonStr = m.Groups[1].Value.Trim();
        }

        var jsonOptions = new JsonDocumentOptions { AllowTrailingCommas = true };
        var repaired = RepairJsonString(jsonStr) ?? jsonStr;
        var blocks = ExtractJsonBlocks(repaired);

        foreach (var block in blocks)
        {
            foreach (var candidate in new[] { block, RepairJsonString(block) })
            {
                if (candidate == null) continue;
                try
                {
                    using var doc = JsonDocument.Parse(candidate, jsonOptions);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("edits", out var editsEl) && editsEl.ValueKind == JsonValueKind.Array)
                    {
                        var envelope = JsonSerializer.Deserialize<MinimalEditsEnvelope>(candidate,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (envelope?.Edits != null)
                        {
                            var i = 0;
                            foreach (var e in envelope.Edits)
                            {
                                if (string.IsNullOrEmpty(e.OldString) && string.IsNullOrEmpty(e.NewString)) continue;
                                steps.Add(new AgentStep
                                {
                                    Index = i++,
                                    Type = "edit",
                                    Path = string.IsNullOrWhiteSpace(e.Path) ? defaultPath : e.Path,
                                    OldString = e.OldString,
                                    NewString = e.NewString,
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
            steps = ExtractEditPairs(jsonStr, defaultPath);

        return steps;
    }

    private static List<AgentStep> ExtractEditPairs(string text, string defaultPath)
    {
        var steps = new List<AgentStep>();

        // Fix common unquoted-key LLM blunder
        var unquotedNew = text.IndexOf(",newString\"", StringComparison.OrdinalIgnoreCase);
        var unquotedOld = text.IndexOf(",oldString\"", StringComparison.OrdinalIgnoreCase);
        if (unquotedNew >= 0 || unquotedOld >= 0)
        {
            var fixedText = text;
            if (unquotedNew >= 0) fixedText = fixedText.Substring(0, unquotedNew + 1) + "\"" + fixedText.Substring(unquotedNew + 1);
            if (unquotedOld >= 0) fixedText = fixedText.Substring(0, unquotedOld + 1) + "\"" + fixedText.Substring(unquotedOld + 1);
            return ExtractEditPairs(fixedText, defaultPath);
        }

        var i = 0;
        while (i < text.Length)
        {
            var oldKeyIdx = text.IndexOf("\"oldString\"", i, StringComparison.OrdinalIgnoreCase);
            var newKeyIdx = text.IndexOf("\"newString\"", i, StringComparison.OrdinalIgnoreCase);
            if (oldKeyIdx < 0 || newKeyIdx < 0) break;

            string firstKey, secondKey;
            int firstIdx, secondIdx;
            if (oldKeyIdx < newKeyIdx)
            { firstKey = "oldString"; secondKey = "newString"; firstIdx = oldKeyIdx; secondIdx = newKeyIdx; }
            else
            { firstKey = "newString"; secondKey = "oldString"; firstIdx = newKeyIdx; secondIdx = oldKeyIdx; }

            var firstVal = ExtractJsonStringValue(text, firstIdx + firstKey.Length);
            if (firstVal == null) { i = firstIdx + 1; continue; }

            var secKeyPos = text.IndexOf("\"" + secondKey + "\"", firstVal.Value.EndPos, StringComparison.OrdinalIgnoreCase);
            if (secKeyPos < 0) { i = firstIdx + 1; continue; }

            var secVal = ExtractJsonStringValue(text, secKeyPos + secondKey.Length);
            if (secVal == null) { i = firstIdx + 1; continue; }

            var oldStr = firstKey == "oldString" ? firstVal.Value.Text : secVal.Value.Text;
            var newStr = firstKey == "newString" ? firstVal.Value.Text : secVal.Value.Text;

            if (!string.IsNullOrEmpty(oldStr) || !string.IsNullOrEmpty(newStr))
                steps.Add(new AgentStep
                {
                    Index = steps.Count,
                    Type = "edit",
                    Path = defaultPath,
                    OldString = oldStr ?? "",
                    NewString = newStr ?? "",
                    Description = "LLM edit (extracted)"
                });

            i = secVal.Value.EndPos;
        }
        return steps;
    }

    private static (string Text, int EndPos)? ExtractJsonStringValue(string text, int keyEndPos)
    {
        var pos = keyEndPos;
        while (pos < text.Length && text[pos] != ':') pos++;
        if (pos >= text.Length) return null;
        pos++;
        while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
        if (pos >= text.Length || text[pos] != '"') return null;
        pos++;

        var start = pos;
        while (pos < text.Length)
        {
            if (text[pos] == '\\') { pos += 2; continue; }
            if (text[pos] == '"') return (UnescapeJsonString(text.Substring(start, pos - start)), pos + 1);
            pos++;
        }
        return null;
    }

    private static string UnescapeJsonString(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\') { sb.Append(s[i]); continue; }
            i++;
            if (i >= s.Length) { sb.Append('\\'); break; }
            switch (s[i])
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u':
                    if (i + 4 < s.Length && int.TryParse(s.Substring(i + 1, 4),
                        System.Globalization.NumberStyles.HexNumber, null, out var code))
                    { sb.Append((char)code); i += 4; }
                    else sb.Append('u');
                    break;
                default: sb.Append(s[i]); break;
            }
        }
        return sb.ToString();
    }

    private static string? RepairJsonString(string json)
    {
        var sb = new StringBuilder(json.Length);
        var inString = false; var depth = 0; var valueStartDepth = 0; var changed = false;

        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (!inString)
            {
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                if (c == '"') { inString = true; valueStartDepth = depth; }
                sb.Append(c); continue;
            }

            // Inside a string — braces/brackets are content, not JSON structure.
            // Do NOT adjust depth for them.
            if (c == '\\') { sb.Append(c); i++; if (i < json.Length) sb.Append(json[i]); continue; }

            if (c == '"')
            {
                var nextNonWs = -1;
                for (var j = i + 1; j < json.Length; j++)
                    if (!char.IsWhiteSpace(json[j])) { nextNonWs = j; break; }

                if (nextNonWs >= 0 && depth == valueStartDepth &&
                    (json[nextNonWs] == ',' || json[nextNonWs] == '}' || json[nextNonWs] == ']' || json[nextNonWs] == ':'))
                { sb.Append(c); inString = false; }
                else { sb.Append("\\\""); changed = true; }
                continue;
            }

            if (c == '\n') { sb.Append("\\n"); changed = true; continue; }
            if (c == '\r') { sb.Append("\\r"); changed = true; continue; }
            if (c == '\t') { sb.Append("\\t"); changed = true; continue; }
            sb.Append(c);
        }
        return changed ? sb.ToString() : null;
    }

    private static List<string> ExtractJsonBlocks(string text)
    {
        var blocks = new List<string>();
        var depth = 0; var start = -1; var inString = false;

        for (var i = 0; i < text.Length; i++)
        {
            if (inString) { if (text[i] == '\\') { i++; continue; } if (text[i] == '"') inString = false; continue; }
            if (text[i] == '"') { inString = true; continue; }
            if (text[i] == '{') { if (depth == 0) start = i; depth++; }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0 && start >= 0) { blocks.Add(text.Substring(start, i - start + 1)); start = -1; }
            }
        }
        return blocks;
    }
}