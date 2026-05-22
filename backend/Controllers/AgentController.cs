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
        root = Path.GetFullPath(root);
        fullPath = Path.GetFullPath(fullPath);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
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

        var (allSteps, agentResp, error) = await RunAgentLoop(
            req.Prompt, fileContents, projectRoot, maxIter, maxBatch, stream: false);

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

            var (allSteps, agentResp, error) = await RunAgentLoop(
                req.Prompt, fileContents, projectRoot, maxIter, maxBatch, stream: true);

            if (agentResp == null)
            {
                await SendSse(Response, "error", new { message = error ?? "Failed to parse AI response" });
                await SendSse(Response, "done", new { steps = allSteps, filesEdited = ExtractFilesEdited(allSteps) });
                return;
            }

            await SendSse(Response, "done", new
            {
                thinking = agentResp.Thinking,
                summary = agentResp.Summary,
                complete = agentResp.Complete,
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
        string prompt, string fileContents, string projectRoot, int maxIterations, int maxStepsPerBatch, bool stream)
    {
        var allSteps = new List<object>();
        var observations = new StringBuilder();
        AgentResponse? lastResponse = null;
        string? lastError = null;
        var globalStepIndex = 0;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            if (stream)
                await SendSse(Response, "phase", new { phase = "llm", iteration, message = iteration == 0 ? "Planning…" : "Continuing with observations…" });

            var (raw, agentResp, parseError) = await CallLlm(
                prompt, fileContents, projectRoot, observations.ToString(), iteration, maxStepsPerBatch, stream && iteration == 0);

            lastResponse = agentResp;
            lastError = parseError;

            if (agentResp == null)
                break;

            if (stream && !string.IsNullOrWhiteSpace(agentResp.Thinking))
                await SendSse(Response, "thinking", new { text = agentResp.Thinking, iteration });

            if (stream && !string.IsNullOrWhiteSpace(agentResp.Summary))
                await SendSse(Response, "summary", new { text = agentResp.Summary, iteration });

            var batch = agentResp.Steps.Take(maxStepsPerBatch).ToList();
            if (batch.Count == 0)
                break;

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

            // Keep going: read-only batch but task still needs file changes
            if (expectsEdits && !anyEditsDone)
                continue;

            if (!modelSaysComplete)
                continue;

            break;
        }

        // Last resort: force an edit-only LLM pass when reads happened but no files changed
        if (TaskExpectsFileChanges(prompt) && !HasSuccessfulEdits(allSteps))
        {
            if (stream)
                await SendSse(Response, "phase", new { phase = "mandatory-edits", message = "Applying required file changes…" });

            var (rawM, agentRespM, _) = await CallLlmMandatoryEdits(
                prompt, observations.ToString(), projectRoot);

            if (agentRespM != null)
            {
                lastResponse = agentRespM;
                if (stream && !string.IsNullOrWhiteSpace(agentRespM.Thinking))
                    await SendSse(Response, "thinking", new { text = agentRespM.Thinking, iteration = "final" });
                if (stream && !string.IsNullOrWhiteSpace(agentRespM.Summary))
                    await SendSse(Response, "summary", new { text = agentRespM.Summary, iteration = "final" });

                var editBatch = agentRespM.Steps
                    .Where(s => string.Equals(s.Type, "edit", StringComparison.OrdinalIgnoreCase))
                    .Take(maxStepsPerBatch)
                    .ToList();
                if (editBatch.Count == 0)
                    editBatch = agentRespM.Steps.Take(maxStepsPerBatch).ToList();

                if (editBatch.Count > 0)
                {
                    var mandatoryResults = await ExecuteSteps(editBatch, projectRoot, globalStepIndex, stream);
                    globalStepIndex += editBatch.Count;
                    allSteps.AddRange(mandatoryResults);
                    AppendObservations(observations, mandatoryResults);
                }
            }
        }

        return (allSteps, lastResponse, lastError);
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
        string prompt, string fileContents, string projectRoot, string observations,
        int iteration, int maxSteps, bool stream)
    {
        var systemPrompt = BuildSystemPrompt(maxSteps);
        var userMessage = BuildUserMessage(prompt, projectRoot, fileContents, observations, iteration);

        var baseUrl = GetLlamaBaseUrl();
        var target = baseUrl + "/v1/chat/completions";
        var client = _clientFactory.CreateClient("llama");
        var model = _config.GetValue<string>("Ai:Model") ?? "medgemma:4b";

        var messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userMessage }
        };

        return stream
            ? await CallLlmStreaming(client, target, model, messages)
            : await CallLlmNonStreaming(client, target, model, messages);
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

Paths use / relative to project root. Commands run with cwd = project root.";

    private static string BuildUserMessage(string prompt, string projectRoot, string fileContents, string observations, int iteration)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Task");
        sb.AppendLine(prompt);
        sb.AppendLine();
        sb.AppendLine("## Project root (use this for all paths)");
        sb.AppendLine(projectRoot);

        if (TaskExpectsFileChanges(prompt))
        {
            sb.AppendLine();
            sb.AppendLine("## Requirement");
            sb.AppendLine("This task requires file changes. Include edit step(s). Do not mark complete without edits.");
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
        string prompt, string observations, string projectRoot)
    {
        var systemPrompt = @"You are Maestro completing a task that MUST modify files.
The previous agent pass only read files — no edits were applied. This is your last chance.

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
- steps must be type ""edit"" only (no read/list).
- oldString must be copied EXACTLY from the file content in observations.
- You may include multiple edit steps for the same file.
- path is relative to project root, use / separators.";

        var userMessage = new StringBuilder();
        userMessage.AppendLine("## Task to complete");
        userMessage.AppendLine(prompt);
        userMessage.AppendLine();
        userMessage.AppendLine("## Project root");
        userMessage.AppendLine(projectRoot);
        userMessage.AppendLine();
        userMessage.AppendLine("## File contents from prior reads (copy oldString from here)");
        userMessage.AppendLine(observations.Length > 0 ? observations.ToString() : "(missing — use attached context if any)");

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
        var requestBody = new { model, messages, stream = false, temperature = 0.15 };
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
                await SendSse(Response, "status", new { message = "Generating plan…" });
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

    private AgentResponse? ParseAgentResponse(string raw)
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
            return JsonSerializer.Deserialize<AgentResponse>(jsonStr, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
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
                await SendSse(Response, "step", result);

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
                await SendSse(Response, "step", result);
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
            result["status"] = "error";
            result["error"] = "File does not exist";
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
