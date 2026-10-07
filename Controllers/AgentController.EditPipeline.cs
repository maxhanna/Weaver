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
using static Weaver.Services.AgentPlanningPolicy;
using Weaver;
namespace Weaver.Controllers;

partial class AgentController
{
    // ── Phase: context-prep (exploration, intent, strategy, enrichment, preservation) ──
    // Verbatim from ResolveAndApplyEdit lines 64-136. No control-flow changes.
    private async Task<(PlanStep Step, string ExplorationContext, string? PreservationDirective,
        EditPlanDecision? DecidedStrategy, string? TargetSymbol)> PrepareEditContextAsync(
        PlanStep step, string projectRoot, bool emitSse, CancellationToken ct,
        string? prompt, AgentPlan? plan, int planItemIndex, string? cardId,
        List<string>? attachedFiles, bool skipLlmPreResolution, string relPath, string fullPath)
    {
        var fe = System.IO.File.Exists(fullPath);
        var fc = fe ? await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct) : "";

        // ── Deterministic content generation — fully-resolved edit, ZERO LLM calls ──
        // Synthesizes oldStr → newStr pairs for mechanically-describable changes
        // (literal swaps, property/field additions, getter/setter pairs) BEFORE any
        // exploration, intent classification or strategy resolution runs.
        //
        // HOWEVER: if the planner already provided a good anchor (oldString exists
        // verbatim in the file), skip the generator — the planner's anchor is
        // location-aware (e.g. near sibling properties) while the generator always
        // anchors at the end of the class body, which inserts members in the wrong spot.
        var planAlreadyHasGoodAnchor = !string.IsNullOrWhiteSpace(step.OldString)
            && !string.IsNullOrWhiteSpace(step.NewString)
            && fe && fc.Contains(step.OldString!);
        if (planAlreadyHasGoodAnchor)
        {
            await EmitLog(emitSse, "info",
                $"📌 Plan-provided anchor found verbatim in file — skipping deterministic generator to preserve planned location", ct: ct);
        }
        if (!skipLlmPreResolution && fe && !planAlreadyHasGoodAnchor)
        {
            var det = DeterministicEditGenerator.TryGenerate(relPath, fe, fc, step.Change ?? "");
            if (det != null)
            {
                step.OldString = det.OldStr;
                step.NewString = det.NewStr;
                if (det.LineNumber > 0) step.LineNumber = det.LineNumber;
                if (det.Edits is { Count: > 0 })
                {
                    // Multi-match: one anchored edit per occurrence — the batch apply path
                    // composes newContent from step.Edits; OldString/NewString are set so the
                    // plan-provided path is taken (no LLM resolve call) before the batch runs.
                    step.Edits = det.Edits;
                }
                var detDecision = new EditPlanDecision(det.Strategy, det.TargetType, det.TargetName,
                    det.OldStr, det.Reason, det.NewStr, det.Edits);
                await EmitLog(emitSse, "success",
                    $"⚙️ Deterministic edit synthesized — no LLM round-trip: {det.Reason}",
                    detDecision, ct: ct);
                return (step, "", null, detDecision, det.TargetName);
            }
        }

        var exploration = skipLlmPreResolution
            ? new StepExplorationResult
            {
                EnrichedStep = step,
                ExplorationContext = "",
                FilesRead = new List<string>(),
                TargetSymbol = step.TargetSymbol,
                Confidence = 100,
                RoundsCompleted = 0
            }
            : await RunStepExplorationLoop(
                step, projectRoot,
                prompt ?? step.Change ?? "",
                plan, planItemIndex, emitSse, ct, cardId, attachedFiles);
        step = exploration.EnrichedStep;
        var explorationContext = exploration.ExplorationContext;
        var eiTask = skipLlmPreResolution
            ? null
            : EditIntentClassifier.ClassifyAsync(step.Change ?? "", relPath,
                async (sys, usr, c) =>
                {
                    var (raw, _, err) = await CallLlmRaw(sys, usr, c, _infiniteTimeout, maxTokens: 128);
                    return (raw, err);
                }, ct);
        var ei = eiTask != null ? await eiTask : new EditIntent(EditIntentKind.TargetedEdit, null, null);
        var decidedEditStrategy = skipLlmPreResolution
            ? null
            : EditStrategyResolver.Decide(relPath, fe, fc, step.Change ?? "", ei);
        if (!skipLlmPreResolution && decidedEditStrategy!.ResolvedOldStr != null)
        {
            await EmitLog(emitSse, "info",
                $"  🎯 AST-resolved '{decidedEditStrategy.TargetName}' ({decidedEditStrategy.ResolvedOldStr.Split('\n').Length}L) via EditStrategyResolver", decidedEditStrategy, ct: ct);
            if (decidedEditStrategy.Strategy == EditStrategy.ReplaceMethod)
                step.OldString = decidedEditStrategy.ResolvedOldStr;
            // Fully-resolved deterministic edit (e.g. from Decide's generator hook):
            // both strings are server-authored — the apply loop needs zero LLM calls.
            if (decidedEditStrategy.ResolvedNewStr != null)
            {
                step.OldString = decidedEditStrategy.ResolvedOldStr;
                step.NewString = decidedEditStrategy.ResolvedNewStr;
                if (decidedEditStrategy.ResolvedEdits is { Count: > 0 })
                    step.Edits = decidedEditStrategy.ResolvedEdits;
                // Include the generator's reason — for multi-match batches it reports
                // applied N/M occurrences + skipped counts, so partial batches are visible.
                await EmitLog(emitSse, "info",
                    $"  ⚙️ Deterministic edit: old/new both server-resolved (no LLM authoring): {decidedEditStrategy.Reason}", ct: ct);
            }
            explorationContext = $"### TARGET FILE: {relPath}\n\n```\n{decidedEditStrategy.ResolvedOldStr}\n```" +
                (!string.IsNullOrWhiteSpace(explorationContext) ? "\n\n" + explorationContext : "");
        }
        if (!skipLlmPreResolution && !string.IsNullOrWhiteSpace(explorationContext) && !string.IsNullOrWhiteSpace(step.Change))
        {
            explorationContext = await EnrichContextWithProjectTypesAndSql(
                projectRoot, relPath, step.Change, explorationContext,
                new HashSet<string>(exploration.FilesRead, StringComparer.OrdinalIgnoreCase),
                emitSse, ct, targetSymbol: exploration.TargetSymbol);
            var typeChainContext = await EnrichWithTypeChain(
                projectRoot, relPath, step.Change,
                new HashSet<string>(exploration.FilesRead, StringComparer.OrdinalIgnoreCase),
                emitSse, ct);
            if (!string.IsNullOrWhiteSpace(typeChainContext))
            {
                explorationContext += typeChainContext;
            }
        }
        if (exploration.LowConfidenceWarning != null)
        {
            await EmitLog(emitSse, "warn", $"  ⚠ {exploration.LowConfidenceWarning}", ct: ct);
            await SendSse(Response, "step", new
            {
                index = planItemIndex,
                type = "edit",
                status = "low-confidence",
                path = relPath,
                warning = exploration.LowConfidenceWarning,
                planItemIndex
            }, ct);
        }
        string? preservationDirective = null;
        if (!skipLlmPreResolution && !string.IsNullOrWhiteSpace(exploration.TargetSymbol))
        {
            preservationDirective = await AnalyzePreservationAndDependenciesAsync(
                step, projectRoot, relPath, exploration.TargetSymbol, explorationContext, emitSse, ct);
        }
        return (step, explorationContext, preservationDirective, decidedEditStrategy, exploration.TargetSymbol);
    }
    // ── Phase: AST oldString extraction + causal context ───────────────────
    // Verbatim from ResolveAndApplyEdit lines 154-210. Mutates step.LineNumber/OldString.
    private async Task<(string? PlanOldStr, string? CausalContext)> ResolveAstOldStringAndCausalAsync(
        PlanStep step, string? planOldStr, string? targetSymbol, string relPath, string fullPath,
        string fileExt, string? prompt, string projectRoot, bool skipLlmPreResolution,
        bool emitSse, CancellationToken ct)
    {
        if (System.IO.File.Exists(fullPath))
        {
            var preExtractContent = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
            if (string.IsNullOrWhiteSpace(planOldStr) &&
                AstCodeEditorService.IsSupportedExtension(fileExt) &&
                fileExt is not ".css" and not ".scss" and not ".less")
            {
                if (!string.IsNullOrWhiteSpace(targetSymbol))
                {
                    string logMsg1 = @$"AST is resolving '{targetSymbol}' in {relPath} for exact method source extraction. 
                    Detected file extension: {fileExt}.";
                    await EmitLog(emitSse, "info", logMsg1, ct: ct);
                    var (astOldStr, astStartLine, astErr) = AstCodeEditorService.FindFunctionSource(
                        preExtractContent, targetSymbol, fileExt, step.Change);
                    if (astOldStr != null && astStartLine > 0)
                    {
                        // Dotted `Class.method` symbols resolve to the method only — the class
                        // name isn't part of the method source, so compare the last segment.
                        var symbolMatch = targetSymbol.Contains('.')
                            ? astOldStr.Contains(targetSymbol[(targetSymbol.LastIndexOf('.') + 1)..], StringComparison.Ordinal)
                            : astOldStr.Contains(targetSymbol, StringComparison.Ordinal);
                        if (!symbolMatch)
                        {
                            await EmitLog(emitSse, "warn",
                                $"AST-resolved '{targetSymbol}' but body does not contain symbol name — rejecting to avoid wrong-context edit",
                                ct: ct);
                        }
                        else
                        {
                            step.LineNumber = astStartLine;
                            step.OldString = astOldStr;
                            planOldStr = astOldStr;
                            await EmitLog(emitSse, "info",
                                $"AST-resolved '{targetSymbol}' at line {astStartLine} — using exact method source as oldString", ct: ct);
                        }
                    }
                    else
                    {
                        await EmitLog(emitSse, "warn",
                            $"AST could not find '{targetSymbol}': {astErr}", ct: ct);
                    }
                }
                else
                {
                    await EmitLog(emitSse, "info",
                        $"No target symbol available for {relPath} — resolver will use full file context", ct: ct);
                }
            }
        }
        // HTML: the plan-provided oldString/newString is a concrete edit — TRY it first through the
        // tolerant apply matchers. FORMAT D stays as the automatic fallback when the plan edit's
        // oldString can't be matched (the LLM resolver enforces FORMAT D for HTML on retry).
        if (HtmlDomEditor.IsHtmlDomFile(relPath) && !string.IsNullOrWhiteSpace(planOldStr))
        {
            await EmitLog(emitSse, "info",
                $"HTML file {relPath}: plan provides oldString/newString — applying plan edit first; FORMAT D remains the fallback if it doesn't match", ct: ct);
        }
        string? causalContext = null;
        if (!skipLlmPreResolution && System.IO.File.Exists(fullPath))
        {
            var preExtractContent = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
            causalContext = await RunCausalReasoningAsync(prompt ?? step.Change ?? "", relPath, preExtractContent, emitSse, ct);
        }
        return (planOldStr, causalContext);
    }
    // ── Phase: LLM verification rounds (multi-round keep/abandon votes) ────
    // Verbatim from ResolveAndApplyEdit lines 1449-1523.
    private async Task<(List<string> Decisions, List<string> Reasons, List<int> Scores,
        List<bool> NeedsExtraStepFlags, bool DeterministicPlaceholderReject)> RunLlmVerifyRoundsAsync(
        string? newStr, string? oldStr, string relPath, string? prompt, string? stepChange,
        string? preEditContent, string newContent, bool emitSse, CancellationToken ct,
        List<(int attempt, int score, string reason, string failedNew)> attemptScores,
        string explorationContext, AgentPlan? plan, int planItemIndex,
        string? sqlMigrationNote, string? causalContext)
    {
        const int VerificationRounds = 3;
        var decisions = new List<string>();
        var reasons = new List<string>();
        var scores = new List<int>();
        var needsExtraStepFlags = new List<bool>();
        var deterministicPlaceholderReject = false;
        for (int r = 0; r < VerificationRounds; r++)
        {
                    if (r == 0 && !string.IsNullOrWhiteSpace(newStr) &&
                        AgentEditHeuristics.LooksLikePlaceholderStub(newStr, preExisting: oldStr))
                    {
                        deterministicPlaceholderReject = true;
                        await EmitLog(emitSse, "warn",
                            $"⛔ Deterministic placeholder-stub reject on {relPath}: new code is a placeholder stub (console.log-only body, placeholder comment, empty body, or NotImplementedException). Retrying with a directive to implement the real logic.", ct: ct);
                        decisions.Add("abandon");
                        reasons.Add("Deterministic placeholder-stub detection: new code is a placeholder stub — implement the REAL logic (mirror the pattern from the referenced component file).");
                        scores.Add(0);
                        needsExtraStepFlags.Add(false);
                        break;
                    }
                    // if (r == 0)
                    // {
                    //     var stepKeywords = Regex.Matches(step.Change ?? "", @"\b\w+\.\w+\b")
                    //         .Select(m => m.Value)
                    //         .Distinct(StringComparer.OrdinalIgnoreCase)
                    //         .ToList();
                    //     if (stepKeywords.Count > 0 && !stepKeywords.Any(k =>
                    //         newStr?.Contains(k, StringComparison.OrdinalIgnoreCase) == true))
                    //     {
                    //         if (stepKeywords.Any(k => oldStr?.Contains(k, StringComparison.OrdinalIgnoreCase) == true))
                    //         {
                    //             continue;
                    //         }
                    //         decisions.Add("abandon");
                    //         reasons.Add($"Auto-abandon: step mentions \"{string.Join(", ", stepKeywords)}\" but newStr doesn't contain any of them — edit doesn't implement the keyword");
                    //         scores.Add(0);
                    //         needsExtraStepFlags.Add(false);
                    //         await EmitLog(emitSse, "warn",
                    //             $"⛔ Deterministic reject: newStr has no match for step keywords \"{string.Join(", ", stepKeywords)}\"", ct: ct);
                    //         break;
                    //     }
                    // }
                    // Labeled per round so the panel shows each verification round's prompt +
                    // response token spend, and the step result aggregates it as llmTokens.
                    var (d, reason, score, needsEs) = await LlmVerifyEditStepAsync(
                        relPath, prompt ?? stepChange ?? "", stepChange ?? "",
                        oldStr!, newStr!, preEditContent ?? "", newContent, emitSse, ct,
                        priorAttempts: attemptScores.Count > 0
                            ? attemptScores.Select(a => (a.score, a.reason, a.failedNew)).ToList()
                            : null,
                        explorationContext: explorationContext,
                        fullPlan: plan,
                        currentStepIndex: planItemIndex,
                        causalContext: sqlMigrationNote == null
                            ? causalContext
                            : (causalContext ?? "") + "\n\n" + sqlMigrationNote,
                        llmRoundLabel: $"verify step {planItemIndex + 1} round {r + 1}/{VerificationRounds}");
                    decisions.Add(d);
                    reasons.Add(reason);
                    scores.Add(score);
                    needsExtraStepFlags.Add(needsEs);
                    if (r >= 1)
                    {
                        var keep2 = decisions.Take(r + 1).Count(x => x == "keep");
                        var es2 = needsExtraStepFlags.Take(r + 1).Count(f => f);
                        if (keep2 >= 2 && es2 == 0) break;
                        if (es2 >= 2) break;
                    }
                    else if (r == 0 && d == "keep" && score >= 85 && !needsEs)
                    {
                        decisions.Add("keep");
                        decisions.Add("keep");
                        scores.Add(score);
                        scores.Add(score);
                        needsExtraStepFlags.Add(false);
                        needsExtraStepFlags.Add(false);
                        break;
                    }
        }
        return (decisions, reasons, scores, needsExtraStepFlags, deterministicPlaceholderReject);
    }
    /// <summary>First line of a snippet, trimmed and capped, for compact per-edit display.</summary>
    private static string OneLinePreview(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var first = s.Trim();
        var nl = first.IndexOf('\n');
        if (nl >= 0) first = first[..nl].TrimEnd();
        return first.Length > 80 ? first[..80] + "…" : first;
    }

    // ── Phase: successful-edit completion (record outcome, wire new methods, emit result) ──
    // Verbatim from ResolveAndApplyEdit lines 1785-1853. Returns the next step index.
    private async Task<int> CompleteSuccessfulEditAsync(
        int attempt, List<(string old, string @new, string error)> history,
        string? oldStr, string? newStr, PlanStep step, string? prompt, string projectRoot,
        string relPath, string fullPath, AgentPlan? plan, int planItemIndex,
        bool stepNeedsExtraStep, string? stepExtraStepReason, string? stepExtraStepFile,
        bool emitSse, CancellationToken ct, List<object> allResults, int stepIndex,
        string? cardId, string fileExt, string? beforeContent = null, string? afterContent = null)
    {
        var successReason = "";
            if (attempt > 0 && history.Count > 0)
            {
                var lastFailure = history[history.Count - 1];
                var failSummary = lastFailure.error;
                if (failSummary.Length > 200) failSummary = failSummary[..200] + "…";
                successReason = $"Succeeded on attempt {attempt + 1} after {history.Count} failure(s). " +
                                $"Last failure: {failSummary}. " +
                                $"Strategy that worked: {(attempt == 1 ? "VERBATIM_COPY" : attempt == 2 ? "SINGLE_LINE_ANCHOR" : "LINE_RANGE_REPLACEMENT")}.";
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await _editKnowledge.RecordOutcomeAsync(
                        projectRoot, relPath, step.Change ?? "", prompt ?? step.Change ?? "",
                        oldStr, newStr, outcome: "success", reason: successReason, ct);
                }
                catch { }
                try
                {
                    await _editKnowledge.UpdateArchitectureAsync(
                        projectRoot, relPath, newStr ?? "", ct);
                }
                catch { }
            }, CancellationToken.None);
            await EmitLog(emitSse, "success", $"✓ Edited {relPath}", ct: ct);
            var addedMethodName = ExtractNewlyAddedMethodName(step.Change, newStr);
            if (!string.IsNullOrWhiteSpace(addedMethodName) && plan?.Plan != null && planItemIndex >= 0)
            {
                var (wired, wiringReason) = await CheckNewMethodIsWiredUpAsync(addedMethodName, relPath, projectRoot, ct);
                if (!wired)
                {
                    var alreadyQueued = plan.Plan.Any(p =>
                        (p.Change ?? "").Contains(addedMethodName, StringComparison.OrdinalIgnoreCase) &&
                        Regex.IsMatch(p.Change ?? "", @"\b(wire|call|use|invoke|hook up|connect)\b", RegexOptions.IgnoreCase));
                    if (!alreadyQueued)
                    {
                        plan.Plan.Insert(planItemIndex + 1, new PlanStep
                        {
                            File = relPath,
                            Change = $"Wire up the newly added '{addedMethodName}' method — call it from wherever " +
                                     $"in this file the feature it implements is supposed to actually run. It " +
                                     $"currently has no call sites anywhere in the project.",
                            Priority = 1,
                            LineNumber = 0
                        });
                        await EmitLog(emitSse, "warn", $"⚠ {wiringReason}", ct: ct);
                    }
                }
            }
            var result = new Dictionary<string, object?>();
            PopulateEditResult(result, "modified", relPath, oldStr, newStr ?? "",
                afterContent ?? "", beforeContent);
            // Deterministic multi-match batches: surface the applied/total counts plus the
            // per-edit lines so the board step card can render "5/5 occurrences updated"
            // with each sub-edit expanded — instead of the confusing first-edit diff.
            if (newStr != null && newStr.StartsWith("(deterministic batch:", StringComparison.Ordinal) &&
                step.Edits != null && step.Edits.Count > 0)
            {
                var batchMarker = Regex.Match(newStr,
                    @"^\(deterministic batch: \d+ edits, applied (\d+)/(\d+) ([a-z]+)\)$");
                if (batchMarker.Success)
                {
                    result["batchApplied"] = int.Parse(batchMarker.Groups[1].Value);
                    result["batchTotal"] = int.Parse(batchMarker.Groups[2].Value);
                    result["batchUnit"] = batchMarker.Groups[3].Value;
                    result["batchEdits"] = step.Edits.Select(e => (object)new Dictionary<string, object?>
                    {
                        ["line"] = e.LineNumber,
                        ["old"] = OneLinePreview(e.OldString),
                        ["to"] = OneLinePreview(e.NewString)
                    }).ToList();
                }
            }
            result["index"] = stepIndex; result["planItemIndex"] = planItemIndex;
            result["needsExtraStep"] = stepNeedsExtraStep;
            result["extraStepReason"] = stepExtraStepReason;
            result["extraStepFile"] = stepExtraStepFile;
            var stepDiffs = await CollectRecentDiffPathsAsync(relPath, projectRoot, ct);
            result["diffs"] = stepDiffs;
            if (emitSse) await SendSse(Response, "step", result, ct);
            allResults.Add(result);
            await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct, stepDiffs, projectRoot: projectRoot);
            if (fileExt == ".cs" && !string.IsNullOrWhiteSpace(oldStr) && !string.IsNullOrWhiteSpace(newStr))
            {
                stepIndex = await HandleMethodSignatureChange(
                    fullPath, relPath, oldStr, newStr, projectRoot,
                    emitSse, ct, stepIndex, allResults, cardId);
            }
            return stepIndex + 1;
    }
    // ── Phase: failure handling + replanning ───────────────────────────────
    // Verbatim from ResolveAndApplyEdit lines 1856-2015. Always exits via return or throw.
    private async Task<int> HandleStepFailureAsync(
        List<(string old, string @new, string error)> history,
        List<(int attempt, int score, string reason, string failedNew)> attemptScores,
        int bestScore, int bestAttempt, string relPath, PlanStep step,
        int stepIndex, int planItemIndex, string? cardId, List<object> allResults,
        bool emitSse, CancellationToken ct, int replanDepth, AgentPlan? plan,
        string? prompt, List<string>? attachedFiles, string projectRoot,
        Func<string, Task>? onActivity)
    {
        var lastErr = history.Count > 0 ? history[^1].error : "resolve failed";
        var failureSummary = new StringBuilder();
        failureSummary.AppendLine($"Step failed after {history.Count} attempts on {relPath}");
        failureSummary.AppendLine($"Step description: {step.Change}");
        failureSummary.AppendLine($"Final error: {lastErr}");
        if (attemptScores.Count > 0)
        {
            failureSummary.AppendLine($"\nAttempt score history:");
            foreach (var a in attemptScores)
            {
                failureSummary.AppendLine($"  Attempt {a.attempt}: score={a.score}/100 — {a.reason}");
            }
            failureSummary.AppendLine($"Best score achieved: {bestScore}/100 on attempt {bestAttempt + 1}");
        }
        failureSummary.AppendLine($"\nFailed code snippets (reverted — do NOT reproduce):");
        foreach (var a in attemptScores.TakeLast(3))
        {
            failureSummary.AppendLine($"--- Attempt {a.attempt} (score {a.score}/100): {a.reason} ---");
            failureSummary.AppendLine("```");
            failureSummary.AppendLine(TruncateForLlm(a.failedNew, 500));
            failureSummary.AppendLine("```");
        }
        var failureContext = failureSummary.ToString();
        await EmitLog(emitSse, "warn",
            $"Step failure summary for replanning:\n{failureContext}", ct: ct);
        _ = Task.Run(async () =>
        {
            try
            {
                await _editKnowledge.RecordOutcomeAsync(
                    projectRoot, step.File, step.Change ?? "", prompt ?? step.Change ?? "",
                    step.OldString, step.NewString,
                    outcome: "failure", reason: $"{lastErr}\n\n{failureContext}", ct);
            }
            catch { }
        }, CancellationToken.None);
        if (replanDepth > 0)
        {
            await EmitLog(emitSse, "error",
                $"✗ FATAL: Replan step failed (depth {replanDepth}) — aborting {relPath}: {lastErr}",
                new { failureContext, attemptScores }, ct: ct);
            var failDepth = new Dictionary<string, object?>
            {
                ["index"] = stepIndex,
                ["type"] = "edit",
                ["status"] = "error",
                ["path"] = relPath,
                ["error"] = lastErr,
                ["planItemIndex"] = planItemIndex,
                ["failureContext"] = failureContext,
                ["attemptScores"] = attemptScores.Select(a => new { a.attempt, a.score, a.reason }).ToList(),
                ["bestScore"] = bestScore,
                ["replanAttempts"] = 0
            };
            if (emitSse) await SendSse(Response, "step", failDepth, ct);
            allResults.Add(failDepth);
            await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct, projectRoot: projectRoot);
            throw new StepFatalException(
                $"Replan step failed after {history.Count} attempts: {relPath} — {lastErr}",
                relPath,
                step.Change ?? "",
                failureContext);
        }
        var replanAttempts = 0;
        const int MaxReplanAttempts = 2;
        while (replanAttempts < MaxReplanAttempts)
        {
            replanAttempts++;
            await EmitLog(emitSse, "info",
                $"🔄 Replanning cycle {replanAttempts}/{MaxReplanAttempts} for {relPath} — " +
                $"feeding failure context back to planner…", ct: ct);
            var replanSteering =
                $"PREVIOUS APPROACH FAILED after {attemptScores.Count} attempts. " +
                $"Best score: {bestScore}/100.\n\n" +
                $"FAILURE CONTEXT:\n{failureContext}\n\n" +
                $"You MUST take a FUNDAMENTALLY DIFFERENT approach. " +
                $"The code snippets above were tried and rejected — do NOT reproduce them. " +
                $"Consider:\n" +
                $"  - Using a smaller, more targeted edit (1-3 lines instead of a full method rewrite)\n" +
                $"  - Using oldString/newString instead of FORMAT C (or vice versa)\n" +
                $"  - Editing a different part of the file that achieves the same goal\n" +
                $"  - Breaking the change into a simpler, smaller edit\n" +
                $"Score your new plan 85+ only if it addresses the specific failure reasons above.";
            var replanSteps = await GenerateReplanStepsAsync(
                prompt ?? step.Change ?? "", allResults, plan,
                replanSteering, projectRoot, emitSse, ct,
                attachedFiles: attachedFiles,
                qualityCheckReason: failureContext);
            if (replanSteps == null || replanSteps.Count == 0)
            {
                await EmitLog(emitSse, "warn", $"Replan cycle {replanAttempts} returned no steps", ct: ct);
                continue;
            }
            await EmitLog(emitSse, "info", $"Replan cycle {replanAttempts} generated {replanSteps.Count} new step(s): " +
                string.Join(" | ", replanSteps.Select(s => s.Change)), ct: ct);
            replanSteps = await PruneIrrelevantPlanStepsAsync(replanSteps, projectRoot, ct);
            // File-truth guard: a replan step is only "too similar to the failed step" when
            // its named target ACTUALLY exists in the file — a description that promised a
            // method but only partially landed (e.g. deterministic member synthesis added the
            // property, not the method) is the remaining work, not a repetitive re-proposal.
            var isRepetitive = false;
            foreach (var s in replanSteps)
            {
                if (s.File == relPath && s.Change != null &&
                    TokenOverlap(s.Change, step.Change ?? "") > 0.5 &&
                    await StepTargetExistsInFileAsync(s, projectRoot, ct))
                {
                    isRepetitive = true;
                    break;
                }
            }
            if (isRepetitive)
            {
                await EmitLog(emitSse, "warn",
                    $"Replan cycle {replanAttempts}: new step is too similar to the failed step — stopping",
                    ct: ct);
                break;
            }
            var replanResults = new List<object>();
            // Carry executed web results into the replan: ResolveAndApplyEdit harvests web
            // results from the allResults arg it receives, which here is the fresh
            // replanResults list. Without seeding, a re-resolved edit would lose the real
            // titles/URLs and invent them. The seeded dicts are already in the outer
            // allResults, so they are skipped on merge (reference equality) and excluded
            // from the step-index count.
            var seededWebCount = 0;
            foreach (var prior in allResults.OfType<Dictionary<string, object?>>())
            {
                if (IsWebStep(prior.GetValueOrDefault("type")?.ToString()) &&
                    prior.GetValueOrDefault("status")?.ToString() == "done")
                {
                    replanResults.Add(prior);
                    seededWebCount++;
                }
            }
            foreach (var replanStep in replanSteps)
            {
                var replanStepIndex = stepIndex;
                try
                {
                    replanStepIndex = await ResolveAndApplyEdit(
                        replanStep, projectRoot, emitSse, ct,
                        replanResults, replanStepIndex,
                        prompt, plan, planItemIndex, cardId, attachedFiles,
                        replanDepth + 1, onActivity);
                }
                catch (StepFatalException)
                { }
            }
            var hasSuccess = replanResults.OfType<Dictionary<string, object?>>()
                .Any(r => r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created");
            if (hasSuccess)
            {
                await EmitLog(emitSse, "success",
                    $"✓ Replan cycle {replanAttempts} succeeded for {relPath}", ct: ct);
                allResults.AddRange(replanResults.Skip(seededWebCount));
                await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct, projectRoot: projectRoot);
                return stepIndex + replanResults.Count - seededWebCount;
            }
            failureContext = $"Replan attempt {replanAttempts} also failed.\n" + failureContext;
            allResults.AddRange(replanResults.Skip(seededWebCount));
        }
        await EmitLog(emitSse, "error",
            $"✗ FATAL: All resolve attempts AND {MaxReplanAttempts} replan cycles failed for {relPath}: {lastErr}",
            new { failureContext, attemptScores }, ct: ct);
        var diffFiles = await CollectRecentDiffPathsAsync(relPath, projectRoot, ct);
        var fail = new Dictionary<string, object?>
        {
            ["index"] = stepIndex,
            ["type"] = "edit",
            ["status"] = "error",
            ["path"] = relPath,
            ["error"] = lastErr,
            ["planItemIndex"] = planItemIndex,
            ["failureContext"] = failureContext,
            ["attemptScores"] = attemptScores.Select(a => new { a.attempt, a.score, a.reason }).ToList(),
            ["bestScore"] = bestScore,
            ["replanAttempts"] = MaxReplanAttempts,
            ["diffs"] = diffFiles
        };
        if (emitSse) await SendSse(Response, "step", fail, ct);
        allResults.Add(fail);
        await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct, projectRoot: projectRoot);
        throw new StepFatalException(
            $"Step failed after {history.Count} attempts and {MaxReplanAttempts} replan cycles: {relPath} — {lastErr}",
            relPath,
            step.Change ?? "",
            failureContext);
    }
}
