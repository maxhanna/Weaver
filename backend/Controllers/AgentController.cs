using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MaestroBackend.Services;

[ApiController]
[Route("api/agent")]
public class AgentController : ControllerBase
{
    private const int DefaultMaxIterations = 4;
    private const int DefaultMaxStepsPerBatch = 6;
    private const int MaxFileContextChars = 12000;
    private const int MaxObservationChars = 8000;
    private const int MaxReadOutputChars = 6000;
    private const int MaxWebResponseChars = 8000;

    private readonly IHttpClientFactory _clientFactory;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly TerminalService _terminal;

    public AgentController(IHttpClientFactory cf, IConfiguration config, IWebHostEnvironment env, TerminalService terminal)
    {
        _clientFactory = cf;
        _config = config;
        _env = env;
        _terminal = terminal;
    }

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

    public class CommandAction
    {
        public string Command { get; set; } = "";
    }

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
        public bool? Complete { get; set; }
    }

    public class AgentResponse
    {
        public string Thinking { get; set; } = "";
        public string Summary { get; set; } = "";
        public bool Complete { get; set; }
        public List<AgentStep> Steps { get; set; } = new();
    }

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

    private string ResolveWorkspaceRoot()
    {
        var configuredRoot = _config.GetValue<string>("Editor:WorkspaceRoot");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            return Path.IsPathRooted(configuredRoot) ? configuredRoot : Path.GetFullPath(Path.Combine(_env.ContentRootPath, configuredRoot));
        return Path.GetFullPath(Path.Combine(_env.ContentRootPath, ".."));
    }

    private string GetProjectRoot(string project)
    {
        var workspaceRoot = ResolveWorkspaceRoot();
        var projectSegment = string.IsNullOrWhiteSpace(project) ? "" : project.Trim().TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(workspaceRoot, projectSegment));
    }

    private static bool IsPathUnderRoot(string fullPath, string root)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        fullPath = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            return true;
        var rootPrefix = root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLineEndings(string s) => s.Replace("\r\n", "\n");

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "\n…(truncated)";

    private static string NormalizeUiStatus(string? status) =>
        status switch
        {
            "written" or "ok" or "created" => "done",
            "running" => "running",
            "error" => "error",
            _ => status ?? "pending"
        };

    private static readonly HashSet<string> ExplorationStepTypes = new(StringComparer.OrdinalIgnoreCase)
        { "read", "list", "glob", "grep", "web" };

    private static bool HasSuccessfulEdits(IEnumerable<object> steps) =>
        steps.OfType<Dictionary<string, object?>>().Any(s =>
            s.TryGetValue("type", out var t) && string.Equals(t?.ToString(), "edit", StringComparison.OrdinalIgnoreCase) &&
            s.TryGetValue("status", out var st) && st?.ToString() == "done");

    private static bool TaskExpectsFileChanges(string prompt)
    {
        var lower = prompt.ToLowerInvariant();
        string[] verbs =
        {
            "add", "implement", "fix", "update", "change", "create", "modify", "remove", "delete",
            "refactor", "edit", "write", "toggle", "enable", "disable", "insert", "set", "make",
            "build", "install", "configure", "hook", "wire", "connect", "show", "hide", "display",
            "save", "persist", "store", "expose", "include"
        };
        return verbs.Any(v => lower.Contains(v, StringComparison.Ordinal));
    }

    private static bool BatchWasExplorationOnly(IReadOnlyList<AgentStep> batch) =>
        batch.Count > 0 && batch.All(s => ExplorationStepTypes.Contains(s.Type ?? ""));

    private async Task EmitLog(bool emit, string level, string message, object? detail = null)
    {
        if (!emit) return;
        await SendSse(Response, "log", new
        {
            ts = DateTime.UtcNow.ToString("o"),
            level,
            message,
            detail
        });
    }

    private static List<string> ExtractSearchKeywords(string prompt)
    {
        var result = new List<string>();
        string[] priority = { "settings", "terminal", "popup", "panel", "toggle", "config", "visibility", "maestro", "showSettingsPanel", "autoQueue" };
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

    private static List<string> FindLikelyFiles(string prompt, string projectRoot)
    {
        var matches = new List<string>();
        if (!Directory.Exists(projectRoot)) return matches;

        var lower = prompt.ToLowerInvariant();
        var presets = new[]
        {
            "backend/wwwroot/app.js", "backend/wwwroot/index.html",
            "wwwroot/app.js", "wwwroot/index.html",
            "app.js", "index.html"
        };
        foreach (var p in presets)
        {
            var full = Path.Combine(projectRoot, p.Replace('/', Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(full))
                matches.Add(p.Replace('\\', '/'));
        }

        var nameHints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (lower.Contains("setting") || lower.Contains("popup") || lower.Contains("panel") || lower.Contains("toggle"))
        {
            nameHints.Add("app.js"); nameHints.Add("index.html"); nameHints.Add("settings");
        }
        if (lower.Contains("terminal"))
        {
            nameHints.Add("app.js"); nameHints.Add("index.html"); nameHints.Add("terminal");
        }

        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "node_modules", ".git", "bin", "obj", "dist", ".angular", "packages" };

        foreach (var file in Directory.EnumerateFiles(projectRoot, "*.*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
            if (skip.Any(s => rel.Contains("/" + s + "/", StringComparison.OrdinalIgnoreCase))) continue;
            var name = Path.GetFileName(file);
            if (nameHints.Any(h => rel.Contains(h, StringComparison.OrdinalIgnoreCase) || name.Contains(h, StringComparison.OrdinalIgnoreCase)))
                matches.Add(rel);
            if (matches.Count >= 12) break;
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

    private async Task<(string discoveryText, List<object> steps)> RunBootstrapDiscovery(
        string prompt, string projectRoot, bool emitSse)
    {
        await EmitLog(emitSse, "info", "Auto-discovering project files (list → grep → read)…");

        var plan = new List<AgentStep>();
        var idx = 0;
        plan.Add(new AgentStep
        {
            Index = idx++,
            Type = "list",
            Path = "",
            Description = "Auto: list project root"
        });

        foreach (var kw in ExtractSearchKeywords(prompt))
        {
            plan.Add(new AgentStep
            {
                Index = idx++,
                Type = "grep",
                Query = kw,
                Description = $"Auto: search codebase for '{kw}'"
            });
        }

        foreach (var file in FindLikelyFiles(prompt, projectRoot))
        {
            plan.Add(new AgentStep
            {
                Index = idx++,
                Type = "read",
                Path = file,
                Description = $"Auto: read candidate file {file}"
            });
        }

        // Windows-friendly discovery via terminal
        plan.Add(new AgentStep
        {
            Index = idx++,
            Type = "command",
            Command = OperatingSystem.IsWindows()
                ? "dir /s /b app.js index.html 2>nul | more"
                : "find . -name 'app.js' -o -name 'index.html' 2>/dev/null | head -20",
            Description = "Auto: locate app.js / index.html via shell"
        });

        var steps = await ExecuteSteps(plan, projectRoot, 0, emitSse);

        var sb = new StringBuilder();
        sb.AppendLine("ONLY use paths that appear below. Do NOT invent paths like src/components/ unless listed.");
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

        await EmitLog(emitSse, "info", $"Discovery complete ({steps.Count} bootstrap steps)");
        return (sb.ToString(), steps);
    }

    private async Task<(string discoveryText, List<object> steps)> RunLightBootstrap(
        List<string> attachedFiles, string projectRoot, bool emitSse)
    {
        await EmitLog(emitSse, "info", "Light bootstrap: reading attached files only");

        var plan = attachedFiles
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select((f, i) => new AgentStep
            {
                Index = i,
                Type = "read",
                Path = f.Replace('\\', '/'),
                Description = $"Read attached {f}"
            })
            .ToList();

        if (plan.Count == 0)
            return ("", new List<object>());

        var steps = await ExecuteSteps(plan, projectRoot, 0, emitSse);
        var sb = new StringBuilder();
        sb.AppendLine("Attached files (edit these paths only):");
        foreach (var f in attachedFiles)
            sb.AppendLine($"  - {f.Replace('\\', '/')}");
        foreach (var item in steps)
        {
            if (item is Dictionary<string, object?> r && r.TryGetValue("output", out var o) && o != null)
                sb.AppendLine($"\n### {r.GetValueOrDefault("path")}\n{Truncate(o.ToString() ?? "", 3000)}");
        }
        return (sb.ToString(), steps);
    }

    private string GetLlamaBaseUrl()
    {
        var configPath = Path.Combine(_env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot"), "config.json");
        var baseUrl = "http://192.168.2.58:8080";
        if (System.IO.File.Exists(configPath))
        {
            try
            {
                var configText = System.IO.File.ReadAllText(configPath);
                var configJson = JsonSerializer.Deserialize<JsonElement>(configText);
                if (configJson.TryGetProperty("LlamaUrl", out var llamaUrlElement))
                    baseUrl = llamaUrlElement.GetString() ?? baseUrl;
            }
            catch { }
        }
        return baseUrl.TrimEnd('/');
    }

    [HttpPost("execute")]
    public async Task<IActionResult> Execute([FromBody] AgentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt))
            return BadRequest("Prompt is required");

        var projectRoot = GetProjectRoot(req.Project);
        var fileContents = await ReadAttachedFiles(req.Files, projectRoot);
        var maxIter = req.MaxIterations ?? DefaultMaxIterations;
        var maxBatch = req.MaxStepsPerBatch ?? DefaultMaxStepsPerBatch;

        var (discoveryText, bootstrapSteps) = await RunBootstrapDiscovery(req.Prompt, projectRoot, emitSse: false);
        var (allSteps, agentResp, error) = await RunAgentLoop(
            req.Prompt, fileContents, discoveryText, projectRoot, maxIter, maxBatch,
            stream: false, bootstrapSteps, req.Files);

        if (agentResp == null)
            return Ok(new { error = error ?? "Failed to parse AI response", steps = allSteps });

        return Ok(new
        {
            thinking = agentResp.Thinking,
            summary = agentResp.Summary,
            complete = agentResp.Complete,
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
                    var output = _terminal.ReadLastLines(50);
                    commandResults.Add(new { command = cmd.Command, status = "done", output });
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
            var fileContents = await ReadAttachedFiles(req.Files, projectRoot);
            var maxIter = req.MaxIterations ?? DefaultMaxIterations;
            var maxBatch = req.MaxStepsPerBatch ?? DefaultMaxStepsPerBatch;

            await SendSse(Response, "phase", new { phase = "start", projectRoot, maxIter, maxBatch });
            await EmitLog(true, "info", "Agent run started", new { projectRoot, task = req.Prompt });

            List<object> allSteps;
            AgentResponse? agentResp;
            string? error;

            var useFastPath = (req.Files?.Count > 0) && TaskExpectsFileChanges(req.Prompt);
            if (useFastPath)
            {
                await SendSse(Response, "phase", new { phase = "fast", message = "Attached files — applying edits (per-file)…" });
                await EmitLog(true, "info", "Fast path: skipping heavy discovery + plan LLM");
                var (discoveryText, bootstrapSteps) = await RunLightBootstrap(req.Files, projectRoot, emitSse: true);
                allSteps = new List<object>(bootstrapSteps);
                var editResults = await RunDedicatedEditPhase(
                    req.Prompt, req.Files, discoveryText, projectRoot, allSteps.Count, stream: true);
                allSteps.AddRange(editResults);
                agentResp = new AgentResponse
                {
                    Summary = HasSuccessfulEdits(editResults) ? "Edits applied (fast path)" : "Edit phase completed",
                    Complete = HasSuccessfulEdits(editResults)
                };
                error = null;
            }
            else
            {
                await SendSse(Response, "phase", new { phase = "discover", message = "Scanning project…" });
                var (discoveryText, bootstrapSteps) = await RunBootstrapDiscovery(req.Prompt, projectRoot, emitSse: true);
                (allSteps, agentResp, error) = await RunAgentLoop(
                    req.Prompt, fileContents, discoveryText, projectRoot, maxIter, maxBatch,
                    stream: true, bootstrapSteps, req.Files);
            }

            var targetPaths = ResolveEditTargetPaths(req.Prompt, req.Files ?? new List<string>(), projectRoot);
            var editsApplied = HasSuccessfulEdits(allSteps);
            var requirementsMet = editsApplied || TaskRequirementsMet(req.Prompt, targetPaths, projectRoot, allSteps);
            if (agentResp == null && !requirementsMet)
                await SendSse(Response, "error", new { message = error ?? "Failed to parse AI response" });

            await SendSse(Response, "done", new
            {
                thinking = agentResp?.Thinking ?? "",
                summary = agentResp?.Summary ?? (requirementsMet ? "Task completed" : "No edits applied"),
                complete = requirementsMet,
                editsApplied = requirementsMet,
                incomplete = TaskExpectsFileChanges(req.Prompt) && !requirementsMet,
                warning = !requirementsMet && TaskExpectsFileChanges(req.Prompt)
                    ? "No files were modified. Check failed steps below."
                    : null,
                steps = allSteps,
                filesEdited = ExtractFilesEdited(allSteps)
            });
        }
        catch (Exception ex)
        {
            await SendSse(Response, "error", new { message = ex.Message });
            await SendSse(Response, "done", new { });
        }
    }

    private async Task<(List<object> steps, AgentResponse? lastResponse, string? error)> RunAgentLoop(
        string prompt, string fileContents, string discoveryContext, string projectRoot,
        int maxIterations, int maxStepsPerBatch, bool stream, List<object>? bootstrapSteps,
        List<string>? attachedFiles = null)
    {
        var allSteps = bootstrapSteps != null ? new List<object>(bootstrapSteps) : new List<object>();
        var observations = new StringBuilder();
        var discoveryBuilder = new StringBuilder(discoveryContext ?? "");
        if (discoveryBuilder.Length > 0)
            observations.AppendLine(discoveryBuilder.ToString());
        if (bootstrapSteps != null)
            AppendObservations(observations, bootstrapSteps);

        AgentResponse? lastResponse = null;
        string? lastError = null;
        var globalStepIndex = allSteps.Count;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            if (stream)
                await SendSse(Response, "phase", new { phase = "llm", iteration, message = iteration == 0 ? "Asking model (batch 1)…" : $"Model follow-up (batch {iteration + 1})…" });

            await EmitLog(stream, "info", iteration == 0 ? "Calling LLM for plan + steps…" : $"Calling LLM for batch {iteration + 1}…");

            var discoverySnapshot = discoveryBuilder.ToString();
            var enrichedContext = fileContents;
            if (!string.IsNullOrWhiteSpace(discoverySnapshot))
                enrichedContext += "\n\n## Project discovery\n" + discoverySnapshot;

            var (raw, agentResp, parseError) = await CallLlm(
                prompt, enrichedContext, discoverySnapshot, projectRoot, observations.ToString(),
                iteration, maxStepsPerBatch, stream: false);

            lastResponse = agentResp;
            lastError = parseError;

            if (agentResp == null)
            {
                await EmitLog(stream, "error", "Failed to parse model response", new { error = parseError });
                break;
            }

            await EmitLog(stream, "info", $"Model returned {agentResp.Steps.Count} step(s), complete={agentResp.Complete}");

            if (stream && !string.IsNullOrWhiteSpace(agentResp.Thinking))
                await SendSse(Response, "thinking", new { text = agentResp.Thinking, iteration });

            if (stream && !string.IsNullOrWhiteSpace(agentResp.Summary))
                await SendSse(Response, "summary", new { text = agentResp.Summary, iteration });

            var batch = agentResp.Steps.Take(maxStepsPerBatch).ToList();
            if (batch.Count == 0)
            {
                await EmitLog(stream, "warn", "Model returned zero steps");
                break;
            }

            await EmitLog(stream, "info", $"Executing {batch.Count} step(s)…", new
            {
                steps = batch.Select(s => new { s.Type, s.Path, s.Command, s.Description }).ToList()
            });

            var batchResults = await ExecuteSteps(batch, projectRoot, globalStepIndex, stream);
            globalStepIndex += batch.Count;
            allSteps.AddRange(batchResults);

            AppendObservations(observations, batchResults);

            if (observations.Length > MaxObservationChars)
            {
                var trimmed = observations.ToString();
                observations.Clear();
                observations.Append(Truncate(trimmed, MaxObservationChars));
            }

            var expectsEdits = TaskExpectsFileChanges(prompt);
            var anyEditsDone = HasSuccessfulEdits(allSteps);
            var hasEditErrors = batchResults.Any(r =>
                r is Dictionary<string, object?> d &&
                d.TryGetValue("type", out var t) && t?.ToString() == "edit" &&
                d.TryGetValue("status", out var st) && st?.ToString() == "error");

            var pathNotFound = batchResults.Any(r =>
                r is Dictionary<string, object?> d &&
                d.TryGetValue("error", out var err) &&
                (err?.ToString()?.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ?? false));

            if (pathNotFound && iteration < maxIterations - 1)
            {
                await EmitLog(stream, "warn", "Model used wrong paths — re-scanning project…");
                var (extraDiscovery, extraSteps) = await RunBootstrapDiscovery(prompt, projectRoot, stream);
                discoveryBuilder.AppendLine(extraDiscovery);
                allSteps.AddRange(extraSteps);
                AppendObservations(observations, extraSteps);
                globalStepIndex += extraSteps.Count;
                continue;
            }

            // Small models often set complete:true after a read — ignore until edits land
            var modelSaysComplete = agentResp.Complete;
            if (modelSaysComplete && expectsEdits && !anyEditsDone)
                modelSaysComplete = false;

            if (modelSaysComplete && (!expectsEdits || anyEditsDone))
                break;

            if (iteration >= maxIterations - 1)
                break;

            if (hasEditErrors)
                continue;

            // Model returned another read/grep batch — run dedicated edit phase immediately
            if (expectsEdits && !anyEditsDone && BatchWasExplorationOnly(batch))
            {
                var inlineEdits = await RunDedicatedEditPhase(
                    prompt, attachedFiles ?? new List<string>(), discoveryBuilder.ToString(),
                    projectRoot, globalStepIndex, stream);
                globalStepIndex += inlineEdits.Count;
                allSteps.AddRange(inlineEdits);
                if (HasSuccessfulEdits(allSteps))
                    break;
            }

            // Keep going: read-only batch but task still needs file changes
            if (expectsEdits && !anyEditsDone)
                continue;

            if (!modelSaysComplete)
                continue;

            break;
        }

        // Dedicated edit phase — full file bodies, edit-only JSON (models often skip edits in the main loop)
        if (TaskExpectsFileChanges(prompt) && !HasSuccessfulEdits(allSteps))
        {
            var editPhaseResults = await RunDedicatedEditPhase(
                prompt, attachedFiles ?? new List<string>(), discoveryBuilder.ToString(),
                projectRoot, globalStepIndex, stream);
            globalStepIndex += editPhaseResults.Count;
            allSteps.AddRange(editPhaseResults);
            if (editPhaseResults.Count > 0)
                lastResponse = new AgentResponse { Summary = "Dedicated edit phase", Complete = HasSuccessfulEdits(editPhaseResults) };
        }

        return (allSteps, lastResponse, lastError);
    }

    private async Task<string> BuildFullFileContextAsync(IEnumerable<string> relativePaths, string projectRoot, int maxTotalChars = 120000)
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
            sb.AppendLine("```");
            sb.AppendLine(text);
            sb.AppendLine("```");
            sb.AppendLine();
            total += text.Length;
            if (total >= maxTotalChars) break;
        }
        return sb.ToString();
    }

    private static List<string> ResolveEditTargetPaths(string prompt, List<string> attachedFiles, string projectRoot)
    {
        var paths = attachedFiles.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Replace('\\', '/')).ToList();
        foreach (var likely in FindLikelyFiles(prompt, projectRoot))
        {
            if (!paths.Contains(likely, StringComparer.OrdinalIgnoreCase))
                paths.Add(likely);
        }
        if (paths.Count == 0)
            paths = FindLikelyFiles(prompt, projectRoot);

        if (prompt.Contains("config.json", StringComparison.OrdinalIgnoreCase) &&
            !paths.Any(p => p.EndsWith("config.json", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var p in new[] { "backend/wwwroot/config.json", "wwwroot/config.json" })
            {
                var full = Path.Combine(projectRoot, p.Replace('/', Path.DirectorySeparatorChar));
                if (System.IO.File.Exists(full)) paths.Add(p);
            }
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<List<object>> RunDedicatedEditPhase(
        string prompt, List<string> attachedFiles, string discoveryContext, string projectRoot,
        int startIndex, bool stream)
    {
        var results = new List<object>();
        var targetPaths = ResolveEditTargetPaths(prompt, attachedFiles, projectRoot);
        if (targetPaths.Count == 0)
        {
            await EmitLog(stream, "error", "Edit phase: no target files found");
            return results;
        }

        if (stream)
            await SendSse(Response, "phase", new { phase = "edit-phase", message = $"Generating edits for {targetPaths.Count} file(s)…" });

        await EmitLog(stream, "info", "Edit phase (per-file first)", new { files = targetPaths });

        var idx = startIndex;
        foreach (var path in targetPaths)
        {
            if (!path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            var full = Path.GetFullPath(Path.Combine(projectRoot, path.Replace('/', Path.DirectorySeparatorChar)));
            var fileExists = System.IO.File.Exists(full);

            if (stream)
                await SendSse(Response, "phase", new { phase = "edit-file", message = $"Editing {path}…" });

            try
            {
                var content = fileExists
                    ? await System.IO.File.ReadAllTextAsync(full, Encoding.UTF8)
                    : "";

                if (!fileExists && path.EndsWith("config.json", StringComparison.OrdinalIgnoreCase))
                {
                    await EmitLog(stream, "info", $"Creating missing {path}");
                    content = "";
                }
                else if (!fileExists)
                {
                    await EmitLog(stream, "warn", $"File not found: {path}");
                    continue;
                }

                var fileEdits = new List<AgentStep>();

                for (var attempt = 0; attempt < 2 && fileEdits.Count == 0; attempt++)
                {
                    await EmitLog(stream, "info", $"LLM edit: {path} (attempt {attempt + 1})", new { chars = content.Length });
                    var (raw, _, err) = await CallLlmSingleFileEdit(prompt, path, content, projectRoot, attempt);

                    await EmitLog(stream, "debug", $"LLM raw response for {path}",
                        new { raw = raw ?? "", error = err });

                    fileEdits = ParseEditsFromLlmRaw(raw, path);

                    if (fileEdits.Count == 0)
                    {
                        await EmitLog(stream, "warn", $"Parse failed for {path}",
                            new { error = err, raw = raw ?? "" });
                    }
                    else
                    {
                        foreach (var fe in fileEdits)
                        {
                            await EmitLog(stream, "debug", $"Extracted edit for {path}",
                                new
                                {
                                    oldString = fe.OldString,
                                    newString = fe.NewString,
                                    description = fe.Description
                                });
                        }
                    }
                }

                if (fileEdits.Count == 0)
                {
                    fileEdits = TryHeuristicPatches(prompt, path, content);
                    if (fileEdits.Count > 0)
                        await EmitLog(stream, "info", $"Applied built-in patch for {path}");
                }

                if (fileEdits.Count == 0 && IsTaskAlreadySatisfied(prompt, path, content))
                {
                    await EmitLog(stream, "info", $"{path}: task already implemented in file");
                    results.Add(new Dictionary<string, object?>
                    {
                        ["index"] = idx++,
                        ["type"] = "note",
                        ["path"] = path,
                        ["description"] = "Task already implemented in this file",
                        ["status"] = "done"
                    });
                    continue;
                }

                if (fileEdits.Count == 0)
                {
                    await EmitLog(stream, "error", $"No edits for {path} — model output was not usable JSON");
                    continue;
                }

                var batch = await ExecuteSteps(fileEdits, projectRoot, idx, stream);
                foreach (var r in batch)
                {
                    if (r is Dictionary<string, object?> stepResult)
                    {
                        var status = stepResult.GetValueOrDefault("status")?.ToString();
                        var err = stepResult.GetValueOrDefault("error")?.ToString();
                        if (!string.IsNullOrEmpty(err) || status == "error")
                        {
                            await EmitLog(stream, "error", $"Edit step failed for {stepResult.GetValueOrDefault("path") ?? path}",
                                new
                                {
                                    error = err,
                                    oldStringPreview = stepResult.GetValueOrDefault("oldStringPreview"),
                                    snippet = stepResult.GetValueOrDefault("snippet"),
                                    suggestions = stepResult.GetValueOrDefault("suggestions")
                                });
                        }
                        else if (status == "skipped")
                        {
                            await EmitLog(stream, "info", $"Edit skipped for {stepResult.GetValueOrDefault("path") ?? path} — no changes needed");
                        }
                    }
                }
                results.AddRange(batch);
                idx += batch.Count;
            }
            catch (Exception ex)
            {
                await EmitLog(stream, "error", $"Edit failed for {path}: {ex.Message}");
            }
        }

        // Optional: all-files batch only if per-file did nothing (smaller models may timeout on huge prompt)
        if (!HasSuccessfulEdits(results) && targetPaths.Count == 1)
        {
            var fullFiles = await BuildFullFileContextAsync(targetPaths, projectRoot, maxTotalChars: 40000);
            if (!string.IsNullOrWhiteSpace(fullFiles))
            {
                try
                {
                    await EmitLog(stream, "info", "Single-file combined edit attempt");
                    var (raw, agentResp, err) = await CallLlmEditsOnly(prompt, fullFiles, discoveryContext, projectRoot, 0);
                    if (agentResp?.Steps != null)
                    {
                        var edits = agentResp.Steps.Where(s => string.Equals(s.Type, "edit", StringComparison.OrdinalIgnoreCase)).ToList();
                        if (edits.Count > 0)
                        {
                            var batchResults = await ExecuteSteps(edits, projectRoot, idx, stream);
                            results.AddRange(batchResults);
                        }
                    }
                }
                catch (Exception ex)
                {
                    await EmitLog(stream, "error", $"Combined edit failed: {ex.Message}");
                }
            }
        }

        return results;
    }

    private async Task<(string raw, AgentResponse? parsed, string? error)> CallLlmEditsOnly(
        string taskPrompt, string fullFileContext, string discoveryContext, string projectRoot, int attempt)
    {
        var systemPrompt = @"You MUST output file edits as JSON. Do not only plan or read — apply changes.

Output ONLY this JSON (no markdown):
{
  ""thinking"": ""short"",
  ""summary"": ""edits applied"",
  ""complete"": true,
  ""steps"": [
    {
      ""index"": 0,
      ""type"": ""edit"",
      ""description"": ""what changed"",
      ""path"": ""exact/path/from/FILE/headers"",
      ""oldString"": ""verbatim text from file including whitespace"",
      ""newString"": ""replacement""
    }
  ]
}

Rules:
- steps must ALL be type ""edit"".
- path must match a ### FILE: header path exactly.
- oldString must appear exactly once in that file.
- For config.json: preserve valid JSON; use POST-worthy structure (projects, defaultProject, showTerminal, etc.).";

        var user = new StringBuilder();
        user.AppendLine("## Task");
        user.AppendLine(taskPrompt);
        user.AppendLine();
        user.AppendLine("## Project root");
        user.AppendLine(projectRoot);
        if (!string.IsNullOrWhiteSpace(discoveryContext))
        {
            user.AppendLine();
            user.AppendLine("## Discovery notes");
            user.AppendLine(Truncate(discoveryContext, 4000));
        }
        user.AppendLine();
        user.AppendLine("## Full files to edit (copy oldString from here)");
        user.AppendLine(fullFileContext);
        if (attempt > 0)
            user.AppendLine("\n## Retry: previous attempt produced no valid edits. Output correct edit steps now.");

        try
        {
            return await CallLlmNonStreaming(
                _clientFactory.CreateClient("llama"),
                GetLlamaBaseUrl() + "/v1/chat/completions",
                _config.GetValue<string>("Ai:Model") ?? "medgemma:4b",
                new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = user.ToString() }
                });
        }
        catch (TaskCanceledException ex)
        {
            return ("", null, "LLM request timed out. Try a smaller model or fewer files per card.");
        }
        catch (Exception ex)
        {
            return ("", null, ex.Message);
        }
    }

    private async Task<(string raw, AgentResponse? parsed, string? error)> CallLlmSingleFileEdit(
        string taskPrompt, string relativePath, string fileContent, string projectRoot, int attempt = 0)
    {
        var systemPrompt = @"You are a code patch tool. Output ONLY valid JSON, no markdown, no explanation.

Format:
{""edits"":[{""oldString"":""exact text copied from file"",""newString"":""replacement""}]}

Rules:
- oldString MUST be copied verbatim from the file (2-8 lines with exact spaces).
- Include at least one edit in the edits array.
- Do not wrap in ``` fences.";

        var user = new StringBuilder();
        user.AppendLine($"Task: {taskPrompt}");
        user.AppendLine($"File path: {relativePath}");
        if (attempt > 0)
            user.AppendLine("RETRY: Your last reply was not valid JSON. Output ONLY the edits JSON object.");
        user.AppendLine();
        user.AppendLine("FILE CONTENT:");
        user.AppendLine("```");
        user.AppendLine(Truncate(fileContent, 24000));
        user.AppendLine("```");

        try
        {
            var (raw, parsed, err) = await CallLlmNonStreaming(
                _clientFactory.CreateClient("llama"),
                GetLlamaBaseUrl() + "/v1/chat/completions",
                _config.GetValue<string>("Ai:Model") ?? "medgemma:4b",
                new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = user.ToString() }
                });

            var steps = ParseEditsFromLlmRaw(raw, relativePath);
            if (steps.Count > 0)
                return (raw, new AgentResponse { Steps = steps, Summary = "Parsed edits" }, null);

            return (raw, parsed, err ?? "No edits in response");
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
                    string.IsNullOrEmpty(s.OldString) && string.IsNullOrEmpty(s.NewString))
                    continue;
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

        // Try parsing the full block, then fall back to individual brace blocks
        var blocks = ExtractJsonBlocks(jsonStr);
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
                        var envelope = JsonSerializer.Deserialize<MinimalEditsEnvelope>(candidate, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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

        // Fallback: character-by-character extraction of oldString/newString pairs
        if (steps.Count == 0)
        {
            steps = ExtractEditPairs(jsonStr, defaultPath);
        }

        return steps;
    }

    private static List<AgentStep> ExtractEditPairs(string text, string defaultPath)
    {
        var steps = new List<AgentStep>();
        var i = 0;

        while (i < text.Length)
        {
            var oldKeyIdx = text.IndexOf("\"oldString\"", i, StringComparison.OrdinalIgnoreCase);
            var newKeyIdx = text.IndexOf("\"newString\"", i, StringComparison.OrdinalIgnoreCase);

            if (oldKeyIdx < 0 || newKeyIdx < 0) break;

            // Determine which comes first
            string firstKey, secondKey;
            int firstIdx, secondIdx;
            if (oldKeyIdx < newKeyIdx)
            {
                firstKey = "oldString"; secondKey = "newString";
                firstIdx = oldKeyIdx; secondIdx = newKeyIdx;
            }
            else
            {
                firstKey = "newString"; secondKey = "oldString";
                firstIdx = newKeyIdx; secondIdx = oldKeyIdx;
            }

            var firstVal = ExtractJsonStringValue(text, firstIdx + firstKey.Length);
            if (firstVal == null) { i = firstIdx + 1; continue; }

            var secKeyPos = text.IndexOf("\"" + secondKey + "\"", firstVal.Value.EndPos, StringComparison.OrdinalIgnoreCase);
            if (secKeyPos < 0) { i = firstIdx + 1; continue; }

            var secVal = ExtractJsonStringValue(text, secKeyPos + secondKey.Length);
            if (secVal == null) { i = firstIdx + 1; continue; }

            var oldStr = firstKey == "oldString" ? firstVal.Value.Text : secVal.Value.Text;
            var newStr = firstKey == "newString" ? firstVal.Value.Text : secVal.Value.Text;

            if (!string.IsNullOrEmpty(oldStr) || !string.IsNullOrEmpty(newStr))
            {
                steps.Add(new AgentStep
                {
                    Index = steps.Count,
                    Type = "edit",
                    Path = defaultPath,
                    OldString = oldStr ?? "",
                    NewString = newStr ?? "",
                    Description = "LLM edit (extracted)"
                });
            }

            i = secVal.Value.EndPos;
        }

        return steps;
    }

    private static (string Text, int EndPos)? ExtractJsonStringValue(string text, int keyEndPos)
    {
        // Skip past "key":  — find the colon and opening quote
        var pos = keyEndPos;
        while (pos < text.Length && text[pos] != ':') pos++;
        if (pos >= text.Length) return null;
        pos++; // skip ':'
        while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
        if (pos >= text.Length || text[pos] != '"') return null;
        pos++; // skip opening '"'

        var start = pos;
        while (pos < text.Length)
        {
            if (text[pos] == '\\')
            {
                pos += 2; // skip escape sequence
                continue;
            }
            if (text[pos] == '"')
            {
                var rawValue = text.Substring(start, pos - start);
                return (UnescapeJsonString(rawValue), pos + 1);
            }
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
            if (s[i] != '\\')
            {
                sb.Append(s[i]);
                continue;
            }
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
                    if (i + 4 < s.Length &&
                        int.TryParse(s.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code))
                    {
                        sb.Append((char)code);
                        i += 4;
                    }
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
        var inString = false;
        var changed = false;

        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];

            if (!inString)
            {
                if (c == '"')
                    inString = true;
                sb.Append(c);
                continue;
            }

            // Inside a string value
            if (c == '\\')
            {
                sb.Append(c);
                i++;
                if (i < json.Length) sb.Append(json[i]);
                continue;
            }

            if (c == '"')
            {
                var nextNonWs = -1;
                for (var j = i + 1; j < json.Length; j++)
                {
                    if (!char.IsWhiteSpace(json[j]))
                    {
                        nextNonWs = j;
                        break;
                    }
                }

                if (nextNonWs >= 0 && (json[nextNonWs] == ',' || json[nextNonWs] == '}' || json[nextNonWs] == ']' || json[nextNonWs] == ':'))
                {
                    sb.Append(c);
                    inString = false;
                }
                else
                {
                    sb.Append("\\\"");
                    changed = true;
                }
                continue;
            }

            if (c == '\n')
            {
                sb.Append("\\n");
                changed = true;
                continue;
            }
            if (c == '\r')
            {
                sb.Append("\\r");
                changed = true;
                continue;
            }
            if (c == '\t')
            {
                sb.Append("\\t");
                changed = true;
                continue;
            }

            sb.Append(c);
        }

        var result = sb.ToString();
        return changed ? result : null;
    }

    private static List<string> ExtractJsonBlocks(string text)
    {
        var blocks = new List<string>();
        var depth = 0;
        var start = -1;
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (inString)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == '"') inString = false;
                continue;
            }
            if (text[i] == '"') { inString = true; continue; }
            if (text[i] == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    blocks.Add(text.Substring(start, i - start + 1));
                    start = -1;
                }
            }
        }
        return blocks;
    }

    private static List<AgentStep> TryHeuristicPatches(string prompt, string relativePath, string content)
    {
        var steps = new List<AgentStep>();
        var task = prompt.ToLowerInvariant();
        var path = relativePath.Replace('\\', '/').ToLowerInvariant();

        if (!task.Contains("terminal") && !task.Contains("showterminal"))
            return steps;

        if (path.EndsWith("index.html"))
        {
            if (!content.Contains("vm.showTerminal"))
            {
                const string oldBlock = "        <div class=\"form-row\">\r\n          <label>\r\n            <input type=\"checkbox\" ng-model=\"vm.autoQueue\" /> Auto-queue: process next Todo card when one completes\r\n          </label>\r\n        </div>";
                const string newBlock = oldBlock + "\r\n        <div class=\"form-row\">\r\n          <label>\r\n            <input type=\"checkbox\" ng-model=\"vm.showTerminal\" /> Show terminal panel (saved to config.json)\r\n          </label>\r\n        </div>";
                if (content.Contains("vm.autoQueue") && content.Contains("Auto-queue"))
                {
                    steps.Add(new AgentStep { Index = 0, Type = "edit", Path = relativePath, OldString = NormalizeLineEndings(oldBlock), NewString = NormalizeLineEndings(newBlock), Description = "Add terminal toggle (heuristic)" });
                }
            }
            if (!content.Contains("ng-if=\"vm.showTerminal\"") && content.Contains("class=\"panel term-panel\""))
            {
                steps.Add(new AgentStep
                {
                    Index = steps.Count,
                    Type = "edit",
                    Path = relativePath,
                    OldString = "<div class=\"panel term-panel\">",
                    NewString = "<div class=\"panel term-panel\" ng-if=\"vm.showTerminal\">",
                    Description = "Hide terminal panel when off (heuristic)"
                });
            }
        }

        if (path.EndsWith("app.js"))
        {
            if (!content.Contains("vm.showTerminal"))
            {
                steps.Add(new AgentStep
                {
                    Index = steps.Count,
                    Type = "edit",
                    Path = relativePath,
                    OldString = "    vm.autoQueue = true;",
                    NewString = "    vm.autoQueue = true;\r\n    vm.showTerminal = true;",
                    Description = "Add showTerminal state (heuristic)"
                });
            }
            if (!content.Contains("cfg.showTerminal"))
            {
                steps.Add(new AgentStep
                {
                    Index = steps.Count,
                    Type = "edit",
                    Path = relativePath,
                    OldString = "        vm.defaultProject = cfg.defaultProject;",
                    NewString = "        vm.defaultProject = cfg.defaultProject;\r\n        if (typeof cfg.showTerminal === 'boolean') vm.showTerminal = cfg.showTerminal;",
                    Description = "Load showTerminal from config (heuristic)"
                });
            }
        }

        if (path.EndsWith("config.json"))
        {
            if (string.IsNullOrWhiteSpace(content) || content.Trim() == "{}")
            {
                steps.Add(new AgentStep
                {
                    Index = 0,
                    Type = "edit",
                    Path = relativePath,
                    OldString = "",
                    NewString = "{\r\n  \"projects\": [],\r\n  \"defaultProject\": \"..\",\r\n  \"showTerminal\": true\r\n}\r\n",
                    Description = "Create config.json with showTerminal (heuristic)"
                });
            }
            else if (!content.Contains("showTerminal"))
            {
                var trimmed = content.TrimEnd();
                if (trimmed.EndsWith("}"))
                {
                    var insert = trimmed.TrimEnd('}').TrimEnd() + ",\r\n  \"showTerminal\": true\r\n}";
                    steps.Add(new AgentStep
                    {
                        Index = 0,
                        Type = "edit",
                        Path = relativePath,
                        OldString = content,
                        NewString = insert,
                        Description = "Add showTerminal to config.json (heuristic)"
                    });
                }
            }
        }

        return steps.Where(s => !string.IsNullOrEmpty(s.NewString) || !string.IsNullOrEmpty(s.OldString)).ToList();
    }

    private static bool IsTaskAlreadySatisfied(string prompt, string relativePath, string content)
    {
        var task = prompt.ToLowerInvariant();
        if (!task.Contains("terminal") && !task.Contains("show") && !task.Contains("hide"))
            return false;

        var path = relativePath.Replace('\\', '/').ToLowerInvariant();
        if (path.EndsWith("index.html"))
            return content.Contains("vm.showTerminal", StringComparison.OrdinalIgnoreCase) &&
                   content.Contains("ng-if=\"vm.showTerminal\"", StringComparison.OrdinalIgnoreCase);
        if (path.EndsWith("app.js"))
            return content.Contains("vm.showTerminal", StringComparison.OrdinalIgnoreCase) &&
                   content.Contains("cfg.showTerminal", StringComparison.OrdinalIgnoreCase);
        if (path.EndsWith("config.json"))
            return content.Contains("showTerminal", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static bool TaskRequirementsMet(string prompt, List<string> targetPaths, string projectRoot, List<object> steps)
    {
        if (HasSuccessfulEdits(steps)) return true;

        foreach (var rel in targetPaths)
        {
            var full = Path.GetFullPath(Path.Combine(projectRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!System.IO.File.Exists(full))
            {
                if (rel.EndsWith("config.json", StringComparison.OrdinalIgnoreCase) &&
                    prompt.Contains("config", StringComparison.OrdinalIgnoreCase))
                    return false;
                continue;
            }
            var content = System.IO.File.ReadAllText(full);
            if (!IsTaskAlreadySatisfied(prompt, rel, content))
                return false;
        }
        return targetPaths.Count > 0;
    }

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
                    var paths = sug is IEnumerable<string> ss ? ss : (sug as System.Collections.IEnumerable)?.Cast<object>().Select(x => x?.ToString() ?? "") ?? Array.Empty<string>();
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

    private static List<object> ExtractFilesEdited(List<object> steps)
    {
        return steps
            .OfType<Dictionary<string, object?>>()
            .Where(s => s.TryGetValue("type", out var t) && t?.ToString() == "edit" && s.TryGetValue("status", out var st) && st?.ToString() == "done")
            .Select(s => new
            {
                path = s.GetValueOrDefault("path"),
                action = s.GetValueOrDefault("editAction"),
                linesAdded = s.GetValueOrDefault("linesAdded"),
                linesRemoved = s.GetValueOrDefault("linesRemoved"),
                preview = s.GetValueOrDefault("diffPreview")
            })
            .Cast<object>()
            .ToList();
    }

    private async Task<(string raw, AgentResponse? parsed, string? error)> CallLlm(
        string prompt, string fileContents, string discoveryContext, string projectRoot, string observations,
        int iteration, int maxSteps, bool stream)
    {
        var systemPrompt = BuildSystemPrompt(maxSteps);
        var userMessage = BuildUserMessage(prompt, projectRoot, fileContents, discoveryContext, observations, iteration);

        var baseUrl = GetLlamaBaseUrl();
        var target = baseUrl + "/v1/chat/completions";
        var client = _clientFactory.CreateClient("llama");
        var model = _config.GetValue<string>("Ai:Model") ?? "medgemma:4b";

        var messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userMessage }
        };

        if (stream)
            await SendSse(Response, "phase", new { phase = "llm", message = "Waiting for model response…" });

        return await CallLlmNonStreaming(client, target, model, messages);
    }

    private static string BuildSystemPrompt(int maxSteps) => $@"You are Maestro — a fast, tool-using coding agent on a Kanban board.

KANBAN RULES (critical for small models):
- This card is ONE small slice of work. Max {maxSteps} steps this batch.
- If the task changes code/UI/config, you MUST include ""edit"" steps (read + edit in the SAME batch is best).
- NEVER set ""complete"": true unless edit steps are included and the task is finished.
- A batch with only ""read"" is incomplete — set ""complete"": false.
- Each edit: copy oldString EXACTLY from the file content (3–8 lines of context).
- Set ""complete"": true only after all required edits are listed in steps.

TOOLS (step types):
- read — read a file (path)
- edit — find/replace (path, oldString, newString); new file: oldString=""""
- command — shell in project root (command)
- list — list directory (path, optional)
- glob — find files (pattern e.g. **/*.cs)
- grep — search text in project (query, optional path directory)
- web — fetch URL (url) for docs/APIs

Respond ONLY with JSON (no markdown):
{{
  ""thinking"": ""brief plan"",
  ""summary"": ""what this batch will do"",
  ""complete"": false,
  ""steps"": [
    {{ ""index"": 0, ""type"": ""read"", ""description"": ""..."", ""path"": ""src/foo.js"" }}
  ]
}}

Paths use / relative to project root. Commands run with cwd = project root.
NEVER invent paths (e.g. src/components/SettingsPopup.jsx) unless they appear in Project discovery.
Before edit: read the file OR use exact content from discovery. Use list/grep/command to find real paths.";

    private static string BuildUserMessage(string prompt, string projectRoot, string fileContents,
        string discoveryContext, string observations, int iteration)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Task");
        sb.AppendLine(prompt);
        sb.AppendLine();
        sb.AppendLine("## Project root (use this for all paths)");
        sb.AppendLine(projectRoot);

        if (!string.IsNullOrWhiteSpace(discoveryContext))
        {
            sb.AppendLine();
            sb.AppendLine("## Project discovery (REAL paths — only edit files listed here)");
            sb.AppendLine(discoveryContext);
        }

        if (TaskExpectsFileChanges(prompt))
        {
            sb.AppendLine();
            sb.AppendLine("## Requirement");
            sb.AppendLine("This task requires file changes. Use paths from discovery (e.g. wwwroot/app.js, wwwroot/index.html).");
            sb.AppendLine("If unsure: add grep/list/read steps first — do NOT guess React paths.");
        }

        if (iteration > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## Batch {iteration + 1} — CONTINUE (edits required)");
            sb.AppendLine("You already read files below. Output edit step(s) NOW with exact oldString from that content.");
            sb.AppendLine("Do NOT output another read-only batch. Do NOT set complete:true without edit steps.");
            sb.AppendLine();
            sb.AppendLine(observations.Length > 0 ? observations.ToString() : "(no observations)");
        }

        if (!string.IsNullOrWhiteSpace(fileContents))
        {
            sb.AppendLine();
            sb.AppendLine("## Attached files");
            sb.AppendLine(Truncate(fileContents, MaxFileContextChars));
        }
        return sb.ToString();
    }

    private async Task<(string raw, AgentResponse? parsed, string? error)> CallLlmMandatoryEdits(
        string prompt, string observations, string discoveryContext, string projectRoot)
    {
        var systemPrompt = @"You are Maestro completing a task that MUST modify files.
The previous agent pass failed or used wrong paths. This is your last chance.

Respond ONLY with JSON:
{
  ""thinking"": ""brief"",
  ""summary"": ""edits being applied"",
  ""complete"": true,
  ""steps"": [
    { ""index"": 0, ""type"": ""edit"", ""description"": ""..."", ""path"": ""relative/path"", ""oldString"": ""exact existing text"", ""newString"": ""replacement"" }
  ]
}

Rules:
- steps must be type ""edit"" only.
- path MUST be from Project discovery (e.g. backend/wwwroot/app.js or wwwroot/index.html) — NEVER invent src/components paths.
- oldString must be copied EXACTLY from FILE content in observations.
- Maestro Kanban UI: backend/wwwroot/app.js, backend/wwwroot/index.html, backend/wwwroot/config.json (from repo root).
- Persist UI settings via config.json field showTerminal (bool); save uses POST /api/config/save.";

        var userMessage = new StringBuilder();
        userMessage.AppendLine("## Task to complete");
        userMessage.AppendLine(prompt);
        userMessage.AppendLine();
        userMessage.AppendLine("## Project root");
        userMessage.AppendLine(projectRoot);
        userMessage.AppendLine();
        userMessage.AppendLine("## Project discovery");
        userMessage.AppendLine(discoveryContext);
        userMessage.AppendLine();
        userMessage.AppendLine("## File contents (copy oldString from here)");
        userMessage.AppendLine(observations.Length > 0 ? observations.ToString() : "(missing)");

        var baseUrl = GetLlamaBaseUrl();
        var target = baseUrl + "/v1/chat/completions";
        var client = _clientFactory.CreateClient("llama");
        var model = _config.GetValue<string>("Ai:Model") ?? "medgemma:4b";

        var messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userMessage.ToString() }
        };

        return await CallLlmNonStreaming(client, target, model, messages);
    }

    private async Task<(string, AgentResponse?, string?)> CallLlmNonStreaming(HttpClient client, string target, string model, object messages)
    {
        var requestBody = new
        {
            model,
            messages,
            stream = false,
            temperature = 0.05,
            max_tokens = 4096
        };
        var contentJson = JsonSerializer.Serialize(requestBody);
        var httpContent = new StringContent(contentJson, Encoding.UTF8, "application/json");

        var resp = await client.PostAsync(target, httpContent);
        var respText = await resp.Content.ReadAsStringAsync();

        var llmContent = ExtractLlmContent(respText);
        if (string.IsNullOrWhiteSpace(llmContent))
            return (respText, null, "Empty LLM response");

        var parsed = ParseAgentResponse(llmContent);
        return (llmContent, parsed, parsed == null ? "JSON parse failed" : null);
    }

    private async Task<(string, AgentResponse?, string?)> CallLlmStreaming(HttpClient client, string target, string model, object messages)
    {
        var requestBody = new { model, messages, stream = true, temperature = 0.15 };
        var contentJson = JsonSerializer.Serialize(requestBody);
        var httpContent = new StringContent(contentJson, Encoding.UTF8, "application/json");

        using var httpResp = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, target) { Content = httpContent },
            HttpCompletionOption.ResponseHeadersRead);
        httpResp.EnsureSuccessStatusCode();

        using var stream = await httpResp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        var fullContent = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ")) continue;
            var data = line.Substring(6).Trim();
            if (data == "[DONE]") break;

            try
            {
                using var doc = JsonDocument.Parse(data);
                if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    continue;
                var delta = choices[0].GetProperty("delta");
                if (!delta.TryGetProperty("content", out var contentProp)) continue;
                var token = contentProp.GetString() ?? "";
                fullContent.Append(token);
            }
            catch { }
        }

        var llmContent = fullContent.ToString();
        if (string.IsNullOrWhiteSpace(llmContent))
            return (llmContent, null, "Empty LLM response");

        var parsed = ParseAgentResponse(llmContent);
        return (llmContent, parsed, parsed == null ? "JSON parse failed" : null);
    }

    private static string ExtractLlmContent(string respText)
    {
        try
        {
            using var doc = JsonDocument.Parse(respText);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
                    return c.GetString() ?? "";
            }
        }
        catch { }
        return "";
    }

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
        if (start >= 0 && end > start)
            jsonStr = jsonStr.Substring(start, end - start + 1);

        try
        {
            var parsed = JsonSerializer.Deserialize<AgentResponse>(jsonStr, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed != null && parsed.Steps.Count > 0)
                return parsed;
        }
        catch { }

        try
        {
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var steps = JsonSerializer.Deserialize<List<AgentStep>>(jsonStr, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (steps != null && steps.Count > 0)
                    return new AgentResponse { Steps = steps, Summary = "Parsed steps array" };
            }
            if (root.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
            {
                var steps = JsonSerializer.Deserialize<List<AgentStep>>(stepsEl.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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

    private async Task<List<object>> ExecuteSteps(List<AgentStep> steps, string projectRoot, int indexOffset, bool emitSse)
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
                await EmitLog(emitSse, "step", $"▶ {step.Type}: {step.Description ?? step.Path ?? step.Command ?? step.Query ?? ""}");
                await SendSse(Response, "step", result);
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
                await EmitLog(emitSse, st == "error" ? "error" : "info", $"✓ {step.Type} finished ({st})",
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

        return results;
    }

    private async Task ExecuteEditStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var relPath = (step.Path ?? "").Replace('/', Path.DirectorySeparatorChar);
        var targetPath = Path.GetFullPath(Path.Combine(projectRoot, relPath));
        if (!IsPathUnderRoot(targetPath, projectRoot))
        {
            result["status"] = "error";
            result["error"] = "Path outside project root";
            return;
        }

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
        {
            result["status"] = "skipped";
            result["path"] = step.Path;
            return;
        }

        await System.IO.File.WriteAllTextAsync(targetPath, newContent, Encoding.UTF8);
        PopulateEditResult(result, "modified", step.Path!, oldString, newString, newContent);
    }

    private static void PopulateEditResult(Dictionary<string, object?> result, string action, string path, string? oldStr, string? newStr, string writtenContent)
    {
        result["status"] = "done";
        result["editAction"] = action;
        result["path"] = path;
        var oldLines = (oldStr ?? "").Split('\n').Length;
        var newLines = (newStr ?? "").Split('\n').Length;
        result["linesRemoved"] = oldLines;
        result["linesAdded"] = newLines;
        if (!string.IsNullOrEmpty(oldStr))
            result["oldStringPreview"] = Truncate(oldStr, 300);
        if (!string.IsNullOrEmpty(newStr))
            result["newStringPreview"] = Truncate(newStr, 300);
        result["diffPreview"] = BuildDiffPreview(oldStr, newStr);
    }

    private static string BuildDiffPreview(string? oldStr, string? newStr)
    {
        if (string.IsNullOrEmpty(oldStr) && !string.IsNullOrEmpty(newStr))
            return "+ " + newStr.Replace("\n", "\n+ ").TrimEnd();
        if (string.IsNullOrEmpty(newStr) && !string.IsNullOrEmpty(oldStr))
            return "- " + oldStr.Replace("\n", "\n- ").TrimEnd();
        return $"--- removed ({(oldStr ?? "").Split('\n').Length} lines)\n+++ added ({(newStr ?? "").Split('\n').Length} lines)";
    }

    private static (bool ok, string content, string? error, string? snippet) TryReplace(string content, string oldString, string newString)
    {
        var searchContent = NormalizeLineEndings(content);
        var searchOld = NormalizeLineEndings(oldString);
        var idx = searchContent.IndexOf(searchOld, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var result = searchContent.Substring(0, idx) + newString + searchContent.Substring(idx + searchOld.Length);
            return (true, result, null, null);
        }

        // Whitespace-flexible fallback
        var flexPattern = Regex.Escape(searchOld).Replace(@"\ ", @"\s+");
        try
        {
            var m = Regex.Match(searchContent, flexPattern, RegexOptions.Multiline);
            if (m.Success)
            {
                var result = searchContent.Substring(0, m.Index) + newString + searchContent.Substring(m.Index + m.Length);
                return (true, result, null, null);
            }
        }
        catch { }

        var lines = searchContent.Split('\n');
        var oldLines = searchOld.Split('\n');
        var hint = oldLines.Length > 0 ? string.Join("\n", lines.Where(l => l.Contains(oldLines[0].Trim(), StringComparison.OrdinalIgnoreCase)).Take(3)) : null;
        return (false, content, $"oldString not found in file", hint != null ? Truncate(hint, 400) : null);
    }

    private async Task ExecuteCommandStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var command = step.Command ?? "";
        if (string.IsNullOrWhiteSpace(command))
        {
            result["status"] = "error";
            result["error"] = "No command provided";
            return;
        }

        await _terminal.SendCommandAsync(command, projectRoot);
        await Task.Delay(1000);
        var output = _terminal.ReadLastLines(200);

        result["status"] = "done";
        result["command"] = command;
        result["output"] = Truncate(output, MaxReadOutputChars);
    }

    private async Task ExecuteReadStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var relPath = (step.Path ?? "").Replace('/', Path.DirectorySeparatorChar);
        var targetPath = Path.GetFullPath(Path.Combine(projectRoot, relPath));
        if (!IsPathUnderRoot(targetPath, projectRoot))
        {
            result["status"] = "error";
            result["error"] = "Path outside project root";
            return;
        }

        if (!System.IO.File.Exists(targetPath))
        {
            result["status"] = "error";
            result["error"] = "File not found";
            return;
        }

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
        {
            result["status"] = "error";
            result["error"] = "Path outside project root";
            return Task.CompletedTask;
        }

        if (!Directory.Exists(targetPath))
        {
            result["status"] = "error";
            result["error"] = "Directory not found";
            return Task.CompletedTask;
        }

        var entries = Directory.GetFileSystemEntries(targetPath)
            .Select(e =>
            {
                var name = Path.GetFileName(e);
                var isDir = Directory.Exists(e);
                return (isDir ? "[dir]  " : "[file] ") + name;
            })
            .OrderBy(x => x)
            .Take(200);

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

            var list = files
                .Where(f => IsPathUnderRoot(f, projectRoot))
                .Take(100)
                .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/'))
                .ToList();

            result["status"] = "done";
            result["output"] = list.Count == 0 ? "(no matches)" : string.Join("\n", list);
        }
        catch (Exception ex)
        {
            result["status"] = "error";
            result["error"] = ex.Message;
        }
        return Task.CompletedTask;
    }

    private Task ExecuteGrepStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var query = step.Query ?? step.Pattern ?? "";
        if (string.IsNullOrWhiteSpace(query))
        {
            result["status"] = "error";
            result["error"] = "grep requires query";
            return Task.CompletedTask;
        }

        var searchRoot = projectRoot;
        if (!string.IsNullOrWhiteSpace(step.Path))
        {
            searchRoot = Path.GetFullPath(Path.Combine(projectRoot, step.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsPathUnderRoot(searchRoot, projectRoot))
            {
                result["status"] = "error";
                result["error"] = "Path outside project root";
                return Task.CompletedTask;
            }
        }

        var matches = new List<string>();
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules", ".git", "bin", "obj", "dist", ".angular" };

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
                        var rel = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
                        matches.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
                        if (matches.Count >= 50) break;
                    }
                }
                catch { }

                if (matches.Count >= 50) break;
            }

            result["status"] = "done";
            result["output"] = matches.Count == 0 ? "(no matches)" : string.Join("\n", matches);
        }
        catch (Exception ex)
        {
            result["status"] = "error";
            result["error"] = ex.Message;
        }
        return Task.CompletedTask;
    }

    private async Task ExecuteWebStep(AgentStep step, Dictionary<string, object?> result)
    {
        var url = step.Url ?? step.Path ?? "";
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            result["status"] = "error";
            result["error"] = "Valid absolute url required for web step";
            return;
        }

        try
        {
            var client = _clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Maestro-Agent/1.0");
            var resp = await client.GetAsync(uri);
            var body = await resp.Content.ReadAsStringAsync();
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "text/plain";

            result["status"] = "done";
            result["url"] = url;
            result["output"] = Truncate($"HTTP {(int)resp.StatusCode} ({contentType})\n{body}", MaxWebResponseChars);
        }
        catch (Exception ex)
        {
            result["status"] = "error";
            result["error"] = ex.Message;
        }
    }

    private async Task<List<EditResult>> ApplyEditsDirect(List<EditAction> edits, string projectRoot)
    {
        var results = new List<EditResult>();
        var fileGroups = new Dictionary<string, List<EditAction>>(StringComparer.OrdinalIgnoreCase);
        var fileOrder = new List<string>();

        foreach (var edit in edits)
        {
            if (!fileGroups.ContainsKey(edit.Path))
            {
                fileGroups[edit.Path] = new List<EditAction>();
                fileOrder.Add(edit.Path);
            }
            fileGroups[edit.Path].Add(edit);
        }

        foreach (var filePath in fileOrder)
        {
            var fileEdits = fileGroups[filePath];
            var targetPath = Path.GetFullPath(Path.Combine(projectRoot, filePath));
            if (!IsPathUnderRoot(targetPath, projectRoot))
            {
                foreach (var _ in fileEdits)
                    results.Add(new EditResult { Path = filePath, Status = "skipped", Error = "Path outside project root" });
                continue;
            }

            string content = "";
            var fileExists = System.IO.File.Exists(targetPath);
            if (fileExists)
                content = await System.IO.File.ReadAllTextAsync(targetPath, Encoding.UTF8);
            else if (fileEdits.Any(e => !string.IsNullOrEmpty(e.OldString)))
            {
                foreach (var e in fileEdits)
                    results.Add(new EditResult { Path = filePath, Status = "skipped", Error = "File does not exist" });
                continue;
            }

            var hasError = false;
            foreach (var edit in fileEdits)
            {
                if (!fileExists && string.IsNullOrEmpty(edit.OldString))
                {
                    content = edit.NewString ?? "";
                    continue;
                }
                if (string.IsNullOrEmpty(edit.OldString))
                {
                    content += edit.NewString ?? "";
                    continue;
                }

                var (ok, newContent, err, _) = TryReplace(content, edit.OldString, edit.NewString ?? "");
                if (!ok)
                {
                    results.Add(new EditResult { Path = filePath, Status = "error", Error = err });
                    hasError = true;
                    break;
                }
                content = newContent;
            }

            if (!hasError)
            {
                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
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

    private static async Task SendSse(HttpResponse response, string eventName, object data)
    {
        var json = JsonSerializer.Serialize(data);
        await response.WriteAsync($"event: {eventName}\ndata: {json}\n\n");
        await response.Body.FlushAsync();
    }
}
