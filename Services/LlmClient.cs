using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Weaver.Services;

using static Weaver.Services.AgentDiffUtilities;
using static Weaver.Services.AgentJsonUtilities;
using static Weaver.Services.AgentMethodInventory;

/// <summary>Transport client for LLM requests and streaming response parsing.</summary>
public sealed class LlmClient : ILlmClient
{
    private const int StreamWindowChars = 400;
    private const int StreamChunkLen = 40;
    private const int StreamRepeatThreshold = 4;
    private const double WallOfTextMinNewlineRatio = 0.001;

    private readonly IHttpClientFactory _clientFactory;
    private readonly Func<Task<FrontendConfig>> _loadConfig;
    private readonly Func<TimeSpan> _getTimeout;
    private readonly Func<string, CancellationToken, Task> _emitToken;
    private readonly Func<double, int, CancellationToken, Task> _emitProgress;

    public LlmClient(IHttpClientFactory clientFactory, Func<Task<FrontendConfig>> loadConfig,
        Func<TimeSpan> getTimeout, Func<string, CancellationToken, Task> emitToken,
        Func<double, int, CancellationToken, Task> emitProgress)
    {
        _clientFactory = clientFactory;
        _loadConfig = loadConfig;
        _getTimeout = getTimeout;
        _emitToken = emitToken;
        _emitProgress = emitProgress;
    }

    public async Task<string> GetBaseUrlAsync(string? overrideBaseUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideBaseUrl)) return overrideBaseUrl;
        var cfg = await _loadConfig();
        return (cfg.llamaUrl ?? "http://localhost:8080").TrimEnd('/');
    }

    public async Task<string> GetModelAsync(string? overrideModel = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideModel)) return overrideModel;
        var cfg = await _loadConfig();
        return cfg.llamaModel ?? "medgemma:4b";
    }

    public async Task<(string baseUrl, string model, string name)> ResolveEndpointAsync(string? endpointId)
    {
        var cfg = await _loadConfig();
        var defaultBaseUrl = (cfg.llamaUrl ?? "http://localhost:8080").TrimEnd('/');
        var defaultModel = cfg.llamaModel ?? "medgemma:4b";
        if (!string.IsNullOrWhiteSpace(endpointId) && cfg.llamaEndpoints != null)
        {
            var ep = cfg.llamaEndpoints.FirstOrDefault(e => e.id == endpointId);
            if (ep != null)
            {
                var baseUrl = (string.IsNullOrWhiteSpace(ep.url) ? cfg.llamaUrl : ep.url)?.TrimEnd('/') ?? "";
                var model = (string.IsNullOrWhiteSpace(ep.model) ? cfg.llamaModel : ep.model) ?? "";
                return (baseUrl, model, string.IsNullOrWhiteSpace(ep.name) ? (ep.url ?? "Default") : ep.name);
            }
        }
        return (defaultBaseUrl, defaultModel, "Default");
    }

    public async Task PollProgressAsync(string baseUrl, CancellationToken ct)
    {
        var client = _clientFactory.CreateClient("llama");
        client.Timeout = TimeSpan.FromSeconds(3);
        double lastSent = -1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var resp = await client.GetAsync(baseUrl + "/slots", ct);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Backend has no /slots endpoint (e.g. Ollama) — stop polling.
                    break;
                }
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        double best = 0;
                        var processing = false;
                        foreach (var slot in doc.RootElement.EnumerateArray())
                        {
                            var st = slot.TryGetProperty("state", out var stEl) ? stEl.GetString() : "idle";
                            if (st == "processing" && slot.TryGetProperty("progress", out var prEl))
                            {
                                processing = true;
                                best = Math.Max(best, prEl.GetDouble());
                            }
                        }
                        var pct = (int)Math.Round(best * 100);
                        if (processing && pct != (int)lastSent)
                        {
                            lastSent = pct;
                            await _emitProgress(best, pct, ct);
                        }
                    }
                }
            }
            catch { /* transient error (timeout, server busy) — skip this tick */ }
            try { await Task.Delay(500, ct); } catch { break; }
        }
    }

    public async Task<(string raw, AgentResponse? parsed, string? error)> CallNonStreamingAsync(
      HttpClient client, string target, string model, object messages,
      CancellationToken ct = default, int? maxTokens = null)
    {
        var cfg6 = await _loadConfig();
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
            var hallError = DetectHallucination(llmContent);
            if (hallError != null) return (llmContent, null, hallError);
            var parsed = ParseAgentResponse(llmContent);
            return (llmContent, parsed, parsed == null ? "JSON parse failed" : null);
        }
        catch (TaskCanceledException) { return ("", null, "LLM request timed out"); }
        catch (Exception ex) { return ("", null, ex.Message); }
    }

    public async Task<(string raw, AgentResponse? parsed, string? error)> CallStreamingAsync(
    HttpClient client, string target, string model, object messages,
    CancellationToken ct = default, int? maxTokens = null, bool emitSse = false)
    {
        var cfg7 = await _loadConfig();
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
        // Derive the llama base URL from the target endpoint and start the
        // /slots progress poller so the frontend sees real loading progress.
        var llmBaseUrl = target.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? target.Substring(0, target.Length - "/v1/chat/completions".Length)
            : target;
        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (emitSse) _ = PollProgressAsync(llmBaseUrl, progressCts.Token);
        var sb = new StringBuilder();
        var truncatedByTokenLimit = false;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = httpContent };
            var resp = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            { var t2 = await resp.Content.ReadAsStringAsync(ct); return (t2, null, $"HTTP {resp.StatusCode}"); }
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var reader = new StreamReader(stream);
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
                        if (choice.TryGetProperty("finish_reason", out var finishReason) &&
                            finishReason.ValueKind == JsonValueKind.String &&
                            string.Equals(finishReason.GetString(), "length", StringComparison.OrdinalIgnoreCase))
                            truncatedByTokenLimit = true;
                        if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content))
                        {
                            var token = content.GetString();
                            if (!string.IsNullOrWhiteSpace(token))
                            {
                                if (emitSse) await _emitToken(token, ct);
                                sb.Append(token);
                                // Check for repetition loop (chunk-level)
                                if (sb.Length >= StreamChunkLen * (StreamRepeatThreshold + 1) &&
                                    IsRepeatingLoop(sb, StreamWindowChars, StreamChunkLen, StreamRepeatThreshold))
                                {
                                    try { resp.Dispose(); } catch { }
                                    var truncated = sb.ToString();
                                    return (truncated, null,
                                        $"Repetition loop detected after {truncated.Length} chars — aborted early. " +
                                        "The model got stuck re-emitting the same block. Retry with a smaller, more targeted anchor.");
                                }
                                // Check for wall-of-text hallucination (every ~500 chars beyond threshold)
                                if (sb.Length % 500 < 10) // check roughly every 500 chars
                                {
                                    var wallError = CheckStreamingHallucination(sb);
                                    if (wallError != null)
                                    {
                                        try { resp.Dispose(); } catch { }
                                        return (sb.ToString(), null, wallError);
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            var raw = sb.ToString();
            if (string.IsNullOrWhiteSpace(raw)) return ("", null, "Empty LLM response");
            // Post-hoc hallucination check (safety net)
            var hallError2 = DetectHallucination(raw);
            if (hallError2 != null) return (raw, null, hallError2);
            var braceCount = 0;
            var topLevelOpens = 0;
            foreach (var c in raw)
            {
                if (c == '{') { braceCount++; if (braceCount == 1) topLevelOpens++; }
                else if (c == '}') braceCount--;
            }
            if (topLevelOpens > 1)
            {
                // Model emitted multiple JSON objects — extract only the first
                raw = AgentJsonUtilities.ExtractFirstJsonObject(raw);
            }
            var parsed2 = ParseAgentResponse(raw);
            // Only treat a max-token cut as unrecoverable when the JSON failed to parse — a
            // truncated-but-parseable response is returned as-is to avoid spurious retries.
            if (parsed2 == null && truncatedByTokenLimit)
                return (raw, null, "Response truncated at max_tokens — partial kept for recovery hint.");
            return (raw, parsed2, parsed2 == null ? "JSON parse failed" : null);
        }
        catch (TaskCanceledException) { return (sb.ToString(), null, "LLM request timed out"); }
        catch (Exception ex) { return (sb.ToString(), null, ex.Message); }
        finally { try { progressCts.Cancel(); } catch { } }
    }

    public async Task<(string raw, string? error)> CallRawTextOnceAsync(
        string systemPrompt, string userMessage, bool emitSse, CancellationToken ct, int? maxTokens = null,
        bool appendTruncationMarker = false)
    {
        var baseUrl = await GetBaseUrlAsync();
        var model = await GetModelAsync();
        var client = _clientFactory.CreateClient("llama");
        client.Timeout = _getTimeout();
        var messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user",   content = userMessage  }
        };
        var cfg = await _loadConfig();
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
        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (emitSse) _ = PollProgressAsync(baseUrl, progressCts.Token);
        var sb = new StringBuilder();
        var truncatedByTokenLimit = false;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/chat/completions") { Content = httpContent };
            var resp = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            { var t2 = await resp.Content.ReadAsStringAsync(ct); return (t2, $"HTTP {resp.StatusCode}"); }
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var reader = new StreamReader(stream);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync().WaitAsync(ct);
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
                        // Mirror CallLlmStreaming: a finish_reason of "length" means max_tokens
                        // cut the response — without this, a budget-capped cut is silent.
                        if (choice.TryGetProperty("finish_reason", out var finishReason) &&
                            finishReason.ValueKind == JsonValueKind.String &&
                            string.Equals(finishReason.GetString(), "length", StringComparison.OrdinalIgnoreCase))
                            truncatedByTokenLimit = true;
                        if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content))
                        {
                            var token = content.GetString();
                            if (!string.IsNullOrWhiteSpace(token))
                            {
                                if (emitSse) await _emitToken(token, ct);
                                sb.Append(token);
                                // Check for repetition loop
                                if (sb.Length >= StreamChunkLen * (StreamRepeatThreshold + 1) &&
                                    IsRepeatingLoop(sb, StreamWindowChars, StreamChunkLen, StreamRepeatThreshold))
                                {
                                    try { resp.Dispose(); } catch { }
                                    return (sb.ToString(), "Repetition loop detected — aborted early. The model got stuck re-emitting the same block.");
                                }
                                // Check for wall-of-text hallucination
                                if (sb.Length % 500 < 10)
                                {
                                    var wallError = CheckStreamingHallucination(sb);
                                    if (wallError != null)
                                    {
                                        try { resp.Dispose(); } catch { }
                                        return (sb.ToString(), wallError);
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            var raw = sb.ToString();
            if (string.IsNullOrWhiteSpace(raw)) return ("", "Empty LLM response");
            var hallError = DetectHallucination(raw);
            if (hallError != null) return (raw, hallError);
            // NOTE: max_tokens truncation on the PROSE path is intentionally NOT an error —
            // pre-plan thinking and compaction are budget-capped and their partial output is
            // still usable reasoning (the partial is returned as-is, no retry). Only the JSON
            // path treats a truncated response as an error, because an unparseable step/edit
            // is genuinely unrecoverable. Callers that WANT the cut visible (pre-plan reasoning
            // streamed to the panel) opt in and get an explicit marker instead of a silent
            // mid-sentence stop that looks like a transport bug.
            if (truncatedByTokenLimit && appendTruncationMarker)
                return (raw + "\n\n…[reasoning truncated — hit the per-response token budget]…", null);
            return (raw, null);
        }
        catch (TaskCanceledException) { return (sb.ToString(), "LLM request timed out"); }
        catch (Exception ex) { return (sb.ToString(), ex.Message); }
        finally { try { progressCts.Cancel(); } catch { } }
    }

    public static string? DetectHallucination(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length < 1000) return null;

        // 1. Wall-of-text check: very low newline density. Deliberately EXTREME
        //    (fewer than 1 newline per 1000 chars) — dense single-paragraph prose from
        //    smaller models legitimately lands at ~1 newline per 200-500 chars, and
        //    treating that band as hallucination kept aborting useful pre-plan reasoning
        //    (e.g. "2249 chars with 8 line breaks" on a web-search task). Real stuck-ramble
        //    output runs thousands of chars with near-zero breaks.
        var newlineCount = raw.Count(c => c == '\n');
        var newlineRatio = (double)newlineCount / raw.Length;
        if (raw.Length > 2000 && newlineRatio < WallOfTextMinNewlineRatio)
        {
            return $"Hallucination (wall of text): {raw.Length} chars with {newlineCount} line breaks " +
                   $"(ratio {newlineRatio:F4}). The model is rambling without structure. Output truncated.";
        }

        // 2. Semantic repetition: same 120+ char substring appearing 3+ times (not just chunk repetition)
        if (raw.Length > 800)
        {
            const int subLen = 120;
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i <= raw.Length - subLen; i += 40) // step by 40 for performance
            {
                var sub = raw.Substring(i, subLen);
                var trimmed = sub.Trim();
                if (trimmed.Length < 60) continue; // skip near-empty/short substrings
                seen.TryGetValue(trimmed, out var count);
                count++;
                if (count >= 3)
                    return $"Hallucination (semantic repetition): same {trimmed.Length}-char block repeated {count}+ times. " +
                           "The model is stuck in a repetition loop.";
                seen[trimmed] = count;
            }
        }

        return null;
    }

    /// <summary>
    /// Real-time newline-density check for streaming output.
    /// Returns an error if the model has produced a large output with almost no line breaks.
    /// </summary>
    public static string? CheckStreamingHallucination(StringBuilder sb)
    {
        var len = sb.Length;
        if (len < 2500) return null;

        // Check full accumulated text for newline density
        var newlineCount = 0;
        for (var i = 0; i < len; i++)
            if (sb[i] == '\n') newlineCount++;

        var ratio = (double)newlineCount / len;
        if (ratio < WallOfTextMinNewlineRatio) // extreme, not just dense prose
        {
            return $"Hallucination (wall of text): {len} chars with {newlineCount} line breaks (ratio {ratio:F4}) — aborted early.";
        }

        return null;
    }

    public static bool IsRepeatingLoop(StringBuilder sb, int windowChars, int chunkLen, int repeatThreshold)
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
    public static string ExtractLlmContent(string respText)
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
    public static AgentResponse? ParseAgentResponse(string raw)
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

    public static string? RecoveryDetail(string? partial)
        => string.IsNullOrWhiteSpace(partial)
            ? null
            : (partial.Length > 600 ? partial[..600] + "\n…(partial preview)…" : partial);

    /// <summary>
    /// Builds the user message for the one-shot retry: the original prompt plus the partial
    /// response the model already produced, framed as a continuation task so the retry keeps
    /// the good work (e.g. the correct refactor that was streamed before the connection died)
    /// instead of regenerating from scratch. Capped to keep the retry context sane.
    /// </summary>
    public static string AppendPartialContinuationHint(string userMessage, string partial)
    {
        // Keep the HEAD (structure/context) and the TAIL (the exact continuation point) of a
        // long partial; dropping the middle is safe, dropping the end would orphan the retry.
        var cap = partial;
        if (partial.Length > 16000)
            cap = partial[..12000] + "\n…(partial truncated, middle omitted)…\n" + partial[^4000..];
        return userMessage + "\n\n" +
               "### YOUR PREVIOUS RESPONSE WAS INTERRUPTED BY A STREAM ERROR ###\n" +
               "Your previous attempt produced the partial response below but the connection dropped before it " +
               "finished. The partial work is good — CONTINUE from exactly where it left off and output the " +
               "COMPLETE response to the original request. Preserve everything already written below; finish any " +
               "truncated JSON (close braces/brackets, complete field values) and return the FULL valid response.\n" +
               "\nPARTIAL RESPONSE (already produced — continue from this point):\n```\n" +
               cap + "\n```\n";
    }

    /// <summary>
    /// True when a streaming failure was caused by hitting the max_tokens output cap — the
    /// requested edit is bigger than one response can hold, NOT a network blip. This is the
    /// case that needs the "finish this" continuation loop (re-asking for the full response
    /// would just truncate again at the same point).
    /// </summary>
    public static bool IsMaxTokenTruncation(string? error)
        => !string.IsNullOrWhiteSpace(error) &&
           error.Contains("truncated at max_tokens", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the accumulated response text is now a structurally complete JSON object.</summary>
    public static bool LooksLikeCompleteJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.StartsWith("```"))
        {
            var m = Regex.Match(t, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (m.Success) t = m.Groups[1].Value.Trim();
        }
        var start = t.IndexOf('{');
        var end = t.LastIndexOf('}');
        if (start < 0 || end <= start) return false;
        try
        {
            using var doc = JsonDocument.Parse(t[start..(end + 1)]);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch { return false; }
    }

    /// <summary>
    /// Builds the prompt for a "finish this" continuation pass. Unlike the transport-retry
    /// hint (which asks for the FULL response), this asks the model to output ONLY the missing
    /// tail that continues the partial EXACTLY where it stopped, so the appended result grows
    /// the response instead of regenerating it (which would truncate again at the same size).
    /// The ORIGINAL user message is kept in full at the front so the model still has the task
    /// context (file, change required) it needs to finish the edit correctly. The last ~160
    /// chars of the partial are given as an explicit continuation anchor so the model can see
    /// the exact character it must follow.
    /// </summary>
    public static string BuildFinishThisPrompt(string userMessage, string partial)
    {
        // The tail is what matters (the exact continuation point); the head just gives context.
        var cap = partial;
        if (partial.Length > 12000)
            cap = partial[..8000] + "\n…(partial truncated, middle omitted)…\n" + partial[^4000..];
        var anchor = partial.Length > 160 ? partial[^160..] : partial;
        return userMessage + "\n\n" +
               "### FINISH THIS OUTPUT ###\n" +
               "Your previous response was cut off because it exceeded the token limit. The partial below is " +
               "GOOD WORK and must be preserved — do NOT restart, do NOT repeat it, do NOT summarize it.\n" +
               "Output ONLY the REMAINING characters that continue the partial from the exact point it stopped, " +
               "so that when the original partial is followed by your output, the result is the COMPLETE valid " +
               "JSON response to the ORIGINAL request above. Continue mid-string exactly as the model was writing " +
               "it (e.g. finish the truncated method body, then close any open quotes/braces/brackets).\n" +
               "Your output will be APPENDED verbatim to the partial — it must start with the very next character.\n" +
               "Do NOT emit any preamble, explanation, markdown fences, or JSON keys/objects that the partial " +
               "already contains. Output ONLY the continuation text.\n\n" +
               "### EXACT CONTINUATION POINT (the partial ends right after this) ###\n" +
               "```\n" + anchor + "\n```\n" +
               "### PARTIAL OUTPUT (already produced — continue after its last character) ###\n" +
               "```\n" + cap + "\n```";
    }

    /// <summary>
    /// Stitches a continuation chunk onto the partial, defensively trimming any leading overlap
    /// that repeats the continuation-point tail (models sometimes re-emit the anchor). Finds
    /// the LONGEST suffix of the accumulated text (20..160 chars) that the chunk starts with
    /// and strips it, so a model that re-emitted part of the method declaration doesn't create
    /// a duplicated block.
    /// </summary>
    public static string StitchContinuation(string accumulated, string chunk, int maxOverlapChars = 160)
    {
        var c = (chunk ?? "").TrimStart();
        if (string.IsNullOrWhiteSpace(c)) return accumulated;
        var maxOverlap = Math.Min(maxOverlapChars, Math.Min(accumulated.Length, c.Length));
        for (var len = maxOverlap; len >= 20; len--)
        {
            var tail = accumulated[^len..];
            if (c.StartsWith(tail, StringComparison.Ordinal))
            {
                c = c[len..];
                break;
            }
        }
        return accumulated + c;
    }

    /// <summary>
    /// "Finish this" continuation loop: when a response is truncated by max_tokens (the edit is
    /// bigger than one response), run up to 5 passes that each ask the model to output ONLY the
    /// missing tail, stitch it onto the partial, and stop as soon as the accumulated text parses
    /// as complete JSON. This lets a step proposal carrying a whole method survive a budget that
    /// is smaller than the final payload. Passes are bounded so a stuck model can't loop forever.
    /// </summary>

}
