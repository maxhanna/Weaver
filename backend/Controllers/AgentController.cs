using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using MaestroBackend.Services;

[ApiController]
[Route("api/agent")]
public class AgentController : ControllerBase
{
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
        public string? Status { get; set; }
        public string? Output { get; set; }
        public string? Error { get; set; }
    }

    public class AgentResponse
    {
        public string Thinking { get; set; } = "";
        public string Summary { get; set; } = "";
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

    private static string NormalizeLineEndings(string s)
    {
        return s.Replace("\r\n", "\n");
    }

    // ============================================================
    // POST /api/agent/execute - Non-streaming (legacy)
    // ============================================================
    [HttpPost("execute")]
    public async Task<IActionResult> Execute([FromBody] AgentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt))
            return BadRequest("Prompt is required");

        var projectRoot = GetProjectRoot(req.Project);
        var fileContents = await ReadAttachedFiles(req.Files, projectRoot);

        var (llmRaw, agentResp) = await CallLlm(req.Prompt, fileContents, stream: false);

        if (agentResp == null)
            return Ok(new { error = "Failed to parse AI response", raw = llmRaw });

        var stepResults = await ExecuteSteps(agentResp.Steps, projectRoot);

        return Ok(new
        {
            thinking = agentResp.Thinking,
            summary = agentResp.Summary,
            steps = stepResults
        });
    }

    // ============================================================
    // POST /api/agent/apply - Apply edits directly (no LLM call)
    // ============================================================
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
                    await _terminal.SendCommandAsync(cmd.Command);
                    await Task.Delay(800);
                    var output = _terminal.ReadLastLines(50);
                    commandResults.Add(new { command = cmd.Command, status = "ok", output });
                }
                catch (Exception ex)
                {
                    commandResults.Add(new { command = cmd.Command, status = "error", error = ex.Message });
                }
            }
        }

        return Ok(new { edits = editResults, commands = commandResults });
    }

    // ============================================================
    // POST /api/agent/execute-stream - Streaming multi-step agent
    // ============================================================
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

            var (llmRaw, agentResp) = await CallLlm(req.Prompt, fileContents, stream: true);

            if (agentResp == null)
            {
                await SendSse(Response, "error", new { message = "Failed to parse AI response", raw = llmRaw });
                await SendSse(Response, "done", new { });
                return;
            }

            // Execute steps
            var stepResults = await ExecuteSteps(agentResp.Steps, projectRoot);

            await SendSse(Response, "done", new
            {
                thinking = agentResp.Thinking,
                summary = agentResp.Summary,
                steps = stepResults
            });
        }
        catch (Exception ex)
        {
            await SendSse(Response, "error", new { message = ex.Message });
            await SendSse(Response, "done", new { });
        }
    }

    // ============================================================
    // Core: Call LLM with comprehensive system prompt
    // ============================================================
    private async Task<(string rawContent, AgentResponse? parsed)> CallLlm(string prompt, string fileContents, bool stream)
    {
        var systemPrompt = @"You are Maestro — an autonomous AI agent integrated with a Kanban board. You receive tasks and execute them on the user's machine.

CAPABILITIES:
1. Read files — use a ""read"" step to inspect file contents
2. Edit files — use an ""edit"" step with find-and-replace (oldString must match exactly, preserve whitespace)
3. Run commands — use a ""command"" step to execute shell commands (cmd/powershell on Windows, bash on Linux)
4. Write new files — use an ""edit"" step with oldString="""" and newString containing the full file content
5. Browse the web — use a ""command"" step with curl/powershell Invoke-WebRequest to fetch URLs
6. Plan — think step-by-step before acting

INSTRUCTIONS:
- Break the task into clear, sequential steps
- Each step must have a unique index starting from 0
- Steps run in order; later steps can depend on earlier ones
- For file edits, oldString must uniquely match the existing content (include surrounding context)
- For commands, the working directory is the project root
- After each command/read, you will see the output — use it to inform subsequent steps
- When done, provide a concise English summary

Respond ONLY with valid JSON. No markdown, no code fences:
{
  ""thinking"": ""Your step-by-step reasoning about the task"",
  ""summary"": ""Brief summary of what was done"",
  ""steps"": [
    {
      ""index"": 0,
      ""type"": ""edit|command|read"",
      ""description"": ""Human-readable description"",
      ""path"": ""relative/file/path (for edit/read)"",
      ""oldString"": ""exact text to replace (for edit)"",
      ""newString"": ""replacement text (for edit)"",
      ""command"": ""shell command to run (for command)""
    }
  ]
}

EXAMPLES:
- To edit a file: { ""type"": ""edit"", ""path"": ""src/main.js"", ""oldString"": ""old code here"", ""newString"": ""new code here"" }
- To run a command: { ""type"": ""command"", ""command"": ""npm install"", ""description"": ""Install dependencies"" }
- To read a file: { ""type"": ""read"", ""path"": ""src/main.js"", ""description"": ""Check current contents"" }
- To browse: { ""type"": ""command"", ""command"": ""curl -s https://example.com"", ""description"": ""Fetch example.com"" }
- To write a new file: { ""type"": ""edit"", ""path"": ""newfile.txt"", ""oldString"": """", ""newString"": ""File content here"" }

Rules:
- path values are relative to the project root
- Use / as path separator
- oldString must match the file content exactly (including indentation)
- Multiple edits to the same file are applied in order
- Prefer small, targeted edits over large blocks";

        var userMessage = $"## Task\n{prompt}\n\n## Project root\n{GetProjectRoot("")}\n\n## Attached files\n{fileContents}";

        var baseUrl = GetLlamaBaseUrl();
        var target = baseUrl + "/v1/chat/completions";
        var client = _clientFactory.CreateClient("llama");
        string model = _config.GetValue<string>("Ai:Model") ?? "medgemma:4b";

        var messages = new[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userMessage }
        };

        if (stream)
        {
            return await CallLlmStreaming(client, target, model, messages);
        }
        else
        {
            return await CallLlmNonStreaming(client, target, model, messages);
        }
    }

    private async Task<(string, AgentResponse?)> CallLlmNonStreaming(HttpClient client, string target, string model, object messages)
    {
        var requestBody = new { model, messages, stream = false, temperature = 0.2 };
        var contentJson = JsonSerializer.Serialize(requestBody);
        var httpContent = new StringContent(contentJson, Encoding.UTF8, "application/json");

        var resp = await client.PostAsync(target, httpContent);
        var respText = await resp.Content.ReadAsStringAsync();

        string llmContent = "";
        try
        {
            using var doc = JsonDocument.Parse(respText);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
                    llmContent = c.GetString() ?? "";
            }
        }
        catch { }

        if (string.IsNullOrWhiteSpace(llmContent))
            return (respText, null);

        var parsed = ParseAgentResponse(llmContent);
        return (llmContent, parsed);
    }

    private async Task<(string, AgentResponse?)> CallLlmStreaming(HttpClient client, string target, string model, object messages)
    {
        var requestBody = new { model, messages, stream = true, temperature = 0.2 };
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
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data: ")) continue;

            var data = line.Substring(6).Trim();
            if (data == "[DONE]") break;

            try
            {
                using var doc = JsonDocument.Parse(data);
                if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    continue;

                var delta = choices[0].GetProperty("delta");
                if (!delta.TryGetProperty("content", out var contentProp))
                    continue;

                var token = contentProp.GetString() ?? "";
                fullContent.Append(token);

                await SendSse(Response, "token", new { t = token });
            }
            catch { }
        }

        var llmContent = fullContent.ToString();
        if (string.IsNullOrWhiteSpace(llmContent))
            return (llmContent, null);

        var parsed = ParseAgentResponse(llmContent);
        return (llmContent, parsed);
    }

    private AgentResponse? ParseAgentResponse(string raw)
    {
        var jsonStr = raw;
        var start = jsonStr.IndexOf('{');
        var end = jsonStr.LastIndexOf('}');
        if (start >= 0 && end > start)
            jsonStr = jsonStr.Substring(start, end - start + 1);

        try
        {
            var resp = JsonSerializer.Deserialize<AgentResponse>(jsonStr, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return resp;
        }
        catch { return null; }
    }

    // ============================================================
    // Execute steps: edit, command, read
    // ============================================================
    private async Task<List<object>> ExecuteSteps(List<AgentStep> steps, string projectRoot)
    {
        var results = new List<object>();
        bool terminalStarted = false;

        foreach (var step in steps)
        {
            var result = new Dictionary<string, object?>
            {
                ["index"] = step.Index,
                ["type"] = step.Type,
                ["description"] = step.Description,
                ["status"] = "running"
            };

            // Send running event
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
                        await ExecuteCommandStep(step, result);
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

            results.Add(result);

            // Send completed event
            await SendSse(Response, "step", result);
        }

        return results;
    }

    private async Task ExecuteEditStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var targetPath = Path.GetFullPath(Path.Combine(projectRoot, step.Path ?? ""));
        if (!targetPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            result["status"] = "error";
            result["error"] = "Path outside project root";
            return;
        }

        var fileExists = System.IO.File.Exists(targetPath);
        var oldString = step.OldString ?? "";
        var newString = step.NewString ?? "";

        if (!fileExists)
        {
            if (string.IsNullOrEmpty(oldString) && !string.IsNullOrEmpty(newString))
            {
                // Create new file
                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                await System.IO.File.WriteAllTextAsync(targetPath, newString, Encoding.UTF8);
                result["status"] = "written";
                result["path"] = step.Path;
                result["description"] = $"Created {step.Path}";
                return;
            }
            result["status"] = "error";
            result["error"] = "File does not exist";
            return;
        }

        var content = await System.IO.File.ReadAllTextAsync(targetPath, Encoding.UTF8);

        if (string.IsNullOrEmpty(oldString))
        {
            // Append
            content += newString;
            await System.IO.File.WriteAllTextAsync(targetPath, content, Encoding.UTF8);
            result["status"] = "written";
            result["path"] = step.Path;
            return;
        }

        // Find-and-replace with line ending normalization
        var searchContent = NormalizeLineEndings(content);
        var searchOld = NormalizeLineEndings(oldString);
        var idx = searchContent.IndexOf(searchOld, StringComparison.Ordinal);

        if (idx == -1)
        {
            result["status"] = "error";
            result["error"] = $"oldString not found in {step.Path}";
            return;
        }

        content = searchContent.Substring(0, idx) + newString + searchContent.Substring(idx + searchOld.Length);
        await System.IO.File.WriteAllTextAsync(targetPath, content, Encoding.UTF8);

        result["status"] = "written";
        result["path"] = step.Path;
    }

    private async Task ExecuteCommandStep(AgentStep step, Dictionary<string, object?> result)
    {
        var command = step.Command ?? "";
        if (string.IsNullOrWhiteSpace(command))
        {
            result["status"] = "error";
            result["error"] = "No command provided";
            return;
        }

        await _terminal.SendCommandAsync(command);
        await Task.Delay(1200);
        var output = _terminal.ReadLastLines(200);

        result["status"] = "ok";
        result["command"] = command;
        result["output"] = output;
    }

    private async Task ExecuteReadStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var targetPath = Path.GetFullPath(Path.Combine(projectRoot, step.Path ?? ""));
        if (!targetPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
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
        result["status"] = "ok";
        result["path"] = step.Path;
        result["output"] = content;
    }

    // ============================================================
    // Apply edits directly (no retry, no LLM)
    // ============================================================
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
            if (!targetPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var _ in fileEdits)
                    results.Add(new EditResult { Path = filePath, Status = "skipped", Error = "Path outside project root" });
                continue;
            }

            string content = "";
            bool fileExists = System.IO.File.Exists(targetPath);
            if (fileExists)
                content = await System.IO.File.ReadAllTextAsync(targetPath, Encoding.UTF8);
            else if (fileEdits.Any(e => !string.IsNullOrEmpty(e.OldString)))
            {
                foreach (var e in fileEdits)
                    results.Add(new EditResult { Path = filePath, Status = "skipped", Error = "File does not exist" });
                continue;
            }

            bool hasError = false;
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

                content = NormalizeLineEndings(content);
                var searchOld = NormalizeLineEndings(edit.OldString);
                var idx = content.IndexOf(searchOld, StringComparison.Ordinal);
                if (idx == -1)
                {
                    results.Add(new EditResult { Path = filePath, Status = "error", Error = "oldString not found" });
                    hasError = true;
                    break;
                }
                content = content.Substring(0, idx) + (edit.NewString ?? "") + content.Substring(idx + searchOld.Length);
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

    // ============================================================
    // Read attached file contents for context
    // ============================================================
    private async Task<string> ReadAttachedFiles(List<string> files, string projectRoot)
    {
        var sb = new StringBuilder();
        foreach (var filePath in files)
        {
            if (string.IsNullOrWhiteSpace(filePath)) continue;
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, filePath));
            if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)) continue;
            if (System.IO.File.Exists(fullPath))
            {
                var content = await System.IO.File.ReadAllTextAsync(fullPath);
                sb.AppendLine($"\n### {filePath}\n```\n{content}\n```");
            }
        }
        return sb.ToString();
    }

    // ============================================================
    // SSE helper
    // ============================================================
    private static async Task SendSse(HttpResponse response, string eventName, object data)
    {
        var json = JsonSerializer.Serialize(data);
        await response.WriteAsync($"event: {eventName}\ndata: {json}\n\n");
        await response.Body.FlushAsync();
    }
}
