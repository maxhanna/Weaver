using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MaestroBackend.Services;

// ╔══════════════════════════════════════════════════════════════════════════════╗
// ║  MAESTRO AGENT  —  Pipeline Architecture                              ║ 
// ╚══════════════════════════════════════════════════════════════════════════════╝

[ApiController]
[Route("api/agent")]
public class AgentController : ControllerBase
{
    // ── tuning constants ──────────────────────────────────────────────────────
    private const int MaxFileContextChars = 24_000;
    private const int MaxReadOutputChars = 24_000;
    private const int MaxWebResponseChars = 24_000;
    private const int MaxPlanFiles = 5;   // plan phase: cap files to avoid token bloat
    private bool _lastConnectionCheckResult = true;
    private static DateTime _nextConnectivityCheck = DateTime.MinValue;
    private static TimeSpan _infiniteTimeout = Timeout.InfiniteTimeSpan;
    private static DateTime _lastHostCheck = DateTime.MinValue;
    private static bool _lastHostReachable = false;

    private static bool IsHostReachable(CancellationToken ct = default)
    {
        // Check if 10 minutes have passed since last check
        if ((DateTime.Now - _lastHostCheck).TotalMinutes < 10)
        {
            // Return cached result
            return _lastHostReachable;
        }
        
        // Perform new check (this would be the actual implementation)
        // For now, we'll simulate with a placeholder
        bool isReachable = true; // This would be the actual check
        
        // Update cache
        _lastHostCheck = DateTime.Now;
        _lastHostReachable = isReachable;
        
        return isReachable;
    }

    private readonly IHttpClientFactory _clientFactory;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly TerminalService _terminal;
    private readonly FileHintsManager _fileHints;
    private readonly ConfigFileService _configFile;

    public AgentController(
        IHttpClientFactory cf, IConfiguration config,
        IWebHostEnvironment env, TerminalService terminal, FileHintsManager fileHints,
        ConfigFileService configFile)
    {
        _clientFactory = cf;
        _config = config;
        _env = env;
        _terminal = terminal;
        _fileHints = fileHints;
        _configFile = configFile;
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
        public string Change { get; set; } = "";
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

    public class AgentPlan
    {
        public string Thinking { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public List<PlanStep> Plan { get; set; } = new();
    }

    public class PlanStep
    {
        public string File { get; set; } = string.Empty;
        public string Change { get; set; } = string.Empty;
        public int Priority { get; set; }
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

    /// <summary>
    /// Infers the correct subfolder for a new file based on naming conventions
    /// and content patterns discovered in the repo.
    /// </summary>
    private static string InferTargetFolder(string fileName, string projectRoot)
    {
        var name = Path.GetFileName(fileName.Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name)) return "";

        // If the path already includes a directory, respect it
        var dir = Path.GetDirectoryName(fileName.Replace('/', Path.DirectorySeparatorChar)) ?? "";
        if (!string.IsNullOrWhiteSpace(dir))
            return dir.Replace('\\', '/') + "/";

        // Controller files — *Controller.cs
        if (name.EndsWith("Controller.cs", StringComparison.OrdinalIgnoreCase))
            return "Controllers/";

        // Service files — *Service.cs, *Manager.cs
        if (name.EndsWith("Service.cs", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("Manager.cs", StringComparison.OrdinalIgnoreCase))
            return "Services/";

        // Pipeline files
        if (name.EndsWith("Pipeline.cs", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Pipeline", StringComparison.OrdinalIgnoreCase))
            return "Pipelines/";

        // Routing files
        if (name.EndsWith("Router.cs", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Router", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("Routing.cs", StringComparison.OrdinalIgnoreCase))
            return "Routing/";

        // Frontend files — wwwroot
        var frontendExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".html", ".js", ".css", ".mjs", ".ts", ".tsx", ".jsx", ".vue", ".svelte" };
        if (frontendExts.Contains(Path.GetExtension(name)))
            return "wwwroot/";

        // Config files — placed at root
        var configFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "appsettings.json", "maestroconfig.json", "filehints.json", ".gitignore",
              "appsettings.development.json", "appsettings.production.json" };
        if (configFiles.Contains(name))
            return "";

        // Docs — .md files
        if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return "Docs/";

        // CS files with "Dto", "Model", "Entity" → Models/ (create if exists)
        var modelsDir = Path.Combine(projectRoot, "Models");
        if ((name.EndsWith("Dto.cs", StringComparison.OrdinalIgnoreCase) ||
             name.EndsWith("Model.cs", StringComparison.OrdinalIgnoreCase) ||
             name.EndsWith("Entity.cs", StringComparison.OrdinalIgnoreCase)) &&
            Directory.Exists(modelsDir))
            return "Models/";

        return "";
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
            "wwwroot/app.js", "wwwroot/index.html", "wwwroot/styles.css",
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
                sb.AppendLine($"\n### {r.GetValueOrDefault("path")}\n{Truncate(o.ToString() ?? "", 3000)}");
        }
        return (sb.ToString(), steps);
    }


    private async Task<AgentPlan?> AnalyzePromptAndPlan( 
        string prompt, string discoveryContext, string projectRoot, bool emitSse, CancellationToken ct = default)
    {
        const string systemPrompt = @"You are a task planning agent.

Given a task and the contents of project files, output a structured plan.

IMPORTANT — SPECIAL MARKERS (use these in the file field instead of file paths for certain tasks):
- For GIT OPERATIONS (pull, commit, push, branch, revert, sync): use ""_git"" as the file.
- For PACKAGE INSTALLATION: use ""_package_install"" as the file.
- For PING / NETWORK DIAGNOSTIC: use ""_ping"" as the file.
- For SHOWING OUTPUT to the user: use ""_show"" as the file.
- For CREATING NEW FILES: use ""_create_file"" as the file.

For EDITING EXISTING FILES: use the actual relative file path (e.g. ""src/app.js"") in the file field.

When describing changes, be very specific and detailed. The more precise you are, the better the agent can execute the plan.

OUTPUT FORMAT — respond with ONLY this JSON object, no markdown, no extra text:
{
  ""thinking"": ""analysis of what the task requires"",
  ""summary"":  ""description of the overall changes"",
  ""plan"": [
    {
      ""file"":   ""_git"" or ""_package_install"" or ""_ping"" or ""_show"" or ""_create_file"" or ""relative/path/to/file"",
      ""change"": ""description of what to do. Be very detailed."",
      ""priority"": 1
    },
    {
      ""file"": ""example/relative/path/to/file"",
      ""change"": ""example specific description of what to add/modify/remove in this file. Be very detailed."",
      ""priority"": 2
    }
  ]
}

The ""change"" field is CRITICAL — it will be passed directly to the handler so it knows exactly what to do.
Make it specific and accurate.

SPECIAL MARKER DETAILS:

GIT OPERATIONS (pull, commit, push, branch, revert, sync):
  Set ""file"" to ""_git"". In ""change"", describe the git operation naturally.
  The agent will detect the operation type and run the appropriate git commands.
  Valid: commit, revert (discard working tree changes), branch (create new), pull, sync (pull + push).
  Examples:
    - ""pull all changes and show what was pulled"" → use _git then _show
    - ""commit all changes with message 'WIP'"" → runs git add -A && git commit
    - ""revert all changes"" → runs git checkout -- .
    - ""create a branch called feature/new"" → runs git checkout -b feature/new
    - ""sync with remote"" → runs git pull && git push
  DO NOT list real file paths for git tasks. Do NOT plan edits to TerminalController.cs or any other file.

PACKAGE INSTALLATION:
  Set ""file"" to ""_package_install"". In ""change"", specify the exact install command.
  Example: 'dotnet add package SonarAnalyzer.CSharp --version 9.0.0'

PING / NETWORK DIAGNOSTIC:
  Set ""file"" to ""_ping"". In ""change"", specify the target and parameters.
  Extract port from user's request (e.g. ""check 192.168.1.1:8080"" means port 8080).

SHOWING INFORMATION to the user:
  Set ""file"" to ""_show"". In ""change"", put the exact text to display in the frontend.
  Use this when the user asks to see output, status, or results.

CREATING NEW FILES:
  Set ""file"" to ""_create_file"". In ""change"", describe what file to create and its contents.
  IMPORTANT: Use correct filename with leading dot (e.g. "".editorconfig"", not ""_editorconfig"").
  FOLDER CONVENTIONS — when describing a new file, mention its type so the system places it correctly:
    • Controllers → file names ending with Controller.cs → e.g. ""Controllers/FooController.cs""
    • Services → file names ending with Service.cs or Manager.cs → e.g. ""Services/FooService.cs""
    • Frontend (.html, .js, .css) → wwwroot/ folder → e.g. ""wwwroot/foo.js""
    • Docs (.md) → Docs/ folder → e.g. ""Docs/FEATURE.md""
    • Pipelines → Pipelines/ folder
    • Routing → Routing/ folder
  If you don't specify a folder, the system infers it from the file name pattern above.

FILE EDIT RULES (only when NOT using a special marker):
- Only list files that actually exist in the Project Discovery section below.
- Priority 1 = most important file. Sort by priority ascending.
- When describing changes, quote exact existing code to modify.
- For RENAME/MOVE tasks: list source file, set change to 'Rename this file to <new/path>'.
- DO NOT write any code yet. DO NOT include oldString or newString.
- CRITICAL: Only reference code that actually exists in the provided file contents.
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
        foreach (var candidate in GeneratePlanJsonCandidates(cleaned))
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

    private static IEnumerable<string> GeneratePlanJsonCandidates(string json)
    {
        // Candidate 1 — as-is
        yield return json;

        // Candidate 2 — quote unquoted keys  {thinking: → {"thinking":
        var quoted = Regex.Replace(json,
            @"(?<=[{,])\s*([a-zA-Z_$][\w$]*)\s*(?=:)",
            m => m.Value.Replace(m.Groups[1].Value, $"\"{m.Groups[1].Value}\""));
        if (quoted != json) yield return quoted;

        // Candidate 3 — escape bare newlines inside string values
        var repaired = RepairJsonStringValues(json);
        if (repaired != null && repaired != json) yield return repaired;

        // Candidate 4 — both repairs combined
        if (repaired != null && repaired != json)
        {
            var both = Regex.Replace(repaired,
                @"(?<=[{,])\s*([a-zA-Z_$][\w$]*)\s*(?=:)",
                m => m.Value.Replace(m.Groups[1].Value, $"\"{m.Groups[1].Value}\""));
            if (both != repaired) yield return both;
        }
    }

    /// <summary>
    /// Walks the raw JSON character-by-character and escapes bare control
    /// characters (LF, CR, TAB) that appear inside string values.
    /// Returns null if no change was needed.
    /// </summary>
    private static string? RepairJsonStringValues(string json)
    {
        var sb = new StringBuilder(json.Length + 64);
        var inString = false;
        var changed = false;

        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (!inString)
            {
                if (c == '"') inString = true;
                sb.Append(c);
                continue;
            }

            // Inside a JSON string
            if (c == '\\') { sb.Append(c); i++; if (i < json.Length) sb.Append(json[i]); continue; }
            if (c == '"') { sb.Append(c); inString = false; continue; }

            // Bare control characters must be escaped
            switch (c)
            {
                case '\n': sb.Append("\\n"); changed = true; break;
                case '\r': sb.Append("\\r"); changed = true; break;
                case '\t': sb.Append("\\t"); changed = true; break;
                default: sb.Append(c); break;
            }
        }
        return changed ? sb.ToString() : null;
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
                @"(?:new\s+)?file\s+(?:called|named|`` `)?\s*([\w./\\-]+\.[\w.-]+)", RegexOptions.IgnoreCase);
            if (namedMatch.Success)
                targetRelPath = namedMatch.Groups[1].Value.Replace('\\', '/');
        }

        if (string.IsNullOrWhiteSpace(targetRelPath))
        {
            // Strategy 2: dotfiles like .editorconfig, .gitignore, .env
            var dotMatch = Regex.Match(changeDesc, @"\.[\w-]+(?:\.[\w-]+)*");
            if (dotMatch.Success)
                targetRelPath = dotMatch.Value;
        }

        if (string.IsNullOrWhiteSpace(targetRelPath))
        {
            // Strategy 3: standard path like "path/to/file.ext"
            var pathMatch = Regex.Match(changeDesc, @"[\w/\\]+\.[\w]+");
            if (pathMatch.Success)
                targetRelPath = pathMatch.Value.Replace('\\', '/');
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
            var folder = InferTargetFolder(targetRelPath, projectRoot);
            if (!string.IsNullOrWhiteSpace(folder))
                targetRelPath = folder + targetRelPath;
        }

        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, targetRelPath.Replace('/', Path.DirectorySeparatorChar)));

        if (!IsPathUnderRoot(fullPath, projectRoot))
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
{Truncate(discoveryContext, 4000)}

Respond with ONLY the raw file content — no markdown, no code fences, no explanation. The content will be written directly to the file.";

        var (content, _, err) = await CallLlmRaw(
            "You are a file creation assistant. Output ONLY the raw file content — no markdown, no code fences, no explanation.",
            contentPrompt, ct, requestTimeout: TimeSpan.FromSeconds(30));

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
            ["output"] = Truncate(cleaned, 2000),
            ["type"] = "create"
        };
        results.Add(result);

        return (results, 1);
    }

    /// <summary>
    /// Detects simple, self-contained intents directly from the prompt string
    /// without any LLM call or file discovery.
    /// Returns a ready-to-execute AgentPlan, or null if the prompt needs full
    /// pipeline analysis.
    /// </summary>
    private static AgentPlan? TryDetectSimpleIntent(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return null;

        var p = prompt.Trim();
        var lower = p.ToLowerInvariant();

        // ── Git pull ──────────────────────────────────────────────────────────
        if (Regex.IsMatch(lower, @"\b(git\s+pull|pull\s+(all\s+)?change|pull\s+from\s+git|pull\s+latest)\b")
            || (lower.Contains("pull") && lower.Contains("git") && !lower.Contains("request")))
        {
            return new AgentPlan
            {
                Thinking = "Direct git pull intent detected from prompt.",
                Summary = "Pull latest changes from the remote repository and show the result.",
                Plan = new List<PlanStep>
            {
                new() { File = "_git",  Change = "pull all changes",             Priority = 1 },
                new() { File = "_show", Change = "show what was pulled from git", Priority = 2 }
            }
            };
        }

        // ── Git commit ────────────────────────────────────────────────────────
        if (Regex.IsMatch(lower, @"\b(git\s+commit|commit\s+all|commit\s+change|commit\s+everything)\b"))
        {
            var msgMatch = Regex.Match(p, "\"([^\"]+)\"");
            var msg = msgMatch.Success ? msgMatch.Groups[1].Value : $"Auto-commit {DateTime.Now:yyyy-MM-dd HH:mm}";
            return new AgentPlan
            {
                Thinking = "Direct git commit intent detected.",
                Summary = $"Commit all staged changes: {msg}",
                Plan = new List<PlanStep>
            {
                new() { File = "_git", Change = $"commit all changes with message \"{msg}\"", Priority = 1 }
            }
            };
        }

        // ── Git push / sync ───────────────────────────────────────────────────
        if (Regex.IsMatch(lower, @"\b(git\s+(push|sync)|push\s+(to\s+)?(remote|origin|git)|sync\s+(with\s+)?remote)\b"))
        {
            return new AgentPlan
            {
                Thinking = "Direct git sync intent detected.",
                Summary = "Sync with remote (pull then push).",
                Plan = new List<PlanStep>
            {
                new() { File = "_git", Change = "sync with remote (pull then push)", Priority = 1 }
            }
            };
        }

        // ── Git revert / discard ──────────────────────────────────────────────
        if (Regex.IsMatch(lower, @"\b(git\s+revert|revert\s+all|discard\s+all|undo\s+all\s+change)\b"))
        {
            return new AgentPlan
            {
                Thinking = "Direct git revert intent detected.",
                Summary = "Discard all local working-tree changes.",
                Plan = new List<PlanStep>
            {
                new() { File = "_git", Change = "revert all changes", Priority = 1 }
            }
            };
        }

        // ── Ping / connectivity ───────────────────────────────────────────────
        if (Regex.IsMatch(lower, @"\b(ping\s+\S|check\s+(connect|reach|host)|test\s+connect|is\s+\S+\s+(up|alive|reachable))\b"))
        {
            return new AgentPlan
            {
                Thinking = "Direct ping/connectivity check detected.",
                Summary = "Test network connectivity.",
                Plan = new List<PlanStep>
            {
                new() { File = "_ping", Change = p, Priority = 1 }
            }
            };
        }

        // ── Package install ───────────────────────────────────────────────────
        if (Regex.IsMatch(lower, @"\b(install\s+package|npm\s+install|dotnet\s+add\s+package|pip\s+install)\b"))
        {
            return new AgentPlan
            {
                Thinking = "Direct package install intent detected.",
                Summary = "Install the requested package.",
                Plan = new List<PlanStep>
            {
                new() { File = "_package_install", Change = p, Priority = 1 }
            }
            };
        }

        return null; // needs full pipeline
    }

    /// <summary>
    /// Orchestrate the agent pipeline from start to finish. 
    /// </summary>
    /// <returns>A tuple of (all steps/results, summary message, whether task is complete)</returns>
    /// <remarks>The "complete" flag indicates whether the agent believes it has fully completed the task after the final review
    /// loop. Even if false, the allSteps may contain successful edits/commands that were applied.</remarks>
    /// <exception cref="Exception">Throws if there is a critical failure in the pipeline orchestration. Individual step failures are captured in the results and do not throw.</exception>
    /// <exception cref="OperationCanceledException">Throws if the operation is cancelled via the cancellation token.</exception>
    /// <exception cref="TimeoutException">Throws if any LLM call exceeds its allotted timeout.</exception>
    /// <exception cref="IOException">Throws if there are issues reading/writing files during the edit phase.</exception>
    /// <exception cref="UnauthorizedAccessException">Throws if the agent tries to access files/directories it doesn't have permission for.</exception>
    /// <exception cref="Exception">Throws if there are unexpected errors during command execution, file edits, or any other phase of the pipeline.</exception>
    private async Task<(List<object> allSteps, string summary, bool complete)> Orchestrate(
     string prompt, string projectRoot, bool emitSse, CancellationToken ct = default)
    {
        var allSteps = new List<object>();

        // ── Phase 0: Connectivity check ────────────────────────────────────────
        bool isLlmConnected = await CheckLlmConnectivity(projectRoot, emitSse, ct);
        if (!isLlmConnected)
        { 
            throw new InvalidOperationException("LLM connectivity check failed.");
        }

        // ── Fast path: pure-command intents skip discovery + LLM planning ──────
        var fastPlan = TryDetectSimpleIntent(prompt);
        if (fastPlan != null)
        {
            await EmitLog(emitSse, "info",
                $"Fast-path detected: skipping discovery and planning ({fastPlan.Plan.Count} step(s))", ct: ct);

            if (emitSse)
                await SendSse(Response, "plan",
                    new { thinking = fastPlan.Thinking, summary = fastPlan.Summary, items = fastPlan.Plan }, ct);

            await ExecutePlan(prompt, projectRoot, emitSse, "", fastPlan, ct, allSteps);

            return (allSteps, fastPlan.Summary, true);
        }

        // ── Phase 1: DISCOVER ──────────────────────────────────────────────────
        var (discoveryContext, discoverySteps) =
            await RunBootstrapDiscovery(prompt, projectRoot, emitSse);
        allSteps.AddRange(discoverySteps);

        // ── Phase 2: PLAN ──────────────────────────────────────────────────────
        await EmitLog(emitSse, "info", "Phase 2 — PLAN: asking model to analyze the prompt and create a plan...", ct: ct);
        if (emitSse)
            await SendSse(Response, "phase", new { phase = "plan", message = "Planning..." }, ct);

        var plan = await AnalyzePromptAndPlan(prompt, discoveryContext, projectRoot, emitSse, ct);
        if (plan == null || plan.Plan.Count == 0)
        {
            await EmitLog(emitSse, "warn", "Plan phase produced no items.", new { plan }, ct: ct);
            throw new InvalidOperationException("LLM returned an empty or unparseable plan.");
        }

        await EmitLog(emitSse, "info",
            $"Plan: {plan.Plan.Count} step(s) — {string.Join(", ", plan.Plan.Select(p => p.File))}",
            new { thinking = plan.Thinking, summary = plan.Summary, items = plan.Plan.Select(p => new { p.File, p.Change }) },
            ct: ct);

        if (emitSse)
            await SendSse(Response, "plan",
                new { thinking = plan.Thinking, summary = plan.Summary, items = plan.Plan }, ct);

        // ── Phase 3: ORCHESTRATE PLAN ──────────────────────────────────────────
        await ExecutePlan(prompt, projectRoot, emitSse, discoveryContext, plan, ct, allSteps);

        string? reviewFeedback = null;
        bool isComplete = false;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var editsApplied = HasSuccessfulEdits(allSteps);
            var summary = editsApplied
                ? $"Edits applied to {ExtractFilesEdited(allSteps).Count} file(s)"
                : "No edits were applied — check failed steps for details";

            if (editsApplied)
            {
                await EmitLog(emitSse, "info", $"Phase 4 — VERIFY: ✓ {summary}");
                var (complete, feedback) = await RunContentReview(prompt, allSteps, projectRoot, emitSse, ct);
                reviewFeedback = feedback ?? "Task not yet complete";
                if (complete) { isComplete = true; break; }
            }
            else if (!TaskExpectsFileChanges(prompt))
            {
                break;
            }

            await EmitLog(emitSse, "warn",
                $"Review attempt {attempt + 1}: {(editsApplied ? $"task not complete — {reviewFeedback}" : "no successful edits")}",
                ct: ct);

            var revisedPrompt = editsApplied && !string.IsNullOrWhiteSpace(reviewFeedback)
                ? $"{prompt}\n\nRemaining work: {reviewFeedback}"
                : prompt;

            var revisedPlan = await AnalyzePromptAndPlan(revisedPrompt, discoveryContext, projectRoot, emitSse, ct);
            if (revisedPlan == null || revisedPlan.Plan.Count == 0)
            {
                await EmitLog(emitSse, "warn", "Revised plan phase produced no items.", new { revisedPlan }, ct: ct);
                throw new InvalidOperationException("LLM returned an empty or unparseable revised plan.");
            }

            await ExecutePlan(prompt, projectRoot, emitSse, discoveryContext, revisedPlan, ct, allSteps);
        }

        return (allSteps, reviewFeedback ?? "Task not yet complete", isComplete);
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
                    new { output = Truncate(gitOutput, 2000) }, ct: ct);
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
{Truncate(output, 4000)}
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
                var dstPath = ExtractTargetPath(changeDesc, planFile, projectRoot);
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

            else if (IsRelativePath(planFile))
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

        if (!IsPathUnderRoot(fullPath, projectRoot))
        {
            await EmitLog(emitSse, "warn", $"Skipping {relPath} — outside project root", ct: ct);
            return;
        } 
        if (emitSse) {
            await SendSse(Response, "phase", new { phase = "edit-file", message = $"Editing {relPath}…" }, ct); 
        }

        var fileContent = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8);

        // --- up to 2 attempts ---
        List<AgentStep> editSteps = new();
        var timedOut = false;
        var editHistory = new List<(string path, string preContent)>();
        for (var attempt = 0; attempt < 2 && editSteps.Count == 0; attempt++)
        {
            await EmitLog(emitSse, "info",
                $"LLM edit call: {relPath} (attempt {attempt + 1})",
                new { chars = fileContent.Length, taskSummary = item.Change }, ct: ct);

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

            if (editSteps.Count == 0) { 
                await EmitLog(emitSse, "warn",
                    $"No valid edits parsed from attempt {attempt + 1} for {relPath}. Error: {rejectReason ?? err ?? "No edits in response"}", ct: ct);
            }
        }

        if (timedOut) {return;}  
        if (editSteps.Count == 0)
        {
            await EmitLog(emitSse, "error", $"Could not produce edits for {relPath} — skipping", ct: ct);
            return;
        }

        for (var i = 0; i < editSteps.Count; i++) { 
            editSteps[i].Index = i;
        }

        var batchResults = await ExecuteSteps(editSteps, projectRoot, idx, emitSse, ct);
        idx += batchResults.Count;
        allResults.AddRange(batchResults);

        var fileEdited = batchResults.Any(r =>
            r is Dictionary<string, object?> d &&
            d.TryGetValue("status", out var st) && st?.ToString() == "done");

        var appliedEdit = fileEdited;
        if (!fileEdited)
        {
            await EmitLog(emitSse, "warn",
                $"All edits failed for {relPath} — running one retry with re-read content", ct: ct);

            if (System.IO.File.Exists(fullPath))
            {
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
                    attempt: 2,                   // signals RETRY to the prompt
                    discoveryContext,
                    ct,
                    nearMatchSnippets: nearMatches!);  // ← pass hints

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
                    appliedEdit = retryResults.Any(r =>
                        r is Dictionary<string, object?> rd &&
                        rd.TryGetValue("status", out var rs) && rs?.ToString() == "done");
                }
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
                var buildOk = await RunQuickBuild(projectRoot, buildCmd, emitSse, ct);

                for (var retryLoop = 0; retryLoop < 2 && !buildOk && editHistory.Count > 0; retryLoop++)
                {
                    // Undo edits (up to 2) until build passes
                    var undoneCount = 0;
                    while (undoneCount < 2 && editHistory.Count > 0 && !buildOk)
                    {
                        var (undoPath, undoContent) = editHistory[^1];
                        editHistory.RemoveAt(editHistory.Count - 1);
                        var undoFullPath = Path.GetFullPath(
                            Path.Combine(projectRoot, undoPath.Replace('/', Path.DirectorySeparatorChar)));
                        if (System.IO.File.Exists(undoFullPath))
                        {
                            await System.IO.File.WriteAllTextAsync(undoFullPath, undoContent, Encoding.UTF8);
                            await EmitLog(emitSse, "warn", $"Undid edit: {undoPath}", ct: ct);
                        }
                        undoneCount++;
                        buildOk = await RunQuickBuild(projectRoot, buildCmd, emitSse, ct);
                    }

                    if (buildOk && undoneCount > 0)
                    {
                        // Build passes with undo — retry the original file edit
                        await EmitLog(emitSse, "info", $"Build passes after undo — retrying edit for {relPath}", ct: ct);

                        var currentContent = System.IO.File.Exists(fullPath)
                            ? await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8)
                            : fileContent;

                        var (retryRaw2, _, _) = await CallLlmSingleFileEdit(
                            fileTask, relPath, currentContent, projectRoot, 2, discoveryContext, ct);

                        var retrySteps2 = ParseEditsFromLlmRaw(retryRaw2, relPath)
                            .Where(e => !string.Equals(
                                NormalizeLineEndings(e.OldString ?? ""),
                                NormalizeLineEndings(e.NewString ?? ""),
                                StringComparison.Ordinal))
                            .ToList();

                        if (retrySteps2.Count > 0)
                        {
                            for (var i = 0; i < retrySteps2.Count; i++) retrySteps2[i].Index = i;
                            var retryResults2 = await ExecuteSteps(retrySteps2, projectRoot, idx, emitSse, ct);
                            idx += retryResults2.Count;
                            allResults.AddRange(retryResults2);

                            buildOk = await RunQuickBuild(projectRoot, buildCmd, emitSse, ct);
                        }
                    }
                }

                if (!buildOk)
                {
                    await EmitLog(emitSse, "warn",
                        $"Build failing after edit/retry for {relPath} — restoring and skipping", ct: ct);
                    // Restore current file to pre-edit state
                    await System.IO.File.WriteAllTextAsync(fullPath, fileContent, Encoding.UTF8);
                    editHistory.RemoveAll(e => e.path == relPath);
                    return;
                }
            }
        }
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

        var (allSteps, summary, complete) = await Orchestrate(req.Prompt, projectRoot, emitSse: false);

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
            // ── Full phased pipeline ────────────────────────────────────
            (allSteps, summary, complete) =
                await Orchestrate(req.Prompt, projectRoot, emitSse: true, ct: Response.HttpContext.RequestAborted);
            
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
            await SendSse(Response, "done", new { incomplete = true, summary = ex.Message });
        }
    } 

    private async Task<string> WaitForBuildOutput(string beforeOutput)
    {
        var timeout = TimeSpan.FromMinutes(3);
        var started = DateTime.UtcNow;
        var delay = 1000;

        while (DateTime.UtcNow - started < timeout)
        {
            await Task.Delay(delay, CancellationToken.None);
            var current = _terminal.ReadAll();
            if (current.Length > beforeOutput.Length + 80)
            {
                var stable = _terminal.ReadAll();
                if (stable == current)
                    return current;
            }
            delay = Math.Min(delay + 1000, 8000);
        }
        return _terminal.ReadAll();
    }

    private static List<PlanItem> ParseBuildFixPlan(string raw)
    {
        var items = new List<PlanItem>();
        if (string.IsNullOrWhiteSpace(raw)) return items;
        try
        {
            var blocks = ExtractJsonBlocks(raw);
            foreach (var block in blocks)
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<PlanItemDeserialized>>(block);
                    if (parsed != null)
                    {
                        foreach (var p in parsed)
                        {
                            if (!string.IsNullOrWhiteSpace(p.file))
                                items.Add(new PlanItem { File = p.file, Change = p.change, Priority = items.Count + 1 });
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        return items;
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
        return (cfg.llamaUrl ?? "http://localhost:8081").TrimEnd('/');
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
            user.AppendLine(Truncate(discoveryContext, 3_000));
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
  - oldString and newString must not be the same. You must make an edit.
  - Prefer MANY small targeted edits over one large edit.
  - Preserve indentation exactly in both oldString and newString.
  - Return ONLY valid JSON, no markdown fences, no explanation.
  - If no changes are needed, return: {""edits"": []}

Example:
{""edits"":[{""oldString"":""<button class=\""foo\"">"",""newString"":""<button class=\""foo bar\"">""},{""oldString"":""<button class=\""foo2\"">"",""newString"":""<button class=\""foo2 bar2\"">""}]}";

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
            foreach (var p in new[] { "maestroconfig.json", "maestroconfig.json" })
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
            // Enhanced error reporting with context
            result["status"] = "error";
            result["error"] = matchError ?? "oldString not found";
            if (snippet != null) result["snippet"] = snippet;
            result["oldStringPreview"] = Truncate(oldString, 200);
            
            // Add helpful context about file content for debugging
            var lines = content.Split('\n');
            result["fileLineCount"] = lines.Length;
            result["fileCharCount"] = content.Length;
            

            
            // If the file is small, show more context
            if (content.Length < 1000)
            {
                result["fileContentPreview"] = Truncate(content, 300);
            }
            
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
    /// Pass 6 – token-overlap similarity (≥85% of meaningful tokens match).
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
        // Adaptive wait: poll output length every 500ms, stop when no growth for 2s, cap at 15s
        var beforeLen = _terminal.ReadAll().Length;
        var prevLen = beforeLen;
        var stableMs = 0;
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            var curLen = _terminal.ReadAll().Length;
            if (curLen == prevLen)
            {
                stableMs += 500;
                if (stableMs >= 2000) break;
            }
            else { stableMs = 0; prevLen = curLen; }
        }
        result["status"] = "done";
        result["command"] = command;
        var fullOutput = _terminal.ReadAll();
        result["output"] = beforeLen >= 0 && beforeLen < fullOutput.Length
            ? Truncate(fullOutput.Substring(beforeLen), MaxReadOutputChars)
            : "";
    }

    /// <summary>
    /// Quick build check — runs the build command and returns success/failure.
    /// Does NOT attempt LLM-based fix analysis (unlike RunBuildVerification).
    /// </summary>
    private async Task<bool> RunQuickBuild(string projectRoot, string buildCmd, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(buildCmd)) return true;
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
        return success;
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
            client.Timeout = TimeSpan.FromMinutes(30);
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
                sb.AppendLine(Truncate(content, MaxFileContextChars));
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

        var (complete, feedback) = TryParseReviewResponse(raw);
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

    /// <summary>
    /// Tries to parse a review response JSON from the LLM, with
    /// multiple fallback strategies for common malformed outputs.
    /// Returns (null, errorMessage) on failure.
    /// </summary>
    private static (bool? complete, string? feedback) TryParseReviewResponse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (null, "Empty response");

        // Strategy 1: Try direct parse with repair
        foreach (var candidate in GetReviewJsonCandidates(raw))
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                var c = doc.RootElement.TryGetProperty("complete", out var cp) &&
                        (cp.ValueKind == JsonValueKind.True ||
                         (cp.ValueKind == JsonValueKind.String &&
                          string.Equals(cp.GetString(), "true", StringComparison.OrdinalIgnoreCase)));
                var f = doc.RootElement.TryGetProperty("feedback", out var fb) ? fb.GetString() : null;
                return (c, f);
            }
            catch { }
        }

        return (null, "Failed to parse review JSON");
    }

    /// <summary>
    /// Generates candidate JSON strings from raw LLM output,
    /// trying increasingly aggressive repair strategies.
    /// </summary>
    private static IEnumerable<string> GetReviewJsonCandidates(string raw)
    {
        var trimmed = raw.Trim();

        // Strip markdown fences
        if (trimmed.StartsWith("```"))
        {
            var m = Regex.Match(trimmed, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (m.Success) trimmed = m.Groups[1].Value.Trim();
        }

        // Extract JSON object
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start) yield break;
        var json = trimmed.Substring(start, end - start + 1);

        // Candidate 1: raw extracted JSON
        yield return json;

        // Candidate 2: run existing repair
        var repaired = RepairJsonString(json);
        if (repaired != null) yield return repaired;

        // Candidate 3: quote unquoted property names
        var quoted = QuoteJsonKeys(json);
        if (quoted != json) yield return quoted;

        // Candidate 4: repair + quote
        if (repaired != null)
        {
            var quotedRepaired = QuoteJsonKeys(repaired);
            if (quotedRepaired != repaired) yield return quotedRepaired;
        }

        // Candidate 5: try extracting JSON blocks
        foreach (var block in ExtractJsonBlocks(trimmed))
        {
            yield return block;
            var br = RepairJsonString(block);
            if (br != null) yield return br;
            var bq = QuoteJsonKeys(block);
            if (bq != block) yield return bq;
        }
    }

    /// <summary>
    /// Quotes unquoted JSON property names (e.g. {error: "msg"} → {"error": "msg"}).
    /// </summary>
    private static string QuoteJsonKeys(string json)
    {
        // Match property names that are NOT already quoted:
        // After { or , skip whitespace, capture identifier chars, then must be followed by :
        var result = Regex.Replace(json,
            @"(?<=[\{\,])\s*([a-zA-Z_$][a-zA-Z0-9_$]*)\s*(?=:)",
            m =>
            {
                var val = m.Value;
                var name = m.Groups[1].Value;
                // Preserve leading whitespace, add quotes around name, no trailing space
                return val.Substring(0, m.Groups[1].Index - m.Index) + "\"" + name + "\"";
            });
        return result;
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
    /// <summary>
    /// Extracts a keyword-relevant section of a large file with line numbers.
    /// Always includes the first <paramref name="headerLines"/> lines (globals,
    /// imports, controller init) plus a window centred on the highest-scoring
    /// anchor line.  Total output is kept within ~<paramref name="maxChars"/>.
    /// </summary>
    private static string ExtractRelevantFileSection(
        string content, string taskHint, int maxChars = 16_000, int headerLines = 60)
    {
        var lines = content.Split('\n');

        // Small file: number every line and return the whole thing.
        if (content.Length <= maxChars)
            return BuildNumberedLines(lines, 0, lines.Length - 1);

        // Score each line by keyword overlap with the task description.
        var keywords = ExtractTaskKeywords(taskHint);
        int bestLine = 0, bestScore = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            int score = keywords.Sum(kw =>
                line.Contains(kw, StringComparison.OrdinalIgnoreCase) ? 1 : 0);

            // Extra weight for function declarations that mention a keyword.
            if ((line.Contains("function") || line.Contains("= function")) && score > 0)
                score += 2;

            if (score > bestScore) { bestScore = score; bestLine = i; }
        }

        // Budget: header block + separator text + window block.
        int headerEnd = Math.Min(headerLines - 1, lines.Length - 1);

        // Window: 40 lines before the best anchor, up to ~200 lines total.
        int windowSize = 200;
        int winStart = Math.Max(headerEnd + 1, bestLine - 40);
        int winEnd = Math.Min(lines.Length - 1, winStart + windowSize - 1);

        var sb = new StringBuilder();
        sb.AppendLine($"// ── file header (lines 1–{headerEnd + 1}) ─────────────");
        sb.Append(BuildNumberedLines(lines, 0, headerEnd));

        if (winStart > headerEnd + 1)
            sb.AppendLine($"// ── … {winStart - headerEnd - 1} lines omitted … ──────────────");

        sb.AppendLine($"// ── relevant section (lines {winStart + 1}–{winEnd + 1}) ───────────");
        sb.Append(BuildNumberedLines(lines, winStart, winEnd));

        if (winEnd < lines.Length - 1)
            sb.AppendLine($"// ── … {lines.Length - 1 - winEnd} more lines not shown … ──────");

        return sb.ToString();
    }

    /// <summary>Formats lines[from..to] with 1-based line-number prefixes.</summary>
    private static string BuildNumberedLines(string[] lines, int from, int to)
    {
        var sb = new StringBuilder((to - from + 1) * 60);
        for (int i = from; i <= to && i < lines.Length; i++)
            sb.AppendLine($"{i + 1,5}: {lines[i]}");
        return sb.ToString();
    }

    /// <summary>
    /// Splits a task hint into meaningful keyword tokens (≥3 chars, no stop words).
    /// </summary>
    private static string[] ExtractTaskKeywords(string hint)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the","and","for","from","that","this","with","when","should","not",
            "into","back","have","been","will","make","card","file","code","line",
            "its","set","get","put","use","let","var","new","old","all","any","can"
        };

        return hint.Split(new[] { ' ', '\n', '\r', '.', ',', '(', ')', '{', '}', '[', ']', '"', '\'', ';' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !stopWords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
    }

    /// <summary>
    /// Returns (startLine, endLine) of the most relevant contiguous block in
    /// <paramref name="content"/> for <paramref name="taskHint"/>, using the
    /// same keyword-scoring logic as ExtractRelevantFileSection.
    /// Small files return the full range.  The caller may re-slice the original
    /// lines array to obtain a raw code snippet — no line‑number prefixes.
    /// </summary>
    private static (int start, int end) FindRelevantLines(string content, string taskHint)
    {
        var lines = content.Split('\n');
        if (lines.Length <= 200)
            return (0, lines.Length - 1);

        var keywords = ExtractTaskKeywords(taskHint);
        if (keywords.Length == 0)
            return (0, Math.Min(199, lines.Length - 1));

        int bestLine = 0, bestScore = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            int score = keywords.Sum(kw =>
                lines[i].Contains(kw, StringComparison.OrdinalIgnoreCase) ? 1 : 0);
            if ((lines[i].Contains("function") || lines[i].Contains("= function")) && score > 0)
                score += 2;
            if (score > bestScore) { bestScore = score; bestLine = i; }
        }

        if (bestScore <= 0)
            return (0, Math.Min(199, lines.Length - 1));

        var start = Math.Max(0, bestLine - 40);
        var end = Math.Min(lines.Length - 1, start + 199);
        return (start, end);
    }

    private async Task<string?> GetBuildCommand()
    {
        var cfg = await _configFile.LoadConfigAsync();
        return cfg.buildCommands;
    }

    /** <summary> Checks if a path is a relative path (not absolute) and contains directory separators, 
     indicating it's likely a file path rather than a simple filename. </summary>  */
    private static bool IsRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (Path.IsPathRooted(path)) return false;

        // Special action markers are NOT file paths
        var specialMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "_git", "_ping", "_show", "_display", "_create_file", "_package_install"
    };
        return !specialMarkers.Contains(path);
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