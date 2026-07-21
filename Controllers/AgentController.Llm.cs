using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Weaver.Services;
namespace Weaver.Controllers;
partial class AgentController
{
    private async Task<string> GetLlamaBaseUrl()
    {
        var cfg = await _configFile.LoadConfigAsync();
        return (cfg.llamaUrl ?? "http://localhost:8080").TrimEnd('/');
    }
    private async Task<string> GetLlamaModel()
    {
        var cfg = await _configFile.LoadConfigAsync();
        return cfg.llamaModel ?? "medgemma:4b";
    }
    private async Task<(string raw, AgentResponse? response, string? error)> CallLlmRaw(
        string systemPrompt, string userMessage, CancellationToken ct = default,
        TimeSpan? requestTimeout = null, int? maxTokens = null)
    {
        var baseUrl = await GetLlamaBaseUrl();
        var model = await GetLlamaModel();
        var client = _clientFactory.CreateClient("llama");
        client.Timeout = _infiniteTimeout;
        var messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user",   content = userMessage  }
        };
        var timeout = requestTimeout ?? TimeSpan.FromMinutes(30);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        return await CallLlmNonStreaming(client, baseUrl + "/v1/chat/completions", model, messages, linkedCts.Token, maxTokens);
    }
    private async Task<(string raw, AgentResponse? response, string? error)> CallLlmRawStreaming(
        string systemPrompt, string userMessage, bool emitSse, CancellationToken ct = default,
        TimeSpan? requestTimeout = null, int? maxTokens = null)
    {
        var baseUrl = await GetLlamaBaseUrl();
        var model = await GetLlamaModel();
        var client = _clientFactory.CreateClient("llama");
        client.Timeout = _infiniteTimeout;
        var messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user",   content = userMessage  }
        };
        var timeout = requestTimeout ?? TimeSpan.FromMinutes(30);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        return await CallLlmStreaming(client, baseUrl + "/v1/chat/completions", model, messages, linkedCts.Token, maxTokens, emitSse);
    }
    private async Task<(string raw, AgentResponse? parsed, string? error)> CallLlmNonStreaming(
      HttpClient client, string target, string model, object messages,
      CancellationToken ct = default, int? maxTokens = null)
    {
        var cfg6 = await LoadConfigAsync();
        var mt = maxTokens ?? cfg6.defaultMaxTokens;
        var reqBody = new
        {
            model,
            messages,
            stream = false,
            temperature = 0.05,
            max_tokens = mt,
            repeat_penalty = 1.3,
            repeat_last_n = 256
        };
        var httpContent = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");
        try
        {
            var resp = await client.PostAsync(target, httpContent, ct);
            var respText = await resp.Content.ReadAsStringAsync(ct);
            var llmContent = ExtractLlmContent(respText);
            if (string.IsNullOrWhiteSpace(llmContent)) return (respText, null, "Empty LLM response");
            var parsed = ParseAgentResponse(llmContent);
            return (llmContent, parsed, parsed == null ? "JSON parse failed" : null);
        }
        catch (TaskCanceledException) { return ("", null, "LLM request timed out"); }
        catch (Exception ex) { return ("", null, ex.Message); }
    }
    private async Task<(string raw, AgentResponse? parsed, string? error)> CallLlmStreaming(
    HttpClient client, string target, string model, object messages,
    CancellationToken ct = default, int? maxTokens = null, bool emitSse = false)
    {
        var cfg7 = await LoadConfigAsync();
        var mt = maxTokens ?? cfg7.defaultMaxTokens;
        var reqBody = new
        {
            model,
            messages,
            stream = true,
            temperature = 0.05,
            max_tokens = mt,
            repeat_penalty = 1.3,
            repeat_last_n = 256
        };
        var httpContent = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = httpContent };
            var resp = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            { var t2 = await resp.Content.ReadAsStringAsync(ct); return (t2, null, $"HTTP {resp.StatusCode}"); }
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var reader = new StreamReader(stream);
            var sb = new StringBuilder();
            const int WindowChars = 400;
            const int ChunkLen = 40;
            const int RepeatThreshold = 4;
            using var repeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            while (true)
            {
                repeatCts.Token.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync().WaitAsync(repeatCts.Token);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Contains("[DONE]")) break;
                if (!line.StartsWith("data: ")) continue;
                var data = line[6..].Trim();
                if (string.IsNullOrWhiteSpace(data)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                    {
                        var choice = choices[0];
                        if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content))
                        {
                            var token = content.GetString();
                            if (!string.IsNullOrWhiteSpace(token))
                            {
                                if (emitSse) await SendSse(Response, "token", new { token }, ct);
                                sb.Append(token);
                                if (sb.Length >= ChunkLen * (RepeatThreshold + 1) &&
                                    IsRepeatingLoop(sb, WindowChars, ChunkLen, RepeatThreshold))
                                {
                                    try { resp.Dispose(); } catch { }
                                    var truncated = sb.ToString();
                                    return (truncated, null,
                                        $"Repetition loop detected after {truncated.Length} chars — aborted early. " +
                                        "The model got stuck re-emitting the same block. Retry with a smaller, more targeted anchor.");
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            var raw = sb.ToString();
            if (string.IsNullOrWhiteSpace(raw)) return ("", null, "Empty LLM response");
            var braceCount = 0;
            var topLevelOpens = 0;
            foreach (var c in raw)
            {
                if (c == '{') { braceCount++; if (braceCount == 1) topLevelOpens++; }
                else if (c == '}') braceCount--;
            }
            if (topLevelOpens > 1)
            {
                return (raw, null,
                    $"Multiple JSON objects detected ({topLevelOpens}) in single response — " +
                    "model is emitting multiple attempts. Use a stronger model or lower temperature.");
            }
            var parsed2 = ParseAgentResponse(raw);
            return (raw, parsed2, parsed2 == null ? "JSON parse failed" : null);
        }
        catch (TaskCanceledException) { return ("", null, "LLM request timed out"); }
        catch (Exception ex) { return ("", null, ex.Message); }
    }
    private static string? ExtractNewlyAddedMethodName(string? stepChange, string? newStr)
    {
        if (string.IsNullOrWhiteSpace(stepChange)) return null;
        var isAddition = Regex.IsMatch(stepChange,
            @"\b(add|create|insert|implement|define)\b.{0,40}\b(method|function)\b", RegexOptions.IgnoreCase);
        if (!isAddition) return null;
        if (!string.IsNullOrWhiteSpace(newStr))
        {
            var m = AgentUtilities.MethodDeclRegex.Match(newStr);
            if (m.Success) return m.Groups[1].Value;
            var tsMatch = Regex.Match(newStr,
                @"\b(?:private|public|protected)?\s*(?:async\s+)?([A-Za-z_]\w*)\s*\([^)]*\)\s*(?::\s*[^{]+)?\s*\{");
            if (tsMatch.Success) return tsMatch.Groups[1].Value;
        }
        var dm = Regex.Match(stepChange, @"(?:method|function)\s+'?([A-Za-z_]\w*)'?", RegexOptions.IgnoreCase);
        if (dm.Success) return dm.Groups[1].Value;
        dm = Regex.Match(stepChange, @"\b([A-Za-z_]\w*)\s*\(\s*\)", RegexOptions.IgnoreCase);
        return dm.Success ? dm.Groups[1].Value : null;
    }
    /// <summary>
    /// Deterministic (non-LLM) check: does a call to this newly-added method exist ANYWHERE
    /// in the project outside its own declaration? No LLM opinion needed — grep either finds
    /// a call site or it doesn't. This is what catches "added but never wired up," which an
    /// LLM step-verifier reliably misses because it's judging the step in isolation.
    /// </summary>
    private async Task<(bool wired, string? reason)> CheckNewMethodIsWiredUpAsync(
        string methodName, string relPath, string projectRoot, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(methodName) || methodName.Length < 3) return (true, null);
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        var searchPatterns = ext switch
        {
            ".ts" or ".tsx" => new[] { "*.ts", "*.tsx", "*.html" },
            ".cs" => new[] { "*.cs" },
            ".js" or ".jsx" => new[] { "*.js", "*.jsx", "*.html" },
            _ => new[] { "*" + ext }
        };
        var callPattern = new Regex($@"\b{Regex.Escape(methodName)}\s*\(", RegexOptions.Compiled);
        var declLinePattern = new Regex(
            $@"\b(?:private|public|protected|internal|static|async|get|set)\b[^\n]*\b{Regex.Escape(methodName)}\s*\(",
            RegexOptions.Compiled);
        foreach (var pattern in searchPatterns)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(projectRoot, pattern, SearchOption.AllDirectories)
                    .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\")
                             && !f.Contains("\\node_modules\\") && !f.Contains("\\.git\\") && !f.Contains("\\dist\\"));
            }
            catch { continue; }
            foreach (var file in files)
            {
                string content;
                try { content = await System.IO.File.ReadAllTextAsync(file, Encoding.UTF8, ct); }
                catch { continue; }
                foreach (Match m in callPattern.Matches(content))
                {
                    var lineStart = content.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
                    var lineEndIdx = content.IndexOf('\n', m.Index);
                    var line = content[lineStart..(lineEndIdx < 0 ? content.Length : lineEndIdx)];
                    if (declLinePattern.IsMatch(line)) continue; // this hit is the declaration itself
                    return (true, null); // found a real call site
                }
            }
        }
        return (false,
            $"Method '{methodName}' was just added to {relPath} but has ZERO call sites anywhere else in the " +
            "project — only its own declaration exists. It needs to be wired up (called from wherever the " +
            "feature it implements is supposed to run).");
    }
    private async Task<string> RunCausalReasoningAsync(string taskDesc, string relPath, string fileContent, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileContent)) return string.Empty;
        var classifyPrompt = $"Is the following task a bug fix or a feature/enhancement? Reply with exactly one word: BUG or FEATURE.\n\nTask: {taskDesc}";
        var (classification, _, _) = await CallLlmRawStreaming(
            "You classify tasks as BUG or FEATURE. Reply with exactly one word.",
            classifyPrompt, false, ct, requestTimeout: _infiniteTimeout, maxTokens: 10);
        await EmitLog(emitSse, "info", $"Causal reasoning classification: '{classification?.Trim()}' for task: {taskDesc}", ct: ct);
        if (string.IsNullOrWhiteSpace(classification) || !classification.Trim().Equals("BUG", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        var sysPrompt = "You are an expert software debugger. " +
                        "Given a bug report and the full file content, trace the execution flow to identify the ROOT CAUSE. " +
                        "Small details matter: check if callbacks are missed, state isn't updated, or variables are out of sync. " +
                        "Do NOT write the fix. Output ONLY JSON: " +
                        "{\"rootCause\": \"detailed explanation\", \"affectedMethods\": [\"method1\", \"method2\"]}";
        var userPrompt = $"### BUG REPORT / TASK ###\n{taskDesc}\n\n" +
                         $"### FILE: {relPath} ###\n" +
                         $"```\n{fileContent}\n```\n\n" +
                         "Trace the logic. What is the exact root cause of the issue, and which methods are affected?";
        try
        {
            var (raw, _, err) = await CallLlmRawStreaming(sysPrompt, userPrompt, emitSse, ct, requestTimeout: TimeSpan.FromSeconds(45), maxTokens: 500);
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var cleaned = AgentUtilities.ExtractFirstJsonObject(raw);
            using var doc = JsonDocument.Parse(cleaned);
            var rootCause = doc.RootElement.TryGetProperty("rootCause", out var rc) ? rc.GetString() : "Failed to parse root cause.";
            var affected = new List<string>();
            if (doc.RootElement.TryGetProperty("affectedMethods", out var am) && am.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in am.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) affected.Add(item.GetString() ?? "");
            }
            var sb = new StringBuilder();
            sb.AppendLine("### ⚠️ CAUSAL REASONING ANALYSIS (CRITICAL — READ BEFORE EDITING) ###");
            sb.AppendLine($"Root Cause Identified: {rootCause}");
            sb.AppendLine($"Affected Methods: {string.Join(", ", affected)}");
            sb.AppendLine("Your edit MUST address the root cause above and ensure the affected methods do not break.");
            sb.AppendLine();
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }
    private static bool IsRepeatingLoop(StringBuilder sb, int windowChars, int chunkLen, int repeatThreshold)
    {
        var len = sb.Length;
        var start = Math.Max(0, len - windowChars);
        var window = sb.ToString(start, len - start);
        if (window.Length < chunkLen * repeatThreshold) return false;
        var tail = window[^chunkLen..];
        if (string.IsNullOrWhiteSpace(tail.Trim())) return false;
        var pos = window.Length - chunkLen;
        var consecutive = 1;
        pos -= chunkLen;
        while (pos >= 0)
        {
            var candidate = window.Substring(pos, chunkLen);
            if (candidate == tail)
            {
                consecutive++;
                if (consecutive >= repeatThreshold) return true;
                pos -= chunkLen;
            }
            else break;
        }
        return false;
    }
    private async Task<(string raw, string? error)> CallLlmRawText(
        string systemPrompt, string userMessage, bool emitSse, CancellationToken ct = default,
        TimeSpan? requestTimeout = null, int? maxTokens = null)
    {
        var baseUrl = await GetLlamaBaseUrl();
        var model = await GetLlamaModel();
        var client = _clientFactory.CreateClient("llama");
        client.Timeout = _infiniteTimeout;
        var messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user",   content = userMessage  }
        };
        var timeout = requestTimeout ?? TimeSpan.FromMinutes(30);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var cfg = await LoadConfigAsync();
        var mt = maxTokens ?? cfg.defaultMaxTokens;
        var reqBody = new
        {
            model,
            messages,
            stream = true,
            temperature = 0.0,
            max_tokens = mt,
            repeat_penalty = 1.3,
            repeat_last_n = 256
        };
        var httpContent = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/chat/completions") { Content = httpContent };
            var resp = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
            if (!resp.IsSuccessStatusCode)
            { var t2 = await resp.Content.ReadAsStringAsync(linkedCts.Token); return (t2, $"HTTP {resp.StatusCode}"); }
            var stream = await resp.Content.ReadAsStreamAsync(linkedCts.Token);
            var reader = new StreamReader(stream);
            var sb = new StringBuilder();
            while (true)
            {
                linkedCts.Token.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync().WaitAsync(linkedCts.Token);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Contains("[DONE]")) break;
                if (!line.StartsWith("data: ")) continue;
                var data = line[6..].Trim();
                if (string.IsNullOrWhiteSpace(data)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                    {
                        var choice = choices[0];
                        if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content))
                        {
                            var token = content.GetString();
                            if (!string.IsNullOrWhiteSpace(token))
                            {
                                if (emitSse) await SendSse(Response, "token", new { token }, ct);
                                sb.Append(token);
                            }
                        }
                    }
                }
                catch { }
            }
            var raw = sb.ToString();
            if (string.IsNullOrWhiteSpace(raw)) return ("", "Empty LLM response");
            return (raw, null);
        }
        catch (TaskCanceledException) { return ("", "LLM request timed out"); }
        catch (Exception ex) { return ("", ex.Message); }
    }
    private static string ExtractLlmContent(string respText)
    {
        try
        {
            using var doc = JsonDocument.Parse(respText);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content))
                    return content.GetString() ?? "";
            }
        }
        catch { }
        return "";
    }
    private static AgentResponse? ParseAgentResponse(string raw)
    {
        var jsonStr = raw.Trim();
        if (jsonStr.StartsWith("```")) { var m = Regex.Match(jsonStr, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) jsonStr = m.Groups[1].Value.Trim(); }
        var start = jsonStr.IndexOf('{'); var end = jsonStr.LastIndexOf('}');
        if (start >= 0 && end > start) jsonStr = jsonStr[start..(end + 1)];
        try
        {
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var steps = JsonSerializer.Deserialize<List<AgentStep>>(jsonStr, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (steps?.Count > 0) return new AgentResponse { Steps = steps, Summary = "Parsed array" };
            }
            if (root.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
            {
                var steps = JsonSerializer.Deserialize<List<AgentStep>>(stepsEl.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (steps?.Count > 0)
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
}