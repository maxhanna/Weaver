using System.Linq;
using System.Text;
using System.Threading;
using System.Text.Json;
using System.Text.RegularExpressions;
using Weaver.Services;
namespace Weaver.Controllers;
partial class AgentController
{
    private const int StreamWindowChars = 400;
    private const int StreamChunkLen = 40;
    private const int StreamRepeatThreshold = 4;

    /// <summary>
    /// Records the OUTCOME of a recovery retry (finish-this continuation, hint retry, or
    /// non-streaming transport retry) for the endpoint health tracker, and emits a 📊 metric
    /// log entry so users can see in the agent log whether their endpoints' recovery feature
    /// is actually working. Called once per retry after it completes. The counter mutation
    /// lives in EndpointHealthService; this wrapper adds the SSE/log surface.
    /// </summary>
    private async Task RecordRecoveryOutcomeAsync(string? baseUrl, bool recovered, string retryKind,
        bool emitSse = false, CancellationToken ct = default)
    {
        var counts = EndpointHealthService.RecordRecoveryOutcome(baseUrl, recovered);
        // The service increments a counter BEFORE returning, so the sum is always >= 1 for
        // a real endpoint and only 0 when the baseUrl normalized to empty — i.e. this is a
        // proxy for "the endpoint was tracked" (equivalent to the old key.Length > 0 check).
        if (counts.Recovered + counts.RecoveryFailed > 0)
        {
            await EmitLog(emitSse, "metric",
                $"📊 Recovery ({retryKind}): {(recovered ? "recovered ✓" : "still failed ✗")} — " +
                $"{counts.Recovered} recovered / {counts.RecoveryFailed} failed retries on this endpoint", ct: ct);
        }
        else
        {
            await EmitLog(emitSse, "metric",
                $"📊 Recovery ({retryKind}): {(recovered ? "recovered ✓" : "still failed ✗")}", ct: ct);
        }
    }

    /// <summary>
    /// Editor:DisableLLMRetries — when true, ALL recovery retries are skipped: the
    /// non-streaming transport retry, the streaming hint retry, the prose retry, the
    /// finish-this continuation loop, AND the terminal/build transient-blip retry
    /// (RunTerminalCommandWithRetryAsync gates on this same flag). Failed calls/commands
    /// return their partial/error immediately instead. For users whose flaky endpoints
    /// make the retry delay worse than the failure itself (a 300ms pause + full re-stream
    /// per call adds up fast).
    /// </summary>
    private bool LlmRetriesDisabled() => LlmRetriesDisabled(_config);
    internal static bool LlmRetriesDisabled(IConfiguration? config)
    {
        try { return config?.GetValue<bool>("Editor:DisableLLMRetries") ?? false; }
        catch { return false; }
    }

    private string? _runBaseUrl;
    private string? _runModel;

    private Task<string> GetLlamaBaseUrl() => LlmTransport.GetBaseUrlAsync(_runBaseUrl);
    private Task<string> GetLlamaModel() => LlmTransport.GetModelAsync(_runModel);

    /// <summary>Resolves the endpoint requested by the current card, falling back to defaults.</summary>
    private Task<(string baseUrl, string model, string name)> ResolveRunEndpointAsync(string? endpointId) =>
        LlmTransport.ResolveEndpointAsync(endpointId);

    /// <summary>
    /// Polls the llama.cpp server's /slots endpoint while an LLM call streams and
    /// forwards the same "progress" value the server prints to its console as SSE
    /// "progress" events (percent 0-100), so the frontend can show a real loading
    /// bar instead of a spinner. Falls back silently for backends without /slots
    /// (e.g. Ollama), in which case the poller simply stops.
    /// </summary>
    private async Task<(string raw, AgentResponse? response, string? error)> CallLlmRaw(
        string systemPrompt, string userMessage, CancellationToken ct = default,
        TimeSpan? requestTimeout = null, int? maxTokens = null, string? llmRoundLabel = null,
        AgentRunContext? runContext = null)
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
        var timeout = requestTimeout ?? _infiniteTimeout;
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var first = await CallLlmNonStreaming(client, baseUrl + "/v1/chat/completions", model, messages, linkedCts.Token, maxTokens);
        EndpointHealthService.RecordCall(baseUrl, first.raw, first.error);
        // AUTO-SWAP: when the server returns 409 slots_pinned_error (Lemonade Server with a
        // different model pinned in VRAM), unload the pinned model, load the requested one,
        // and retry once. The swap takes 10-30s for large models — the user sees a log line.
        if (IsSlotsPinnedError(first.raw) && await TryAutoSwapModelAsync(baseUrl, model, false, ct))
        {
            using var swapTimeoutCts = new CancellationTokenSource(timeout);
            using var swapLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, swapTimeoutCts.Token);
            first = await CallLlmNonStreaming(client, baseUrl + "/v1/chat/completions", model, messages, swapLinkedCts.Token, maxTokens);
            EndpointHealthService.RecordCall(baseUrl, first.raw, first.error);
        }
        // Non-streaming calls can't reuse partial output, but a one-shot retry still
        // recovers a transient transport blip (network drop, connection reset, premature
        // close, timeout) — the prompt is intact and the second attempt often lands.
        // A tiny delay lets a momentary blip clear before re-issuing. Skipped entirely
        // when Editor:DisableLLMRetries is set (retry delay hurts more than it helps).
        if (!LlmRetriesDisabled() && TransientFailureDetector.IsTransientTransportFailure(first.error))
        {
            try { await Task.Delay(300, ct); } catch { return first; }
            using var retryTimeoutCts = new CancellationTokenSource(timeout);
            using var retryLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, retryTimeoutCts.Token);
            first = await CallLlmNonStreaming(client, baseUrl + "/v1/chat/completions", model, messages, retryLinkedCts.Token, maxTokens);
            EndpointHealthService.RecordCall(baseUrl, first.raw, first.error);
            await RecordRecoveryOutcomeAsync(baseUrl, string.IsNullOrWhiteSpace(first.error), "non-streaming retry", ct: ct);
        }
        try
        {
            if (runContext != null)
                await RecordLlmRoundMetricsForRunAsync(runContext, llmRoundLabel, systemPrompt, userMessage, first.raw, false, ct);
        }
        catch { /* metrics must never break the pipeline */ }
        return first;
    }
    private async Task<(string raw, AgentResponse? response, string? error)> CallLlmRawStreaming(
        string systemPrompt, string userMessage, bool emitSse, CancellationToken ct = default,
        TimeSpan? requestTimeout = null, int? maxTokens = null, string? llmRoundLabel = null,
        AgentRunContext? runContext = null)
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
        var timeout = requestTimeout ?? _infiniteTimeout;
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var first = await CallLlmStreaming(client, baseUrl + "/v1/chat/completions", model, messages, linkedCts.Token, maxTokens, emitSse);
        EndpointHealthService.RecordCall(baseUrl, first.raw, first.error);
        // AUTO-SWAP: same 409 slots_pinned_error handling as the non-streaming path — swap
        // the pinned model and retry once. Must run BEFORE the truncation/transport recovery
        // because a 409 is not a recoverable stream failure — it's a model-not-loaded error.
        if (IsSlotsPinnedError(first.raw) && await TryAutoSwapModelAsync(baseUrl, model, emitSse, ct))
        {
            using var swapTimeoutCts = new CancellationTokenSource(timeout);
            using var swapLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, swapTimeoutCts.Token);
            first = await CallLlmStreaming(client, baseUrl + "/v1/chat/completions", model, messages, swapLinkedCts.Token, maxTokens, emitSse);
            EndpointHealthService.RecordCall(baseUrl, first.raw, first.error);
        }
        // A stream read error / truncated response can kill an otherwise-good response mid-run
        // (e.g. the planner had already produced the correct edit before the connection dropped).
        // Two distinct recoveries, chosen by WHY it failed:
        //   1. Max-token truncation → the requested content (e.g. a whole method in newString) is
        //      bigger than one response's token budget. Re-asking for the FULL response would just
        //      truncate again at the same point, so we run a "finish this" continuation loop that
        //      asks the model to output ONLY the missing tail and appends it until the JSON parses.
        //   2. Transport/stream drop → one retry with the partial as a continuation hint.
        // Editor:DisableLLMRetries skips ALL of this — the partial + error return as-is.
        if (TransientFailureDetector.IsRecoverableStreamFailure(first.raw, first.error) && LlmRetriesDisabled())
        {
            await EmitLog(emitSse, "warn",
                "⚠ LLM retries disabled (Editor:DisableLLMRetries) — failed call returned without recovery.",
                detail: RecoveryDetail(first.raw), ct: ct);
        }
        else if (TransientFailureDetector.IsRecoverableStreamFailure(first.raw, first.error))
        {
            string retryKind;
            if (IsMaxTokenTruncation(first.error))
            {
                retryKind = "finish-this";
                first = await FinishThisTruncatedJsonAsync(
                    client, baseUrl, model, systemPrompt, userMessage, first.raw,
                    linkedCts.Token, maxTokens, emitSse);
            }
            else
            {
                retryKind = "stream retry";
                await EmitLog(emitSse, "recovering",
                    string.Format(StreamRecoveryRetryMessage, first.raw.Length),
                    detail: RecoveryDetail(first.raw), ct: ct);
                using var retryTimeoutCts = new CancellationTokenSource(timeout);
                using var retryLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, retryTimeoutCts.Token);
                var hintMessages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user",   content = AppendPartialContinuationHint(userMessage, first.raw) }
                };
                first = await CallLlmStreaming(client, baseUrl + "/v1/chat/completions", model, hintMessages, retryLinkedCts.Token, maxTokens, emitSse: false);
            }
            EndpointHealthService.RecordCall(baseUrl, first.raw, first.error);
            await RecordRecoveryOutcomeAsync(baseUrl, string.IsNullOrWhiteSpace(first.error), retryKind, emitSse, ct);
        }
        try
        {
            if (runContext != null)
                await RecordLlmRoundMetricsForRunAsync(runContext, llmRoundLabel, systemPrompt, userMessage, first.raw, emitSse, ct);
        }
        catch { /* metrics must never break the pipeline */ }
        return first;
    }
    private Task PollLlamaProgressAsync(string baseUrl, CancellationToken ct) =>
        LlmTransport.PollProgressAsync(baseUrl, ct);

    private Task<(string raw, AgentResponse? parsed, string? error)> CallLlmNonStreaming(
      HttpClient client, string target, string model, object messages,
      CancellationToken ct = default, int? maxTokens = null) =>
        LlmTransport.CallNonStreamingAsync(client, target, model, messages, ct, maxTokens);

    private Task<(string raw, AgentResponse? parsed, string? error)> CallLlmStreaming(
      HttpClient client, string target, string model, object messages,
      CancellationToken ct = default, int? maxTokens = null, bool emitSse = false) =>
        LlmTransport.CallStreamingAsync(client, target, model, messages, ct, maxTokens, emitSse);

    private async Task<(string raw, string? error)> CallLlmRawText(
        string systemPrompt, string userMessage, bool emitSse, CancellationToken ct = default,
        TimeSpan? requestTimeout = null, int? maxTokens = null, bool appendTruncationMarker = false,
        string? llmRoundLabel = null)
    {
        var baseUrl = await GetLlamaBaseUrl();
        var model = await GetLlamaModel();
        var timeout = requestTimeout ?? _infiniteTimeout;
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var first = await CallLlmRawTextOnce(systemPrompt, userMessage, emitSse, linkedCts.Token, maxTokens, appendTruncationMarker);
        EndpointHealthService.RecordCall(baseUrl, first.raw, first.error);
        // AUTO-SWAP: same 409 slots_pinned_error handling as the JSON paths — swap the pinned
        // model and retry once before attempting transport-failure recovery. Must run first
        // because a 409 is a model-not-loaded error, not a recoverable stream failure.
        if (IsSlotsPinnedError(first.raw) && await TryAutoSwapModelAsync(baseUrl, model, emitSse, ct))
        {
            using var swapTimeoutCts = new CancellationTokenSource(timeout);
            using var swapLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, swapTimeoutCts.Token);
            first = await CallLlmRawTextOnce(systemPrompt, userMessage, emitSse, swapLinkedCts.Token, maxTokens, appendTruncationMarker);
            EndpointHealthService.RecordCall(baseUrl, first.raw, first.error);
        }
        // Same recovery as CallLlmRawStreaming: a dropped connection or max-token cut must
        // not discard a good partial response — retry once with the partial as a hint.
        // Editor:DisableLLMRetries skips this — the partial + error return as-is.
        if (TransientFailureDetector.IsRecoverableStreamFailure(first.raw, first.error) && LlmRetriesDisabled())
        {
            await EmitLog(emitSse, "warn",
                "⚠ LLM retries disabled (Editor:DisableLLMRetries) — failed call returned without recovery.",
                detail: RecoveryDetail(first.raw), ct: ct);
        }
        else if (!LlmRetriesDisabled() && TransientFailureDetector.IsRecoverableStreamFailure(first.raw, first.error))
        {
            await EmitLog(emitSse, "recovering",
                string.Format(StreamRecoveryRetryMessage, first.raw.Length),
                detail: RecoveryDetail(first.raw), ct: ct);
            using var retryTimeoutCts = new CancellationTokenSource(timeout);
            using var retryLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, retryTimeoutCts.Token);
            first = await CallLlmRawTextOnce(systemPrompt, AppendPartialContinuationHint(userMessage, first.raw), emitSse: false, retryLinkedCts.Token, maxTokens, appendTruncationMarker);
            EndpointHealthService.RecordCall(baseUrl, first.raw, first.error);
            await RecordRecoveryOutcomeAsync(baseUrl, string.IsNullOrWhiteSpace(first.error), "prose retry", emitSse, ct);
        }
        return first;
    }
    private Task<(string raw, string? error)> CallLlmRawTextOnce(
        string systemPrompt, string userMessage, bool emitSse, CancellationToken ct, int? maxTokens = null,
        bool appendTruncationMarker = false) =>
        LlmTransport.CallRawTextOnceAsync(systemPrompt, userMessage, emitSse, ct, maxTokens, appendTruncationMarker);

    private const string StreamRecoveryRetryMessage =
        "⚠ Stream interrupted — retrying the same call once with the partial response ({0} chars) as a continuation hint.";

    /// <summary>
    /// True when a failed streaming LLM call should be retried once with its partial output
    /// as a continuation hint. Only genuine transport/stream failures or max-token truncation
    /// qualify — a substantive partial response was received but the call did not complete.
    /// Pure semantic failures (JSON parse, hallucination, repetition loops, empty) are NOT
    /// recoverable by re-running and must flow through their existing retry/rejection paths.
    /// </summary>

    private static string? RecoveryDetail(string? partial) => LlmClient.RecoveryDetail(partial);

    private static string AppendPartialContinuationHint(string userMessage, string partial) =>
        LlmClient.AppendPartialContinuationHint(userMessage, partial);

    private static bool IsMaxTokenTruncation(string? error) => LlmClient.IsMaxTokenTruncation(error);

    private static bool LooksLikeCompleteJson(string text) => LlmClient.LooksLikeCompleteJson(text);

    private static string BuildFinishThisPrompt(string userMessage, string partial) =>
        LlmClient.BuildFinishThisPrompt(userMessage, partial);

    private static string StitchContinuation(string accumulated, string chunk, int maxOverlapChars = 160) =>
        LlmClient.StitchContinuation(accumulated, chunk, maxOverlapChars);

    private async Task<(string raw, AgentResponse? parsed, string? error)> FinishThisTruncatedJsonAsync(
        HttpClient client, string baseUrl, string model, string systemPrompt, string userMessage,
        string partial, CancellationToken ct, int? maxTokens = null, bool emitSse = false)
    {
        const int maxPasses = 5;
        var accumulated = partial;
        // If the partial already parses as complete JSON (e.g. the object finished and only
        // trailing prose got cut), don't burn a continuation pass — hand it back as-is.
        if (LooksLikeCompleteJson(accumulated))
            return (accumulated, null, null);
        for (var pass = 0; pass < maxPasses; pass++)
        {
            var finishMessages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = BuildFinishThisPrompt(userMessage, accumulated) }
            };
            var cont = await CallLlmStreaming(client, baseUrl + "/v1/chat/completions", model,
                finishMessages, ct, maxTokens, emitSse: false);
            if (string.IsNullOrWhiteSpace(cont.raw))
            {
                await EmitLog(emitSse, "recovering",
                    $"✂️ Finish-this pass {pass + 1} returned empty ({cont.error}) — stopping", ct: ct);
                break;
            }
            var before = accumulated.Length;
            accumulated = StitchContinuation(accumulated, cont.raw);
            await EmitLog(emitSse, "recovering",
                $"✂️ Edit exceeded token budget — finish-this pass {pass + 1} appended {accumulated.Length - before} chars (total {accumulated.Length})", ct: ct);
            if (LooksLikeCompleteJson(accumulated))
            {
                // The finished text parses — but leave the caller to decide semantics; the raw
                // is returned whole with a null error so the step proposal flow proceeds normally.
                await EmitLog(emitSse, "recovering",
                    $"✂️ Finished truncated response after {pass + 1} continuation pass(es)", ct: ct);
                return (accumulated, null, null);
            }
            // If a pass added almost nothing, the model is stuck — don't burn more passes.
            if (accumulated.Length - before < 20) break;
        }
        return (accumulated, null,
            $"Response truncated at max_tokens — {maxPasses} finish-this passes could not complete it (final {accumulated.Length} chars)");
    }



    private static string? ExtractNewlyAddedMethodName(string? stepChange, string? newStr)
    {
        if (string.IsNullOrWhiteSpace(stepChange)) return null;
        var isAddition = Regex.IsMatch(stepChange,
            @"\b(add|create|insert|implement|define)\b.{0,40}\b(method|function)\b", RegexOptions.IgnoreCase);
        if (!isAddition) return null;
        if (!string.IsNullOrWhiteSpace(newStr))
        {
            var m = AgentMethodInventory.MethodDeclRegex.Match(newStr);
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
            var (raw, _, err) = await CallLlmRawStreaming(sysPrompt, userPrompt, emitSse, ct, requestTimeout: _infiniteTimeout, maxTokens: 500);
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var cleaned = AgentJsonUtilities.ExtractFirstJsonObject(raw);
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
    /// <summary>
    /// Wall-of-text floor: fewer than 1 newline per 1000 chars. Dense single-paragraph prose
    /// from smaller models legitimately lands at ~1 newline per 200-500 chars, so anything
    /// above this floor is normal prose — only genuinely break-free walls trip the check.
    /// </summary>
    private const double WallOfTextMinNewlineRatio = 0.001;

    /// <summary>
    /// Global hallucination detection — call on full LLM output (post-streaming or non-streaming).
    /// Returns an error string if hallucination is detected, null otherwise.
    /// Checks: wall-of-text (very low newline density), semantic repetition of long substrings.
    /// </summary>
    private static string? DetectHallucination(string raw) => LlmClient.DetectHallucination(raw);

    private static string? CheckStreamingHallucination(StringBuilder sb) => LlmClient.CheckStreamingHallucination(sb);

    private static bool IsRepeatingLoop(StringBuilder sb, int windowChars, int chunkLen, int repeatThreshold) =>
        LlmClient.IsRepeatingLoop(sb, windowChars, chunkLen, repeatThreshold);

    private static string ExtractLlmContent(string respText) => LlmClient.ExtractLlmContent(respText);

    private static AgentResponse? ParseAgentResponse(string raw) => LlmClient.ParseAgentResponse(raw);

    /// <summary>Detects the Lemonade-specific "slots_pinned_error" in an LLM response body.
    /// The server returns HTTP 409 with <c>{"error":{"code":"slots_pinned_error",...}}</c>
    /// when the requested model is not loaded in VRAM. The raw body is in
    /// <c>first.raw</c> (non-streaming) or <c>first.raw</c> (streaming) — both paths return
    /// the error JSON body even though the error string differs ("Empty LLM response" vs
    /// "HTTP Conflict").</summary>
    private static bool IsSlotsPinnedError(string raw) =>
        !string.IsNullOrEmpty(raw) && raw.Contains("slots_pinned_error", StringComparison.OrdinalIgnoreCase);

    /// <summary>Attempts to swap the loaded model on the server when a 409
    /// slots_pinned_error is detected. Unloads all pinned models and loads the requested
    /// one via the server's /api/v1/load + /api/v1/unload API (Lemonade Server). Returns
    /// true when the swap succeeded and the caller should retry the LLM call. Emits a log
    /// line so the user sees what happened. Called once per LLM round — never retries the
    /// swap itself.</summary>
    private async Task<bool> TryAutoSwapModelAsync(string baseUrl, string model, bool emitSse, CancellationToken ct)
    {
        if (_aiDiscovery == null) return false;
        await EmitLog(emitSse, "warn",
            $"⚠ Model '{model}' not loaded — swapping (unloading pinned model, loading '{model}')…", ct: ct);
        try
        {
            var result = await _aiDiscovery.SwapModelAsync(baseUrl, model, ct);
            if (result.Success)
            {
                await EmitLog(emitSse, "info", $"✓ Model '{model}' loaded successfully.", ct: ct);
                return true;
            }
            await EmitLog(emitSse, "error", $"✗ Model swap failed: {result.Error}", ct: ct);
        }
        catch (Exception ex)
        {
            await EmitLog(emitSse, "error", $"✗ Model swap error: {ex.Message}", ct: ct);
        }
        return false;
    }
}