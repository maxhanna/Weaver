using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Weaver.Services;
using Weaver;

[ApiController]
[Route("api/agent")]
public class AgentController : ControllerBase
{
    private readonly IHttpClientFactory _clientFactory;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly TerminalService _terminal;
    private readonly FileHintsManager _fileHints;
    private readonly ConfigFileService _configFile;
    private readonly EmailService _emailService;
    private readonly BoardDataService _boardData;
    private const int MaxFileContextChars = 24_000;
    private const int MAX_COMMAND_ITERATIONS = 30;
    private bool _lastConnectionCheckResult = true;
    private static DateTime _nextConnectivityCheck = DateTime.MinValue;
    private static TimeSpan _infiniteTimeout = Timeout.InfiniteTimeSpan;
    private static readonly ConcurrentDictionary<string, PendingQuestion> _pendingQuestions = new();
    private static readonly ConcurrentDictionary<string, PendingContextReview> _pendingContextReviews = new();
    private static readonly string[] UnsafeEditMarkers =
    {
        "…(truncated)", "â€¦(truncated)", "...(truncated)"
    };

    // ── Delimiter constants for edit resolution ────────────────────────────
    // Legacy delimiter constants — kept for fallback parsing
    private const string D_OLD = "<<<OLD>>>";
    private const string D_OLD_END = "<<<END_OLD>>>";
    private const string D_NEW = "<<<NEW>>>";
    private const string D_NEW_END = "<<<END_NEW>>>";
    private const string D_FULL = "<<<FULL_FILE>>>";
    private const string D_FULL_END = "<<<END_FULL_FILE>>>";
    private const string D_DONE = "<<<ALREADY_DONE>>>";

    private const string EditResolveSystemPrompt =
        "You are a surgical code editor. Output ONLY a JSON object.\n\n" +
        "FORMAT A — multi-line (output VERBATIM lines, one per array element — no escaping needed):\n" +
        "{\n" +
        "  \"oldString\": [\n" +
        "    \"  first line EXACTLY as it appears in the file\",\n" +
        "    \"  second line\",\n" +
        "    \"  third line\"\n" +
        "  ],\n" +
        "  \"newString\": [\n" +
        "    \"  first line\",\n" +
        "    \"  replacement second line\"\n" +
        "  ]\n" +
        "}\n\n" +
        "FORMAT B — single-line (escape newlines as \\n):\n" +
        "{\n" +
        "  \"oldString\": \"line 1\\nline 2\\nline 3\",\n" +
        "  \"newString\": \"replacement line 1\\nreplacement line 2\"\n" +
        "}\n\n" +
        "FULL FILE:\n" +
        "{\n" +
        "  \"fullFile\": \"Complete file content (use array format for multi-line)\",\n" +
        "  \"fullFile\": [\"line1\", \"line2\"]\n" +
        "}\n\n" +
        "NO CHANGE:\n" +
        "{\n" +
        "  \"alreadyDone\": true\n" +
        "}\n\n" +
        "CRITICAL RULES:\n" +
        "1. oldString must exist VERBATIM in the file — copy character-for-character including EVERY space and tab\n" +
        "2. oldString must appear exactly ONCE in the file — include 2-3 surrounding lines as anchor context\n" +
        "3. NEVER put ... or […] or /* ... */ or any placeholder in oldString or newString\n" +
        "4. NEVER add trailing whitespace to any line in oldString — trim trailing spaces from each line\n" +
        "5. oldString must NOT have blank first/last lines — trim any empty lines\n" +
        "6. For insertions: include the line BEFORE as part of oldString, repeat it unchanged at the start of newString, then add the new lines after it\n" +
        "7. Every line in oldString must be ≥ 8 characters (after trimming) — lines like `}`, `);`, `{` are too short and match everywhere\n" +
        "8. oldString must be ≥ 20 characters total — short strings cause false matches\n" +
        "9. Use FORMAT A (array) whenever the content has multiple lines — it is more reliable and needs no escaping" +
        "10. Output ONLY the JSON — no markdown, no code fences, no introductory text";

    public AgentController(
        IHttpClientFactory cf, IConfiguration config,
        IWebHostEnvironment env, TerminalService terminal, FileHintsManager fileHints,
        ConfigFileService configFile, EmailService emailService, BoardDataService boardData)
    {
        _clientFactory = cf; _config = config; _env = env; _terminal = terminal;
        _fileHints = fileHints; _configFile = configFile; _emailService = emailService;
        _boardData = boardData;
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

    // ═══════════════════════════════════════════════════════════════════════
    //  SSE / LOGGING
    // ═══════════════════════════════════════════════════════════════════════

    private async Task EmitLog(bool emit, string level, string message, object? detail = null, CancellationToken ct = default)
    {
        if (!emit) return;
        await SendSse(Response, "log", new { ts = DateTime.UtcNow.ToString("o"), level, message, detail }, ct);
    }

    private static async Task SendSse(HttpResponse response, string eventName, object data, CancellationToken ct = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(data);
            await response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", ct);
            await response.Body.FlushAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
        catch (Exception) { }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  EDIT RESOLUTION  (two-phase: plan describes WHAT, resolve finds HOW)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Makes a focused LLM call to resolve the exact edit for a single plan step.
    /// The LLM sees the real file content and outputs delimiter-format diff.
    /// </summary>
    private async Task<(string? oldStr, string? newStr, bool fullFile, string? fullContent,
        bool alreadyDone, string? error)>
        ResolveEditForStep(PlanStep step, string projectRoot, bool emitSse, CancellationToken ct,
            List<(string old, string @new, string error)>? history = null)
    {
        var relPath = step.File.Replace('\\', '/');
        var fullPath = Path.GetFullPath(
            Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));

        var fileExists = System.IO.File.Exists(fullPath);
        var fileContent = fileExists
            ? await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct)
            : string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"FILE: {relPath}");
        sb.AppendLine($"CHANGE REQUIRED: {step.Change}");
        sb.AppendLine();

        if (!fileExists)
        {
            sb.AppendLine("FILE DOES NOT EXIST YET. Use <<<FULL_FILE>>> to create it with complete content.");
        }
        else if (fileContent.Length > 10000)
        {
            sb.AppendLine($"FILE IS LARGE ({fileContent.Length} chars). Relevant excerpt:");
            sb.AppendLine("Use <<<OLD>>> / <<<NEW>>> targeted edits only — copy the EXACT text from the excerpt below.");
            sb.AppendLine();
            sb.AppendLine("```");
            // Show more of the file for better context
            sb.AppendLine(fileContent.Length > 12000
                ? fileContent[..6000] + "\n\n... [middle omitted] ...\n\n" + fileContent[^4000..]
                : fileContent);
            sb.AppendLine("```");
        }
        else
        {
            sb.AppendLine("CURRENT FILE CONTENT:");
            sb.AppendLine("```");
            sb.AppendLine(fileContent);
            sb.AppendLine("```");
        }

        if (history?.Count > 0)
        {
            var hadTruncation = history.Any(h => h.error.Contains("truncated", StringComparison.OrdinalIgnoreCase));
            sb.AppendLine();
            sb.AppendLine($"⚠ PREVIOUS {history.Count} ATTEMPT(S) FAILED. Learn from each failure:");
            for (var i = 0; i < history.Count; i++)
            {
                var h = history[i];
                sb.AppendLine($"\n--- Attempt {i + 1} — Error: {h.error} ---");
                if (!string.IsNullOrWhiteSpace(h.old))
                {
                    sb.AppendLine($"  Your oldString was:");
                    sb.AppendLine($"  ```");
                    sb.AppendLine($"  {h.old[..Math.Min(400, h.old.Length)]}");
                    sb.AppendLine($"  ```");
                    // Show exact file lines at the fuzzy-match location for verbatim copying
                    var exactBlock = BuildExactMatchBlock(fileContent, h.old);
                    if (exactBlock != null)
                    {
                        sb.AppendLine($"  The EXACT lines from the file at the matched location (copy these VERBATIM for oldString):");
                        sb.AppendLine($"  ```");
                        sb.AppendLine($"  {exactBlock}");
                        sb.AppendLine($"  ```");
                    }
                    else
                    {
                        // Show what lines in the file were close (fallback)
                        var hint = BuildExactMatchHint(fileContent, h.old);
                        if (hint != null)
                        {
                            sb.AppendLine($"  These lines in the file are SIMILAR to what you wrote:");
                            sb.AppendLine($"  {hint}");
                        }
                    }
                }
            }
            sb.AppendLine();
            if (hadTruncation)
            {
                sb.AppendLine("Previous FULL_FILE response was too long and got truncated.");
                sb.AppendLine("Use <<<OLD>>> / <<<NEW>>> targeted edits instead — they are smaller and always fit.");
                sb.AppendLine("If multiple changes are needed, make one small edit at a time.");
            }
            else
            {
                sb.AppendLine("COMMON FAILURES to avoid:");
                sb.AppendLine("- Did you ADD extra blank lines at the start or end of OLD? Trim them.");
                sb.AppendLine("- Did you ADD trailing spaces to lines in OLD? Trim trailing whitespace.");
                sb.AppendLine("- Did you change the indentation? Copy INDENTATION character-for-character from the file.");
                sb.AppendLine("- Did you write a shortened/paraphrased version? OLD must be a VERBATIM copy.");
                sb.AppendLine("- Is OLD too short (only 1 line)? Include 1-2 surrounding lines as ANCHOR context.");
                sb.AppendLine("- Look at the SIMILAR lines above — pick the closest one and copy it exactly.");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Output the edit now:");

        if (emitSse)
            await SendSse(Response, "edit-resolve", new { }, ct);
        var (raw, _, _) = await CallLlmRawStreaming(EditResolveSystemPrompt, sb.ToString(), emitSse, ct, _infiniteTimeout, maxTokens: 8192);

        if (string.IsNullOrWhiteSpace(raw))
            return (null, null, false, null, false, "LLM returned empty response");

        string? oldStr = null, newStr = null;

        // Try JSON first
        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (m.Success) cleaned = m.Groups[1].Value.Trim();
            }
            var fb = cleaned.IndexOf('{');
            var lb = cleaned.LastIndexOf('}');
            if (fb >= 0 && lb > fb)
                cleaned = cleaned[fb..(lb + 1)];

            // Pre-process: escape literal newlines inside JSON string values so parsing doesn't choke
            // The LLM often outputs raw newlines instead of \n in string values
            cleaned = RepairJsonNewlines(cleaned);

            using var jDoc = JsonDocument.Parse(cleaned);
            var jRoot = jDoc.RootElement;

            // Already done
            if (jRoot.TryGetProperty("alreadyDone", out var ad) && ad.GetBoolean())
                return (null, null, false, null, true, null);

            // Full file (string or array)
            if (jRoot.TryGetProperty("fullFile", out var ff))
            {
                string? body = null;
                if (ff.ValueKind == JsonValueKind.String)
                    body = ff.GetString();
                else if (ff.ValueKind == JsonValueKind.Array)
                {
                    var lines = new List<string>();
                    foreach (var item in ff.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                            lines.Add(item.GetString() ?? "");
                    }
                    if (lines.Count > 0) body = string.Join("\n", lines);
                }
                if (!string.IsNullOrWhiteSpace(body))
                {
                    body = StripFullFileFence(body);
                    return (null, null, true, body, false, null);
                }
            }

            // Targeted edit — support both string and array formats
            // String:  "oldString": "line1\nline2"
            // Array:   "oldString": ["line1", "line2"]
            {
                string? ResolveString(JsonElement el)
                {
                    if (el.ValueKind == JsonValueKind.String)
                        return el.GetString();
                    if (el.ValueKind == JsonValueKind.Array)
                    {
                        var lines = new List<string>();
                        foreach (var item in el.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                                lines.Add(item.GetString() ?? "");
                        }
                        return lines.Count > 0 ? string.Join("\n", lines) : null;
                    }
                    return null;
                }

                oldStr = jRoot.TryGetProperty("oldString", out var osEl) ? ResolveString(osEl) : null;
                newStr = jRoot.TryGetProperty("newString", out var nsEl) ? ResolveString(nsEl) : null;
            }

            if (!string.IsNullOrWhiteSpace(oldStr))
                return (oldStr, newStr ?? "", false, null, false, null);

            return (null, null, false, null, false, "JSON has no oldString, fullFile, or alreadyDone field");
        }
        catch
        {
            // Fallback: legacy delimiter format
            if (raw.Contains(D_DONE, StringComparison.OrdinalIgnoreCase))
                return (null, null, false, null, true, null);

            var ffS = raw.IndexOf(D_FULL, StringComparison.OrdinalIgnoreCase);
            var ffE = raw.IndexOf(D_FULL_END, StringComparison.OrdinalIgnoreCase);
            if (ffS >= 0)
            {
                if (ffE < ffS)
                    return (null, null, false, null, false, "Response truncated — FULL_FILE not closed.");
                var body = raw[(ffS + D_FULL.Length)..ffE];
                body = StripFullFileFence(body);
                return (null, null, true, body, false, null);
            }

            var oS = raw.IndexOf(D_OLD, StringComparison.OrdinalIgnoreCase);
            var oE = raw.IndexOf(D_OLD_END, StringComparison.OrdinalIgnoreCase);
            var nS = raw.IndexOf(D_NEW, StringComparison.OrdinalIgnoreCase);
            var nE = raw.IndexOf(D_NEW_END, StringComparison.OrdinalIgnoreCase);

            if (oS < 0)
                return (null, null, false, null, false, "No edit markers found — check LLM output");
            if (oE < 0 || nS < 0 || nE < 0)
                return (null, null, false, null, false, "Response truncated — markers not closed");

            oldStr = raw[(oS + D_OLD.Length)..oE].TrimStart('\r', '\n').TrimEnd('\r', '\n');
            newStr = raw[(nS + D_NEW.Length)..nE].TrimStart('\r', '\n').TrimEnd('\r', '\n');

            if (string.IsNullOrWhiteSpace(oldStr))
                return (null, null, false, null, false, "OLD section is empty");

            return (oldStr, newStr, false, null, false, null);
        }
    }

    /// <summary>
    /// Resolves and applies a single file edit step.  Up to 3 resolve attempts
    /// before giving up and moving on.  Each failure re-feeds the bad anchor back
    /// to the LLM so it can correct itself.
    /// </summary>
    private async Task<int> ResolveAndApplyEdit(
        PlanStep step, string projectRoot, bool emitSse, CancellationToken ct,
        List<object> allResults, int stepIndex, int planItemIndex = -1,
        string? cardId = null)
    {
        var relPath = step.File.Replace('\\', '/');
        var fullPath = Path.GetFullPath(
            Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));

        await EmitLog(emitSse, "info", $"▶ Resolving: {relPath} — {step.Change}", ct: ct);

        if (emitSse)
            await SendSse(Response, "step", new
            {
                index = stepIndex,
                type = "edit",
                status = "running",
                path = relPath,
                description = step.Change,
                planItemIndex
            }, ct);

        var history = new List<(string old, string @new, string error)>();
        var planOldStr = step.OldString;
        var planNewStr = step.NewString;
        var planOldTried = false;
        var stuckCount = 0;
        var resolveStuckCount = 0;
        var lastResolveError = "";
        var lastOld = "";
        const int MaxAttempts = 20;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            string? oldStr = null, newStr = null, resolveError = null;
            bool fullFile = false, alreadyDone = false;
            string? fullContent = null;

            // Attempt 0: use plan-provided oldString/newString directly if available
            if (attempt == 0 && !string.IsNullOrWhiteSpace(planOldStr) && !planOldTried)
            {
                planOldTried = true;
                oldStr = AgentUtilities.NormalizeLineEndings(planOldStr);
                newStr = AgentUtilities.NormalizeLineEndings(planNewStr ?? "");
                await EmitLog(emitSse, "info",
                    $"Using plan-provided edit for {relPath}", ct: ct);
            }
            else
            {
                if (attempt > 0)
                    await EmitLog(emitSse, "warn",
                        $"Resolve retry {attempt + 1} for {relPath}", new { step, projectRoot }, ct: ct);

                (oldStr, newStr, fullFile, fullContent, alreadyDone, resolveError) =
                    await ResolveEditForStep(step, projectRoot, emitSse, ct, history);
            }

            if (resolveError != null)
            {
                await EmitLog(emitSse, "warn",
                    $"Resolve attempt {attempt + 1}/{MaxAttempts}: {resolveError}", new {resolveError, fullContent}, ct: ct);
                history.Add((step.OldString ?? "", step.NewString ?? "", resolveError));

                // Detect stuck: 3+ consecutive resolve errors (LLM not outputting valid format)
                if (resolveError == lastResolveError)
                    resolveStuckCount++;
                else
                { resolveStuckCount = 0; lastResolveError = resolveError; }
                if (resolveStuckCount >= 3)
                {
                    await EmitLog(emitSse, "error",
                        $"LLM keeps failing to produce valid edit output — aborting {relPath}", ct: ct);
                    goto RecordFailure;
                }
                continue;
            }

            if (alreadyDone)
            {
                await EmitLog(emitSse, "info", $"✓ Already done: {relPath}", ct: ct);
                var r = new Dictionary<string, object?>
                {
                    ["index"] = stepIndex,
                    ["type"] = "edit",
                    ["status"] = "skipped",
                    ["path"] = relPath,
                    ["reason"] = "already done",
                    ["planItemIndex"] = planItemIndex
                };
                if (emitSse) await SendSse(Response, "step", r, ct);
                allResults.Add(r);
                return stepIndex + 1;
            }

            // ── Full file replacement ─────────────────────────────────────
            if (fullFile && fullContent != null)
            {
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                await System.IO.File.WriteAllTextAsync(fullPath, fullContent, Encoding.UTF8, ct);
                await EmitLog(emitSse, "success",
                    $"✓ Written {relPath} ({fullContent.Length} chars)", new {fullContent, fullFile }, ct: ct);
                var r = new Dictionary<string, object?>();
                PopulateEditResult(r, "modified", relPath, null, fullContent, "");
                r["index"] = stepIndex;
                r["planItemIndex"] = planItemIndex;
                if (emitSse) await SendSse(Response, "step", r, ct);
                allResults.Add(r);
                await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct);
                return stepIndex + 1;
            }

            // ── Targeted replacement ──────────────────────────────────────
            var fileContent = System.IO.File.Exists(fullPath)
                ? await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct)
                : string.Empty;

            var (replaced, newContent, matchError, snippet) =
                TryReplaceSafe(fileContent, oldStr!, newStr ?? string.Empty);

            if (!replaced)
            {
                var err = matchError ?? "oldString not found verbatim";
                if (!string.IsNullOrEmpty(snippet)) err += $". Nearby: {snippet}";
                await EmitLog(emitSse, "warn",
                    $"Edit attempt {attempt + 1}/{MaxAttempts} failed for {relPath}: {err}", ct: ct);

                // Self-heal: if the oldString doesn't match but fuzzy location is found,
                // extract the exact file lines and retry immediately
                var correctedBlock = BuildExactMatchBlock(fileContent, oldStr!);
                if (correctedBlock != null && correctedBlock != oldStr)
                {
                    await EmitLog(emitSse, "info",
                        $"Self-healing: extracted exact oldString from file ({correctedBlock.Length} chars)", ct: ct);
                    var (replaced2, newContent2, _, _) =
                        TryReplaceSafe(fileContent, correctedBlock, newStr ?? string.Empty);
                    if (replaced2)
                    {
                        var (approved2, verifyReason2, _) =
                            VerifyEdit(correctedBlock, newStr ?? "", fileContent, newContent2);
                        if (approved2)
                        {
                            await System.IO.File.WriteAllTextAsync(fullPath, newContent2, Encoding.UTF8, ct);
                            await EmitLog(emitSse, "success", $"✓ Edited {relPath} (self-healed)", ct: ct);
                            var r = new Dictionary<string, object?>();
                            PopulateEditResult(r, "modified", relPath, correctedBlock, newStr ?? "", "self-healed");
                            r["index"] = stepIndex;
                            r["planItemIndex"] = planItemIndex;
                            if (emitSse) await SendSse(Response, "step", r, ct);
                            allResults.Add(r);
                            await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct);
                            return stepIndex + 1;
                        }
                    }
                }

                history.Add((oldStr!, newStr ?? "", err));


                // Detect stuck: same oldString repeated 3+ times
                if (string.Equals(oldStr, lastOld, StringComparison.Ordinal))
                    stuckCount++;
                else
                { stuckCount = 0; lastOld = oldStr ?? ""; }
                if (stuckCount >= 3)
                {
                    await EmitLog(emitSse, "error",
                        $"LLM keeps producing the same oldString — aborting {relPath}", ct: ct);
                    goto RecordFailure;
                }
                continue;
            }

            var (approved, verifyReason, _) =
                VerifyEdit(oldStr!, newStr ?? "", fileContent, newContent);
            if (!approved)
            {
                await EmitLog(emitSse, "warn",
                    $"Verify failed for {relPath}: {verifyReason}", ct: ct);
                history.Add((oldStr!, newStr ?? "", verifyReason));

                if (string.Equals(oldStr, lastOld, StringComparison.Ordinal))
                    stuckCount++;
                else
                { stuckCount = 0; lastOld = oldStr ?? ""; }
                if (stuckCount >= 3)
                {
                    await EmitLog(emitSse, "error",
                        $"LLM keeps producing the same oldString — aborting {relPath}", ct: ct);
                    goto RecordFailure;
                }
                continue;
            }

            await System.IO.File.WriteAllTextAsync(fullPath, newContent, Encoding.UTF8, ct);

            // Verify replacement: the new string should be present in the written file
            if (!string.IsNullOrWhiteSpace(newStr) &&
                !newContent.Contains(newStr, StringComparison.Ordinal))
            {
                // Indentation may have been adjusted — retry with leading whitespace stripped
                var strippedNew = StripLineLeadingWhitespace(newStr);
                var strippedContent = StripLineLeadingWhitespace(newContent);
                if (!strippedContent.Contains(strippedNew, StringComparison.Ordinal))
                {
                    var verr = "Replacement produced mismatched content — oldString matched wrong location";
                    await EmitLog(emitSse, "warn", $"Verify failed for {relPath}: {verr}", ct: ct);
                    history.Add((oldStr!, newStr, verr));
                    continue;
                }
            }

            await EmitLog(emitSse, "success", $"✓ Edited {relPath}", ct: ct);

            var result = new Dictionary<string, object?>();
            PopulateEditResult(result, "modified", relPath, oldStr, newStr ?? "", "");
            result["index"] = stepIndex;
            result["planItemIndex"] = planItemIndex;
            if (emitSse) await SendSse(Response, "step", result, ct);
            allResults.Add(result);
            await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct);
            return stepIndex + 1;
        }

    RecordFailure:
        var lastErr = history.Count > 0 ? history[^1].error : "resolve failed";
        await EmitLog(emitSse, "error",
            $"✗ All resolve attempts failed for {relPath}: {lastErr}", ct: ct);
        var fail = new Dictionary<string, object?>
        {
            ["index"] = stepIndex,
            ["type"] = "edit",
            ["status"] = "error",
            ["path"] = relPath,
            ["error"] = lastErr,
            ["planItemIndex"] = planItemIndex
        };
        if (emitSse) await SendSse(Response, "step", fail, ct);
        allResults.Add(fail);
        return stepIndex + 1;
    }

    private async Task PersistBoardDataPlanStepAsync(string? cardId, int planItemIndex, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardId) || planItemIndex < 0)
            return;

        try
        {
            var raw = await _boardData.LoadRawAsync();
            if (string.IsNullOrWhiteSpace(raw)) return;

            using var jsonDoc = JsonDocument.Parse(raw);
            var root = JsonNode.Parse(jsonDoc.RootElement.GetRawText())?.AsObject();
            if (root == null) return;

            var columns = new[] { "todo", "doing", "done", "selfImproving" };
            foreach (var column in columns)
            {
                if (!root.TryGetPropertyValue(column, out var columnNode) || columnNode is not JsonArray columnItems)
                    continue;

                foreach (var item in columnItems)
                {
                    if (item is not JsonObject cardObj || cardObj["id"]?.GetValue<string>() != cardId)
                        continue;

                    if (cardObj["_plan"] is not JsonObject planObj || planObj["items"] is not JsonArray items)
                        continue;

                    var target = items.FirstOrDefault(i => i is JsonObject obj && obj["index"]?.GetValue<int>() == planItemIndex);
                    if (target is JsonObject stepObj)
                    {
                        stepObj["done"] = true;
                        var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                        await _boardData.SaveRawAsync(saved);
                        if (emitSse)
                        {
                            await SendSse(Response, "refresh", new
                            {
                                target = "boarddata",
                                reason = "plan-step-completed",
                                cardId,
                                planItemIndex
                            }, ct);
                        }
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to persist boarddata plan progress", new { cardId, planItemIndex, error = ex.Message });
        }
    }

    private async Task<(AgentPlan? plan, HashSet<int>? completedIndices)> LoadPlanFromBoardDataAsync(string? cardId)
    {
        if (string.IsNullOrWhiteSpace(cardId))
            return (null, null);

        try
        {
            var raw = await _boardData.LoadRawAsync();
            if (string.IsNullOrWhiteSpace(raw)) return (null, null);

            using var jsonDoc = JsonDocument.Parse(raw);
            var root = JsonNode.Parse(jsonDoc.RootElement.GetRawText())?.AsObject();
            if (root == null) return (null, null);

            var columns = new[] { "todo", "doing", "done", "selfImproving" };
            foreach (var column in columns)
            {
                if (!root.TryGetPropertyValue(column, out var columnNode) || columnNode is not JsonArray columnItems)
                    continue;

                foreach (var item in columnItems)
                {
                    if (item is not JsonObject cardObj || cardObj["id"]?.GetValue<string>() != cardId)
                        continue;

                    if (cardObj["_plan"] is not JsonObject planObj)
                        continue;

                    var itemsArr = planObj["items"] as JsonArray;
                    if (itemsArr == null || itemsArr.Count == 0)
                        continue;

                    var steps = new List<PlanStep>();
                    var completed = new HashSet<int>();
                    for (var i = 0; i < itemsArr.Count; i++)
                    {
                        if (itemsArr[i] is not JsonObject si) continue;
                        var step = new PlanStep
                        {
                            File = si["file"]?.GetValue<string>() ?? "",
                            Change = si["change"]?.GetValue<string>() ?? "",
                            Priority = si["priority"]?.GetValue<int>() ?? 1,
                            OldString = si["oldString"]?.GetValue<string>() ?? "",
                            NewString = si["newString"]?.GetValue<string>() ?? ""
                        };
                        // Use stored index if available, otherwise position in array
                        var idx = si["index"]?.GetValue<int>() ?? i;
                        steps.Add(step);
                        var done = si["done"]?.GetValue<bool>() ?? false;
                        if (done) completed.Add(idx);
                    }

                    if (steps.Count == 0) return (null, null);

                    var plan = new AgentPlan
                    {
                        Summary = planObj["summary"]?.GetValue<string>() ?? "",
                        Plan = steps
                    };

                    return (plan, completed.Count > 0 ? completed : null);
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to load plan from board data", new { cardId, error = ex.Message });
        }

        return (null, null);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  PLANNING — simplified, no oldString/newString in plan
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Lightweight planning prompt.  Steps contain only FILE + CHANGE (description).
    /// The actual edit (oldString/newString) is resolved per-step during execution.
    /// </summary>
    private static string BuildPlanningPrompt() =>
        "You are a software-engineering agent. " +
        "Produce a concise JSON plan of code changes.\n" +
        "Output ONLY valid JSON — no markdown fences, no extra text.\n\n" +
        "### STEP TYPES (the \"file\" field) ###\n" +
        "  \"relative/path.ext\"  — Edit an existing file (must be in discovery context). INCLUDE \"oldString\" and \"newString\".\n" +
        "  \"_command\"            — Run a terminal command; put the full command in \"change\"\n" +
        "  \"_create_file\"        — Create a new file: put full file content in \"newString\", leave \"oldString\" empty\n" +
        "  \"_web_search\"         — Search the web; put the query in \"change\"\n" +
        "  \"_web_fetch\"          — Fetch a URL; put the full URL in \"change\"\n" +
        "  \"_git\"                — Git operation (commit/pull/push/branch/revert)\n" +
        "  \"_rename_file\"        — Rename: put \"oldpath → newpath\" in \"change\"\n" +
        "  \"_delete_file\"        — Delete a file path in \"change\"\n" +
        "  \"_show\"               — Display text to the user (use last)\n" +
        "  \"_done\"               — Task is already complete; put reason in \"change\"\n" +
        "  \"_checkpoint\"         — Split large refactor into phases\n\n" +
        "### RULES ###\n" +
        "1. Only reference files that exist in the discovery context\n" +
        "2. For EDIT steps (relative/path.ext), INCLUDE \"oldString\" — copy EXACT VERBATIM text from the file (character-for-character including indentation). INCLUDE \"newString\" — the replacement text\n" +
        "3. WEB FIRST: add a _web_search step if you need current API docs or recent data\n" +
        "4. COMMANDS BEFORE EDITS: if a file must exist first, add _command BEFORE the edit step\n" +
        "5. SELF-STOP: emit a single _done step if the code already satisfies the requirement\n" +
        "6. Score: Score from 0-100 (0 being lowest) of how confident you are the steps completely solve the task. \n" +
        "7. CHECKPOINTS: for >4 files or >8 steps, split phases with _checkpoint\n" +
        "8. Each step must be ONE focused change — do not combine unrelated edits in one step\n" +
        "9. \"oldString\" must appear exactly ONCE in the file — include 1-2 surrounding lines as context if needed\n" +
        "10. Never put ... or [...] or placeholders in oldString or newString\n" +
        "11. If the file path contains \"\\\\\" escape it for JSON: use \"path/to/file.ext\"\n\n" + 
        "### OUTPUT FORMAT ###\n" +
        "{\n" +
        "  \"thinking\": \"1-4 lines: which files need changing and why\",\n" +
        "  \"summary\": \"one sentence: what this plan accomplishes\",\n" +
        "  \"score\": <0-100>,\n" +
        "  \"plan\": [\n" +
        "    {\n" +
        "      \"file\": \"wwwroot/app.js\",\n" +
        "      \"change\": \"Modify confirmFilePicker to append files to existing list\",\n" +
        "      \"oldString\": \"var existing = Array.isArray(card.attached) ? card.attached : (card.attached ? [card.attached] : []);\\nfiles.forEach(function (f) {\\nif (existing.indexOf(f) === -1) existing.push(f);\\n});\\ncard.attached = existing;\",\n" +
        "      \"newString\": \"var existing = Array.isArray(card.attached) ? card.attached : (card.attached ? [card.attached] : []);\\nfiles.forEach(function (f) {\\nif (existing.indexOf(f) === -1) existing.push(f);\\n});\\ncard.attached = existing;\\nvm.saveCards();\"\n" +
        "    },\n" +
        "    {\n" +
        "      \"file\": \"_command\",\n" +
        "      \"change\": \"dotnet build\"\n" +
        "    }\n" +
        "  ]\n" +
        "}";

    private async Task<AgentPlan?> AnalyzePromptAndPlanCodeChanges(
        string prompt, string discoveryContext, string projectRoot, bool emitSse,
        CancellationToken ct = default, string? steeringContext = null)
    {
        var planningPrompt = BuildPlanningPrompt();

        var userPrompt = new StringBuilder();
        userPrompt.AppendLine("### TASK ###");
        userPrompt.AppendLine(prompt);
        if (!string.IsNullOrWhiteSpace(steeringContext))
        {
            userPrompt.AppendLine();
            userPrompt.AppendLine("### USER STEERING ###");
            userPrompt.AppendLine(steeringContext);
        }
        userPrompt.AppendLine();
        userPrompt.AppendLine("### PROJECT ROOT ###");
        userPrompt.AppendLine(projectRoot);
        userPrompt.AppendLine("### DISCOVERY CONTEXT (only use paths listed here) ###");
        userPrompt.AppendLine(BuildPlannerDiscoveryContext(discoveryContext));

        var (raw, _, llmError) = await CallLlmRawStreaming(
            planningPrompt, userPrompt.ToString(), emitSse, ct,
            requestTimeout: _infiniteTimeout, maxTokens: 2048);

        if (string.IsNullOrWhiteSpace(raw))
        {
            await EmitLog(emitSse, "error",
                $"LLM returned empty plan response: {llmError ?? "no content"}", ct: ct);
            return null;
        }

        // Try JSON first, fall back to delimiter format
        AgentPlan? plan = ParsePlan(raw);
        if (plan == null && (raw.Contains("<<<STEP", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("### STEP", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("STEP", StringComparison.OrdinalIgnoreCase)))
            plan = AgentUtilities.ParseDelimitedPlan(raw);

        if (plan == null)
        {
            bool containsLLMError = false;
            bool containsLLMLoading = false;
            if (!string.IsNullOrEmpty(raw)) {
                if (raw.ToLower().Contains("error")) {
                    containsLLMError = true;
                }
                if (raw.ToLower().Contains("loading model")) {
                    containsLLMLoading = true;
                }
            }
            string errorMessage = containsLLMLoading ? " Model Loading. Please retry after a short period of time."
                                    : containsLLMError ? " LLM Returned Error state. Check LLM." 
                                    : "";
            await EmitLog(emitSse, "error", "Failed to parse plan." + errorMessage, raw, ct: ct);
            return null;
        }

        // Check for missing web search
        var webViolation = DetectMissingWebSearch(prompt, plan);
        if (webViolation != null)
            await EmitLog(emitSse, "warn", $"Plan may need web search: {webViolation}", ct: ct);

        await EmitLog(emitSse, "info",
            $"Plan: {plan.Plan.Count} step(s) — score {plan.Score}/100", new { plan }, ct: ct);

        return plan;
    }

    private static bool LooksLikeTruncated(string raw)
    {
        var opens = raw.Count(c => c is '{' or '[');
        var closes = raw.Count(c => c is '}' or ']');
        if (opens > closes + 1) return true;
        var lastLine = raw.Split('\n')[^1];
        return Regex.Matches(lastLine, @"(?<!\\)""").Count % 2 != 0;
    }

    private async Task<(AgentPlan? plan, string? error)> ParseAndScore(
        string raw, bool emitSse, CancellationToken ct)
    {
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```"))
        {
            var m = Regex.Match(cleaned, @"```(?:text|json)?\s*([\s\S]*?)```",
                RegexOptions.IgnoreCase);
            cleaned = m.Success ? m.Groups[1].Value.Trim() : cleaned.TrimStart('`');
        }

        AgentPlan? parsed = null;
        if (cleaned.Contains("<<<STEP", StringComparison.OrdinalIgnoreCase))
            parsed = AgentUtilities.ParseDelimitedPlan(cleaned);
        if (parsed == null)
            parsed = ParsePlan(cleaned);

        if (parsed == null)
        {
            await EmitLog(emitSse, "error", "Failed to parse plan.", cleaned, ct: ct);
            return (null, "Response was unparseable.");
        }

        // Size violations only matter if oldString is present (legacy plans)
        var violations = GetPlanSizeViolations(parsed);
        if (violations.Count > 0)
        {
            // With the new resolve architecture, oversized oldStrings are handled
            // at execution time — just warn, don't penalise the score
            await EmitLog(emitSse, "warn",
                $"{violations.Count} oversized anchor(s) — will attempt resolve at execution time",
                ct: ct);
        }

        return (parsed, null);
    }

    private static string? DetectMissingWebSearch(string prompt, AgentPlan plan)
    {
        var lower = prompt.ToLowerInvariant();
        var triggers = new[] { "search for", "look up", "find out", "up to date", "up-to-date" };
        var hit = triggers.FirstOrDefault(t => lower.Contains(t));
        if (hit == null) return null;
        var hasWebStep = plan.Plan?.Any(s =>
            s.File.Equals("_web_search", StringComparison.OrdinalIgnoreCase) ||
            s.File.Equals("_web_fetch", StringComparison.OrdinalIgnoreCase)) ?? false;
        if (hasWebStep) return null;
        return $"Prompt contains \"{hit}\" but plan has no _web_search step.";
    }

    private static string BuildPlannerDiscoveryContext(string fullDiscovery)
    {
        if (string.IsNullOrWhiteSpace(fullDiscovery)) return fullDiscovery;
        const int MaxLinesPerFile = 60;
        const int MaxFiles = 4;
        var result = new StringBuilder();
        var sections = Regex.Split(fullDiscovery, @"(?=### \S)");
        var fileCount = 0;
        foreach (var section in sections)
        {
            if (string.IsNullOrWhiteSpace(section)) continue;
            var trimmed = section.TrimStart();
            if (!trimmed.StartsWith("### read") && !trimmed.StartsWith("### list"))
            {
                result.AppendLine(section.TrimEnd());
                continue;
            }
            if (fileCount >= MaxFiles)
            {
                result.AppendLine("...(additional files omitted from planner context)");
                break;
            }
            var lines = section.Split('\n');
            result.AppendLine(lines[0]);
            var body = lines.Skip(1).ToArray();
            if (body.Length <= MaxLinesPerFile)
                result.AppendLine(string.Join('\n', body));
            else
            {
                result.AppendLine(string.Join('\n', body.Take(MaxLinesPerFile)));
                result.AppendLine($"...(truncated — full content used during execution)");
            }
            result.AppendLine();
            fileCount++;
        }
        return result.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  BOOTSTRAP DISCOVERY
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<(string discoveryText, List<object> steps)> RunLightBootstrap(
        List<string> attachedFiles, string projectRoot, bool emitSse)
    {
        await EmitLog(emitSse, "info", "Fast-path bootstrap: reading attached files only");
        var plan = (attachedFiles ?? new List<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select((f, i) => new AgentStep
            {
                Index = i,
                Type = "read",
                Path = f.Replace('\\', '/'),
                Description = $"Read attached {f}"
            }).ToList();

        if (plan.Count == 0) return ("", new List<object>());

        var steps = await ExecuteSteps(plan, projectRoot, 0, emitSse);
        var sb = new StringBuilder();
        sb.AppendLine("Attached files (edit these paths only):");
        foreach (var f in attachedFiles ?? new List<string>())
            sb.AppendLine($"  - {f.Replace('\\', '/')}");
        foreach (var item in steps)
        {
            if (item is Dictionary<string, object?> r && r.TryGetValue("output", out var o) && o != null)
                sb.AppendLine($"\n### {r.GetValueOrDefault("path")}\n{o}");
        }
        return (sb.ToString(), steps);
    }

    private async Task<(string discoveryText, List<object> steps)> RunBootstrapDiscovery(
        string prompt, string projectRoot, bool emitSse,
        List<string>? attachedFiles = null, CancellationToken ct = default)
    {
        if (attachedFiles != null && attachedFiles.Count > 0)
            return await RunLightBootstrap(attachedFiles, projectRoot, emitSse);

        await EmitLog(emitSse, "info", "Phase 1 — DISCOVER: enumerating project files…", ct: ct);
        var allSteps = new List<object>();

        var listStep = new AgentStep { Index = 0, Type = "list", Path = "", Description = "Auto: list project root" };
        var listResults = await ExecuteDiscoveryStepsConcurrent(
            new List<AgentStep> { listStep }, projectRoot, 0, emitSse);
        allSteps.AddRange(listResults);

        if (!Directory.Exists(projectRoot)) return ("", allSteps);

        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "node_modules", ".git", "bin", "obj", "dist", ".angular", "packages", ".vs", ".idea" };

        var allFiles = Directory.EnumerateFiles(projectRoot, "*.*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/'))
            .Where(rel => !skipDirs.Any(d =>
                rel.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase) ||
                rel.Contains("/" + d + "/", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (allFiles.Count == 0) return ("", allSteps);

        var hintedFiles = _fileHints.GetFilesForPrompt(prompt, projectRoot)
            .Where(f => allFiles.Any(a => string.Equals(a, f, StringComparison.OrdinalIgnoreCase)))
            .Take(4).ToList();

        var heuristicCandidates = ApplyTaskTypeHeuristics(prompt, allFiles);
        var candidatePool = hintedFiles
            .Concat(heuristicCandidates)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(60).ToList();

        List<string> toRead;
        if (candidatePool.Count <= 6)
        {
            toRead = candidatePool;
            await EmitLog(emitSse, "info",
                $"Phase 1 — {candidatePool.Count} candidate(s), reading all directly", ct: ct);
        }
        else
        {
            await EmitLog(emitSse, "info",
                $"Phase 1 — selecting from {candidatePool.Count} candidates…", ct: ct);
            var selected = await SelectRelevantFilesWithLlm(prompt, candidatePool, emitSse, ct);
            toRead = hintedFiles.Concat(selected)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        }

        toRead = toRead.Where(f =>
        {
            var full = Path.GetFullPath(Path.Combine(projectRoot, f.Replace('/', Path.DirectorySeparatorChar)));
            return System.IO.File.Exists(full) && AgentUtilities.IsPathUnderRoot(full, projectRoot);
        }).ToList();

        await EmitLog(emitSse, "info",
            $"Phase 1 — reading {toRead.Count} file(s): {string.Join(", ", toRead)}", ct: ct);

        if (toRead.Count > 0)
        {
            var readPlan = toRead.Select((f, i) => new AgentStep
            {
                Index = i,
                Type = "read",
                Path = f,
                Description = $"Auto: read {f}",
                Prompt = prompt
            }).ToList();

            var readResults = await ExecuteDiscoveryStepsConcurrent(
                readPlan, projectRoot, allSteps.Count, emitSse);
            allSteps.AddRange(readResults);
            foreach (var f in toRead) _fileHints.LearnFromGrepOutput(prompt, f, projectRoot);
        }

        var sb = new StringBuilder();
        sb.AppendLine("ONLY use paths that appear below. Do NOT invent paths.");
        sb.AppendLine();
        foreach (var item in allSteps)
        {
            if (item is not Dictionary<string, object?> r) continue;
            var type = r.TryGetValue("type", out var t) ? t?.ToString() : "";
            if (!r.TryGetValue("output", out var output) ||
                output == null || string.IsNullOrEmpty(output.ToString())) continue;
            sb.AppendLine($"### {type} {r.GetValueOrDefault("path") ?? r.GetValueOrDefault("description")}");
            sb.AppendLine(output.ToString());
            sb.AppendLine();
        }

        await EmitLog(emitSse, "info",
            $"Phase 1 complete — {allSteps.Count} step(s), {toRead.Count} file(s) read", ct: ct);
        return (sb.ToString(), allSteps);
    }

    private static List<string> ApplyTaskTypeHeuristics(string prompt, List<string> allFiles)
    {
        var lower = prompt.ToLowerInvariant();
        var isStyle = Regex.IsMatch(lower, @"\b(style|css|color|theme|layout|spacing|font|design|ui|ux|brand|visual|margin|padding|border|shadow|panel|card)\b");
        var isHtml = Regex.IsMatch(lower, @"\b(html|template|page|view|markup|modal|popup|section|div)\b");
        var isJs = Regex.IsMatch(lower, @"\b(javascript|script|function|event|click|toggle|show|hide|angular|react|vue|component|state|behavior)\b");
        var isBackend = Regex.IsMatch(lower, @"\b(api|endpoint|controller|service|database|model|route|logic|backend|server|c#|csharp|dotnet)\b");
        var isConfig = Regex.IsMatch(lower, @"\b(config|setting|option|appsettings|environment|json)\b");
        var keywords = AgentUtilities.ExtractMeaningfulKeywords(lower);

        var scored = allFiles.Select(f =>
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            var nameLow = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
            var pathLow = f.ToLowerInvariant();
            var score = 0;
            if (isStyle) { if (ext is ".css" or ".scss" or ".sass") score += 120; else if (ext is ".html") score += 60; else if (ext is ".js") score += 20; }
            if (isHtml) { if (ext is ".html" or ".htm") score += 120; else if (ext is ".css") score += 50; else if (ext is ".js") score += 30; }
            if (isJs) { if (ext is ".js" or ".ts" or ".jsx" or ".tsx") score += 120; else if (ext is ".html") score += 40; }
            if (isBackend) { if (ext == ".cs") score += 120; else if (ext == ".json") score += 30; }
            if (isConfig) { if (ext is ".json" or ".yaml" or ".yml") score += 120; }
            foreach (var kw in keywords) if (nameLow.Contains(kw)) score += 50;
            if ((isStyle || isHtml || isJs) && pathLow.StartsWith("wwwroot/")) score += 25;
            if (nameLow.Contains("agentcontroller")) score -= 200;
            if (nameLow == "filehints") score -= 200;
            if (pathLow.EndsWith(".min.js") || pathLow.EndsWith(".min.css")) score -= 300;
            if (ext is ".dll" or ".exe" or ".pdb" or ".nupkg" or ".lock") score -= 1000;
            return (file: f, score);
        })
        .Where(x => x.score > 0).OrderByDescending(x => x.score).Take(50).Select(x => x.file).ToList();

        if (scored.Count == 0)
            scored = allFiles.Where(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                var ext = Path.GetExtension(f).ToLowerInvariant();
                return name is "index" or "app" or "main" or "program" or "startup" or "styles" or "global" or "layout"
                    && ext is ".html" or ".js" or ".ts" or ".css" or ".cs";
            }).Take(10).ToList();

        return scored;
    }

    private static List<string> ExtractMeaningfulKeywords(string lower) =>
        AgentUtilities.ExtractMeaningfulKeywords(lower);

    private async Task<List<string>> SelectRelevantFilesWithLlm(
        string prompt, List<string> candidates, bool emitSse, CancellationToken ct)
    {
        if (candidates.Count == 0) return new List<string>();
        const string system =
            "You are a file relevance selector. Given a task and files, pick 3-7 most likely to need editing. " +
            "Output ONLY valid JSON, no markdown: {\"files\": [\"path1\", \"path2\"]}";
        var user = $"Task: {prompt}\n\nFiles:\n{string.Join("\n", candidates)}\n\nSelect 3-7 max.";
        var (raw, _, err) = await CallLlmRaw(system, user, ct, TimeSpan.FromSeconds(25));
        if (string.IsNullOrWhiteSpace(raw))
            return candidates.Take(6).ToList();
        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (m.Success) cleaned = m.Groups[1].Value.Trim();
            }
            var s = cleaned.IndexOf('{'); var e = cleaned.LastIndexOf('}');
            if (s >= 0 && e > s) cleaned = cleaned[s..(e + 1)];
            using var doc = JsonDocument.Parse(cleaned);
            if (doc.RootElement.TryGetProperty("files", out var filesEl) &&
                filesEl.ValueKind == JsonValueKind.Array)
            {
                var selected = filesEl.EnumerateArray()
                    .Select(el => el.GetString()?.Replace('\\', '/') ?? "")
                    .Where(f => !string.IsNullOrWhiteSpace(f) &&
                                candidates.Any(c => string.Equals(c, f, StringComparison.OrdinalIgnoreCase)))
                    .Take(7).ToList();
                if (selected.Count > 0) return selected;
            }
        }
        catch { }
        return candidates.Take(6).ToList();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  PLAN PARSING
    // ═══════════════════════════════════════════════════════════════════════

    public AgentPlan? ParsePlan(string jsonString)
    {
        if (string.IsNullOrWhiteSpace(jsonString)) return null;
        var cleaned = jsonString.Trim();
        if (cleaned.StartsWith("```"))
        {
            var fm = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            cleaned = fm.Success ? fm.Groups[1].Value.Trim() : cleaned.TrimStart('`');
        }
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        var truncRepaired = AgentUtilities.TryRepairTruncatedPlanJson(cleaned);
        if (truncRepaired != null)
        {
            var truncOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true };
            foreach (var candidate in AgentUtilities.GeneratePlanJsonCandidates(truncRepaired))
            {
                try { var r = JsonSerializer.Deserialize<AgentPlan>(candidate, truncOpts); if (r?.Plan?.Count > 0) return r; } catch { }
            }
        }
        var jsonBlocks = AgentUtilities.ExtractJsonBlocks(cleaned)
            .Where(LooksLikePlanJson).OrderByDescending(b => b.Length).ToList();
        if (LooksLikePlanJson(cleaned) && cleaned.StartsWith("{")) jsonBlocks.Insert(0, cleaned);
        var fb = cleaned.IndexOf('{'); var lb = cleaned.LastIndexOf('}');
        if (fb >= 0 && lb > fb) { var bc = cleaned[fb..(lb + 1)]; if (LooksLikePlanJson(bc)) jsonBlocks.Add(bc); }
        foreach (var candidate in jsonBlocks.Distinct())
        {
            foreach (var repaired in AgentUtilities.GeneratePlanJsonCandidates(candidate))
            {
                try { var result = JsonSerializer.Deserialize<AgentPlan>(repaired, opts); if (result?.Plan != null) return result; } catch { }
            }
        } 
        var arrayCandidates = new List<string> { cleaned };
        var f2 = cleaned.IndexOf('['); var l2 = cleaned.LastIndexOf(']');
        if (f2 >= 0 && l2 > f2) arrayCandidates.Add(cleaned[f2..(l2 + 1)]);
        foreach (var block in arrayCandidates.Distinct())
        {
            try
            {
                var c = block.Trim();
                if (!c.StartsWith("[")) continue;
                var steps = JsonSerializer.Deserialize<List<PlanStep>>(c, opts);
                    if (steps is { Count: > 0 }) return new AgentPlan { Summary = "Parsed array", Plan = steps, Score = 0 };
            }
            catch { }
        }
        return null;
    }

    private static bool LooksLikePlanJson(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        Regex.IsMatch(text, @"""?plan""?\s*:", RegexOptions.IgnoreCase);

    // ═══════════════════════════════════════════════════════════════════════
    //  ORCHESTRATOR
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<(List<object> allSteps, AgentPlan? plan, bool complete)> Orchestrate(
        string prompt, string projectRoot, bool emitSse, CancellationToken ct = default,
        List<string>? attachedFiles = null, bool skipContextReview = false,
        string? steeringContext = null, bool skipQualityCheck = false,
        AgentPlan? existingPlan = null, HashSet<int>? completedStepIndices = null,
        string? cardId = null)
    {
        if (!await CheckLlmConnectivity(projectRoot, emitSse, ct))
            throw new InvalidOperationException("LLM connectivity check failed.");

        var fastPlan = AgentUtilities.TryDetectSimpleIntent(prompt);
        if (fastPlan != null)
        {
            var steps = await QuickPipeline(prompt, projectRoot, emitSse, fastPlan, ct,
                cardId: cardId);
            return (steps, fastPlan, true);
        }

        // ── Resume from existing plan (skip replanning) ─────────────────────
        if (existingPlan != null && existingPlan.Plan.Count > 0)
        {
            var doneCount = completedStepIndices?.Count ?? 0;
            await EmitLog(emitSse, "info",
                $"Using existing plan — {existingPlan.Plan.Count} step(s), {doneCount} already done", ct: ct);
            if (emitSse)
                await SendSse(Response, "plan",
                    new { thinking = existingPlan.Thinking, summary = existingPlan.Summary,
                          items = existingPlan.Plan, resumed = true }, ct);

            var resumeSteps = new List<object>();
            await ExecutePlan(prompt, projectRoot, emitSse, "", existingPlan, ct, resumeSteps,
                steeringContext: steeringContext, attachedFiles: attachedFiles,
                completedStepIndices: completedStepIndices, cardId: cardId);

            return (resumeSteps, existingPlan, resumeSteps.Count > 0);
        }

        var (pipelineType, cmdScore, editScore) = AgentUtilities.ClassifyTask(prompt);
        await EmitLog(emitSse, "info",
            $"Router → {pipelineType}",
            new { CommandScore = cmdScore, EditScore = editScore }, ct: ct);

        var (allSteps, plan) = pipelineType switch
        {
            PipelineType.CommandExecution =>
                await CommandExecutionPipeline(prompt, projectRoot, emitSse, ct, steeringContext: steeringContext),
            _ => await UnifiedPipeline(prompt, projectRoot, emitSse, ct,
                attachedFiles: attachedFiles, skipContextReview: skipContextReview,
                steeringContext: steeringContext, cardId: cardId)
        };

        // ── Quality check ─────────────────────────────────────────────────
        bool complete = true;
        if (!skipQualityCheck && allSteps.Count > 0)
        {
            var hasDone = allSteps.OfType<Dictionary<string, object?>>()
                .Any(s => s.TryGetValue("type", out var t) && t?.ToString() == "done_signal");

            if (!hasDone)
            {
                var (ok, reason) = await AssessCompletion(prompt, allSteps, projectRoot, ct, plan, attachedFiles: attachedFiles);
                complete = ok;
                if (!ok)
                {
                    await EmitLog(emitSse, "warn", $"Quality check: {reason}", ct: ct);

                    // Build completedStepIndices from existing results
                    var doneIndices = new HashSet<int>();
                    for (var i = 0; i < (plan?.Plan?.Count ?? 0); i++)
                    {
                        var step = plan!.Plan[i];
                        var result = allSteps.OfType<Dictionary<string, object?>>()
                            .LastOrDefault(s => string.Equals(s.GetValueOrDefault("path")?.ToString(), step.File, StringComparison.OrdinalIgnoreCase) &&
                                s.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created" or "skipped" &&
                                s.GetValueOrDefault("type")?.ToString() is "edit" or "create" or "rename");
                        if (result != null) doneIndices.Add(i);
                    }

                    var hasIncomplete = plan != null && doneIndices.Count < plan.Plan.Count;

                    if (hasIncomplete)
                    {
                        // Retry incomplete steps from the existing plan first
                        await EmitLog(emitSse, "info",
                            $"Replan: retrying {plan!.Plan.Count - doneIndices.Count} incomplete step(s)…", ct: ct);
                        var retryResults = new List<object>();
                        await ExecutePlan(prompt, projectRoot, emitSse, "", plan, ct, retryResults,
                            steeringContext: steeringContext, attachedFiles: attachedFiles,
                            completedStepIndices: doneIndices, cardId: cardId);
                        allSteps.AddRange(retryResults);

                        var (ok2, _) = await AssessCompletion(prompt, allSteps, projectRoot, ct, plan, attachedFiles: attachedFiles);
                        complete = ok2;
                    }

                    // Rebuild doneIndices after retry for the check below
                    if (!complete && plan?.Plan?.Count > 0)
                    {
                        for (var i = 0; i < plan.Plan.Count; i++)
                        {
                            var step = plan.Plan[i];
                            var result = allSteps.OfType<Dictionary<string, object?>>()
                                .LastOrDefault(s => string.Equals(s.GetValueOrDefault("path")?.ToString(), step.File, StringComparison.OrdinalIgnoreCase) &&
                                    s.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created" or "skipped" &&
                                    s.GetValueOrDefault("type")?.ToString() is "edit" or "create" or "rename");
                            if (result != null) doneIndices.Add(i);
                        }
                    }

                    // Only generate new steps if all original steps are done but quality check still failed
                    if (!complete && (plan?.Plan?.Count == 0 || doneIndices.Count == (plan?.Plan?.Count ?? 0)))
                    {
                        await EmitLog(emitSse, "info", "All steps done — generating additional steps…", ct: ct);
                        var newSteps = await GenerateReplanStepsAsync(prompt, allSteps, plan,
                            steeringContext, projectRoot, emitSse, ct,
                            attachedFiles: attachedFiles, qualityCheckReason: reason);
                        if (newSteps?.Count > 0)
                        {
                            plan = MergePlans(plan ?? new AgentPlan(), new AgentPlan { Plan = newSteps });
                            if (emitSse)
                                await SendSse(Response, "plan",
                                    new { thinking = plan.Thinking, summary = "Replan: added steps", items = plan.Plan }, ct);

                            // Rebuild doneIndices with merged plan and execute remaining
                            var mergedDone = new HashSet<int>();
                            for (var i = 0; i < plan.Plan.Count; i++)
                            {
                                var step = plan.Plan[i];
                                var result = allSteps.OfType<Dictionary<string, object?>>()
                                    .LastOrDefault(s => string.Equals(s.GetValueOrDefault("path")?.ToString(), step.File, StringComparison.OrdinalIgnoreCase) &&
                                        s.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created" or "skipped" &&
                                        s.GetValueOrDefault("type")?.ToString() is "edit" or "create" or "rename");
                                if (result != null) mergedDone.Add(i);
                            }

                            var newResults = new List<object>();
                            await ExecutePlan(prompt, projectRoot, emitSse, "", plan, ct, newResults,
                                steeringContext: steeringContext, attachedFiles: attachedFiles,
                                completedStepIndices: mergedDone, cardId: cardId);
                            allSteps.AddRange(newResults);

                            var (ok3, _) = await AssessCompletion(prompt, allSteps, projectRoot, ct, plan, attachedFiles: attachedFiles);
                            complete = ok3;
                        }
                        else
                        {
                            await EmitLog(emitSse, "warn", "No additional steps needed — stopping.", ct: ct);
                            complete = plan?.Plan?.All(p =>
                            {
                                var result = allSteps.OfType<Dictionary<string, object?>>()
                                    .LastOrDefault(s => string.Equals(s.GetValueOrDefault("path")?.ToString(), p.File, StringComparison.OrdinalIgnoreCase) &&
                                        s.GetValueOrDefault("type")?.ToString() is "edit" or "create" or "rename");
                                return result != null && result.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created" or "skipped";
                            }) ?? true;
                        }
                    }
                }
                else
                {
                    await EmitLog(emitSse, "success", "Quality check passed.", ct: ct);
                }
            }
        }

        // ── Build check ───────────────────────────────────────────────────
        if (allSteps.Count > 0)
        {
            var cfg = await _configFile.LoadConfigAsync();
            var cmds = ParseBuildCommands(cfg.buildCommands);
            if (cmds.Count > 0)
            {
                if (emitSse)
                    await SendSse(Response, "phase",
                        new { phase = "build", message = $"Running {cmds.Count} build command(s)" }, ct);
                foreach (var cmd in cmds)
                    await RunSmartBuildCheck(projectRoot, cmd, emitSse, ct);
            }
        }

        return (allSteps, plan, complete);
    }

    private static string BuildFailedEditHistory(List<object> allSteps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Failures from previous execution:");
        var failures = allSteps.OfType<Dictionary<string, object?>>()
            .Where(s => s.TryGetValue("type", out var t) && t?.ToString() == "edit" &&
                        (!s.TryGetValue("status", out var st) || st?.ToString() != "done"))
            .Take(8).ToList();
        if (failures.Count == 0) { sb.AppendLine("- No failed edits."); return sb.ToString(); }
        foreach (var f in failures)
        {
            sb.AppendLine($"- {f.GetValueOrDefault("path")}: {f.GetValueOrDefault("error") ?? f.GetValueOrDefault("status")}");
            if (f.TryGetValue("snippet", out var sn) && sn != null) sb.AppendLine($"  Nearby: {sn}");
        }
        return sb.ToString();
    }

    private static string PreviewForPrompt(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars] + "\n[truncated]";

    private static List<string> ParseBuildCommands(string buildCommands)
    {
        if (string.IsNullOrWhiteSpace(buildCommands)) return new List<string>();
        try { var arr = JsonSerializer.Deserialize<List<string>>(buildCommands); if (arr?.Count > 0) return arr.Where(c => !string.IsNullOrWhiteSpace(c)).ToList(); }
        catch { }
        return new List<string> { buildCommands.Trim() };
    }

    private static string BuildReplanPrompt(string originalPrompt, List<string> history, string? steeringContext = null,
        AgentPlan? existingPlan = null, List<object>? executedSteps = null,
        string qualityCheckReason = "", string fileContents = "")
    {
        var sb = new StringBuilder();
        sb.AppendLine("Previous plan did not fully complete. You must ONLY plan the FEWEST new steps needed.");
        sb.AppendLine("IMPORTANT: Only plan steps that address specific failures below. Do NOT repeat existing steps.");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(steeringContext)) { sb.AppendLine("## Steering"); sb.AppendLine(steeringContext); sb.AppendLine(); }

        // Show what was already planned and their results
        if (existingPlan?.Plan?.Count > 0)
        {
            sb.AppendLine("## Existing plan with results");
            foreach (var step in existingPlan.Plan)
            {
                // Look up the result for this step
                string? status = null;
                if (executedSteps != null)
                {
                    var result = executedSteps.OfType<Dictionary<string, object?>>()
                        .LastOrDefault(s =>
                            string.Equals(s.GetValueOrDefault("path")?.ToString(), step.File, StringComparison.OrdinalIgnoreCase));
                    if (result != null)
                        status = result.GetValueOrDefault("status")?.ToString();
                }
                var tag = status switch
                {
                    "done" or "modified" or "created" => "✓ DONE",
                    "skipped" => "○ SKIPPED",
                    "error" => "✗ FAILED",
                    _ => "… PENDING"
                };
                sb.AppendLine($"  {tag} {step.File}: {step.Change}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Original task"); sb.AppendLine(originalPrompt); sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(qualityCheckReason))
        {
            sb.AppendLine("## Quality check assessment");
            sb.AppendLine(qualityCheckReason);
            sb.AppendLine();
        }

        sb.AppendLine("## What went wrong");
        foreach (var h in history) sb.AppendLine(h);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(fileContents))
        {
            sb.AppendLine("## Current file contents");
            sb.Append(fileContents);
            sb.AppendLine();
        }

        sb.AppendLine("Add at most 2-3 new steps to fix ONLY the issues identified. If everything is done, return an EMPTY plan with no steps.");
        return sb.ToString();
    }

    /// <summary>
    /// Lightweight replan: asks the LLM for only the additional PlanSteps needed,
    /// without running discovery or full planning again.
    /// </summary>
    private async Task<List<PlanStep>?> GenerateReplanStepsAsync(
        string originalPrompt, List<object> executedSteps, AgentPlan? existingPlan,
        string? steeringContext, string projectRoot, bool emitSse, CancellationToken ct,
        List<string>? attachedFiles = null, string qualityCheckReason = "")
    {
        var failHist = BuildFailedEditHistory(executedSteps);

        // Read current content of all files that were modified or attached, for richer context
        var fileContents = new StringBuilder();
        var pathsToRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in executedSteps.OfType<Dictionary<string, object?>>())
        {
            var p = step.GetValueOrDefault("path")?.ToString();
            if (!string.IsNullOrWhiteSpace(p)) pathsToRead.Add(p.Replace('\\', '/'));
        }
        if (attachedFiles != null)
        {
            foreach (var f in attachedFiles) pathsToRead.Add(f.Replace('\\', '/'));
        }
        foreach (var relPath in pathsToRead.Take(8))
        {
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath));
            if (!System.IO.File.Exists(fullPath)) continue;
            var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
            fileContents.AppendLine($"### {relPath}\n```\n{content}\n```\n");
        }

        var replanPrompt = BuildReplanPrompt(originalPrompt, new List<string> { failHist },
            steeringContext, existingPlan, executedSteps, qualityCheckReason, fileContents.ToString());

        var (raw, _, llmError) = await CallLlmRaw(
            "You are a plan-fixer. Output ONLY valid JSON with a 'plan' array. Example: {\"plan\": [{\"file\": \"path/to/file.js\", \"change\": \"describe the change\", \"priority\": 1}]}. Max 3 steps. Empty array if all done.",
            replanPrompt, ct, TimeSpan.FromSeconds(30));

        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```")) { var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) cleaned = m.Groups[1].Value.Trim(); }
            var s2 = cleaned.IndexOf('{'); var e2 = cleaned.LastIndexOf('}');
            if (s2 >= 0 && e2 > s2) cleaned = cleaned[s2..(e2 + 1)];
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            if (!root.TryGetProperty("plan", out var planEl) || planEl.ValueKind != JsonValueKind.Array)
                return null;

            var steps = new List<PlanStep>();
            foreach (var item in planEl.EnumerateArray())
            {
                var file = item.TryGetProperty("file", out var f) ? f.GetString() : null;
                var change = item.TryGetProperty("change", out var c) ? c.GetString() : null;
                var priority = item.TryGetProperty("priority", out var p) ? p.GetInt32() : 1;
                if (!string.IsNullOrWhiteSpace(file) && !string.IsNullOrWhiteSpace(change))
                    steps.Add(new PlanStep { File = file, Change = change, Priority = priority });
            }
            return steps.Count > 0 ? steps : null;
        }
        catch
        {
            await EmitLog(emitSse, "warn", "Failed to parse replan steps from LLM response", ct: ct);
            return null;
        }
    }

    private async Task<List<object>> QuickPipeline(
        string prompt, string projectRoot, bool emitSse, AgentPlan fastPlan, CancellationToken ct,
        string? cardId = null)
    {
        await EmitLog(emitSse, "info", $"Fast-path → {fastPlan.Summary}", ct: ct);
        if (emitSse)
            await SendSse(Response, "plan",
                new { thinking = fastPlan.Thinking, summary = fastPlan.Summary, items = fastPlan.Plan }, ct);
        var allResults = new List<object>();
        await ExecutePlan(prompt, projectRoot, emitSse, "", fastPlan, ct, allResults,
            cardId: cardId);
        return allResults;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  UNIFIED PIPELINE  (discover → plan → execute)
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<(List<object> steps, AgentPlan plan)> UnifiedPipeline(
        string prompt, string projectRoot, bool emitSse, CancellationToken ct,
        List<string>? attachedFiles = null,
        bool skipContextReview = false,
        string? steeringContext = null,
        string? cardId = null)
    {
        var allSteps = new List<object>();

        // Phase 1: Discover
        await EmitLog(emitSse, "info", "Phase 1 — DISCOVER", ct: ct);
        var (discoveryContext, ds) = await RunBootstrapDiscovery(prompt, projectRoot, emitSse, attachedFiles, ct);
        allSteps.AddRange(ds);

        // Context review (let user trim files before planning)
        if (emitSse && !skipContextReview)
            discoveryContext = await RunContextReview(ds, discoveryContext, allSteps, ct);

        // Phase 2: Plan
        await EmitLog(emitSse, "info", "Phase 2 — PLAN", ct: ct);
        if (emitSse)
            await SendSse(Response, "phase",
                new { phase = "plan", message = "Planning...", contextSize = discoveryContext.Length }, ct);

        var plan = await AnalyzePromptAndPlanCodeChanges(
            prompt, discoveryContext, projectRoot, emitSse, ct, steeringContext);

        if (plan == null || plan.Plan.Count == 0)
        {
            await EmitLog(emitSse, "error", "Plan phase produced no items.", ct: ct);
            throw new InvalidOperationException("LLM returned an empty or unparseable plan.");
        }

        if (emitSse && !string.IsNullOrWhiteSpace(plan.Thinking))
            await SendSse(Response, "thinking", new { text = plan.Thinking }, ct);

        await EmitLog(emitSse, "info",
            $"Plan: {plan.Plan.Count} step(s) — {string.Join(", ", plan.Plan.Select(p => p.File))}",
            new { plan }, ct: ct);

        if (emitSse)
            await SendSse(Response, "plan",
                new { thinking = plan.Thinking, summary = plan.Summary, items = plan.Plan }, ct);

        allSteps.Add(new Dictionary<string, object?>
        {
            ["index"] = allSteps.Count,
            ["type"] = "plan",
            ["status"] = "complete",
            ["description"] = "Plan complete"
        });

        // Phase 2.5: Explore if needed
        var exploreSteps = plan.Plan
            .Where(p => p.File.Equals("_explore", StringComparison.OrdinalIgnoreCase)).ToList();
        if (exploreSteps.Count > 0)
        {
            await EmitLog(emitSse, "info",
                $"Phase 2.5 — EXPLORE: {exploreSteps.Count} target(s)…", ct: ct);
            discoveryContext = await ExplorationPipeline(
                exploreSteps, discoveryContext, projectRoot, emitSse, ct);
            var replan = await AnalyzePromptAndPlanCodeChanges(
                prompt, discoveryContext, projectRoot, emitSse, ct, steeringContext);
            if (replan == null) throw new InvalidOperationException("Re-plan after exploration returned empty.");
            plan = MergePlans(plan, replan);
            if (emitSse)
                await SendSse(Response, "plan",
                    new { thinking = plan.Thinking, summary = plan.Summary, items = plan.Plan }, ct);
        }

        // Phase 3: Execute
        await EmitLog(emitSse, "info", "Phase 3 — EXECUTE", ct: ct);
        if (emitSse)
            await SendSse(Response, "phase", new { phase = "execute", message = "Executing plan…" }, ct);

        await ExecutePlan(prompt, projectRoot, emitSse, discoveryContext, plan, ct, allSteps,
            steeringContext: steeringContext, attachedFiles: attachedFiles,
            cardId: cardId);

        return (allSteps, plan);
    }

    private async Task<string> RunContextReview(
        List<object> ds, string discoveryContext, List<object> allSteps, CancellationToken ct)
    {
        var readFiles = ds.OfType<Dictionary<string, object?>>()
            .Where(s => s.TryGetValue("type", out var t) && t?.ToString() == "read")
            .Select(s => s.GetValueOrDefault("path")?.ToString())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (readFiles.Count == 0) return discoveryContext;

        var reviewId = Guid.NewGuid().ToString();
        var review = new PendingContextReview
        {
            Id = reviewId,
            Files = readFiles.Where(f => f != null).ToList()!,
            CreatedUtc = DateTime.UtcNow,
            Answer = new TaskCompletionSource<List<string>>()
        };
        _pendingContextReviews[reviewId] = review;

        await SendSse(Response, "context-review", new
        {
            id = reviewId,
            files = readFiles.Select(f => new { path = f }).ToList(),
            contextSize = discoveryContext.Length
        }, ct);

        try
        {
            var confirmedFiles = await review.Answer.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            var confirmedSet = new HashSet<string>(confirmedFiles, StringComparer.OrdinalIgnoreCase);
            if (confirmedFiles.Count < readFiles.Count)
            {
                var filtered = ds.Where(item =>
                {
                    if (item is not Dictionary<string, object?> r) return true;
                    var type = r.TryGetValue("type", out var t) ? t?.ToString() : "";
                    if (type != "read") return true;
                    var p = r.GetValueOrDefault("path")?.ToString();
                    return !string.IsNullOrWhiteSpace(p) && confirmedSet.Contains(p);
                }).ToList();
                allSteps.Clear(); allSteps.AddRange(filtered);
                return AgentUtilities.BuildDiscoveryTextFromSteps(filtered);
            }
        }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }
        finally { _pendingContextReviews.TryRemove(reviewId, out _); }

        return discoveryContext;
    }

    private async Task<string> ExplorationPipeline(
        List<PlanStep> exploreSteps, string discoveryContext,
        string projectRoot, bool emitSse, CancellationToken ct)
    {
        var enriched = new StringBuilder(discoveryContext);
        enriched.AppendLine();
        foreach (var step in exploreSteps)
        {
            var target = step.Change?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(target)) continue;
            await EmitLog(emitSse, "info", $"Exploring: {target}", ct: ct);
            if (target.Contains('*') || target.Contains('?'))
            {
                var sep = Path.DirectorySeparatorChar;
                var pattern = target.Replace('/', sep);
                var dir = Path.GetDirectoryName(pattern) ?? ".";
                var searchDir = Path.GetFullPath(Path.Combine(projectRoot, dir));
                if (!Directory.Exists(searchDir)) continue;
                foreach (var match in Directory.EnumerateFiles(searchDir, Path.GetFileName(pattern), SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/')).Take(10))
                {
                    var fp = Path.GetFullPath(Path.Combine(projectRoot, match.Replace('/', sep)));
                    if (!System.IO.File.Exists(fp)) continue;
                    var content = await System.IO.File.ReadAllTextAsync(fp, Encoding.UTF8, ct);
                    enriched.AppendLine($"### {match}\n```\n{content}\n```\n");
                }
            }
            else
            {
                var fp = Path.GetFullPath(Path.Combine(projectRoot, target.Replace('/', Path.DirectorySeparatorChar)));
                if (System.IO.File.Exists(fp) && AgentUtilities.IsPathUnderRoot(fp, projectRoot))
                {
                    var content = await System.IO.File.ReadAllTextAsync(fp, Encoding.UTF8, ct);
                    enriched.AppendLine($"### {target}\n```\n{content}\n```\n");
                }
            }
        }
        return enriched.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  EXECUTE PLAN  — progressive checklist, per-step resolve+retry
    // ═══════════════════════════════════════════════════════════════════════

    private async Task ExecutePlan(
        string prompt, string projectRoot, bool emitSse, string discoveryContext,
        AgentPlan plan, CancellationToken ct, List<object> allResults,
        string? steeringContext = null, List<string>? attachedFiles = null,
        HashSet<int>? completedStepIndices = null, string? cardId = null)
    {
        var stepIndex = 0;
        var planItems = plan.Plan.ToList();
        var webCtx = new StringBuilder();
        var checkpointCount = 0;
        const int MaxCheckpoints = 3;
        completedStepIndices ??= new HashSet<int>();

        for (var itemIdx = 0; itemIdx < planItems.Count; itemIdx++)
        {
            ct.ThrowIfCancellationRequested();

            var item = planItems[itemIdx];

            // Skip steps already completed in a previous run
            if (completedStepIndices.Contains(itemIdx))
            {
                if (emitSse)
                    await SendSse(Response, "step", new
                    {
                        index = stepIndex,
                        type = "plan",
                        description = item.Change,
                        path = item.File,
                        status = "done",
                        skipped = true,
                        planItemIndex = itemIdx,
                        message = "Already completed in a previous run"
                    }, ct);
                stepIndex++;
                continue;
            }
            var planFile = item.File;
            var changeDesc = item.Change;

            // ── Special markers ───────────────────────────────────────────

            if (planFile.Equals("_done", StringComparison.OrdinalIgnoreCase))
            {
                await EmitLog(emitSse, "success", $"Task self-reported complete: {changeDesc}", ct: ct);
                if (emitSse) await SendSse(Response, "done_signal", new { message = changeDesc }, ct);
                allResults.Add(new Dictionary<string, object?> { ["type"] = "done_signal", ["status"] = "done", ["output"] = changeDesc });
                return;
            }

            if (planFile.Equals("_checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                if (++checkpointCount > MaxCheckpoints) { await EmitLog(emitSse, "warn", "Max checkpoints reached", ct: ct); continue; }
                await EmitLog(emitSse, "info", $"Checkpoint {checkpointCount}/{MaxCheckpoints}: {changeDesc}", ct: ct);
                if (emitSse) await SendSse(Response, "phase", new { phase = "checkpoint", message = $"Checkpoint {checkpointCount}" }, ct);
                allResults.Add(new Dictionary<string, object?> { ["type"] = "checkpoint", ["status"] = "done", ["output"] = changeDesc });
                var remaining = planItems.Skip(itemIdx + 1).ToList();
                if (remaining.Count > 0)
                {
                    var newSteps = await CheckpointReplan(prompt, discoveryContext, remaining, allResults, projectRoot, emitSse, ct, steeringContext);
                    if (newSteps?.Count > 0)
                    {
                        planItems = MergePlanSteps(planItems, newSteps);
                        if (emitSse) await SendSse(Response, "plan", new { summary = $"Phase {checkpointCount + 1}", items = planItems }, ct);
                    }
                }
                continue;
            }

            if (planFile.Equals("_continue", StringComparison.OrdinalIgnoreCase))
            {
                await EmitLog(emitSse, "info", $"Continuation: {changeDesc}", ct: ct);
                allResults.Add(new Dictionary<string, object?> { ["type"] = "continue_signal", ["status"] = "done", ["output"] = changeDesc });
                continue;
            }

            if (planFile.Equals("_rename", StringComparison.OrdinalIgnoreCase) ||
                planFile.Equals("_rename_file", StringComparison.OrdinalIgnoreCase))
            {
                stepIndex = await ExecuteRenameFromChange(changeDesc, projectRoot, emitSse, ct, allResults, stepIndex);
                continue;
            }

            if (planFile.Equals("_delete_file", StringComparison.OrdinalIgnoreCase))
            {
                var target = changeDesc.Trim().Trim('"', '\'').Replace('\\', '/');
                var fullPath = Path.GetFullPath(Path.Combine(projectRoot, target.Replace('/', Path.DirectorySeparatorChar)));
                if (AgentUtilities.IsPathUnderRoot(fullPath, projectRoot) && System.IO.File.Exists(fullPath))
                {
                    System.IO.File.Delete(fullPath);
                    await EmitLog(emitSse, "success", $"Deleted {target}", ct: ct);
                    allResults.Add(new Dictionary<string, object?> { ["type"] = "rename", ["status"] = "done", ["path"] = target, ["editAction"] = "deleted" });
                }
                else await EmitLog(emitSse, "warn", $"Delete target not found: {target}", ct: ct);
                continue;
            }

            if (planFile.Equals("_git", StringComparison.OrdinalIgnoreCase))
            {
                stepIndex = await ExecuteGitStep(changeDesc, projectRoot, emitSse, ct, allResults, stepIndex);
                continue;
            }

            if (planFile.Equals("_show", StringComparison.OrdinalIgnoreCase) ||
                planFile.Equals("_display", StringComparison.OrdinalIgnoreCase))
            {
                var text = changeDesc.Trim().Trim('`', '"', '\'');
                await EmitLog(emitSse, "info", text, ct: ct);
                if (emitSse) await SendSse(Response, "show", new { text }, ct);
                allResults.Add(new Dictionary<string, object?> { ["status"] = "done", ["type"] = "show", ["output"] = text });
                continue;
            }

            if (planFile.Equals("_create_file", StringComparison.OrdinalIgnoreCase))
            {
                await EmitLog(emitSse, "info", $"Creating file: {changeDesc}", ct: ct);
                var cr = await HandleCreateFile(changeDesc, projectRoot, prompt, discoveryContext, stepIndex, emitSse, ct, null, attachedFiles);
                stepIndex += cr.stepsCount; allResults.AddRange(cr.results);
                continue;
            }

            if (planFile.Equals("_ping", StringComparison.OrdinalIgnoreCase))
            { stepIndex = await ExecutePingStep(changeDesc, projectRoot, emitSse, ct, allResults, stepIndex); continue; }

            if (planFile.Equals("_package_install", StringComparison.OrdinalIgnoreCase))
            { stepIndex = await ExecutePackageInstallStep(changeDesc, projectRoot, emitSse, ct, allResults, stepIndex); continue; }

            if (planFile.Equals("_command", StringComparison.OrdinalIgnoreCase))
            {
                var cmd = changeDesc.Trim().Trim('`', '"', '\'');
                if (!string.IsNullOrWhiteSpace(cmd))
                {
                    await EmitLog(emitSse, "info", $"Command: {cmd}", ct: ct);
                    _terminal.Start();
                    var cs = new AgentStep { Index = 0, Type = "command", Command = cmd, Description = cmd };
                    var cr = await ExecuteSteps(new List<AgentStep> { cs }, projectRoot, stepIndex, emitSse, ct);
                    stepIndex += cr.Count; allResults.AddRange(cr);
                }
                continue;
            }

            if (planFile.Equals("_web_search", StringComparison.OrdinalIgnoreCase) ||
                planFile.Equals("_web_fetch", StringComparison.OrdinalIgnoreCase))
            {
                (stepIndex, discoveryContext) = await ExecuteWebPlanStep(planFile, changeDesc, prompt, projectRoot, emitSse, ct,
                    allResults, planItems, itemIdx, stepIndex, discoveryContext, webCtx);
                continue;
            }

            if (planFile.Equals("_move_file", StringComparison.OrdinalIgnoreCase))
            {
                var dst = AgentUtilities.ExtractTargetPath(changeDesc, planFile, projectRoot);
                if (dst != null)
                {
                    var rs = new AgentStep { Index = 0, Type = "rename", Path = planFile, ToPath = dst, Description = $"Move {planFile} → {dst}" };
                    var rr = await ExecuteSteps(new List<AgentStep> { rs }, projectRoot, stepIndex, emitSse, ct);
                    stepIndex += rr.Count; allResults.AddRange(rr);
                }
                continue;
            }

            // ── File edit: resolve then apply ─────────────────────────────
            if (AgentUtilities.IsRelativePath(planFile))
            {
                // ResolveAndApplyEdit handles plan-provided oldString + unbounded LLM retries internally
                var prevCount = allResults.Count;
                stepIndex = await ResolveAndApplyEdit(item, projectRoot, emitSse, ct, allResults, stepIndex, itemIdx, cardId);

                if (allResults.Count > prevCount &&
                    allResults[^1] is Dictionary<string, object?> lastDict &&
                    lastDict.TryGetValue("status", out var st) && st?.ToString() == "error")
                {
                    await EmitLog(emitSse, "error",
                        $"✗ Step permanently failed for {planFile} — {lastDict.GetValueOrDefault("error")}", ct: ct);
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(planFile))
            {
                await EmitLog(emitSse, "warn", "Plan item with empty file — skipping", new { item }, ct: ct);
            }
        }
    }

    private async Task<int> ExecuteRenameFromChange(
        string changeDesc, string projectRoot, bool emitSse, CancellationToken ct,
        List<object> allResults, int stepIndex)
    {
        string? src = null, dst = null;
        var arrow = changeDesc.IndexOf('→');
        if (arrow > 0) { src = changeDesc[..arrow].Trim(); dst = changeDesc[(arrow + 1)..].Trim(); }
        else
        {
            var toIdx = changeDesc.LastIndexOf(" to ", StringComparison.OrdinalIgnoreCase);
            if (toIdx > 0) { src = changeDesc[..toIdx].Trim(); dst = changeDesc[(toIdx + 4)..].Trim(' ', '"', '\''); }
        }
        if (!string.IsNullOrWhiteSpace(src) && !string.IsNullOrWhiteSpace(dst))
        {
            src = src.Replace('\\', '/').Trim('/');
            dst = dst.Replace('\\', '/').TrimEnd('/');
            if (!dst.Contains('/') && src.Contains('/'))
                dst = src[..(src.LastIndexOf('/') + 1)] + dst;
            var rs = new AgentStep { Index = 0, Type = "rename", Path = src, ToPath = dst, Description = $"Rename {src} → {dst}" };
            var rr = await ExecuteSteps(new List<AgentStep> { rs }, projectRoot, stepIndex, emitSse, ct);
            stepIndex += rr.Count; allResults.AddRange(rr);
        }
        else await EmitLog(emitSse, "error", $"_rename: could not parse src/dst from: {changeDesc}", ct: ct);
        return stepIndex;
    }

    private async Task<int> ExecuteGitStep(
        string changeDesc, string projectRoot, bool emitSse, CancellationToken ct,
        List<object> allResults, int stepIndex)
    {
        var lower = (changeDesc.Trim().Trim('`', '"', '\'') + " ").ToLowerInvariant();
        string gitCmd;
        if (lower.StartsWith("commit") || lower.Contains("commit all"))
        {
            var mm = Regex.Match(changeDesc, "\"([^\"]+)\"");
            var msg = mm.Success ? mm.Groups[1].Value : $"Auto-commit {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            gitCmd = $"git add -A && git commit -m \"{msg.Replace("\"", "\\\"")}\"";
        }
        else if (lower.StartsWith("revert") || lower.Contains("discard")) gitCmd = "git checkout -- .";
        else if (lower.StartsWith("pull")) gitCmd = "git pull";
        else if (lower.StartsWith("sync") || lower.Contains("push")) gitCmd = "git pull && git push";
        else
        {
            gitCmd = changeDesc.Trim().Trim('`', '"', '\'');
            if (!gitCmd.StartsWith("git ", StringComparison.OrdinalIgnoreCase)) gitCmd = "git " + gitCmd;
        }
        await EmitLog(emitSse, "info", $"Git: {gitCmd}", ct: ct);
        _terminal.Start();
        var gs = new AgentStep { Index = 0, Type = "command", Command = gitCmd, Description = gitCmd };
        var gr = await ExecuteSteps(new List<AgentStep> { gs }, projectRoot, stepIndex, emitSse, ct);
        stepIndex += gr.Count; allResults.AddRange(gr);
        return stepIndex;
    }

    private async Task<int> ExecutePingStep(
        string changeDesc, string projectRoot, bool emitSse, CancellationToken ct,
        List<object> allResults, int stepIndex)
    {
        var pingCmd = changeDesc.Trim().Trim('`', '"', '\'');
        if (pingCmd.Contains("<llamaUrl>", StringComparison.OrdinalIgnoreCase))
        {
            var baseUrl = await GetLlamaBaseUrl();
            var uri = new Uri(baseUrl);
            pingCmd = OperatingSystem.IsWindows()
                ? $"powershell -Command \"Test-NetConnection {uri.Host} -Port {uri.Port} -WarningAction SilentlyContinue | Select-Object TcpTestSucceeded | Format-List\""
                : $"nc -zv -w 2 {uri.Host} {uri.Port} 2>&1";
        }
        await EmitLog(emitSse, "info", $"Ping: {pingCmd}", ct: ct);
        _terminal.Start();
        var cs = new AgentStep { Index = 0, Type = "command", Command = pingCmd, Description = pingCmd };
        var cr = await ExecuteSteps(new List<AgentStep> { cs }, projectRoot, stepIndex, emitSse, ct);
        stepIndex += cr.Count; allResults.AddRange(cr);
        return stepIndex;
    }

    private async Task<int> ExecutePackageInstallStep(
        string changeDesc, string projectRoot, bool emitSse, CancellationToken ct,
        List<object> allResults, int stepIndex)
    {
        var installCmd = changeDesc.Trim().Trim('`', '"', '\'');
        await EmitLog(emitSse, "info", $"Package install: {installCmd}", ct: ct);
        _terminal.Start();
        var cs = new AgentStep { Index = 0, Type = "command", Command = installCmd, Description = installCmd };
        var cr = await ExecuteSteps(new List<AgentStep> { cs }, projectRoot, stepIndex, emitSse, ct);
        stepIndex += cr.Count; allResults.AddRange(cr);
        return stepIndex;
    }

    private async Task<(int stepIndex, string discoveryContext)> ExecuteWebPlanStep(
        string planFile, string changeDesc, string prompt,
        string projectRoot, bool emitSse, CancellationToken ct,
        List<object> allResults, List<PlanStep> planItems, int itemIdx,
        int stepIndex, string discoveryContext, StringBuilder webCtx)
    {
        var isSearch = planFile.Equals("_web_search", StringComparison.OrdinalIgnoreCase);
        var query = changeDesc.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return (stepIndex, discoveryContext);
        await EmitLog(emitSse, "info", $"Web {(isSearch ? "search" : "fetch")}: {query}", ct: ct);
        var (outp, err) = isSearch ? await WebSearchAsync(query, ct) : await WebFetchAsync(query, ct);
        var curIdx = stepIndex;
        var wr = new Dictionary<string, object?>
        {
            ["index"] = curIdx,
            ["type"] = planFile,
            [isSearch ? "query" : "url"] = query,
            ["status"] = err == null ? "done" : "error",
            ["output"] = outp
        };
        allResults.Add(wr);
        if (emitSse) await SendSse(Response, "step", wr, ct);
        if (!string.IsNullOrWhiteSpace(outp) && outp.Length > 80)
            webCtx.AppendLine($"\n## Web [{query}]\n{outp}");
        var nextIsWeb = itemIdx + 1 < planItems.Count &&
            (planItems[itemIdx + 1].File.Equals("_web_search", StringComparison.OrdinalIgnoreCase) ||
             planItems[itemIdx + 1].File.Equals("_web_fetch", StringComparison.OrdinalIgnoreCase));
        if (!nextIsWeb && webCtx.Length > 0)
        {
            var remaining = planItems.Skip(itemIdx + 1).ToList();
            if (remaining.Any(r => AgentUtilities.IsRelativePath(r.File ?? "") || r.File == "_create_file"))
            {
                var uctx = discoveryContext + "\n\n" + webCtx;
                var rp = await ReplanRemainingSteps(prompt, remaining, uctx, emitSse, ct);
                if (rp?.Count > 0)
                {
                    planItems = MergePlanSteps(planItems, rp);
                    discoveryContext = uctx;
                    if (emitSse)
                        await SendSse(Response, "plan", new { summary = "Plan updated after web results", items = planItems }, ct);
                }
                webCtx.Clear();
            }
        }
        return (stepIndex + 1, discoveryContext);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  EDIT APPLICATION
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<(bool applied, string reason, int score)> ApplyEdit(
        PlanStep item, string projectRoot, bool emitSse, CancellationToken ct)
    {
        var relPath = item.File.Replace('\\', '/');
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!AgentUtilities.IsPathUnderRoot(fullPath, projectRoot)) return (false, "Path outside project root", 0);
        if (!System.IO.File.Exists(fullPath)) return (false, "File not found", 0);
        if (string.IsNullOrWhiteSpace(item.OldString)) return (false, "No oldString provided", 0);
        if ((item.OldString ?? "").Trim() == (item.NewString ?? "").Trim()) return (false, "oldString and newString identical", 3);

        var unsafeReason = GetUnsafeEditPayloadReason(item.OldString ?? "", item.NewString ?? "");
        if (unsafeReason != null) return (false, unsafeReason, 0);

        var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8);
        var (replaced, newContent, matchError, snippet) =
            TryReplaceSafe(content, item.OldString!, item.NewString ?? "");
        if (!replaced)
        {
            var reason = matchError ?? "oldString not found";
            if (!string.IsNullOrWhiteSpace(snippet)) reason += $". Nearby: {snippet}";
            return (false, reason, 1);
        }

        var (approved, verifyReason, score) =
            VerifyEdit(item.OldString!, item.NewString ?? "", content, newContent);
        if (!approved) return (false, verifyReason, score);

        await System.IO.File.WriteAllTextAsync(fullPath, newContent, Encoding.UTF8);
        await EmitLog(emitSse, "success", $"✔ Edited {relPath}", ct: ct);
        return (true, "", 10);
    }

    private static (bool approved, string reason, int score) VerifyEdit(
        string oldString, string newString, string oldContent, string newContent)
    {
        if (oldContent == newContent) return (false, "Edit produced no change", 3);

        var normOld = AgentUtilities.NormalizeLineEndings(oldString);
        var normNew = AgentUtilities.NormalizeLineEndings(newString);
        var normOldContent = AgentUtilities.NormalizeLineEndings(oldContent);
        var normNewContent = AgentUtilities.NormalizeLineEndings(newContent);

        // newString must be present in result
        if (!string.IsNullOrEmpty(normNew) &&
            !normNewContent.Contains(normNew, StringComparison.Ordinal))
        {
            // Indentation may have been adjusted — retry with leading whitespace stripped from each line
            var strippedNew = StripLineLeadingWhitespace(normNew);
            var strippedContent = StripLineLeadingWhitespace(normNewContent);
            if (!strippedContent.Contains(strippedNew, StringComparison.Ordinal))
                return (false, "newString not found after replacement", 4);
        }

        // oldString should not appear as-frequently in the result
        // (it was supposed to be replaced). Only check this for non-trivial oldStrings.
        if (!string.IsNullOrEmpty(normOld) && normOld.Length >= 10)
        {
            // Strip leading whitespace from each line for indentation-aware comparison
            var strippedOld = StripLineLeadingWhitespace(normOld);
            var strippedOldContent = StripLineLeadingWhitespace(normOldContent);
            var strippedNewContent = StripLineLeadingWhitespace(normNewContent);

            var oldCount = 0; var newCount = 0; var pos = 0;
            while ((pos = strippedOldContent.IndexOf(strippedOld, pos, StringComparison.Ordinal)) >= 0)
            { oldCount++; pos += strippedOld.Length; }
            pos = 0;
            while ((pos = strippedNewContent.IndexOf(strippedOld, pos, StringComparison.Ordinal)) >= 0)
            { newCount++; pos += strippedOld.Length; }
            // If oldString appears the same number of times, the edit was a no-op
            if (oldCount > 0 && newCount >= oldCount)
                return (false, "oldString still fully present after replacement — edit hit wrong location", 4);
        }

        // Verify the replacement actually changed the target area:
        // oldString and newString should be meaningfully different
        if (string.Equals(normOld.Trim(), normNew.Trim(), StringComparison.Ordinal))
            return (false, "oldString and newString are identical after normalization", 3);

        return (true, "Programmatic check passed", 10);
    }

    private static string StripLineLeadingWhitespace(string s)
    {
        var lines = s.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimStart();
        return string.Join("\n", lines);
    }

    private async Task<bool> ApplyEditWithRetry(
        PlanStep item, string projectRoot, bool emitSse, CancellationToken ct)
    {
        var relPath = item.File.Replace('\\', '/');
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!System.IO.File.Exists(fullPath)) return false;

        var history = new List<(string oldString, string newString, int score, string reason)>();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                var freshContent = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8);
                var corrected = await CorrectEdit(
                    "", relPath, freshContent, item.Change, history, attempt, emitSse, ct);
                if (corrected == null) break;
                if ((corrected.Value.oldString ?? "").Trim() == (corrected.Value.newString ?? "").Trim())
                {
                    await EmitLog(emitSse, "warn",
                        $"CorrectEdit returned identical strings for {relPath} — stopping retries", ct: ct);
                    break;
                }
                item.OldString = corrected.Value.oldString!;
                item.NewString = corrected.Value.newString!;
            }

            var (applied, reason, score) = await ApplyEdit(item, projectRoot, emitSse, ct);
            if (applied) return true;

            history.Add((item.OldString!, item.NewString ?? "", score, reason));
            if (attempt < 2)
                await EmitLog(emitSse, "warn",
                    $"Attempt {attempt + 1}/3 failed for {relPath}: {reason}", ct: ct);
            else
                await EmitLog(emitSse, "error",
                    $"All 3 attempts failed for {relPath}: {reason}", ct: ct);
        }
        return false;
    }

    private async Task<(string oldString, string newString)?> CorrectEdit(
        string originalPrompt, string relPath, string fileContent, string changeDesc,
        List<(string oldString, string newString, int score, string reason)> history,
        int attempt, bool emitSse, CancellationToken ct)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(originalPrompt)) { sb.AppendLine("## Original task"); sb.AppendLine(originalPrompt); sb.AppendLine(); }
        if (!string.IsNullOrWhiteSpace(changeDesc)) { sb.AppendLine("## Planned change"); sb.AppendLine(changeDesc); sb.AppendLine(); }
        sb.AppendLine($"## File: {relPath}");
        sb.AppendLine();
        sb.AppendLine(BuildEditCorrectionContext(fileContent, changeDesc, history));
        sb.AppendLine();
        sb.AppendLine("### Previous failed attempts:");
        for (var i = 0; i < history.Count; i++)
        {
            var h = history[i];
            sb.AppendLine($"--- Attempt {i + 1} — Score {h.score}/10 ---");
            sb.AppendLine($"Reason: {h.reason}");
            sb.AppendLine($"oldString:\n```\n{RemoveUnsafeEditMarkersForPrompt(h.oldString)}\n```");
            sb.AppendLine($"newString:\n```\n{RemoveUnsafeEditMarkersForPrompt(h.newString)}\n```");
        }
        sb.AppendLine("Produce corrected oldString/newString.");
        sb.AppendLine("- oldString must exist verbatim in the file");
        sb.AppendLine("- Match exact whitespace and indentation");

        const string system = @"You are an edit-correction agent. Output ONLY valid JSON:
{""oldString"": ""exact code from file"", ""newString"": ""replacement code""}
Rules: oldString MUST exist verbatim. Escape newlines as \n. Never return identical strings.";

        var (raw, _, err) = await CallLlmRaw(system, sb.ToString(), ct, TimeSpan.FromSeconds(30));
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            var (os, ns, parseError) = AgentUtilities.ExtractEditFromCodeGen(raw);
            if (string.IsNullOrWhiteSpace(os)) return null;
            return (os, ns ?? "");
        }
        catch
        {
            await EmitLog(emitSse, "warn", $"Failed to parse correction for {relPath}", ct: ct);
            return null;
        }
    }

    private static string BuildEditCorrectionContext(
        string fileContent, string changeDesc,
        List<(string oldString, string newString, int score, string reason)> history)
    {
        var normalized = AgentUtilities.NormalizeLineEndings(fileContent);
        var sb = new StringBuilder();
        if (normalized.Length <= MaxFileContextChars)
        {
            sb.AppendLine("### Current file content\n```");
            sb.AppendLine(normalized);
            sb.AppendLine("```");
            return sb.ToString();
        }
        var tokens = ExtractCorrectionTokens(changeDesc, history);
        var lines = normalized.Split('\n');
        var hitLines = new SortedSet<int>();
        if (tokens.Count > 0)
            for (var i = 0; i < lines.Length; i++)
                if (tokens.Any(t => lines[i].Contains(t, StringComparison.OrdinalIgnoreCase)))
                    hitLines.Add(i);
        var windows = new List<(int start, int end)>();
        foreach (var hit in hitLines.Take(12))
        {
            var start = Math.Max(0, hit - 30); var end = Math.Min(lines.Length - 1, hit + 30);
            if (windows.Count > 0 && start <= windows[^1].end + 5) windows[^1] = (windows[^1].start, Math.Max(windows[^1].end, end));
            else windows.Add((start, end));
        }
        if (windows.Count == 0) { windows.Add((0, Math.Min(lines.Length - 1, 180))); }
        sb.AppendLine("### Current file excerpts");
        var usedChars = 0;
        foreach (var w in windows)
        {
            var excerpt = string.Join('\n', lines.Skip(w.start).Take(w.end - w.start + 1));
            if (usedChars + excerpt.Length > MaxFileContextChars) excerpt = excerpt[..Math.Max(0, MaxFileContextChars - usedChars)];
            if (string.IsNullOrWhiteSpace(excerpt)) break;
            sb.AppendLine($"Lines {w.start + 1}-{w.end + 1}:\n```\n{excerpt}\n```");
            usedChars += excerpt.Length;
            if (usedChars >= MaxFileContextChars) break;
        }
        return sb.ToString();
    }

    private static HashSet<string> ExtractCorrectionTokens(
        string changeDesc,
        List<(string oldString, string newString, int score, string reason)> history)
    {
        var common = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "string","public","private","protected","internal","static","readonly","return",
            "await","async","using","namespace","class","void","var","new","null","true","false",
            "this","base","file","change","oldString","newString","reason","score"
        };
        var text = new StringBuilder(changeDesc ?? "");
        foreach (var h in history) { text.AppendLine(h.oldString); text.AppendLine(h.newString); }
        return Regex.Matches(text.ToString(), @"\b[A-Za-z_][A-Za-z0-9_]{2,}\b")
            .Select(m => m.Value).Where(t => !common.Contains(t))
            .OrderByDescending(t => t.Length).Take(40)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<List<PlanStep>?> ReplanRemainingSteps(
        string originalPrompt, List<PlanStep> remaining,
        string updatedContext, bool emitSse, CancellationToken ct)
    {
        if (remaining.Count == 0) return null;
        var sb = new StringBuilder();
        sb.AppendLine("Revise remaining steps given web results. Keep ALL existing steps and add any new ones needed. Original task: " + originalPrompt);
        foreach (var s in remaining) sb.AppendLine($"  {s.File}: {s.Change}");
        sb.AppendLine(updatedContext);
        const string sys = "Revise remaining execution steps. NEVER remove existing steps. Output ONLY JSON: {\"plan\":[{\"file\":\"...\",\"change\":\"...\",\"priority\":1}]}";
        var (raw, _, _) = await CallLlmRaw(sys, sb.ToString(), ct, _infiniteTimeout, maxTokens: 2048);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```")) { var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) cleaned = m.Groups[1].Value.Trim(); }
        var parsed = ParsePlan(cleaned);
        return parsed?.Plan?.Count > 0 ? parsed.Plan : null;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  COMMAND EXECUTION PIPELINE
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<(List<object> steps, AgentPlan? plan)> CommandExecutionPipeline(
        string prompt, string projectRoot, bool emitSse, CancellationToken ct,
        string? steeringContext = null)
    {
        var steps = new List<object>();
        var fastPlan = AgentUtilities.TryDetectSimpleIntent(prompt);
        if (fastPlan != null)
        {
            await EmitLog(emitSse, "info", $"CommandExecution (fast): {fastPlan.Plan.Count} step(s)", ct: ct);
            if (emitSse) await SendSse(Response, "plan", new { thinking = fastPlan.Thinking, summary = fastPlan.Summary, items = fastPlan.Plan }, ct);
            await ExecutePlan(prompt, projectRoot, emitSse, "", fastPlan, ct, steps);
            return (steps, fastPlan);
        }

        await EmitLog(emitSse, "info", "CommandExecution (agentic): LLM has terminal control", ct: ct);
        _terminal.Start();

        var isWindows = OperatingSystem.IsWindows();
        var shellName = isWindows ? "PowerShell" : "Bash";
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        var conversation = new StringBuilder();
        conversation.AppendLine("You are a terminal automation agent. You have full terminal access.");
        conversation.AppendLine($"You are running on {shellName} ({Environment.OSVersion}).");
        conversation.AppendLine("Output ONLY valid JSON. Options:");
        conversation.AppendLine("  {\"cmd\": \"the full command\"}");
        conversation.AppendLine("  {\"web_search\": \"query\"}");
        conversation.AppendLine("  {\"web_fetch\": \"url\"}");
        conversation.AppendLine("  {\"message\": \"answer for user\"}");
        conversation.AppendLine("  {\"done\": true, \"summary\": \"what was accomplished\"}");
        conversation.AppendLine($"Desktop: {desktopPath}");
        conversation.AppendLine("NEVER use mkdir for files — use New-Item -ItemType File -Path \"<path>\" -Force");
        conversation.AppendLine("NEVER use cd/Set-Location — use absolute paths");
        if (!string.IsNullOrWhiteSpace(steeringContext)) { conversation.AppendLine("### Steering ###"); conversation.AppendLine(steeringContext); }
        conversation.AppendLine($"Task: {prompt}");

        const int maxIter = MAX_COMMAND_ITERATIONS;
        var stepIndex = 0; string? summary = null;
        var usedSearchQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < maxIter; i++)
        {
            ct.ThrowIfCancellationRequested();
            AgentUtilities.CompactConversation(conversation);

            var (raw, _, err) = await CallLlmRaw(
                "You are a terminal agent. Output only JSON.",
                conversation.ToString(), ct, TimeSpan.FromSeconds(30));

            if (string.IsNullOrWhiteSpace(raw)) { summary ??= "Completed with issues"; break; }

            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```")) { var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) cleaned = m.Groups[1].Value.Trim(); }

            var jsonOpts = new JsonDocumentOptions { AllowTrailingCommas = true };
            string? jsonToParse = null;
            var candidates = new List<string> { cleaned };
            foreach (var block in AgentUtilities.ExtractJsonBlocks(cleaned)) if (!candidates.Contains(block)) candidates.Add(block);
            foreach (var c in candidates.ToList()) { var rep = AgentUtilities.RepairJsonString(c); if (rep != null && !candidates.Contains(rep)) candidates.Add(rep); }
            foreach (var candidate in candidates) { if (string.IsNullOrWhiteSpace(candidate)) continue; try { JsonDocument.Parse(candidate, jsonOpts); jsonToParse = candidate; break; } catch (JsonException) { } }

            if (jsonToParse != null)
            {
                using var doc = JsonDocument.Parse(jsonToParse, jsonOpts);
                var root = doc.RootElement;

                if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
                { summary = root.TryGetProperty("summary", out var s) ? s.GetString() : "Task complete"; break; }

                if (root.TryGetProperty("cmd", out var cmdEl) || root.TryGetProperty("command", out cmdEl))
                {
                    var cmd = cmdEl.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(cmd)) { conversation.AppendLine("Empty command — try again."); continue; }
                    if ((cmd.Contains('\n') || cmd.Contains('\r')) && !cmd.Contains("@\""))
                    {
                        var san = cmd.Replace("\r\n", "; ").Replace("\r", "; ").Replace("\n", "; ");
                        await EmitLog(emitSse, "info", $"⚠ newlines in cmd — joined", ct: ct);
                        cmd = san;
                    }
                    var cmdLower = cmd.TrimStart().ToLowerInvariant();
                    if (cmdLower.StartsWith("mkdir") && Regex.IsMatch(cmd, @"\.\w{2,4}[""'\s]|\.\w{2,4}$"))
                    { conversation.AppendLine($"REJECTED: mkdir creates DIRECTORIES. Use: New-Item -ItemType File -Path \"<path>\" -Force"); continue; }
                    if (cmdLower == "cd" || cmdLower.StartsWith("cd ") || cmdLower.Contains("set-location"))
                    { conversation.AppendLine($"REJECTED: cd/Set-Location not supported. Use absolute paths."); continue; }

                    var beforeLen = _terminal.ReadAll().Length;
                    await _terminal.SendCommandAsync(cmd, projectRoot);
                    var marker = $"__DONE_{Guid.NewGuid():N}__";
                    await _terminal.WriteStdinAsync($"echo '{marker}'");
                    var timeout2 = DateTime.UtcNow.AddMinutes(10);
                    while (!ct.IsCancellationRequested && DateTime.UtcNow < timeout2)
                    { await Task.Delay(500); if (_terminal.ReadAll().Contains(marker)) break; }
                    var fullOut = _terminal.ReadAll();
                    var freshOut = beforeLen < fullOut.Length ? fullOut[beforeLen..] : "";
                    freshOut = string.Join("\n", (freshOut ?? "").Split('\n').Where(l => !l.Contains("__DONE_")));
                    var isError = !string.IsNullOrWhiteSpace(freshOut) &&
                        Regex.IsMatch(freshOut.ToLowerInvariant(),
                            @"not recognized|not found|cannot find|terminate|error|exception|failed|access denied|permission denied");
                    var result = new Dictionary<string, object?>
                    { ["index"] = stepIndex++, ["type"] = "command", ["command"] = cmd, ["status"] = isError ? "error" : "done", ["output"] = freshOut };
                    steps.Add(result);
                    if (emitSse) await SendSse(Response, "step", result, ct);
                    conversation.AppendLine($"Command [{i + 1}]: {cmd}");
                    conversation.AppendLine(isError ? "⚠ Error:" : "Output:");
                    conversation.AppendLine(freshOut);
                    continue;
                }

                if (root.TryGetProperty("web_search", out var searchEl))
                {
                    var query = searchEl.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(query)) { conversation.AppendLine("Empty query."); continue; }
                    if (!usedSearchQueries.Add(query)) { conversation.AppendLine($"Already searched for \"{query}\". Use the results above."); continue; }
                    var (searchOut, _) = await WebSearchAsync(query, ct);
                    var wr = new Dictionary<string, object?> { ["index"] = stepIndex++, ["type"] = "web_search", ["query"] = query, ["status"] = "done", ["output"] = searchOut };
                    steps.Add(wr); if (emitSse) await SendSse(Response, "step", wr, ct);
                    conversation.AppendLine($"Web search [{i + 1}]: {query}\nResults:\n{searchOut}");
                    continue;
                }

                if (root.TryGetProperty("web_fetch", out var fetchEl))
                {
                    var url = fetchEl.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(url)) { conversation.AppendLine("Empty URL."); continue; }
                    var (fetchOut, _) = await WebFetchAsync(url, ct);
                    var fr = new Dictionary<string, object?> { ["index"] = stepIndex++, ["type"] = "web_fetch", ["url"] = url, ["status"] = "done", ["output"] = fetchOut };
                    steps.Add(fr); if (emitSse) await SendSse(Response, "step", fr, ct);
                    conversation.AppendLine($"Fetch [{i + 1}]: {url}\n{fetchOut}");
                    continue;
                }

                if (root.TryGetProperty("message", out var msgEl) || root.TryGetProperty("result", out msgEl))
                {
                    var msgText = msgEl.GetString() ?? "";
                    var mr = new Dictionary<string, object?> { ["index"] = stepIndex++, ["type"] = "message", ["output"] = msgText };
                    steps.Add(mr); if (emitSse) await SendSse(Response, "step", mr, ct);
                    conversation.AppendLine($"Message: {msgText}");
                    continue;
                }

                conversation.AppendLine("Unrecognized JSON — use cmd, web_search, web_fetch, message, or done.");
                continue;
            }

            var fallback = cleaned.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(fallback) && fallback.Length < 500)
            {
                var bl = _terminal.ReadAll().Length;
                await _terminal.SendCommandAsync(fallback, projectRoot);
                await Task.Delay(3000);
                var out2 = _terminal.ReadAll();
                var fresh2 = bl < out2.Length ? out2[bl..] : "";
                conversation.AppendLine($"Tried: {fallback}\nOutput:\n{fresh2}");
                steps.Add(new Dictionary<string, object?> { ["index"] = stepIndex++, ["type"] = "command", ["command"] = fallback, ["status"] = "done", ["output"] = fresh2 });
                continue;
            }
            conversation.AppendLine("Could not parse — use valid JSON.");
        }

        summary ??= $"Command execution completed ({steps.Count} steps)";
        await EmitLog(emitSse, "info", summary, steps, ct: ct);
        return (steps, null);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FILE CREATION
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<(List<object> results, int stepsCount)> HandleCreateFile(
        string changeDesc, string projectRoot, string originalPrompt, string discoveryContext,
        int idx, bool emitSse, CancellationToken ct,
        string? explicitRelPath = null, List<string>? attachedFiles = null)
    {
        var results = new List<object>();
        var targetRelPath = explicitRelPath;
        if (string.IsNullOrWhiteSpace(targetRelPath))
        {
            var namedMatch = Regex.Match(changeDesc, @"(?:new\s+)?file\s+(?:called|named)?\s*['""`]?([\w./\\-]+\.[\w.-]+)['""`]?", RegexOptions.IgnoreCase);
            if (namedMatch.Success) targetRelPath = namedMatch.Groups[1].Value.Replace('\\', '/');
        }
        if (string.IsNullOrWhiteSpace(targetRelPath)) { var pm = Regex.Match(changeDesc, @"[\w/\\]+\.[\w]+"); if (pm.Success) targetRelPath = pm.Value.Replace('\\', '/'); }
        if (string.IsNullOrWhiteSpace(targetRelPath)) { var dm = Regex.Match(changeDesc, @"\.[\w-]+(?:\.[\w-]+)*"); if (dm.Success) targetRelPath = dm.Value; }
        if (string.IsNullOrWhiteSpace(targetRelPath)) targetRelPath = "newfile.txt";
        targetRelPath = targetRelPath.Replace('\\', '/');
        if (!targetRelPath.Contains('/'))
        { var folder = AgentUtilities.InferTargetFolder(targetRelPath, projectRoot); if (!string.IsNullOrWhiteSpace(folder)) targetRelPath = folder + targetRelPath; }

        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, targetRelPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!AgentUtilities.IsPathUnderRoot(fullPath, projectRoot))
        { await EmitLog(emitSse, "error", $"Create target {targetRelPath} is outside project root", ct: ct); return (results, 0); }

        await EmitLog(emitSse, "info", $"Generating content for: {targetRelPath}", ct: ct);

        var contentPrompt = $"Generate COMPLETE content for: {targetRelPath}\nTask: {originalPrompt}\nDescription: {changeDesc}\nContext:\n{discoveryContext}\n\nOutput ONLY the raw file content — no markdown, no fences, no explanation.";
        var (content, _, _) = await CallLlmRaw(
            "Output ONLY raw file content — no markdown, no code fences, no explanation.",
            contentPrompt, ct, _infiniteTimeout);

        var cleaned = StripFullFileFence(content ?? "");
        if (string.IsNullOrWhiteSpace(cleaned)) { await EmitLog(emitSse, "warn", $"Empty content for {targetRelPath}", ct: ct); return (results, 0); }

        var parentDir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir)) Directory.CreateDirectory(parentDir);
        await System.IO.File.WriteAllTextAsync(fullPath, cleaned, Encoding.UTF8);
        await EmitLog(emitSse, "success", $"Created {targetRelPath} ({cleaned.Length} chars)", ct: ct);
        attachedFiles?.Add(fullPath);

        if (emitSse) await SendSse(Response, "result", new { type = "create", path = targetRelPath, chars = cleaned.Length }, ct);
        results.Add(new Dictionary<string, object?> { ["status"] = "done", ["path"] = targetRelPath, ["output"] = cleaned, ["type"] = "create" });
        return (results, 1);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  HTTP ENDPOINTS
    // ═══════════════════════════════════════════════════════════════════════

    [HttpPost("execute")]
    public async Task<IActionResult> Execute([FromBody] AgentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt)) return BadRequest("Prompt is required");
        var projectRoot = GetProjectRoot(req.Project);
        await EmitLog(true, "info", "Orchestrating Request.", new { projectRoot, task = req.Prompt });

        var (allSteps, plan, complete) = await Orchestrate(req.Prompt, projectRoot, emitSse: false);
        return Ok(new
        {
            summary = plan?.Summary ?? "",
            thinking = plan?.Thinking ?? "",
            complete,
            steps = allSteps,
            filesEdited = ExtractFilesEdited(allSteps)
        });
    }

    [HttpPost("apply")]
    public async Task<IActionResult> ApplyEdits([FromBody] ApplyEditsRequest req)
    {
        if (req.Edits == null || req.Edits.Count == 0) return BadRequest(new { error = "No edits provided" });
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
                catch (Exception ex) { commandResults.Add(new { command = cmd.Command, status = "error", error = ex.Message }); }
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
        Response.Headers["Connection"] = "keep-alive";
        var bufferingFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
        bufferingFeature?.DisableBuffering();
        await Response.StartAsync(Response.HttpContext.RequestAborted);

        var keepaliveCts = CancellationTokenSource.CreateLinkedTokenSource(Response.HttpContext.RequestAborted);
        var keepaliveTask = Task.Run(async () =>
        {
            while (!keepaliveCts.Token.IsCancellationRequested)
            {
                try { await Task.Delay(15000, keepaliveCts.Token); await Response.WriteAsync(":\n\n", keepaliveCts.Token); await Response.Body.FlushAsync(keepaliveCts.Token); }
                catch { break; }
            }
        }, keepaliveCts.Token);

        if (string.IsNullOrWhiteSpace(req.Prompt))
        {
            await SendSse(Response, "error", new { message = "Prompt is required" });
            await SendSse(Response, "done", new { });
            keepaliveCts.Cancel(); try { await keepaliveTask; } catch { }
            return;
        }

        try
        {
            var projectRoot = GetProjectRoot(req.Project);
            await SendSse(Response, "phase", new { phase = "start", projectRoot });
            await EmitLog(true, "info", "Agent started", new { projectRoot, task = req.Prompt });

            // Load existing plan from board data (by cardId) — no need for frontend to pass it
            AgentPlan? existingPlan = null;
            HashSet<int>? completedIndices = null;
            if (!string.IsNullOrWhiteSpace(req.CardId))
            {
                var (loadedPlan, loadedCompleted) = await LoadPlanFromBoardDataAsync(req.CardId);
                existingPlan = loadedPlan;
                completedIndices = loadedCompleted;
            } 

            var (allSteps, plan, complete) = await Orchestrate(
                req.Prompt, projectRoot, emitSse: true,
                ct: Response.HttpContext.RequestAborted,
                attachedFiles: req.Files?.Count > 0 ? req.Files : null,
                steeringContext: req.SteeringContext,
                existingPlan: existingPlan,
                completedStepIndices: completedIndices,
                cardId: req.CardId);

            var filesEdited = ExtractFilesEdited(allSteps);
            var editsApplied = AgentUtilities.HasSuccessfulEdits(allSteps);

            await SendSse(Response, "done", new
            {
                summary = plan?.Summary ?? "",
                thinking = plan?.Thinking ?? "",
                complete,
                editsApplied,
                incomplete = AgentUtilities.TaskExpectsFileChanges(req.Prompt) && !complete,
                warning = !complete && AgentUtilities.TaskExpectsFileChanges(req.Prompt)
                    ? (editsApplied ? "Task may be incomplete. Please review."
                                    : "No files were modified.")
                    : (string?)null,
                steps = allSteps,
                filesEdited
            });

            if (req.SelfImproving)
            {
                try { await RunSelfImprovingPipeline(req.Prompt, projectRoot, allSteps, plan, complete, editsApplied); }
                catch (Exception siEx) { await EmitLog(true, "warn", $"Self-improving error: {siEx.Message}"); }
            }
        }
        catch (Exception ex)
        {
            await SendSse(Response, "error", new { message = ex.Message });
            await SendSse(Response, "done", new { incomplete = true, summary = ex.Message });
        }
        finally { keepaliveCts.Cancel(); try { await keepaliveTask; } catch { } }
    }

    [HttpGet("questions/pending")]
    public IActionResult GetPendingQuestions()
    {
        var list = _pendingQuestions.Values.OrderBy(q => q.CreatedUtc)
            .Select(q => new { q.Id, q.Question, q.Fields, q.CreatedUtc }).ToList();
        return Ok(new { questions = list });
    }

    [HttpPost("questions/answer")]
    public async Task<IActionResult> AnswerQuestion([FromBody] QuestionAnswerRequest req)
    {
        if (!_pendingQuestions.TryRemove(req.Id, out var pending))
            return NotFound("Question not found or already answered");
        pending.Answer.TrySetResult(req.Answers);
        return Ok(new { status = "answered" });
    }

    [HttpPost("context-review/confirm")]
    public IActionResult ConfirmContextReview([FromBody] ContextReviewAnswer req)
    {
        if (!_pendingContextReviews.TryRemove(req.Id, out var pending))
            return NotFound("Context review not found or already answered");
        pending.Answer.TrySetResult(req.Files ?? pending.Files);
        return Ok(new { status = "confirmed" });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  LLM CONNECTIVITY + CALL HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<bool> CheckLlmConnectivity(string projectRoot, bool emitSse, CancellationToken ct)
    {
        if (_nextConnectivityCheck != DateTime.MinValue &&
            DateTime.UtcNow - _nextConnectivityCheck < TimeSpan.FromMinutes(5))
        {
            await EmitLog(emitSse, "info", "Skipping connectivity check (cached)", ct: ct);
            return _lastConnectionCheckResult;
        }
        var baseUrl = await GetLlamaBaseUrl();
        _lastConnectionCheckResult = await CheckForConnectivity(projectRoot, emitSse, baseUrl, ct);
        _nextConnectivityCheck = DateTime.UtcNow.AddMinutes(5);
        return _lastConnectionCheckResult;
    }

    private async Task<bool> CheckForConnectivity(
        string projectRoot, bool emitSse, string baseUrl, CancellationToken ct)
    {
        var uri = new Uri(baseUrl);
        await EmitLog(emitSse, "info", $"Connectivity check: {uri.Host}:{uri.Port}", ct: ct);
        var tcpCmd = OperatingSystem.IsWindows()
            ? $"powershell -Command \"Test-NetConnection {uri.Host} -Port {uri.Port} -WarningAction SilentlyContinue | Select-Object TcpTestSucceeded | Format-List\""
            : $"nc -zv -w 2 {uri.Host} {uri.Port} 2>&1";
        var step = new AgentStep { Index = 0, Type = "command", Command = tcpCmd, Description = "tcp check" };
        var results = await ExecuteSteps(new List<AgentStep> { step }, projectRoot, 0, emitSse, ct);
        var first = results.FirstOrDefault() as Dictionary<string, object?>;
        var output = first?.TryGetValue("output", out var o) == true ? o?.ToString() ?? "" : "";
        var succeeded = output.Contains("TcpTestSucceeded : True", StringComparison.OrdinalIgnoreCase) ||
                        output.Contains("succeeded", StringComparison.OrdinalIgnoreCase) ||
                        output.Contains("HTTP 200", StringComparison.Ordinal);
        if (succeeded) { await EmitLog(emitSse, "info", $"LLM reachable", ct: ct); return true; }
        // Fallback: just try an HTTP call
        try
        {
            var client = _clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var resp = await client.GetAsync(baseUrl + "/api/tags", ct);
            if (resp.IsSuccessStatusCode || (int)resp.StatusCode < 500)
            { await EmitLog(emitSse, "info", $"LLM reachable via HTTP", ct: ct); return true; }
        }
        catch { }
        await EmitLog(emitSse, "error", $"LLM unreachable at {uri.Host}:{uri.Port}", ct: ct);
        return false;
    }

    private async Task<string> GetLlamaBaseUrl()
    {
        var cfg = await _configFile.LoadConfigAsync();
        return (cfg.llamaUrl ?? "http://localhost:8080").TrimEnd('/');
    }

    private async Task<(string raw, AgentResponse? response, string? error)> CallLlmRaw(
        string systemPrompt, string userMessage, CancellationToken ct = default,
        TimeSpan? requestTimeout = null, int? maxTokens = null)
    {
        var baseUrl = await GetLlamaBaseUrl();
        var model = _config.GetValue<string>("Ai:Model") ?? "medgemma:4b";
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
        var model = _config.GetValue<string>("Ai:Model") ?? "medgemma:4b";
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
        var mt = maxTokens ?? 2048;
        var reqBody = new { model, messages, stream = false, temperature = 0.05, max_tokens = mt };
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
        var mt = maxTokens ?? 2048;
        var reqBody = new { model, messages, stream = true, temperature = 0.05, max_tokens = mt };
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

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (line == null || string.IsNullOrWhiteSpace(line)) continue;
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
            if (string.IsNullOrWhiteSpace(raw)) return ("", null, "Empty LLM response");
            var parsed2 = ParseAgentResponse(raw);
            return (raw, parsed2, parsed2 == null ? "JSON parse failed" : null);
        }
        catch (TaskCanceledException) { return ("", null, "LLM request timed out"); }
        catch (Exception ex) { return ("", null, ex.Message); }
    }

    private async Task<(string raw, string? error)> CallLlmRawText(
        string systemPrompt, string userMessage, CancellationToken ct = default,
        TimeSpan? requestTimeout = null, int? maxTokens = null)
    {
        try
        {
            var baseUrl = await GetLlamaBaseUrl();
            var model = _config.GetValue<string>("Ai:Model") ?? "medgemma:4b";
            var client = _clientFactory.CreateClient("llama");
            client.Timeout = _infiniteTimeout;
            var messages = new object[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userMessage } };
            var timeout = requestTimeout ?? TimeSpan.FromMinutes(30);
            using var tCts = new CancellationTokenSource(timeout);
            using var lCts = CancellationTokenSource.CreateLinkedTokenSource(ct, tCts.Token);
            var reqBody = new { model, messages, stream = false, temperature = 0.0, max_tokens = maxTokens ?? MaxFileContextChars / 2 };
            var httpContent = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(baseUrl + "/v1/chat/completions", httpContent, lCts.Token);
            var respText = await resp.Content.ReadAsStringAsync(lCts.Token);
            var raw = ExtractLlmContent(respText);
            if (string.IsNullOrWhiteSpace(raw)) return (respText, "Empty LLM response");
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("```")) { var m = Regex.Match(trimmed, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) trimmed = m.Groups[1].Value.Trim(); }
            return (trimmed, null);
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
                var first = choices[0];
                if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
                    return c.GetString() ?? "";
            }
        }
        catch { }
        return "";
    }

    private static List<object> ExtractFilesEdited(List<object> steps)
    {
        var result = steps.OfType<Dictionary<string, object?>>()
            .Where(s => s.TryGetValue("type", out var t) && (t?.ToString() == "edit" || t?.ToString() == "rename") &&
                        s.TryGetValue("status", out var st) && st?.ToString() == "done")
            .Select(s => (object)new
            {
                path = s.GetValueOrDefault("path"),
                action = s.GetValueOrDefault("editAction"),
                toPath = s.GetValueOrDefault("toPath"),
                linesAdded = s.GetValueOrDefault("linesAdded"),
                linesRemoved = s.GetValueOrDefault("linesRemoved"),
                preview = s.GetValueOrDefault("diffPreview")
            }).ToList();
        if (result.Count > 0) return result;
        foreach (var step in steps)
        {
            if (step is Dictionary<string, object?>) continue;
            try
            {
                var json = JsonSerializer.Serialize(step);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : "";
                var status = root.TryGetProperty("status", out var st) ? st.GetString() : "";
                if ((type == "edit" || type == "rename") && status == "done")
                    result.Add(new { path = root.TryGetProperty("path", out var p) ? p.GetString() : null, action = (string?)null, toPath = (string?)null, linesAdded = 0, linesRemoved = 0, preview = (string?)null });
            }
            catch { }
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  STEP EXECUTORS
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<List<object>> ExecuteSteps(
        List<AgentStep> steps, string projectRoot, int indexOffset, bool emitSse,
        CancellationToken ct = default)
    {
        var results = new List<object>();
        var terminalStarted = false;
        var editContentCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
                var label = step.Description ?? step.Path ?? step.Command ?? step.Query ?? step.Pattern ?? "";
                await EmitLog(emitSse, "step", $"▶ {step.Type}: {label}", new {step, result}, ct: ct);
                await SendSse(Response, "step", result, ct);
            }
            try
            {
                switch (step.Type?.ToLowerInvariant())
                {
                    case "edit": await ExecuteEditStep(step, projectRoot, result, editContentCache); break;
                    case "command": if (!terminalStarted) { _terminal.Start(); terminalStarted = true; } await ExecuteCommandStep(step, projectRoot, result); break;
                    case "rename": await ExecuteRenameStep(step, projectRoot, result); break;
                    case "read": await ExecuteReadStep(step, projectRoot, result); break;
                    case "list": await ExecuteListStep(step, projectRoot, result); break;
                    case "glob": await ExecuteGlobStep(step, projectRoot, result); break;
                    case "grep": await ExecuteGrepStep(step, projectRoot, result); break;
                    case "web": case "web_search": case "web_fetch": await ExecuteWebStep(step, result); break;
                    default: result["status"] = "error"; result["error"] = $"Unknown step type: {step.Type}"; break;
                }
            }
            catch (Exception ex) { result["status"] = "error"; result["error"] = ex.Message; }
            result["status"] = AgentUtilities.NormalizeUiStatus(result["status"]?.ToString());
            results.Add(result);
            if (emitSse)
            {
                var st = result["status"]?.ToString() ?? "?";
                var outputRaw = result.GetValueOrDefault("output")?.ToString();
                var outputPreview = outputRaw != null && outputRaw.Length > 200 ? outputRaw[..200] + "…" : outputRaw;
                await EmitLog(emitSse, st == "error" ? "error" : "info", $"✓ {step.Type} ({st})",
                    new { path = result.GetValueOrDefault("path"), error = result.GetValueOrDefault("error"), output = outputPreview }, ct: ct);
                await SendSse(Response, "step", result, ct);
            }
        }
        return results;
    }

    private async Task<List<object>> ExecuteDiscoveryStepsConcurrent(
        List<AgentStep> steps, string projectRoot, int indexOffset, bool emitSse)
    {
        var count = steps.Count;
        var results = new Dictionary<string, object?>[count];
        for (var i = 0; i < count; i++)
        {
            var step = steps[i];
            var displayIndex = indexOffset + step.Index;
            var result = new Dictionary<string, object?>
            { ["index"] = displayIndex, ["type"] = step.Type, ["description"] = step.Description, ["status"] = "running" };
            results[i] = result;
            if (emitSse)
            {
                await EmitLog(emitSse, "step", $"▶ {step.Type}: {step.Description ?? step.Path ?? ""}");
                await SendSse(Response, "step", result);
            }
        }
        var tasks = steps.Select((step, i) => Task.Run(async () =>
        {
            var result = results[i];
            try
            {
                switch (step.Type?.ToLowerInvariant())
                {
                    case "list": await ExecuteListStep(step, projectRoot, result); break;
                    case "grep": await ExecuteGrepStep(step, projectRoot, result); break;
                    case "read": await ExecuteReadStep(step, projectRoot, result); break;
                    default: result["status"] = "error"; result["error"] = $"Unknown: {step.Type}"; break;
                }
            }
            catch (Exception ex) { result["status"] = "error"; result["error"] = ex.Message; }
            result["status"] = AgentUtilities.NormalizeUiStatus(result["status"]?.ToString());
        }));
        await Task.WhenAll(tasks);
        for (var i = 0; i < count; i++)
        {
            if (emitSse)
            {
                var st = results[i]["status"]?.ToString() ?? "?";
                await EmitLog(emitSse, st == "error" ? "error" : "info", $"✓ {steps[i].Type} ({st})", new { path = results[i].GetValueOrDefault("path"), error = results[i].GetValueOrDefault("error") });
                await SendSse(Response, "step", results[i]);
            }
        }
        return results.Cast<object>().ToList();
    }

    private async Task ExecuteEditStep(
        AgentStep step, string projectRoot, Dictionary<string, object?> result,
        Dictionary<string, string>? contentCache = null)
    {
        var rawPath = (step.Path ?? "").Replace('/', Path.DirectorySeparatorChar);
        var isAbs = rawPath.Contains(":\\") || rawPath.StartsWith('/') || rawPath.StartsWith('\\');
        var targetPath = isAbs ? Path.GetFullPath(rawPath) : Path.GetFullPath(Path.Combine(projectRoot, rawPath));
        if (!isAbs && !AgentUtilities.IsPathUnderRoot(targetPath, projectRoot))
        { result["status"] = "error"; result["error"] = "Path outside project root"; return; }

        result["path"] = step.Path;
        var oldString = step.OldString ?? ""; var newString = step.NewString ?? "";
        var unsafeReason = GetUnsafeEditPayloadReason(oldString, newString);
        if (unsafeReason != null) { result["status"] = "error"; result["error"] = unsafeReason; return; }

        string content;
        if (contentCache != null && contentCache.TryGetValue(targetPath, out var cached)) content = cached;
        else
        {
            if (!System.IO.File.Exists(targetPath))
            {
                if (string.IsNullOrEmpty(oldString) && !string.IsNullOrEmpty(newString))
                {
                    var d = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) Directory.CreateDirectory(d);
                    await System.IO.File.WriteAllTextAsync(targetPath, newString, Encoding.UTF8);
                    result["oldStartLine"] = 0;
                    PopulateEditResult(result, "created", step.Path!, null, newString, newString);
                    if (contentCache != null) contentCache[targetPath] = newString;
                    return;
                }
                result["status"] = "error"; result["error"] = $"File does not exist: {step.Path}";
                result["suggestions"] = AgentUtilities.FindSimilarFiles(step.Path ?? "", projectRoot);
                return;
            }
            content = await System.IO.File.ReadAllTextAsync(targetPath, Encoding.UTF8);
        }

        if (string.IsNullOrEmpty(oldString))
        {
            content += newString;
            await System.IO.File.WriteAllTextAsync(targetPath, content, Encoding.UTF8);
            if (contentCache != null) contentCache[targetPath] = content;
            PopulateEditResult(result, "modified", step.Path!, null, newString, newString);
            return;
        }

        var (replaced, newContent, matchError, snippet) = TryReplaceSafe(content, oldString, newString);
        if (!replaced)
        {
            result["status"] = "error"; result["error"] = matchError ?? "oldString not found";
            if (snippet != null) result["snippet"] = snippet;
            result["oldStringPreview"] = oldString;
            return;
        }

        if (AgentUtilities.NormalizeLineEndings(newContent) == AgentUtilities.NormalizeLineEndings(content))
        { result["status"] = "skipped"; result["path"] = step.Path; return; }

        var normOld = AgentUtilities.NormalizeLineEndings(content);
        var normNew = AgentUtilities.NormalizeLineEndings(newContent);
        var minLen = Math.Min(normOld.Length, normNew.Length);
        var diffIdx = 0;
        while (diffIdx < minLen && normOld[diffIdx] == normNew[diffIdx]) diffIdx++;
        result["oldStartLine"] = normOld[..diffIdx].Count(c => c == '\n');

        await System.IO.File.WriteAllTextAsync(targetPath, newContent, Encoding.UTF8);
        if (contentCache != null) contentCache[targetPath] = newContent;
        PopulateEditResult(result, "modified", step.Path!, oldString, newString, newContent);
    }

    private static List<string> GetPlanSizeViolations(AgentPlan plan)
    {
        var violations = new List<string>();
        for (var i = 0; i < plan.Plan.Count; i++)
        {
            var step = plan.Plan[i];
            if (!AgentUtilities.IsRelativePath(step.File ?? "")) continue;
            var old = step.OldString ?? "";
            var lines = old.Split('\n').Length; var chars = old.Length;
            if (lines > 10 || chars > 400)
                violations.Add($"Step {i + 1} ({step.File}): oldString is {lines} lines/{chars} chars — will be resolved via focused call");
        }
        return violations;
    }

    private async Task<(bool isComplete, string reason)> AssessCompletion(
        string prompt, List<object> executedSteps, string projectRoot, CancellationToken ct,
        AgentPlan? plan = null, List<string>? attachedFiles = null)
    {
        var editSteps = executedSteps.OfType<Dictionary<string, object?>>()
            .Where(s => s.TryGetValue("type", out var t) && t?.ToString() == "edit").ToList();
        if (editSteps.Count == 0) return (true, "No edit steps — command-only task");

        var failed = editSteps.Where(s => !s.TryGetValue("status", out var st) || st?.ToString() is not ("done" or "skipped")).ToList();
        if (failed.Count > 0)
        {
            var failedPaths = string.Join(", ", failed.Select(f => f.GetValueOrDefault("path")?.ToString() ?? "?").Distinct());
            return (false, $"{failed.Count} edit step(s) failed: {failedPaths}");
        }

        var sb = new StringBuilder();
        sb.AppendLine("## Task"); sb.AppendLine(prompt); sb.AppendLine();

        if (plan?.Plan?.Count > 0)
        {
            sb.AppendLine("## Planned steps");
            foreach (var step in plan.Plan)
                sb.AppendLine($"- {step.File}: {step.Change}");
            sb.AppendLine();
        }

        sb.AppendLine("## Edit results");
        foreach (var s in editSteps.Take(10))
        {
            var path = s.GetValueOrDefault("path")?.ToString() ?? "?";
            var status = s.TryGetValue("status", out var st) ? st?.ToString() : "?";
            var error = s.TryGetValue("error", out var e) ? e?.ToString() : null;
            sb.AppendLine($"- {path}: {status}{(error != null ? $" → {error}" : "")}");
        }
        sb.AppendLine();

        // Collect paths of files modified by edit steps
        var modifiedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in editSteps)
        {
            var p = s.GetValueOrDefault("path")?.ToString();
            if (!string.IsNullOrWhiteSpace(p))
                modifiedSet.Add(p.Replace('\\', '/'));
        }

        // Attached files that were NOT modified: show their current content as "original context"
        if (attachedFiles != null && attachedFiles.Count > 0)
        {
            sb.AppendLine("## Original attached files");
            foreach (var relPath in attachedFiles)
            {
                var normalized = relPath.Replace('\\', '/');
                if (modifiedSet.Contains(normalized)) continue; // skip modified — shown fresh below
                var fullPath = Path.GetFullPath(Path.Combine(projectRoot, normalized));
                if (!System.IO.File.Exists(fullPath)) continue;
                var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                sb.AppendLine($"### {normalized}\n```\n{content}\n```\n");
            }
        }

        // All modified files: refetch fresh from disk (no truncation)
        var allModifiedPaths = editSteps
            .Where(s => s.TryGetValue("status", out var st) && st?.ToString() == "done")
            .Select(s => s.GetValueOrDefault("path")?.ToString())
            .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();

        if (allModifiedPaths.Count > 0)
        {
            sb.AppendLine("## Modified files (current state after edits)");
            foreach (var relPath in allModifiedPaths)
            {
                var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath!.Replace('/', Path.DirectorySeparatorChar)));
                if (!System.IO.File.Exists(fullPath)) { sb.AppendLine($"### {relPath}\n*File not found*\n"); continue; }
                var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                sb.AppendLine($"### {relPath}\n```\n{content}\n```\n");
            }
        }

        sb.AppendLine(@"Evaluate the code changes against the original task. Check for:
1. Does the code solve the original task completely?
2. Are there any bugs, syntax errors, or logic issues in the modified files?
3. Are there any missing pieces or regressions?

Respond with JSON only:
```json
{
  ""complete"": true|false,
  ""reason"": ""one sentence summary"",
  ""issues"": [""description of each bug or remaining work""]
}
```");

        const string sys = @"You are a thorough code reviewer and task completion verifier. Examine the original task, the changes made, and the current state of all files. Check for bugs, logic errors, syntax mistakes, and whether the task requirements are fully met. Output ONLY valid JSON in the format specified.";

        var (raw, _, _) = await CallLlmRaw(sys, sb.ToString(), ct, TimeSpan.FromSeconds(30));
        if (string.IsNullOrWhiteSpace(raw)) return (failed.Count == 0, "Assessment timed out");

        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```")) { var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) cleaned = m.Groups[1].Value.Trim(); }
            var s2 = cleaned.IndexOf('{'); var e2 = cleaned.LastIndexOf('}');
            if (s2 >= 0 && e2 > s2) cleaned = cleaned[s2..(e2 + 1)];
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            var isComplete = root.TryGetProperty("complete", out var c) && c.ValueKind == JsonValueKind.True;
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            // Collect issues into the reason for richer feedback
            if (root.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array)
            {
                var issueList = new List<string>();
                foreach (var issue in issues.EnumerateArray())
                {
                    if (issue.ValueKind == JsonValueKind.String)
                        issueList.Add(issue.GetString() ?? "");
                }
                if (issueList.Count > 0)
                    reason = reason + " | Issues: " + string.Join("; ", issueList);
            }
            return (isComplete, reason);
        }
        catch { return (failed.Count == 0, "Could not parse assessment"); }
    }

    private static AgentPlan MergePlans(AgentPlan? original, AgentPlan? replan)
    {
        if (original == null) return replan ?? new AgentPlan();
        if (replan == null) return original;
        var merged = new AgentPlan
        {
            Thinking = !string.IsNullOrWhiteSpace(replan.Thinking) ? replan.Thinking : original.Thinking,
            Summary = !string.IsNullOrWhiteSpace(replan.Summary) ? replan.Summary : original.Summary,
            Score = replan.Score > 0 ? replan.Score : original.Score,
            Plan = MergePlanSteps(original.Plan, replan.Plan)
        };
        return merged;
    }

    private static List<PlanStep> MergePlanSteps(IEnumerable<PlanStep> existing, IEnumerable<PlanStep> additions)
    {
        var result = new List<PlanStep>(existing);
        var existingKeys = new HashSet<string>(existing.Select(s => $"{s.File}|||{s.Change}"), StringComparer.OrdinalIgnoreCase);
        foreach (var step in additions)
        {
            var key = $"{step.File}|||{step.Change}";
            if (existingKeys.Add(key))
                result.Add(step);
        }
        return result;
    }

    private async Task<List<PlanStep>?> CheckpointReplan(
        string originalPrompt, string currentDiscoveryContext, List<PlanStep> remainingSteps,
        List<object> completedResults, string projectRoot, bool emitSse, CancellationToken ct,
        string? steeringContext = null)
    {
        var modifiedPaths = completedResults.OfType<Dictionary<string, object?>>()
            .Where(r => r.TryGetValue("type", out var t) && t?.ToString() is "edit" or "create" &&
                        r.TryGetValue("status", out var s) && s?.ToString() == "done")
            .Select(r => r.GetValueOrDefault("path")?.ToString())
            .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();

        await EmitLog(emitSse, "info", $"Checkpoint: refreshing {modifiedPaths.Count} file(s)…", ct: ct);
        var enriched = new StringBuilder(currentDiscoveryContext);
        enriched.AppendLine("\n## CHECKPOINT — current file states");
        foreach (var relPath in modifiedPaths)
        {
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath!.Replace('/', Path.DirectorySeparatorChar)));
            if (!System.IO.File.Exists(fullPath)) continue;
            var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
            enriched.AppendLine($"\n### {relPath} (post-phase)\n```\n{content}\n```");
        }
        if (remainingSteps.Count == 0) return null;

        var remainDesc = new StringBuilder("Intended remaining work (KEEP ALL of these — only add new ones):\n");
        foreach (var step in remainingSteps) remainDesc.AppendLine($"- {step.File}: {step.Change}");
        var replanPrompt = $"## Original task\n{originalPrompt}\n\n{remainDesc}" +
            (string.IsNullOrWhiteSpace(steeringContext) ? "" : $"\n## Steering\n{steeringContext}");

        var newPlan = await AnalyzePromptAndPlanCodeChanges(
            replanPrompt, enriched.ToString(), projectRoot, emitSse, ct, steeringContext);
        return newPlan?.Plan;
    }

    private async Task ExecuteRenameStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var srcRel = (step.Path ?? "").Replace('\\', '/');
        var dstRel = (step.ToPath ?? "").Replace('\\', '/');
        var srcPath = Path.GetFullPath(Path.Combine(projectRoot, srcRel.Replace('/', Path.DirectorySeparatorChar)));
        var dstPath = Path.GetFullPath(Path.Combine(projectRoot, dstRel.Replace('/', Path.DirectorySeparatorChar)));
        result["path"] = srcRel; result["toPath"] = dstRel;
        if (!AgentUtilities.IsPathUnderRoot(srcPath, projectRoot) || !AgentUtilities.IsPathUnderRoot(dstPath, projectRoot))
        { result["status"] = "error"; result["error"] = "Path outside project root"; return; }
        if (!System.IO.File.Exists(srcPath)) { result["status"] = "error"; result["error"] = $"Source not found: {srcRel}"; return; }
        if (System.IO.File.Exists(dstPath)) { result["status"] = "error"; result["error"] = $"Destination exists: {dstRel}"; return; }
        try
        {
            var dir = Path.GetDirectoryName(dstPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            System.IO.File.Move(srcPath, dstPath);
            result["status"] = "done"; result["editAction"] = "renamed";
        }
        catch (Exception ex) { result["status"] = "error"; result["error"] = ex.Message; }
    }

    private static void PopulateEditResult(
        Dictionary<string, object?> result, string action, string path,
        string? oldStr, string? newStr, string writtenContent)
    {
        result["type"] = "edit";
        result["status"] = "done";
        result["editAction"] = action;
        result["path"] = path;
        result["linesRemoved"] = (oldStr ?? "").Split('\n').Length;
        result["linesAdded"] = (newStr ?? "").Split('\n').Length;
        if (!string.IsNullOrEmpty(oldStr)) result["oldStringPreview"] = oldStr;
        if (!string.IsNullOrEmpty(newStr)) result["newStringPreview"] = newStr;
        result["diffPreview"] = AgentUtilities.BuildDiffPreview(oldStr, newStr);
        result["oldLines"] = (oldStr ?? "").Split('\n');
        result["newLines"] = (newStr ?? "").Split('\n');
    }

    // ── Multi-strategy string replacement (progressive fallback chain) ────

    private static string RepairJsonNewlines(string json)
    {
        // The LLM often outputs literal newlines inside JSON string values instead of \n.
        // We try to repair this by scanning inside string boundaries and escaping raw newlines.
        var sb = new StringBuilder(json.Length);
        var inString = false;
        var escaped = false;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (escaped) { sb.Append(c); escaped = false; continue; }
            if (c == '\\' && inString) { sb.Append(c); escaped = true; continue; }
            if (c == '"' && !escaped) { inString = !inString; sb.Append(c); continue; }
            if (inString && (c == '\n' || c == '\r'))
            {
                sb.Append("\\n");
                if (c == '\r' && i + 1 < json.Length && json[i + 1] == '\n') i++;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string CollapseWhitespace(string s) =>
        string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static int FindLineBlock(string[] fileLines, string[] patternLines, StringComparison cmp)
    {
        if (patternLines.Length == 0 || fileLines.Length < patternLines.Length) return -1;
        for (var fi = 0; fi <= fileLines.Length - patternLines.Length; fi++)
        {
            var match = true;
            for (var li = 0; li < patternLines.Length; li++)
            {
                if (!string.Equals(fileLines[fi + li], patternLines[li], cmp))
                { match = false; break; }
            }
            if (match) return fi;
        }
        return -1;
    }

    private static int ComputeLevenshteinDistance(string a, string b)
    {
        var m = a.Length; var n = b.Length;
        if (m == 0) return n; if (n == 0) return m;
        var d = new int[n + 1];
        for (var i = 0; i <= n; i++) d[i] = i;
        for (var i = 1; i <= m; i++)
        {
            var prev = d[0]; d[0] = i;
            for (var j = 1; j <= n; j++)
            {
                var temp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1),
                    prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = temp;
            }
        }
        return d[n];
    }

    private static double ComputeLineSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 1.0;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
        var aNorm = a.Trim().ToLowerInvariant();
        var bNorm = b.Trim().ToLowerInvariant();
        var maxLen = Math.Max(aNorm.Length, bNorm.Length);
        if (maxLen == 0) return 1.0;
        // For short strings use exact char comparison, for longer use Levenshtein
        if (maxLen <= 80)
            return 1.0 - (double)ComputeLevenshteinDistance(aNorm, bNorm) / maxLen;
        // For long strings, check common prefix ratio
        var common = 0; var minLen = Math.Min(aNorm.Length, bNorm.Length);
        for (var i = 0; i < minLen; i++) { if (aNorm[i] == bNorm[i]) common++; else break; }
        return (double)common / maxLen;
    }

    private static (int lineIdx, double score, bool hasExactLine) FindBestFuzzyBlock(string[] fileLines, string[] oldLines)
    {
        if (oldLines.Length == 0 || fileLines.Length < oldLines.Length) return (-1, 0, false);
        // Collapse whitespace so indentation/style differences don't penalize similarity
        var collapsedFile = fileLines.Select(CollapseWhitespace).ToArray();
        var collapsedOld = oldLines.Select(CollapseWhitespace).ToArray();
        var bestScore = 0.0; var bestIdx = -1; var bestHasExact = false;
        for (var fi = 0; fi <= collapsedFile.Length - collapsedOld.Length; fi++)
        {
            var totalSim = 0.0; var anyExact = false;
            for (var li = 0; li < collapsedOld.Length; li++)
            {
                var sim = ComputeLineSimilarity(collapsedFile[fi + li], collapsedOld[li]);
                totalSim += sim;
                if (sim >= 0.95) anyExact = true;
            }
            var avg = totalSim / collapsedOld.Length;
            if (avg > bestScore) { bestScore = avg; bestIdx = fi; bestHasExact = anyExact; }
        }
        return (bestIdx, bestScore, bestHasExact);
    }

    private static (int lineIdx, double score, int exactCount) FindBestAnchorLineBlock(string[] fileLines, string[] oldLines)
    {
        if (oldLines.Length == 0) return (-1, 0, 0);

        // A line is a candidate anchor only if it's long enough to be distinctive
        var candidates = oldLines
            .Select((l, i) => (line: l.Trim(), idx: i))
            .Where(x => x.line.Length >= 8)
            .OrderByDescending(x => x.line.Length)
            .ToList();

        foreach (var anchor in candidates)
        {
            var matches = new List<int>();
            for (var fi = 0; fi < fileLines.Length; fi++)
            {
                if (string.Equals(fileLines[fi].Trim(), anchor.line, StringComparison.Ordinal))
                    matches.Add(fi);
            }
            if (matches.Count != 1) continue;

            var filePos = matches[0] - anchor.idx;
            if (filePos < 0 || filePos + oldLines.Length > fileLines.Length) continue;

            var totalSim = 0.0; var exactCount = 0;
            for (var li = 0; li < oldLines.Length; li++)
            {
                var sim = ComputeLineSimilarity(fileLines[filePos + li], oldLines[li]);
                totalSim += sim;
                if (sim >= 0.95) exactCount++;
            }
            var avg = totalSim / oldLines.Length;
            if (avg >= 0.80 && exactCount >= 2)
                return (filePos, avg, exactCount);
        }
        return (-1, 0, 0);
    }

    private static (bool ok, string content, string? error, string? snippet) TryReplaceSafe(
        string content, string oldString, string newString)
    {
        content = AgentUtilities.NormalizeLineEndings(content);
        oldString = AgentUtilities.NormalizeLineEndings(oldString);
        newString = AgentUtilities.NormalizeLineEndings(newString);

        if (string.IsNullOrEmpty(oldString))
            return (false, content, "oldString is empty", null);

        // ── S1: Exact ordinal match ─────────────────────────────────────────
        var idx = content.IndexOf(oldString, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var sec = content.IndexOf(oldString, idx + oldString.Length, StringComparison.Ordinal);
            if (sec >= 0) return (false, content,
                "oldString appears more than once — use a longer unique anchor", null);
            return (true, content[..idx] + newString + content[(idx + oldString.Length)..], null, null);
        }

        var fileLines = content.Split('\n');
        var oldLines = oldString.Split('\n');
        if (oldLines.Length == 0) return (false, content, "oldString is empty", null);

        // ── S2: Trailing-whitespace-trimmed exact match ────────────────────
        var trimmedOld = oldLines.Select(l => l.TrimEnd()).ToArray();
        var trimmedFile = fileLines.Select(l => l.TrimEnd()).ToArray();
        var matchLine = FindLineBlock(trimmedFile, trimmedOld, StringComparison.Ordinal);
        if (matchLine >= 0)
            return (true, ReplaceLineBlock(fileLines, matchLine, oldLines.Length, newString), null, null);

        // ── S3: Whitespace-collapsed case-sensitive match ──────────────────
        var wsOld = oldLines.Select(l => CollapseWhitespace(l)).ToArray();
        var wsFile = fileLines.Select(l => CollapseWhitespace(l)).ToArray();
        matchLine = FindLineBlock(wsFile, wsOld, StringComparison.Ordinal);
        if (matchLine >= 0)
            return (true, ReplaceLineBlock(fileLines, matchLine, oldLines.Length, newString), null, null);

        // ── S4: Whitespace-collapsed case-insensitive match ────────────────
        matchLine = FindLineBlock(wsFile, wsOld, StringComparison.OrdinalIgnoreCase);
        if (matchLine >= 0)
            return (true, ReplaceLineBlock(fileLines, matchLine, oldLines.Length, newString), null, null);

        // ── Guard: reject oldStrings that are too short or generic for fuzzy strategies ──
        var meaningfulChars = oldLines.Sum(l => l.Trim().Length);
        var maxMeaningfulLine = oldLines.Max(l => l.Trim().Length);
        if (meaningfulChars < 20 || maxMeaningfulLine < 8)
        {
            return (false, content,
                $"oldString too short or generic ({meaningfulChars} meaningful chars, longest line {maxMeaningfulLine} chars)", null);
        }

        // ── Fuzzy strategies: apply edit at high confidence ────────────────
        // S5 and S6 APPLY the edit when similarity is high enough.
        // The caller (ResolveAndApplyEdit) runs VerifyEdit on the result,
        // which catches any wrong-location matches.

        // S5: Fuzzy Levenshtein block match (high threshold → apply)
        {
            var (fl, score, hasExact) = FindBestFuzzyBlock(fileLines, oldLines);
            if (fl >= 0 && score >= 0.95)
            {
                var snippet = $"fuzzy {score:P0} at line {fl + 1}";
                return (true, ReplaceLineBlock(fileLines, fl, oldLines.Length, newString), null, snippet);
            }
            // Lower threshold → report location hint only (don't apply)
            if (fl >= 0 && score >= 0.88)
            {
                return (false, content, "oldString not found verbatim in file",
                    $"fuzzy {score:P0} at line {fl + 1}: too low confidence ({score:P0} < 95%)");
            }
        }

        // S6: Anchor-line block match (high threshold → apply)
        {
            var (al, score, exactCount) = FindBestAnchorLineBlock(fileLines, oldLines);
            if (al >= 0 && score >= 0.90 && exactCount >= 2)
            {
                var snippet = $"anchor {score:P0} at line {al + 1} ({exactCount} exact line(s))";
                return (true, ReplaceLineBlock(fileLines, al, oldLines.Length, newString), null, snippet);
            }
        }

        // S7: Single-line exact match (safe to apply — trimmed line appears exactly once)
        if (oldLines.Length == 1)
        {
            var trimmed = oldLines[0].Trim();
            if (trimmed.Length >= 15)
            {
                var occ = 0; var occIdx = -1;
                for (var fi = 0; fi < fileLines.Length; fi++)
                {
                    if (string.Equals(fileLines[fi].Trim(), trimmed, StringComparison.Ordinal))
                    { occ++; occIdx = fi; }
                }
                if (occ == 1)
                    return (true, ReplaceLineBlock(fileLines, occIdx, 1, newString),
                        null, $"single-line: line {occIdx + 1}");
            }
        }

        var hint = BuildExactMatchHint(content, oldString);
        return (false, content, "oldString not found verbatim in file", hint);
    }

    private static string StripFullFileFence(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var cleaned = value.Replace("\r\n", "\n");
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = cleaned.IndexOf('\n');
            if (firstNewline >= 0)
                cleaned = cleaned[(firstNewline + 1)..];
            else
                return string.Empty;
        }

        if (cleaned.EndsWith("```", StringComparison.Ordinal))
            cleaned = cleaned[..^3];

        return cleaned.TrimStart('\n').TrimEnd('\n');
    }

    private static string? BuildExactMatchHint(string content, string oldString)
    {
        var fileLines = content.Split('\n');
        var oldLines = oldString.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length >= 8)  // skip short generic lines like "}", ");"
            .ToList();
        if (oldLines.Count == 0 || fileLines.Length == 0) return null;

        var results = new List<(int fileIdx, double score, string line)>();
        for (var fi = 0; fi < fileLines.Length; fi++)
        {
            var fLine = fileLines[fi];
            if (fLine.Trim().Length < 8) continue;  // skip short file lines too
            var bestSim = oldLines.Max(o => ComputeLineSimilarity(fLine, o));
            if (bestSim >= 0.50)
                results.Add((fi, bestSim, fLine));
        }

        // Take the top 3 highest-scoring results, prefer longer lines for disambiguation
        var best = results
            .OrderByDescending(r => r.score)
            .ThenByDescending(r => r.line.Trim().Length)
            .Take(3)
            .ToList();
        if (best.Count == 0) return null;

        var detail = best.Select(b =>
            $"  ({(b.score * 100):F0}% match) line {b.fileIdx + 1}: {b.line}");
        return string.Join('\n', detail);
    }

    /// <summary>Find where oldString fuzzily matches the file and return the verbatim file lines for copying.</summary>
    private static string? BuildExactMatchBlock(string content, string oldString)
    {
        var fileLines = content.Split('\n');
        var oldLines = oldString.Split('\n');
        if (oldLines.Length < 2 || fileLines.Length < oldLines.Length) return null;

        // Use collapsed-whitespace fuzzy matching (same logic as FindBestFuzzyBlock)
        var collapsedFile = fileLines.Select(CollapseWhitespace).ToArray();
        var collapsedOld = oldLines.Select(CollapseWhitespace).ToArray();

        var bestScore = 0.0; var bestIdx = -1;
        for (var fi = 0; fi <= collapsedFile.Length - collapsedOld.Length; fi++)
        {
            var totalSim = 0.0;
            for (var li = 0; li < collapsedOld.Length; li++)
                totalSim += ComputeLineSimilarity(collapsedFile[fi + li], collapsedOld[li]);
            var avg = totalSim / collapsedOld.Length;
            if (avg > bestScore) { bestScore = avg; bestIdx = fi; }
        }

        if (bestIdx < 0 || bestScore < 0.85) return null;

        return string.Join("\n", fileLines.Skip(bestIdx).Take(oldLines.Length));
    }

    private static string? GetUnsafeEditPayloadReason(string oldString, string newString)
    {
        foreach (var marker in UnsafeEditMarkers)
            if (oldString.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                newString.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return $"Edit contains placeholder marker '{marker}'.";
        return null;
    }

    private static string RemoveUnsafeEditMarkersForPrompt(string value)
    {
        foreach (var marker in UnsafeEditMarkers)
            value = value.Replace(marker, "[placeholder removed]", StringComparison.OrdinalIgnoreCase);
        return value;
    }

    private static string ReplaceLineBlock(string[] fileLines, int start, int count, string replacement)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < start; i++) { sb.Append(fileLines[i]); sb.Append('\n'); }
        sb.Append(IndentReplacement(fileLines, start, replacement));
        for (var i = start + count; i < fileLines.Length; i++) { sb.Append('\n'); sb.Append(fileLines[i]); }
        return sb.ToString();
    }

    /// <summary>Adjust replacement indentation to match the target block's base indent.</summary>
    private static string IndentReplacement(string[] fileLines, int start, string replacement)
    {
        if (string.IsNullOrEmpty(replacement) || start >= fileLines.Length)
            return replacement;

        var fileIndent = GetLeadingWhitespace(fileLines[start]);
        if (fileIndent.Length == 0)
            return replacement;

        // Find the indentation of the first non-empty replacement line
        var replLines = replacement.Split('\n');
        var replBaseIndent = replLines.Where(l => l.Length > 0).Select(GetLeadingWhitespace).FirstOrDefault();

        if (replBaseIndent != fileIndent)
        {
            for (var i = 0; i < replLines.Length; i++)
            {
                if (replLines[i].Length == 0) continue;
                var lineIndent = GetLeadingWhitespace(replLines[i]);
                if (lineIndent.StartsWith(replBaseIndent, StringComparison.Ordinal))
                {
                    var excess = lineIndent[replBaseIndent.Length..];
                    replLines[i] = fileIndent + excess + replLines[i][lineIndent.Length..];
                }
                else
                {
                    replLines[i] = fileIndent + replLines[i];
                }
            }
        }

        // Auto-indent: apply proper inner indentation based on brace/scoping depth
        return AutoIndentFromFile(string.Join("\n", replLines), fileIndent, fileLines, start);
    }

    /// <summary>Auto-indent replacement lines based on brace depth, using the file's indent style.</summary>
    private static string AutoIndentFromFile(string replacement, string fileIndent, string[] fileLines, int start)
    {
        // Infer indent size from the file (difference between parent and child indent levels)
        var indentSize = InferIndentSize(fileLines, start);
        if (indentSize <= 0) return replacement;

        var lines = replacement.Split('\n');
        var depth = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;

            var trimmed = lines[i].TrimStart();
            // Compute indent depth for THIS line only (don't mutate depth yet)
            var lineDepth = depth;
            if (trimmed.StartsWith("}"))
                lineDepth = Math.Max(0, lineDepth - 1);

            var expectedIndent = fileIndent + new string(' ', lineDepth * indentSize);
            var lineIndent = GetLeadingWhitespace(lines[i]);
            if (lineIndent != expectedIndent)
                lines[i] = expectedIndent + trimmed;

            // Update depth for the NEXT line by counting braces in this line
            foreach (var c in trimmed)
            {
                if (c == '{') depth++;
                if (c == '}') depth = Math.Max(0, depth - 1);
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>Infer the file's indent size (e.g. 2 or 4) by sampling indentation deltas.</summary>
    private static int InferIndentSize(string[] fileLines, int start)
    {
        var sampleStart = Math.Max(0, start - 5);
        var sampleEnd = Math.Min(fileLines.Length, start + 20);
        var deltas = new List<int>();
        for (var i = sampleStart + 1; i < sampleEnd; i++)
        {
            var prev = GetLeadingWhitespace(fileLines[i - 1]).Length;
            var curr = GetLeadingWhitespace(fileLines[i]).Length;
            var delta = Math.Abs(curr - prev);
            if (delta > 0 && delta <= 8)
                deltas.Add(delta);
        }
        if (deltas.Count == 0) return 2; // default
        // Return the most common delta (or the average rounded)
        return (int)Math.Round(deltas.Average());
    }

    private static string GetLeadingWhitespace(string s)
    {
        var i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        return s[..i];
    }

    // ── Individual step executors ──────────────────────────────────────────

    private async Task ExecuteCommandStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var command = step.Command ?? "";
        if (string.IsNullOrWhiteSpace(command)) { result["status"] = "error"; result["error"] = "No command"; return; }
        var beforeLen = _terminal.ReadAll().Length;
        await _terminal.SendCommandAsync(command, projectRoot);
        var prevLen = beforeLen; var stableMs = 0;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(500);
            var curLen = _terminal.ReadAll().Length;
            if (curLen == prevLen) { stableMs += 500; if (stableMs >= 3000) break; }
            else { stableMs = 0; prevLen = curLen; }
        }
        result["status"] = "done"; result["command"] = command;
        var fullOutput = _terminal.ReadAll();
        result["output"] = beforeLen >= 0 && beforeLen < fullOutput.Length ? fullOutput[beforeLen..] : "";
        result["snippet"] = result["output"] as string ?? "";
    }

    private async Task<(string output, string? error)> WebSearchAsync(string query, CancellationToken ct)
    {
        try
        {
            var client = _clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(1);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            var apiUrl = "https://api.duckduckgo.com/?q=" + Uri.EscapeDataString(query) + "&format=json&no_html=1&skip_disambig=1";
            var resp = await client.GetAsync(apiUrl, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var sb = new StringBuilder();
            if (root.TryGetProperty("AbstractText", out var abs) && abs.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(abs.GetString()))
            { sb.AppendLine("## Summary"); sb.AppendLine(abs.GetString()); if (root.TryGetProperty("AbstractURL", out var url)) sb.AppendLine($"Source: {url.GetString()}"); sb.AppendLine(); }
            if (root.TryGetProperty("Answer", out var ans) && ans.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(ans.GetString()))
                sb.AppendLine($"Answer: {ans.GetString()}");
            if (root.TryGetProperty("RelatedTopics", out var topics) && topics.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine("## Results"); var count = 0;
                foreach (var topic in topics.EnumerateArray())
                {
                    if (count >= 10) break;
                    if (topic.TryGetProperty("Text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        var u = topic.TryGetProperty("FirstURL", out var fu) ? fu.GetString() : "";
                        sb.AppendLine($"  - {text.GetString()}{(string.IsNullOrWhiteSpace(u) ? "" : $" ({u})")}"); count++;
                    }
                }
            }
            return (sb.Length > 0 ? sb.ToString() : "(no results)", null);
        }
        catch (Exception ex) { return ("", ex.Message); }
    }

    private async Task<(string output, string? error)> WebFetchAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = _clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            var resp = await client.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "text/plain";
            if (contentType.Contains("html")) body = Regex.Replace(body, "<[^>]+>", " ");
            return ($"HTTP {(int)resp.StatusCode}\n{body.Trim()}", null);
        }
        catch (Exception ex) { return ("", ex.Message); }
    }

    private async Task ExecuteReadStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var relPath = (step.Path ?? "").Replace('/', Path.DirectorySeparatorChar);
        var targetPath = Path.GetFullPath(Path.Combine(projectRoot, relPath));
        if (!AgentUtilities.IsPathUnderRoot(targetPath, projectRoot)) { result["status"] = "error"; result["error"] = "Path outside root"; return; }
        if (!System.IO.File.Exists(targetPath)) { result["status"] = "error"; result["error"] = "File not found"; return; }
        result["path"] = step.Path;
        result["output"] = await System.IO.File.ReadAllTextAsync(targetPath, Encoding.UTF8);
        result["status"] = "done";
    }

    private Task ExecuteListStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var relPath = string.IsNullOrWhiteSpace(step.Path) ? "" : step.Path.Replace('/', Path.DirectorySeparatorChar);
        var targetPath = Path.GetFullPath(Path.Combine(projectRoot, relPath));
        if (!AgentUtilities.IsPathUnderRoot(targetPath, projectRoot)) { result["status"] = "error"; result["error"] = "Path outside root"; return Task.CompletedTask; }
        if (!Directory.Exists(targetPath)) { result["status"] = "error"; result["error"] = "Directory not found"; return Task.CompletedTask; }
        var entries = Directory.GetFileSystemEntries(targetPath)
            .Select(e => (Directory.Exists(e) ? "[dir]  " : "[file] ") + Path.GetFileName(e))
            .OrderBy(x => x).Take(200);
        result["status"] = "done"; result["path"] = step.Path ?? ".";
        result["output"] = string.Join("\n", entries);
        return Task.CompletedTask;
    }

    private Task ExecuteGlobStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var pattern = (step.Pattern ?? step.Path ?? "*").Replace('\\', '/');
        result["path"] = pattern;
        try
        {
            IEnumerable<string> files;
            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                var parts = pattern.Split('/'); var filePattern = parts[^1];
                var dirParts = parts.Length > 1 ? parts[..^1] : Array.Empty<string>();
                var hasRec = dirParts.Any(p => p == "**");
                var dirClean = dirParts.Where(p => p != "**").ToList();
                if (dirClean.Count == 0 || hasRec)
                    files = Directory.EnumerateFiles(projectRoot, filePattern == "**" ? "*" : filePattern, SearchOption.AllDirectories);
                else
                {
                    var searchRoot = Path.GetFullPath(Path.Combine(projectRoot, string.Join(Path.DirectorySeparatorChar, dirClean)));
                    if (!AgentUtilities.IsPathUnderRoot(searchRoot, projectRoot)) throw new InvalidOperationException("Pattern outside root");
                    files = Directory.EnumerateFiles(searchRoot, filePattern, SearchOption.AllDirectories);
                }
            }
            else
            {
                var single = Path.GetFullPath(Path.Combine(projectRoot, pattern));
                files = System.IO.File.Exists(single) ? new[] { single } : Array.Empty<string>();
            }
            var list = files.Where(f => AgentUtilities.IsPathUnderRoot(f, projectRoot)).Take(100)
                .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/')).ToList();
            result["status"] = "done"; result["output"] = list.Count == 0 ? "(no matches)" : string.Join("\n", list);
        }
        catch (Exception ex) { result["status"] = "error"; result["error"] = ex.Message; }
        return Task.CompletedTask;
    }

    private Task ExecuteGrepStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var query = step.Query ?? step.Pattern ?? "";
        result["path"] = step.Path ?? ""; result["query"] = query;
        if (string.IsNullOrWhiteSpace(query)) { result["status"] = "error"; result["error"] = "grep requires query"; return Task.CompletedTask; }
        var searchRoot = projectRoot;
        if (!string.IsNullOrWhiteSpace(step.Path))
        {
            searchRoot = Path.GetFullPath(Path.Combine(projectRoot, step.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!AgentUtilities.IsPathUnderRoot(searchRoot, projectRoot)) { result["status"] = "error"; result["error"] = "Path outside root"; return Task.CompletedTask; }
        }
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules", ".git", "bin", "obj", "dist", ".angular" };
        var matches = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories))
            {
                if (!AgentUtilities.IsPathUnderRoot(file, projectRoot)) continue;
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
        var isFetch = step.Type is "web_fetch";
        var target = step.Url ?? step.Path ?? "";
        var query = step.Query ?? "";
        try
        {
            var client = _clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            if (isFetch || (!string.IsNullOrWhiteSpace(target) && Uri.TryCreate(target, UriKind.Absolute, out _)))
            {
                var url = Uri.TryCreate(target, UriKind.Absolute, out var pu) ? pu : new Uri(target);
                var resp = await client.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();
                var ct2 = resp.Content.Headers.ContentType?.MediaType ?? "text/plain";
                if (ct2.Contains("html")) body = Regex.Replace(body, "<[^>]+>", " ");
                result["status"] = "done"; result["url"] = url.ToString();
                result["output"] = $"HTTP {(int)resp.StatusCode}\n{body.Trim()}";
            }
            else
            {
                var search = !string.IsNullOrWhiteSpace(query) ? query : target;
                if (string.IsNullOrWhiteSpace(search)) { result["status"] = "error"; result["error"] = "web_search requires query"; return; }
                var (searchOut, _) = await WebSearchAsync(search, CancellationToken.None);
                result["status"] = "done"; result["query"] = search; result["output"] = searchOut;
            }
        }
        catch (Exception ex) { result["status"] = "error"; result["error"] = ex.Message; }
    }

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
            if (!AgentUtilities.IsPathUnderRoot(targetPath, projectRoot))
            { foreach (var _ in fileEdits) results.Add(new EditResult { Path = filePath, Status = "skipped", Error = "Path outside root" }); continue; }
            string content = "";
            var fileExists = System.IO.File.Exists(targetPath);
            if (fileExists) content = await System.IO.File.ReadAllTextAsync(targetPath, Encoding.UTF8);
            else if (fileEdits.Any(e => !string.IsNullOrEmpty(e.OldString)))
            { foreach (var e in fileEdits) results.Add(new EditResult { Path = filePath, Status = "skipped", Error = "File does not exist" }); continue; }
            var hasError = false;
            foreach (var edit in fileEdits)
            {
                var ur = GetUnsafeEditPayloadReason(edit.OldString, edit.NewString ?? "");
                if (ur != null) { results.Add(new EditResult { Path = filePath, Status = "error", Error = ur }); hasError = true; break; }
                if (!fileExists && string.IsNullOrEmpty(edit.OldString)) { content = edit.NewString ?? ""; continue; }
                if (string.IsNullOrEmpty(edit.OldString)) { content += edit.NewString ?? ""; continue; }
                var (ok, newContent, err, snippet) = TryReplaceSafe(content, edit.OldString, edit.NewString ?? "");
                if (!ok)
                {
                    var fullErr = err;
                    if (!string.IsNullOrEmpty(snippet)) fullErr += $". Nearby: {snippet}";
                    results.Add(new EditResult { Path = filePath, Status = "error", Error = fullErr });
                    hasError = true; break;
                }
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

    // ═══════════════════════════════════════════════════════════════════════
    //  BUILD CHECK + SELF-IMPROVING
    // ═══════════════════════════════════════════════════════════════════════

    private async Task RunSmartBuildCheck(string projectRoot, string buildCmd, bool emitSse, CancellationToken ct)
    {
        const string systemPrompt = @"You are a build checker. Analyze the build output.
Output ONLY valid JSON (no markdown):
{""decision"": ""done""|""command""|""ask_user"", ""summary"": ""brief"", ""command"": ""cmd if needed"", ""userQuestion"": ""question if needed""}
done = build OK; command = run this to fix; ask_user = need input";

        _terminal.Start();
        await EmitLog(emitSse, "info", $"Build check: {buildCmd}", ct: ct);
        var iteration = 0; const int maxIter = 5;

        while (iteration < maxIter)
        {
            iteration++;
            var beforeLen = _terminal.ReadAll().Length;
            await _terminal.SendCommandAsync(buildCmd, projectRoot);
            var prevLen = beforeLen;
            for (var i = 0; i < 30; i++) { await Task.Delay(500); var cl = _terminal.ReadAll().Length; if (cl == prevLen) break; prevLen = cl; }
            var output = _terminal.ReadAll();
            var fresh = beforeLen < output.Length ? output[beforeLen..] : output;

            var userPrompt = $"Build command: {buildCmd}\nOutput:\n```\n{fresh}\n```\nIteration: {iteration}/{maxIter}";
            var (raw, err) = await CallLlmRawText(systemPrompt, userPrompt, ct);
            if (string.IsNullOrWhiteSpace(raw)) { await EmitLog(emitSse, "warn", $"Build check LLM failed: {err}", ct: ct); break; }

            var decision = ParseBuildCheckResponse(raw);
            if (decision == null) { await EmitLog(emitSse, "warn", "Could not parse build check response", ct: ct); break; }

            switch (decision.Decision)
            {
                case "done": await EmitLog(emitSse, "success", $"Build OK: {decision.Summary}", ct: ct); return;
                case "command":
                    if (!string.IsNullOrWhiteSpace(decision.Command))
                    { await EmitLog(emitSse, "info", $"Build fix: {decision.Command}", ct: ct); await _terminal.SendCommandAsync(decision.Command, projectRoot); await Task.Delay(2000); }
                    continue;
                case "ask_user":
                    await EmitLog(emitSse, "info", $"Build needs user input: {decision.Summary}", ct: ct);
                    return;
                default: return;
            }
        }
        await EmitLog(emitSse, "warn", $"Build check inconclusive after {maxIter} iterations", ct: ct);
    }

    private static BuildCheckDecision? ParseBuildCheckResponse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var json = raw.Trim();
        if (json.StartsWith("```")) { var m = Regex.Match(json, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) json = m.Groups[1].Value.Trim(); }
        try { return JsonSerializer.Deserialize<BuildCheckDecision>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch
        {
            var rep = AgentUtilities.RepairJsonString(json);
            if (rep != null)
                try { return JsonSerializer.Deserialize<BuildCheckDecision>(rep, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
                catch { }
        }
        return null;
    }

    private async Task RunSelfImprovingPipeline(
        string prompt, string projectRoot, List<object> allSteps,
        AgentPlan? plan, bool complete, bool editsApplied)
    {
        var filePath = Path.Combine(projectRoot, "improvementdata.json");
        List<JsonElement> features = new();
        if (System.IO.File.Exists(filePath))
        {
            try
            {
                var ex = await System.IO.File.ReadAllTextAsync(filePath);
                var root = JsonSerializer.Deserialize<JsonElement>(ex);
                if (root.TryGetProperty("features", out var feats) && feats.ValueKind == JsonValueKind.Array)
                    features = feats.EnumerateArray().ToList();
            }
            catch { }
        }
        var now = DateTime.UtcNow.ToString("o");
        var filesEdited = ExtractFilesEdited(allSteps);
        var filePaths = filesEdited.Select(f =>
        {
            if (f is Dictionary<string, object?> d && d.TryGetValue("path", out var p) && p is string ps) return ps;
            if (f is JsonElement je && je.TryGetProperty("path", out var pp)) return pp.GetString() ?? "";
            return "";
        }).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();

        var entry = new Dictionary<string, object?> { ["description"] = plan?.Summary ?? "No summary", ["complete"] = complete && editsApplied, ["date"] = now };
        var existIdx = features.FindIndex(f => f.TryGetProperty("feature", out var ft) && ft.GetString() == prompt);
        Dictionary<string, object?> featureEntry;
        List<object> improvements;
        if (existIdx >= 0)
        {
            featureEntry = JsonSerializer.Deserialize<Dictionary<string, object?>>(features[existIdx].GetRawText()) ?? new();
            improvements = new List<object>();
            featureEntry["lastUpdated"] = now;
        }
        else
        {
            featureEntry = new Dictionary<string, object?> { ["feature"] = prompt, ["files"] = filePaths, ["improvements"] = new List<object>(), ["lastUpdated"] = now };
            improvements = new List<object>();
        }
        improvements.Add(entry); featureEntry["improvements"] = improvements;
        if (existIdx >= 0) features[existIdx] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(featureEntry));
        else features.Add(JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(featureEntry)));

        var output = new Dictionary<string, object?> { ["features"] = features.Select(f => JsonSerializer.Deserialize<Dictionary<string, object?>>(f.GetRawText())).ToList() };
        var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        await System.IO.File.WriteAllTextAsync(filePath, json);
        await EmitLog(true, "info", $"Self-improving data written for: {prompt}");
    }
}