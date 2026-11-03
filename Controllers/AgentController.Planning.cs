using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using System.Collections.Concurrent;
using System.Diagnostics;
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
using static Weaver.Services.AgentPlanningPolicy;
using Weaver;
namespace Weaver.Controllers;

partial class AgentController
{
    private static List<string> ExtractTopLevelCssSelectors(string css)
    {
        var selectors = new List<string>();
        if (string.IsNullOrWhiteSpace(css)) return selectors;
        var i = 0;
        var depth = 0;
        var selectorStart = 0;
        while (i < css.Length)
        {
            var c = css[i];
            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var endPos = end >= 0 ? end + 2 : css.Length;
                if (depth == 0) selectorStart = endPos;
                i = endPos;
                continue;
            }
            if (c == '"' || c == '\'')
            {
                i++;
                while (i < css.Length && css[i] != c)
                {
                    if (css[i] == '\\') i += 2;
                    else i++;
                }
                i++;
                continue;
            }
            if (c == '{' && depth == 0)
            {
                var selector = css[selectorStart..i].Trim();
                if (!string.IsNullOrWhiteSpace(selector))
                    selectors.Add(selector);
                var bodyDepth = 1;
                var j = i + 1;
                while (j < css.Length && bodyDepth > 0)
                {
                    if (css[j] == '{') bodyDepth++;
                    else if (css[j] == '}') bodyDepth--;
                    j++;
                }
                i = j;
                selectorStart = i;
                continue;
            }
            if (c == '@' && depth == 0)
            {
                var j = i;
                while (j < css.Length && css[j] != '{' && css[j] != ';') j++;
                if (j < css.Length && css[j] == ';')
                {
                    i = j + 1;
                    selectorStart = i;
                    continue;
                }
                var blockDepth = 1;
                var k = j + 1;
                while (k < css.Length && blockDepth > 0)
                {
                    if (css[k] == '{') blockDepth++;
                    else if (css[k] == '}') blockDepth--;
                    k++;
                }
                i = k;
                selectorStart = i;
                continue;
            }
            i++;
        }
        return selectors;
    }
    private static (string content, List<string> warnings) MergeDuplicateCssRules(string css)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(css)) return (css, warnings);
        var rules = new List<CssRule>();
        var i = 0;
        var depth = 0;
        var selectorStart = 0;
        while (i < css.Length)
        {
            var c = css[i];
            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var endPos = end >= 0 ? end + 2 : css.Length;
                if (depth == 0) selectorStart = endPos;
                i = endPos;
                continue;
            }
            if (c == '"' || c == '\'')
            {
                i++;
                while (i < css.Length && css[i] != c)
                {
                    if (css[i] == '\\') i += 2;
                    else i++;
                }
                i++;
                continue;
            }
            if (c == '{' && depth == 0)
            {
                var selector = css[selectorStart..i].Trim();
                var bodyStart = i + 1;
                var bodyDepth = 1;
                var j = bodyStart;
                while (j < css.Length && bodyDepth > 0)
                {
                    if (css[j] == '{') bodyDepth++;
                    else if (css[j] == '}') bodyDepth--;
                    if (bodyDepth > 0) j++;
                }
                var body = css[bodyStart..j];
                rules.Add(new CssRule
                {
                    Selector = selector,
                    Body = body,
                    Start = selectorStart,
                    End = j + 1
                });
                i = j + 1;
                selectorStart = i;
                continue;
            }
            if (c == '@' && depth == 0)
            {
                var j = i;
                while (j < css.Length && css[j] != '{' && css[j] != ';') j++;
                if (j < css.Length && css[j] == ';')
                {
                    i = j + 1;
                    selectorStart = i;
                    continue;
                }
                var blockDepth = 1;
                var k = j + 1;
                while (k < css.Length && blockDepth > 0)
                {
                    if (css[k] == '{') blockDepth++;
                    else if (css[k] == '}') blockDepth--;
                    if (blockDepth > 0) k++;
                }
                rules.Add(new CssRule
                {
                    Selector = css[selectorStart..(k + 1)],
                    Body = "",
                    Start = selectorStart,
                    End = k + 1,
                    IsAtRuleBlock = true
                });
                i = k + 1;
                selectorStart = i;
                continue;
            }
            i++;
        }
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<(int firstIdx, int dupIdx)>();
        for (var idx = 0; idx < rules.Count; idx++)
        {
            var rule = rules[idx];
            if (rule.IsAtRuleBlock) continue;
            var norm = Regex.Replace(
                Regex.Replace(rule.Selector.ToLowerInvariant(), @"\s+", " ").Trim(),
                @"\s*,\s*", ",").Trim();
            if (seen.TryGetValue(norm, out var firstIdx))
            {
                duplicates.Add((firstIdx, idx));
                var lineApprox = css[..rule.Start].Count(ch => ch == '\n') + 1;
                warnings.Add($"Duplicate CSS selector '{rule.Selector}' — merging into first occurrence (line ~{lineApprox})");
            }
            else
            {
                seen[norm] = idx;
            }
        }
        if (duplicates.Count == 0) return (css, warnings);
        var merges = new Dictionary<int, List<int>>();
        foreach (var (firstIdx, dupIdx) in duplicates)
        {
            if (!merges.ContainsKey(firstIdx)) merges[firstIdx] = new List<int>();
            merges[firstIdx].Add(dupIdx);
        }
        var skipIndices = new HashSet<int>();
        foreach (var kvp in merges)
            foreach (var di in kvp.Value)
                skipIndices.Add(di);
        var result = new StringBuilder(css.Length);
        var lastEnd = 0;
        for (var idx = 0; idx < rules.Count; idx++)
        {
            var rule = rules[idx];
            if (skipIndices.Contains(idx))
            {
                lastEnd = rule.End;
                continue;
            }
            result.Append(css[lastEnd..rule.Start]);
            if (merges.TryGetValue(idx, out var dupIndices))
            {
                var propMap = new Dictionary<string, (string value, string indent)>(StringComparer.OrdinalIgnoreCase);
                var propOrder = new List<string>();
                foreach (var (prop, value, indent) in ParseCssProperties(rule.Body))
                {
                    if (!propMap.ContainsKey(prop)) propOrder.Add(prop);
                    propMap[prop] = (value, indent);
                }
                foreach (var dupIdx in dupIndices)
                {
                    foreach (var (prop, value, indent) in ParseCssProperties(rules[dupIdx].Body))
                    {
                        if (!propMap.ContainsKey(prop)) propOrder.Add(prop);
                        propMap[prop] = (value, indent.Length > 0 ? indent : "  ");
                    }
                }
                var bodySb = new StringBuilder();
                foreach (var prop in propOrder)
                {
                    var (value, indent) = propMap[prop];
                    bodySb.Append(indent);
                    bodySb.Append(prop);
                    bodySb.Append(": ");
                    bodySb.Append(value);
                    bodySb.Append(";\n");
                }
                if (bodySb.Length > 0 && bodySb[bodySb.Length - 1] == '\n')
                    bodySb.Length--;
                result.Append(rule.Selector);
                result.Append(" {\n");
                result.Append(bodySb);
                result.Append("\n}");
            }
            else
            {
                result.Append(css[rule.Start..rule.End]);
            }
            lastEnd = rule.End;
        }
        result.Append(css[lastEnd..]);
        return (result.ToString(), warnings);
    }
    private static List<(string prop, string value, string indent)> ParseCssProperties(string body)
    {
        var props = new List<(string, string, string)>();
        if (string.IsNullOrWhiteSpace(body)) return props;
        foreach (var line in body.Split('\n'))
        {
            var stripped = line.Trim();
            if (string.IsNullOrWhiteSpace(stripped)) continue;
            if (stripped.StartsWith("/*") || stripped.StartsWith("//")) continue;
            if (!stripped.EndsWith(';')) continue;
            var colonIdx = IndexOfFirstColonOutsideParensCss(stripped);
            if (colonIdx <= 0) continue;
            var prop = stripped[..colonIdx].Trim();
            var value = stripped[(colonIdx + 1)..].TrimEnd(';').Trim();
            var indent = LeadingWhitespaceCss(line);
            props.Add((prop, value, indent));
        }
        return props;
    }
    private sealed class CssRule
    {
        public string Selector { get; set; } = "";
        public string Body { get; set; } = "";
        public int Start { get; set; }
        public int End { get; set; }
        public bool IsAtRuleBlock { get; set; }
    }
    private async Task PersistBoardDataPlanAsync(string? cardId, List<PlanStep> planSteps, bool emitSse, CancellationToken ct,
        string summary = "", int score = 0, bool append = false)
    {
        if (string.IsNullOrWhiteSpace(cardId) || planSteps == null || planSteps.Count == 0)
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
                    var existingItems = cardObj["_plan"]?.AsObject()?["items"] as JsonArray;
                    var doneLookup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    // Per-step ground truth verified flags, keyed by (file|change) → (anchor → verified):
                    // a rebuild recomputes the expectation texts from the steps but must keep the
                    // verified=true/false marks already stamped at step completion (PersistBoardDataPlanStepAsync),
                    // or an already-completed step would silently drop back to unverified.
                    var gtVerifiedLookup = new Dictionary<string, Dictionary<string, bool>>(StringComparer.OrdinalIgnoreCase);
                    if (existingItems != null)
                    {
                        foreach (var existing in existingItems)
                        {
                            if (existing is not JsonObject eo) continue;
                            if (eo["done"]?.GetValue<bool>() == true &&
                                eo["file"]?.GetValue<string>() is string ef &&
                                eo["change"]?.GetValue<string>() is string ec)
                            {
                                doneLookup.Add(ef + "|" + ec);
                            }
                            if (eo["groundTruth"] is JsonArray gtArr &&
                                eo["file"]?.GetValue<string>() is string gtf &&
                                eo["change"]?.GetValue<string>() is string gtc)
                            {
                                var byAnchor = new Dictionary<string, bool>(StringComparer.Ordinal);
                                foreach (var n in gtArr.OfType<JsonObject>())
                                {
                                    if (n["anchor"]?.GetValue<string>() is string a &&
                                        n["verified"] is JsonValue v && v.TryGetValue<bool>(out var vb))
                                        byAnchor[a] = vb;
                                }
                                if (byAnchor.Count > 0)
                                    gtVerifiedLookup[gtf + "|" + gtc] = byAnchor;
                            }
                        }
                    }
                    var planItems = new JsonArray();
                    if (append && existingItems != null)
                    {
                        foreach (var existing in existingItems)
                        {
                            if (existing is JsonObject eo)
                            {
                                if (eo["status"]?.GetValue<string>() == "rejected") continue; // preserved below
                                eo["done"] = true;
                                planItems.Add(eo.DeepClone());
                            }
                        }
                    }
                    for (var i = 0; i < planSteps.Count; i++)
                    {
                        var s = planSteps[i];
                        var wasDone = doneLookup.Contains((s.File ?? "") + "|" + (s.Change ?? ""));
                        // Per-step computed ground truth: the deterministic expected outcome
                        // for THIS step (e.g. a literal swap's new text must be present; a
                        // 'did you mean' typo fix's corrected token must be what's on disk).
                        // Attached so each plan item shows its own expected outcome; verified
                        // against disk when the step completes (PersistBoardDataPlanStepAsync
                        // sets verified=true/false per entry).
                        var stepGt = s.GroundTruth;
                        JsonNode? stepGtNode = stepGt is { Count: > 0 } ? JsonSerializer.SerializeToNode(stepGt) : null;
                        if (stepGtNode is JsonArray gtArr &&
                            gtVerifiedLookup.TryGetValue((s.File ?? "") + "|" + (s.Change ?? ""), out var prevByAnchor))
                        {
                            foreach (var n in gtArr.OfType<JsonObject>())
                            {
                                if (n["anchor"]?.GetValue<string>() is string a && prevByAnchor.TryGetValue(a, out var vb))
                                    n["verified"] = vb;
                            }
                        }
                        planItems.Add(new JsonObject
                        {
                            ["index"] = planItems.Count,
                            ["file"] = s.File,
                            ["change"] = s.Change,
                            ["priority"] = s.Priority,
                            ["line"] = s.LineNumber,
                            ["metaGroup"] = s.MetaGroup,
                            ["done"] = wasDone,
                            ["groundTruth"] = stepGtNode
                        });
                    }
                    // Preserve rejected step records (web-gate vetoes) across plan rebuilds
                    // so the classifier's reason survives reloads — re-anchor their indexes
                    // to the rebuilt array's tail.
                    if (existingItems != null)
                    {
                        foreach (var existing in existingItems)
                        {
                            if (existing is JsonObject eo && eo["status"]?.GetValue<string>() == "rejected")
                            {
                                eo["index"] = planItems.Count;
                                planItems.Add(eo.DeepClone());
                            }
                        }
                    }
                    cardObj["_plan"] = new JsonObject
                    {
                        ["items"] = planItems,
                        ["summary"] = summary,
                        ["score"] = score
                    };
                    var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await _boardData.SaveRawAsync(saved);
                    if (emitSse)
                    {
                        await SendSse(Response, "refresh", new
                        {
                            target = "boarddata",
                            reason = "plan-updated",
                            cardId
                        }, ct);
                    }
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to persist full plan to boarddata", new { cardId, error = ex.Message });
        }
    }
    private async Task PersistCohesionToCardAsync(string? cardId, string relPath, List<string> issues, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardId) || issues == null)
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
                    cardObj["_cohesion"] = new JsonObject
                    {
                        ["file"] = relPath,
                        ["issues"] = new JsonArray(issues.Select(i => JsonValue.Create(i)).ToArray())
                    };
                    var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await _boardData.SaveRawAsync(saved);
                    if (emitSse)
                    {
                        await SendSse(Response, "cohesion", new
                        {
                            file = relPath,
                            issues
                        }, ct);
                    }
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to persist cohesion check to boarddata", new { cardId, error = ex.Message });
        }
    }
    private async Task AttachFilesToCardAsync(string? cardId, List<string> filePaths, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardId) || filePaths == null || filePaths.Count == 0)
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
                    var attached = cardObj["attached"] as JsonArray ?? new JsonArray();
                    foreach (var fp in filePaths)
                        if (!attached.Any(a => a?.GetValue<string>() == fp))
                            attached.Add(JsonValue.Create(fp));
                    cardObj["attached"] = attached;
                    var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await _boardData.SaveRawAsync(saved);
                    if (emitSse)
                        await SendSse(Response, "refresh", new { target = "boarddata", reason = "files-attached", cardId }, ct);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to attach files to card", new { cardId, error = ex.Message });
        }
    }
    private async Task<int> HandleMethodSignatureChange(
        string fullPath, string relPath,
        string oldStr, string newStr,
        string projectRoot, bool emitSse, CancellationToken ct,
        int stepIndex, List<object> allResults, string? cardId)
    {
        var oldMatch = MethodDeclRegex.Match(oldStr);
        var newMatch = MethodDeclRegex.Match(newStr);
        if (!oldMatch.Success || !newMatch.Success)
            return stepIndex;
        var oldMethodName = oldMatch.Groups[1].Value;
        var newMethodName = newMatch.Groups[1].Value;
        if (!string.Equals(oldMethodName, newMethodName, StringComparison.Ordinal))
            return stepIndex;
        var oldParams = oldMatch.Groups[2].Value;
        var newParams = newMatch.Groups[2].Value;
        if (string.Equals(oldParams, newParams, StringComparison.Ordinal))
            return stepIndex;
        await EmitLog(emitSse, "info",
            $"Method signature change detected: {oldMethodName}({oldParams}) → {newMethodName}({newParams}). Searching for call sites...", ct: ct);
        var csFiles = new List<string>();
        try
        {
            if (Directory.Exists(projectRoot))
            {
                csFiles = Directory.GetFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
                    .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\") && !f.Contains("\\node_modules\\")
                             && !f.Contains("\\dist\\") && !f.Contains("\\.git\\"))
                    .OrderBy(f => f.Length)
                    .ToList();
            }
        }
        catch { return stepIndex; }
        if (csFiles.Count == 0)
        {
            await EmitLog(emitSse, "info", "No .cs files found in project to search for call sites.", ct: ct);
            return stepIndex;
        }
        var methodNameLower = oldMethodName.ToLowerInvariant();
        var candidateFiles = new List<string>();
        foreach (var f in csFiles)
        {
            if (string.Equals(f, fullPath, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                using var sr = new StreamReader(f, Encoding.UTF8);
                var firstFewKb = new char[4096];
                var read = await sr.ReadAsync(firstFewKb, 0, firstFewKb.Length);
                var head = new string(firstFewKb, 0, read);
                if (head.Contains(methodNameLower, StringComparison.OrdinalIgnoreCase))
                    candidateFiles.Add(f);
            }
            catch { }
        }
        if (candidateFiles.Count == 0)
        {
            await EmitLog(emitSse, "info", "No call site files found.", ct: ct);
            return stepIndex;
        }
        await EmitLog(emitSse, "info",
            $"Found {candidateFiles.Count} file(s) containing '{oldMethodName}' — checking for call sites...", ct: ct);
        foreach (var candidateFile in candidateFiles)
        {
            ct.ThrowIfCancellationRequested();
            var fileContent = await System.IO.File.ReadAllTextAsync(candidateFile, Encoding.UTF8, ct);
            var candidateRelPath = Path.GetRelativePath(projectRoot, candidateFile).Replace('\\', '/');
            var callSitePrompt = $@"File: {candidateRelPath}
METHOD SIGNATURE CHANGED:
Old: `{oldMethodName}({oldParams})`
New: `{newMethodName}({newParams})`
The file above contains one or more calls to `{oldMethodName}` that may need updating because the method's signature changed.
Search through the ENTIRE file content below and find EVERY occurrence of `{oldMethodName}(`. For each call site found:
1. Determine the correct new call based on the new signature
2. Output the edits needed
FILE CONTENT:
```csharp
{fileContent}
```
For each call site that needs updating, output a JSON array:
[
  {{""oldString"": ""exact text of the old call"", ""newString"": ""exact text of the updated call""}}
]
If no call sites need updating, output an empty array [].
Reply ONLY with the JSON array — no explanation, no markdown.";
            var (callSitesJson, _, _) = await CallLlmRaw(
                "You are a code refactoring assistant. Update method call sites to match a changed signature. Output only JSON.",
                callSitePrompt, ct, _infiniteTimeout, maxTokens: 4096);
            if (string.IsNullOrWhiteSpace(callSitesJson))
                continue;
            var cleanJson = callSitesJson.Trim();
            if (cleanJson.StartsWith("```"))
            {
                var m = Regex.Match(cleanJson, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (m.Success) cleanJson = m.Groups[1].Value.Trim();
            }
            List<Dictionary<string, string>>? callSiteEdits = null;
            try { callSiteEdits = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(cleanJson); }
            catch { }
            if (callSiteEdits == null || callSiteEdits.Count == 0)
                continue;
            await EmitLog(emitSse, "info",
                $"  {candidateRelPath}: {callSiteEdits.Count} call site edit(s) suggested", ct: ct);
            var fileContentMut = fileContent;
            var appliedCount = 0;
            foreach (var edit in callSiteEdits)
            {
                if (!edit.TryGetValue("oldString", out var callOld) || string.IsNullOrWhiteSpace(callOld))
                    continue;
                if (!edit.TryGetValue("newString", out var callNew))
                    callNew = "";
                var (replaced, newContent, _, _) = AnchorEditHeuristics.TryReplaceSafe(fileContentMut, callOld, callNew);
                if (replaced)
                {
                    fileContentMut = newContent;
                    appliedCount++;
                    stepIndex++;
                    var stepResult = new Dictionary<string, object?>
                    {
                        ["index"] = stepIndex,
                        ["type"] = "edit",
                        ["status"] = "modified",
                        ["path"] = candidateRelPath,
                        ["description"] = $"Updated call site: {oldMethodName} → {newMethodName}",
                        ["planItemIndex"] = -1,
                        ["parentStep"] = relPath,
                        ["methodSignature"] = $"{oldMethodName}({oldParams}) → {newMethodName}({newParams})"
                    };
                    allResults.Add(stepResult);
                    if (emitSse)
                        await SendSse(Response, "step", stepResult, ct);
                }
            }
            if (appliedCount > 0)
            {
                await System.IO.File.WriteAllTextAsync(candidateFile, fileContentMut, Encoding.UTF8, ct);
                await EmitLog(emitSse, "success",
                    $"  ✓ Updated {appliedCount} call site(s) in {candidateRelPath}", ct: ct);
            }
        }
        return stepIndex;
    }
    private async Task<IncrementalStepProposal?> ProposeNextIncrementalStepAsync(
        AgentRunContext runContext, string originalPrompt, string discoveryContext, List<PlanStep> planSoFar,
        string? steeringContext, List<string> rejectionFeedback, bool emitSse, CancellationToken ct,
        string stepMode = "all", string? extendedReasoning = null, int? atomicStepEstimate = null,
        string? projectRoot = null)
    {
        var cfg = await LoadConfigAsync();
        var sys = BuildIncrementalStepSystemPrompt(stepMode, await FilterToolsForStepAsync(originalPrompt, cfg.enabledTools, ct), atomicStepEstimate);
        var user = BuildIncrementalStepUserPrompt(originalPrompt, discoveryContext, planSoFar, steeringContext, rejectionFeedback, extendedReasoning, atomicStepEstimate, runContext.RequirementChecklist, projectRoot);
        // Labeled so the agent panel shows the token spend of this planning round (prompt +
        // response) right in the run's log, and the step result aggregates it as llmTokens.
        var (raw, _, err) = await CallLlmRawStreaming(sys, user, emitSse, ct, requestTimeout: _infiniteTimeout,
            maxTokens: 4096, llmRoundLabel: $"planner step {planSoFar.Count + 1}", runContext: runContext);
        if (string.IsNullOrWhiteSpace(raw))
        {
            await EmitLog(emitSse, "warn", $"Incremental step proposal returned empty: {err}", ct: ct);
            return null;
        }
        try
        {
            var cleaned = AgentJsonUtilities.ExtractJsonObjectWithKeys(raw, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "step", "planComplete", "exploreFile" });
            using var doc = JsonDocument.Parse(cleaned, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;
            var complete = root.TryGetProperty("planComplete", out var pc) && pc.ValueKind == JsonValueKind.True;
            var completionReason = root.TryGetProperty("completionReason", out var cr) ? cr.GetString() : null;
            var thinking = root.TryGetProperty("thinking", out var th) ? th.GetString() : null;
            var exploreFile = root.TryGetProperty("exploreFile", out var ef) && ef.ValueKind == JsonValueKind.String
                ? ef.GetString() : null;
            if (complete)
                return new IncrementalStepProposal { PlanComplete = true, CompletionReason = completionReason, Thinking = thinking };
            if (!string.IsNullOrWhiteSpace(exploreFile))
                return new IncrementalStepProposal { PlanComplete = false, ExploreFile = exploreFile, Thinking = thinking };
            if (root.TryGetProperty("step", out var maybeExploreStep) && maybeExploreStep.ValueKind == JsonValueKind.Object)
            {
                var maybeFile = maybeExploreStep.TryGetProperty("file", out var mfEl) ? mfEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(maybeFile) &&
                    (maybeFile.StartsWith("_explore", StringComparison.OrdinalIgnoreCase)))
                {
                    var maybeChange = maybeExploreStep.TryGetProperty("change", out var mcEl) ? mcEl.GetString() : "";
                    var pathMatch = Regex.Match(maybeChange ?? "", @"[\w./\\-]+\.\w{1,5}\b");
                    var target = pathMatch.Success ? pathMatch.Value : maybeChange;
                    if (!string.IsNullOrWhiteSpace(target))
                        return new IncrementalStepProposal { PlanComplete = false, ExploreFile = target, Thinking = thinking };
                }
            }
            if (!root.TryGetProperty("step", out var stepEl) || stepEl.ValueKind != JsonValueKind.Object)
                return new IncrementalStepProposal { PlanComplete = false, Thinking = thinking };
            var file = stepEl.TryGetProperty("file", out var fEl) ? fEl.GetString() : null;
            var change = stepEl.TryGetProperty("change", out var cEl) ? cEl.GetString() : null;
            var targetSymbol = stepEl.TryGetProperty("targetSymbol", out var tsEl) && tsEl.ValueKind == JsonValueKind.String ? tsEl.GetString() : null;
            var line = stepEl.TryGetProperty("line", out var lEl) && lEl.ValueKind == JsonValueKind.Number ? lEl.GetInt32() : 0;
            static string? ReadPlannerString(JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.String)
                    return AgentTextUtilities.UnescapeString(el.GetString() ?? "");
                if (el.ValueKind == JsonValueKind.Array)
                {
                    var lines = new List<string>();
                    foreach (var item in el.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String)
                            lines.Add(AgentTextUtilities.UnescapeString(item.GetString() ?? ""));
                    return lines.Count > 0 ? string.Join("\n", lines) : null;
                }
                return null;
            }
            var oldString = stepEl.TryGetProperty("oldString", out var osEl) ? ReadPlannerString(osEl) : null;
            var newString = stepEl.TryGetProperty("newString", out var nsEl) ? ReadPlannerString(nsEl) : null;
            var refFiles = new List<string>();
            if (stepEl.TryGetProperty("referenceFiles", out var rfArr) && rfArr.ValueKind == JsonValueKind.Array)
                foreach (var rf in rfArr.EnumerateArray())
                    if (rf.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(rf.GetString()))
                        refFiles.Add(rf.GetString()!);
            var edits = new List<EditPair>();
            if (stepEl.TryGetProperty("edits", out var editsEl) && editsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var editEl in editsEl.EnumerateArray())
                {
                    if (editEl.ValueKind != JsonValueKind.Object) continue;
                    var editOld = editEl.TryGetProperty("oldString", out var eosEl) ? ReadPlannerString(eosEl) : null;
                    var editNew = editEl.TryGetProperty("newString", out var ensEl) ? ReadPlannerString(ensEl) : null;
                    var editLine = editEl.TryGetProperty("line", out var elnEl) && elnEl.ValueKind == JsonValueKind.Number
                        ? elnEl.GetInt32()
                        : 0;
                    if (!string.IsNullOrWhiteSpace(editOld) || editNew != null)
                    {
                        edits.Add(new EditPair
                        {
                            OldString = editOld ?? "",
                            NewString = editNew ?? "",
                            LineNumber = editLine
                        });
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(change))
                return new IncrementalStepProposal { PlanComplete = false, Thinking = thinking };
            var justification = root.TryGetProperty("justification", out var jEl) ? jEl.GetString() : null;
            var targetType = stepEl.TryGetProperty("targetType", out var ttEl) && ttEl.ValueKind == JsonValueKind.String ? ttEl.GetString() : null;
            var targetName = stepEl.TryGetProperty("targetName", out var tnEl) && tnEl.ValueKind == JsonValueKind.String ? tnEl.GetString() : null;
            var insertAfter = stepEl.TryGetProperty("insertAfter", out var iaEl) && iaEl.ValueKind == JsonValueKind.True;
            var newCode = new List<string>();
            if (stepEl.TryGetProperty("newCode", out var ncEl) && ncEl.ValueKind == JsonValueKind.Array)
                foreach (var ncItem in ncEl.EnumerateArray())
                    if (ncItem.ValueKind == JsonValueKind.String)
                        newCode.Add(AgentTextUtilities.UnescapeString(ncItem.GetString() ?? ""));
            var fullFile = stepEl.TryGetProperty("fullFile", out var ffEl) ? ReadPlannerString(ffEl) : null;
            var primaryStep = ParseStepFromJson(file, change, targetSymbol, line, oldString, newString, refFiles, edits,
                targetType, targetName, insertAfter, newCode.Count > 0 ? newCode : null, fullFile);
            var additionalSteps = new List<PlanStep>();
            var allObjects = AgentJsonUtilities.ExtractAllJsonObjects(raw);
            var foundPrimary = false;
            foreach (var objStr in allObjects)
            {
                if (!foundPrimary)
                {
                    if (string.Equals(objStr, cleaned, StringComparison.Ordinal)) { foundPrimary = true; }
                    continue;
                }
                try
                {
                    using var extraDoc = JsonDocument.Parse(objStr, new JsonDocumentOptions { AllowTrailingCommas = true });
                    if (!extraDoc.RootElement.TryGetProperty("step", out var extraStepEl) || extraStepEl.ValueKind != JsonValueKind.Object) continue;
                    var extraFile = extraStepEl.TryGetProperty("file", out var efEl) ? efEl.GetString() : null;
                    var extraChange = extraStepEl.TryGetProperty("change", out var ecEl) ? ecEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(extraFile) || string.IsNullOrWhiteSpace(extraChange)) continue;
                    var extraTargetSymbol = extraStepEl.TryGetProperty("targetSymbol", out var etsEl) && etsEl.ValueKind == JsonValueKind.String ? etsEl.GetString() : null;
                    var extraLine = extraStepEl.TryGetProperty("line", out var elEl) && elEl.ValueKind == JsonValueKind.Number ? elEl.GetInt32() : 0;
                    var extraOld = extraStepEl.TryGetProperty("oldString", out var eosEl2) ? ReadPlannerString(eosEl2) : null;
                    var extraNew = extraStepEl.TryGetProperty("newString", out var ensEl2) ? ReadPlannerString(ensEl2) : null;
                    var extraRefFiles = new List<string>();
                    if (extraStepEl.TryGetProperty("referenceFiles", out var erfArr) && erfArr.ValueKind == JsonValueKind.Array)
                        foreach (var rf in erfArr.EnumerateArray())
                            if (rf.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(rf.GetString()))
                                extraRefFiles.Add(rf.GetString()!);
                    var extraEdits = new List<EditPair>();
                    if (extraStepEl.TryGetProperty("edits", out var eeditsEl) && eeditsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var editEl in eeditsEl.EnumerateArray())
                        {
                            if (editEl.ValueKind != JsonValueKind.Object) continue;
                            var eo = editEl.TryGetProperty("oldString", out var eosEl3) ? ReadPlannerString(eosEl3) : null;
                            var en = editEl.TryGetProperty("newString", out var ensEl3) ? ReadPlannerString(ensEl3) : null;
                            if (!string.IsNullOrWhiteSpace(eo) || en != null)
                                extraEdits.Add(new EditPair { OldString = eo ?? "", NewString = en ?? "" });
                        }
                    }
                    var extraTargetType = extraStepEl.TryGetProperty("targetType", out var ettEl) && ettEl.ValueKind == JsonValueKind.String ? ettEl.GetString() : null;
                    var extraTargetName = extraStepEl.TryGetProperty("targetName", out var etnEl) && etnEl.ValueKind == JsonValueKind.String ? etnEl.GetString() : null;
                    var extraInsertAfter = extraStepEl.TryGetProperty("insertAfter", out var eiaEl) && eiaEl.ValueKind == JsonValueKind.True;
                    var extraNewCode = new List<string>();
                    if (extraStepEl.TryGetProperty("newCode", out var encEl) && encEl.ValueKind == JsonValueKind.Array)
                        foreach (var encItem in encEl.EnumerateArray())
                            if (encItem.ValueKind == JsonValueKind.String)
                                extraNewCode.Add(AgentTextUtilities.UnescapeString(encItem.GetString() ?? ""));
                    var extraFullFile = extraStepEl.TryGetProperty("fullFile", out var effEl) ? ReadPlannerString(effEl) : null;
                    additionalSteps.Add(ParseStepFromJson(extraFile, extraChange, extraTargetSymbol, extraLine, extraOld, extraNew, extraRefFiles, extraEdits,
                        extraTargetType, extraTargetName, extraInsertAfter, extraNewCode.Count > 0 ? extraNewCode : null, extraFullFile));
                }
                catch { }
            }
            return new IncrementalStepProposal
            {
                PlanComplete = false,
                Thinking = thinking,
                CompletionReason = justification,
                Step = primaryStep,
                AdditionalSteps = additionalSteps.Count > 0 ? additionalSteps : null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<(bool valid, string? reason)> ValidateIncrementalStepAsync(
        PlanStep step, string originalPrompt, string discoveryContext, List<PlanStep> planSoFar,
        string projectRoot, bool emitSse, CancellationToken ct,
        bool skipLlm = false, string? lastStepCompletionNote = null,
        List<string>? attachedFiles = null)
    {
        if (string.IsNullOrWhiteSpace(step.File) || string.IsNullOrWhiteSpace(step.Change))
            return (false, "Step is missing file or change description.");
        // OS-filesystem tasks: _create_directory/_create_file are repo-relative and CANNOT
        // reach the Desktop — only a _command step touches the OS filesystem. Knowing the OS
        // also lets the _command rejection teach the model the real desktop path instead of
        // letting it invent Linux paths.
        var osTask = IsExternalFilesystemTask(originalPrompt);
        var osDesktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (string.IsNullOrWhiteSpace(osDesktopPath))
        {
            // Headless/service hosts (Linux CI runners) have no Desktop folder — give the
            // rejection feedback a usable anchor instead of "Desktop is at ."
            var osProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            osDesktopPath = string.IsNullOrWhiteSpace(osProfile)
                ? (OperatingSystem.IsWindows() ? "C:\\Users\\<username>\\Desktop" : "$HOME/Desktop")
                : Path.Combine(osProfile, "Desktop");
        }
        var normNew = NormalizeChangeForDedup(step.Change);
        // Web-step re-run guard FIRST (web-specific feedback + the lower 0.35 threshold): a
        // re-proposed _web_search/_web_fetch whose query/URL matches — or near-matches — an
        // already-executed one, while the results are in context, is the "loop of searching".
        // The generic edit-step dedup below (0.82 Jaccard) lets reworded queries slip through.
        if (RepeatedWebStepReason(step, planSoFar, discoveryContext) is string webDupReason)
            return (false, webDupReason);
        // A duplicate heuristic already ran for this proposal and was explicitly overruled
        // by the LLM adjudicator. Keep every other validation gate active, but do not run the
        // same similarity check again and undo that adjudication.
        if (step.SkipDuplicateCheck)
            goto SkipDuplicateChecks;

        // TABULAR-FILE EXEMPTION: structured CSV/TSV/XLSX edits are sequential ops whose change
        // descriptions share vocabulary ("add a type column" → "add a row with id=1026…" →
        // "replace all 'unknown' with 'normal' in column type"). The 0.82-Jaccard / exact-match
        // dedup below would flag near-similar follow-up ops as duplicates; tabular steps dedup
        // by EXACT change only (the ops are deterministic — a re-proposed identical op no-ops
        // or repeats at APPLY time, where the CSV editor is the arbiter).
        if (TabularFileService.IsTabularFile(step.File))
        {
            foreach (var existing in planSoFar)
            {
                if (!string.Equals(existing.File, step.File, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(NormalizeChangeForDedup(existing.Change), normNew, StringComparison.OrdinalIgnoreCase))
                    return (false, $"Duplicates an already-committed step targeting {existing.File}: \"{existing.Change}\".");
            }
        }
        else
        {
            foreach (var existing in planSoFar)
            {
                if (!string.Equals(existing.File, step.File, StringComparison.OrdinalIgnoreCase)) continue;
                var normExisting = NormalizeChangeForDedup(existing.Change);
                if (normNew == normExisting || CalculateChangeSimilarity(normNew, normExisting) >= 0.82)
                    return (false, $"Duplicates an already-committed step targeting {existing.File}: \"{existing.Change}\".");
            }
        }

    SkipDuplicateChecks:
        // Reject _create_file steps with no actual content (hallucinated file creation)
        if (string.Equals(step.File, "_sql_migration", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(step.NewString))
                return (false, "_sql_migration step must carry the DDL in newString — " +
                                "provide the full CREATE TABLE or ALTER TABLE statement (e.g. \"CREATE TABLE IF NOT EXISTS benchmark_scores (...);\" " +
                                "or \"ALTER TABLE benchmark_scores ADD COLUMN metric_type TEXT;\") or edit an existing file instead.");
            var tables = SqlMigrationService.ExtractCreateTableStatements(step.NewString);
            var alters = SqlMigrationService.ExtractAlterTableStatements(step.NewString);
            if (tables.Count == 0 && alters.Count == 0)
                return (false, "_sql_migration step's newString does not contain a parseable CREATE TABLE or ALTER TABLE statement — " +
                                "include the complete DDL with column definitions and a trailing ';'.");
        }
        if (string.Equals(step.File, "_create_file", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(step.NewString))
                return (false, "_create_file step has no file content in newString — provide the full file content or edit an existing file instead.");
            if (step.NewString.Trim().Length < 1)
                return (false, "_create_file step content is too short (" + step.NewString.Trim().Length + " chars) — provide meaningful file content.");
            if (planSoFar.Any(s => string.Equals(s.File, "_create_file", StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(s.Change, step.Change, StringComparison.OrdinalIgnoreCase)))
                return (false, $"File '{step.Change}' was already created by a prior _create_file step — target the existing file instead.");

            var jsSyntaxReason = ValidateJavaScriptLikeCreateFile(step.Change, step.NewString);
            if (jsSyntaxReason != null)
                return (false, jsSyntaxReason);

            // '.' is in the path class so dotted prefixes (maxhanna.client/src/…) are captured
            // whole — the old [\w\-/\\]+ stopped at the first dot and matched only
            // "maxhanna.client", blindfolding both the same-dir conflict check and this guard.
            var createPathMatch = Regex.Match(step.Change, @"([\w.\-/\\]+\.\w{1,10})");
            if (createPathMatch.Success)
            {
                var createCandidate = createPathMatch.Groups[1].Value.Replace('\\', '/');
                var createFileName = Path.GetFileName(createCandidate);
                // Only a same-named file in the SAME directory as the target blocks creation — a
                // same-named file in a different folder (e.g. benchmark_test_4/index.html when
                // creating benchmark_test_7/index.html) is NOT a conflict.
                var existingFile = AgentDiscovery.FindSameDirectoryFile(createCandidate, projectRoot);
                if (existingFile != null)
                {
                    var createFullPath = Path.GetFullPath(Path.Combine(projectRoot, createCandidate.Replace('/', Path.DirectorySeparatorChar)));
                    if (!System.IO.File.Exists(createFullPath))
                        return (false, $"File '{createFileName}' ALREADY EXISTS at '{existingFile}' — do NOT create it. " +
                                        "Target the existing file path in a normal edit step instead.");
                }
                // INVENTED-DIRECTORY GUARD: a _create_file under a directory prefix that does
                // not exist anywhere in the project (e.g. "src/helpers/weaver/util.ts" when no
                // such folder exists) would silently materialize the whole invented path on
                // disk — the directory counterpart of the edit-to-a-nonexistent-file guard.
                // Reject and steer to the CLOSEST REAL directory (deepest existing ancestor,
                // or an existing folder with the same leaf name). The check (and its three
                // exemptions: earlier _create_directory step, path named by the task, OS paths)
                // is shared with the classic route's whole-plan gate.
                if (InventedCreateDirectoryReason(createCandidate, originalPrompt, planSoFar, projectRoot) is string createDirReason)
                    return (false, createDirReason);
            }
        }
        if (string.Equals(step.File, "_command", StringComparison.OrdinalIgnoreCase) &&
            !AgentProjectUtilities.LooksLikeShellCommand(step.Change))
        {
            if (osTask)
            {
                var mkDirExample = OperatingSystem.IsWindows()
                    ? $"New-Item -ItemType Directory -Path \"{osDesktopPath}\\<name>\" -Force"
                    : $"mkdir -p \"{osDesktopPath}/<name>\"";
                return (false, $"_command step is not an executable shell command. You are on {(OperatingSystem.IsWindows() ? "Windows" : Environment.OSVersion)} and the Desktop is at {osDesktopPath}. A _command step's change must BE the real command with an absolute path, e.g. {mkDirExample}. Never put planning notes in a _command step.");
            }
            return (false, "_command step is not an executable shell command. Use _command only for real terminal commands such as `dotnet test`, `npm install`, or `cd app; npx ng g c name`. Put planning notes in the thinking field, not in a command step.");
        }
        // WRONG-ROOT GUARD: a _command step that creates a folder/file using an absolute path
        // that falls OUTSIDE the project root is almost always the model confusing the Desktop
        // (parent of benchmark_sandbox) with the sandbox itself. Reject and steer it to use
        // relative paths (handled by _create_file/_create_directory) or the correct absolute
        // project root so benchmark folders land inside the sandbox, not alongside it.
        // OS-filesystem tasks are EXEMPT: when the prompt itself demands the output on the
        // Desktop/Downloads/etc. ("write a text file to my desktop"), writing there is the
        // task — the guard only exists to stop REPO tasks (benchmark folders, sandbox work)
        // from leaking out to the Desktop.
        if (!osTask &&
            string.Equals(step.File, "_command", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(step.Change) &&
            !string.IsNullOrWhiteSpace(projectRoot))
        {
            // Extract any absolute path embedded in the command (mkdir/New-Item/Set-Content/etc.).
            // URL schemes (https://…, ftp://…, file://…) are stripped FIRST so they are never read
            // as a drive/absolute path — otherwise a fetch command gets the wrong-root veto instead
            // of the fetch-in-command veto. The (?<![A-Za-z]) lookbehind is belt-and-braces for the
            // drive-prefix match on Windows.
            var pathProbe = Regex.Replace(step.Change,
                @"\b(?:https?|ftp|sftp|file)://[^\s""'|>&]+", " ", RegexOptions.IgnoreCase);
            var absPathMatch = Regex.Match(pathProbe,
                OperatingSystem.IsWindows()
                    ? @"(?<![A-Za-z])[A-Za-z]:[\\/][^\s""'|>&]+"
                    : @"/[^\s""'|>&]+",
                RegexOptions.IgnoreCase);
            if (absPathMatch.Success)
            {
                var absPath = absPathMatch.Value.TrimEnd('\\', '/');
                var fullAbs = Path.GetFullPath(absPath);
                var fullRoot = Path.GetFullPath(projectRoot);
                // Only fire when the absolute path is NOT under the project root.
                if (!fullAbs.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) &&
                    !fullAbs.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    // Check it looks like a directory creation / file-write command — not
                    // a legitimate tool invocation like `git clone` or `dotnet publish -o`.
                    var isMkDir = Regex.IsMatch(step.Change,
                        @"\b(mkdir|md|New-Item|ni)\b", RegexOptions.IgnoreCase);
                    var isWrite = Regex.IsMatch(step.Change,
                        @"\b(Set-Content|Out-File|Add-Content|echo\s|Write-Output)\b",
                        RegexOptions.IgnoreCase);
                    if (isMkDir || isWrite)
                    {
                        return (false,
                            $"_command creates a path OUTSIDE the project root. " +
                            $"The project root is \"{fullRoot}\". " +
                            $"To create a subfolder use a _create_directory step with a relative path (e.g. \"benchmark_test_22\"), " +
                            $"or use an absolute path under the project root: \"{Path.Combine(fullRoot, Path.GetFileName(fullAbs))}\".");
                    }
                }
            }
        }
        // FETCH-IN-COMMAND GUARD: a _command step that pulls CONTENT from an http(s)
        // URL with a download tool is the planner doing a web search by writing a
        // command — the "Invoke-RestMethod https://api.current.ai/..." failure mode
        // (hallucinated API, results never land in context). Steer to the web tools.
        // Only fires when the task hints at needing web data: a bare legit
        // `curl https://…/health` on a non-web task stays allowed. Legit URL commands
        // (git clone, npm install <url>, --source, git fetch origin) never match
        // LooksLikeContentFetchCommand regardless.
        if (string.Equals(step.File, "_command", StringComparison.OrdinalIgnoreCase) &&
            ShouldRejectFetchCommand(originalPrompt, step.Change))
        {
            return (false, FetchCommandFeedbackFor(originalPrompt, step, planSoFar, discoveryContext));
        }
        // SCRAPER-FILE GUARD: a _create_file whose payload is a scraper/fetch script
        // (programs an HTTP request against a URL AND writes the result to a file) is the
        // "wrote a Python app to do the fetch" failure mode — the deterministic sibling of
        // the fetch-in-command guard above. It must fire EVEN AFTER a _web_search/_web_fetch
        // already ran: the missing-web-search gate only rejects while NO web step is in the
        // plan, so a scraper proposed after the search used to sail straight through, land on
        // disk, and then get "run" (the benchmark run that created fetch_poke_data.py after
        // the search and burned a whole planning round re-deriving it). On a web-needing task
        // the fetch belongs to the _web_fetch step tool — the system fetches the URL and
        // auto-writes the demanded file — never to a script. Scoped to TaskHintsWebNeed like
        // the command guard: a task that explicitly asks for a standalone scraper utility on a
        // non-web task (no live-data hint) still gets it.
        // Both scraper guards honor the explicit-script escape hatch: when the task itself asks
        // for a script ("write a python script …"), writing AND running it is the task, so the
        // guards stand down. The generic missing-web-search gate still applies before any web
        // step exists (research first, then script).
        if (TaskHintsWebNeed(originalPrompt) && !TaskExplicitlyRequestsScript(originalPrompt) &&
            ScraperScriptFeedback(step) is string scraperReason)
        {
            return (false, scraperReason);
        }
        // RUN-A-SCRAPER GUARD: a _command that RUNS a script via an interpreter
        // (python/node/… fetch_poke_data.py) or pipes a fetched HTTP stream into one
        // (curl … | python) is the execution half of the same drift — the run that followed
        // the scraper _create_file landing. The script either IS a scraper (read from disk) or
        // does not exist (the create was rejected, so running it is running nothing); both are
        // rejected on web-needing tasks and steered to _web_fetch (or _scraper when web steps
        // keep failing). An existing non-scraper script run stays legitimate.
        if (TaskHintsWebNeed(originalPrompt) && !TaskExplicitlyRequestsScript(originalPrompt) &&
            RunScraperScriptReason(step, projectRoot) is string runScraperReason)
        {
            return (false, runScraperReason);
        }
        // WINDOWS POWER-SHELL && GUARD: powershell.exe (PS 5.1) rejects `&&` as a statement
        // separator — the whole chained write fails with a parser error on the user's machine
        // (the "echo … > file && echo … >> file" run). Windows-only; bash/PS7 allow &&.
        if (string.Equals(step.File, "_command", StringComparison.OrdinalIgnoreCase) &&
            OperatingSystem.IsWindows() &&
            WindowsPowerShellAndChainReason(step.Change) is string andChainReason)
        {
            return (false, andChainReason);
        }
        // REDUNDANT OS-OUTPUT WRITE GUARD: once a committed _command step already wrote the
        // demanded OS output (Set-Content/echo>/Out-File to the demanded directory), a NEW
        // _command step writing the same location is stacked re-work — reject so the planner
        // completes instead of piling redundant writes (Set-Content, then an echo && chain,
        // then an Invoke-RestMethod redirect — all to the same desktop file).
        if (string.Equals(step.File, "_command", StringComparison.OrdinalIgnoreCase) &&
            RedundantOsWriteReason(originalPrompt, step.Change, planSoFar) is string redundantWriteReason)
        {
            return (false, redundantWriteReason);
        }
        // OS tasks: _create_directory/_create_file write relative to the PROJECT ROOT — a step
        // whose change names an OS location would silently create the folder/file INSIDE the
        // project (the "/home/user/search_results" failure). Reject and steer to _command.
        if (osTask &&
            (string.Equals(step.File, "_create_directory", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(step.File, "_create_file", StringComparison.OrdinalIgnoreCase)))
        {
            var ch = (step.Change ?? "").Trim();
            var looksOsPath = ch.StartsWith("/")
                || ch.StartsWith("~")
                || ch.StartsWith("\\\\") // UNC \\server\share
                || Regex.IsMatch(ch, @"^[A-Za-z]:[\\/]") // C:\ or C:/
                || Regex.IsMatch(ch, @"\b(desktop|downloads|documents|userprofile|%userprofile%|home dir|home directory)\b", RegexOptions.IgnoreCase);
            if (looksOsPath)
            {
                var mkDirExample = OperatingSystem.IsWindows()
                    ? $"New-Item -ItemType Directory -Path \"{osDesktopPath}\\<name>\" -Force"
                    : $"mkdir -p \"{osDesktopPath}/<name>\"";
                return (false, $"{step.File} writes RELATIVE TO THE PROJECT ROOT — it cannot create \"{ch}\" on the Desktop/OS filesystem. Use a _command step whose change is a real command with an absolute path, e.g. {mkDirExample} (Desktop is at {osDesktopPath}).");
            }
        }
        // ATTACHED-FILES EDIT GUARD: when the user attached specific files AND the task prompt
        // does not ask to create a new file/folder, a _create_file/_create_directory step is
        // almost always the planner dodging the real edit — the "group benchmarks by benchmark
        // name" run invented benchmark_grouping_helper.ts at the repo ROOT instead of adding a
        // method to the attached weaver.component.ts and updating its template. Reject
        // deterministically (fires even in retry mode) and steer back to editing the attached
        // files. Tasks that genuinely want a new artifact phrase it as creation ("create a
        // helper file…") and stay allowed via PromptSignalsFileCreation.
        if (attachedFiles is { Count: > 0 } &&
            (string.Equals(step.File, "_create_file", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(step.File, "_create_directory", StringComparison.OrdinalIgnoreCase)) &&
            !PromptSignalsFileCreation(originalPrompt))
        {
            var attachedList = string.Join(", ", attachedFiles.Select(f => Path.GetFileName(f.Replace('\\', '/'))));
            return (false,
                $"The task is scoped to the attached file(s) [{attachedList}] and does not ask to create a new file — do NOT create \"{step.Change}\". " +
                "Implement the change as an EDIT to the attached file(s): add the method/property to the component and update its template, " +
                "targeting those exact files. Only use _create_file/_create_directory when the task EXPLICITLY asks for a new file or folder.");
        }
        if (string.Equals(step.File, "_show", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(step.File, "_display", StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"{step.File} is not an actionable planning/edit step. If more context is needed, use _explore for a specific file; otherwise propose the concrete edit step.");
        }
        // Reject after too many edits to the same file+symbol (hallucination loop detection)
        var sameTargetCount = planSoFar.Count(s =>
            !string.IsNullOrWhiteSpace(s.TargetSymbol) &&
            string.Equals(s.TargetSymbol, step.TargetSymbol, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.File, step.File, StringComparison.OrdinalIgnoreCase));
        if (sameTargetCount >= 3)
            return (false, $"Already committed {sameTargetCount} steps targeting '{step.TargetSymbol}' in {step.File}. Further edits to the same symbol suggest a hallucination loop — the task is likely complete.");
        var changeLower = (step.Change ?? "").ToLowerInvariant();
        var rejectedActions = new[] { "move ", "reorder ", "restructure ", "refactor " };
        if (rejectedActions.Any(v => changeLower.StartsWith(v)))
        {
            return (false, $"Step rejected — '{changeLower.Split(' ')[0]}' is a structural change that should be decided by the user, not auto-planned. " +
                            "If the task is functionally complete, return planComplete=true.");
        }
        var researchVerbs = new[] { "locate", "find", "examine", "understand", "read", "explore", "look at", "inspect", "review", "check", "see", "search" };
        // _web_search/_web_fetch are ACTIONABLE research markers — their change field IS the
        // query/URL. The web-need gate decides whether web steps are allowed, not this guard;
        // rejecting them here deadlocks web-needing tasks (the model proposes the right tool
        // and the loop bounces it, exactly like the "Search the web…" run).
        // _browser_test/_scraper/_benchmark_verify are the same class: action markers whose
        // change is the INSPECT/FETCH TARGET, and their change naturally starts with research
        // verbs ("Inspect the benchmark game for correct rendering…") — rejecting them
        // deadlocks the visual-verify flow (benchmark-22: the run burned all its slots after
        // the gate bounced the final _browser_test). _create_file is exempt too: its change
        // field is a RELATIVE PATH, and legit paths can start with research verbs
        // ("README.md", "findings.txt", "search_results.json").
        if (!step.File.Equals("_discover", StringComparison.OrdinalIgnoreCase) &&
            !IsWebStep(step.File) &&
            !step.File.Equals("_create_file", StringComparison.OrdinalIgnoreCase) &&
            !step.File.Equals("_browser_test", StringComparison.OrdinalIgnoreCase) &&
            !step.File.Equals("_scraper", StringComparison.OrdinalIgnoreCase) &&
            !step.File.Equals("_benchmark_verify", StringComparison.OrdinalIgnoreCase) &&
            researchVerbs.Any(v => changeLower.StartsWith(v)))
        {
            return (false, $"Research step rejected — '{changeLower.Split(' ')[0]}' is not an actionable edit. " +
                            "All steps must make actual code changes (add/modify/delete/replace). " +
                            "The file content is already available in the discovery context.");
        }
        if (changeLower.StartsWith("remove") || changeLower.StartsWith("delete"))
        {
            var targetMatch = Regex.Match(step.Change ?? "", @"remove\s+(?:the\s+)?(\w+)", RegexOptions.IgnoreCase);
            if (targetMatch.Success)
            {
                var target = targetMatch.Groups[1].Value;
                var contradicts = planSoFar.Any(p =>
                    string.Equals(p.File, step.File, StringComparison.OrdinalIgnoreCase) &&
                    Regex.IsMatch(p.Change ?? "", $@"\b(add|create|insert)\b.*\b{Regex.Escape(target)}\b", RegexOptions.IgnoreCase));
                if (contradicts)
                    return (false, $"Removes '{target}' but an earlier committed step just added it — contradicts the plan so far.");
            }
        }
        var isSpecial = AgentProjectUtilities.IsSpecialMarker(step.File);
        // TARGETED-ANCHOR GUARD: an edit step whose oldString reproduces a whole block
        // (RULE 17 says 1-3 lines) is unreliable — the LLM drifts when re-outputting a big
        // verbatim block, the plan edit then fails to match at apply time, and the resolver
        // falls back to FORMAT C/D which again forces reproducing the same wall of text
        // (the "group benchmarks" run: a 30-line oldString/newString pair that never
        // matched, then FORMAT D demanded the full section again). Reject oversized anchors
        // deterministically with the targeted-replace pattern so the planner emits a small
        // unique anchor instead. Thresholds mirror GetPlanSizeViolations.
        if (!isSpecial && !string.IsNullOrWhiteSpace(step.OldString))
        {
            var oldAnchorLines = step.OldString.Split('\n').Length;
            var oldAnchorChars = step.OldString.Length;
            if (oldAnchorLines > 10 || oldAnchorChars > 400)
            {
                return (false,
                    $"oldString is {oldAnchorLines} lines/{oldAnchorChars} chars — WAY too large for a reliable targeted edit. " +
                    "Use the TARGETED REPLACE pattern: oldString = the SINGLE most unique line in the target region " +
                    "(1-3 lines MAX, e.g. `<div *ngFor=\"let b of benchmarks\" class=\"benchmark-item\">`), copied verbatim; " +
                    "newString = that same line UNCHANGED followed by your new lines. Do NOT reproduce the whole enclosing " +
                    "block/section in oldString. If you are REPLACING an ENTIRE method, use FORMAT C " +
                    "(targetType/targetName/newCode) instead of oldString/newString.");
            }
        }
        if (!isSpecial && AgentProjectUtilities.IsRelativePath(step.File))
        {
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, step.File.Replace('/', Path.DirectorySeparatorChar)));
            var isModifyVerb = Regex.IsMatch(changeLower, @"^\s*(modify|update|change|replace|fix|add|insert|append|prepend)\b");
            var fileExists = System.IO.File.Exists(fullPath);
            var willBeCreatedEarlier = planSoFar.Any(p =>
                (p.File.Equals("_create_file", StringComparison.OrdinalIgnoreCase) ||
                 p.File.Equals("_command", StringComparison.OrdinalIgnoreCase)) &&
                (p.Change ?? "").Contains(Path.GetFileName(step.File), StringComparison.OrdinalIgnoreCase));
            if (!fileExists)
            {
                var similarExisting = AgentDiscovery.FindSameDirectoryFile(step.File, projectRoot);
                if (similarExisting != null)
                    return (false, $"Path '{step.File}' does not exist. A file with the same name ALREADY EXISTS at '{similarExisting}' — retarget this step to that path (do not create a duplicate).");
                if (!string.IsNullOrWhiteSpace(step.NewString) && string.IsNullOrWhiteSpace(step.OldString))
                {
                    var origPath = step.File;
                    step.File = "_create_file";
                    step.Change = origPath;
                    return (true, null);
                }
                // INVENTED-FILE GUARD: an edit step that is NOT create-eligible (it carries an
                // oldString anchor, or no content at all) targets a file that does not exist.
                // The resolver would "resolve" it into a full-file CREATION — the planner's
                // invented path silently materializes on disk — and the executor's own
                // "File does not exist" refusal is unreachable because the AST pre-resolution
                // drops the anchor first. Reject deterministically so the next proposal
                // re-grounds: use _create_file if the file is genuinely new, or target an
                // existing file. A file an earlier committed step creates (willBeCreatedEarlier)
                // is exempt — that edit is a legit follow-up, not an invention.
                if (!willBeCreatedEarlier)
                {
                    // EXTENDED STEERING: instead of a bare rejection, propose the closest REAL
                    // sibling path — the existing file the planner most likely meant (same leaf
                    // name in a different real directory, or a typo/plural/case variant in the
                    // closest real directory) — so the next proposal re-grounds in a real file
                    // instead of retrying the same fiction.
                    var sibling = AgentDiscovery.FindRealSiblingForInventedFile(step.File, projectRoot);
                    return (false,
                        sibling != null
                            ? $"Cannot edit '{step.File}' — the file does not exist in the project. " +
                              $"The closest real sibling is '{sibling}' — retarget this step to that existing file. " +
                              $"If the file should genuinely be created, use a _create_file step with the full file content in newString. NEVER invent file paths."
                            : $"Cannot edit '{step.File}' — the file does not exist in the project. " +
                              $"If the file should be created, use a _create_file step with the full file content in newString. " +
                              $"Otherwise target an existing file. NEVER invent file paths.");
                }
            }
            if (fileExists)
            {
                var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                var (verdict, reason) = PreEditValidation(content, step);
                if (verdict == PreEditVerdict.AlreadyDone)
                {
                    // HALLUCINATED-REMOVAL GUARD: when NO prior step has touched this file,
                    // "code to be removed is already absent from file" cannot mean "already done"
                    // — it means the planner invented a symbol that is NOT in the file (e.g. it
                    // decided a method was 'broken' and tried to delete it although no such method
                    // exists). Reject with corrective feedback that names the file's actual
                    // members, so the next proposal re-grounds in the real content instead of
                    // retrying the same fiction (which previously degraded into a loop of
                    // hallucinated edits). If an earlier step DID touch this file, a removal can
                    // legitimately be a no-op and is left to the resolver. Never intercept
                    // special markers (_command etc.).
                    if (reason.Contains(RemovalTargetAbsentReason, StringComparison.OrdinalIgnoreCase) &&
                        !AgentProjectUtilities.IsSpecialMarker(step.File) &&
                        !planSoFar.Any(p =>
                            !AgentProjectUtilities.IsSpecialMarker(p.File) &&
                            string.Equals(p.File.Replace('\\', '/'), step.File.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                    {
                        var inventory = ExtractMemberInventory(content);
                        return (false,
                            $"The removal target in '{step.File}' does NOT exist in the file" +
                            (inventory.Length > 0 ? $" — its members are: {inventory}" : "") +
                            ". Re-read the ATTACHED FILES / DISCOVERY CONTEXT above and propose a step grounded " +
                            "in those exact members. NEVER delete or reference a symbol that is not present in the file. " +
                            "If the task's work is already complete in the file, return planComplete=true instead of proposing further edits.");
                    }
                    // FROZEN-PLAN RULE: once the planner has produced a concrete edit
                    // (oldString/newString) for this step, the plan is frozen — a heuristic
                    // AlreadyDone verdict (often a false positive from oldString drift, e.g.
                    // "code to be removed is already absent") must NOT bounce the step back
                    // into LLM re-planning/re-thinking. Route it to the resolver, which
                    // re-attempts the actual edit with tolerant matchers. If the edit
                    // genuinely cannot apply, the resolver reports a no-op and the pipeline
                    // continues normally (crap edit → ignored).
                    if (HasConcreteEdit(step))
                    {
                        await EmitLog(emitSse, "bypass",
                            $"⚡ Frozen plan — [{step.File}] {step.Change} carries a concrete edit; overriding heuristic '{reason}' and re-attempting the edit via the resolver (no re-planning)", ct: ct);
                    }
                    else
                    {
                        return (false, $"Already satisfied in the current file — {reason}. Move on to the next requirement.");
                    }
                }
            }
        }
        if (isSpecial) return (true, null);
        if (skipLlm)
        {
            await EmitLog(emitSse, "info", $"LLM validator skipped (retry mode) — accepting step: [{step.File}] {step.Change}", ct: ct);
            return (true, null);
        }
        var sb = new StringBuilder();
        sb.AppendLine("### ORIGINAL TASK ###");
        sb.AppendLine(originalPrompt);
        sb.AppendLine();
        sb.AppendLine("### PLAN SO FAR (already committed, in order) ###");
        if (planSoFar.Count == 0) sb.AppendLine("(none yet — this would be the first step)");
        else for (var i = 0; i < planSoFar.Count; i++) sb.AppendLine($"  Step {i + 1}: [{planSoFar[i].File}] {planSoFar[i].Change}");
        sb.AppendLine();
        sb.AppendLine("### PROPOSED NEXT STEP ###");
        sb.AppendLine($"[{step.File}] {step.Change}");
        sb.AppendLine();
        sb.AppendLine("### RELEVANT DISCOVERY CONTEXT (target file only) ###");
        var validatorDiscovery = StripEditKnowledgeHeader(discoveryContext);
        var fileSection = AgentDiscovery.ExtractFileSectionFromContext(validatorDiscovery, step.File);
        sb.AppendLine(string.IsNullOrWhiteSpace(fileSection) ? validatorDiscovery : fileSection);
        sb.AppendLine();
        // The discovery context can be a STALE snapshot (captured before earlier committed
        // steps applied). A validator judging against it rejects valid follow-up steps that
        // reference symbols earlier steps already added — the live navigation
        // component.ts run: the validator claimed "no existing moviesTodoCount property"
        // right after step 1 had just added it. Read the CURRENT file from disk so the
        // judgment is grounded in the real state, not the snapshot.
        string? currentOnDisk = null;
        try
        {
            var vfp = Path.GetFullPath(Path.Combine(projectRoot,
                step.File.Replace('/', Path.DirectorySeparatorChar)));
            if (System.IO.File.Exists(vfp))
                currentOnDisk = await System.IO.File.ReadAllTextAsync(vfp, Encoding.UTF8, ct);
        }
        catch { }
        if (!string.IsNullOrWhiteSpace(currentOnDisk))
        {
            sb.AppendLine("### CURRENT FILE ON DISK (authoritative — read just now, AFTER all committed steps) ###");
            sb.AppendLine(currentOnDisk.Length > 6000 ? currentOnDisk[..6000] + "\n… (truncated)" : currentOnDisk);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(lastStepCompletionNote))
        {
            sb.AppendLine("### PREVIOUS STEP COMPLETED ###");
            sb.AppendLine(lastStepCompletionNote);
            sb.AppendLine();
        }
        sb.AppendLine("Judge the PROPOSED NEXT STEP ONLY. Answer:");
        sb.AppendLine("1. Does it reference any method/property/symbol that does NOT exist in the CURRENT FILE ON DISK " +
                      "(or the discovery context when no file was shown) " +
                      "AND is not introduced by an earlier committed step? (if so: invalid)");
        sb.AppendLine("   The CURRENT FILE ON DISK is the source of truth for what exists — prefer it over the discovery context.");
        sb.AppendLine("2. Does it contradict or redo anything already committed? (if so: invalid)");
        sb.AppendLine("3. Does it require a prerequisite step not yet committed (e.g. an endpoint before its DTO)? (if so: invalid)");
        sb.AppendLine("4. Is it a genuinely necessary, atomic step toward the ORIGINAL TASK (not scope creep)? (if not: invalid)");
        sb.AppendLine("5. If a previous step was marked complete (needsExtraStep=false), does this step address a GENUINELY DIFFERENT " +
                      "requirement — not a continuation, cleanup, or refinement of already-completed work? (if not: invalid)");
        sb.AppendLine();
        sb.AppendLine("Output ONLY JSON: {\"valid\": true|false, \"reason\": \"short reason, only if invalid\"}");
        var (raw, _, err) = await CallLlmRaw(
            "You are a strict plan-coherence validator. Output ONLY the requested JSON.",
            sb.ToString(), ct, _infiniteTimeout, maxTokens: 200);
        if (string.IsNullOrWhiteSpace(raw))
        {
            await EmitLog(emitSse, "warn", $"Coherence validator call failed ({err}) — accepting step by default.", ct: ct);
            return (true, null);
        }
        try
        {
            var cleaned = ExtractFirstJsonObject(raw);
            using var doc = JsonDocument.Parse(cleaned);
            var valid = !doc.RootElement.TryGetProperty("valid", out var v) || v.ValueKind != JsonValueKind.False;
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
            if (!valid && reason != null &&
                await FileTruthOverrideForRejectionAsync(reason, step, projectRoot, ct))
            {
                // The LLM rejected claiming a symbol is MISSING from the code, but the real
                // file on disk actually contains it (stale discovery-context judgment — the
                // live navigation.component.ts run: "no existing moviesTodoCount property"
                // right after step 1 added it). A rejection built on a false premise must
                // not starve the step.
                await EmitLog(emitSse, "info",
                    $"✓ Coherence validator rejection overridden (file-truth): [{step.File}] {step.Change} — " +
                    $"the reason claims a symbol is missing that the CURRENT FILE actually contains. Reason: {TruncateForLlm(reason, 140)}", ct: ct);
                return (true, null);
            }
            return (valid, valid ? null : (reason ?? "Rejected by coherence validator."));
        }
        catch
        {
            return (true, null);
        }
    }

    /// <summary>
    /// File-truth override for coherence-validator rejections. When the LLM rejects a step
    /// with a reason claiming a symbol is MISSING from the code ("no existing X", "X does
    /// not exist", "X is missing", "not implemented"…) but that symbol is actually present
    /// in the CURRENT file on disk, the rejection is built on a stale/false premise. Returns
    /// true only when the reason BOTH uses missing-phrasing AND names a real identifier the
    /// file contains — a genuinely-absent symbol never triggers the override.
    /// </summary>
    private static async Task<bool> FileTruthOverrideForRejectionAsync(
        string reason, PlanStep step, string projectRoot, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason) || step == null || string.IsNullOrWhiteSpace(step.File) ||
            AgentProjectUtilities.IsSpecialMarker(step.File)) return false;
        if (!Regex.IsMatch(reason,
                @"\b(?:no existing|does not exist|doesn'?t exist|not exist|is missing|\bmissing\b|does not have|doesnt have|absent|not implemented|undefined|never added|not found|not present)\b",
                RegexOptions.IgnoreCase))
            return false;
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
        // A camelCase identifier the reason names (moviesTodoCount, getMoviesInfo, …) that the
        // file actually contains disproves the "missing" claim.
        foreach (Match m in Regex.Matches(reason, @"[A-Za-z_$][A-Za-z0-9_$]*"))
        {
            var id = m.Value;
            if (id.Length < 4) continue;
            if (!(id.Any(char.IsLower) && id.Any(char.IsUpper))) continue; // camelCase-ish only
            if (Regex.IsMatch(current, $@"\b{Regex.Escape(id)}\b"))
                return true;
        }
        return false;
    }


    private async Task<(AgentPlan plan, string discoveryContext)> RunIncrementalPlanningLoop(
        AgentRunContext runContext, string prompt, string discoveryContext, string projectRoot, bool emitSse,
        CancellationToken ct, string? steeringContext, string? cardId = null,
        int? atomicStepEstimate = null, List<string>? attachedFiles = null)
    {
        var planSoFar = new List<PlanStep>();
        var rejectionFeedback = new List<string>();
        var exploredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var thinkingLog = new StringBuilder();
        var regenAttempts = 0;
        var consecutiveSlotFailures = 0;
        var totalPlanningRounds = 0;
        var stepEventIndex = 0;
        var webNeedVerified = 0; // 0 = unchecked, 1 = task needs web, -1 = no web needed
        var webNeedVerifyFailures = 0; // consecutive failed classifier calls — caps re-confirmation at 2 per run
        string? webInjectedQuery = null; // search query from the same verification call, used if we auto-inject _web_search
        var fetchCommandRejections = 0; // consecutive fetch-command rejections this slot — auto-inject _web_search at the regen cap
        string? webNeedVetoReason = null; // classifier's reason when web was vetoed — shown inline on the rejected step card
        // OS-filesystem task guard state: pure OS tasks reject repo file edits
        // deterministically; hint-y tasks get one memoized LLM verdict per file.
        var osTaskGuard = IsExternalFilesystemTask(prompt);
        var osTaskPure = osTaskGuard && !OsPromptHintsRepoWork(prompt);
        var osRepoEditVerdicts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        await EmitLog(emitSse, "info", "Incremental planning: proposing steps one at a time…", ct: ct);
        if (emitSse)
            await SendSse(Response, "plan", new
            {
                thinking = "",
                summary = "Building plan incrementally — 0 steps so far",
                items = Array.Empty<PlanStep>(),
                incremental = true
            }, ct);
        for (var turn = 0; turn < MAX_INCREMENTAL_STEPS; turn++)
        {
            ct.ThrowIfCancellationRequested();
            if (emitSse)
                await SendSse(Response, "phase", new { message = $"Planning — step {planSoFar.Count + 1}/{MAX_INCREMENTAL_STEPS}" }, ct);
            var proposal = await ProposeNextIncrementalStepAsync(
                runContext, prompt, discoveryContext, planSoFar, steeringContext, rejectionFeedback, emitSse, ct,
                atomicStepEstimate: atomicStepEstimate, projectRoot: projectRoot);
            if (proposal == null)
            {
                var jsonFb = "Your previous response could not be parsed as valid JSON. " +
                    "Output ONLY the JSON object described in the system prompt.";
                await EmitRejectedLog(emitSse, "Incremental planning: rejected — response was not valid JSON; retrying with parse feedback", jsonFb, ct);
                rejectionFeedback.Add(jsonFb);
                if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                continue;
            }
            if (proposal.PlanComplete)
            {
                // Web-need gate (mirror of the interleaved loop): declaring the plan
                // complete is itself a refusal to use the web tools when the task
                // needs current external info and no web step exists. Reject, and on
                // the regen cap inject the search so web-needing tasks cannot escape
                // by claiming completion.
                if (TaskHintsWebNeed(prompt) && !planSoFar.Any(s => IsWebStep(s.File)))
                {
                    if (webNeedVerified == 0 && webNeedVerifyFailures < 2)
                    {
                        await EmitLog(emitSse, "info",
                            "Task hints at needing current external info — checking whether _web_search is required…", ct: ct);
                        var (needsWeb, query, vetoReason, verified) = await ConfirmWebNeedAsync(prompt, emitSse, ct);
                        if (verified)
                        {
                            webNeedVerified = needsWeb ? 1 : -1;
                            if (needsWeb) webInjectedQuery = query;
                            else webNeedVetoReason = vetoReason;
                        }
                        else
                        {
                            webNeedVerifyFailures++; // transient classifier failure — retry up to 2×, then stop calling
                        }
                    }
                    if (webNeedVerified == 1)
                    {
                        await EmitRejectedLog(emitSse,
                            "Incremental planning: rejected plan-complete — task needs current external info but the plan has no _web_search step",
                            WebNeedFeedback, ct);
                        rejectionFeedback.Add(WebNeedFeedback);
                        if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS)
                        {
                            var queryText = string.IsNullOrWhiteSpace(webInjectedQuery)
                                ? BuildFallbackWebQuery(prompt)
                                : webInjectedQuery.Trim();
                            planSoFar.Add(new PlanStep { File = "_web_search", Change = queryText });
                            thinkingLog.AppendLine($"Step {planSoFar.Count}: [auto-injected] {queryText}");
                            if (emitSse)
                            {
                                await SendSse(Response, "thinking",
                                    new { text = $"Auto-injected _web_search step — the planner declared completion without it after {MAX_STEP_REGEN_ATTEMPTS} rejections." }, ct);
                                await SendSse(Response, "step", new
                                {
                                    index = ++stepEventIndex,
                                    type = "plan",
                                    status = "pending",
                                    path = "_web_search",
                                    description = queryText,
                                    line = 0,
                                    planItemIndex = planSoFar.Count
                                }, ct);
                            }
                            await EmitLog(emitSse, "warn",
                                $"The planner declared the plan complete without using _web_search after {MAX_STEP_REGEN_ATTEMPTS} rejections — auto-injecting a _web_search step: \"{queryText}\"", ct: ct);
                            break; // plan is done — the injected search executes with the rest
                        }
                        continue;
                    }
                }
                if (emitSse)
                    await SendSse(Response, "thinking", new { text = $"Plan complete: {proposal.CompletionReason}" }, ct);
                await EmitLog(emitSse, "success",
                    $"Incremental planning: plan complete after {planSoFar.Count} step(s) — {proposal.CompletionReason}", ct: ct);
                var budgetTxt = atomicStepEstimate is > 0
                    ? $" (estimated {atomicStepEstimate})" : "";
                await EmitLog(emitSse, "metric",
                    $"📊 Card planning: {planSoFar.Count} step(s){budgetTxt}, {totalPlanningRounds} total planning round(s)", ct: ct);
                break;
            }
            if (!string.IsNullOrWhiteSpace(proposal.ExploreFile))
            {
                if (emitSse)
                    await SendSse(Response, "step", new
                    {
                        index = ++stepEventIndex,
                        type = "explore",
                        status = "exploring",
                        path = proposal.ExploreFile,
                        description = $"Exploring: {proposal.ExploreFile}",
                        planItemIndex = planSoFar.Count,
                        message = proposal.Thinking ?? ""
                    }, ct);
                if (exploredFiles.Add(proposal.ExploreFile))
                {
                    if (proposal.ExploreFile.Equals("_discover", StringComparison.OrdinalIgnoreCase))
                    {
                        await EmitLog(emitSse, "info", "Planner requested _discover — running project-wide search…", ct: ct);
                        discoveryContext = await RunDiscoveryToolAsync(prompt, discoveryContext, projectRoot, emitSse, ct);
                        await EmitContextUpdateAsync(runContext, discoveryContext, emitSse, ct);
                        regenAttempts = 0;
                        continue;
                    }
                    var isMarker = proposal.ExploreFile.StartsWith("_");
                    var alreadyInContext = false;
                    if (!isMarker)
                    {
                        var normPath = proposal.ExploreFile.Replace('\\', '/').TrimStart('/');
                        // A file present ONLY as a focused region is NOT fully in context —
                        // a later re-request may legitimately need a different symbol's region.
                        alreadyInContext = !IsFocusedSectionInContext(discoveryContext, normPath) &&
                                           (discoveryContext.Contains($"### read {normPath}") ||
                                            discoveryContext.Contains($"### {normPath}") ||
                                            Regex.IsMatch(discoveryContext, $@"### (?:read )?\S*{Regex.Escape(Path.GetFileName(normPath))}\b"));
                    }
                    if (alreadyInContext)
                    {
                        var contextMsg = $"STOP — '{proposal.ExploreFile}' is ALREADY in the DISCOVERY CONTEXT above (its full content is already shown). " +
                            "Do NOT request it again. Read the file content from the DISCOVERY CONTEXT and propose the actual edit step now.";
                        await EmitRejectedLog(emitSse,
                            $"Incremental planning: rejected explore — '{proposal.ExploreFile}' already in discovery context", contextMsg, ct);
                        rejectionFeedback.Add(contextMsg);
                        if (emitSse)
                            await SendSse(Response, "step", new
                            {
                                index = ++stepEventIndex,
                                type = "explore",
                                status = "error",
                                path = proposal.ExploreFile,
                                description = $"Already in context: {proposal.ExploreFile}",
                                error = contextMsg
                            }, ct);
                        if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                        continue;
                    }
                    await EmitLog(emitSse, "info", $"Incremental planning: exploring {proposal.ExploreFile}", ct: ct);
                    discoveryContext = await ExplorationPipeline(
                        new List<PlanStep> { new() { File = "_explore", Change = proposal.ExploreFile } },
                        discoveryContext, projectRoot, emitSse, ct, prompt);
                    await EmitContextUpdateAsync(runContext, discoveryContext, emitSse, ct);
                    if (emitSse)
                        await SendSse(Response, "step", new
                        {
                            index = ++stepEventIndex,
                            type = "explore",
                            status = "done",
                            path = proposal.ExploreFile,
                            description = $"Explored: {proposal.ExploreFile}"
                        }, ct);
                    if (!string.IsNullOrWhiteSpace(cardId))
                    {
                        await AutoAttachFileToCardAsync(cardId, proposal.ExploreFile, emitSse, ct);
                    }
                    regenAttempts = 0;
                    continue;
                }
                else
                {
                    // The earlier read may have been only a FOCUSED region (the file was
                    // never shown in full) — then re-exploring is legitimate: the model
                    // can surface a different symbol's region. Only reject when the file
                    // is genuinely shown in full already.
                    var normPath = proposal.ExploreFile.Replace('\\', '/').TrimStart('/');
                    var focusedOnly = !proposal.ExploreFile.StartsWith("_") &&
                        IsFocusedSectionInContext(discoveryContext, normPath);
                    if (!focusedOnly)
                    {
                        var exploreFb =
                            $"You asked to explore '{proposal.ExploreFile}' again — it is ALREADY shown in full in the " +
                            "DISCOVERY CONTEXT above. Do not re-request it. Read it carefully and propose the actual next " +
                            "step now, using the exact symbol/method names visible there.";
                        await EmitRejectedLog(emitSse,
                            $"Incremental planning: rejected explore — '{proposal.ExploreFile}' already shown in full; retrying", exploreFb, ct);
                        rejectionFeedback.Add(exploreFb);
                        if (emitSse)
                        {
                            await SendSse(Response, "step", new
                            {
                                index = ++stepEventIndex,
                                type = "explore",
                                status = "error",
                                path = proposal.ExploreFile,
                                description = $"Already explored: {proposal.ExploreFile}"
                            }, ct);
                        }
                        if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) { break; }
                        continue;
                    }
                    await EmitLog(emitSse, "info",
                        $"Incremental planning: re-exploring {proposal.ExploreFile} for another focused region", ct: ct);
                    discoveryContext = await ExplorationPipeline(
                        new List<PlanStep> { new() { File = "_explore", Change = proposal.ExploreFile } },
                        discoveryContext, projectRoot, emitSse, ct, prompt);
                    await EmitContextUpdateAsync(runContext, discoveryContext, emitSse, ct);
                    if (emitSse)
                        await SendSse(Response, "step", new
                        {
                            index = ++stepEventIndex,
                            type = "explore",
                            status = "done",
                            path = proposal.ExploreFile,
                            description = $"Explored: {proposal.ExploreFile}"
                        }, ct);
                    if (!string.IsNullOrWhiteSpace(cardId))
                    {
                        await AutoAttachFileToCardAsync(cardId, proposal.ExploreFile, emitSse, ct);
                    }
                    regenAttempts = 0;
                    continue;
                }
            }
            if (proposal.Step == null)
            {
                var neitherFb = "You returned neither planComplete=true, exploreFile, nor a step — return exactly one.";
                await EmitRejectedLog(emitSse, "Incremental planning: rejected — returned neither planComplete, exploreFile, nor a step; retrying", neitherFb, ct);
                rejectionFeedback.Add(neitherFb);
                if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                continue;
            }
            if (proposal.Step.File.Equals("_discover", StringComparison.OrdinalIgnoreCase))
            {
                if (exploredFiles.Add("_discover"))
                {
                    await EmitLog(emitSse, "info", "Incremental planning: _discover step — running project-wide search…", ct: ct);
                    discoveryContext = await RunDiscoveryToolAsync(prompt, discoveryContext, projectRoot, emitSse, ct);
                    await EmitContextUpdateAsync(runContext, discoveryContext, emitSse, ct);
                    regenAttempts = 0;
                    continue;
                }
                var discoverFb = "You already ran _discover — its results are now in the DISCOVERY CONTEXT. Use them to propose the next step.";
                await EmitRejectedLog(emitSse, "Incremental planning: rejected — _discover was already run this session; retrying", discoverFb, ct);
                rejectionFeedback.Add(discoverFb);
                if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                continue;
            }
            // Web-step gate: a _web_search/_web_fetch proposal is allowed freely once the LLM
            // has confirmed the task needs the web (webNeedVerified == 1). Otherwise the first
            // web proposal triggers ONE confirmation round; if the verdict is that the task
            // does NOT need current external info, the web step is rejected with feedback
            // steering back to the repo context (no further web steps this run).
            if (IsWebStep(proposal.Step.File) && webNeedVerified != 1)
            {
                if (webNeedVerified == 0 && webNeedVerifyFailures < 2)
                {
                    await EmitLog(emitSse, "info",
                        "The planner proposed a _web_search/_web_fetch step — checking whether the task actually needs current external info…", ct: ct);
                    var (needsWeb, query, vetoReason, verified) = await ConfirmWebNeedAsync(prompt, emitSse, ct);
                    if (verified)
                    {
                        webNeedVerified = needsWeb ? 1 : -1;
                        if (needsWeb) webInjectedQuery = query;
                        else webNeedVetoReason = vetoReason;
                    }
                    else
                    {
                        webNeedVerifyFailures++; // transient classifier failure — retry up to 2×, then fail open
                    }
                }
                if (webNeedVerified == -1)
                {
                    // Surface the classifier's own reason inline on the rejected step card
                    // (the plan-item error row) so a false veto is auditable without
                    // expanding the transcript.
                    var vetoDetail = string.IsNullOrWhiteSpace(webNeedVetoReason)
                        ? ""
                        : $" Classifier: {webNeedVetoReason}";
                    await EmitRejectedLog(emitSse,
                        $"Incremental planning: rejected [{proposal.Step.File}] {proposal.Step.Change} — task does not need current external info.{vetoDetail}",
                        WebNotNeededFeedback + vetoDetail, ct);
                    rejectionFeedback.Add(WebNotNeededFeedback + vetoDetail);
                    if (emitSse)
                        await SendSse(Response, "step", new
                        {
                            index = ++stepEventIndex,
                            type = "plan",
                            status = "rejected",
                            path = proposal.Step.File,
                            description = proposal.Step.Change,
                            error = $"Task does not need current external info.{vetoDetail}",
                            line = proposal.Step.LineNumber,
                            planItemIndex = planSoFar.Count
                        }, ct);
                    // Persist the veto on the card's plan so it survives a reload.
                    await PersistRejectedPlanStepAsync(cardId, proposal.Step, $"Task does not need current external info.{vetoDetail}", emitSse, ct);
                    if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                    continue;
                }
                // webNeedVerified == 1 after the confirmation round → allow the web step freely.
            }
            // Missing-web-search guard (mirror of the interleaved loop): if the task
            // hints at needing CURRENT EXTERNAL information but neither the plan nor
            // this proposal carries a _web_search/_web_fetch step, confirm with the
            // LLM (regex alone is too noisy — "search for" usually means searching
            // the repo) and, when confirmed, reject with feedback steering the model
            // to the web tools. If the model still refuses after
            // MAX_STEP_REGEN_ATTEMPTS rejections, auto-inject a _web_search step so
            // genuinely web-needing tasks always get the search.
            if (proposal.Step.File != null &&
                !IsWebStep(proposal.Step.File) &&
                // Filesystem PREP is not a refusal to use the web tools — a web-needing task
                // that demands a folder ("create a folder called X …") must be able to create
                // it up front instead of bouncing mkdir for 3 rejections before the search is
                // auto-injected. _create_directory and mkdir-style _command steps qualify
                // (the dump destination is additionally pre-created in the web step itself).
                !IsFilesystemPrepStep(proposal.Step) &&
                !planSoFar.Any(s => IsWebStep(s.File)) &&
                TaskHintsWebNeed(prompt))
            {
                if (webNeedVerified == 0 && webNeedVerifyFailures < 2)
                {
                    await EmitLog(emitSse, "info",
                        "Task hints at needing current external info — checking whether _web_search is required…", ct: ct);
                    var (needsWeb, query, vetoReason, verified) = await ConfirmWebNeedAsync(prompt, emitSse, ct);
                    if (verified)
                    {
                        webNeedVerified = needsWeb ? 1 : -1;
                        if (needsWeb) webInjectedQuery = query;
                        else webNeedVetoReason = vetoReason;
                    }
                    else
                    {
                        webNeedVerifyFailures++; // transient classifier failure — retry up to 2×, then fail closed
                    }
                }
                if (webNeedVerified == 1)
                {
                    // When the task explicitly ASKS for a script, the scraper steering stands
                    // down (writing it is the task) — only the generic web feedback remains.
                    var webFb = DirectoryIntentFeedback(proposal.Step)
                        ?? (TaskExplicitlyRequestsScript(prompt) ? null : ScraperScriptFeedback(proposal.Step))
                        ?? WebNeedFeedback;
                    await EmitRejectedLog(emitSse,
                        $"Incremental planning: rejected [{proposal.Step.File}] {proposal.Step.Change} — task needs current external info but the plan has no _web_search step",
                        webFb, ct);
                    rejectionFeedback.Add(webFb);
                    if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS)
                    {
                        // The planner kept refusing _web_search after MAX_STEP_REGEN_ATTEMPTS
                        // rejections. Genuinely web-needing tasks must still complete, so stop
                        // asking and inject the web step directly into the plan; the model can
                        // then plan the follow-up steps (e.g. writing the results to a file).
                        var queryText = string.IsNullOrWhiteSpace(webInjectedQuery)
                            ? BuildFallbackWebQuery(prompt)
                            : webInjectedQuery.Trim();
                        planSoFar.Add(new PlanStep { File = "_web_search", Change = queryText });
                        thinkingLog.AppendLine($"Step {planSoFar.Count}: [auto-injected] {queryText}");
                        if (emitSse)
                        {
                            await SendSse(Response, "thinking",
                                new { text = $"Auto-injected _web_search step — the planner refused after {MAX_STEP_REGEN_ATTEMPTS} rejections." }, ct);
                            await SendSse(Response, "step", new
                            {
                                index = ++stepEventIndex,
                                type = "plan",
                                status = "pending",
                                path = "_web_search",
                                description = queryText,
                                line = 0,
                                planItemIndex = planSoFar.Count
                            }, ct);
                        }
                        await EmitLog(emitSse, "warn",
                            $"The planner refused to use _web_search after {MAX_STEP_REGEN_ATTEMPTS} rejections — auto-injecting a _web_search step: \"{queryText}\"", ct: ct);
                        regenAttempts = 0;
                        rejectionFeedback.Clear();
                        continue; // model can now plan the follow-up steps around the injected search
                    }
                    continue;
                }
            }
            if (emitSse && !string.IsNullOrWhiteSpace(proposal.Thinking))
            {
                await SendSse(Response, "thinking", new { text = proposal.Thinking }, ct);
            }
            var stepIndex = ++stepEventIndex;
            if (emitSse)
            {
                await SendSse(Response, "step", new
                {
                    index = stepIndex,
                    type = "plan",
                    status = "proposing",
                    path = proposal.Step.File,
                    description = proposal.Step.Change,
                    line = proposal.Step.LineNumber,
                    planItemIndex = planSoFar.Count,
                    thinking = proposal.Thinking,
                    justification = proposal.CompletionReason
                }, ct);
            }
            // Task-tool mismatch guard (mirror of the web-need gate): a task that
            // targets the OS filesystem outside the repo must NOT be implemented by
            // editing repo source files — that is the "wrote an HTTP endpoint to
            // create a desktop folder" failure. Pure OS tasks reject repo edits
            // deterministically; when the task also hints at repo work (e.g. "save a
            // link in the README"), one memoized LLM call per file adjudicates
            // whether the edit is genuinely required.
            if (osTaskGuard &&
                proposal.Step.File != null &&
                !AgentProjectUtilities.IsSpecialMarker(proposal.Step.File))
            {
                var rejectOsEdit = osTaskPure;
                if (!osTaskPure)
                {
                    var allowed = false;
                    if (osRepoEditVerdicts.TryGetValue(proposal.Step.File, out var cached))
                    {
                        allowed = cached;
                    }
                    else
                    {
                        var (isRequired, verified) = await ConfirmRepoEditRequiredAsync(prompt, proposal.Step.File, proposal.Step.Change, emitSse, ct);
                        // Cache only SUCCESSFUL verdicts — a transient LLM failure must
                        // not permanently lock a file out of a legitimately needed edit;
                        // a later re-proposal then retries the verification. A cached
                        // "allowed" also lets subsequent edits to that file through.
                        if (verified) osRepoEditVerdicts[proposal.Step.File] = isRequired;
                        allowed = isRequired;
                    }
                    rejectOsEdit = !allowed;
                }
                if (rejectOsEdit)
                {
                    await EmitRejectedLog(emitSse,
                        $"Incremental planning: rejected [{proposal.Step.File}] {proposal.Step.Change} — task targets the OS filesystem, not the repository",
                        OsTaskEditFeedback, ct);
                    await PersistRejectedPlanStepAsync(cardId, proposal.Step, OsTaskEditFeedback, emitSse, ct);
                    rejectionFeedback.Add(OsTaskEditFeedback);
                    if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                    continue;
                }
            }
            var skipLlm = regenAttempts > 0;
            if (!skipLlm && proposal.Step != null && !AgentProjectUtilities.IsSpecialMarker(proposal.Step.File))
            {
                var stepFile = proposal.Step?.File ?? "";
                var fullPath = Path.GetFullPath(Path.Combine(projectRoot,
                    stepFile.Replace('/', Path.DirectorySeparatorChar)));
                if (System.IO.File.Exists(fullPath))
                    skipLlm = true;
            }
            if (proposal.Step != null)
            {
                var (valid, reason) = await ValidateIncrementalStepAsync(
                    proposal.Step, prompt, discoveryContext, planSoFar, projectRoot, emitSse, ct,
                    skipLlm: skipLlm, attachedFiles: attachedFiles);
                if (!valid)
                {
                    if (reason != null && reason.StartsWith(FetchCommandFeedbackMarker, StringComparison.Ordinal)) fetchCommandRejections++;
                    var stepFb = $"REJECTED — [{proposal.Step.File}] {proposal.Step.Change} → {reason}";
                    await EmitRejectedLog(emitSse,
                        $"Incremental planning: rejected [{proposal.Step.File}] {proposal.Step.Change} — {reason}", stepFb, ct);
                    rejectionFeedback.Add(stepFb);
                    if (emitSse)
                        await SendSse(Response, "step", new
                        {
                            index = stepIndex,
                            type = "plan",
                            status = "rejected",
                            path = proposal.Step.File,
                            description = proposal.Step.Change,
                            error = reason,
                            line = proposal.Step.LineNumber,
                            planItemIndex = planSoFar.Count
                        }, ct);
                    // Persist the rejected step on the card's plan so every rejection
                    // (OS-task guard, fetch-command guard, duplicates, ...) survives a reload.
                    await PersistRejectedPlanStepAsync(cardId, proposal.Step, reason, emitSse, ct);
                    if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS)
                    {
                        // FETCH-COMMAND AUTO-INJECT: the planner kept proposing URL-fetching
                        // _command steps instead of a web step (the "api.current.ai" failure
                        // mode) — inject _web_search directly, mirroring the missing-web-search
                        // auto-inject, so the fetch-command loop can't dead-end in a slot failure.
                        if (fetchCommandRejections >= 2)
                        {
                            var queryText = string.IsNullOrWhiteSpace(webInjectedQuery)
                                ? BuildFallbackWebQuery(prompt)
                                : webInjectedQuery.Trim();
                            planSoFar.Add(new PlanStep { File = "_web_search", Change = queryText });
                            thinkingLog.AppendLine($"Step {planSoFar.Count}: [auto-injected] {queryText}");
                            if (emitSse)
                            {
                                await SendSse(Response, "thinking",
                                    new { text = $"Auto-injected _web_search step — the planner kept proposing URL-fetching _command steps after {MAX_STEP_REGEN_ATTEMPTS} rejections." }, ct);
                                await SendSse(Response, "step", new
                                {
                                    index = ++stepEventIndex,
                                    type = "plan",
                                    status = "pending",
                                    path = "_web_search",
                                    description = queryText,
                                    line = 0,
                                    planItemIndex = planSoFar.Count
                                }, ct);
                            }
                            await EmitLog(emitSse, "warn",
                                $"The planner kept proposing URL-fetching _command steps instead of _web_search after {MAX_STEP_REGEN_ATTEMPTS} rejections — auto-injecting a _web_search step: \"{queryText}\"", ct: ct);
                            rejectionFeedback.Clear();
                            regenAttempts = 0;
                            fetchCommandRejections = 0;
                            consecutiveSlotFailures = 0;
                            continue;
                        }
                        consecutiveSlotFailures++;
                        await EmitLog(emitSse, "warn",
                            $"Incremental planning: giving up on this slot after {MAX_STEP_REGEN_ATTEMPTS} rejections — moving on. " +
                            $"({consecutiveSlotFailures} consecutive slot failures)", ct: ct);
                        if (emitSse)
                            await SendSse(Response, "thinking", new
                            {
                                text = $"Giving up on this slot after {MAX_STEP_REGEN_ATTEMPTS} rejections — moving on. ({consecutiveSlotFailures} consecutive slot failures)"
                            }, ct);
                        rejectionFeedback.Clear();
                        regenAttempts = 0;
                        if (consecutiveSlotFailures >= 3)
                            throw new InvalidOperationException(
                                "Incremental planner failed 3 slots in a row — the discovery context likely doesn't contain " +
                                "what the task needs (wrong file attached, or the target method/property doesn't exist as described). " +
                                "Attach the correct file(s) and retry.");
                        continue;
                    }
                    continue;
                }
                consecutiveSlotFailures = 0;
                fetchCommandRejections = 0;
                var isDuplicate = false;
                foreach (var existing in planSoFar)
                {
                    if (existing.File == "_noop") continue;
                    var checks = 0;
                    if (string.Equals(existing.File, proposal.Step.File, StringComparison.OrdinalIgnoreCase)) checks++;
                    if (string.Equals(existing.Change, proposal.Step.Change, StringComparison.Ordinal) ||
                        (existing.Change?.Length > 10 && proposal.Step.Change?.Length > 10 &&
                         (existing.Change.Contains(proposal.Step.Change) || proposal.Step.Change.Contains(existing.Change)))) checks++;
                    if ((!string.IsNullOrEmpty(existing.OldString) && string.Equals(existing.OldString, proposal.Step.OldString, StringComparison.Ordinal)) ||
                        (!string.IsNullOrEmpty(existing.NewString) && string.Equals(existing.NewString, proposal.Step.NewString, StringComparison.Ordinal)) ||
                        (!string.IsNullOrEmpty(existing.TargetSymbol) && string.Equals(existing.TargetSymbol, proposal.Step.TargetSymbol, StringComparison.Ordinal))) checks++;
                    if (checks >= 2) { isDuplicate = true; break; }
                }
                if (isDuplicate)
                {
                    await EmitLog(emitSse, "info",
                        $"Plan complete — duplicate step proposed: [{proposal.Step.File}] {proposal.Step.Change} — nothing new to add", ct: ct);
                    break;
                }
                planSoFar.Add(proposal.Step);
                rejectionFeedback.Clear();
                var planningRounds = regenAttempts + 1;
                totalPlanningRounds += planningRounds;
                regenAttempts = 0;
                var retryWord = planningRounds - 1 == 1 ? "retry" : "retries";
                await EmitLog(emitSse, planningRounds >= 3 ? "warn" : "metric",
                    planningRounds > 1
                        ? $"📊 Step {planSoFar.Count} planned in {planningRounds} rounds ({planningRounds - 1} {retryWord}) — [{proposal.Step.File}] {proposal.Step.Change}"
                        : $"📊 Step {planSoFar.Count} planned in 1 round — [{proposal.Step.File}] {proposal.Step.Change}", ct: ct);
                if (!string.IsNullOrWhiteSpace(proposal.Thinking))
                {
                    thinkingLog.AppendLine($"Step {planSoFar.Count}: {proposal.Thinking}");
                }
                await EmitLog(emitSse, "info",
                    $"Incremental planning: committed step {planSoFar.Count} — [{proposal.Step.File}] {proposal.Step.Change}", ct: ct);
                if (emitSse)
                {
                    await SendSse(Response, "step", new
                    {
                        index = stepIndex,
                        type = "plan",
                        status = "pending",
                        path = proposal.Step.File,
                        description = proposal.Step.Change,
                        line = proposal.Step.LineNumber,
                        planItemIndex = planSoFar.Count,
                        message = proposal.CompletionReason
                    }, ct);
                    await SendSse(Response, "plan", new
                    {
                        thinking = thinkingLog.ToString(),
                        summary = $"Building plan incrementally — {planSoFar.Count} step(s) so far",
                        items = planSoFar,
                        incremental = true
                    }, ct);
                }
            }
            break;
        }
        if (planSoFar.Count == 0)
            throw new InvalidOperationException("Incremental planner did not produce any actionable steps.");
        var plan = new AgentPlan
        {
            Thinking = thinkingLog.ToString(),
            Summary = $"Built incrementally across {planSoFar.Count} validated step(s)",
            Score = 90,
            Plan = planSoFar
        };
        return (plan, discoveryContext);
    }
    private async Task SendPlanActivityEventAsync(
        StringBuilder thinkingLog, List<PlanStep> planSoFar, bool emitSse,
        string activityFile, string activityChange, string summary, int? runningIndex, CancellationToken ct)
    {
        if (!emitSse) return;
        List<Object> stepItems = planSoFar.Select((s, idx) => new
        {
            File = s.File,
            Change = s.Change,
            Line = s.LineNumber,
            OldString = s.OldString,
            NewString = s.NewString,
            done = runningIndex == null || idx < runningIndex.Value
        }).ToList<object>();
        // The transient activity marker ("Deep thinking for plan — Step 3…",
        // "Proposing step 2…", "Applying edits — Step 2 — …") is NEVER a plan step:
        // it must not appear in `items` (no checkbox, no done flag, not counted by
        // the plan gate). It is sent as a separate `marker` so the UI renders it as
        // a bottom-of-plan status line while the current step is being produced.
        // Emitted even before the first step is committed (items empty) so the UI
        // streams verbose thinking/planning/editing updates instead of freezing on
        // the initial "Reading task…" placeholder during the (often minutes-long)
        // discovery + pre-plan thinking phase.
        await SendSse(Response, "plan", new
        {
            thinking = thinkingLog.ToString(),
            summary = summary,
            items = stepItems,
            marker = new { File = activityFile, Change = activityChange },
            incremental = true,
            live = true
        }, ct);
    }

    /// <summary>
    // SMART WEB-CONTEXT BUDGET: the raw output a _web_fetch returns (a whole tag-stripped
    // page) can be megabytes, but every prompt that consumes it is token-bounded. The budget
    // is split by consumer instead of one hard chop:
    //   - DISCOVERY CONTEXT (planner / thinking / assessment / replay seed): compact
    //     20k-per-section with a SHARED 60k total — the decision-critical input (the
    //     harvested search results' titles + URLs) lives in the first few kB of each
    //     section, and the shared total bounds a long web chain instead of growing it
    //     without limit.
    //   - EDIT-RESOLUTION (HarvestWebResultsForEditContext): a LONGER 60k-per-section
    //     prefix pulled straight from allResults ON DEMAND — this is the prompt that
    //     actually generates file content, so it gets ~3x the fetched article to copy
    //     facts from instead of fabricating them. allResults always keeps the FULL output.
    private const int MaxWebSectionChars = 20000;
    private const int MaxWebTotalChars = 60000;
    private const int MaxWebSectionCharsForEdit = 60000;
    private const int MaxWebTotalCharsForEdit = 60000;

    /// <summary>
    /// Harvests executed _web_search/_web_fetch outputs from a step's results and appends
    /// them to the discovery context so the NEXT planning/thinking round can see what the
    /// search/fetch actually returned. ExecuteWebPlanStep accumulates results into a local
    /// webCtx that is only flushed via ReplanRemainingSteps when the SAME plan has further
    /// steps — in the interleaved loop each step runs as its own single-step plan, so that
    /// flush never fires and the model re-invented scraping code instead of using results.
    /// Sections are capped per-section (20k) and against a shared total budget, so a long
    /// web chain is bounded; callers needing more (the edit-resolution) pass a larger
    /// per-section cap — the full output always stays in allResults.
    /// </summary>
    private static string AppendWebResultsToDiscoveryContext(
        string discoveryContext, IEnumerable<Dictionary<string, object?>> newResults,
        int maxSectionChars = MaxWebSectionChars, int maxTotalChars = MaxWebTotalChars)
    {
        var remaining = maxTotalChars;
        var dropped = 0;
        foreach (var r in newResults)
        {
            var type = r.GetValueOrDefault("type")?.ToString();
            if (!IsWebStep(type)) continue;
            // Only successful fetches are real web data — an error result carrying text (e.g. an
            // exception message) must never be presented to the planner as search output.
            if (r.GetValueOrDefault("status")?.ToString() != "done") continue;
            var query = r.GetValueOrDefault("query")?.ToString() ?? r.GetValueOrDefault("url")?.ToString() ?? "";
            var output = r.GetValueOrDefault("output")?.ToString();
            if (string.IsNullOrWhiteSpace(output) || output.Length <= 80) continue;
            if (remaining <= 0) { dropped++; continue; }
            var alloc = Math.Min(output.Length, Math.Min(maxSectionChars, remaining));
            var capped = alloc < output.Length ? output[..alloc] + "…" : output;
            discoveryContext += $"\n\n### WEB RESULTS [{query}] ###\n{capped}\n";
            remaining -= alloc;
        }
        if (dropped > 0)
            discoveryContext += $"\n…[web-results budget exhausted — {dropped} later section(s) omitted from context]\n";
        return discoveryContext;
    }

    /// <summary>
    /// Carries a deterministically-recovered server forward in context: when a _command
    /// server start hit EADDRINUSE and was restarted on a free port (portRecovered result),
    /// the resolved URL MUST reach the planner so the next step — the _browser_test
    /// navigation, a curl probe, or the /api/health check — targets the port that actually
    /// answered instead of the busy default. Replaced in place so a later recovery (a
    /// second port bump) never stacks stale sections.
    /// </summary>
    private static string AppendServerRecoveryToDiscoveryContext(
        string discoveryContext, IEnumerable<Dictionary<string, object?>> newResults)
    {
        var recovered = newResults.FirstOrDefault(r => r.GetValueOrDefault("portRecovered") is true);
        if (recovered == null) return discoveryContext;
        var url = recovered.GetValueOrDefault("serverUrl")?.ToString();
        var port = recovered.GetValueOrDefault("serverPort")?.ToString();
        if (string.IsNullOrWhiteSpace(url)) return discoveryContext;
        var note = $"\n### RUNNING SERVER ###\n" +
                   $"The server from the previous step is RUNNING at {url}" +
                   (string.IsNullOrWhiteSpace(port) ? "" : $" (free port {port})") +
                   $" — the configured port was already in use, so this is the free port the server actually bound. " +
                   $"Use THIS URL/port for the browser test, /api/health checks, and any navigation — do NOT retry the busy default port.\n";
        const string marker = "### RUNNING SERVER ###";
        var start = discoveryContext.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return discoveryContext + note;
        var end = discoveryContext.IndexOf("\n### ", start + marker.Length, StringComparison.Ordinal);
        var stale = end >= 0 ? discoveryContext[..start] + discoveryContext[(end + 1)..] : discoveryContext[..start];
        return stale + note;
    }

    /// <summary>
    /// Harvests executed web results from the run's results for injection into the
    /// edit-resolution prompt, so FORMAT C/D / oldString-newString generation can copy real
    /// titles, URLs, and facts into newString instead of inventing them. This is the
    /// ON-DEMAND consumer: it pulls a LONGER prefix (60k/section) straight from allResults
    /// — where the full fetch output always lives — than the discovery context carries, so
    /// an edit that writes fetched article data sees ~3x the article to copy from. Returns
    /// "" when there are no usable web results.
    /// </summary>
    private static string HarvestWebResultsForEditContext(IEnumerable<Dictionary<string, object?>> results)
    {
        var ctx = AppendWebResultsToDiscoveryContext("", results,
            MaxWebSectionCharsForEdit, MaxWebTotalCharsForEdit);
        return ctx.TrimStart('\n', ' ', '\t');
    }

    /// <summary>
    /// Appends '### CREATED {path} (… exists on disk, current state) ###' sections for
    /// successfully created directories/files so later planning/thinking/verification turns
    /// see the filesystem actually changed. A _create_directory result used to VANISH from
    /// long-term context: web results and edits get appended after each step, but a bare
    /// create result is neither a read nor an edit, and the FILES-IN inventory skips empty
    /// directories — so the reasoning engine concluded "no confirmation this step actually
    /// completed successfully — only its intent is recorded" and the planner re-proposed
    /// mkdir, while the between-steps verifier claimed the folder was "never physically
    /// created". Existence is verified on disk so a failed create is never reported as real.
    /// </summary>
    private static string AppendCreatedPathsToDiscoveryContext(
        string discoveryContext, IEnumerable<Dictionary<string, object?>> newResults, string projectRoot)
    {
        var created = newResults
            .Where(r => r.GetValueOrDefault("type")?.ToString() == "create")
            .Where(r => r.GetValueOrDefault("status")?.ToString() is "done" or "created")
            .Select(r => r.GetValueOrDefault("path")?.ToString())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (created.Count == 0) return discoveryContext;
        var sb = new StringBuilder();
        foreach (var rel in created)
        {
            var full = Path.GetFullPath(Path.Combine(projectRoot, rel!.Replace('/', Path.DirectorySeparatorChar)));
            var kind = System.IO.Directory.Exists(full) ? "directory"
                : System.IO.File.Exists(full) ? "file"
                : "path (not found on disk)";
            sb.AppendLine($"### CREATED {rel} ({kind} — exists on disk, current state) ###");
        }
        return discoveryContext + "\n" + sb.ToString().TrimEnd();
    }

    /// <summary>
    /// LLM-confirmed version of the missing-web-search gate. Regex hints alone
    /// false-positive on repo-internal "search"/"look up" phrasing, so a cheap
    /// classifier call decides whether the task genuinely needs current external
    /// information.
    /// Tri-state return: Verified=false when the classifier call failed (empty/parse
    /// error) — callers decide how to fail: the missing-web-search guard fails CLOSED
    /// (keeps the non-web plan, caches no verdict so a re-proposal retries), while the
    /// web-step gate fails OPEN (allows the web step) so a transient LLM blip can
    /// never block a genuinely web-needing task.
    /// When the task DOES need the web, the same single call also yields the search
    /// query used later for the auto-injected _web_search step if the planner
    /// refuses to plan one after repeated rejections.
    /// </summary>
    private async Task<(bool NeedsWeb, string? Query, string Reason, bool Verified)> ConfirmWebNeedAsync(string prompt, bool emitSse, CancellationToken ct)
    {
        // An explicit web-search command in the task text is authoritative: the user
        // told the agent to search the web, so no classifier call may veto it. This is
        // the difference between "search for X" (usually repo search — the classifier
        // decides) and "search the web for X" (an explicit instruction — always web).
        if (TaskExplicitlyCommandsWeb(prompt))
        {
            await EmitLog(emitSse, "info",
                "Task explicitly commands a web search — treating the task as web-required without a classifier call.", ct: ct);
            return (true, BuildFallbackWebQuery(prompt), "", true);
        }
        try
        {
            var taskPreview = AgentTextUtilities.Truncate(prompt ?? "", 1200);
            var sb = new StringBuilder();
            sb.AppendLine("TASK:");
            sb.AppendLine(taskPreview);
            sb.AppendLine();
            sb.AppendLine("DECIDE: does this task REQUIRE CURRENT, EXTERNAL information from the web (live prices, breaking news, up-to-date API docs, latest package versions, facts that change over time) — information that CANNOT be found or computed inside the local repository?");
            sb.AppendLine("Tasks about the repo's own code, files, or tests NEVER need the web, even if they contain words like 'search', 'look up', or 'find'.");
            sb.AppendLine("An EXPLICIT instruction to search the web, browse the internet, or fetch from an online source is decisive: such tasks DO need the web.");
            sb.AppendLine("Output ONLY JSON: {\"needsWeb\": true|false, \"reason\": \"one short sentence\", \"query\": \"concise web search query under 80 chars — only when needsWeb is true, else an empty string\"}");
            var (raw, _, err) = await CallLlmRaw(
                "You are a strict task classifier. Output ONLY the requested JSON.",
                sb.ToString(), ct, _infiniteTimeout, maxTokens: 120);
            if (string.IsNullOrWhiteSpace(raw))
            {
                await EmitLog(emitSse, "warn", $"Web-need verification failed ({err}) — no verdict cached.", ct: ct);
                return (false, null, "", false);
            }
            var cleaned = ExtractFirstJsonObject(raw);
            if (!TryParseWebNeedVerdict(cleaned, out var needsWeb, out var query, out var reason))
            {
                await EmitLog(emitSse, "warn", "Web-need classifier output was unparseable — no verdict cached.", ct: ct);
                return (false, null, "", false);
            }
            // Surface the classifier's reason so a false "does not need current
            // external info" veto is auditable in the agent panel — the model can
            // claim the task needs no web while the prompt clearly asked for one.
            if (!needsWeb)
            {
                var why = string.IsNullOrWhiteSpace(reason) ? "no reason given" : reason;
                if (why.Length > 200) why = why[..200] + "…";
                await EmitLog(emitSse, "warn", $"Web-need classifier vetoed web usage — {why}", ct: ct);
            }
            return (needsWeb, query, reason, true);
        }
        catch
        {
            return (false, null, "", false);
        }
    }

    /// <summary>Feedback shown whenever a repo file edit is rejected for an OS-filesystem task.</summary>
    private const string OsTaskEditFeedback =
        "This task operates on the OS filesystem OUTSIDE the repository (desktop/home/Downloads/etc.) — the repository's source files are NOT the target. " +
        "Use \"_command\" (e.g. mkdir/New-Item for folders, rm/Move-Item for files) or \"_create_directory\" for the OS operation. Do NOT edit repository source files to perform it.";

    /// <summary>
    /// True when an OS-filesystem task ALSO mentions repo work (README, link, note,
    /// docs, "add to"...). Pure OS tasks reject repo edits deterministically; only
    /// this case pays an LLM call to adjudicate whether a specific repo edit is
    /// genuinely part of the task (e.g. "create the folder AND save a link in the
    /// README").
    /// </summary>
    private static bool OsPromptHintsRepoWork(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        var lower = prompt.ToLowerInvariant();
        var hints = new[] { "readme", "link", "mention", "note", "document", "add to" };
        foreach (var h in hints)
        {
            if (lower.Contains(h)) return true;
        }
        return false;
    }

    private async Task<DuplicateStepAdjudication> AdjudicateDuplicateStepAsync(
        string originalPrompt, PlanStep completedStep, PlanStep proposedStep,
        string projectRoot, bool emitSse, CancellationToken ct)
    {
        try
        {
            var currentFile = "(file unavailable)";
            if (!string.IsNullOrWhiteSpace(proposedStep.File) && !AgentProjectUtilities.IsSpecialMarker(proposedStep.File))
            {
                var fullPath = Path.GetFullPath(Path.Combine(
                    projectRoot, proposedStep.File.Replace('/', Path.DirectorySeparatorChar)));
                if (System.IO.File.Exists(fullPath))
                {
                    var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                    currentFile = content.Length > 12000 ? content[..12000] + "\n… (truncated)" : content;
                }
            }

            var promptText = new StringBuilder();
            promptText.AppendLine("ORIGINAL TASK:");
            promptText.AppendLine(TruncateForLlm(originalPrompt ?? "", 1800));
            promptText.AppendLine();
            promptText.AppendLine("COMPLETED STEP (already executed; do not repeat it):");
            promptText.AppendLine($"File: {completedStep.File}");
            promptText.AppendLine($"Change: {completedStep.Change}");
            promptText.AppendLine();
            promptText.AppendLine("CURRENT FILE ON DISK (authoritative after the completed step):");
            promptText.AppendLine(currentFile);
            promptText.AppendLine();
            promptText.AppendLine("PROPOSED NEXT STEP:");
            promptText.AppendLine($"File: {proposedStep.File}");
            promptText.AppendLine($"Change: {proposedStep.Change}");
            promptText.AppendLine($"OldString: {proposedStep.OldString ?? "(none)"}");
            promptText.AppendLine($"NewString: {proposedStep.NewString ?? "(none)"}");
            promptText.AppendLine();
            promptText.AppendLine("The deterministic duplicate check is only a heuristic. Decide whether the proposed step is genuinely distinct work toward the original task.");
            promptText.AppendLine("If it is distinct, return DISTINCT and preserve the proposed step.");
            promptText.AppendLine("If it is a duplicate but another requirement remains, return REPLACEMENT with one complete deduped step that should be executed next. The replacement must target a real repository file or a valid special marker and include file, change, and any required edit fields.");
            promptText.AppendLine("If it is a true duplicate and no replacement is needed, return DUPLICATE.");
            promptText.AppendLine("Output ONLY JSON in exactly one of these forms:");
            promptText.AppendLine("{\"decision\":\"DISTINCT\"}");
            promptText.AppendLine("{\"decision\":\"DUPLICATE\"}");
            promptText.AppendLine("{\"decision\":\"REPLACEMENT\",\"step\":{\"file\":\"...\",\"change\":\"...\",\"oldString\":\"...\",\"newString\":\"...\"}}");

            var (raw, _, err) = await CallLlmRaw(
                "You are a strict duplicate-step adjudicator. Never repeat completed work. Output ONLY the requested JSON.",
                promptText.ToString(), ct, _infiniteTimeout, maxTokens: 1000);
            if (string.IsNullOrWhiteSpace(raw))
            {
                await EmitLog(emitSse, "warn", $"Duplicate adjudication failed ({err}); keeping the duplicate rejection.", ct: ct);
                return DuplicateStepAdjudication.Duplicate;
            }

            using var doc = JsonDocument.Parse(ExtractFirstJsonObject(raw), new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (!root.TryGetProperty("decision", out var decisionEl) || decisionEl.ValueKind != JsonValueKind.String)
                return DuplicateStepAdjudication.Duplicate;
            var decision = decisionEl.GetString()?.Trim().ToUpperInvariant();
            if (decision == "DISTINCT") return DuplicateStepAdjudication.Distinct;
            if (decision != "REPLACEMENT" || !root.TryGetProperty("step", out var stepEl) || stepEl.ValueKind != JsonValueKind.Object)
                return DuplicateStepAdjudication.Duplicate;

            var replacement = ParseAdjudicatedStep(stepEl);
            return replacement == null
                ? DuplicateStepAdjudication.Duplicate
                : new DuplicateStepAdjudication(DuplicateStepDecision.Replacement, replacement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await EmitLog(emitSse, "warn", $"Duplicate adjudication returned unusable output ({ex.Message}); keeping the duplicate rejection.", ct: ct);
            return DuplicateStepAdjudication.Duplicate;
        }
    }

    private static PlanStep? ParseAdjudicatedStep(JsonElement stepEl)
    {
        static string? ReadString(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var value)) return null;
            if (value.ValueKind == JsonValueKind.String)
                return AgentTextUtilities.UnescapeString(value.GetString() ?? "");
            if (value.ValueKind == JsonValueKind.Array)
            {
                var lines = value.EnumerateArray()
                    .Where(v => v.ValueKind == JsonValueKind.String)
                    .Select(v => AgentTextUtilities.UnescapeString(v.GetString() ?? ""));
                return string.Join("\n", lines);
            }
            return null;
        }

        var file = ReadString(stepEl, "file")?.Trim();
        var change = ReadString(stepEl, "change")?.Trim();
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(change)) return null;
        var targetSymbol = ReadString(stepEl, "targetSymbol");
        var targetType = ReadString(stepEl, "targetType");
        var targetName = ReadString(stepEl, "targetName");
        var oldString = ReadString(stepEl, "oldString");
        var newString = ReadString(stepEl, "newString");
        var fullFile = ReadString(stepEl, "fullFile");
        var line = stepEl.TryGetProperty("line", out var lineEl) && lineEl.ValueKind == JsonValueKind.Number
            ? lineEl.GetInt32() : 0;
        var insertAfter = stepEl.TryGetProperty("insertAfter", out var insertEl) && insertEl.ValueKind == JsonValueKind.True;
        var newCode = new List<string>();
        if (stepEl.TryGetProperty("newCode", out var newCodeEl) && newCodeEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in newCodeEl.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String)
                    newCode.Add(AgentTextUtilities.UnescapeString(item.GetString() ?? ""));
        }
        var refFiles = new List<string>();
        if (stepEl.TryGetProperty("referenceFiles", out var refEl) && refEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in refEl.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    refFiles.Add(item.GetString()!);
        }
        var edits = new List<EditPair>();
        if (stepEl.TryGetProperty("edits", out var editsEl) && editsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in editsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var oldEdit = ReadString(item, "oldString");
                var newEdit = ReadString(item, "newString");
                if (!string.IsNullOrWhiteSpace(oldEdit) || newEdit != null)
                    edits.Add(new EditPair { OldString = oldEdit ?? "", NewString = newEdit ?? "" });
            }
        }
        return ParseStepFromJson(file, change, targetSymbol, line, oldString, newString,
            refFiles, edits, targetType, targetName, insertAfter,
            newCode.Count > 0 ? newCode : null, fullFile);
    }

    /// <summary>
    /// LLM adjudication for a repo file edit proposed while the task targets the OS
    /// filesystem: decides whether editing this specific repo file is genuinely part
    /// of the task (e.g. "create the folder AND add a link in the README"). Fails
    /// closed to (false, false) — for an OS task the prior is that repo edits are
    /// wrong, and the rejection feedback steers the model to marker tools. The
    /// Verified flag tells the caller whether to CACHE the verdict: a transient LLM
    /// failure must not permanently lock a file out, so a re-proposal retries.
    /// </summary>
    private async Task<(bool Required, bool Verified)> ConfirmRepoEditRequiredAsync(
        string prompt, string file, string? change, bool emitSse, CancellationToken ct)
    {
        try
        {
            var taskPreview = AgentTextUtilities.Truncate(prompt ?? "", 1200);
            var sb = new StringBuilder();
            sb.AppendLine("TASK:");
            sb.AppendLine(taskPreview);
            sb.AppendLine();
            sb.AppendLine($"PROPOSED STEP: edit repository file \"{file}\" — {(string.IsNullOrWhiteSpace(change) ? "(no description)" : change)}");
            sb.AppendLine();
            sb.AppendLine("The task appears to target the OS filesystem OUTSIDE the repository (desktop/home/Downloads/etc.).");
            sb.AppendLine("DECIDE: is editing this repository source file GENUINELY required by the task (e.g. the task explicitly says to also update a repo file such as a README, add a link, or record something in the repo), or is the model wrongly reaching into the repo when the task is purely about the OS filesystem?");
            sb.AppendLine("Output ONLY JSON: {\"repoEditRequired\": true|false, \"reason\": \"one short sentence\"}");
            var (raw, _, err) = await CallLlmRaw(
                "You are a strict task classifier. Output ONLY the requested JSON.",
                sb.ToString(), ct, _infiniteTimeout, maxTokens: 120);
            if (string.IsNullOrWhiteSpace(raw))
            {
                await EmitLog(emitSse, "warn", $"OS-task repo-edit verification failed ({err}) — rejecting the repo edit this round (re-proposal will retry).", ct: ct);
                return (false, false);
            }
            var cleaned = ExtractFirstJsonObject(raw);
            using var doc = JsonDocument.Parse(cleaned);
            return (doc.RootElement.TryGetProperty("repoEditRequired", out var r) && r.ValueKind == JsonValueKind.True, true);
        }
        catch
        {
            return (false, false);
        }
    }

    /// <summary>
    /// Deterministic fallback query for the auto-injected _web_search step when the
    /// LLM verification produced no usable query (or the LLM call failed). The
    /// flattened task text itself is a serviceable search query for the task's most
    /// important topic.
    /// </summary>
    private static string BuildFallbackWebQuery(string? prompt)
    {
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            var flat = Regex.Replace(prompt.Replace('\r', ' ').Replace('\n', ' '), @"\s+", " ").Trim();
            if (flat.Length > 160)
            {
                flat = flat.Substring(0, 160);
                // Don't split a UTF-16 surrogate pair (emoji) at the cutoff.
                if (flat.Length > 0 && char.IsHighSurrogate(flat[^1])) flat = flat[..^1];
                flat = flat.TrimEnd();
            }
            if (flat.Length > 0) return flat;
        }
        return "latest information";
    }

    // A concrete, inspectable label for an auto-injected _browser_test step when the
    // visual classifier returned no target: prefer the first quoted human phrase in the
    // prompt (e.g. the 'Benchmark 22' the task demands as a heading), skipping file-ish
    // tokens (paths/extensions), then fall back to a generic label — FindTargetSection
    // also scores against the prompt's own keywords, so a generic label still matches.
    private static string BuildFallbackVisualTarget(string? prompt)
    {
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            foreach (Match m in Regex.Matches(prompt, @"['""]\s*([^'""]{2,60}?)\s*['""]"))
            {
                var v = m.Groups[1].Value.Trim();
                if (v.IndexOf('.') >= 0 || v.IndexOf('/') >= 0) continue; // file-ish, not a UI label
                if (v.Length >= 2) return v;
            }
        }
        return "the page";
    }

    private async Task<(AgentPlan plan, List<object> results, string discoveryContext, bool planCompleteDeclared,
        Dictionary<string, string> preEditSnapshots)> RunInterleavedPlanExecutionLoop(
        AgentRunContext runContext, string prompt, string discoveryContext, string projectRoot, bool emitSse,
        CancellationToken ct, string? steeringContext, string? cardId = null,
        List<string>? attachedFiles = null, int? atomicStepEstimate = null)
    {
        var planSoFar = new List<PlanStep>();
        var allResults = new List<object>();
        // First-capture-per-file pre-edit content of every file edited this run. Unlike the
        // per-step preEditContents (which holds the content before the LAST edit, for diffs),
        // this keeps the ORIGINAL pre-run content so post-execution template-binding validation
        // only flags symbols the run actually introduced — pre-existing bindings (template refs
        // like #keywordsInput, array properties like searchResults.length, members the regex
        // extractor simply misses) never false-positive the repair loop.
        var preRunSnapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rejectionFeedback = new List<string>();
        var planCompleteDeclared = false;
        var exploredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var thinkingLog = new StringBuilder();
        // Set when the between-steps completion assessment said the task is NOT done yet; used
        // by the planComplete gate below so a planner that declares completion right after an
        // "incomplete" verdict is re-assessed instead of blindly trusted (the wrong-target
        // class: the assessment now judges the user's LATEST intent, steering included).
        var lastAssessIncomplete = false;
        var lastAssessReason = "";
        // Bounded accumulator for the "CHANGES FROM PREVIOUS STEP" section: instead of stacking
        // every step's raw diff forever, we keep ONE section and LLM-summarize it once it passes
        // cfg.diffContextSummaryChars (when cfg.summarizeDiffContext is on).
        var diffContextAccum = new StringBuilder();
        var rejectionStepIndex = 100000; // plan-level rejection step events use a high index offset so they never collide with ExecutePlan's 0-based per-step indexes
        var regenAttempts = 0;
        var consecutiveSlotFailures = 0;
        var totalPlanningRounds = 0;
        var webNeedVerified = 0; // 0 = unchecked, 1 = task needs web, -1 = no web needed
        var webNeedVerifyFailures = 0; // consecutive failed classifier calls — caps re-confirmation at 2 per run
        string? webInjectedQuery = null; // search query from the same verification call, used if we auto-inject _web_search
        var visualNeedVerified = 0; // 0 = unchecked, 1 = task needs visual inspection of a rendered page, -1 = no visual needed
        string? visualNeedTarget = null; // inspect target from the same verification call, used if we auto-inject _browser_test
        var fetchCommandRejections = 0; // consecutive fetch-command rejections this slot — auto-inject _web_search at the regen cap
        string? webNeedVetoReason = null; // classifier's reason when web was vetoed — shown inline on the rejected step card
        // OS-filesystem task guard state: pure OS tasks reject repo file edits
        // deterministically; hint-y tasks get one memoized LLM verdict per file.
        var osTaskGuard = IsExternalFilesystemTask(prompt);
        var osTaskPure = osTaskGuard && !OsPromptHintsRepoWork(prompt);
        var osRepoEditVerdicts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        // Failed _web_fetch/_web_search steps are retried with feedback instead of halting the
        // run (the pre-fix behavior: an invented URL fails, the loop halts, and the repair
        // replanner — which has no web-results context — invents application code to "write"
        // the demanded file). Bounded so a persistently-broken URL can't loop forever; after
        // the cap the run halts into post-execution verification like any other failure.
        var webStepFailures = 0;
        const int MaxWebStepRetries = 2;
        // Failed _command steps are fed back to the planner for a RECOVERY EDIT (bounded like
        // web steps): the benchmark-22 failure — `node server.js` crashed with a SyntaxError
        // but was reported "done", so no recovery edit was ever planned and the planner
        // deadlocked re-proposing the same broken command. The error output gives the planner
        // the diagnostic it needs to fix the failing file (or change approach) instead of
        // halting; after the cap the run falls through to the halt + post-execution repair.
        var commandStepFailures = 0;
        const int MaxCommandStepRetries = 2;
        // Once the fetch retry budget is exhausted and the task still demands data, the loop
        // auto-injects ONE _scraper step (system-built scraper) for the failed URL instead of
        // halting straight into repair — the web tool and the scraper are different fetch
        // mechanisms, so a URL the tool chokes on can still succeed here. The flag bounds it:
        // a failing scraper falls through to the halt + deterministic repair auto-dump.
        var scraperInjected = false;
        if (emitSse)
        {
            await SendSse(Response, "plan", new
            {
                thinking = "",
                summary = "Plan atomic step → execute it → verify → decide if another step is needed… — 0 done so far",
                items = Array.Empty<object>(),
                marker = new { File = "_planning", Change = "Reading task & discovery context…" },
                incremental = true
            }, ct);
        }
        var pendingSteps = new Queue<PlanStep>();
        Func<string, Task>? planActivity = null;
        if (emitSse)
        {
            planActivity = async phase =>
            {
                var stepNum = planSoFar.Count;
                if (stepNum == 0) return;
                var isVerifying = phase == "verifying";
                var currentIsConcrete = ShouldApplyDirectly(planSoFar[stepNum - 1]);
                var label = isVerifying
                    ? $"Verifying Step {stepNum} — checking the edit…"
                    : currentIsConcrete
                        ? $"Applying edits — Step {stepNum}…"
                        : $"Thinking for edit — Step {stepNum}…";
                await SendPlanActivityEventAsync(thinkingLog, planSoFar, emitSse,
                    isVerifying ? "_verifying" : "_executing", label,
                    $"Executed {stepNum - 1} step(s) — {label}", stepNum - 1, ct);
            };
        }
        if (emitSse)
        {
            await SendPlanActivityEventAsync(thinkingLog, planSoFar, emitSse,
                "_planning", "Reading task & discovery context…",
                "Plan atomic step → execute it → verify → decide if another step is needed… — 0 done so far", null, ct);
        }
        for (var turn = 0; turn < MAX_INCREMENTAL_STEPS; turn++)
        {
            ct.ThrowIfCancellationRequested();
            // Live steer: a message posted to POST api/agent/steer while this card is running
            // is drained HERE, before the next planner turn, and merged ONCE into the
            // persistent steering context — so a mid-run instruction ("stop, don't add what
            // you just proposed") overrides the model's trajectory from the next turn onward,
            // exactly like run-start steering. TryRemove consumes it: it never re-applies or
            // duplicates on later turns (the merged text stays via steeringContext itself).
            if (!string.IsNullOrWhiteSpace(cardId) && _liveSteer.TryRemove(cardId, out var liveSteer))
            {
                var deliveredTurn = planSoFar.Count + 1;
                steeringContext = string.IsNullOrWhiteSpace(steeringContext)
                    ? liveSteer
                    : $"{steeringContext}\n\n### LIVE STEER ###\n{liveSteer}";
                await EmitLog(emitSse, "info",
                    $"⚡ Live steer injected — visible to the next planner turn (step {deliveredTurn})",
                    new { message = liveSteer }, ct: ct);
                // Persist the delivered steer onto the card as _steers (transcript: what was
                // injected, at which turn) so it survives a reload — mirrors the ground-truth
                // persistence. Display-only: a persistence failure is logged, never fatal.
                await PublishDeliveredSteerAsync(cardId, liveSteer, deliveredTurn, emitSse, ct);
            }
            if (emitSse && pendingSteps.Count == 0 && !planCompleteDeclared)
            {
                await SendSse(Response, "phase", new { message = $"Thinking about Step {planSoFar.Count + 1}…" }, ct);
            }
            if (pendingSteps.Count > 0)
            {
                var queuedStep = pendingSteps.Dequeue();
                planSoFar.Add(queuedStep);
                if (!string.IsNullOrWhiteSpace(queuedStep.Change))
                    thinkingLog.AppendLine($"Step {planSoFar.Count}: {queuedStep.Change}");
                var queuedIsConcrete = ShouldApplyDirectly(queuedStep);
                await EmitLog(emitSse, "info",
                    queuedIsConcrete
                        ? $"▶ Applying edits — Step {planSoFar.Count} — [{queuedStep.File}] {queuedStep.Change}"
                        : $"▶ Thinking for edit — Step {planSoFar.Count} — [{queuedStep.File}] {queuedStep.Change}", ct: ct);
                await SendPlanActivityEventAsync(thinkingLog, planSoFar, emitSse,
                    "_executing", queuedIsConcrete
                        ? $"Applying edits — Step {planSoFar.Count} — {queuedStep.Change}"
                        : $"Thinking for edit — Step {planSoFar.Count} — {queuedStep.Change}",
                    $"Completed {planSoFar.Count - 1} step(s) — {(queuedIsConcrete ? "applying edits for" : "thinking for edit on")} Step {planSoFar.Count}",
                    planSoFar.Count - 1, ct);
                await PersistBoardDataPlanAsync(cardId, planSoFar, emitSse, ct,
                    summary: $"Interleaved execution — {planSoFar.Count} step(s) so far", score: 90);
                var singleStepPlan = new AgentPlan { Plan = new List<PlanStep> { queuedStep }, Summary = queuedStep.Change, Score = 90 };
                var beforeCount = allResults.Count;
                var preEditContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (queuedStep.File != null && !AgentProjectUtilities.IsSpecialMarker(queuedStep.File))
                {
                    var fp = Path.GetFullPath(Path.Combine(projectRoot, (queuedStep.File ?? "").Replace('/', Path.DirectorySeparatorChar)));
                    if (System.IO.File.Exists(fp))
                    {
                        var preContent = await System.IO.File.ReadAllTextAsync(fp, Encoding.UTF8, ct);
                        preEditContents[queuedStep.File!] = preContent;
                        var relKey = queuedStep.File!.Replace('\\', '/');
                        if (!preRunSnapshots.ContainsKey(relKey)) preRunSnapshots[relKey] = preContent;
                    }
                }
                try
                {
                    await ExecutePlanCore(runContext, prompt, projectRoot, emitSse, discoveryContext, singleStepPlan, ct, allResults,
                        steeringContext: steeringContext, attachedFiles: attachedFiles, cardId: cardId,
                        replanBudget: new[] { 0 }, onActivity: planActivity,
                        skipLlmPreResolution: queuedIsConcrete);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await EmitLog(emitSse, "error",
                        $"⛔ Interleaved execution halted — queued step {planSoFar.Count} threw: {ex.Message}", ct: ct);
                    if (planSoFar.Count > 0) planSoFar.RemoveAt(planSoFar.Count - 1);
                    await PersistBoardDataPlanAsync(cardId, planSoFar, emitSse, ct,
                        summary: $"Execution halted at queued step {planSoFar.Count + 1} — exception: {ex.Message}", score: 0,
                        append: false);
                    break;
                }
                var newResults = allResults.Skip(beforeCount).OfType<Dictionary<string, object?>>().ToList();
                var stepSucceeded = newResults.Any(r => r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created");
                await EmitLog(emitSse, "info",
                    $"DIAG: After queued step — stepSucceeded={stepSucceeded}, planSoFar.Count={planSoFar.Count}", ct: ct);
                discoveryContext = AppendWebResultsToDiscoveryContext(discoveryContext, newResults);
                discoveryContext = AppendCreatedPathsToDiscoveryContext(discoveryContext, newResults, projectRoot);
                discoveryContext = AppendServerRecoveryToDiscoveryContext(discoveryContext, newResults);
                await EmitContextUpdateAsync(runContext, discoveryContext, emitSse, ct);
                var globalPlanIdx = planSoFar.Count - 1;
                foreach (var r in newResults)
                {
                    r["planItemIndex"] = globalPlanIdx;
                    if (emitSse && r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
                        await SendSse(Response, "step", r, ct);
                }
                await PersistBoardDataPlanStepAsync(cardId, globalPlanIdx, emitSse, ct, projectRoot: projectRoot);
                if (!stepSucceeded) { break; }
                continue;
            }
            string? extendedReasoning = null;
            var prePlanCfg = await LoadConfigAsync();
            // Retry mode (a step was just rejected): the planner already produced its concrete
            // edit for this step — skip the pre-plan thinking round so rejected edits flow
            // straight back into re-proposal instead of spawning more (often hallucinated)
            // thinking walls. A crap edit is ignored and the edit pipeline continues normally.
            if (prePlanCfg.extendThinking && regenAttempts == 0 && !string.IsNullOrWhiteSpace(cardId))
            {
                if (emitSse && !planCompleteDeclared)
                    await SendPlanActivityEventAsync(thinkingLog, planSoFar, emitSse,
                        "_planning", $"Deep thinking for plan — Step {planSoFar.Count + 1}…",
                        $"Deep thinking for plan — Step {planSoFar.Count + 1}…", null, ct);
                try
                {
                    extendedReasoning = await ExtendThinkingPrePlanAsync(
                        cardId, prompt, discoveryContext, planSoFar, projectRoot, emitSse, ct,
                        attachedFiles: attachedFiles);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    await EmitLog(emitSse, "warn", $"Pre-plan thinking skipped: {ex.Message}",
                        new { reason = ex.Message }, ct: ct);
                }
            }
            else if (prePlanCfg.extendThinking && regenAttempts > 0)
            {
                await EmitLog(emitSse, "bypass",
                    $"Pre-plan thinking skipped (retry {regenAttempts}) — re-proposing directly with rejection feedback", ct: ct);
            }
            if (emitSse && !planCompleteDeclared)
            {
                var proposingText = planSoFar.Count == 0
                    ? "Reading task & proposing the first step…"
                    : $"Proposing step {planSoFar.Count + 1} — planning the edit…";
                await SendPlanActivityEventAsync(thinkingLog, planSoFar, emitSse,
                    "_planning", proposingText,
                    $"Proposing step {planSoFar.Count + 1}…", null, ct);
            }
            var proposal = await ProposeNextIncrementalStepAsync(runContext, prompt, discoveryContext, planSoFar, steeringContext, rejectionFeedback, emitSse, ct, extendedReasoning: extendedReasoning, atomicStepEstimate: atomicStepEstimate, projectRoot: projectRoot);
            if (proposal == null)
            {
                var jsonFb = "Your previous response could not be parsed as valid JSON. " +
                    "Output ONLY the JSON object described in the system prompt.";
                await EmitRejectedLog(emitSse, "Interleaved execution: rejected — response was not valid JSON; retrying with parse feedback", jsonFb, ct);
                rejectionFeedback.Add(jsonFb);
                if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) { break; }
                continue;
            }
            if (proposal.PlanComplete)
            {
                // Web-need gate: declaring the plan complete is itself a refusal to
                // use the web tools when the task needs current external info and no
                // web step exists. Reject, and on the regen cap inject the search so
                // web-needing tasks cannot escape by claiming completion.
                if (TaskHintsWebNeed(prompt) && !planSoFar.Any(s => IsWebStep(s.File)))
                {
                    if (webNeedVerified == 0 && webNeedVerifyFailures < 2)
                    {
                        await EmitLog(emitSse, "info",
                            "Task hints at needing current external info — checking whether _web_search is required…", ct: ct);
                        var (needsWeb, query, vetoReason, verified) = await ConfirmWebNeedAsync(prompt, emitSse, ct);
                        if (verified)
                        {
                            webNeedVerified = needsWeb ? 1 : -1;
                            if (needsWeb) webInjectedQuery = query;
                            else webNeedVetoReason = vetoReason;
                        }
                        else
                        {
                            webNeedVerifyFailures++; // transient classifier failure — retry up to 2×, then stop calling
                        }
                    }
                    if (webNeedVerified == 1)
                    {
                        await EmitRejectedLog(emitSse,
                            "Interleaved execution: rejected plan-complete — task needs current external info but the plan has no _web_search step",
                            WebNeedFeedback, ct);
                        rejectionFeedback.Add(WebNeedFeedback);
                        if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS)
                        {
                            var queryText = string.IsNullOrWhiteSpace(webInjectedQuery)
                                ? BuildFallbackWebQuery(prompt)
                                : webInjectedQuery.Trim();
                            pendingSteps.Enqueue(new PlanStep { File = "_web_search", Change = queryText });
                            await EmitLog(emitSse, "warn",
                                $"The planner declared the plan complete without using _web_search after {MAX_STEP_REGEN_ATTEMPTS} rejections — auto-injecting a _web_search step: \"{queryText}\"", ct: ct);
                            regenAttempts = 0;
                            rejectionFeedback.Clear();
                            continue; // loop top executes the injected step, then planning resumes
                        }
                        continue;
                    }
                }
                // ── Visual-inspection gate ────────────────────────────────────────────────────
                // A task that demands VISUAL verification of a rendered page ("check my game for
                // visual bugs", "you must LOOK at the rendered page", "visually confirm the
                // heading renders") is NOT complete while the run never executed a _browser_test
                // step — the observed failure declared planComplete=true after only the build
                // steps, so the live web test (and the Test Browser tab) never happened. Reject,
                // and inject the _browser_test step so visual tasks cannot escape by claiming
                // completion. The injection is IMMEDIATE (not gated on the regen cap): the
                // weak-model failure mode is a planner that answers the "plan a _browser_test
                // step" steering by proposing _command steps instead ("start the server and then
                // visually inspect…"), whose own rejections RESET regenAttempts via the slot-
                // failure path — so the 3-strike cap never accumulates and the run dead-ends
                // into the repair loop with the browser test never run. The classifier already
                // deterministically confirmed visual inspection is required, and _browser_test
                // is a deterministic step (no model content), so inject it the moment the
                // planner first tries to close the run without it.
                if (TestIntentClassifier.HasVisualInspectionHint(prompt) &&
                    !planSoFar.Any(s => string.Equals(s.File, "_browser_test", StringComparison.OrdinalIgnoreCase)))
                {
                    // A task that EXPLICITLY names the live browser test as a requirement
                    // ("…pass/fail only based on the number of legs confirmed by the browser
                    // test") bypasses the LLM classifier entirely: the classifier reads the
                    // dominant build/implement language and answers needsVisual=false, which
                    // vetoes the deterministically-required browser test (benchmark 23).
                    if (TestIntentClassifier.DemandsLiveBrowserTest(prompt))
                    {
                        visualNeedVerified = 1;
                    }
                    else if (visualNeedVerified == 0)
                    {
                        await EmitLog(emitSse, "info",
                            "Task demands visual verification — checking whether a _browser_test step is required…", ct: ct);
                        var (needsVisual, target) = await ClassifyVisualInspectionPromptAsync(prompt, ct);
                        // Fail OPEN on an unavailable classifier (null verdict): the
                        // deterministic hint already fired, so a flaky LLM must not
                        // silently skip the required live browser test — only a
                        // confident "needsVisual:false" from a working classifier vetoes.
                        visualNeedVerified = TestIntentClassifier.ShouldInjectVisualBrowserTest(needsVisual) ? 1 : -1;
                        if (needsVisual == true) visualNeedTarget = target;
                    }
                    if (visualNeedVerified == 1)
                    {
                        var inspectTarget = string.IsNullOrWhiteSpace(visualNeedTarget)
                            ? BuildFallbackVisualTarget(prompt)
                            : visualNeedTarget.Trim();
                        await EmitLog(emitSse, "warn",
                            $"The planner tried to close the run without a _browser_test step — auto-injecting a live web test: \"{inspectTarget}\"", ct: ct);
                        pendingSteps.Enqueue(new PlanStep { File = "_browser_test", Change = inspectTarget });
                        rejectionFeedback.Clear();
                        regenAttempts = 0;
                        continue; // loop top executes the injected step, then planning resumes
                    }
                }
                // ── OS output-file gate ─────────────────────────────────────────────────────
                // A task that explicitly demands a file be written to the OS filesystem
                // ("write the data into a text file on my desktop") is NOT complete while that
                // file is missing — even when the model claims it is (the observed failure
                // declared planComplete=true with the reason "Now I need only create the final
                // output file"). When the run gathered web content but never wrote it, dump the
                // harvested results straight to the target path (a deterministic server-side
                // finalization — no planning round). Otherwise reject the completion so the
                // planner is steered to plan the write.
                if (AgentOsOutputVerifier.TryGetOsFileOutputDemand(prompt, out var osDemand) &&
                    !AgentOsOutputVerifier.IsOsOutputWritten(osDemand, allResults.OfType<Dictionary<string, object?>>()))
                {
                    var (dumped, dumpPath, dumpError) = AgentOsOutputVerifier.TryAutoDumpWebResults(
                        prompt, osDemand, allResults.OfType<Dictionary<string, object?>>());
                    if (dumped && dumpPath != null)
                    {
                        planSoFar.Add(new PlanStep { File = "_command", Change = $"Auto-dump web results to {dumpPath}" });
                        var dumpResult = new Dictionary<string, object?>
                        {
                            ["index"] = allResults.Count,
                            ["type"] = "command",
                            ["status"] = "done",
                            ["command"] = $"Auto-dumped web results → {dumpPath}",
                            ["path"] = dumpPath,
                            ["output"] = $"Web results written to {dumpPath} (auto-dump — the task demanded an OS output file)"
                        };
                        allResults.Add(dumpResult);
                        if (emitSse)
                            await SendSse(Response, "step", dumpResult, ct);
                        await EmitLog(emitSse, "success",
                            $"💾 Auto-dumped web results to {dumpPath} — the task asked to write a file to {osDemand.DirectoryPath}", ct: ct);
                    }
                    else
                    {
                        var targetName = string.IsNullOrWhiteSpace(osDemand.FileNameHint)
                            ? AgentOsOutputVerifier.DefaultDumpFileName
                            : osDemand.FileNameHint!;
                        var osTargetPath = Path.Combine(osDemand.DirectoryPath, targetName);
                        var writeExample = OperatingSystem.IsWindows()
                            ? $"Set-Content -Path \"{osTargetPath}\" -Value \"<content>\" -Encoding UTF8"
                            : $"echo \"<content>\" > \"{osTargetPath}\"";
                        var fb = $"The task explicitly asks to write a file to \"{osTargetPath}\" " +
                            $"on the OS filesystem, but the run ended without creating it{(dumpError != null ? $" ({dumpError})" : "")}. " +
                            $"Plan a _command step that writes the output file there with an absolute path, e.g. " +
                            $"{writeExample}. " +
                            $"Do NOT declare the plan complete until the file exists.";
                        await EmitRejectedLog(emitSse,
                            "Interleaved execution: rejected plan-complete — the task demanded an OS output file that was never created",
                            fb, ct);
                        rejectionFeedback.Add(fb);
                        if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) { break; }
                        continue;
                    }
                }
                // ── Incomplete-assessment gate ──────────────────────────────────────────────
                // The planner declared the plan complete right after the completion assessment
                // said the task is NOT done (judged against the user's LATEST intent, steering
                // included). Accepting that blindly lets a run complete with the wrong target —
                // the planner refusing the remaining work. Re-assess BEFORE accepting; if the
                // ground truth still says incomplete, reject the planComplete with the specific
                // unmet requirement as feedback so the next proposal addresses it.
                if (lastAssessIncomplete && !string.IsNullOrWhiteSpace(lastAssessReason))
                {
                    var (recheckComplete, recheckReason) = await AssessCompletionCore(runContext,
                        prompt, allResults, projectRoot, ct,
                        new AgentPlan { Plan = planSoFar.ToList(), Summary = "Plan-complete recheck", Score = 90 },
                        attachedFiles: attachedFiles, atomicStepEstimate: atomicStepEstimate,
                        steeringContext: steeringContext);
                    if (!recheckComplete)
                    {
                        var fb = $"You declared the plan complete, but the completion assessment still says the task is " +
                            $"NOT complete: {recheckReason}. Do NOT declare the plan complete — propose the step that " +
                            $"satisfies that remaining requirement, then the assessment will re-run.";
                        await EmitRejectedLog(emitSse,
                            "Interleaved execution: rejected plan-complete — the completion assessment still reports unmet requirements", fb, ct);
                        rejectionFeedback.Add(fb);
                        if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) { break; }
                        continue;
                    }
                    lastAssessIncomplete = false;
                }
                if (emitSse)
                {
                    await SendSse(Response, "thinking", new { text = $"Plan complete: {proposal.CompletionReason}" }, ct);
                    await SendSse(Response, "plan", new
                    {
                        thinking = thinkingLog.ToString(),
                        summary = $"Plan complete — {planSoFar.Count} step(s) executed",
                        items = planSoFar.Select((s, idx) => new
                        {
                            File = s.File,
                            Change = s.Change,
                            Line = s.LineNumber,
                            OldString = s.OldString,
                            NewString = s.NewString,
                            done = true,
                            GroundTruth = s.GroundTruth
                        }).ToList(),
                        incremental = true
                    }, ct);
                }
                await EmitLog(emitSse, "success",
                    $"Interleaved execution: complete after {planSoFar.Count} step(s) — {proposal.CompletionReason}", ct: ct);
                var budgetTxt = atomicStepEstimate is > 0
                    ? $" (estimated {atomicStepEstimate})" : "";
                await EmitLog(emitSse, "metric",
                    $"📊 Card planning: {planSoFar.Count} step(s){budgetTxt}, {totalPlanningRounds} total planning round(s)", ct: ct);
                planCompleteDeclared = true;
                break;
            }
            if (!string.IsNullOrWhiteSpace(proposal.ExploreFile))
            {
                if (exploredFiles.Add(proposal.ExploreFile))
                {
                    if (proposal.ExploreFile.Equals("_discover", StringComparison.OrdinalIgnoreCase))
                    {
                        await EmitLog(emitSse, "info", "Planner requested _discover — running project-wide search…", ct: ct);
                        discoveryContext = await RunDiscoveryToolAsync(prompt, discoveryContext, projectRoot, emitSse, ct);
                        await EmitContextUpdateAsync(runContext, discoveryContext, emitSse, ct);
                        regenAttempts = 0;
                        continue;
                    }
                    var isMarker = proposal.ExploreFile.StartsWith("_");
                    var alreadyInContext = false;
                    if (!isMarker)
                    {
                        var normPath = proposal.ExploreFile.Replace('\\', '/').TrimStart('/');
                        // A file present ONLY as a focused region is NOT fully in context —
                        // a later re-request may legitimately need a different symbol's region.
                        alreadyInContext = !IsFocusedSectionInContext(discoveryContext, normPath) &&
                                           (discoveryContext.Contains($"### read {normPath}") ||
                                            discoveryContext.Contains($"### {normPath}") ||
                                            Regex.IsMatch(discoveryContext, $@"### (?:read )?\S*{Regex.Escape(Path.GetFileName(normPath))}\b"));
                    }
                    if (alreadyInContext)
                    {
                        var ctxFb = $"STOP — '{proposal.ExploreFile}' is ALREADY in the DISCOVERY CONTEXT above. " +
                            "Do NOT request it again. Read it and propose the actual next step.";
                        await EmitRejectedLog(emitSse,
                            $"Interleaved execution: rejected explore — '{proposal.ExploreFile}' already in discovery context; retrying", ctxFb, ct);
                        rejectionFeedback.Add(ctxFb);
                        if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                        continue;
                    }
                    await EmitLog(emitSse, "info", $"Interleaved execution: exploring {proposal.ExploreFile}", ct: ct);
                    await SendPlanActivityEventAsync(thinkingLog, planSoFar, emitSse,
                        "_exploring", $"Exploring {proposal.ExploreFile}…",
                        $"Exploring {proposal.ExploreFile}…", null, ct);
                    discoveryContext = await ExplorationPipeline(
                        new List<PlanStep> { new() { File = "_explore", Change = proposal.ExploreFile } },
                        discoveryContext, projectRoot, emitSse, ct, prompt);
                    await EmitContextUpdateAsync(runContext, discoveryContext, emitSse, ct);
                    if (!string.IsNullOrWhiteSpace(cardId))
                        await AutoAttachFileToCardAsync(cardId, proposal.ExploreFile, emitSse, ct);
                    regenAttempts = 0;
                    continue;
                }
                else
                {
                    // The earlier read may have been only a FOCUSED region (the file was
                    // never shown in full) — then re-exploring is legitimate: the model
                    // can surface a different symbol's region. Only reject when the file
                    // is genuinely shown in full already.
                    var normPath = proposal.ExploreFile.Replace('\\', '/').TrimStart('/');
                    var focusedOnly = !proposal.ExploreFile.StartsWith("_") &&
                        IsFocusedSectionInContext(discoveryContext, normPath);
                    if (!focusedOnly)
                    {
                        var exploreFb = $"You asked to explore '{proposal.ExploreFile}' again — it is ALREADY shown in full above. " +
                            "Do not re-request it. Propose the actual next step using the exact names visible there.";
                        await EmitRejectedLog(emitSse,
                            $"Interleaved execution: rejected explore — '{proposal.ExploreFile}' already shown in full; retrying", exploreFb, ct);
                        rejectionFeedback.Add(exploreFb);
                        if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                        continue;
                    }
                    await EmitLog(emitSse, "info",
                        $"Interleaved execution: re-exploring {proposal.ExploreFile} for another focused region", ct: ct);
                    await SendPlanActivityEventAsync(thinkingLog, planSoFar, emitSse,
                        "_exploring", $"Re-exploring {proposal.ExploreFile}…",
                        $"Re-exploring {proposal.ExploreFile}…", null, ct);
                    discoveryContext = await ExplorationPipeline(
                        new List<PlanStep> { new() { File = "_explore", Change = proposal.ExploreFile } },
                        discoveryContext, projectRoot, emitSse, ct, prompt);
                    await EmitContextUpdateAsync(runContext, discoveryContext, emitSse, ct);
                    if (!string.IsNullOrWhiteSpace(cardId))
                        await AutoAttachFileToCardAsync(cardId, proposal.ExploreFile, emitSse, ct);
                    regenAttempts = 0;
                    continue;
                }
            }
            if (proposal.Step == null)
            {
                var neitherFb = "You returned neither planComplete=true, exploreFile, nor a step — return exactly one.";
                await EmitRejectedLog(emitSse, "Interleaved execution: rejected — returned neither planComplete, exploreFile, nor a step; retrying", neitherFb, ct);
                rejectionFeedback.Add(neitherFb);
                if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                continue;
            }
            if (proposal.Step.File.Equals("_discover", StringComparison.OrdinalIgnoreCase))
            {
                if (exploredFiles.Add("_discover"))
                {
                    await EmitLog(emitSse, "info", "Planner proposed _discover step — running project-wide search…", ct: ct);
                    discoveryContext = await RunDiscoveryToolAsync(prompt, discoveryContext, projectRoot, emitSse, ct);
                    await EmitContextUpdateAsync(runContext, discoveryContext, emitSse, ct);
                    regenAttempts = 0;
                    continue;
                }
                var discoverFb = "You already ran _discover — its results are now in the DISCOVERY CONTEXT. Use them to propose the next step.";
                await EmitRejectedLog(emitSse, "Interleaved execution: rejected — _discover was already run this session; retrying", discoverFb, ct);
                rejectionFeedback.Add(discoverFb);
                if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                continue;
            }
            if (proposal.Step.File != null && planSoFar.Count > 0)
            {
                // For special markers (_create_file, _create_directory, _command, etc.),
                // the "File" field is always the marker name — use Change for identity.
                // Two _create_file steps are only duplicates if they target the exact same path.
                var isSpecialStep = AgentProjectUtilities.IsSpecialMarker(proposal.Step.File);
                PlanStep? duplicateOf = null;
                if (isSpecialStep)
                {
                    // Special markers: duplicate only if the Change (=path/command) is identical
                    duplicateOf = planSoFar.FirstOrDefault(s =>
                        string.Equals(s.File, proposal.Step.File, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(s.Change, proposal.Step.Change, StringComparison.OrdinalIgnoreCase));
                }
                else if (!string.IsNullOrEmpty(proposal.Step.OldString))
                {
                    // Content-truth duplicate check for concrete edits: a step is a duplicate
                    // ONLY when its target text is no longer present in the current file — i.e.
                    // the same edit already landed (a re-proposal of the same oldString→newString
                    // finds its oldString already replaced). A step whose oldString IS still
                    // present is actionable follow-up work — including steering-driven REVERSALS
                    // that edit ON TOP of a previous step (their oldString is the earlier step's
                    // NEW content). The old description-token-overlap heuristic (> 0.35) rejected
                    // those legitimate reversals with "STEP ALREADY DONE", after which the
                    // planner declared planComplete and the run completed with the wrong target.
                    string? current = null;
                    try
                    {
                        var fp = Path.GetFullPath(Path.Combine(projectRoot,
                            proposal.Step.File.Replace('/', Path.DirectorySeparatorChar)));
                        if (System.IO.File.Exists(fp))
                            current = await System.IO.File.ReadAllTextAsync(fp, Encoding.UTF8, ct);
                    }
                    catch { }
                    if (current != null && !current.Contains(proposal.Step.OldString, StringComparison.Ordinal))
                        // The target text is gone — the identical edit already landed.
                        duplicateOf = planSoFar.FirstOrDefault(s =>
                            string.Equals(s.File, proposal.Step.File, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    // No oldString (full-file / FORMAT C/D style): fall back to the token-overlap proxy.
                    // EXEMPTION — TABULAR DATA FILES: CSV/TSV/XLSX steps are a SEQUENCE of structured
                    // ops (add a column → add rows → mass edit → per-row edits) whose change
                    // descriptions legitimately share vocabulary ("add a type column" vs
                    // "add a row with id=1026, name=weavmon, type=unknown" share {add,type} →
                    // TokenOverlap 0.67 > 0.35). The proxy flags the second op as a duplicate and
                    // kills the whole edit sequence after the first edit. Tabular ops are
                    // deterministic and applied structurally (zero-LLM), so dedup here is
                    // EXACT-CHANGE only — a re-proposed identical op is a duplicate; a different
                    // op on the same file is legitimate follow-up work.
                    if (TabularFileService.IsTabularFile(proposal.Step.File))
                    {
                        duplicateOf = planSoFar.FirstOrDefault(s =>
                            string.Equals(s.File, proposal.Step.File, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(s.Change, proposal.Step.Change, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        duplicateOf = planSoFar.FirstOrDefault(s =>
                            string.Equals(s.File, proposal.Step.File, StringComparison.OrdinalIgnoreCase) &&
                            TokenOverlap(s.Change ?? "", proposal.Step.Change ?? "") > 0.35);
                        // FILE-TRUTH guard: the token-overlap proxy compares step DESCRIPTIONS,
                        // which can over-promise — an earlier step may have been only PARTIALLY
                        // applied (e.g. deterministic member synthesis added just a property while
                        // the description also promised a method: the live navigation
                        // component.ts moviesTodoCount/getMoviesInfo run). If the named target is
                        // NOT actually in the file, the work was never done — this is legitimate
                        // follow-up, not a duplicate.
                        if (duplicateOf != null &&
                            !await StepTargetExistsInFileAsync(proposal.Step, projectRoot, ct))
                            duplicateOf = null;
                    }
                }
                if (duplicateOf != null)
                {
                    // Description-token overlap is only a heuristic. Before rejecting the
                    // proposal, ask the LLM to adjudicate the completed step against the
                    // proposed step and current file truth. This is important for sequential
                    // edits in one method: "add save_note handler" and "add downloaded_painting
                    // handler" share the same file/method vocabulary but are distinct work.
                    var adjudication = await AdjudicateDuplicateStepAsync(
                        prompt, duplicateOf, proposal.Step, projectRoot, emitSse, ct);
                    if (adjudication.Decision == DuplicateStepDecision.Distinct)
                    {
                        await EmitLog(emitSse, "info",
                            $"Duplicate heuristic overruled by LLM — allowing distinct step [{proposal.Step.File}] {proposal.Step.Change}", ct: ct);
                        duplicateOf = null;
                        proposal.Step.SkipDuplicateCheck = true;
                    }
                    else if (adjudication.Decision == DuplicateStepDecision.Replacement && adjudication.Step != null)
                    {
                        proposal.Step = adjudication.Step;
                        proposal.Step.SkipDuplicateCheck = true;
                        await EmitLog(emitSse, "info",
                            $"LLM supplied a deduped replacement step: [{proposal.Step.File}] {proposal.Step.Change}", ct: ct);
                        duplicateOf = null;
                    }
                    else
                    {
                        var dupFb =
                            $"STEP ALREADY DONE — [{proposal.Step.File}] {proposal.Step.Change}\n" +
                            $"The duplicate heuristic and LLM adjudication both found it equivalent to the completed step:\n" +
                            $"  [{duplicateOf.File}] {duplicateOf.Change}\n" +
                            $"Do not repeat completed work. If another requirement remains, return one concrete, distinct step; otherwise return planComplete=true.";
                        await EmitRejectedLog(emitSse,
                            $"Interleaved execution: rejected duplicate step — [{proposal.Step.File}] {proposal.Step.Change} repeats completed step [{duplicateOf.File}] {duplicateOf.Change}", dupFb, ct);
                        rejectionFeedback.Add(dupFb);
                        if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                        continue;
                    }
                }
            }
            // Web-step gate (mirror of the incremental loop): a _web_search/_web_fetch
            // proposal is allowed freely once the LLM has confirmed the task needs the web
            // (webNeedVerified == 1). Otherwise the first web proposal triggers ONE
            // confirmation round; if the verdict is that the task does NOT need current
            // external info, the web step is rejected with feedback steering back to the
            // repo context (no further web steps this run).
            if (IsWebStep(proposal.Step.File) && webNeedVerified != 1)
            {
                if (webNeedVerified == 0 && webNeedVerifyFailures < 2)
                {
                    await EmitLog(emitSse, "info",
                        "The planner proposed a _web_search/_web_fetch step — checking whether the task actually needs current external info…", ct: ct);
                    var (needsWeb, query, vetoReason, verified) = await ConfirmWebNeedAsync(prompt, emitSse, ct);
                    if (verified)
                    {
                        webNeedVerified = needsWeb ? 1 : -1;
                        if (needsWeb) webInjectedQuery = query;
                        else webNeedVetoReason = vetoReason;
                    }
                    else
                    {
                        webNeedVerifyFailures++; // transient classifier failure — retry up to 2×, then fail open
                    }
                }
                if (webNeedVerified == -1)
                {
                    // Surface the classifier's own reason inline on the rejected step card
                    // (the plan-item error row) so a false veto is auditable without
                    // expanding the transcript.
                    var vetoDetail = string.IsNullOrWhiteSpace(webNeedVetoReason)
                        ? ""
                        : $" Classifier: {webNeedVetoReason}";
                    await EmitRejectedLog(emitSse,
                        $"Interleaved execution: rejected [{proposal.Step.File}] {proposal.Step.Change} — task does not need current external info.{vetoDetail}",
                        WebNotNeededFeedback + vetoDetail, ct);
                    rejectionFeedback.Add(WebNotNeededFeedback + vetoDetail);
                    if (emitSse)
                        await SendSse(Response, "step", new
                        {
                            index = rejectionStepIndex++,
                            type = "plan",
                            status = "rejected",
                            path = proposal.Step.File,
                            description = proposal.Step.Change,
                            error = $"Task does not need current external info.{vetoDetail}",
                            line = proposal.Step.LineNumber,
                            planItemIndex = planSoFar.Count
                        }, ct);
                    // Persist the veto on the card's plan so it survives a reload.
                    await PersistRejectedPlanStepAsync(cardId, proposal.Step, $"Task does not need current external info.{vetoDetail}", emitSse, ct);
                    if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                    continue;
                }
                // webNeedVerified == 1 after the confirmation round → allow the web step freely.
            }
            // Missing-web-search guard: if the task hints at needing CURRENT
            // EXTERNAL information but neither the plan nor this proposal carries a
            // _web_search/_web_fetch step, confirm with the LLM (regex alone is too
            // noisy — "search for" usually means searching the repo) and, when
            // confirmed, reject with feedback steering the model to the web tools.
            // The verification is memoized so it costs at most one call per run.
            if (proposal.Step.File != null &&
                !IsWebStep(proposal.Step.File) &&
                // Filesystem PREP is not a refusal to use the web tools — a web-needing task
                // that demands a folder ("create a folder called X …") must be able to create
                // it up front instead of bouncing mkdir for 3 rejections before the search is
                // auto-injected. _create_directory and mkdir-style _command steps qualify
                // (the dump destination is additionally pre-created in the web step itself).
                !IsFilesystemPrepStep(proposal.Step) &&
                !planSoFar.Any(s => IsWebStep(s.File)) &&
                TaskHintsWebNeed(prompt))
            {
                if (webNeedVerified == 0 && webNeedVerifyFailures < 2)
                {
                    await EmitLog(emitSse, "info",
                        "Task hints at needing current external info — checking whether _web_search is required…", ct: ct);
                    var (needsWeb, query, vetoReason, verified) = await ConfirmWebNeedAsync(prompt, emitSse, ct);
                    if (verified)
                    {
                        webNeedVerified = needsWeb ? 1 : -1;
                        if (needsWeb) webInjectedQuery = query;
                        else webNeedVetoReason = vetoReason;
                    }
                    else
                    {
                        webNeedVerifyFailures++; // transient classifier failure — retry up to 2×, then fail closed
                    }
                }
                if (webNeedVerified == 1)
                {
                    // When the task explicitly ASKS for a script, the scraper steering stands
                    // down (writing it is the task) — only the generic web feedback remains.
                    var webFb = DirectoryIntentFeedback(proposal.Step)
                        ?? (TaskExplicitlyRequestsScript(prompt) ? null : ScraperScriptFeedback(proposal.Step))
                        ?? WebNeedFeedback;
                    await EmitRejectedLog(emitSse,
                        $"Interleaved execution: rejected [{proposal.Step.File}] {proposal.Step.Change} — task needs current external info but the plan has no _web_search step",
                        webFb, ct);
                    rejectionFeedback.Add(webFb);
                    if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS)
                    {
                        // The planner kept refusing _web_search after MAX_STEP_REGEN_ATTEMPTS
                        // rejections. Genuinely web-needing tasks must still complete, so stop
                        // asking and auto-inject a _web_search step. It is queued, so it flows
                        // through the exact same execution path as any model-proposed step
                        // (loop-top pendingSteps branch → ExecutePlan), board persistence and
                        // SSE included.
                        var queryText = string.IsNullOrWhiteSpace(webInjectedQuery)
                            ? BuildFallbackWebQuery(prompt)
                            : webInjectedQuery.Trim();
                        pendingSteps.Enqueue(new PlanStep { File = "_web_search", Change = queryText });
                        await EmitLog(emitSse, "warn",
                            $"The planner refused to use _web_search after {MAX_STEP_REGEN_ATTEMPTS} rejections — auto-injecting a _web_search step: \"{queryText}\"", ct: ct);
                        regenAttempts = 0;
                        rejectionFeedback.Clear();
                        continue; // loop top picks up the injected step and executes it
                    }
                    continue;
                }
            }
            // Task-tool mismatch guard (mirror of the web-need gate): a task that
            // targets the OS filesystem outside the repo must NOT be implemented by
            // editing repo source files — that is the "wrote an HTTP endpoint to
            // create a desktop folder" failure. Pure OS tasks reject repo edits
            // deterministically; when the task also hints at repo work (e.g. "save a
            // link in the README"), one memoized LLM call per file adjudicates
            // whether the edit is genuinely required.
            if (osTaskGuard &&
                proposal.Step.File != null &&
                !AgentProjectUtilities.IsSpecialMarker(proposal.Step.File))
            {
                var rejectOsEdit = osTaskPure;
                if (!osTaskPure)
                {
                    var allowed = false;
                    if (osRepoEditVerdicts.TryGetValue(proposal.Step.File, out var cached))
                    {
                        allowed = cached;
                    }
                    else
                    {
                        var (isRequired, verified) = await ConfirmRepoEditRequiredAsync(prompt, proposal.Step.File, proposal.Step.Change, emitSse, ct);
                        // Cache only SUCCESSFUL verdicts — a transient LLM failure must
                        // not permanently lock a file out of a legitimately needed edit;
                        // a later re-proposal then retries the verification. A cached
                        // "allowed" also lets subsequent edits to that file through.
                        if (verified) osRepoEditVerdicts[proposal.Step.File] = isRequired;
                        allowed = isRequired;
                    }
                    rejectOsEdit = !allowed;
                }
                if (rejectOsEdit)
                {
                    await EmitRejectedLog(emitSse,
                        $"Interleaved execution: rejected [{proposal.Step.File}] {proposal.Step.Change} — task targets the OS filesystem, not the repository",
                        OsTaskEditFeedback, ct);
                    await PersistRejectedPlanStepAsync(cardId, proposal.Step, OsTaskEditFeedback, emitSse, ct);
                    rejectionFeedback.Add(OsTaskEditFeedback);
                    if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                    continue;
                }
            }
            var skipLlm = regenAttempts > 0;
            if (!skipLlm && !AgentProjectUtilities.IsSpecialMarker(proposal.Step.File))
            {
                var fp = Path.GetFullPath(Path.Combine(projectRoot, (proposal.Step.File ?? "").Replace('/', Path.DirectorySeparatorChar)));
                if (System.IO.File.Exists(fp)) skipLlm = true;
            }
            // If the last step had needsExtraStep=false, force LLM validation to check if the new step
            // is about a genuinely different concern — the per-step verifier already confirmed completion.
            string? completionNote = null;
            var lastResult = allResults
                .OfType<Dictionary<string, object?>>()
                .Where(r => r.ContainsKey("needsExtraStep") && r.GetValueOrDefault("status")?.ToString() is "modified" or "done" or "created")
                .LastOrDefault();
            if (lastResult != null && lastResult["needsExtraStep"] is false &&
                string.Equals(lastResult.GetValueOrDefault("path")?.ToString(), proposal.Step.File, StringComparison.OrdinalIgnoreCase))
            {
                var lastPath = lastResult.GetValueOrDefault("path")?.ToString() ?? "";
                var lastChange = lastResult.GetValueOrDefault("change")?.ToString() ?? "";
                completionNote = $"The previous step [{lastPath}] \"{lastChange}\" was verified complete (needsExtraStep=false). " +
                    "Your proposed step MUST address a GENUINELY DIFFERENT requirement from the original task, " +
                    "not a continuation or cleanup of already-completed work. If the task is done, return planComplete=true.";
                skipLlm = false; // force LLM validation
            }
            if (emitSse)
                await EmitLog(true, "info", $"Pre-validate plan event: planSoFar.Count={planSoFar.Count}, step.File={proposal.Step.File}", ct: ct);
            var proposedPlanItems = planSoFar.Select((s, idx) => new
            {
                File = s.File,
                Change = s.Change,
                Line = s.LineNumber,
                OldString = s.OldString,
                NewString = s.NewString,
                done = true
            }).ToList<object>().Concat(new object[]
            {
                new
                {
                    File = proposal.Step.File, Change = proposal.Step.Change,
                    Line = proposal.Step.LineNumber,
                    OldString = proposal.Step.OldString, NewString = proposal.Step.NewString,
                    done = false
                }
            }).ToList();
            if (emitSse)
            {
                await EmitLog(true, "info", $"Sending plan event with {proposedPlanItems.Count} item(s)", ct: ct);
                await SendSse(Response, "plan", new
                {
                    thinking = thinkingLog.ToString(),
                    summary = $"Proposing step {planSoFar.Count + 1}",
                    items = proposedPlanItems,
                    incremental = true
                }, ct);
            }
            var (valid, reason) = await ValidateIncrementalStepAsync(
                proposal.Step, prompt, discoveryContext, planSoFar, projectRoot, emitSse, ct,
                skipLlm: skipLlm, lastStepCompletionNote: completionNote, attachedFiles: attachedFiles);
            if (!valid)
            {
                if (reason != null && reason.StartsWith(FetchCommandFeedbackMarker, StringComparison.Ordinal)) fetchCommandRejections++;
                var stepFb = $"REJECTED — [{proposal.Step.File}] {proposal.Step.Change} → {reason}";
                await EmitRejectedLog(emitSse,
                    $"Interleaved execution: rejected [{proposal.Step.File}] {proposal.Step.Change} — {reason}", stepFb, ct);
                // Persist the rejected step on the card's plan so every rejection
                // (OS-task guard, fetch-command guard, duplicates, ...) survives a reload.
                await PersistRejectedPlanStepAsync(cardId, proposal.Step, reason, emitSse, ct);
                rejectionFeedback.Add(stepFb);
                if (emitSse && planSoFar.Count > 0)
                {
                    var planSummary = $"Step {planSoFar.Count + 1} rejected — {reason}";
                    thinkingLog.AppendLine($"\n[{planSummary}]");
                    await SendSse(Response, "plan", new
                    {
                        thinking = thinkingLog.ToString(),
                        summary = planSummary,
                        items = planSoFar.Select((s, idx) => new
                        {
                            File = s.File,
                            Change = s.Change,
                            Line = s.LineNumber,
                            OldString = s.OldString,
                            NewString = s.NewString,
                            done = true,
                            GroundTruth = s.GroundTruth
                        }).ToList(),
                        incremental = true
                    }, ct);
                }
                if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS)
                {
                    // FETCH-COMMAND AUTO-INJECT: the planner kept proposing URL-fetching
                    // _command steps instead of a web step (the "api.current.ai" failure
                    // mode) — inject _web_search directly, mirroring the missing-web-search
                    // auto-inject, so the fetch-command loop can't dead-end in a slot failure.
                    if (fetchCommandRejections >= 2)
                    {
                        var queryText = string.IsNullOrWhiteSpace(webInjectedQuery)
                            ? BuildFallbackWebQuery(prompt)
                            : webInjectedQuery.Trim();
                        pendingSteps.Enqueue(new PlanStep { File = "_web_search", Change = queryText });
                        await EmitLog(emitSse, "warn",
                            $"The planner kept proposing URL-fetching _command steps instead of _web_search after {MAX_STEP_REGEN_ATTEMPTS} rejections — auto-injecting a _web_search step: \"{queryText}\"", ct: ct);
                        rejectionFeedback.Clear();
                        regenAttempts = 0;
                        fetchCommandRejections = 0;
                        consecutiveSlotFailures = 0;
                        continue; // loop top executes the injected step, then planning resumes
                    }
                    consecutiveSlotFailures++;
                    rejectionFeedback.Clear();
                    regenAttempts = 0;
                    if (consecutiveSlotFailures >= 3)
                        throw new InvalidOperationException(
                            "Interleaved planner failed 3 slots in a row — attach the correct file(s) and retry.");
                    continue;
                }
                continue;
            }
            consecutiveSlotFailures = 0;
            fetchCommandRejections = 0;
            var planningRounds = regenAttempts + 1;
            totalPlanningRounds += planningRounds;
            regenAttempts = 0;
            rejectionFeedback.Clear();
            var retryWord = planningRounds - 1 == 1 ? "retry" : "retries";
            await EmitLog(emitSse, planningRounds >= 3 ? "warn" : "metric",
                planningRounds > 1
                    ? $"📊 Step {planSoFar.Count + 1} planned in {planningRounds} rounds ({planningRounds - 1} {retryWord}) — [{proposal.Step.File}] {proposal.Step.Change}"
                    : $"📊 Step {planSoFar.Count + 1} planned in 1 round — [{proposal.Step.File}] {proposal.Step.Change}", ct: ct);
            if (HasConcreteEdit(proposal.Step))
                await EmitLog(emitSse, "bypass",
                    "⚡ Step accepted — planner supplied a concrete edit (oldString/newString, FORMAT C/D, or fullFile); executing it directly without further planning", ct: ct);
            if (proposal.AdditionalSteps?.Count > 0)
            {
                foreach (var extraStep in proposal.AdditionalSteps)
                {
                    if (extraStep.File != null && extraStep.File.Equals("_discover", StringComparison.OrdinalIgnoreCase))
                    {
                        if (exploredFiles.Add("_discover"))
                        {
                            await EmitLog(emitSse, "info", "Additional _discover step — running project-wide search…", ct: ct);
                            discoveryContext = await RunDiscoveryToolAsync(prompt, discoveryContext, projectRoot, emitSse, ct);
                            await EmitContextUpdateAsync(runContext, discoveryContext, emitSse, ct);
                        }
                        continue;
                    }
                    // Same file-truth guard as the main duplicate check: an overlapping step
                    // description is only a duplicate when its named target actually exists in
                    // the file — a partially-applied earlier step must not starve the follow-up.
                    var extraIsDup = false;
                    foreach (var s in planSoFar)
                    {
                        if (string.Equals(s.File, extraStep.File, StringComparison.OrdinalIgnoreCase) &&
                            TokenOverlap(s.Change ?? "", extraStep.Change ?? "") > 0.35 &&
                            (AgentProjectUtilities.IsSpecialMarker(extraStep.File) ||
                             await StepTargetExistsInFileAsync(extraStep, projectRoot, ct)))
                        {
                            extraIsDup = true;
                            break;
                        }
                    }
                    if (!extraIsDup)
                    {
                        pendingSteps.Enqueue(extraStep);
                        await EmitLog(emitSse, "info",
                            $"Queued additional step for later: [{extraStep.File}] {extraStep.Change}", ct: ct);
                    }
                }
            }
            var stepToRun = proposal.Step;
            if (stepToRun != null)
            {
                var dupStep = planSoFar.FirstOrDefault(s =>
                    s.File != "_noop" &&
                    string.Equals(s.File, stepToRun.File, StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(s.Change, stepToRun.Change, StringComparison.Ordinal) ||
                     (s.Change?.Length > 10 && stepToRun.Change?.Length > 10 &&
                      (s.Change.Contains(stepToRun.Change) || stepToRun.Change.Contains(s.Change)))) &&
                    ((!string.IsNullOrEmpty(s.OldString) && string.Equals(s.OldString, stepToRun.OldString, StringComparison.Ordinal)) ||
                     (!string.IsNullOrEmpty(s.NewString) && string.Equals(s.NewString, stepToRun.NewString, StringComparison.Ordinal)) ||
                     (!string.IsNullOrEmpty(s.TargetSymbol) && string.Equals(s.TargetSymbol, stepToRun.TargetSymbol, StringComparison.Ordinal))));
                if (dupStep != null)
                {
                    await EmitLog(emitSse, "info",
                        $"Plan complete — duplicate step in interleaved execution: [{stepToRun.File}] {stepToRun.Change} — nothing new to add",
                        ct: ct);
                    break;
                }
            }
            if (stepToRun != null)
            {
                planSoFar.Add(stepToRun);
                if (!string.IsNullOrWhiteSpace(proposal.Thinking))
                    thinkingLog.AppendLine($"Step {planSoFar.Count}: {proposal.Thinking}");
                var stepToRunIsConcrete = ShouldApplyDirectly(stepToRun);
                await EmitLog(emitSse, "info",
                    stepToRunIsConcrete
                        ? $"▶ Applying edits — Step {planSoFar.Count} — [{stepToRun.File}] {stepToRun.Change}"
                        : $"▶ Thinking for edit — Step {planSoFar.Count} — [{stepToRun.File}] {stepToRun.Change}", ct: ct);
                await SendPlanActivityEventAsync(thinkingLog, planSoFar, emitSse,
                    "_executing", stepToRunIsConcrete
                        ? $"Applying edits — Step {planSoFar.Count} — {stepToRun.Change}"
                        : $"Thinking for edit — Step {planSoFar.Count} — {stepToRun.Change}",
                    $"Completed {planSoFar.Count - 1} step(s) — {(stepToRunIsConcrete ? "applying edits for" : "thinking for edit on")} Step {planSoFar.Count}",
                    planSoFar.Count - 1, ct);
                await PersistBoardDataPlanAsync(cardId, planSoFar, emitSse, ct,
                    summary: $"Interleaved execution — {planSoFar.Count} step(s) so far", score: 90);
                var singleStepPlan = new AgentPlan { Plan = new List<PlanStep> { stepToRun }, Summary = stepToRun.Change, Score = 90 };
                var beforeCount = allResults.Count;
                var preEditContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (stepToRun?.File != null && !AgentProjectUtilities.IsSpecialMarker(stepToRun.File))
                {
                    var fp = Path.GetFullPath(Path.Combine(projectRoot, (stepToRun.File ?? "").Replace('/', Path.DirectorySeparatorChar)));
                    if (System.IO.File.Exists(fp) && stepToRun.File != null)
                    {
                        var preContent = await System.IO.File.ReadAllTextAsync(fp, Encoding.UTF8, ct);
                        preEditContents[stepToRun.File] = preContent;
                        var relKey = stepToRun.File!.Replace('\\', '/');
                        if (!preRunSnapshots.ContainsKey(relKey)) preRunSnapshots[relKey] = preContent;
                    }
                }
                try
                {
                    await ExecutePlanCore(runContext, prompt, projectRoot, emitSse, discoveryContext, singleStepPlan, ct, allResults,
                        steeringContext: steeringContext, attachedFiles: attachedFiles, cardId: cardId,
                        replanBudget: new[] { 0 }, onActivity: planActivity,
                        skipLlmPreResolution: stepToRunIsConcrete);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await EmitLog(emitSse, "error",
                        $"⛔ Interleaved execution halted — step {planSoFar.Count} threw: {ex.Message}", ct: ct);
                    if (planSoFar.Count > 0) planSoFar.RemoveAt(planSoFar.Count - 1);
                    await PersistBoardDataPlanAsync(cardId, planSoFar, emitSse, ct,
                        summary: $"Execution halted at step {planSoFar.Count + 1} — exception: {ex.Message}", score: 0,
                        append: false);
                    break;
                }
                var newResults = allResults.Skip(beforeCount).OfType<Dictionary<string, object?>>().ToList();
                var stepSucceeded = newResults.Any(r => r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created");
                await EmitLog(emitSse, "info",
                    $"DIAG: After ExecutePlan — stepSucceeded={stepSucceeded}, planSoFar.Count={planSoFar.Count}, newResults.Count={newResults.Count}",
                    ct: ct);
                discoveryContext = AppendWebResultsToDiscoveryContext(discoveryContext, newResults);
                discoveryContext = AppendCreatedPathsToDiscoveryContext(discoveryContext, newResults, projectRoot);
                discoveryContext = AppendServerRecoveryToDiscoveryContext(discoveryContext, newResults);
                await EmitContextUpdateAsync(runContext, discoveryContext, emitSse, ct);
                var globalPlanIdx = planSoFar.Count - 1;
                foreach (var r in newResults)
                {
                    r["planItemIndex"] = globalPlanIdx;
                    if (emitSse && r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
                        await SendSse(Response, "step", r, ct);
                }
                await PersistBoardDataPlanStepAsync(cardId, globalPlanIdx, emitSse, ct, projectRoot: projectRoot);
                if (singleStepPlan.Plan.Count > 1)
                {
                    var chainIntact = true;
                    var anyNestedGeneration = false;
                    for (var si = 1; si < singleStepPlan.Plan.Count; si++)
                    {
                        var synthStep = singleStepPlan.Plan[si]!;
                        planSoFar.Add(synthStep);
                        await EmitLog(emitSse, "info",
                            $"▶ Resolving Edits for Step {planSoFar.Count} — [{synthStep.File}] {synthStep.Change}", ct: ct);
                        await SendPlanActivityEventAsync(thinkingLog, planSoFar, emitSse,
                            "_executing", $"Resolving Edits for Step {planSoFar.Count} — {synthStep.Change}",
                            $"Completed {planSoFar.Count - 1} step(s) — resolving edits for Step {planSoFar.Count}",
                            planSoFar.Count - 1, ct);
                        await PersistBoardDataPlanAsync(cardId, planSoFar, emitSse, ct,
                            summary: $"Interleaved execution — {planSoFar.Count} step(s) so far (incl. auto)", score: 90);
                        var synthPlan = new AgentPlan
                        { Plan = new List<PlanStep> { synthStep }, Summary = synthStep.Change, Score = 90 };
                        var synthBefore = allResults.Count;
                        if (synthStep?.File != null && !AgentProjectUtilities.IsSpecialMarker(synthStep.File))
                        {
                            var sfp = Path.GetFullPath(Path.Combine(projectRoot, synthStep.File.Replace('/', Path.DirectorySeparatorChar)));
                            if (System.IO.File.Exists(sfp))
                            {
                                var preContent = await System.IO.File.ReadAllTextAsync(sfp, Encoding.UTF8, ct);
                                preEditContents[synthStep.File] = preContent;
                                var relKey = synthStep.File.Replace('\\', '/');
                                if (!preRunSnapshots.ContainsKey(relKey)) preRunSnapshots[relKey] = preContent;
                            }
                        }
                        try
                        {
                            await ExecutePlanCore(runContext, prompt, projectRoot, emitSse, discoveryContext, synthPlan, ct, allResults,
                                steeringContext: steeringContext, attachedFiles: attachedFiles, cardId: cardId,
                                replanBudget: new[] { 0 }, onActivity: planActivity);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            await EmitLog(emitSse, "error",
                                $"⛔ Auto-generated step {planSoFar.Count} threw: {ex.Message}", ct: ct);
                            if (planSoFar.Count > 0) planSoFar.RemoveAt(planSoFar.Count - 1);
                            chainIntact = false;
                            break;
                        }
                        var synthGlobalIdx = planSoFar.Count - 1;
                        if (synthGlobalIdx > 0)
                        {
                            var synthResults = allResults.Skip(synthBefore).OfType<Dictionary<string, object?>>().ToList();
                            foreach (var r in synthResults)
                            {
                                r["planItemIndex"] = synthGlobalIdx;
                            }
                        }
                        await PersistBoardDataPlanStepAsync(cardId, synthGlobalIdx, emitSse, ct, projectRoot: projectRoot);
                        if (synthPlan.Plan.Count > 1)
                            anyNestedGeneration = true;
                        if (synthStep!.File != null)
                            discoveryContext = await RefreshFileInDiscoveryContext(synthStep.File, discoveryContext, projectRoot, ct);
                    }
                    if (chainIntact && !anyNestedGeneration)
                    {
                        await EmitLog(emitSse, "info",
                            "Auto-generated follow-up chain exhausted — plan complete without further LLM round-trip", ct: ct);
                        break;
                    }
                }
                var touchedPaths = newResults
                    .Where(r => r.GetValueOrDefault("type")?.ToString() is "edit" or "create")
                    // Only edits that actually stuck feed the next planning round — failed/reverted
                    // attempts must never appear as applied changes.
                    .Where(r => r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
                    .Select(r => r.GetValueOrDefault("path")?.ToString())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var stepDiffs = new List<string>();
                if (stepSucceeded)
                {
                    foreach (var touched in touchedPaths)
                    {
                        if (touched == null) continue;
                        var oldContent = preEditContents.GetValueOrDefault(touched);
                        var fp = Path.GetFullPath(Path.Combine(projectRoot, touched.Replace('/', Path.DirectorySeparatorChar)));
                        if (oldContent != null && System.IO.File.Exists(fp))
                        {
                            var newContent = await System.IO.File.ReadAllTextAsync(fp, Encoding.UTF8, ct);
                            var diff = ComputeSimpleDiff(oldContent, newContent, touched);
                            if (!string.IsNullOrWhiteSpace(diff)) stepDiffs.Add(diff);
                        }
                    }
                }
                foreach (var touched in touchedPaths)
                    discoveryContext = await RefreshFileInDiscoveryContext(touched!, discoveryContext, projectRoot, ct);

                // After each step, inject a live directory listing for any directories that were
                // touched. This prevents the planner from hallucinating that a file was created
                // when it wasn't — it can see exactly what exists on disk.
                var affectedDirs = touchedPaths
                    .Select(p => Path.GetDirectoryName(p?.Replace('/', Path.DirectorySeparatorChar) ?? ""))
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var dir in affectedDirs)
                {
                    var fullDir = Path.GetFullPath(Path.Combine(projectRoot, dir!));
                    if (!Directory.Exists(fullDir)) continue;
                    var files = Directory.GetFiles(fullDir)
                        .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/'))
                        .OrderBy(f => f)
                        .ToList();
                    if (files.Count == 0) continue;
                    var inventoryKey = $"### FILES IN {dir!.Replace('\\', '/')} (current state on disk) ###";
                    // Remove stale entry if present, then append fresh one
                    var staleIdx = discoveryContext.IndexOf(inventoryKey, StringComparison.Ordinal);
                    if (staleIdx >= 0)
                    {
                        var staleEnd = discoveryContext.IndexOf("\n### ", staleIdx + inventoryKey.Length, StringComparison.Ordinal);
                        discoveryContext = staleEnd >= 0
                            ? discoveryContext[..staleIdx] + discoveryContext[staleEnd..]
                            : discoveryContext[..staleIdx];
                    }
                    discoveryContext += $"\n{inventoryKey}\n" +
                        string.Join("\n", files.Select(f => $"  - {f}")) + "\n";
                }
                // Feed ONLY successful diffs into the bounded accumulator, then (optionally) LLM-
                // summarize once the accumulated section passes the user's threshold. One section,
                // replaced in place — never a growing stack of raw diffs.
                if (stepDiffs.Count > 0)
                {
                    diffContextAccum.AppendLine(string.Join("\n", stepDiffs));
                }
                var diffCfg = await LoadConfigAsync();
                if (diffCfg.summarizeDiffContext && diffContextAccum.Length > diffCfg.diffContextSummaryChars)
                {
                    var summary = await SummarizeDiffContextAsync(diffContextAccum.ToString(), emitSse, ct);
                    if (!string.IsNullOrWhiteSpace(summary))
                    {
                        diffContextAccum.Clear();
                        diffContextAccum.Append(summary);
                        await EmitLog(emitSse, "metric",
                            $"📊 Diff context summarized: accumulated diffs ({diffCfg.diffContextSummaryChars}+ chars) → compact summary", ct: ct);
                    }
                }
                if (diffContextAccum.Length > 0)
                {
                    var diffSection = "\n### CHANGES FROM PREVIOUS STEP ###\n" +
                        diffContextAccum.ToString().TrimEnd() + "\n" +
                        "(These changes are already reflected in the file content above. " +
                        "The next step MUST build on them, not repeat them.)\n";
                    discoveryContext = ReplaceDiscoveryDiffSection(discoveryContext, diffSection);
                }
                var newEditLogLines = newResults
                    .Where(r => r.GetValueOrDefault("type")?.ToString() == "edit" &&
                                r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
                    .Select(r => $"  · {r.GetValueOrDefault("path")}: {r.GetValueOrDefault("change") ?? r.GetValueOrDefault("editAction") ?? "modified"}")
                    .ToList();
                if (newEditLogLines.Count > 0)
                {
                    var fullLog = "### EDIT LOG (changes applied — do NOT repeat them) ###\n" +
                        string.Join("\n", newEditLogLines) + "\n";
                    if (!discoveryContext.Contains(fullLog))
                        discoveryContext += "\n" + fullLog;
                }
                var editLog = newResults
        .Where(r => r.GetValueOrDefault("type")?.ToString() == "edit" &&
                    r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
        .Select(r => $"  {r.GetValueOrDefault("path")} — " +
            (r.GetValueOrDefault("change")?.ToString() ?? r.GetValueOrDefault("editAction")?.ToString() ?? "modified"))
        .ToList();
                if (editLog.Count > 0)
                {
                    var logSection = "\n### EDIT LOG (changes applied in previous steps) ###\n" +
                        string.Join("\n", editLog) + "\n";
                    discoveryContext += logSection;
                }
                var hadFailure = newResults.Any(r =>
                    r.GetValueOrDefault("status")?.ToString() == "error" ||
                    r.GetValueOrDefault("type")?.ToString() == "plan_halted");
                if (!stepSucceeded || hadFailure)
                {
                    if (!hadFailure)
                    {
                        await EmitLog(emitSse, "warn",
                            $"Step {planSoFar.Count} produced no code changes — skipping and continuing to next step.", ct: ct);
                        // Remove the no-op step from planSoFar so the planner can propose something else.
                        // Do NOT break — a no-op is not a fatal failure; keep planning.
                        if (planSoFar.Count > 0) planSoFar.RemoveAt(planSoFar.Count - 1);
                        continue;
                    }
                    // A FAILED _web_fetch/_web_search is a retryable failure, not a reason to halt
                    // the run: the URL was likely invented (the search results in the discovery
                    // context list real ones) or the site was transiently unreachable. Remove the
                    // failed step and feed the error back to the planner — it can retry with a
                    // REAL URL from the harvested ### WEB RESULTS section, or (for an OS-output
                    // task) plan the _command write directly. The pre-fix behavior halted here and
                    // the repair replanner — with no web results and no OS-write guidance —
                    // invented application code to "write" the demanded file. Bounded so a
                    // persistently-broken URL eventually falls through to the halt below.
                    var failedWebStep = IsWebStep(stepToRun?.File) && webStepFailures < MaxWebStepRetries;
                    if (failedWebStep)
                    {
                        webStepFailures++;
                        var failedWebResult = newResults.FirstOrDefault(r =>
                            r.GetValueOrDefault("type")?.ToString() is "_web_search" or "_web_fetch" or "web_search" or "web_fetch" &&
                            r.GetValueOrDefault("status")?.ToString() == "error");
                        var webErr = failedWebResult?.GetValueOrDefault("error")?.ToString()
                                     ?? failedWebResult?.GetValueOrDefault("output")?.ToString() ?? "";
                        if (planSoFar.Count > 0) planSoFar.RemoveAt(planSoFar.Count - 1);
                        var fb = $"The _web_fetch step to '{stepToRun?.Change}' failed" +
                                 $"{(string.IsNullOrWhiteSpace(webErr) ? "" : $": {TruncateForLlm(webErr, 300)}")}. " +
                                 $"Do NOT retry the same URL and do NOT invent a URL. Use a REAL URL from the " +
                                 $"### WEB RESULTS section in the discovery context (copy it verbatim from the search results), " +
                                 $"or if the task demands an OS output file, plan a _command step that writes the " +
                                 $"already-gathered search results to the demanded path instead of fetching.";
                        await EmitRejectedLog(emitSse,
                            $"Web step failed ({webStepFailures}/{MaxWebStepRetries} retries used) — feeding the failure back to the planner",
                            fb, ct);
                        rejectionFeedback.Add(fb);
                        continue;
                    }
                    // A FAILED _command is ALSO fed back for a recovery edit (bounded like web
                    // steps): the benchmark-22 failure — `node server.js` crashed with a
                    // SyntaxError but was reported "done", so no recovery edit was ever planned
                    // and the planner deadlocked re-proposing the same broken command (rejected
                    // as a duplicate) until the slot-failure valve threw. The error output gives
                    // the planner the diagnostic it needs to fix the failing file (or change
                    // approach) instead of halting into verification blind.
                    var failedCommandStep = stepToRun?.File?.Equals("_command", StringComparison.OrdinalIgnoreCase) == true &&
                                            commandStepFailures < MaxCommandStepRetries;
                    if (failedCommandStep)
                    {
                        commandStepFailures++;
                        var failedCmdResult = newResults.FirstOrDefault(r =>
                            r.GetValueOrDefault("type")?.ToString() == "command" &&
                            r.GetValueOrDefault("status")?.ToString() == "error");
                        var cmdErr = failedCmdResult?.GetValueOrDefault("error")?.ToString()
                                     ?? failedCmdResult?.GetValueOrDefault("output")?.ToString() ?? "";
                        if (planSoFar.Count > 0) planSoFar.RemoveAt(planSoFar.Count - 1);
                        var fb2 = $"The _command step '{stepToRun?.Change}' FAILED" +
                                  $"{(string.IsNullOrWhiteSpace(cmdErr) ? "" : $": {TruncateForLlm(cmdErr, 300)}")}. " +
                                  "Diagnose the failure from the error output and plan a recovery step: fix the " +
                                  "failing file with an edit, or correct the command. Do NOT re-run the same " +
                                  "failing command unchanged.";
                        await EmitRejectedLog(emitSse,
                            $"Command step failed ({commandStepFailures}/{MaxCommandStepRetries} retries used) — feeding the failure back to the planner",
                            fb2, ct);
                        rejectionFeedback.Add(fb2);
                        continue;
                    }
                    // AUTO-INJECT _scraper: the fetch retry budget is exhausted and the task
                    // still demands data (a file output). Give the SYSTEM-BUILT scraper one
                    // deterministic attempt at the failed URL before halting — the web fetch
                    // tool and the scraper are different mechanisms (different UA, redirect and
                    // JS handling), so a URL the tool fails on can still be scraped. Gated on a
                    // real _web_fetch URL, a file output demand, and never more than once per
                    // run; a failing scraper falls through to the halt + repair auto-dump.
                    var failedUrl = stepToRun?.File?.Equals("_web_fetch", StringComparison.OrdinalIgnoreCase) == true
                        ? stepToRun.Change?.Trim()
                        : null;
                    if (!string.IsNullOrWhiteSpace(failedUrl) &&
                        failedUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                        !scraperInjected &&
                        AgentOsOutputVerifier.TryGetFileOutputTarget(prompt, projectRoot, out _) &&
                        !planSoFar.Any(s => s.File?.Equals("_scraper", StringComparison.OrdinalIgnoreCase) == true) &&
                        !pendingSteps.Any(s => s.File?.Equals("_scraper", StringComparison.OrdinalIgnoreCase) == true))
                    {
                        scraperInjected = true;
                        // Drop the failed fetch from the committed plan (like the retry path
                        // does) so the final plan shows only the steps that actually worked.
                        if (planSoFar.Count > 0) planSoFar.RemoveAt(planSoFar.Count - 1);
                        pendingSteps.Enqueue(new PlanStep { File = "_scraper", Change = failedUrl });
                        await EmitLog(emitSse, "warn",
                            $"Fetch retry budget exhausted ({webStepFailures}/{MaxWebStepRetries}) — auto-injecting a _scraper step for {failedUrl} " +
                            "(system-built scraper) before halting", ct: ct);
                        if (emitSse)
                            await SendSse(Response, "phase", new { message = "Falling back to a system-built scraper…" }, ct);
                        continue; // loop top drains the queued _scraper step and executes it
                    }
                    await EmitLog(emitSse, "warn",
                        $"Step {planSoFar.Count} did not complete successfully — stopping interleaved execution here " +
                        "so post-execution verification can assess what genuinely remains.", ct: ct);
                    if (planSoFar.Count > 0) planSoFar.RemoveAt(planSoFar.Count - 1);
                    await PersistBoardDataPlanAsync(cardId, planSoFar, emitSse, ct,
                        summary: $"Execution halted at step {planSoFar.Count + 1} — step failed", score: 0,
                        append: false);
                    if (emitSse)
                        await SendSse(Response, "plan-halted", new
                        {
                            reason = "Step produced an error",
                            failedStep = stepToRun?.File,
                            failedChange = stepToRun?.Change,
                            remainingSteps = 0
                        }, ct);
                    break;
                }
                var needsExtraResult = newResults
                    .OfType<Dictionary<string, object?>>()
                    .FirstOrDefault(r => r.GetValueOrDefault("needsExtraStep") is true);
                if (needsExtraResult != null && !hadFailure)
                {
                    var extraReason = needsExtraResult.GetValueOrDefault("extraStepReason")?.ToString();
                    var extraFile = needsExtraResult.GetValueOrDefault("extraStepFile")?.ToString()
                                    ?? needsExtraResult.GetValueOrDefault("path")?.ToString()
                                    ?? stepToRun?.File ?? "";
                    var missingSymbolMatch = Regex.Match(extraReason ?? "",
                        @"(?:missing\s+(?:method|property|function)\s*)[\(`]?(?:vm\.)?(\w+)[\)`]?");
                    var missingSymbol = missingSymbolMatch.Success ? missingSymbolMatch.Groups[1].Value : null;
                    if (!string.IsNullOrWhiteSpace(missingSymbol))
                    {
                        var knownBuiltIns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "preventDefault", "stopPropagation", "console", "event", "$event"
                    };
                        if (knownBuiltIns.Contains(missingSymbol))
                        {
                            await EmitLog(emitSse, "warn",
                                $"Skipping verifier auto-step for built-in API: {missingSymbol}", ct: ct);
                        }
                        else
                        {
                            var autoStep = new PlanStep
                            {
                                File = extraFile!,
                                Change = $"Add {missingSymbol} method — referenced by previous step but not yet implemented. " +
                                         $"Verifier reason: {extraReason}",
                                TargetSymbol = missingSymbol,
                                Priority = 1,
                                LineNumber = 0,
                                ReferenceFiles = stepToRun?.ReferenceFiles ?? new List<string>()
                            };
                            var isDup = planSoFar.Any(s =>
                                string.Equals(s.File, autoStep.File, StringComparison.OrdinalIgnoreCase) &&
                                TokenOverlap(s.Change ?? "", autoStep.Change) > 0.5);
                            // Also skip when the method already exists on disk (stale-snapshot re-insertion).
                            if (!isDup && !string.IsNullOrWhiteSpace(autoStep.File))
                            {
                                var autoStepPath = System.IO.Path.GetFullPath(Path.Combine(projectRoot, autoStep.File.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                                if (System.IO.File.Exists(autoStepPath) &&
                                    MethodNameExistsInFile(await System.IO.File.ReadAllTextAsync(autoStepPath, Encoding.UTF8, ct), missingSymbol))
                                {
                                    await EmitLog(emitSse, "info",
                                        $"  ⏭ Verifier auto-step skipped: {missingSymbol}() already exists on disk in {autoStep.File}", ct: ct);
                                    isDup = true;
                                }
                            }
                            if (!isDup)
                            {
                                planSoFar.Add(autoStep);
                                await EmitLog(emitSse, "info",
                                    $"⚡ Verifier flagged needsExtraStep — auto-proposing next step WITHOUT planner LLM: " +
                                    $"[{autoStep.File}] {missingSymbol}()", ct: ct);
                                await SendPlanActivityEventAsync(thinkingLog, planSoFar, emitSse,
                                    "_executing", $"Resolving Edits for Step {planSoFar.Count} — {autoStep.Change}",
                                    $"Completed {planSoFar.Count - 1} step(s) — resolving edits for Step {planSoFar.Count} from verifier",
                                    planSoFar.Count - 1, ct);
                                await PersistBoardDataPlanAsync(cardId, planSoFar, emitSse, ct,
                                    summary: $"Interleaved execution — {planSoFar.Count} step(s)", score: 90);
                                var autoPlan = new AgentPlan
                                {
                                    Plan = new List<PlanStep> { autoStep },
                                    Summary = autoStep.Change,
                                    Score = 90
                                };
                                var autoBeforeCount = allResults.Count;
                                try
                                {
                                    await ExecutePlanCore(runContext, prompt, projectRoot, emitSse, discoveryContext, autoPlan, ct, allResults,
                                        steeringContext: steeringContext, attachedFiles: attachedFiles, cardId: cardId,
                                        replanBudget: new[] { 0 }, onActivity: planActivity);
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    await EmitLog(emitSse, "error",
                                        $"⛔ Auto-step from verifier threw: {ex.Message}", ct: ct);
                                }
                                var autoResults = allResults.Skip(autoBeforeCount)
                                    .OfType<Dictionary<string, object?>>()
                                    .Where(r => r.GetValueOrDefault("type")?.ToString() is "edit" or "create")
                                    .Select(r => r.GetValueOrDefault("path")?.ToString())
                                    .Where(p => !string.IsNullOrWhiteSpace(p))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList();
                                foreach (var touched in autoResults)
                                    discoveryContext = await RefreshFileInDiscoveryContext(touched!, discoveryContext, projectRoot, ct);
                                continue;
                            }
                        }
                    }
                }
                // ── Between-steps completion verification ──────────────────────────────────
                // The per-step edit verifier marked the last edit complete (needsExtraStep=false),
                // OR the last step was a successful web step (_web_search/_web_fetch). Before
                // proposing yet ANOTHER step, verify whether the WHOLE task is now satisfied by
                // the original prompt + all applied changes. If it is, declare the plan complete
                // instead of blindly planning a redundant follow-up step. The web arm is the
                // web-only mirror: IsLastEditVerifiedComplete requires an edit result, so a
                // web-only run never triggered this gate and the whole-task assessment never ran
                // for it — the planner could declare planComplete after a fetch (with the
                // demanded write still missing) and the run would end unassessed.
                var lastEditVerified = IsLastEditVerifiedComplete(newResults);
                var lastWebVerified = IsLastWebStepComplete(newResults);
                var webGated = lastWebVerified && !lastEditVerified;
                if (stepSucceeded && !hadFailure && (lastEditVerified || lastWebVerified))
                {
                    await EmitLog(emitSse, "info",
                        webGated
                            ? "🔍 Between-steps verification: last web step completed — checking whether the whole task is done…"
                            : "🔍 Between-steps verification: last edit verified complete — checking whether the whole task is done…", ct: ct);
                    if (emitSse)
                        await SendPlanActivityEventAsync(thinkingLog, planSoFar, emitSse,
                            "_verifying", $"Verifying whole task — checking if the plan is complete after Step {planSoFar.Count}…",
                            $"Verifying whole task after Step {planSoFar.Count}…", planSoFar.Count - 1, ct);
                    var (isComplete, assessReason) = await AssessCompletionCore(runContext,
                        prompt, allResults, projectRoot, ct,
                        new AgentPlan { Plan = planSoFar.ToList(), Summary = "Interleaved verification", Score = 90 },
                        attachedFiles: attachedFiles, atomicStepEstimate: atomicStepEstimate,
                        steeringContext: steeringContext);
                    lastAssessIncomplete = !isComplete;
                    lastAssessReason = assessReason ?? "";
                    // AssessCompletion now uses the configurable LLM timeout and retries once,
                    // so a slow local model can actually finish the assessment. If the assessment
                    // is STILL unavailable (LLM down / unparseable response), do NOT treat that
                    // as a hard "NOT complete" verdict that forces a redundant follow-up step:
                    // the per-step verifier already confirmed the last edit is complete
                    // (needsExtraStep=false) and no step failed — declare the plan complete
                    // instead of planning a meaningless step 2.
                    // Web-gated assessments require a REAL verdict: a web step has no per-step
                    // verifier confirmation, so an unavailable LLM must NOT be read as
                    // "verified complete" (that would prematurely end a multi-step web chain).
                    // Visual-inspection tasks are gated the same way but stronger — the live
                    // browser test is a DETERMINISTIC requirement for tasks that demand LOOKING
                    // at the rendered page (the benchmark-22 failure declared planComplete here
                    // after the build steps because the assessment LLM was unavailable, so the
                    // _browser_test step and the Test Browser tab never happened).
                    var visualGated = TestIntentClassifier.HasVisualInspectionHint(prompt) &&
                        !planSoFar.Any(s => string.Equals(s.File, "_browser_test", StringComparison.OrdinalIgnoreCase));
                    var shouldDeclareComplete = ShouldDeclarePlanCompleteAfterAssessment(
                        isComplete, assessReason, requireAssessment: webGated || visualGated,
                        visualInspectionPending: visualGated, out var completeReason, out var assessFailed);
                    if (shouldDeclareComplete)
                    {
                        planCompleteDeclared = true;
                        await EmitLog(emitSse, assessFailed ? "warn" : "success",
                            $"✓ Plan complete after step {planSoFar.Count} — {completeReason}", ct: ct);
                        thinkingLog.AppendLine($"\n[Plan complete — {completeReason}]");
                        // Whole-task verified complete → clear any pending unmet-requirement state
                        // so a later restart does not re-plan work that is genuinely finished.
                        if (!string.IsNullOrWhiteSpace(cardId))
                            await PersistCardVerifyStateAsync(cardId, null, null, emitSse, ct);
                        if (emitSse)
                            await SendSse(Response, "plan", new
                            {
                                thinking = thinkingLog.ToString(),
                                summary = $"Plan complete — {completeReason}",
                                items = planSoFar.Select((s, idx) => new
                                {
                                    File = s.File, Change = s.Change, Line = s.LineNumber,
                                    OldString = s.OldString, NewString = s.NewString, done = true,
                                    GroundTruth = s.GroundTruth
                                }).ToList(),
                                incremental = true
                            }, ct);
                        break;
                    }
                    await EmitLog(emitSse, "metric",
                        $"🔍 Between-steps verification: task NOT complete yet — {completeReason}", ct: ct);
                    // Persist the unmet assessment onto the card so a crash/stop/restart re-plans
                    // the remaining requirement instead of trusting the all-done plan marks.
                    if (!string.IsNullOrWhiteSpace(cardId))
                        await PersistCardVerifyStateAsync(cardId, completeReason, null, emitSse, ct);
                }
            }
        }
        // ── HALTED-RUN VISUAL GATE ───────────────────────────────────────────────────────
        // The planComplete visual gate above only fires when the planner DECLARES completion.
        // A run can ALSO exit this loop without EVER proposing planComplete: the planner's
        // step proposals were rejected until the regen budget exhausted (the observed
        // benchmark-22 halt — the planner's oversized-anchor edit to backend/server.py was
        // rejected twice, then its third response was unparseable, and the loop broke with
        // planCompleteDeclared=false and NO _browser_test ever executed). For a task that
        // demands LOOKING at the rendered page, that is the same violation as declaring
        // completion early: the visual verification never happened, and post-execution
        // verification (which reads FILES, not a rendered page) would happily finish the run
        // without it. Inject + execute the live web test deterministically — the classifier
        // and _browser_test are both deterministic (no model content to trust) — so the run
        // cannot halt past the visual requirement. A browser_test result that FAILED is
        // handled downstream (deterministic PostExecuteVerify issue + repair re-run); this
        // gate only fires when NO browser test ran at all.
        if (!planCompleteDeclared &&
            TestIntentClassifier.HasVisualInspectionHint(prompt) &&
            !allResults.OfType<Dictionary<string, object?>>()
                .Any(r => r.GetValueOrDefault("type")?.ToString() == "browser_test"))
        {
            // A task that EXPLICITLY names the live browser test as a requirement
            // bypasses the LLM classifier: the classifier reads the dominant build/implement
            // language and answers needsVisual=false, vetoing the deterministically-required
            // browser test (benchmark 23). Same bypass as the plan-complete gate above.
            if (TestIntentClassifier.DemandsLiveBrowserTest(prompt))
            {
                visualNeedVerified = 1;
            }
            else if (visualNeedVerified == 0)
            {
                await EmitLog(emitSse, "info",
                    "Task demands visual verification — checking whether a _browser_test step is required…", ct: ct);
                var (needsVisual, target) = await ClassifyVisualInspectionPromptAsync(prompt, ct);
                // Fail OPEN on an unavailable classifier (null verdict): the
                // deterministic hint already fired, so a flaky LLM must not silently
                // skip the required live browser test at loop halt — only a confident
                // "needsVisual:false" from a working classifier vetoes.
                visualNeedVerified = TestIntentClassifier.ShouldInjectVisualBrowserTest(needsVisual) ? 1 : -1;
                if (needsVisual == true) visualNeedTarget = target;
            }
            if (visualNeedVerified == 1)
            {
                var inspectTarget = string.IsNullOrWhiteSpace(visualNeedTarget)
                    ? BuildFallbackVisualTarget(prompt)
                    : visualNeedTarget.Trim();
                await EmitLog(emitSse, "warn",
                    $"The interleaved loop halted without a _browser_test step — deterministically injecting a live web test: \"{inspectTarget}\"", ct: ct);
                planSoFar.Add(new PlanStep { File = "_browser_test", Change = inspectTarget });
                await ExecutePlanCore(runContext, prompt, projectRoot, emitSse, discoveryContext,
                    new AgentPlan
                    {
                        Plan = new List<PlanStep> { new PlanStep { File = "_browser_test", Change = inspectTarget } },
                        Summary = "Injected live web test", Score = 90
                    },
                    ct, allResults,
                    steeringContext: steeringContext, attachedFiles: attachedFiles,
                    cardId: cardId);
                await EmitLog(emitSse, "info",
                    $"_browser_test injected at loop halt — live web test of \"{inspectTarget}\" executed; the fresh result feeds the deterministic completion check.", ct: ct);
            }
        }
        var finalPlan = new AgentPlan
        {
            Thinking = thinkingLog.ToString(),
            Summary = $"Executed {planSoFar.Count} atomic step(s) via interleaved plan → execute → verify loop",
            Score = 90,
            Plan = planSoFar
        };
        return (finalPlan, allResults, discoveryContext, planCompleteDeclared, preRunSnapshots);
    }
    private async Task<string> RefreshFileInDiscoveryContext(
        string relPath, string discoveryContext, string projectRoot, CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!System.IO.File.Exists(fullPath)) return discoveryContext;
        string content;
        try { content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct); }
        catch { return discoveryContext; }
        var normPath = relPath.Replace('\\', '/');
        var pattern = new Regex(
            $@"^###\s+(?:read\s+)?{Regex.Escape(normPath)}\b.*?(?=^### |\z)",
            RegexOptions.Multiline | RegexOptions.Singleline);
        var replacement = $"### read {normPath}\n```\n{content}\n```\n\n";
        if (pattern.IsMatch(discoveryContext))
            return pattern.Replace(discoveryContext, m => replacement, 1);
        return discoveryContext.TrimEnd() + "\n\n" + replacement;
    }
    private static string ComputeSimpleDiff(string oldContent, string newContent, string relPath)
    {
        var oldLines = oldContent.Split('\n');
        var newLines = newContent.Split('\n');
        var oldSet = new HashSet<string>(oldLines, StringComparer.Ordinal);
        var newSet = new HashSet<string>(newLines, StringComparer.Ordinal);
        var added = newLines.Where(l => !string.IsNullOrWhiteSpace(l) && !oldSet.Contains(l)).ToList();
        var removed = oldLines.Where(l => !string.IsNullOrWhiteSpace(l) && !newSet.Contains(l)).ToList();
        if (added.Count == 0 && removed.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine($"  File: {relPath}");
        if (removed.Count > 0)
        {
            var removeSample = string.Join("\n    ", removed.Take(10));
            sb.AppendLine($"  Removed ({removed.Count} line(s)):\n    {removeSample}");
        }
        if (added.Count > 0)
        {
            var addSample = string.Join("\n    ", added.Take(10));
            sb.AppendLine($"  Added ({added.Count} line(s)):\n    {addSample}");
        }
        return sb.ToString();
    }
    /// <summary>
    /// Replaces the existing "### CHANGES FROM PREVIOUS STEP ###" section in the discovery
    /// context with a fresh one (appending it if none exists yet). Keeps exactly ONE bounded
    /// diff section instead of a growing stack of raw diffs per step.
    /// </summary>
    private static string ReplaceDiscoveryDiffSection(string discoveryContext, string newSection)
    {
        const string marker = "### CHANGES FROM PREVIOUS STEP ###";
        var idx = discoveryContext.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return discoveryContext.TrimEnd() + "\n" + newSection.TrimStart('\n');
        var sectionStart = discoveryContext.LastIndexOf("\n### ", idx, StringComparison.Ordinal);
        var start = sectionStart >= 0 ? sectionStart : discoveryContext.LastIndexOf('\n', idx);
        if (start < 0) start = 0;
        var sectionEnd = discoveryContext.IndexOf("\n### ", idx + marker.Length, StringComparison.Ordinal);
        var end = sectionEnd >= 0 ? sectionEnd : discoveryContext.Length;
        var prefix = discoveryContext[..start].TrimEnd();
        var suffix = discoveryContext[end..];
        return prefix + "\n" + newSection.TrimStart('\n') + "\n" + suffix.TrimStart('\n');
    }

    /// <summary>
    /// LLM compaction for the accumulated per-step diffs. Preserves file paths, symbols and the
    /// essence of each change so later steps can still build on them without the raw text.
    /// </summary>
    private async Task<string?> SummarizeDiffContextAsync(string accumulatedDiffs, bool emitSse, CancellationToken ct)
    {
        var system =
            "You are a context-compaction engine for a coding agent. Below is a raw accumulation of " +
            "code diffs from previous steps of the same task. Rewrite them as ONE compact, high-signal " +
            "summary that preserves: every file path touched, every symbol/method/class added or changed, " +
            "and the essence of each change — enough for the next step to build on them without the raw " +
            "diff text. Keep it under ~1800 chars. Output ONLY the summary text; no markdown fences, no JSON.";
        var user = $"### ACCUMULATED DIFFS ###\n{accumulatedDiffs}";
        var (raw, error) = await CallLlmRawText(system, user, false, ct,
            requestTimeout: _infiniteTimeout, maxTokens: 512);
        if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = raw.Trim();
        if (cleaned.Length < 20) return null;
        return cleaned.Length > 4000 ? cleaned[..4000] + "\n…[summarized]…" : cleaned;
    }

    /// <summary>
    /// LLM compaction for the accumulated pre-plan thinking carried between steps. Produces a
    /// dense recap (task, files, symbols added/renamed, anchors, decisions, what remains) so the
    /// next thinking round builds on a summary instead of the raw wall of reasoning.
    /// </summary>
    private async Task<string?> CompactThinkingContextAsync(string accumulatedThinking, bool emitSse, CancellationToken ct)
    {
        var system =
            "You are the memory-compaction engine of a multi-step coding agent. Below is the accumulated " +
            "PREVIOUS REASONING from earlier planning steps of the SAME task. Rewrite it as ONE dense " +
            "recap that preserves everything the NEXT thinking round must know: the task, every file " +
            "touched, every symbol/method/class added or renamed, anchors used, decisions made, and what " +
            "remains to do. Keep it under ~1800 chars. Output ONLY the recap prose; no markdown fences, no JSON.";
        var user = $"### ACCUMULATED PREVIOUS REASONING (compact this) ###\n{accumulatedThinking}";
        var (raw, error) = await CallLlmRawText(system, user, false, ct,
            requestTimeout: _infiniteTimeout, maxTokens: 512);
        if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = raw.Trim();
        if (cleaned.Length < 20) return null;
        return cleaned.Length > 4000 ? cleaned[..4000] + "\n…[compacted]…" : cleaned;
    }

    private async Task<IncrementalSubPlanProposal?> ProposeNextSubPlanAsync(
        string originalPrompt, string discoveryContext, List<MetaPlanSubPlan> subPlansSoFar,
        List<string> rejectionFeedback, bool emitSse, CancellationToken ct)
    {
        var sys = BuildIncrementalSubPlanSystemPrompt();
        var user = BuildIncrementalSubPlanUserPrompt(originalPrompt, discoveryContext, subPlansSoFar, rejectionFeedback);
        var (raw, _, err) = await CallLlmRawStreaming(sys, user, emitSse, ct, _infiniteTimeout, maxTokens: 500);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var cleaned = ExtractFirstJsonObject(raw);
            using var doc = JsonDocument.Parse(cleaned, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;
            var complete = root.TryGetProperty("metaPlanComplete", out var mc) && mc.ValueKind == JsonValueKind.True;
            var reason = root.TryGetProperty("completionReason", out var cr) ? cr.GetString() : null;
            var thinking = root.TryGetProperty("thinking", out var th) ? th.GetString() : null;
            if (complete)
                return new IncrementalSubPlanProposal { MetaPlanComplete = true, CompletionReason = reason, Thinking = thinking };
            if (!root.TryGetProperty("subPlan", out var spEl) || spEl.ValueKind != JsonValueKind.Object)
                return new IncrementalSubPlanProposal { MetaPlanComplete = false, Thinking = thinking };
            var title = spEl.TryGetProperty("title", out var tEl) ? tEl.GetString() : null;
            var desc = spEl.TryGetProperty("description", out var dEl) ? dEl.GetString() : null;
            var note = spEl.TryGetProperty("contextNote", out var nEl) ? nEl.GetString() : "";
            var files = new List<string>();
            if (spEl.TryGetProperty("files", out var fArr) && fArr.ValueKind == JsonValueKind.Array)
                foreach (var f in fArr.EnumerateArray())
                    if (f.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(f.GetString()))
                        files.Add(f.GetString()!);
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(desc))
                return new IncrementalSubPlanProposal { MetaPlanComplete = false, Thinking = thinking };
            return new IncrementalSubPlanProposal
            {
                MetaPlanComplete = false,
                Thinking = thinking,
                SubPlan = new MetaPlanSubPlan
                {
                    Id = $"sp-{subPlansSoFar.Count + 1}",
                    Title = title!,
                    Description = desc!,
                    ContextNote = note ?? "",
                    Files = files
                }
            };
        }
        catch { return null; }
    }
    private async Task<(bool valid, string? reason)> ValidateSubPlanAsync(
        MetaPlanSubPlan subPlan, string originalPrompt, List<MetaPlanSubPlan> subPlansSoFar, CancellationToken ct)
    {
        foreach (var existing in subPlansSoFar)
        {
            var sim = CalculateChangeSimilarity(NormalizeChangeForDedup(subPlan.Description), NormalizeChangeForDedup(existing.Description));
            if (sim >= 0.82)
                return (false, $"Duplicates already-committed stage '{existing.Title}'.");
        }
        var sb = new StringBuilder();
        sb.AppendLine("### ORIGINAL TASK ###"); sb.AppendLine(originalPrompt);
        sb.AppendLine();
        sb.AppendLine("### STAGES SO FAR ###");
        if (subPlansSoFar.Count == 0) sb.AppendLine("(none)");
        else foreach (var s in subPlansSoFar) sb.AppendLine($"  - {s.Title}: {s.Description}");
        sb.AppendLine();
        sb.AppendLine("### PROPOSED NEXT STAGE ###");
        sb.AppendLine($"{subPlan.Title}: {subPlan.Description}");
        sb.AppendLine();
        sb.AppendLine("Judge ONLY the proposed next stage. Is it atomic (not two concerns split apart, e.g. table " +
                      "creation split from its endpoint)? Does it depend on something not yet staged (e.g. an endpoint " +
                      "before its DTO)? Is it genuinely required by the task (not scope creep)?");
        sb.AppendLine("Output ONLY JSON: {\"valid\": true|false, \"reason\": \"short reason if invalid\"}");
        var (raw, _, _) = await CallLlmRaw(
            "You are a strict meta-plan coherence validator. Output ONLY the requested JSON.",
            sb.ToString(), ct, _infiniteTimeout, maxTokens: 200);
        if (string.IsNullOrWhiteSpace(raw)) return (true, null);
        try
        {
            var cleaned = ExtractFirstJsonObject(raw);
            using var doc = JsonDocument.Parse(cleaned);
            var valid = !doc.RootElement.TryGetProperty("valid", out var v) || v.ValueKind != JsonValueKind.False;
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
            return (valid, valid ? null : reason);
        }
        catch { return (true, null); }
    }
    private async Task<MetaPlanResult?> RunIncrementalMetaPlanLoop(
        string prompt, string discoveryContext, string projectRoot, bool emitSse, CancellationToken ct,
        string? cardId = null)
    {
        var (skipMetaPlan, gateScore) = DeterministicMetaPlanGate(prompt);
        if (skipMetaPlan)
        {
            await EmitLog(emitSse, "info", "Meta-plan: deprecated — disabled permanently.", ct: ct);
            return null;
        }
        var subPlansSoFar = new List<MetaPlanSubPlan>();
        var rejectionFeedback = new List<string>();
        var attempts = 0;
        for (var turn = 0; turn < MAX_INCREMENTAL_SUBPLANS; turn++)
        {
            ct.ThrowIfCancellationRequested();
            var proposal = await ProposeNextSubPlanAsync(prompt, discoveryContext, subPlansSoFar, rejectionFeedback, emitSse, ct);
            if (proposal == null)
            {
                if (++attempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                continue;
            }
            if (proposal.MetaPlanComplete)
            {
                await EmitLog(emitSse, "success", $"Meta-plan: complete after {subPlansSoFar.Count} stage(s) — {proposal.CompletionReason}", ct: ct);
                break;
            }
            if (proposal.SubPlan == null)
            {
                await EmitRejectedLog(emitSse, "Meta-plan: rejected — response contained no subPlan; retrying", "Response contained no subPlan object — must return planComplete or subPlan.", ct);
                if (++attempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                continue;
            }
            var (valid, reason) = await ValidateSubPlanAsync(proposal.SubPlan, prompt, subPlansSoFar, ct);
            if (!valid)
            {
                await EmitRejectedLog(emitSse, $"Meta-plan: rejected stage '{proposal.SubPlan.Title}' — {reason}", reason ?? "", ct);
                rejectionFeedback.Add($"REJECTED — '{proposal.SubPlan.Title}' → {reason}");
                if (++attempts >= MAX_STEP_REGEN_ATTEMPTS) { rejectionFeedback.Clear(); attempts = 0; }
                continue;
            }
            subPlansSoFar.Add(proposal.SubPlan);
            rejectionFeedback.Clear();
            attempts = 0;
            await EmitLog(emitSse, "info", $"Meta-plan: committed stage {subPlansSoFar.Count} — {proposal.SubPlan.Title}", ct: ct);
        }
        if (subPlansSoFar.Count <= 1)
            return null;
        var result = new MetaPlanResult
        {
            MetaThinking = "Built incrementally, one validated stage at a time.",
            MetaSummary = $"{subPlansSoFar.Count}-stage plan built incrementally",
            Complexity = Math.Min(10, 6 + subPlansSoFar.Count),
            SubPlans = subPlansSoFar
        };
        if (emitSse)
            await SendSse(Response, "meta-plan", new
            {
                summary = result.MetaSummary,
                complexity = result.Complexity,
                subPlans = result.SubPlans.Select(sp => new { id = sp.Id, title = sp.Title, description = sp.Description, files = sp.Files, contextNote = sp.ContextNote, done = false })
            }, ct);
        if (!string.IsNullOrWhiteSpace(cardId))
            await PersistMetaPlanToCardAsync(cardId, result, emitSse, ct);
        return result;
    }
    /// <summary>
    /// Persists the "verification still has work to do" state onto a card as _verifyPending
    /// ({reason, issues[], updatedAt}). Calling with a null/empty reason and no issues REMOVES
    /// the property (task verified complete). The resume path reads this so a restart re-plans
    /// the unmet requirements instead of declaring the plan complete just because every
    /// persisted plan step is already marked done.
    /// </summary>
    private async Task PersistCardVerifyStateAsync(string? cardId, string? reason, List<string>? issues, bool emitSse, CancellationToken ct)
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
                    var hasContent = !string.IsNullOrWhiteSpace(reason) || (issues != null && issues.Count > 0);
                    if (hasContent)
                    {
                        cardObj["_verifyPending"] = new JsonObject
                        {
                            ["reason"] = reason ?? "",
                            ["issues"] = new JsonArray((issues ?? new List<string>()).Select(i => JsonValue.Create(i)).ToArray()),
                            ["updatedAt"] = DateTime.UtcNow.ToString("o")
                        };
                    }
                    else
                    {
                        cardObj.Remove("_verifyPending");
                    }
                    var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await _boardData.SaveRawAsync(saved);
                    if (emitSse)
                        await SendSse(Response, "refresh", new { target = "boarddata", reason = "verify-state-updated", cardId }, ct);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to persist card verify state", new { cardId, error = ex.Message });
        }
    }

    /// <summary>Reads the card's _verifyPending blob (unmet requirements from the last
    /// verification). Returns (reason, issues) — both empty when absent.</summary>
    private async Task<(string? reason, List<string> issues)> LoadCardVerifyStateAsync(string? cardId)
    {
        if (string.IsNullOrWhiteSpace(cardId))
            return (null, new List<string>());
        try
        {
            var raw = await _boardData.LoadRawAsync();
            if (string.IsNullOrWhiteSpace(raw)) return (null, new List<string>());
            using var jsonDoc = JsonDocument.Parse(raw);
            var root = JsonNode.Parse(jsonDoc.RootElement.GetRawText())?.AsObject();
            if (root == null) return (null, new List<string>());
            var columns = new[] { "todo", "doing", "done", "selfImproving" };
            foreach (var column in columns)
            {
                if (!root.TryGetPropertyValue(column, out var columnNode) || columnNode is not JsonArray columnItems)
                    continue;
                foreach (var item in columnItems)
                {
                    if (item is not JsonObject cardObj || cardObj["id"]?.GetValue<string>() != cardId)
                        continue;
                    if (cardObj["_verifyPending"] is not JsonObject vp)
                        return (null, new List<string>());
                    var reason = vp["reason"]?.GetValue<string>() ?? "";
                    var issues = new List<string>();
                    if (vp["issues"] is JsonArray arr)
                        foreach (var i in arr)
                            if (i?.GetValue<string>() is string s && !string.IsNullOrWhiteSpace(s))
                                issues.Add(s);
                    return (string.IsNullOrWhiteSpace(reason) ? null : reason, issues);
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to load card verify state", new { cardId, error = ex.Message });
        }
        return (null, new List<string>());
    }
    private async Task<(AgentPlan? plan, HashSet<int>? completedIndices, bool isBenchmark, List<Dictionary<string, object?>>? webResults)> LoadPlanFromBoardDataAsync(string? cardId)
    {
        if (string.IsNullOrWhiteSpace(cardId))
            return (null, null, false, null);
        try
        {
            var raw = await _boardData.LoadRawAsync();
            if (string.IsNullOrWhiteSpace(raw)) return (null, null, false, null);
            using var jsonDoc = JsonDocument.Parse(raw);
            var root = JsonNode.Parse(jsonDoc.RootElement.GetRawText())?.AsObject();
            if (root == null) return (null, null, false, null);
            var columns = new[] { "todo", "doing", "done", "selfImproving" };
            foreach (var column in columns)
            {
                if (!root.TryGetPropertyValue(column, out var columnNode) || columnNode is not JsonArray columnItems)
                    continue;
                foreach (var item in columnItems)
                {
                    if (item is not JsonObject cardObj || cardObj["id"]?.GetValue<string>() != cardId)
                        continue;
                    var isBenchmark = cardObj["_benchmark"]?.GetValue<bool>() ?? false;
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
                            Status = si["status"]?.GetValue<string>(),
                            Priority = si["priority"]?.GetValue<int>() ?? 1,
                            OldString = si["oldString"]?.GetValue<string>() ?? "",
                            NewString = si["newString"]?.GetValue<string>() ?? "",
                            LineNumber = si["line"]?.GetValue<int>() ?? 0
                        };
                        var idx = si["index"]?.GetValue<int>() ?? i;
                        steps.Add(step);
                        var done = si["done"]?.GetValue<bool>() ?? false;
                        if (done) completed.Add(idx);
                    }
                    if (steps.Count == 0) return (null, null, isBenchmark, null);
                    var plan = new AgentPlan
                    {
                        Summary = planObj["summary"]?.GetValue<string>() ?? "",
                        Plan = steps
                    };
                    // The card-level _webResults store (PersistWebResultsToCardAsync) rides
                    // back out with the plan so a replayed run can seed its discovery context
                    // with the previous run's harvested web data.
                    List<Dictionary<string, object?>>? webResults = null;
                    if (cardObj["_webResults"] is JsonArray wrArr && wrArr.Count > 0)
                    {
                        webResults = new List<Dictionary<string, object?>>();
                        foreach (var wr in wrArr.OfType<JsonObject>())
                        {
                            var type = wr["type"]?.GetValue<string>() ?? "";
                            if (type is not ("_web_search" or "_web_fetch")) continue;
                            var d = new Dictionary<string, object?>
                            {
                                ["type"] = type,
                                ["status"] = wr["status"]?.GetValue<string>() ?? "done",
                                ["output"] = wr["output"]?.GetValue<string>() ?? ""
                            };
                            var q = wr["query"]?.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(q)) d["query"] = q;
                            var u = wr["url"]?.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(u)) d["url"] = u;
                            webResults.Add(d);
                        }
                        if (webResults.Count == 0) webResults = null;
                    }
                    return (plan, completed.Count > 0 ? completed : null, isBenchmark, webResults);
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to load plan from board data", new { cardId, error = ex.Message });
        }
        return (null, null, false, null);
    }
    private enum DuplicateStepDecision
    {
        Duplicate,
        Distinct,
        Replacement
    }

    private sealed class DuplicateStepAdjudication
    {
        public DuplicateStepDecision Decision { get; }
        public PlanStep? Step { get; }

        public static DuplicateStepAdjudication Duplicate { get; } = new(DuplicateStepDecision.Duplicate, null);
        public static DuplicateStepAdjudication Distinct { get; } = new(DuplicateStepDecision.Distinct, null);

        public DuplicateStepAdjudication(DuplicateStepDecision decision, PlanStep? step)
        {
            Decision = decision;
            Step = step;
        }
    }

    private sealed class IncrementalStepProposal
    {
        public bool PlanComplete { get; set; }
        public string? CompletionReason { get; set; }
        public string? Thinking { get; set; }
        public string? ExploreFile { get; set; }
        public PlanStep? Step { get; set; }
        public List<PlanStep>? AdditionalSteps { get; set; }
    }
    private sealed class IncrementalSubPlanProposal
    {
        public bool MetaPlanComplete { get; set; }
        public string? CompletionReason { get; set; }
        public string? Thinking { get; set; }
        public MetaPlanSubPlan? SubPlan { get; set; }
    }
}
