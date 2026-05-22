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
  ""edits"": [{ ""path"": ""relative/file/path"", ""oldString"": ""exact text in the file"", ""newString"": ""replacement text"" }],
  ""commands"": [{ ""command"": ""shell command to run"" }],
  ""summary"": ""brief explanation of changes""
}

Rules:
- Each edit is a find-and-replace: oldString must match the existing file content exactly (preserve indentation/whitespace)
- Multiple edits can target the same file; they are applied in order
- For new files, set oldString to """" and newString to the full file content
- oldString must uniquely match the text to replace (include enough surrounding context to be unambiguous)
- Prefer small, targeted edits over large blocks - this saves tokens
- Commands are optional (install deps, run tests, etc.)
- Paths are relative to the project root
- Only include files that need changes";

        var userMessage = $"## Task\n{req.Prompt}\n\n## Files to modify ({req.Files.Count} file(s)):\n{fileContents}";

        // Call LLM
           // Read LlamaUrl from config.json if available
            var configPath = Path.Combine(_env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot"), "config.json");
            var baseUrl = "http://192.168.2.58:8080"; // default fallback
            
            if (System.IO.File.Exists(configPath))
            {
                try
                {
                    var configText = System.IO.File.ReadAllText(configPath);
                    var configJson = JsonSerializer.Deserialize<JsonElement>(configText);
                    if (configJson.TryGetProperty("LlamaUrl", out var llamaUrlElement))
                    {
                        baseUrl = llamaUrlElement.GetString() ?? baseUrl;
                    }
                }
                catch
                {
                    // Use default if parsing fails
                }
            }
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

        // Apply file edits with find-replace and retry
        var editResultsList = await ApplyEditsWithRetry(agentResp.Edits, projectRoot, req);
        var editResults = new List<object>();
        foreach (var r in editResultsList)
            editResults.Add(new { path = r.Path, status = r.Status, error = r.Error });

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
  ""edits"": [{ ""path"": ""relative/file/path"", ""oldString"": ""exact text in the file"", ""newString"": ""replacement text"" }],
  ""commands"": [{ ""command"": ""shell command to run"" }],
  ""summary"": ""brief explanation of changes""
}

Rules:
- Each edit is a find-and-replace: oldString must match the existing file content exactly (preserve indentation/whitespace)
- Multiple edits can target the same file; they are applied in order
- For new files, set oldString to """" and newString to the full file content
- oldString must uniquely match the text to replace (include enough surrounding context to be unambiguous)
- Prefer small, targeted edits over large blocks - this saves tokens
- Commands are optional (install deps, run tests, etc.)
- Paths are relative to the project root
- Only include files that need changes";

            var userMessage = $"## Task\n{req.Prompt}\n\n## Files to modify ({req.Files.Count} file(s)):\n{fileContents}";

            // Call LLM with streaming
       // Read LlamaUrl from config.json if available
        var configPath = Path.Combine(_env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot"), "config.json");
        var baseUrl = "http://192.168.2.58:8080"; // default fallback
        
        if (System.IO.File.Exists(configPath))
        {
            try
            {
                var configText = System.IO.File.ReadAllText(configPath);
                var configJson = JsonSerializer.Deserialize<JsonElement>(configText);
                if (configJson.TryGetProperty("LlamaUrl", out var llamaUrlElement))
                {
                    baseUrl = llamaUrlElement.GetString() ?? baseUrl;
                }
            }
            catch
            {
                // Use default if parsing fails
            }
        }
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

            // Apply edits with find-replace and retry
            var editResults = await ApplyEditsWithRetry(agentResp.Edits, projectRoot, req);
            foreach (var r in editResults)
                await SendSseEvent(Response, "edit", JsonSerializer.Serialize(new { path = r.Path, status = r.Status, error = r.Error }));

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

    private async Task<List<EditResult>> ApplyEditsWithRetry(List<EditAction> edits, string projectRoot, AgentRequest req)
    {
        var results = new List<EditResult>();
        var remainingEdits = new List<EditAction>(edits);
        int maxRetries = 3;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (remainingEdits.Count == 0) break;

            var failedEdits = new List<(EditAction edit, string reason)>();

            // Group remaining edits by file path (preserving order within each file)
            var fileGroups = new Dictionary<string, List<EditAction>>(StringComparer.OrdinalIgnoreCase);
            var fileOrder = new List<string>();
            foreach (var edit in remainingEdits)
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

                try
                {
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
                            results.Add(new EditResult { Path = filePath, Status = "skipped", Error = "File does not exist and edit has non-empty oldString" });
                        continue;
                    }

                    bool fileHasErrors = false;
                    foreach (var edit in fileEdits)
                    {
                        // New file creation
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

                        var idx = content.IndexOf(edit.OldString, StringComparison.Ordinal);
                        if (idx == -1)
                        {
                            fileHasErrors = true;
                            break;
                        }

                        content = content.Substring(0, idx) + (edit.NewString ?? "") + content.Substring(idx + edit.OldString.Length);
                    }

                    if (!fileHasErrors)
                    {
                        var dir = Path.GetDirectoryName(targetPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);
                        await System.IO.File.WriteAllTextAsync(targetPath, content, Encoding.UTF8);
                        results.Add(new EditResult { Path = filePath, Status = "written" });
                    }
                    else
                    {
                        // If any edit in this file failed, retry ALL edits for this file together
                        foreach (var fe in fileEdits)
                            failedEdits.Add((fe, "edit failed for this file, retrying all"));
                    }
                }
                catch (Exception ex)
                {
                    foreach (var _ in fileEdits)
                        results.Add(new EditResult { Path = filePath, Status = "error", Error = ex.Message });
                    failedEdits.AddRange(fileEdits.Select(e => (e, ex.Message)));
                }
            }

            remainingEdits = failedEdits.Select(f => f.edit).ToList();

            if (remainingEdits.Count == 0 || attempt >= maxRetries)
                break;

            // Compact retry: ask LLM for corrected oldString/newString pairs
            var retryPrompt = new StringBuilder();
            retryPrompt.AppendLine("Some find-and-replace edits failed because the oldString was not found in the file. Current file contents:");
            foreach (var filePath in remainingEdits.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var fullPath = Path.GetFullPath(Path.Combine(projectRoot, filePath));
                if (fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(fullPath))
                {
                    var curContent = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8);
                    retryPrompt.AppendLine($"\n### {filePath}\n```\n{curContent}\n```");
                }
            }
            retryPrompt.AppendLine("\nFailed edits (oldString was NOT found):");
            foreach (var (edit, reason) in failedEdits)
            {
                retryPrompt.AppendLine($"- path: {edit.Path}");
                retryPrompt.AppendLine($"  oldString: '{edit.OldString}'");
                retryPrompt.AppendLine($"  newString: '{edit.NewString}'");
                retryPrompt.AppendLine($"  reason: {reason}");
            }
            retryPrompt.AppendLine("\nPlease provide corrected oldString/newString pairs. oldString must match the existing file content exactly.");
            retryPrompt.AppendLine("Respond ONLY with JSON:");
            retryPrompt.AppendLine("{ \"edits\": [{ \"path\": \"...\", \"oldString\": \"...\", \"newString\": \"...\" }] }");

            var baseUrl = GetLlamaBaseUrl();
            var target = baseUrl + "/v1/chat/completions";
            var client = _clientFactory.CreateClient("llama");
            string model = _config.GetValue<string>("Ai:Model") ?? "medgemma:4b";

            var retryMessages = new[]
            {
                new { role = "system", content = "You are a find-and-replace fixer. Correct the oldString values to match existing file content exactly." },
                new { role = "user", content = retryPrompt.ToString() }
            };

            var retryBody = new { model, messages = retryMessages, stream = false, temperature = 0.1 };
            var retryJson = JsonSerializer.Serialize(retryBody);
            var retryContent = new StringContent(retryJson, Encoding.UTF8, "application/json");

            try
            {
                var retryResp = await client.PostAsync(target, retryContent);
                var retryText = await retryResp.Content.ReadAsStringAsync();

                string retryLlmContent = "";
                using var doc = JsonDocument.Parse(retryText);
                if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var first = choices[0];
                    if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
                        retryLlmContent = c.GetString() ?? "";
                }

                if (!string.IsNullOrWhiteSpace(retryLlmContent))
                {
                    var jsonStr2 = retryLlmContent;
                    var s2 = jsonStr2.IndexOf('{');
                    var e2 = jsonStr2.LastIndexOf('}');
                    if (s2 >= 0 && e2 > s2)
                        jsonStr2 = jsonStr2.Substring(s2, e2 - s2 + 1);

                    var fixedEditWrapper = JsonSerializer.Deserialize<JsonElement>(jsonStr2);
                    if (fixedEditWrapper.TryGetProperty("edits", out var editsProp))
                    {
                        var fixedEdits = JsonSerializer.Deserialize<List<EditAction>>(editsProp.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (fixedEdits != null && fixedEdits.Count > 0)
                            remainingEdits = fixedEdits;
                    }
                }
            }
            catch { }
        }

        foreach (var (edit, reason) in remainingEdits.Select(e => (e, "oldString not found after retry")))
        {
            if (!results.Any(r => r.Path == edit.Path && r.Status == "error" && r.Error == reason))
                results.Add(new EditResult { Path = edit.Path, Status = "error", Error = reason });
        }

        return results;
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
                    results.Add(new EditResult { Path = filePath, Status = "skipped", Error = "File does not exist and edit has non-empty oldString" });
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

                var idx = content.IndexOf(edit.OldString, StringComparison.Ordinal);
                if (idx == -1)
                {
                    results.Add(new EditResult { Path = filePath, Status = "error", Error = $"oldString not found in {filePath}" });
                    hasError = true;
                    break;
                }

                content = content.Substring(0, idx) + (edit.NewString ?? "") + content.Substring(idx + edit.OldString.Length);
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

    public class AgentResponse
    {
        public string Thinking { get; set; } = "";
        public string Summary { get; set; } = "";
        public List<EditAction> Edits { get; set; } = new();
        public List<CommandAction> Commands { get; set; } = new();
    }
}
