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

    public class EditAction
    {
        public string Path { get; set; } = "";
        public string Content { get; set; } = "";
    }

    public class CommandAction
    {
        public string Command { get; set; } = "";
    }

    private string ResolveWorkspaceRoot()
    {
        var configuredRoot = _config.GetValue<string>("Editor:WorkspaceRoot");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.GetFullPath(Path.Combine(_env.ContentRootPath, configuredRoot));
        }
        return Path.GetFullPath(Path.Combine(_env.ContentRootPath, ".."));
    }

    private string GetProjectRoot(string project)
    {
        var workspaceRoot = ResolveWorkspaceRoot();
        var projectSegment = string.IsNullOrWhiteSpace(project) ? "" : project.Trim().TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(workspaceRoot, projectSegment));
    }

    [HttpPost("execute")]
    public async Task<IActionResult> Execute([FromBody] AgentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt))
            return BadRequest("Prompt is required");

        var projectRoot = GetProjectRoot(req.Project);

        // Read attached file contents from disk
        var fileContents = new StringBuilder();
        foreach (var filePath in req.Files)
        {
            if (string.IsNullOrWhiteSpace(filePath)) continue;
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, filePath));
            if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                continue;

            if (System.IO.File.Exists(fullPath))
            {
                var content = await System.IO.File.ReadAllTextAsync(fullPath);
                fileContents.AppendLine($"\n### {filePath}\n```\n{content}\n```");
            }
        }

        var systemPrompt = @"You are an AI coding assistant that modifies project files to complete tasks.

You will receive a task description and file contents. You must decide what changes to make.

Respond ONLY with valid JSON. No markdown, no code fences, just raw JSON:
{
  ""thinking"": ""your step-by-step analysis"",
  ""edits"": [{ ""path"": ""relative/file/path"", ""content"": ""FULL new file content"" }],
  ""commands"": [{ ""command"": ""shell command to run"" }],
  ""summary"": ""brief explanation of changes""
}

Rules:
- Each edit has the full new content of the file, not just the changed parts
- Commands are optional (install deps, run tests, etc.)
- Paths are relative to the project root
- If a file doesn't exist yet and needs to be created, include it in edits
- Only include files that need changes";

        var userMessage = $"## Task\n{req.Prompt}\n\n## Files to modify ({req.Files.Count} file(s)):\n{fileContents}";

        // Call LLM
        var baseUrl = _config.GetValue<string>("LlamaUrl") ?? "http://192.168.2.58:8080";
        var target = baseUrl.TrimEnd('/') + "/v1/chat/completions";
        var client = _clientFactory.CreateClient("llama");
        string model = _config.GetValue<string>("Ai:Model") ?? "medgemma:4b";

        var messages = new[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userMessage }
        };

        var requestBody = new { model, messages, stream = false, temperature = 0.2 };
        var contentJson = JsonSerializer.Serialize(requestBody);
        var httpContent = new StringContent(contentJson, Encoding.UTF8, "application/json");

        var resp = await client.PostAsync(target, httpContent);
        var respText = await resp.Content.ReadAsStringAsync();

        // Extract LLM message content
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
            return Ok(new { error = "Empty AI response", raw = respText });

        // Extract JSON object from response (handles markdown-wrapped JSON)
        var jsonStr = llmContent;
        var start = jsonStr.IndexOf('{');
        var end = jsonStr.LastIndexOf('}');
        if (start >= 0 && end > start)
            jsonStr = jsonStr.Substring(start, end - start + 1);

        AgentResponse? agentResp = null;
        try
        {
            agentResp = JsonSerializer.Deserialize<AgentResponse>(jsonStr, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { }

        if (agentResp == null)
            return Ok(new { error = "Failed to parse AI JSON response", raw = llmContent });

        // Apply file edits
        var editResults = new List<object>();
        foreach (var edit in agentResp.Edits)
        {
            try
            {
                var targetPath = Path.GetFullPath(Path.Combine(projectRoot, edit.Path));
                if (!targetPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                {
                    editResults.Add(new { path = edit.Path, status = "skipped", error = "Path outside project root" });
                    continue;
                }
                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                await System.IO.File.WriteAllTextAsync(targetPath, edit.Content ?? "", Encoding.UTF8);
                editResults.Add(new { path = edit.Path, status = "written" });
            }
            catch (Exception ex)
            {
                editResults.Add(new { path = edit.Path, status = "error", error = ex.Message });
            }
        }

        // Execute commands via terminal
        var commandResults = new List<object>();
        _terminal.Start();
        foreach (var cmd in agentResp.Commands)
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

        return Ok(new
        {
            thinking = agentResp.Thinking,
            summary = agentResp.Summary,
            edits = editResults,
            commands = commandResults
        });
    }

    [HttpPost("execute-stream")]
    public async Task ExecuteStream([FromBody] AgentRequest req)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        if (string.IsNullOrWhiteSpace(req.Prompt))
        {
            await SendSseEvent(Response, "error", "{\"message\":\"Prompt is required\"}");
            return;
        }

        try
        {
            var projectRoot = GetProjectRoot(req.Project);

            // Read attached file contents
            var fileContents = new StringBuilder();
            foreach (var filePath in req.Files)
            {
                if (string.IsNullOrWhiteSpace(filePath)) continue;
                var fullPath = Path.GetFullPath(Path.Combine(projectRoot, filePath));
                if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (System.IO.File.Exists(fullPath))
                {
                    var content = await System.IO.File.ReadAllTextAsync(fullPath);
                    fileContents.AppendLine($"\n### {filePath}\n```\n{content}\n```");
                }
            }

            var systemPrompt = @"You are an AI coding assistant that modifies project files to complete tasks.

You will receive a task description and file contents. You must decide what changes to make.

Respond ONLY with valid JSON. No markdown, no code fences, just raw JSON:
{
  ""thinking"": ""your step-by-step analysis"",
  ""edits"": [{ ""path"": ""relative/file/path"", ""content"": ""FULL new file content"" }],
  ""commands"": [{ ""command"": ""shell command to run"" }],
  ""summary"": ""brief explanation of changes""
}

Rules:
- Each edit has the full new content of the file, not just the changed parts
- Commands are optional (install deps, run tests, etc.)
- Paths are relative to the project root
- If a file doesn't exist yet and needs to be created, include it in edits
- Only include files that need changes";

            var userMessage = $"## Task\n{req.Prompt}\n\n## Files to modify ({req.Files.Count} file(s)):\n{fileContents}";

            // Call LLM with streaming
            var baseUrl = _config.GetValue<string>("LlamaUrl") ?? "http://192.168.2.58:8080";
            var target = baseUrl.TrimEnd('/') + "/v1/chat/completions";
            var client = _clientFactory.CreateClient("llama");
            string model = _config.GetValue<string>("Ai:Model") ?? "medgemma:4b";

            var messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            };

            var requestBody = new { model, messages, stream = true, temperature = 0.2 };
            var contentJson = JsonSerializer.Serialize(requestBody);
            var httpContent = new StringContent(contentJson, Encoding.UTF8, "application/json");

            using var httpResp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, target) { Content = httpContent }, HttpCompletionOption.ResponseHeadersRead);
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

                    await SendSseEvent(Response, "token", JsonSerializer.Serialize(new { t = token }));
                }
                catch { }
            }

            // Parse the full LLM response
            var llmContent = fullContent.ToString();
            var jsonStr = llmContent;
            var startIdx = jsonStr.IndexOf('{');
            var endIdx = jsonStr.LastIndexOf('}');
            if (startIdx >= 0 && endIdx > startIdx)
                jsonStr = jsonStr.Substring(startIdx, endIdx - startIdx + 1);

            AgentResponse? agentResp = null;
            try
            {
                agentResp = JsonSerializer.Deserialize<AgentResponse>(jsonStr, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { }

            if (agentResp == null)
            {
                await SendSseEvent(Response, "error", JsonSerializer.Serialize(new { message = "Failed to parse AI response", raw = llmContent }));
                await SendSseEvent(Response, "done", "{}");
                return;
            }

            // Apply edits
            foreach (var edit in agentResp.Edits)
            {
                try
                {
                    var targetPath = Path.GetFullPath(Path.Combine(projectRoot, edit.Path));
                    if (!targetPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        await SendSseEvent(Response, "edit", JsonSerializer.Serialize(new { path = edit.Path, status = "skipped", error = "Path outside project root" }));
                        continue;
                    }
                    var dir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    await System.IO.File.WriteAllTextAsync(targetPath, edit.Content ?? "", Encoding.UTF8);
                    await SendSseEvent(Response, "edit", JsonSerializer.Serialize(new { path = edit.Path, status = "written" }));
                }
                catch (Exception ex)
                {
                    await SendSseEvent(Response, "edit", JsonSerializer.Serialize(new { path = edit.Path, status = "error", error = ex.Message }));
                }
            }

            // Execute commands
            _terminal.Start();
            foreach (var cmd in agentResp.Commands)
            {
                try
                {
                    await _terminal.SendCommandAsync(cmd.Command);
                    await Task.Delay(800);
                    var output = _terminal.ReadLastLines(50);
                    await SendSseEvent(Response, "command", JsonSerializer.Serialize(new { command = cmd.Command, status = "ok", output }));
                }
                catch (Exception ex)
                {
                    await SendSseEvent(Response, "command", JsonSerializer.Serialize(new { command = cmd.Command, status = "error", error = ex.Message }));
                }
            }

            await SendSseEvent(Response, "done", JsonSerializer.Serialize(new
            {
                thinking = agentResp.Thinking,
                summary = agentResp.Summary
            }));
        }
        catch (Exception ex)
        {
            await SendSseEvent(Response, "error", JsonSerializer.Serialize(new { message = ex.Message }));
            await SendSseEvent(Response, "done", "{}");
        }
    }

    private static async Task SendSseEvent(HttpResponse response, string eventName, string data)
    {
        await response.WriteAsync($"event: {eventName}\ndata: {data}\n\n");
        await response.Body.FlushAsync();
    }

    public class AgentResponse
    {
        public string Thinking { get; set; } = "";
        public string Summary { get; set; } = "";
        public List<EditAction> Edits { get; set; } = new();
        public List<CommandAction> Commands { get; set; } = new();
    }
}
