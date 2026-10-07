using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Weaver.Services;
using static Weaver.Services.AgentTokenMetrics;
using static Weaver.Services.AgentEditHeuristics;
using static Weaver.Services.AgentPlanParsing;
using static Weaver.Services.AgentMethodInventory;
using static Weaver.Services.AgentProjectUtilities;
using static Weaver.Services.AgentDiscovery;
using static Weaver.Services.AgentTextUtilities;
using static Weaver.Services.AgentCodeFormatting;
using static Weaver.Services.AgentSkeleton;
using static Weaver.Services.AgentDiffUtilities;
using static Weaver.Services.AgentJsonUtilities;
using Weaver;
namespace Weaver.Controllers;

partial class AgentController
{
    /// <summary>
    /// Persists the run's harvested _web_search/_web_fetch outputs onto the card as a
    /// card-level "_webResults" array (separate from the plan items, so it survives plan
    /// rebuilds). A replayed run (restart) skips completed web steps without re-executing
    /// them, so without this store the discovery context would rebuild EMPTY and lose the
    /// previous run's web data — the fix seeds the replay's results + context from this
    /// store. Entries carry type/status/query-or-url/output; outputs are capped at 20k
    /// (the same cap the discovery-context append applies) and merged idempotently by
    /// (type|query|url) so a re-executed step replaces its own entry instead of duplicating.
    /// </summary>
    private async Task PersistWebResultsToCardAsync(string? cardId, List<object> allResults)
    {
        if (string.IsNullOrWhiteSpace(cardId)) return;
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
                    var existing = cardObj["_webResults"] as JsonArray ?? new JsonArray();
                    var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var existingByIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < existing.Count; i++)
                    {
                        if (existing[i] is not JsonObject eo) continue;
                        var key = (eo["type"]?.GetValue<string>() ?? "") + "|" +
                                  (eo["query"]?.GetValue<string>() ?? eo["url"]?.GetValue<string>() ?? "");
                        if (key == "|") continue;
                        existingKeys.Add(key);
                        existingByIdx[key] = i;
                    }
                    var changed = false;
                    foreach (var r in allResults.OfType<Dictionary<string, object?>>())
                    {
                        var type = r.GetValueOrDefault("type")?.ToString();
                        if (type is not ("_web_search" or "_web_fetch")) continue;
                        if (r.GetValueOrDefault("status")?.ToString() != "done") continue;
                        var output = r.GetValueOrDefault("output")?.ToString();
                        if (string.IsNullOrWhiteSpace(output) || output.Length <= 80) continue;
                        var isSearch = type == "_web_search";
                        var query = isSearch
                            ? r.GetValueOrDefault("query")?.ToString() ?? ""
                            : r.GetValueOrDefault("url")?.ToString() ?? "";
                        var key = type + "|" + query;
                        var capped = output.Length > 20000 ? output[..20000] + "…" : output;
                        var entry = new JsonObject
                        {
                            ["type"] = type,
                            ["status"] = "done",
                            [isSearch ? "query" : "url"] = query,
                            ["output"] = capped
                        };
                        if (existingByIdx.TryGetValue(key, out var idx))
                            existing[idx] = entry;
                        else
                        {
                            existing.Add(entry);
                            existingByIdx[key] = existing.Count - 1;
                        }
                        changed = true;
                    }
                    if (changed)
                    {
                        cardObj["_webResults"] = existing;
                        var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                        await _boardData.SaveRawAsync(saved);
                    }
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to persist web results to boarddata", new { cardId, error = ex.Message });
        }
    }

    private async Task PersistBoardDataPlanStepAsync(string? cardId, int planItemIndex, bool emitSse, CancellationToken ct,
        List<string>? diffs = null, string? projectRoot = null)
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
                        if (diffs != null && diffs.Count > 0)
                            stepObj["diffs"] = new JsonArray(diffs.Select(d => JsonValue.Create(d)).ToArray());
                        // Per-step ground truth verification: each expectation carries the
                        // file + anchor it was computed from — re-read the CURRENT file and
                        // mark verified=true/false so the plan item shows whether the step's
                        // own expected outcome held. Root comes from the caller (the run's
                        // projectRoot) or falls back to the configured workspace root;
                        // unresolvable/absent files are skipped silently (entry stays
                        // unverified rather than claiming a miss).
                        if (stepObj["groundTruth"] is JsonArray gtArray && gtArray.Count > 0)
                        {
                            var rootDir = string.IsNullOrWhiteSpace(projectRoot) ? null : Path.GetFullPath(projectRoot);
                            if (rootDir == null)
                            {
                                try { rootDir = AgentProjectUtilities.ResolveWorkspaceRoot(_config, _env); }
                                catch { rootDir = null; }
                            }
                            if (rootDir != null)
                            {
                                foreach (var gtNode in gtArray)
                                {
                                    if (gtNode is not JsonObject gt) continue;
                                    var gtFile = gt["file"]?.GetValue<string>();
                                    var anchor = gt["anchor"]?.GetValue<string>();
                                    if (string.IsNullOrWhiteSpace(gtFile) || string.IsNullOrWhiteSpace(anchor)) continue;
                                    try
                                    {
                                        var full = Path.GetFullPath(Path.Combine(rootDir, gtFile.Replace('/', Path.DirectorySeparatorChar)));
                                        if (!System.IO.File.Exists(full)) continue;
                                        var content = AgentTextUtilities.NormalizeLineEndings(System.IO.File.ReadAllText(full));
                                        // Normalize paren spacing on BOTH sides: the apply pipeline
                                        // rewrites the whole changed line (`button (` → `button(`), so
                                        // the raw anchor would false-negative on a landing edit.
                                        gt["verified"] = AgentTextUtilities.FindAnchorOffset(
                                            AgentTextUtilities.NormalizeParenSpacing(content),
                                            AgentTextUtilities.NormalizeParenSpacing(anchor)) >= 0;
                                    }
                                    catch { }
                                }
                            }
                        }
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
            await EmitLog(true, "error", "Failed to persist full plan to boarddata - halting to prevent data loss", new { cardId, error = ex.Message });
            throw;
        }
    }
    /// <summary>
    /// Appends a REJECTED plan step (with the rejection reason) to the card's
    /// persisted _plan.items so web-gate vetoes survive a reload. The record carries
    /// status="rejected" + error; PersistBoardDataPlanAsync preserves such records
    /// across plan rebuilds.
    /// </summary>
    private async Task PersistRejectedPlanStepAsync(string? cardId, PlanStep step, string? error, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardId) || step == null)
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
                    if (cardObj["_plan"] is not JsonObject planObj)
                    {
                        // A card can be rejected on its very first proposal — create the
                        // plan container so the rejection still lands on the card.
                        planObj = new JsonObject { ["items"] = new JsonArray() };
                        cardObj["_plan"] = planObj;
                    }
                    if (planObj["items"] is not JsonArray items)
                    {
                        items = new JsonArray();
                        planObj["items"] = items;
                    }
                    items.Add(new JsonObject
                    {
                        ["index"] = items.Count,
                        ["file"] = step.File,
                        ["change"] = step.Change,
                        ["line"] = step.LineNumber,
                        ["done"] = false,
                        ["status"] = "rejected",
                        ["error"] = error ?? ""
                    });
                    var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await _boardData.SaveRawAsync(saved);
                    if (emitSse)
                    {
                        await SendSse(Response, "refresh", new
                        {
                            target = "boarddata",
                            reason = "plan-step-rejected",
                            cardId,
                            planItemIndex = items.Count - 1
                        }, ct);
                    }
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(emitSse, "warn", "Failed to persist rejected plan step to card", new { cardId, error = ex.Message });
        }
    }

    /// <summary>
    /// Persists the run's COMPUTED GROUND TRUTH onto the card as _groundTruth — the
    /// deterministic expectations the post-execution verification checks the run against
    /// (e.g. "Newly created CSS class '.flight-detail-body' must be referenced in the
    /// template") — and emits a live 'groundTruth' SSE event so the card shows the
    /// known-correct answer DURING the run. Set-on-fire semantics: once an expectation is
    /// computed it stays on the card, so a human can see what the run was being checked
    /// against even after a repair pass satisfies it. Display-only: failures are logged,
    /// never fatal (unlike plan persistence, which halts to prevent data loss).
    /// </summary>
    private async Task PublishGroundTruthAsync(string cardId, List<string> items, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardId) || items.Count == 0)
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
                    cardObj["_groundTruth"] = new JsonArray(items.Select(i => JsonValue.Create(i)).ToArray());
                    var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await _boardData.SaveRawAsync(saved);
                    if (emitSse)
                    {
                        await SendSse(Response, "groundTruth", new { items, cardId }, ct);
                    }
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(emitSse, "warn", "Failed to persist ground truth to card", new { cardId, error = ex.Message });
        }
    }
    /// <summary>
    /// Persists the run's FINAL VERIFICATION VERDICT onto the card as _verification —
    /// { complete, reason, at } — so the reason the run was (or wasn't) verified complete
    /// is visible on the card after the run instead of only in the log. Mirrors
    /// <see cref="PublishGroundTruthAsync"/>: display-only, failures are logged never
    /// fatal, and it emits a live 'verification' SSE event so the active card updates
    /// immediately. The reason is the final <c>verified_complete</c> entry's text when the
    /// run completed; for an incomplete run it is the deterministic fallback passed in.
    /// </summary>
    private async Task PublishVerificationAsync(string cardId, bool complete, string? reason, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardId))
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
                    var resolvedReason = string.IsNullOrWhiteSpace(reason)
                        ? (complete
                            ? "Run completed and passed post-execution verification."
                            : "Post-execution verification did not pass — review the run log for the remaining issue(s).")
                        : reason;
                    cardObj["_verification"] = new JsonObject
                    {
                        ["complete"] = complete,
                        ["reason"] = resolvedReason,
                        ["at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    };
                    var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await _boardData.SaveRawAsync(saved);
                    if (emitSse)
                    {
                        await SendSse(Response, "verification", new { complete, reason = resolvedReason, cardId }, ct);
                    }
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(emitSse, "warn", "Failed to persist verification to card", new { cardId, error = ex.Message });
        }
    }

    /// <summary>
    /// Persists the up-front dump-vs-build classification onto the card as _taskKind
    /// ("dump" or "build") so a run that short-circuits deterministically is visibly
    /// distinguishable from a script/program run. Mirrors <see cref="PublishVerificationAsync"/>:
    /// display-only (failures are logged, never fatal), survives a reload via boarddata, and
    /// emits a live 'taskKind' SSE event so the card shows the badge DURING the run.
    /// </summary>
    private async Task PublishTaskKindAsync(string cardId, string? taskKind, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardId))
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
                    if (taskKind == null)
                        cardObj.Remove("_taskKind");
                    else
                        cardObj["_taskKind"] = taskKind;
                    var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await _boardData.SaveRawAsync(saved);
                    if (emitSse)
                        await SendSse(Response, "taskKind", new { cardId, taskKind }, ct);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(emitSse, "warn", "Failed to persist task kind to card", new { cardId, error = ex.Message });
        }
    }

    /// <summary>
    /// Persists a DELIVERED live steer onto the card as _steers — a running transcript of
    /// what was injected into the planner, and at which turn — and emits a live
    /// 'steerDelivered' SSE event so the card shows it DURING the run. Mirrors
    /// <see cref="PublishGroundTruthAsync"/>: set-on-fire append semantics (each delivered
    /// steer is appended in order and survives a reload via boarddata), display-only
    /// (failures are logged, never fatal). The turn number is the step the steer became
    /// visible to — the planner turn it was drained before, so a human can correlate
    /// "turn 3 steer" with the turn-3 proposal in the transcript.
    /// </summary>
    private async Task PublishDeliveredSteerAsync(string cardId, string message, int turn, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardId) || string.IsNullOrWhiteSpace(message))
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
                    var steers = cardObj["_steers"] as JsonArray ?? new JsonArray();
                    steers.Add(new JsonObject
                    {
                        ["turn"] = turn,
                        ["message"] = message,
                        ["at"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
                    });
                    cardObj["_steers"] = steers;
                    var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await _boardData.SaveRawAsync(saved);
                    if (emitSse)
                    {
                        var items = steers.OfType<JsonObject>().Select(s => new
                        {
                            turn = s["turn"]?.GetValue<int>() ?? 0,
                            message = s["message"]?.GetValue<string>() ?? "",
                            at = s["at"]?.GetValue<string>() ?? ""
                        }).ToList();
                        await SendSse(Response, "steerDelivered", new { items, cardId }, ct);
                    }
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(emitSse, "warn", "Failed to persist delivered steer to card", new { cardId, error = ex.Message });
        }
    }
    private async Task<string> PostEditStyleFixAsync(
        string fullPath, string relPath, string content, string appliedNewStr,
        bool emitSse, CancellationToken ct)
    {
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        if (ext == ".html" || ext == ".htm")
            return content;
        var hasSpacingIssue = false;
        var needleLines = appliedNewStr.Split('\n');
        var fileLines = content.Split('\n');
        var excerptStart = -1;
        var excerptEnd = -1;
        foreach (var nLine in needleLines)
        {
            var trimmed = nLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            for (var i = 0; i < fileLines.Length; i++)
            {
                if (fileLines[i].Contains(trimmed, StringComparison.Ordinal))
                {
                    if (excerptStart < 0 || i < excerptStart) excerptStart = i;
                    if (i > excerptEnd) excerptEnd = i;
                    if (Regex.IsMatch(fileLines[i], @"\(\w+\s*[+\-*/%]\d") ||
                        Regex.IsMatch(fileLines[i], @"\d\s*[+\-*/%]\s*\d") ||
                        Regex.IsMatch(fileLines[i], @"(?<![=!<>])=(?!=)\d") ||
                        Regex.IsMatch(fileLines[i], @"[\w\)\]]\s*[+*/%<>]\s*\d") ||
                        Regex.IsMatch(fileLines[i], @"\d\s*[+\-*/%<>]\s*[\w\(]") ||
                        Regex.IsMatch(fileLines[i], @"[\w\)]\s*[<>]\s*[\w\(]") ||
                        Regex.IsMatch(fileLines[i], @"[\w\)]\s*-\s*\w") && !fileLines[i].Contains("-="))
                    {
                        hasSpacingIssue = true;
                    }
                    break;
                }
            }
        }
        if (!hasSpacingIssue || excerptStart < 0)
            return content;
        var contextWindowStart = Math.Max(0, excerptStart - 3);
        var contextWindowEnd = Math.Min(fileLines.Length, excerptEnd + 4);
        var excerpt = string.Join("\n", fileLines[contextWindowStart..contextWindowEnd]);
        var sysPrompt = "You are a meticulous code formatter. Fix spacing issues in the code excerpt below: " +
                        "ensure proper spacing around operators (+, -, *, /, %, =, etc.) and colons in " +
                        "TypeScript/JavaScript/CSS. Output ONLY a JSON object with an array of fixes: " +
                        "{\"fixes\":[{\"oldString\":\"...\",\"newString\":\"...\"}]}. " +
                        "Each fix must be an exact substring from the excerpt. Do NOT change logic or add/remove code. " +
                        "CRITICAL RULE: NEVER add a space between a function/method name and its opening parenthesis. " +
                        "`delete(optionsFile)` is CORRECT; `delete (optionsFile)` is WRONG. " +
                        "`myFunc()` is CORRECT; `myFunc ()` is WRONG. " +
                        "DO NOT add spaces after keywords if they are immediately followed by '(' for a function call. " +
                        "DO NOT modify text inside HTML attribute values.";
        var userMsg = $"### FILE ###\n{relPath}\n\n### EXCERPT WITH SPACING ISSUES ###\n```\n{excerpt}\n```\n\n" +
                      "Fix spacing issues. Return JSON with oldString/newString pairs.";
        var (raw, _, error) = await CallLlmRawStreaming(sysPrompt, userMsg, emitSse, ct,
            requestTimeout: _infiniteTimeout, maxTokens: 1024);
        if (string.IsNullOrWhiteSpace(raw))
            return content;
        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```")) cleaned = cleaned.TrimStart('`').Trim();
            var fb = cleaned.IndexOf('{');
            var lb = cleaned.LastIndexOf('}');
            if (fb >= 0 && lb > fb) cleaned = cleaned[fb..(lb + 1)];
            using var doc = JsonDocument.Parse(cleaned);
            if (!doc.RootElement.TryGetProperty("fixes", out var fixesArr) || fixesArr.ValueKind != JsonValueKind.Array)
                return content;
            var fixedContent = content;
            var fixCount = 0;
            foreach (var fix in fixesArr.EnumerateArray())
            {
                var oldStr = fix.TryGetProperty("oldString", out var oEl) ? oEl.GetString() : null;
                var newStr = fix.TryGetProperty("newString", out var nEl) ? nEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(oldStr) || string.IsNullOrWhiteSpace(newStr) || oldStr == newStr)
                { continue; }
                if (Regex.IsMatch(newStr, @"\w\s+\(")) { continue; }
                var idx = fixedContent.IndexOf(oldStr, StringComparison.Ordinal);
                if (idx < 0) { continue; }
                var secIdx = fixedContent.IndexOf(oldStr, idx + oldStr.Length, StringComparison.Ordinal);
                if (secIdx >= 0) { continue; }
                var matchLine = fixedContent[..idx].Count(c => c == '\n');
                if (matchLine < contextWindowStart || matchLine > contextWindowEnd) { continue; }
                fixedContent = fixedContent[..idx] + newStr + fixedContent[(idx + oldStr.Length)..];
                fixCount++;
            }
            if (fixCount > 0)
            {
                var fixedLines = fixedContent.Split('\n');
                var parenChanged = false;
                for (var i = 0; i < fixedLines.Length; i++)
                {
                    if (i < contextWindowStart || i > contextWindowEnd) continue;
                    var beforeLine = fixedLines[i];
                    var afterLine = Regex.Replace(beforeLine, @"\b(\w+)\s+\(", "$1(");
                    if (afterLine != beforeLine) { fixedLines[i] = afterLine; parenChanged = true; }
                }
                if (parenChanged) fixedContent = string.Join("\n", fixedLines);
                await System.IO.File.WriteAllTextAsync(fullPath, fixedContent, Encoding.UTF8, ct);
                await EmitLog(emitSse, "info",
                    $"Style fix: applied {fixCount} spacing fix(es) in {relPath}", ct: ct);
            }
            return fixedContent;
        }
        catch
        {
            return content;
        }
    }
    private static string StripEditKnowledgeHeader(string discoveryContext)
    {
        if (string.IsNullOrWhiteSpace(discoveryContext)) return discoveryContext;
        var priorKnowledgeIdx = discoveryContext.IndexOf("### PRIOR EDIT KNOWLEDGE FOR THIS PROJECT ###", StringComparison.Ordinal);
        var relevantKnowledgeIdx = discoveryContext.IndexOf("### EDIT KNOWLEDGE (relevant to this file/task) ###", StringComparison.Ordinal);
        var headerIdx = priorKnowledgeIdx >= 0 ? priorKnowledgeIdx :
                        relevantKnowledgeIdx >= 0 ? relevantKnowledgeIdx : -1;
        if (headerIdx < 0) return discoveryContext;
        var afterHeader = headerIdx + 50;
        var nextMainSectionIdx = discoveryContext.IndexOf("\n### ", afterHeader, StringComparison.Ordinal);
        if (nextMainSectionIdx < 0)
            return discoveryContext;
        return discoveryContext.Substring(nextMainSectionIdx + 1).TrimStart();
    }
    private string? RepairBrokenCodeWithLadder(string candidateCode, string? oldStr, string fileContent, int targetLine, string change)
    {
        if (string.IsNullOrWhiteSpace(candidateCode))
            return null;
        var normalized = AgentTextUtilities.NormalizeLineEndings(candidateCode).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;
        var braceAppend = TryAutoAppendMissingClosingBraces(normalized);
        if (!string.IsNullOrWhiteSpace(braceAppend))
            return braceAppend;
        var signatureSplice = TrySignatureSpliceRepair(normalized, oldStr);
        if (!string.IsNullOrWhiteSpace(signatureSplice))
            return signatureSplice;
        var fuzzyAnchor = TryFuzzyAnchorRepair(normalized, oldStr, fileContent, targetLine, change);
        return fuzzyAnchor;
    }
    private static string? TryAutoAppendMissingClosingBraces(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || AgentEditHeuristics.IsBraceBalanced(code))
            return null;
        var depth = 0;
        var inSingle = false;
        var inDouble = false;
        var inTemplate = false;
        var inLineComment = false;
        var inBlockComment = false;
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            var n = i + 1 < code.Length ? code[i + 1] : '\0';
            if (inLineComment && c == '\n') { inLineComment = false; continue; }
            if (inBlockComment && c == '*' && n == '/') { inBlockComment = false; i++; continue; }
            if (inBlockComment || inLineComment) continue;
            if (!inSingle && !inDouble && !inTemplate)
            {
                if (c == '/' && n == '/') { inLineComment = true; i++; continue; }
                if (c == '/' && n == '*') { inBlockComment = true; i++; continue; }
            }
            if (c == '"' && !inSingle && !inTemplate) { inDouble = !inDouble; continue; }
            if (c == '\'' && !inDouble && !inTemplate) { inSingle = !inSingle; continue; }
            if (c == '`' && !inSingle && !inDouble) { inTemplate = !inTemplate; continue; }
            if (c == '\\' && (inSingle || inDouble || inTemplate)) { i++; continue; }
            if (!inSingle && !inDouble && !inTemplate)
            {
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth < 0)
                        return null;
                }
            }
        }
        if (depth <= 0)
            return null;
        var suffix = new string('}', depth);
        return code.TrimEnd() + Environment.NewLine + suffix;
    }
    private static string? TrySignatureSpliceRepair(string candidateCode, string? oldStr)
    {
        if (string.IsNullOrWhiteSpace(candidateCode) || string.IsNullOrWhiteSpace(oldStr) || candidateCode.Contains('{') || candidateCode.Contains('}'))
            return null;
        var signatureStart = oldStr.IndexOf('{');
        if (signatureStart < 0)
            signatureStart = oldStr.IndexOf('(');
        if (signatureStart < 0)
            return null;
        var signature = oldStr[..signatureStart].Trim();
        if (string.IsNullOrWhiteSpace(signature))
            return null;
        var trimmedCandidate = candidateCode.Trim();
        if (trimmedCandidate.StartsWith(signature, StringComparison.OrdinalIgnoreCase) ||
            trimmedCandidate.StartsWith("function ", StringComparison.OrdinalIgnoreCase) ||
            trimmedCandidate.StartsWith("class ", StringComparison.OrdinalIgnoreCase))
            return null;
        var body = trimmedCandidate;
        return signature + Environment.NewLine + body + Environment.NewLine + "}";
    }
    private static string? TryFuzzyAnchorRepair(string candidateCode, string? oldStr, string fileContent, int targetLine, string change)
    {
        if (string.IsNullOrWhiteSpace(candidateCode) || string.IsNullOrWhiteSpace(oldStr) || string.IsNullOrWhiteSpace(fileContent))
            return null;
        var candidateTrim = candidateCode.Trim();
        if (candidateTrim.Contains('{') || candidateTrim.Contains('}'))
            return null;
        var oldLines = oldStr
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        if (oldLines.Count == 0)
            return null;
        var anchor = oldLines.FirstOrDefault(l => l.Length > 6 && !l.StartsWith("//", StringComparison.Ordinal))
            ?? oldLines.First();
        var anchorIdx = fileContent.IndexOf(anchor, StringComparison.Ordinal);
        if (anchorIdx < 0)
            return null;
        var prefix = fileContent[..anchorIdx];
        var lineStart = prefix.LastIndexOf('\n') + 1;
        var lineNo = prefix.Count(c => c == '\n') + 1;
        var lineOffset = Math.Max(0, targetLine - lineNo);
        var signaturePrefix = oldStr.TakeWhile(c => c != '{' && c != '}' && c != ';').ToArray();
        var signature = new string(signaturePrefix).Trim();
        if (string.IsNullOrWhiteSpace(signature))
            return null;
        var repaired = signature + Environment.NewLine + candidateTrim + Environment.NewLine + "}";
        return repaired;
    }
    private async Task<string?> TryFixBracesWithPrettierAsync(string relPath, string newStr, CancellationToken ct)
    {
        if (!CodeFormatterService.CanFormat(relPath))
            return null;
        var formatted = await CodeFormatterService.FormatAsync(relPath, newStr, ct);
        if (string.IsNullOrWhiteSpace(formatted) || formatted == newStr)
            return null;
        return AgentEditHeuristics.IsBraceBalanced(formatted) ? formatted : null;
    }
    private string AutoFormatEditedRegion(string content, string appliedNewStr)
    {
        if (string.IsNullOrWhiteSpace(appliedNewStr) || string.IsNullOrWhiteSpace(content))
            return content;
        var fileLines = content.Split('\n');
        var needleSet = appliedNewStr.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l) && l.Length >= 3)
            .ToHashSet(StringComparer.Ordinal);
        var longNeedles = needleSet.Where(n => n.Length >= 12).ToList();
        var editedLineIndices = new HashSet<int>();
        var firstExact = -1;
        var lastExact = -1;
        for (var i = 0; i < fileLines.Length; i++)
        {
            var trimmed = fileLines[i].Trim();
            if (trimmed.Length < 3) continue;
            if (needleSet.Contains(trimmed))
            {
                editedLineIndices.Add(i);
                if (firstExact < 0) firstExact = i;
                lastExact = i;
                continue;
            }
        }
        if (longNeedles.Count > 0 && firstExact >= 0)
        {
            var windowStart = Math.Max(0, firstExact - 30);
            var windowEnd = Math.Min(fileLines.Length, lastExact + 30);
            for (var i = windowStart; i < windowEnd; i++)
            {
                var trimmed = fileLines[i].Trim();
                if (trimmed.Length < 3) continue;
                foreach (var needle in longNeedles)
                {
                    if (trimmed.Contains(needle, StringComparison.Ordinal)) { editedLineIndices.Add(i); break; }
                }
            }
        }
        if (editedLineIndices.Count == 0) return content;
        var sb = new StringBuilder(content.Length + 16);
        var inStringDouble = false;
        var inStringSingle = false;
        var inTemplate = false;
        var inVerbatimString = false;
        var inLineComment = false;
        var inBlockComment = false;
        var changed = false;
        for (var i = 0; i < fileLines.Length; i++)
        {
            var line = fileLines[i];
            var formattedLine = FormatLineWithState(line, ref inStringDouble, ref inStringSingle, ref inTemplate, ref inVerbatimString, ref inLineComment, ref inBlockComment);
            if (editedLineIndices.Contains(i))
            {
                if (formattedLine != line) changed = true;
                sb.Append(formattedLine);
            }
            else
            {
                sb.Append(line);
            }
            if (i < fileLines.Length - 1) sb.Append('\n');
        }
        if (!changed) return content;
        var result = sb.ToString();
        var resultLines = result.Split('\n');
        var parensChanged = false;
        for (var i = 0; i < resultLines.Length; i++)
        {
            if (!editedLineIndices.Contains(i)) continue;
            var fixedLine = FixStrayClosingParens(resultLines, i);
            if (fixedLine != resultLines[i])
            {
                resultLines[i] = fixedLine;
                parensChanged = true;
            }
        }
        return parensChanged ? string.Join("\n", resultLines) : result;
    }
    private string FormatLineWithState(string line,
    ref bool inStringDouble, ref bool inStringSingle, ref bool inTemplate,
    ref bool inVerbatimString, ref bool inLineComment, ref bool inBlockComment)
    {
        var sb = new StringBuilder(line.Length + 4);
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            var next = (i + 1 < line.Length) ? line[i + 1] : '\0';
            var prev = (i > 0) ? line[i - 1] : '\0';
            if (inBlockComment)
            {
                sb.Append(c);
                if (c == '*' && next == '/')
                {
                    sb.Append(next);
                    i += 2;
                    inBlockComment = false;
                    continue;
                }
                i++;
                continue;
            }
            if (inLineComment)
            {
                sb.Append(c);
                i++;
                continue;
            }
            if (inVerbatimString)
            {
                sb.Append(c);
                if (c == '"')
                {
                    if (next == '"')
                    {
                        sb.Append(next);
                        i += 2;
                        continue;
                    }
                    else
                    {
                        inVerbatimString = false;
                        i++;
                        continue;
                    }
                }
                if (char.IsLetter(c))
                {
                    var rest = line.Substring(i);
                    var match = Regex.Match(rest, @"^(INTERVAL|MINUTE|HOUR|DAY|MONTH|YEAR|SECOND|MICROSECOND|WEEK|QUARTER|LIMIT|OFFSET|TOP|SELECT|DELETE|UPDATE|INSERT|FROM|WHERE|JOIN|AND|OR|NOT|IN|ON|AS|BY|ORDER|GROUP|HAVING|UNION|INTO|VALUES|SET|CREATE|TABLE|ALTER|DROP|CASE|WHEN|THEN|ELSE|END|EXISTS|DISTINCT|WITH|ALL)\d", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        sb.Append(match.Value.Substring(0, match.Value.Length - 1));
                        sb.Append(' ');
                        sb.Append(match.Value[match.Value.Length - 1]);
                        i += match.Value.Length;
                        continue;
                    }
                    match = Regex.Match(rest, @"^(SELECT|DELETE|DISTINCT|ALL)\*", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        sb.Append(match.Value.Substring(0, match.Value.Length - 1));
                        sb.Append(" *");
                        i += match.Value.Length;
                        continue;
                    }
                    match = Regex.Match(rest, @"^(SELECT|FROM|WHERE|JOIN|INNER|LEFT|RIGHT|OUTER|AND|OR|NOT|IN|BETWEEN|LIKE|IS|ON|AS|BY|ORDER|GROUP|HAVING|LIMIT|OFFSET|UNION|INSERT|INTO|VALUES|UPDATE|SET|DELETE|CREATE|TABLE|ALTER|DROP|CASE|WHEN|THEN|ELSE|END|EXISTS|DISTINCT|WITH)\(", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        sb.Append(match.Value.Substring(0, match.Value.Length - 1));
                        sb.Append(" (");
                        i += match.Value.Length;
                        continue;
                    }
                }
                i++;
                continue;
            }
            if (inStringDouble || inStringSingle || inTemplate)
            {
                if (char.IsLetter(c))
                {
                    var rest = line.Substring(i);
                    var match = Regex.Match(rest, @"^(INTERVAL|MINUTE|HOUR|DAY|MONTH|YEAR|SECOND|MICROSECOND|WEEK|QUARTER|LIMIT|OFFSET|TOP|SELECT|DELETE|UPDATE|INSERT|FROM|WHERE|JOIN|AND|OR|NOT|IN|ON|AS|BY|ORDER|GROUP|HAVING|UNION|INTO|VALUES|SET|CREATE|TABLE|ALTER|DROP|CASE|WHEN|THEN|ELSE|END|EXISTS|DISTINCT|WITH|ALL)\d", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        sb.Append(match.Value.Substring(0, match.Value.Length - 1));
                        sb.Append(' ');
                        sb.Append(match.Value[match.Value.Length - 1]);
                        i += match.Value.Length;
                        continue;
                    }
                    match = Regex.Match(rest, @"^(SELECT|DELETE|DISTINCT|ALL)\*", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        sb.Append(match.Value.Substring(0, match.Value.Length - 1));
                        sb.Append(" *");
                        i += match.Value.Length;
                        continue;
                    }
                    match = Regex.Match(rest, @"^(SELECT|FROM|WHERE|JOIN|INNER|LEFT|RIGHT|OUTER|AND|OR|NOT|IN|BETWEEN|LIKE|IS|ON|AS|BY|ORDER|GROUP|HAVING|LIMIT|OFFSET|UNION|INSERT|INTO|VALUES|UPDATE|SET|DELETE|CREATE|TABLE|ALTER|DROP|CASE|WHEN|THEN|ELSE|END|EXISTS|DISTINCT|WITH)\(", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        sb.Append(match.Value.Substring(0, match.Value.Length - 1));
                        sb.Append(" (");
                        i += match.Value.Length;
                        continue;
                    }
                }
                sb.Append(c);
                if (c == '\\' && next != '\0')
                {
                    sb.Append(next);
                    i += 2;
                    continue;
                }
                if (inStringDouble && c == '"') inStringDouble = false;
                else if (inStringSingle && c == '\'') inStringSingle = false;
                else if (inTemplate && c == '`') inTemplate = false;
                i++;
                continue;
            }
            if (c == '/' && next == '/')
            {
                inLineComment = true;
                sb.Append(c);
                i++;
                continue;
            }
            if (c == '/' && next == '*')
            {
                inBlockComment = true;
                sb.Append(c);
                i++;
                continue;
            }
            if (c == '@' && next == '"')
            {
                inVerbatimString = true;
                sb.Append(c);
                sb.Append(next);
                i += 2;
                continue;
            }
            if (c == '"') { inStringDouble = true; sb.Append(c); i++; continue; }
            if (c == '\'') { inStringSingle = true; sb.Append(c); i++; continue; }
            if (c == '`') { inTemplate = true; sb.Append(c); i++; continue; }
            if (c == ',')
            {
                sb.Append(c);
                i++;
                if (i < line.Length)
                {
                    var after = line[i];
                    if (after != ' ' && after != '\t' && after != '\r' && after != '\n'
                        && after != ')' && after != ']' && after != '}')
                    {
                        sb.Append(' ');
                    }
                }
                continue;
            }
            if (c == ':')
            {
                sb.Append(c);
                i++;
                if (i < line.Length)
                {
                    var after = line[i];
                    if (prev != ':' && after != ':'
                        && after != ' ' && after != '\t' && after != '\r' && after != '\n')
                    {
                        sb.Append(' ');
                    }
                }
                continue;
            }
            if (c == ';')
            {
                sb.Append(c);
                i++;
                if (i < line.Length)
                {
                    var after = line[i];
                    if (after != ';' && after != ')'
                        && after != ' ' && after != '\t' && after != '\r' && after != '\n')
                    {
                        sb.Append(' ');
                    }
                }
                continue;
            }
            if (c == '=')
            {
                const string operatorPrevChars = "!<>=+-*/%&|^~?:";
                var isOperatorContext = prev != '\0' && operatorPrevChars.IndexOf(prev) >= 0;
                var nextChar = (i + 1 < line.Length) ? line[i + 1] : '\0';
                var isHtmlAttributeLike = nextChar == '"' || nextChar == '\'' || nextChar == '`';
                if (isHtmlAttributeLike)
                {
                    sb.Append(c);
                    i++;
                    continue;
                }
                if (!isOperatorContext && sb.Length > 0)
                {
                    var lastChar = sb[sb.Length - 1];
                    if (lastChar != ' ' && lastChar != '\t'
                        && (char.IsLetterOrDigit(lastChar) || lastChar == ')' || lastChar == ']'
                            || lastChar == '_' || lastChar == '$'))
                    {
                        sb.Append(' ');
                    }
                }
                sb.Append(c);
                i++;
                if (i < line.Length)
                {
                    var after = line[i];
                    if (after != '=' && after != '>'
                        && after != '"' && after != '\'' && after != '`'
                        && after != ' ' && after != '\t' && after != '\r' && after != '\n')
                    {
                        sb.Append(' ');
                    }
                }
                continue;
            }
            if (c == '?')
            {
                sb.Append(c);
                i++;
                if (i < line.Length)
                {
                    var after = line[i];
                    if (char.IsDigit(after))
                    {
                        sb.Append(' ');
                    }
                }
                continue;
            }
            sb.Append(c);
            i++;
        }
        inLineComment = false;
        return sb.ToString();
    }
    private static string FixStrayClosingParens(string[] fileLines, int idx)
    {
        var line = fileLines[idx];
        if (string.IsNullOrEmpty(line)) return line;
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0) return line;
        char closeCh;
        if (trimmed[0] == ')') closeCh = ')';
        else if (trimmed[0] == ']') closeCh = ']';
        else if (trimmed[0] == '}') closeCh = '}';
        else return line;
        char openCh = closeCh == ')' ? '(' : (closeCh == ']' ? '[' : '{');
        var suffix = trimmed.Substring(1);
        if (!IsSafeCloseSuffix(suffix)) return line;
        var depth = 1;
        var inStrDq = false; var inStrSq = false; var inTmpl = false;
        var inLineCmt = false; var inBlockCmt = false;
        var openerLineIdx = -1;
        for (var li = idx - 1; li >= 0; li--)
        {
            var upLine = fileLines[li];
            if (string.IsNullOrEmpty(upLine))
            {
                inLineCmt = false;
                continue;
            }
            var localInLineCmt = inLineCmt;
            var localInBlockCmt = inBlockCmt;
            var localInDq = inStrDq; var localInSq = inStrSq; var localInTmpl = inTmpl;
            var lastOpenerCharIdx = -1;
            var foundOpenerOnThisLine = false;
            for (var ci = upLine.Length - 1; ci >= 0; ci--)
            {
                var c = upLine[ci];
                var next = (ci + 1 < upLine.Length) ? upLine[ci + 1] : '\0';
                var prev = (ci > 0) ? upLine[ci - 1] : '\0';
                if (localInDq || localInSq || localInTmpl)
                {
                    if (c == '\\' && prev != '\\')
                    {
                        ci--;
                        continue;
                    }
                    if (localInDq && c == '"' && prev != '\\') localInDq = false;
                    else if (localInSq && c == '\'' && prev != '\\') localInSq = false;
                    else if (localInTmpl && c == '`' && prev != '\\') localInTmpl = false;
                    continue;
                }
                if (localInBlockCmt)
                {
                    if (c == '*' && prev == '/')
                    {
                        localInBlockCmt = false;
                        ci--;
                    }
                    continue;
                }
                if (c == '/' && prev == '*')
                {
                    localInBlockCmt = true;
                    ci--;
                    continue;
                }
                if (c == '/' && prev == '/')
                {
                    break;
                }
                if (c == '"') { localInDq = true; continue; }
                if (c == '\'') { localInSq = true; continue; }
                if (c == '`') { localInTmpl = true; continue; }
                if (c == openCh)
                {
                    depth--;
                    if (depth == 0)
                    {
                        lastOpenerCharIdx = ci;
                        foundOpenerOnThisLine = true;
                    }
                }
                else if (c == closeCh)
                {
                    depth++;
                }
            }
            inLineCmt = false;
            inBlockCmt = localInBlockCmt;
            inStrDq = localInDq; inStrSq = localInSq; inTmpl = localInTmpl;
            if (foundOpenerOnThisLine && depth == 0)
            {
                openerLineIdx = li;
                break;
            }
            if (depth < 0) return line;
        }
        if (openerLineIdx < 0) return line;
        var openerLine = fileLines[openerLineIdx];
        var openerIndent = AgentTextUtilities.GetLeadingWhitespace(openerLine);
        var currentIndent = AgentTextUtilities.GetLeadingWhitespace(line);
        if (currentIndent.Length <= openerIndent.Length) return line;
        return openerIndent + line[currentIndent.Length..];
    }
    private static bool IsSafeCloseSuffix(string suffix)
    {
        if (string.IsNullOrEmpty(suffix)) return true;
        if (suffix.Trim().Length == 0) return true;
        var s = suffix.Trim();
        return s is ";" or "," or ")" or "]" or "}"
            or "{" or "; {" or ", {" or ") {" or "] {" or "} {" or ";{" or ",{" or "){" or "]{" or "}{";
    }
    private async Task<(string decision, string reason, int score, bool needsExtraStep)> LlmVerifyEditStepAsync(
        string relPath, string originalPrompt, string stepChange, string oldStr, string newStr,
        string preEditContent, string postEditContent, bool emitSse, CancellationToken ct,
        List<(int score, string reason, string failedNew)>? priorAttempts = null,
        string? explorationContext = null, AgentPlan? fullPlan = null,
        int currentStepIndex = -1, string? causalContext = null, string? llmRoundLabel = null)
    {
        var anchor = newStr.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
        var postLines = postEditContent.Split('\n');
        var anchorIdx = -1;
        if (!string.IsNullOrEmpty(anchor))
        {
            for (var i = 0; i < postLines.Length; i++)
            {
                if (postLines[i].Contains(anchor, StringComparison.Ordinal))
                {
                    anchorIdx = i;
                    break;
                }
            }
        }
        var contextWindow = anchorIdx >= 0
            ? postEditContent.Length < 8000
                ? postEditContent
                : string.Join("\n", postLines[
                    Math.Max(0, anchorIdx - 80)..Math.Min(postLines.Length, anchorIdx + 80)])
            : "(anchor not found in post-edit file)";
        var priorBlock = new StringBuilder();
        if (priorAttempts != null && priorAttempts.Count > 0)
        {
            priorBlock.AppendLine("\n### PRIOR FAILED ATTEMPTS — learn from these ###");
            for (var i = 0; i < priorAttempts.Count; i++)
            {
                var pa = priorAttempts[i];
                priorBlock.AppendLine($"Attempt {i + 1}: score={pa.score}/100, reason={pa.reason}");
                priorBlock.AppendLine("Failed code (DO NOT reproduce this):");
                priorBlock.AppendLine("```");
                priorBlock.AppendLine(TruncateForLlm(pa.failedNew, 800));
                priorBlock.AppendLine("```");
            }
            priorBlock.AppendLine();
        }
        var futureStepsBlock = new StringBuilder();
        if (fullPlan?.Plan?.Count > 0 && currentStepIndex >= 0)
        {
            futureStepsBlock.AppendLine("\n### PLANNED FUTURE STEPS (Context for verification) ###");
            futureStepsBlock.AppendLine("The current edit is step " + (currentStepIndex + 1) + ".");
            futureStepsBlock.AppendLine("If this edit references methods/properties that don't exist yet, check if they are added in a FUTURE step below.");
            futureStepsBlock.AppendLine("If they are added in a future step, DO NOT abandon the current edit for missing references.\n");
            for (int i = currentStepIndex + 1; i < fullPlan.Plan.Count; i++)
            {
                var p = fullPlan.Plan[i];
                futureStepsBlock.AppendLine($"Step {i + 1}: [{p.File}] {p.Change}");
            }
        }
        var sysPrompt = BuildVerifyEditUserPrompt();
        var userMsg =
            $"### TASK PROMPT ###\n{originalPrompt}\n\n" +
            (string.IsNullOrWhiteSpace(causalContext) ? "" : causalContext + "\n\n") +
            $"### STEP DESCRIPTION ###\n{stepChange}\n\n" +
            $"### FILE ###\n{relPath}\n\n" +
            (string.IsNullOrWhiteSpace(explorationContext) ? "" : $"### RELATED SERVICE/MODEL CONTEXT ###\n{explorationContext}\n\n") +
            futureStepsBlock.ToString() +
            $"### OLD CODE (what was there before) ###\n```\n{TruncateForLlm(oldStr, 1500)}\n```\n\n" +
            $"### NEW CODE (what the edit replaced it with) ###\n```\n{TruncateForLlm(newStr, 1500)}\n```\n\n" +
            $"### POST-EDIT CONTEXT WINDOW ({(postEditContent.Length < 4000 ? "full file" : "50+ lines around the edit")}) ###\n```\n{contextWindow}\n```\n" +
            (priorAttempts != null && priorAttempts.Count > 0 ? priorBlock.ToString() : "") +
            "\nDecide: keep or abandon? Set needsExtraStep=true if a follow-up step is needed to add a missing method/property. Output JSON only.";
        try
        {
            var (raw, _, error) = await CallLlmRawStreaming(
                sysPrompt, userMsg, emitSse, ct,
                requestTimeout: _infiniteTimeout,
                maxTokens: 256, llmRoundLabel: llmRoundLabel);
            if (string.IsNullOrWhiteSpace(raw))
                return ("error", $"LLM returned empty response. {error}", 0, false);
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                cleaned = cleaned.TrimStart('`');
                var firstNewline = cleaned.IndexOf('\n');
                if (firstNewline >= 0) cleaned = cleaned[(firstNewline + 1)..];
                if (cleaned.EndsWith("```")) cleaned = cleaned[..^3];
            }
            cleaned = ExtractFirstJsonObject(cleaned);
            using var doc = JsonDocument.Parse(cleaned);
            var decision = doc.RootElement.TryGetProperty("decision", out var dEl)
                ? dEl.GetString()?.ToLowerInvariant().Trim() ?? ""
                : "";
            var reason = doc.RootElement.TryGetProperty("reason", out var rEl)
                ? rEl.GetString()?.Trim() ?? ""
                : "";
            var score = doc.RootElement.TryGetProperty("score", out var sEl) && sEl.ValueKind == JsonValueKind.Number
                ? sEl.GetInt32()
                : (decision == "keep" ? 85 : 30);
            var needsExtraStep = doc.RootElement.TryGetProperty("needsExtraStep", out var nEl) && nEl.ValueKind == JsonValueKind.True;
            if (decision != "keep" && decision != "abandon")
                return ("error", $"LLM returned unknown decision '{decision}'", score, false);
            return (decision, reason, score, needsExtraStep);
        }
        catch (Exception ex)
        {
            return ("error", $"Exception during LLM verify: {ex.Message}", 0, false);
        }
    }
    private static string TruncateForLlm(string? s, int maxChars)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxChars) return s ?? "";
        var headLen = (int)(maxChars * 0.6);
        var tailLen = maxChars - headLen - 20;
        if (tailLen < 0) tailLen = 0;
        return s.Substring(0, headLen) +
               $"\n... [truncated {s.Length - headLen - tailLen} chars] ...\n" +
               (tailLen > 0 ? s.Substring(s.Length - tailLen, tailLen) : "");
    }
    private static int CountNewMethodsInNewCode(string newCode, string oldStr)
    {
        if (string.IsNullOrWhiteSpace(newCode) || string.IsNullOrWhiteSpace(oldStr)) return 0;
        var declPattern = new Regex(
            @"(?:async\s+)?(?:private\s+|public\s+|protected\s+)?(?:static\s+)?(\w+)\s*\([^)]*\)(?:\s*:\s*(?:\w+(?:<[^>]*>)?|Promise<\w+(?:<[^>]*>)?>)\s*)?\s*\{",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var oldPattern = new Regex(
            @"(?:(?:public|private|protected|internal)\s+)?(?:(?:static|virtual|override|abstract|sealed|new|partial|async|unsafe)\s+)*(?:\w+(?:\[\])?(?:<[^>]*>)?)\s+(\w+)\s*\(",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var newMethods = declPattern.Matches(newCode)
            .Select(m => m.Groups[1].Value)
            .Where(n => n.Length > 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet();
        if (newMethods.Count == 0) return 0;
        var existingMethods = oldPattern.Matches(oldStr)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet();
        return newMethods.Count(n => !existingMethods.Contains(n));
    }
    private string NormalizeEditIndentation(string content, string appliedNewStr)
    {
        if (string.IsNullOrWhiteSpace(appliedNewStr) || string.IsNullOrWhiteSpace(content))
            return content;
        var fileLines = content.Split('\n');
        var needleLines = appliedNewStr.Split('\n');
        var firstNeedle = needleLines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Trim()) && l.Trim().Length >= 3);
        if (firstNeedle == null) return content;
        var trimmedFirst = firstNeedle.Trim();
        var firstMatch = -1;
        for (var i = 1; i < fileLines.Length; i++)
        {
            if (fileLines[i].Trim() == trimmedFirst)
            {
                var prevNonBlank = i - 1;
                while (prevNonBlank >= 0 && string.IsNullOrWhiteSpace(fileLines[prevNonBlank]))
                    prevNonBlank--;
                if (prevNonBlank >= 0)
                {
                    var prevIndent = Regex.Match(fileLines[prevNonBlank], @"^(\s*)").Groups[1].Value;
                    var curIndent = Regex.Match(fileLines[i], @"^(\s*)").Groups[1].Value;
                    if (prevIndent.Length >= 3 && curIndent.Length < prevIndent.Length - 1 && curIndent.Length * 2 < prevIndent.Length)
                    {
                        firstMatch = i;
                        break;
                    }
                }
            }
        }
        if (firstMatch < 0) return content;
        var prevLine = fileLines[firstMatch - 1];
        var expectedBase = Regex.Match(prevLine, @"^(\s*)").Groups[1].Value;
        var lastMatch = firstMatch;
        for (var i = firstMatch + 1; i < fileLines.Length; i++)
        {
            var lt = fileLines[i].Trim();
            if (lt.Length >= 3 && needleLines.Any(n => n.Trim() == lt))
                lastMatch = i;
        }
        var changed = false;
        for (var i = firstMatch; i <= lastMatch; i++)
        {
            if (string.IsNullOrWhiteSpace(fileLines[i])) continue;
            var curIndent = Regex.Match(fileLines[i], @"^(\s*)").Groups[1].Value;
            if (curIndent.Length >= expectedBase.Length) continue;
            var newLine = expectedBase + fileLines[i].TrimStart();
            if (newLine != fileLines[i]) { fileLines[i] = newLine; changed = true; }
        }
        if (!changed) return content;
        return string.Join("\n", fileLines);
    }
    private static double TokenOverlap(string a, string b)
    {
        var tokensA = new HashSet<string>(Regex.Split(a.ToLowerInvariant(), @"[^a-z0-9]+")
            .Where(t => t.Length >= 3));
        var tokensB = new HashSet<string>(Regex.Split(b.ToLowerInvariant(), @"[^a-z0-9]+")
            .Where(t => t.Length >= 3));
        if (tokensA.Count == 0 || tokensB.Count == 0) return 0;
        var intersection = tokensA.Intersect(tokensB).Count();
        return (double)intersection / Math.Min(tokensA.Count, tokensB.Count);
    }

    /// <summary>
    /// File-truth check for the description-overlap duplicate guards. The token-overlap
    /// proxy compares step DESCRIPTIONS, which can over-promise: an earlier step may have
    /// been only PARTIALLY applied — e.g. the deterministic member synthesis added just a
    /// property while the description also promised a method (the live navigation
    /// component.ts moviesTodoCount/getMoviesInfo run) — so the proxy wrongly rejects the
    /// missing work as a duplicate and the method never lands. Returns true only when there
    /// is positive evidence the target actually exists in the file: the step's own new code
    /// is present, the method it names is declared, or the identifier it names appears in
    /// the file. Returns false (→ the step is NOT a duplicate) when the named target is
    /// entirely absent.
    /// </summary>
    private static async Task<bool> StepTargetExistsInFileAsync(
        PlanStep step, string projectRoot, CancellationToken ct)
    {
        if (step == null || string.IsNullOrWhiteSpace(step.File) ||
            AgentProjectUtilities.IsSpecialMarker(step.File)) return false;
        string? current = null;
        try
        {
            var fp = Path.GetFullPath(Path.Combine(projectRoot,
                step.File.Replace('/', Path.DirectorySeparatorChar)));
            if (System.IO.File.Exists(fp))
                current = await System.IO.File.ReadAllTextAsync(fp, Encoding.UTF8, ct);
        }
        catch { }
        if (current == null) return false;
        // The step's own new code (when the planner emitted it) is the strongest evidence.
        if (!string.IsNullOrWhiteSpace(step.NewString))
        {
            if (current.Contains(step.NewString, StringComparison.Ordinal)) return true;
            if (CheckMethodExistsInFile(current, step.NewString) != null) return true;
        }
        var change = step.Change ?? "";
        // A declared method/function is the strongest positive evidence — but the extractor
        // also surfaces property-like names ("Add moviesTodoCount property"), so a failed
        // method-style check falls through to the loose identifier check below instead of
        // returning false (a present property must count as implemented).
        var methodName = AgentMethodInventory.ExtractJsMethodNameFromChange(change);
        if (!string.IsNullOrWhiteSpace(methodName) && methodName.Length > 2 &&
            MethodNameExistsInFile(current, methodName))
            return true;
        // Fallback: the identifier the change names (property, variable, etc.).
        var m = Regex.Match(change,
            @"\b(?:add|create|insert|implement|define)\s+(?:a\s+|an\s+|the\s+|new\s+)*([A-Za-z_$][A-Za-z0-9_$]*)\b",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var candidate = m.Groups[1].Value;
            var stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the", "a", "an", "new", "code", "logic", "feature", "support",
                "method", "function", "handler", "property", "file", "class", "service",
                "button", "icon", "item", "nav", "count", "tracking", "saved", "user"
            };
            if (!stopwords.Contains(candidate) && candidate.Length > 2)
                return Regex.IsMatch(current, $@"\b{Regex.Escape(candidate)}\b");
        }
        return false;
    }
    private async Task<string?> DetectMissingCreateTableAsync(
        string oldStr, string newStr, string fileContent, string relPath, string projectRoot, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(newStr) || string.IsNullOrWhiteSpace(fileContent))
            return null;
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        var sqlCapableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".rs", ".java", ".kt", ".php", ".rb", ".sql" };
        if (!sqlCapableExtensions.Contains(ext)) return null;
        var insertUpdateRegex = new Regex(
            @"\b(?:INSERT\s+INTO|UPDATE)\s+`?(\w+)`?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var referencedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchExcerpts = new List<string>();
        foreach (Match m in insertUpdateRegex.Matches(newStr))
        {
            var tbl = m.Groups[1].Value;
            if (tbl.Length <= 2 || char.IsDigit(tbl[0])) continue;
            referencedTables.Add(tbl);
            var start = Math.Max(0, m.Index - 60);
            var end = Math.Min(newStr.Length, m.Index + m.Length + 60);
            matchExcerpts.Add(newStr.Substring(start, end - start).Replace("\n", " ").Trim());
        }
        if (referencedTables.Count == 0) return null;
        var createTableRegex = new Regex(
            @"\bCREATE\s+TABLE(?:\s+IF\s+NOT\s+EXISTS)?\s+`?(\w+)`?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in createTableRegex.Matches(newStr))
            existingTables.Add(m.Groups[1].Value);
        foreach (Match m in createTableRegex.Matches(fileContent))
            existingTables.Add(m.Groups[1].Value);
        var tableMentionRegex = new Regex(
            @"\b(?:FROM|JOIN|INTO|UPDATE|TABLE)\s+`?(\w+)`?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        foreach (Match m in tableMentionRegex.Matches(fileContent))
            existingTables.Add(m.Groups[1].Value);
        // Tables covered by migrations/schema_changes.md count as existing — the user applies
        // the change manually, so the code never inlines DDL.
        foreach (var t in SqlMigrationService.FindMigratedTables(projectRoot))
            existingTables.Add(t);
        var missingTables = referencedTables
            .Where(t => !existingTables.Contains(t))
            .ToList();
        if (missingTables.Count == 0) return null;
        var sysPrompt = "You are a code analysis AI. You examine code snippets to determine if they contain actual SQL statements (INSERT INTO, UPDATE) that are meant to modify a database table, or if they are just regular text/prose/comments that happen to contain those words. Output ONLY a JSON object: {\"isSql\": true|false}";
        var userPrompt = $"File: {relPath}\n\nSnippets found:\n{string.Join("\n---\n", matchExcerpts)}\n\nDo these snippets contain actual SQL statements executing against a database table?";
        try
        {
            var (raw, _, err) = await CallLlmRaw(sysPrompt, userPrompt, ct, _infiniteTimeout, maxTokens: 64);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var cleaned = raw.Trim();
                if (cleaned.StartsWith("```"))
                {
                    var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                    if (m.Success) cleaned = m.Groups[1].Value.Trim();
                }
                var fb = cleaned.IndexOf('{');
                var lb = cleaned.LastIndexOf('}');
                if (fb >= 0 && lb > fb) cleaned = cleaned.Substring(fb, lb - fb + 1);
                using var doc = JsonDocument.Parse(cleaned);
                if (doc.RootElement.TryGetProperty("isSql", out var isSqlEl) && isSqlEl.ValueKind == JsonValueKind.False)
                {
                    await EmitLog(emitSse, "info", $"SQL Guard: LLM verified that matched 'INSERT/UPDATE' keywords in {relPath} are prose, not SQL.", ct: ct);
                    return null;
                }
            }
        }
        catch { }
        var preview = string.Join(", ", missingTables.Take(5));
        return $"MISSING SQL TABLE — newString contains INSERT/UPDATE statements referencing table(s) [{preview}] " +
               "that do NOT exist in the file and are NOT documented in migrations/schema_changes.md. " +
               "Add a _sql_migration step (file=\"_sql_migration\") whose newString is the CREATE TABLE IF NOT EXISTS statement " +
               "for EACH missing table — the system appends it to migrations/schema_changes.md so the user can apply it " +
               "to their database manually. (A NEW COLUMN on an existing table goes in the same _sql_migration step as an " +
               "ALTER TABLE ... ADD COLUMN statement.) Do NOT inline CREATE TABLE or ALTER TABLE inside the method body — " +
               "the endpoint only does INSERT/UPDATE/SELECT. Do NOT emit INSERT/UPDATE for a table that has not been created yet.";
    }

    /// <summary>
    /// Drafts a CREATE TABLE IF NOT EXISTS statement for a table name when a
    /// _sql_migration step arrives without DDL content. Best-effort: falls back to a
    /// generic skeleton when the LLM call fails so the user still gets a usable file.
    /// </summary>
    private async Task<string> DraftCreateTableAsync(string tableName, string description, CancellationToken ct)
    {
        try
        {
            var sys = "You write SQLite/MySQL CREATE TABLE statements. Output ONLY the SQL, no markdown, no explanation.";
            var usr = $"Write a CREATE TABLE IF NOT EXISTS statement for table `{tableName}`. Context: {description}. " +
                      $"Use sensible column types (INTEGER/INT, TEXT/VARCHAR, TIMESTAMP) and a PRIMARY KEY. End with ';'.";
            var (raw, _, _) = await CallLlmRaw(sys, usr, ct, _infiniteTimeout, maxTokens: 256);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var cleaned = raw.Trim();
                if (cleaned.StartsWith("```"))
                {
                    var m = Regex.Match(cleaned, @"```(?:sql)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                    if (m.Success) cleaned = m.Groups[1].Value.Trim();
                }
                if (cleaned.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase) ||
                    cleaned.StartsWith("create table", StringComparison.OrdinalIgnoreCase))
                    return cleaned;
            }
        }
        catch { }
        return $"CREATE TABLE IF NOT EXISTS {tableName} (\n    id INTEGER PRIMARY KEY AUTOINCREMENT\n);";
    }

    private static string? CheckMethodExistsInFile(string fileContent, string newStr)
    {
        var fnMatch = Regex.Match(newStr, @"(?:vm\.)?(\w+)\s*(?:[:=])\s*function\s*\(", RegexOptions.IgnoreCase);
        if (!fnMatch.Success)
            fnMatch = Regex.Match(newStr, @"function\s+(\w+)\s*\(", RegexOptions.IgnoreCase);
        if (!fnMatch.Success)
            fnMatch = Regex.Match(newStr, @"(?m)^\s*(?:(?:public|private|protected|internal|static|readonly|async)\s+)*(?:get\s+|set\s+)?(\w+)\s*\([^)]*\)\s*(?::[^;{]*)?\{", RegexOptions.IgnoreCase);
        if (!fnMatch.Success)
            return null;
        var fnName = fnMatch.Groups[1].Value;
        if (fnName.Length <= 2) return null;
        // MethodNameExistsInFile covers JS (`function name(` / `vm.name = function`) and TS
        // (`openPopupPanel(): void {`) declaration styles.
        if (MethodNameExistsInFile(fileContent, fnName))
            return fnName;
        return null;
    }

    /// <summary>
    /// True when a method/function with the given name is already declared in the file —
    /// covers TypeScript class methods (`openPopupPanel(): void {`, `async foo() {`, getters/setters),
    /// JS function declarations, and vm./this./const arrow-assignment styles.
    /// </summary>
    private static bool MethodNameExistsInFile(string fileContent, string methodName)
    {
        if (string.IsNullOrWhiteSpace(methodName) || methodName.Length <= 2 || string.IsNullOrWhiteSpace(fileContent)) return false;
        var esc = Regex.Escape(methodName);
        // TS class method declaration: `name(...): Type {` / `async name() {` / `get name() {`
        if (Regex.IsMatch(fileContent,
            @"(?m)^\s*(?:(?:public|private|protected|internal|static|readonly|async)\s+)*(?:get\s+|set\s+)?" + esc + @"\s*\([^)]*\)\s*(?::[^;{]*)?\{",
            RegexOptions.IgnoreCase)) return true;
        // JS function declaration / assignment / arrow styles
        if (Regex.IsMatch(fileContent,
            @"(?:function\s+" + esc + @"\s*\(|(?:vm|this|self|that)\." + esc + @"\s*(?:[:=])\s*(?:async\s+)?function\s*\(|(?:vm|this|self|that)\." + esc + @"\s*=\s*(?:async\s+)?\([^)]*\)\s*=>|(?m)^\s*(?:const|let|var\s+)?" + esc + @"\s*[:=]\s*(?:async\s+)?(?:function\s*\(|\([^)]*\)\s*=>))",
            RegexOptions.IgnoreCase)) return true;
        return false;
    }
    private string FormatCssEditedRegion(string content, string appliedNewStr)
    {
        if (string.IsNullOrWhiteSpace(appliedNewStr) || string.IsNullOrWhiteSpace(content))
            return content;
        var fileLines = content.Split('\n');
        var stepCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 1; i < fileLines.Length; i++)
        {
            var line = fileLines[i];
            var trimmed = line.TrimStart();
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (trimmed.Contains('{')) continue;
            if (trimmed.StartsWith("//") || trimmed.StartsWith("/*") ||
                trimmed.StartsWith("*") || trimmed.StartsWith("&") ||
                trimmed.StartsWith("@")) continue;
            if (!trimmed.Contains(':')) continue;
            if (trimmed.Contains("://")) continue;
            for (var j = i - 1; j >= 0; j--)
            {
                if (!fileLines[j].Contains('{')) continue;
                var ruleLeading = LeadingWhitespaceCss(fileLines[j]);
                var propLeading = LeadingWhitespaceCss(line);
                if (propLeading.Length > ruleLeading.Length)
                {
                    var step = propLeading.Substring(ruleLeading.Length);
                    if (!stepCounts.ContainsKey(step)) stepCounts[step] = 0;
                    stepCounts[step]++;
                }
                break;
            }
        }
        var dominantStep = stepCounts.Count > 0
            ? stepCounts.OrderByDescending(k => k.Value).First().Key
            : "  ";
        var anchor = appliedNewStr.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (string.IsNullOrEmpty(anchor))
            return content;
        var editLine = -1;
        for (var i = 0; i < fileLines.Length; i++)
        {
            if (fileLines[i].Contains(anchor, StringComparison.Ordinal))
            {
                editLine = i;
                break;
            }
        }
        if (editLine < 0) return content;
        var rulesToFormat = new HashSet<(int start, int end)>();
        var visited = new HashSet<int>();
        for (var i = editLine; i < fileLines.Length; i++)
        {
            if (!fileLines[i].Contains(anchor, StringComparison.Ordinal) && i != editLine)
            {
                var anyNeedleHere = false;
                foreach (var needle in appliedNewStr.Split('\n').Select(l => l.Trim())
                            .Where(l => !string.IsNullOrWhiteSpace(l)).Take(3))
                {
                    if (fileLines[i].Contains(needle, StringComparison.Ordinal))
                    {
                        anyNeedleHere = true;
                        break;
                    }
                }
                if (!anyNeedleHere && i - editLine > 30) break;
            }
            if (visited.Contains(i)) continue;
            var (ruleStart, ruleEnd) = FindEnclosingRuleCss(fileLines, i);
            if (ruleStart < 0 || ruleEnd <= ruleStart) continue;
            rulesToFormat.Add((ruleStart, ruleEnd));
            for (var k = ruleStart; k <= ruleEnd; k++) visited.Add(k);
        }
        if (rulesToFormat.Count == 0)
        {
            var (rs, re) = FindEnclosingRuleCss(fileLines, editLine);
            if (rs >= 0 && re > rs) rulesToFormat.Add((rs, re));
        }
        if (rulesToFormat.Count == 0) return content;
        var newLines = (string[])fileLines.Clone();
        foreach (var (start, end) in rulesToFormat)
        {
            var ruleIndent = LeadingWhitespaceCss(fileLines[start]);
            var propertyIndent = ruleIndent + dominantStep;
            for (var i = start + 1; i < end; i++)
            {
                var line = fileLines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    newLines[i] = line;
                    continue;
                }
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("/*") ||
                    trimmed.StartsWith("*") || trimmed.StartsWith("@"))
                    continue;
                if (trimmed.StartsWith("&")) continue;
                if (trimmed.Contains('{')) continue;
                if (trimmed.Contains("://")) continue;
                if (!trimmed.Contains(':')) continue;
                if (trimmed.StartsWith(":") || trimmed.StartsWith(">") ||
                    trimmed.StartsWith("+") || trimmed.StartsWith("~") ||
                    trimmed.StartsWith("*"))
                    continue;
                var colonIdx = IndexOfFirstColonOutsideParensCss(trimmed);
                if (colonIdx < 0) continue;
                var prop = trimmed.Substring(0, colonIdx).TrimEnd();
                var rest = trimmed.Substring(colonIdx + 1);
                string trailingComment = "";
                var commentIdx = rest.IndexOf("//");
                if (commentIdx >= 0)
                {
                    trailingComment = " " + rest.Substring(commentIdx).TrimEnd();
                    rest = rest.Substring(0, commentIdx);
                }
                var value = rest.Trim();
                if (value.Length == 0) continue;
                newLines[i] = propertyIndent + prop + ": " + value +
                              (trailingComment.Length > 0 ? trailingComment : "");
            }
        }
        return string.Join("\n", newLines);
    }
    private static (int start, int end) FindEnclosingRuleCss(string[] lines, int fromLine)
    {
        if (lines == null || lines.Length == 0 || fromLine < 0 || fromLine >= lines.Length)
            return (-1, -1);
        var ruleStart = -1;
        var depth = 0;
        for (var i = fromLine; i >= 0; i--)
        {
            foreach (var ch in lines[i])
            {
                if (ch == '}') depth++;
                else if (ch == '{')
                {
                    if (depth > 0) depth--;
                    else { ruleStart = i; goto FoundOpen; }
                }
            }
        }
    FoundOpen:
        if (ruleStart < 0) return (-1, -1);
        depth = 0;
        var foundOpen = false;
        for (var i = ruleStart; i < lines.Length; i++)
        {
            foreach (var ch in lines[i])
            {
                if (ch == '{') { depth++; foundOpen = true; }
                else if (ch == '}') depth--;
            }
            if (foundOpen && depth == 0)
                return (ruleStart, i);
        }
        return (-1, -1);
    }
    private static int IndexOfFirstColonOutsideParensCss(string s)
    {
        if (string.IsNullOrEmpty(s)) return -1;
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            else if (c == ':' && depth == 0) return i;
        }
        return -1;
    }
    private static string LeadingWhitespaceCss(string line)
    {
        if (string.IsNullOrEmpty(line)) return "";
        var sb = new StringBuilder();
        foreach (var ch in line)
        {
            if (ch == ' ' || ch == '\t') sb.Append(ch);
            else break;
        }
        return sb.ToString();
    }
    private async Task<AgentPlan> RunPlanCoherenceCheckAsync(
        AgentPlan plan,
        string projectRoot,
        string originalPrompt,
        bool emitSse,
        CancellationToken ct)
    {
        if (plan?.Plan == null || plan.Plan.Count < 2) return plan!;
        var sb = new StringBuilder();
        sb.AppendLine(
            "You are checking whether a code-change plan is coherent AS A WHOLE — not step by step, " +
            "but as a chain. A plan is coherent when every symbol a step REFERENCES (methods, properties, " +
            "arrays, variables) is either:\n" +
            "  (a) already present in the file's CURRENT content, OR\n" +
            "  (b) explicitly INTRODUCED by a PRIOR step in the same plan.\n" +
            "Name mismatches count as gaps: if the HTML references `selectedImageIndex` but a TS step " +
            "adds `imagePreviewIndex`, that is a gap — they are different names.");
        sb.AppendLine();
        sb.AppendLine("## ORIGINAL TASK");
        sb.AppendLine(originalPrompt);
        sb.AppendLine();
        sb.AppendLine("## CURRENT FILE CONTENTS");
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in plan.Plan)
        {
            if (!AgentProjectUtilities.IsRelativePath(step.File) || AgentProjectUtilities.IsSpecialMarker(step.File)) continue;
            if (!loaded.Add(step.File)) continue;
            var fp = Path.GetFullPath(Path.Combine(projectRoot, step.File.Replace('/', Path.DirectorySeparatorChar)));
            if (!System.IO.File.Exists(fp)) continue;
            var content = await System.IO.File.ReadAllTextAsync(fp, Encoding.UTF8, ct);
            sb.AppendLine($"### {step.File} (current — before this plan runs)");
            sb.AppendLine("```");
            // Full file content — the coherence check must see the whole file to judge whether
            // a prior step's symbols really landed.
            sb.AppendLine(content);
            sb.AppendLine("```");
            sb.AppendLine();
        }
        sb.AppendLine("## PLAN TO CHECK");
        for (var i = 0; i < plan.Plan.Count; i++)
            sb.AppendLine($"Step {i + 1}: [{plan.Plan[i].File}] {plan.Plan[i].Change}");
        sb.AppendLine();
        sb.AppendLine("## INSTRUCTIONS");
        sb.AppendLine("For each step, identify:");
        sb.AppendLine("  introduces: the specific symbol NAMES this step will ADD (e.g. `imagePreviews: FileEntry[]`, `nextImage()`)");
        sb.AppendLine("  requires:   the specific symbol NAMES this step REFERENCES that must already exist");
        sb.AppendLine();
        sb.AppendLine("Then check every 'requires' entry against (a) current file content and (b) prior steps' 'introduces'.");
        sb.AppendLine("A mismatch in NAME is a gap — `selectedImageIndex` ≠ `imagePreviewIndex`.");
        sb.AppendLine();
        sb.AppendLine("ALSO check for REDUNDANT or CONFLICTING steps:");
        sb.AppendLine("- If Step A modifies a method to include a new feature, and Step B updates the UI to call a *different* new method for the same feature, the steps are redundant/conflicting.");
        sb.AppendLine("- If a step is unnecessary because a prior step already achieves the same result, REMOVE it from the correctedPlan.");
        sb.AppendLine("- Do NOT keep steps that conflict with each other. Choose ONE clear approach and discard the other.");
        sb.AppendLine();
        sb.AppendLine("CRITICAL — SELF-INCONSISTENT STEP DESCRIPTIONS:");
        sb.AppendLine("Flag any step whose own description is internally contradictory or assumes something");
        sb.AppendLine("that doesn't exist yet. Common patterns to catch:");
        sb.AppendLine("  a) \"Modify the newly added X method\" — if X is NOT in the current file AND no prior step");
        sb.AppendLine("     in this plan adds it, then the step description is self-inconsistent. The step should");
        sb.AppendLine("     say \"Add X method with ...\" instead of assuming X already exists.");
        sb.AppendLine("  b) \"Update the existing Y method to also ...\" but Y does NOT exist in the current file.");
        sb.AppendLine("     This step needs to ADD Y, not modify it.");
        sb.AppendLine("  c) A step that references a symbol (method, property, variable) that is NOT in the");
        sb.AppendLine("     current file content AND is NOT introduced by any prior step in this plan.");
        sb.AppendLine("  d) Steps that say \"Add X and wire it up\" but the 'wiring' references symbols that");
        sb.AppendLine("     cannot be found in current file content or prior steps.");
        sb.AppendLine("For EACH self-inconsistency found, output it as a gap with afterStep = the step's index,");
        sb.AppendLine("missing = \"SELF-INCONSISTENT: [the issue]\", and include a corrected version");
        sb.AppendLine("of that step in correctedPlan (fixing its description to ADD rather than modify, etc.).");
        sb.AppendLine();
        sb.AppendLine("If coherent: {\"coherent\": true, \"gaps\": []}");
        sb.AppendLine("If not coherent (has gaps, redundant steps, or conflicting logic):");
        sb.AppendLine("{");
        sb.AppendLine("  \"coherent\": false,");
        sb.AppendLine("  \"gaps\": [");
        sb.AppendLine("    {\"afterStep\": 1, \"missing\": \"imagePreviews array\", \"usedBy\": \"Step 3 nextImage() and HTML template\"},");
        sb.AppendLine("    {\"afterStep\": 1, \"missing\": \"selectedImageIndex\", \"usedBy\": \"HTML *ngIf and Step 3\"}");
        sb.AppendLine("  ],");
        sb.AppendLine("  \"correctedPlan\": [");
        sb.AppendLine("    {\"file\": \"...\", \"change\": \"...\"}");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("correctedPlan must include ALL necessary steps (removing redundant/conflicting ones) PLUS new insertion steps in the correct order.");
        sb.AppendLine("Use the SAME property/method names consistently across all steps.");
        sb.AppendLine("Output ONLY JSON — no markdown, no explanation.");
        var (raw, _, err) = await CallLlmRaw(
            "You check code-change plan coherence across steps. Output ONLY valid JSON.",
            sb.ToString(), ct, _infiniteTimeout, maxTokens: 2048);
        if (string.IsNullOrWhiteSpace(raw))
        {
            await EmitLog(emitSse, "warn", $"Plan coherence check skipped: {err ?? "empty response"}", ct: ct);
            return plan;
        }
        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (m.Success) cleaned = m.Groups[1].Value.Trim();
            }
            var fb = cleaned.IndexOf('{'); var lb = cleaned.LastIndexOf('}');
            if (fb >= 0 && lb > fb) cleaned = cleaned[fb..(lb + 1)];
            using var doc = JsonDocument.Parse(cleaned, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;
            var coherent = root.TryGetProperty("coherent", out var cEl) && cEl.GetBoolean();
            if (coherent)
            {
                await EmitLog(emitSse, "info", "Plan coherence: ✓ steps form a coherent chain", ct: ct);
                return plan;
            }
            var gapSummaries = new List<string>();
            if (root.TryGetProperty("gaps", out var gapsEl) && gapsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var gap in gapsEl.EnumerateArray())
                {
                    var afterStep = gap.TryGetProperty("afterStep", out var asEl) ? asEl.GetInt32() : -1;
                    var missing = gap.TryGetProperty("missing", out var miEl) ? miEl.GetString() : "?";
                    var usedBy = gap.TryGetProperty("usedBy", out var ubEl) ? ubEl.GetString() : "";
                    var msg = $"gap after step {afterStep}: '{missing}'" +
                              (string.IsNullOrWhiteSpace(usedBy) ? "" : $" — needed by: {usedBy}");
                    gapSummaries.Add(msg);
                    await EmitLog(emitSse, "warn", $"Plan coherence {msg}", ct: ct);
                }
            }
            if (root.TryGetProperty("correctedPlan", out var cpArr) && cpArr.ValueKind == JsonValueKind.Array)
            {
                var corrected = new List<PlanStep>();
                foreach (var el in cpArr.EnumerateArray())
                {
                    var file = el.TryGetProperty("file", out var f) ? f.GetString() : null;
                    var change = el.TryGetProperty("change", out var c) ? c.GetString() : null;
                    if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(change)) continue;
                    var orig = plan.Plan.FirstOrDefault(p =>
                        string.Equals(p.File, file, StringComparison.OrdinalIgnoreCase));
                    corrected.Add(new PlanStep
                    {
                        File = file,
                        Change = change,
                        Priority = orig?.Priority ?? 1,
                        LineNumber = orig?.LineNumber ?? 0
                    });
                }
                if (corrected.Count >= plan.Plan.Count)
                {
                    var added = corrected.Count - plan.Plan.Count;
                    await EmitLog(emitSse, "info",
                        $"Plan coherence: inserted {added} missing step(s) to close {gapSummaries.Count} gap(s)", ct: ct);
                    plan.Plan = corrected;
                    if (emitSse)
                        await SendSse(Response, "plan", new
                        {
                            thinking = $"Coherence check found {gapSummaries.Count} gap(s) — inserted {added} step(s)",
                            summary = plan.Summary,
                            items = plan.Plan
                        }, ct);
                }
                else
                {
                    await EmitLog(emitSse, "warn",
                        $"Plan coherence: corrected plan ({corrected.Count} steps) is smaller than original " +
                        $"({plan.Plan.Count}) — keeping original to avoid data loss", ct: ct);
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(emitSse, "warn", $"Plan coherence check parse error: {ex.Message}", ct: ct);
        }
        return plan;
    }
}
