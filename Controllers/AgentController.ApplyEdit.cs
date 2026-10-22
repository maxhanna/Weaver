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
using Weaver;
namespace Weaver.Controllers;

partial class AgentController
{
    /// <summary>For a template (.html/.htm) edit, resolves the sibling Angular component
    /// (.component.ts) content so the hallucinated-property guard can judge bound members
    /// against the component's declared members — a template alone never contains them.
    /// Follows Angular's `x.component.html` ↔ `x.component.ts` convention (with a plain
    /// `x.html` → `x.ts` fallback); returns null when no sibling exists.</summary>
    private static string? TryReadComponentTsContent(string relPath, string projectRoot)
    {
        var ext = Path.GetExtension(relPath)?.ToLowerInvariant();
        if (ext is not (".html" or ".htm")) return null;
        var candidates = new List<string>
        {
            Path.ChangeExtension(relPath, ".ts")
        };
        // Belt-and-suspenders: also try the explicit .component.ts name in case the template
        // name is odd (e.g. index.html for a component).
        if (relPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            candidates.Add(relPath[..^5] + ".component.ts");
        else if (relPath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            candidates.Add(relPath[..^4] + ".component.ts");
        foreach (var cand in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var full = Path.GetFullPath(Path.Combine(projectRoot, cand.Replace('/', Path.DirectorySeparatorChar)));
            try
            {
                if (System.IO.File.Exists(full))
                    return System.IO.File.ReadAllText(full);
            }
            catch { }
        }
        return null;
    }

    /// <summary>Applies a STRUCTURAL tabular edit (CSV/TSV/XLSX) via <see cref="TabularFileService"/>
    /// and writes the file. Returns the next step index when the step was handled (done or
    /// skipped), or null to fall through to the normal pipeline (only for text formats whose
    /// operation was unrecognized — binary spreadsheets are always handled here to prevent
    /// corruption).</summary>
    private async Task<int?> ApplyTabularEditAsync(
        PlanStep step, string relPath, string fullPath, string projectRoot, bool emitSse,
        CancellationToken ct, List<object> allResults, int stepIndex, int planItemIndex, string? cardId,
        AgentRunContext runContext)
    {
        var change = step.Change ?? "";
        var isBinary = TabularFileService.IsSpreadsheetBinary(relPath);
        string? reason;
        string? oldText = null, newText = null;
        byte[]? newBytes = null;
        var applied = false;

        if (isBinary)
        {
            byte[] bytes;
            try { bytes = await System.IO.File.ReadAllBytesAsync(fullPath, ct); }
            catch (Exception ex)
            {
                await EmitLog(emitSse, "warn", $"Could not read binary spreadsheet {relPath}: {ex.Message}", ct: ct);
                return null;
            }
            applied = TabularFileService.TryEditXlsx(bytes, change, out newBytes, out reason);
        }
        else
        {
            var text = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
            oldText = text;
            applied = TabularFileService.TryEditDelimited(text, TabularFileService.DelimiterFor(relPath),
                change, out newText, out reason);
        }

        if (!applied || reason == null)
        {
            if (isBinary)
            {
                // A binary spreadsheet with an unrecognized operation must NOT fall through
                // to the text pipeline (it would corrupt the ZIP). Mark it skipped with a
                // steering reason so verification can guide the agent toward a supported op.
                await EmitLog(emitSse, "warn",
                    $"Binary spreadsheet {relPath} — no recognized tabular operation for: {change}. " +
                    "Supported: add/remove/rename column, add/delete row, set cell, replace value.", ct: ct);
                var skip = new Dictionary<string, object?>
                {
                    ["index"] = stepIndex,
                    ["type"] = "edit",
                    ["status"] = "skipped",
                    ["path"] = relPath,
                    ["reason"] = "binary spreadsheet — unrecognized tabular operation",
                    ["planItemIndex"] = planItemIndex
                };
                if (emitSse) await SendSse(Response, "step", skip, ct);
                allResults.Add(skip);
                await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct, projectRoot: projectRoot);
                return stepIndex + 1;
            }
            return null; // text tabular file: fall through to the anchored text edit path
        }

        if (isBinary)
        {
            var existingBytes = await System.IO.File.ReadAllBytesAsync(fullPath, ct);
            if (newBytes != null && !newBytes.SequenceEqual(existingBytes))
                await System.IO.File.WriteAllBytesAsync(fullPath, newBytes, ct);
            await EmitLog(emitSse, "success", $"🧮 Tabular edit on {relPath}: {reason}", ct: ct);
            var r = new Dictionary<string, object?>();
            PopulateEditResult(runContext, r, "modified", relPath, null, null, "");
            r["index"] = stepIndex;
            r["planItemIndex"] = planItemIndex;
            r["reason"] = reason;
            r["tabular"] = true;
            if (emitSse) await SendSse(Response, "step", r, ct);
            allResults.Add(r);
            await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct, projectRoot: projectRoot);
            return stepIndex + 1;
        }

        if (string.Equals(newText, oldText, StringComparison.Ordinal))
        {
            await EmitLog(emitSse, "info", $"✓ Already done (no-op): {relPath} — tabular edit produced no change", ct: ct);
            var skip = new Dictionary<string, object?>
            {
                ["index"] = stepIndex,
                ["type"] = "edit",
                ["status"] = "skipped",
                ["path"] = relPath,
                ["reason"] = "already done",
                ["planItemIndex"] = planItemIndex
            };
            if (emitSse) await SendSse(Response, "step", skip, ct);
            allResults.Add(skip);
            await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct, projectRoot: projectRoot);
            return stepIndex + 1;
        }

        await System.IO.File.WriteAllTextAsync(fullPath, newText!, Encoding.UTF8, ct);
        await EmitLog(emitSse, "success", $"🧮 Tabular edit on {relPath}: {reason}", ct: ct);
        var res = new Dictionary<string, object?>();
        PopulateEditResult(runContext, res, "modified", relPath, oldText, newText, newText!);
        res["index"] = stepIndex;
        res["planItemIndex"] = planItemIndex;
        res["reason"] = reason;
        res["tabular"] = true;
        if (emitSse) await SendSse(Response, "step", res, ct);
        allResults.Add(res);
        await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct, projectRoot: projectRoot);
        try { _fileHints.LearnFromAppliedEdit(projectRoot, fullPath, newText!); } catch { }
        return stepIndex + 1;
    }

    private Task<int> ResolveAndApplyEdit(
        PlanStep step,
        string projectRoot,
        bool emitSse,
        CancellationToken ct,
        List<object> allResults,
        int stepIndex,
        string? prompt = null,
        AgentPlan? plan = null,
        int planItemIndex = -1,
        string? cardId = null,
        List<string>? attachedFiles = null,
        int replanDepth = 0,
        Func<string, Task>? onActivity = null,
        bool skipLlmPreResolution = false)
        => ResolveAndApplyEditCore(new AgentRunContext(), step, projectRoot, emitSse, ct, allResults, stepIndex,
            prompt, plan, planItemIndex, cardId, attachedFiles, replanDepth, onActivity, skipLlmPreResolution);

    private async Task<int> ResolveAndApplyEditCore(
        AgentRunContext runContext,
        PlanStep step,
        string projectRoot,
        bool emitSse,
        CancellationToken ct,
        List<object> allResults,
        int stepIndex,
        string? prompt = null,
        AgentPlan? plan = null,
        int planItemIndex = -1,
        string? cardId = null,
        List<string>? attachedFiles = null,
        int replanDepth = 0,
        Func<string, Task>? onActivity = null,
        bool skipLlmPreResolution = false)
    {
        var relPath = step.File.Replace('\\', '/').TrimStart('/');
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
        // The editor model is trained to write a literal space inside a required heading as
        // `&nbsp;` (it keeps dropping the space — benchmark 23's 'Benchmark 23' → 'Benchmark23').
        // Restore the REAL space deterministically on every edit payload BEFORE anything is
        // written, so the saved file (and the applied-edit ground truth) carries the real space.
        AgentTextUtilities.NormalizeNbspInStep(step);
        // DIRECTORY-TARGET GUARD: a replanner (repair loop) can re-emit "create directory X" as a
        // NORMAL edit step whose File is the directory path itself (not a _create_directory marker).
        // The edit pipeline would then treat the folder as a file target: exploration + LLM produce
        // full-file content, and ApplyFullFile writes it to the directory path → UnauthorizedAccess-
        // Exception on Windows (killed a benchmark run). If the change description names a concrete
        // file, redirect the write INTO the directory; otherwise the directory already exists, so the
        // step's intent (create the directory) is satisfied → mark it done without touching disk.
        if (Directory.Exists(fullPath) && !System.IO.File.Exists(fullPath))
        {
            var redirected = AgentDiscovery.ResolveDirectoryTargetForStep(relPath, step.Change);
            if (!string.IsNullOrWhiteSpace(redirected))
            {
                await EmitLog(emitSse, "info",
                    $"Step targets existing directory '{relPath}' but change names a file — redirecting write to {redirected}", ct: ct);
                relPath = redirected;
                fullPath = Path.GetFullPath(Path.Combine(projectRoot, redirected.Replace('/', Path.DirectorySeparatorChar)));
            }
            else
            {
                await EmitLog(emitSse, "info",
                    $"✓ Already done: {relPath} — target is an existing directory; nothing to write", ct: ct);
                var skip = new Dictionary<string, object?>
                {
                    ["index"] = stepIndex,
                    ["type"] = "edit",
                    ["status"] = "skipped",
                    ["path"] = relPath,
                    ["reason"] = "target is an existing directory — already created",
                    ["planItemIndex"] = planItemIndex
                };
                if (emitSse) await SendSse(Response, "step", skip, ct);
                allResults.Add(skip);
                await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct, projectRoot: projectRoot);
                return stepIndex + 1;
            }
        }
        bool stepNeedsExtraStep = false;
        string? stepExtraStepReason = null;
        string? stepExtraStepFile = relPath;
        var createIdx = await TryCreateFileAsync(step, projectRoot, emitSse, ct, allResults, stepIndex, planItemIndex, cardId, relPath, fullPath);
        if (createIdx != null) return createIdx.Value;
        // ── TABULAR DATA FAST-PATH ────────────────────────────────────────────────
        // CSV/TSV/XLSX files are edited STRUCTURALLY (parse → operation → serialize),
        // never through the text-replace pipeline, which corrupts RFC-4180 quoting (and
        // destroys an .xlsx ZIP outright). A recognized tabular operation is applied here
        // deterministically — zero LLM. For .csv/.tsv an UNRECOGNIZED operation falls
        // through to the normal anchored text path (still safe); .xlsx/.xls are binary and
        // are handled here exclusively so a text read-modify-write can never corrupt them.
        if (TabularFileService.IsTabularFile(relPath) && System.IO.File.Exists(fullPath))
        {
            var tabIdx = await ApplyTabularEditAsync(step, relPath, fullPath, projectRoot, emitSse, ct,
                allResults, stepIndex, planItemIndex, cardId, runContext);
            if (tabIdx != null) return tabIdx.Value;
        }
        var cfg8 = await LoadConfigAsync();
        var attemptScores = new List<(int attempt, int score, string reason, string failedNew)>();
        var bestScore = 0;
        var bestAttempt = -1;
        var fileExt = Path.GetExtension(relPath).ToLowerInvariant();
        var editKnowledge = await _editKnowledge.LoadAsync(projectRoot, ct);
        var filteredEditKnowledge = EditKnowledgeService.FormatForContext(
            editKnowledge, fileExt, step.Change ?? prompt ?? "");
        // Harvested _web_search/_web_fetch outputs flow into the edit-resolution prompt too —
        // when the step is "write the article data into a file", the FORMAT C/D generation must
        // see the real titles/URLs/facts instead of inventing them.
        var webResultsContext = HarvestWebResultsForEditContext(allResults.OfType<Dictionary<string, object?>>());
        if (!string.IsNullOrWhiteSpace(webResultsContext))
        {
            await EmitLog(emitSse, "info",
                $"🌐 Injected {webResultsContext.Length} chars of harvested web results into the edit-resolution context for {relPath}", ct: ct);
        }
        await EmitLog(emitSse, "info",
            $"▶ Resolving Edits: {relPath} — {step.Change}", new { prompt, plan, stepIndex, allResults }, ct: ct);
        if (emitSse)
            await SendSse(Response, "step", new
            {
                index = stepIndex,
                type = "edit",
                status = "running",
                path = relPath,
                description = step.Change,
                planItemIndex,
                line = step.LineNumber
            }, ct);
        var preValidatedIdx = await ValidatePreEditAsync(step, projectRoot, emitSse, ct, allResults, stepIndex, planItemIndex, cardId, relPath, fullPath);
        if (preValidatedIdx != null) return preValidatedIdx.Value;
        var (preparedStep, explorationContext, preservationDirective, decidedEditStrategy, explorationTargetSymbol) =
            await PrepareEditContextAsync(step, projectRoot, emitSse, ct, prompt, plan, planItemIndex,
                cardId, attachedFiles, skipLlmPreResolution, relPath, fullPath, runContext);
        step = preparedStep;
        // Fully deterministic edits (old+new synthesized server-side) need no causal
        // reasoning and no multi-round LLM verification — they are correct by construction.
        // A step that ALREADY carries the server-authored batch marker (OldString/NewString/
        // Edits set by an earlier generation, e.g. skipLlmPreResolution runs) is deterministic
        // too: on drift it must go through G1's zero-LLM re-synthesis, never the LLM resolver.
        var isDeterministicEdit = decidedEditStrategy?.ResolvedNewStr != null ||
            step.NewString?.StartsWith("(deterministic batch:", StringComparison.Ordinal) == true;
        await PersistStepStatusAsync(cardId, planItemIndex, "applying", emitSse, ct);
        var history = new List<(string old, string @new, string error)>();
        var planOldStr = step.OldString;
        var planNewStr = step.NewString;
        var planOldTried = false;
        var stuckCount = 0;
        var resolveStuckCount = 0;
        var lastResolveError = "";
        var lastOld = "";
        string? preEditContent = null;
        if (System.IO.File.Exists(fullPath))
            preEditContent = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
        const int MaxAttempts = 8;
        // TryForcedMethodInsertAsync was removed — it had its own raw-code LLM call that
        // bypassed FORMAT C entirely, producing unvalidated stitched output.
        // Method insertions now go through ResolveEditForStep with editStrategy=InsertMethod
        // which uses FORMAT C insertAfter, validates output, and has full retry/escalation.
        string? causalContext = null;
        (planOldStr, causalContext) = await ResolveAstOldStringAndCausalAsync(
            step, planOldStr, explorationTargetSymbol, relPath, fullPath, fileExt,
            prompt, projectRoot, skipLlmPreResolution || isDeterministicEdit, emitSse, ct);
        // ANCHOR SANITY (deterministic, pre-loop): a plan-provided oldString that is bare
        // punctuation (the "}" anchor class) is never a usable anchor — it matches dozens of
        // places or deletes structural code. Bounce it before the attempt loop so no LLM
        // round-trip is wasted resolving replacement code against it; the resolver then
        // re-anchors against the real file content from scratch.
        if (!string.IsNullOrWhiteSpace(planOldStr) &&
            StructureEditHeuristics.ShouldBounceGarbageAnchor(planOldStr, isDeterministicEdit))
        {
            await EmitLog(emitSse, "warn",
                $"✗ Plan-provided oldString for {relPath} is bare punctuation ('{OneLinePreview(planOldStr)}') — not a usable anchor. " +
                "Bouncing to the edit resolver to re-anchor against the real file content.", ct: ct);
            if (skipLlmPreResolution)
            {
                // The bounce breaks the caller's zero-LLM intent (the plan was supposed to be
                // fully deterministic) — make that visible rather than mysterious.
                await EmitLog(emitSse, "info",
                    $"  Note: skipLlmPreResolution was set, but the garbage anchor forces one resolver round-trip for {relPath}", ct: ct);
            }
            planOldStr = null;
            planNewStr = null;
        }
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (attempt > 0 && !string.IsNullOrWhiteSpace(preEditContent))
            {
                await SaveEditWithUndoAsync(fullPath, preEditContent, relPath, projectRoot, preEditContent, ct);
                await EmitLog(emitSse, "info",
                    $"Retry {attempt + 1}/{MaxAttempts} reset {relPath} to the clean pre-edit snapshot before re-resolving the change", ct: ct);
            }
            string? oldStr = null, newStr = null, resolveError = null;
            bool fullFile = false, alreadyDone = false;
            string? fullContent = null;
            bool fromFormatC = false;
            bool replacementAlreadyFormatted = false;
            if (attempt == 0 && !string.IsNullOrWhiteSpace(planOldStr) && !planOldTried)
            {
                if (decidedEditStrategy?.Strategy == EditStrategy.InsertMethod || step.InsertAfter == true)
                {
                    planOldStr = null;
                    planNewStr = null;
                }
            }
            if (attempt == 0 && !string.IsNullOrWhiteSpace(planOldStr) && !planOldTried)
            {
                planOldTried = true;
                var isRemovalChange = (step.Change ?? "").Trim().ToLowerInvariant() is var chL &&
                    (chL.StartsWith("remove ") || chL.StartsWith("delete ") || chL.StartsWith("delete the ") ||
                     chL.StartsWith("remove the ") || chL.Contains("remove the ") || chL.Contains("delete the "));
                if (string.IsNullOrWhiteSpace(planNewStr))
                {
                    if (skipLlmPreResolution && step.NewCode is { Count: > 0 } && step.InsertAfter != true)
                    {
                        // FORMAT C/D REPLACE: the planner already supplied the replacement payload.
                        // Materialize newCode directly — no focused LLM round-trip.
                        oldStr = AgentTextUtilities.NormalizeLineEndings(planOldStr);
                        newStr = AgentTextUtilities.NormalizeLineEndings(string.Join("\n", step.NewCode));
                        fromFormatC = true;
                        await EmitLog(emitSse, "info",
                            $"Using plan-supplied FORMAT C/D newCode directly for {relPath} (old={oldStr.Split('\n').Length}L, new={newStr.Split('\n').Length}L)", step, ct: ct);
                    }
                    else if (skipLlmPreResolution && string.IsNullOrWhiteSpace(step.NewString) && isRemovalChange)
                    {
                        // Deletion: the planner supplied oldString + empty newString AND the change
                        // explicitly asks to remove/delete. Attempt the deletion directly; the
                        // tolerant matcher absorbs drift. (Gated on removal intent so an oldString-only
                        // "update/modify" step never gets treated as a destructive delete.)
                        oldStr = AgentTextUtilities.NormalizeLineEndings(planOldStr);
                        newStr = "";
                        await EmitLog(emitSse, "info",
                            $"Applying plan-supplied deletion for {relPath} (old={oldStr.Split('\n').Length}L → removed)", step, ct: ct);
                    }
                    else
                    {
                        await EmitLog(emitSse, "info",
                            $"AST-resolved oldString is set — making focused LLM call for replacement code only", ct: ct);
                        var replacePrompt = new StringBuilder();
                        replacePrompt.AppendLine("You are replacing the following method/function in the file. Output ONLY the replacement code — no JSON wrapper, no explanation, no markdown.");
                        replacePrompt.AppendLine();
                        replacePrompt.AppendLine("CURRENT METHOD SOURCE (to be replaced):");
                        replacePrompt.AppendLine(planOldStr);
                        replacePrompt.AppendLine();
                        replacePrompt.AppendLine("CHANGE REQUIRED: " + (step.Change ?? ""));
                        replacePrompt.AppendLine();
                        replacePrompt.AppendLine("Output ONLY the replacement code. It MUST be a complete method/function declaration (signature + body).");
                        replacePrompt.AppendLine("Do NOT include markdown code fences or any other text — just the raw source code.");
                        replacePrompt.AppendLine("HEADING/TITLE SPACES: if the code must contain a heading/title with a literal space (e.g. 'Benchmark 23'), write it as `Benchmark&nbsp;23` — never merge the words. The `&nbsp;` is converted to a real space automatically after the edit.");
                        var (rawReplacement, replaceError) = await CallLlmRawText(
                            "You are a precise code editor. Output ONLY the replacement source code with no formatting, no markdown, no explanation. " +
                            "Do NOT add comments (// or) to the code.",
                            replacePrompt.ToString(), emitSse, ct,
                            requestTimeout: _infiniteTimeout,
                            maxTokens: 2048);
                        if (!string.IsNullOrWhiteSpace(replaceError) || string.IsNullOrWhiteSpace(rawReplacement) || rawReplacement.Length < 10)
                        {
                            resolveError = replaceError ?? "LLM returned empty replacement";
                            await EmitLog(emitSse, "warn",
                                $"Focused replacement call failed: {resolveError}", ct: ct);
                        }
                        else
                        {
                            var cleaned = rawReplacement.Trim();
                            cleaned = Regex.Replace(cleaned, @"^```[a-zA-Z]*\s*", "");
                            cleaned = Regex.Replace(cleaned, @"\s*```$", "");
                            oldStr = AgentTextUtilities.NormalizeLineEndings(planOldStr);
                            newStr = AgentTextUtilities.NormalizeLineEndings(cleaned.Trim());
                            var fmtExt = Path.GetExtension(relPath).ToLowerInvariant();
                            if (fmtExt == ".css" || fmtExt == ".scss" || fmtExt == ".less")
                                newStr = LlmCssCleaner.Clean(newStr);
                            var trimmedNew = newStr.TrimStart();
                            if (trimmedNew.StartsWith("{") && (fmtExt is ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs"))
                            {
                                resolveError = "Focused LLM returned body-only code (starts with '{') — need a complete method declaration";
                                await EmitLog(emitSse, "warn", $"  {resolveError} ({newStr.Length} chars)", ct: ct);
                            }
                            else
                            {
                                // Deterministic Python scope guard: if the AST-resolved oldString is a
                                // whole CLASS but the LLM returned a bare method (or vice versa), the
                                // apply would replace the class with a method and produce an immediate
                                // IndentationError/SyntaxError. Reject BEFORE applying so the retry
                                // regenerates a replacement that matches the oldString's scope.
                                var pyOldKind = fmtExt == ".py" ? ContentEditHeuristics.PythonDeclarationKind(oldStr) : null;
                                var pyNewKind = fmtExt == ".py" ? ContentEditHeuristics.PythonDeclarationKind(newStr) : null;
                                var scopeMismatch = pyOldKind is "class" or "function"
                                    && pyNewKind is "class" or "function"
                                    && pyOldKind != pyNewKind;
                                if (scopeMismatch)
                                {
                                    resolveError = $"Scope mismatch: oldString is a {pyOldKind} declaration but the replacement is a {pyNewKind} declaration — output a complete {pyOldKind} declaration matching the oldString's scope (keep the class/def header and its indentation level)";
                                    await EmitLog(emitSse, "warn", $"  {resolveError} ({newStr.Length} chars)", ct: ct);
                                }
                                else
                                {
                                    if (fmtExt is ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs")
                                        newStr = AgentCodeFormatting.AutoFixOperatorSpacing(newStr);
                                    newStr = await FormatSnippetAsync(planOldStr, newStr, relPath);
                                    fromFormatC = true;
                                    replacementAlreadyFormatted = true;
                                    await EmitLog(emitSse, "info",
                                        $"Focused LLM returned replacement: old={oldStr.Split('\n').Length}L, new={newStr.Split('\n').Length}L", ct: ct);
                                }
                            }
                        }
                    }
                }
                else
                {
                    oldStr = AgentTextUtilities.NormalizeLineEndings(planOldStr);
                    newStr = AgentTextUtilities.NormalizeLineEndings(planNewStr!);
                    await EmitLog(emitSse, "info",
                        $"Using plan-provided edit for {relPath}", step, ct: ct);
                }
            }
            else
            {
                if (attempt > 0)
                {
                    await EmitLog(emitSse, "warn",
                        $"Resolve retry {attempt + 1} for {relPath}",
                        new { step, projectRoot }, ct: ct);
                }
                // G1 — a deterministic step that failed on attempt 0 gets ONE free
                // re-synthesis against the CURRENT file content before escalating to the
                // LLM resolver (which escalates toward full-file rewrite). The file may
                // have drifted between generation and apply (parallel agent threads, an
                // external save, a formatter), so re-running the generator re-anchors the
                // edit at zero LLM cost. If the generator declines now — the change is no
                // longer deterministically describable against this content — fall through
                // to the normal LLM resolve path.
                var usedFreshDeterministic = false;
                if (attempt == 1 && isDeterministicEdit)
                {
                    var freshExists = System.IO.File.Exists(fullPath);
                    var freshContent = freshExists
                        ? await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct)
                        : string.Empty;
                    var freshDet = DeterministicEditGenerator.TryGenerate(
                        relPath, freshExists, freshContent, step.Change ?? "");
                    if (freshDet != null)
                    {
                        oldStr = freshDet.OldStr;
                        newStr = freshDet.NewStr;
                        step.OldString = freshDet.OldStr;
                        step.NewString = freshDet.NewStr;
                        step.Edits = freshDet.Edits is { Count: > 0 } ? freshDet.Edits : null;
                        if (freshDet.LineNumber > 0) step.LineNumber = freshDet.LineNumber;
                        fullFile = false;
                        fullContent = null;
                        alreadyDone = false;
                        resolveError = null;
                        fromFormatC = false;
                        usedFreshDeterministic = true;
                        await EmitLog(emitSse, "info",
                            $"⚙️ G1: deterministic edit re-synthesized against current file content — {freshDet.Reason}", ct: ct);
                    }
                    else
                    {
                        // The generator declined — the change is no longer deterministically
                        // describable against this content, so the stale batch (if any) must
                        // not re-run after the LLM resolve below.
                        step.Edits = null;
                        await EmitLog(emitSse, "info",
                            "⚙️ G1: deterministic re-synthesis declined against current content — escalating to LLM resolver", ct: ct);
                    }
                }
                if (!usedFreshDeterministic)
                {
                    (oldStr, newStr, fullFile, fullContent, alreadyDone, resolveError, fromFormatC) =
                        await ResolveEditForStep(
                            step, projectRoot, emitSse, ct, history,
                            explorationContext: explorationContext,
                            targetSymbol: explorationTargetSymbol,
                            originalPrompt: prompt,
                            preservationDirective: preservationDirective,
                            fullPlan: plan,
                            planItemIndex: planItemIndex,
                            filteredEditKnowledge: filteredEditKnowledge,
                            causalContext: causalContext,
                            webResultsContext: webResultsContext);
                }
                if (resolveError == null && !usedFreshDeterministic)
                {
                    var fmt = fullFile ? "fullFile" : alreadyDone ? "alreadyDone" : fromFormatC ? "FORMAT C" : "oldString/newString";
                    var oldLen = oldStr?.Length ?? 0;
                    var newLen = newStr?.Length ?? 0;
                    await EmitLog(emitSse, "info",
                        $"  LLM produced: format={fmt}, old={oldLen}ch, new={newLen}ch", ct: ct);
                    if (!fromFormatC && !alreadyDone && HtmlDomEditor.IsHtmlDomFile(relPath) && !string.IsNullOrWhiteSpace(newStr))
                    {
                        var err = "HTML files: use FORMAT D (targetType=\"html\", targetName, insertAfter, newCode). Do NOT use oldString/newString.";
                        await EmitRejectedLog(emitSse, $"HTML edit rejected — {err}", err, ct);
                        history.Add((oldStr ?? "", newStr ?? "", err));
                        continue;
                    }
                }
            }
            // ANCHOR SANITY (post-resolve, deterministic): bounce bare-punctuation oldStrings
            // the resolver produced — the same "}" class — without any apply attempt or verify
            // call, feeding the model clear feedback on why the anchor is unusable.
            if (resolveError == null && !string.IsNullOrWhiteSpace(oldStr) &&
                StructureEditHeuristics.ShouldBounceGarbageAnchor(oldStr, isDeterministicEdit))
            {
                var err = "ANCHOR SANITY: oldString is bare punctuation (e.g. a lone '}'), which would match dozens of places " +
                          "or destroy structural code. Output a real, unique code line (with surrounding context) as your oldString.";
                await EmitLog(emitSse, "warn",
                    $"Edit attempt {attempt + 1}/{MaxAttempts} failed for {relPath}: {err}", ct: ct);
                history.Add((oldStr, newStr ?? "", err));
                if (string.Equals(AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""), AgentTextUtilities.NormalizeLineEndings(lastOld), StringComparison.Ordinal)) stuckCount++;
                else { stuckCount = 0; lastOld = AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""); }
                if (stuckCount >= 2) goto RecordFailure;
                continue;
            }
            if (resolveError != null)
            {
                await EmitLog(emitSse, "warn",
                    $"Resolve attempt {attempt + 1}/{MaxAttempts}: {resolveError}",
                    new { resolveError, fullContent, step }, ct: ct);
                history.Add((step.OldString ?? "", step.NewString ?? "", resolveError));
                var normalizedError = Regex.Replace(resolveError ?? "", @"\(\d+ chars\)", "(N chars)")
                    .Trim();
                var normalizedLast = Regex.Replace(lastResolveError ?? "", @"\(\d+ chars\)", "(N chars)")
                    .Trim();
                if (normalizedError == normalizedLast) resolveStuckCount++;
                else { resolveStuckCount = 0; lastResolveError = resolveError; }
                if (resolveStuckCount >= 2)
                {
                    await EmitLog(emitSse, "error",
                        $"LLM keeps failing to produce valid edit output — aborting {relPath}",
                        ct: ct);
                    goto RecordFailure;
                }
                continue;
            }
            // NBSP → real space for LLM-resolved content (the plan-provided payload was already
            // normalized at method start; the resolver's output needs the same deterministic pass).
            if (!string.IsNullOrWhiteSpace(newStr))
                newStr = AgentTextUtilities.NormalizeNbsp(newStr);
            if (fullFile && !string.IsNullOrWhiteSpace(fullContent))
                fullContent = AgentTextUtilities.NormalizeNbsp(fullContent);
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
                await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct, projectRoot: projectRoot);
                return stepIndex + 1;
            }
            if (fullFile && fullContent != null)
            {
                var fileAlreadyExists = System.IO.File.Exists(fullPath);
                var fullFileExt = Path.GetExtension(relPath).ToLowerInvariant();
                var existingFileLength = fileAlreadyExists
                    ? new FileInfo(fullPath).Length
                    : 0;
                var isSmallFile = IsFullFileAllowed(fileAlreadyExists, (int)Math.Min(existingFileLength, int.MaxValue));
                if (fileAlreadyExists && !isSmallFile)
                {
                    var e = "This file already exists and is not small — use a targeted oldString/newString edit instead. " +
                            "Pick a single unique line INSIDE the target section (≥20 chars, appears once in the file) as your oldString.";
                    await EmitLog(emitSse, "error", e, ct: ct);
                    history.Add((step.OldString ?? "", step.NewString ?? "", e));
                    resolveStuckCount++;
                    if (resolveStuckCount >= 3)
                    {
                        await EmitLog(emitSse, "error",
                            $"LLM keeps using wrong format for existing file — aborting {relPath}",
                            ct: ct);
                        goto RecordFailure;
                    }
                    continue;
                }
                if (fileAlreadyExists && isSmallFile)
                {
                    await EmitLog(emitSse, "warn",
                        $"⚠ Accepting replacement for small existing file {relPath} ({fullContent.Length} chars)", ct: ct);
                }
                stepIndex = await ApplyFullFile(
                    fullContent, step, fullPath, relPath,
                    projectRoot, stepIndex, planItemIndex, cardId, emitSse, ct, allResults, runContext);
                return stepIndex;
            }
            var fileContent = System.IO.File.Exists(fullPath)
                ? await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct)
                : string.Empty;
            bool replaced = false;
            string newContent = fileContent;
            string? matchError = null;
            string? snippet = null;
            bool bypassVerify = false;
            int oldLines = oldStr?.Split('\n').Length ?? 0;
            int newLines = newStr?.Split('\n').Length ?? 0;
            string? sqlMigrationNote = null;
            // NEW TABLE/COLUMN ENFORCEMENT: if the LLM still inlined CREATE TABLE or ALTER
            // TABLE statements (old behavior), move them into migrations/schema_changes.md and
            // strip them from the applied code — the user applies the changes manually, the
            // endpoint stays clean, and the SQL guard sees the table/column as covered. NOTE:
            // batch steps (step.Edits) compose newContent independently below and bypass this
            // hook — inline DDL there is an accepted edge case since the planner is trained to
            // emit _sql_migration steps for new tables/columns anyway.
            if (!string.IsNullOrWhiteSpace(newStr) &&
                !string.Equals(Path.GetExtension(relPath), ".sql", StringComparison.OrdinalIgnoreCase))
            {
                var inlineTables = SqlMigrationService.ExtractCreateTableStatements(newStr);
                var inlineAlters = SqlMigrationService.ExtractAlterTableStatements(newStr);
                if (inlineTables.Count > 0 || inlineAlters.Count > 0)
                {
                    var writtenMigrations = new List<string>();
                    foreach (var (table, sql) in inlineTables)
                    {
                        var rel = SqlMigrationService.WriteMigration(projectRoot, table, sql);
                        if (rel != null) writtenMigrations.Add(rel);
                    }
                    foreach (var (table, column, sql) in inlineAlters)
                    {
                        var rel = SqlMigrationService.WriteAlterMigration(projectRoot, table, column, sql);
                        if (rel != null) writtenMigrations.Add(rel);
                    }
                    var strippedNewStr = SqlMigrationService.StripCreateTableStatements(
                        newStr, inlineTables.Select(t => t.Sql).ToList());
                    strippedNewStr = SqlMigrationService.StripAlterTableStatements(
                        strippedNewStr, inlineAlters.Select(a => a.Sql).ToList());
                    if (strippedNewStr != newStr)
                    {
                        await EmitLog(emitSse, "info",
                            $"Auto-documented {inlineTables.Count} inline CREATE TABLE and {inlineAlters.Count} inline ALTER TABLE " +
                            $"statement(s) out of {relPath} into {SqlMigrationService.SchemaChangesRelPath} — the method body now only references the schema", ct: ct);
                        newStr = strippedNewStr;
                        newLines = newStr.Split('\n').Length;
                        // Tell the verifier WHY the DDL is gone so it doesn't reject the edit
                        // as missing schema: the statements moved to migrations/schema_changes.md
                        // and the user applies them manually.
                        sqlMigrationNote =
                            "NOTE: The edit's inline CREATE TABLE / ALTER TABLE statement(s) were automatically moved to " +
                            $"{SqlMigrationService.SchemaChangesRelPath} (e.g. {string.Join(", ", writtenMigrations.Take(3))}). " +
                            "The user applies the schema changes to their database manually. " +
                            "The method body intentionally contains ONLY INSERT/UPDATE/SELECT. " +
                            "Do NOT reject this edit for a missing CREATE TABLE or ALTER TABLE — the schema lives in the schema-changes file.";
                    }
                    foreach (var rel in writtenMigrations.Distinct(StringComparer.OrdinalIgnoreCase))
                        await EmitLog(emitSse, "success",
                            $"📦 Schema change documented: {rel} — apply it to your database manually", ct: ct);
                }
            }
            if (step.Edits is { Count: > 0 } && !replaced)
            {
                // Reject overlapping edits within the same batch — each edit must target a different
                // area. POSITION-AWARE: identical anchors at DIFFERENT lines are fine (each edit
                // carries its own LineNumber hint, which TryReplaceSafe uses to disambiguate); only
                // edits that actually match at overlapping positions in the file are rejected.
                var allApplied = true;
                var normFileBatch = AgentTextUtilities.NormalizeLineEndings(fileContent);
                var assignedRanges = new List<(int editIdx, int start, int end)>();
                for (var i = 0; i < step.Edits.Count; i++)
                {
                    var normO = AgentTextUtilities.NormalizeLineEndings(step.Edits[i].OldString ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(normO)) continue;
                    var positions = new List<int>();
                    var sp = 0;
                    while ((sp = normFileBatch.IndexOf(normO, sp, StringComparison.Ordinal)) >= 0)
                    {
                        positions.Add(sp);
                        sp += Math.Max(1, normO.Length);
                    }
                    if (positions.Count == 0) continue; // missing anchor — the sequential apply reports it
                    var targetLine = step.Edits[i].LineNumber > 0 ? step.Edits[i].LineNumber : step.LineNumber;
                    var chosen = positions[0];
                    if (positions.Count > 1 && targetLine > 0)
                    {
                        var bestDist = int.MaxValue;
                        foreach (var p in positions)
                        {
                            var lineOf = normFileBatch[..p].Count(c => c == '\n') + 1;
                            var dist = Math.Abs(lineOf - targetLine);
                            if (dist < bestDist) { bestDist = dist; chosen = p; }
                        }
                    }
                    assignedRanges.Add((i, chosen, chosen + normO.Length));
                }
                for (var oi = 0; oi < assignedRanges.Count && allApplied; oi++)
                {
                    for (var oj = oi + 1; oj < assignedRanges.Count; oj++)
                    {
                        var ra = assignedRanges[oi];
                        var rb = assignedRanges[oj];
                        if (ra.start < rb.end && rb.start < ra.end)
                        {
                            await EmitLog(emitSse, "warn",
                                $"Batch sub-edit overlap: edit {ra.editIdx + 1} and edit {rb.editIdx + 1} target overlapping oldString sections — " +
                                $"each batch edit must target a unique, non-overlapping area of the file.", ct: ct);
                            allApplied = false;
                            break;
                        }
                    }
                }
                if (allApplied)
                {
                    var batchContent = fileContent;
                    foreach (var edit in step.Edits)
                    {
                        if (string.IsNullOrWhiteSpace(edit.OldString)) continue;
                        var normOld = AgentTextUtilities.NormalizeLineEndings(edit.OldString);
                        var normNew = AgentTextUtilities.NormalizeLineEndings(edit.NewString);
                        var (hasReplaced, nc, err, _) = AnchorEditHeuristics.TryReplaceSafe(batchContent, normOld, normNew,
                            edit.LineNumber > 0 ? edit.LineNumber : step.LineNumber, step.Change);
                        if (!hasReplaced)
                        {
                            await EmitLog(emitSse, "warn", $"Batch sub-edit failed: {err}", ct: ct);
                            allApplied = false;
                            break;
                        }
                        batchContent = nc;
                    }
                    if (allApplied && batchContent != fileContent)
                    {
                        replaced = true;
                        newContent = batchContent;
                        matchError = null;
                        snippet = null;
                        oldStr = step.Edits[0].OldString ?? "";
                        // Preserve the deterministic-batch marker VERBATIM when the batch came
                        // from DeterministicEditGenerator (step.NewString carries it, enriched
                        // with "applied N/M occurrences") so the isDeterministicBatch verify
                        // bypass fires AND the meeting ticker can render the compact applied
                        // count — every sub-edit was validated by an exact TryReplaceSafe match,
                        // so content verification is satisfied by construction. LLM batches keep
                        // their existing "(batch:" marker semantics untouched.
                        newStr = step.NewString?.StartsWith("(deterministic batch:", StringComparison.Ordinal) == true
                            ? step.NewString
                            : "(batch: " + step.Edits.Count + " edits)";
                        await EmitLog(emitSse, "info",
                            $"Applied batch of {step.Edits.Count} edits to {relPath}", ct: ct);
                    }
                }
            }
            if (!replaced)
            {
                // A batch that failed to fully apply must NEVER fall through to the single-edit
                // path: newStr here is the batch MARKER ("(deterministic batch: ...)" or
                // "(batch: ...)"), not real code — TryReplaceSafe would happily write it verbatim
                // into the file, and the marker prefix would then pass the verify bypass,
                // "succeeding" with the marker embedded in the code. Fail fast so attempt 1's G1
                // re-synthesis (or the LLM resolver) re-anchors against the current file content.
                if (newStr?.StartsWith("(deterministic batch:", StringComparison.Ordinal) == true ||
                    newStr?.StartsWith("(batch:", StringComparison.Ordinal) == true)
                {
                    var markerErr = "Batch did not fully apply (anchor drift or overlapping edits) — re-anchoring, marker never applied as code";
                    await EmitLog(emitSse, "warn", $"✗ {markerErr} for {relPath}", ct: ct);
                    history.Add((oldStr ?? "", newStr, markerErr));
                    if (string.Equals(AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""), AgentTextUtilities.NormalizeLineEndings(lastOld), StringComparison.Ordinal)) stuckCount++;
                    else { stuckCount = 0; lastOld = AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""); }
                    if (stuckCount >= 2) goto RecordFailure;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(newStr) && !string.IsNullOrWhiteSpace(oldStr))
                {
                    var oldLinesCount = oldStr!.Split('\n').Length;
                    var oldTrimmed = oldStr.TrimStart();
                    // Plan-supplied concrete deletions (skipLlmPreResolution) carry the EXACT block
                    // the planner wants removed — the tolerant matcher verifies it exists line-by-line
                    // before deleting, so the 3-line guard (meant for LLM-generated guesses) doesn't
                    // apply. Let it through so the plan's deletion is attempted directly.
                    if (oldLinesCount > 3 && !skipLlmPreResolution)
                    {
                        var err = $"DELETION SIZE LIMIT: oldString is {oldLinesCount} lines long. When newString is empty (deletion), oldString MUST be 1-3 lines maximum. Output ONLY the exact element being deleted.";
                        await EmitLog(emitSse, "warn", $"Edit attempt {attempt + 1}/{MaxAttempts} failed for {relPath}: {err}", ct: ct);
                        history.Add((oldStr!, newStr ?? "", err));
                        if (string.Equals(AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""), AgentTextUtilities.NormalizeLineEndings(lastOld), StringComparison.Ordinal)) stuckCount++;
                        else { stuckCount = 0; lastOld = AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""); }
                        if (stuckCount >= 2) goto RecordFailure;
                        continue;
                    }
                    if (oldTrimmed.StartsWith("</div") || oldTrimmed.StartsWith("</label") || oldTrimmed.StartsWith("</span") ||
                        oldTrimmed.StartsWith("<div class=\"card-tags\"") || oldTrimmed.StartsWith("<div class=\"attachments\"") ||
                        oldTrimmed.StartsWith("<div class=\"card-actions\"") || oldTrimmed.StartsWith("<!--"))
                    {
                        var err = "STRUCTURAL DELETION GUARD: Refusing to delete structural HTML elements (like </div>, container openings, or comments). Output ONLY the specific <span> or <label> being removed.";
                        await EmitLog(emitSse, "warn", $"Edit attempt {attempt + 1}/{MaxAttempts} failed for {relPath}: {err}", ct: ct);
                        history.Add((oldStr!, newStr ?? "", err));
                        if (string.Equals(AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""), AgentTextUtilities.NormalizeLineEndings(lastOld), StringComparison.Ordinal)) stuckCount++;
                        else { stuckCount = 0; lastOld = AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""); }
                        if (stuckCount >= 2) goto RecordFailure;
                        continue;
                    }
                }
                oldLines = oldStr?.Split('\n').Length ?? 0;
                newLines = newStr?.Split('\n').Length ?? 0;
                var oldPreview = oldStr is { Length: > 0 }
                    ? string.Join("\\n", oldStr.Split('\n').Take(2).Select(l => l.Length > 80 ? l[..80] + "…" : l))
                    : "(empty)";
                var newPreview = newStr is { Length: > 0 }
                    ? string.Join("\\n", newStr.Split('\n').Take(2).Select(l => l.Length > 80 ? l[..80] + "…" : l))
                    : "(empty)";
                await EmitLog(emitSse, "info",
                    $"Applying edit: old={oldLines}L, new={newLines}L | oldStart: {oldPreview} | newStart: {newPreview}",
                    ct: ct);
                // Format only the replacement snippet. FormatSnippetAsync keeps the
                // formatter's structural cleanup while restoring the snippet relative to
                // the original anchor, so a flat TypeScript payload cannot flatten the
                // surrounding method or conditional chain.
                if (!string.IsNullOrWhiteSpace(newStr) && newStr.Length > 10 &&
                    !string.IsNullOrWhiteSpace(oldStr) && CodeFormatterService.CanFormat(relPath) &&
                    !replacementAlreadyFormatted)
                {
                    var before = newStr;
                    newStr = await FormatSnippetAsync(oldStr, newStr, relPath);
                    if (newStr != before)
                        await EmitLog(emitSse, "info", $"Formatted replacement snippet in {relPath} via CodeFormatterService", ct: ct);
                }
                if (!string.IsNullOrWhiteSpace(newStr) && Path.GetExtension(relPath) is ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs")
                {
                    newStr = AgentCodeFormatting.AutoFixOperatorSpacing(newStr);
                    // The replacement was already formatted above through the same
                    // anchor-aware path used by FORMAT C/D and oldString/newString edits.
                }
                if (string.IsNullOrEmpty(oldStr) && string.IsNullOrWhiteSpace(fileContent) && !string.IsNullOrWhiteSpace(newStr))
                {
                    newContent = newStr;
                    replaced = true;
                    matchError = null;
                    snippet = null;
                }
                else if (fromFormatC && !string.IsNullOrEmpty(oldStr))
                {
                    var normFile = AgentTextUtilities.NormalizeLineEndings(fileContent);
                    var normOld = AgentTextUtilities.NormalizeLineEndings(oldStr).TrimEnd('\r');
                    var idx = normFile.IndexOf(normOld, StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        var normNew = AgentTextUtilities.NormalizeLineEndings(newStr ?? "");
                        newContent = normFile[..idx] + normNew + normFile[(idx + normOld.Length)..];
                        replaced = true;
                    }
                    else
                    {
                        var (safeReplaced, safeContent, safeError, safeSnippet) =
                            AnchorEditHeuristics.TryReplaceSafe(fileContent, oldStr, newStr ?? string.Empty, step.LineNumber, step.Change);
                        replaced = safeReplaced;
                        newContent = safeContent;
                        matchError = safeReplaced
                            ? null
                            : "FORMAT C oldString not found in file (direct match failed)" +
                              (string.IsNullOrWhiteSpace(safeError) ? "" : $"; safe matcher: {safeError}");
                        snippet = safeSnippet;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(oldStr) && !fromFormatC)
                {
                    var normFile = AgentTextUtilities.NormalizeLineEndings(fileContent);
                    var normOld = AgentTextUtilities.NormalizeLineEndings(oldStr).Trim('\n', '\r');
                    var normNew = AgentTextUtilities.NormalizeLineEndings(newStr ?? "").Trim('\n', '\r');
                    var fileLinesArr = normFile.Split('\n').ToList();
                    var oldLinesArr = normOld.Split('\n').ToList();
                    var newLinesArr = string.IsNullOrWhiteSpace(normNew)
                        ? new List<string>()
                        : normNew.Split('\n').ToList();
                    while (newLinesArr.Count > 0 && string.IsNullOrWhiteSpace(newLinesArr[^1]))
                        newLinesArr.RemoveAt(newLinesArr.Count - 1);
                    int matchIdx = -1;
                    var targetLineIdx = step.LineNumber > 0 ? step.LineNumber - 1 : -1;
                    var allMatches = new List<int>();
                    static string NormalizeForMatch(string s) =>
                        Regex.Replace(s.TrimEnd(',', ';', ' ', '\t'), @"\s+", "");
                    for (int i = 0; i <= fileLinesArr.Count - oldLinesArr.Count; i++)
                    {
                        bool match = true;
                        for (int j = 0; j < oldLinesArr.Count; j++)
                        {
                            var fileLine = fileLinesArr[i + j].Trim();
                            var oldLine = oldLinesArr[j].Trim();
                            if (fileLine == oldLine) continue;
                            if (Regex.Replace(fileLine, @"\s+", "") == Regex.Replace(oldLine, @"\s+", "")) continue;
                            if (string.Equals(fileLine, oldLine, StringComparison.OrdinalIgnoreCase)) continue;
                            if (string.Equals(Regex.Replace(fileLine, @"\s+", ""), Regex.Replace(oldLine, @"\s+", ""), StringComparison.OrdinalIgnoreCase)) continue;
                            if (NormalizeForMatch(fileLine) == NormalizeForMatch(oldLine)) continue;
                            match = false;
                            break;
                        }
                        if (match) allMatches.Add(i);
                    }
                    if (allMatches.Count == 1)
                    {
                        matchIdx = allMatches[0];
                    }
                    else if (allMatches.Count > 1)
                    {
                        var keywords = AgentDiscovery.ExtractDisambiguationKeywords(step.Change);
                        if (keywords.Count > 0)
                        {
                            var bestKwScore = -1;
                            foreach (var mi in allMatches)
                            {
                                var ctxStart = Math.Max(0, mi - 3);
                                var ctxLines = fileLinesArr
                                    .Skip(ctxStart)
                                    .Take(mi - ctxStart + oldLinesArr.Count)
                                    .ToList();
                                var ctx = string.Join("\n", ctxLines).ToLowerInvariant();
                                var score = keywords.Count(k => ctx.Contains(k));
                                if (score > bestKwScore) { bestKwScore = score; matchIdx = mi; }
                            }
                        }
                        if (matchIdx < 0 && targetLineIdx >= 0)
                        {
                            var bestDist = int.MaxValue;
                            foreach (var mi in allMatches)
                            {
                                var dist = Math.Abs(mi - targetLineIdx);
                                if (dist < bestDist) { bestDist = dist; matchIdx = mi; }
                            }
                        }
                        if (matchIdx < 0) matchIdx = allMatches[0];
                    }
                    if (matchIdx >= 0)
                    {
                        var exactOldLines = new List<string>();
                        for (var j = matchIdx; j < matchIdx + oldLinesArr.Count; j++)
                        {
                            exactOldLines.Add(fileLinesArr[j]);
                        }
                        var exactOldStr = string.Join("\n", exactOldLines);
                        if (exactOldStr != normOld)
                        {
                            oldStr = exactOldStr;
                            normOld = AgentTextUtilities.NormalizeLineEndings(oldStr).Trim('\n', '\r');
                            oldLinesArr = normOld.Split('\n').ToList();
                        }
                        var finalNewLines = FormattingEditHeuristics.ReindentReplacementSnippet(
                            newLinesArr, oldLinesArr, fileLinesArr, matchIdx,
                            HtmlDomEditor.IsHtmlDomFile(relPath));
                        fileLinesArr.RemoveRange(matchIdx, oldLinesArr.Count);
                        fileLinesArr.InsertRange(matchIdx, finalNewLines);
                        newContent = string.Join("\n", fileLinesArr);
                        replaced = true;
                        matchError = null;
                        snippet = null;
                    }
                    else
                    {
                        var (r, nc, me, sn) = AnchorEditHeuristics.TryReplaceSafe(fileContent, oldStr!, newStr ?? string.Empty, step.LineNumber, step.Change);
                        replaced = r; newContent = nc; matchError = me; snippet = sn;
                    }
                }
                else
                {
                    var (r, nc, me, sn) = AnchorEditHeuristics.TryReplaceSafe(fileContent, oldStr!, newStr ?? string.Empty, step.LineNumber, step.Change);
                    replaced = r; newContent = nc; matchError = me; snippet = sn;
                }
            }
            if (!string.IsNullOrWhiteSpace(newStr) && !StructureEditHeuristics.IsBraceBalanced(newStr))
            {
                var oldIsUnbalanced = !string.IsNullOrWhiteSpace(oldStr) && !StructureEditHeuristics.IsBraceBalanced(oldStr);
                if (oldIsUnbalanced)
                {
                    await EmitLog(emitSse, "info",
                        $"Brace imbalance in newStr matches oldStr — expected for snippet from larger block. Skipping repair.", ct: ct);
                }
                else
                {
                    var repairedNewStr = RepairBrokenCodeWithLadder(newStr, oldStr, fileContent, step.LineNumber, step.Change ?? "");
                    if (repairedNewStr != null)
                    {
                        newStr = repairedNewStr;
                        await EmitLog(emitSse, "warn",
                            $"Deterministic brace repair applied to {relPath} before write (attempt {attempt + 1}/{MaxAttempts})", ct: ct);
                    }
                    else
                    {
                        var prettierFixed = await TryFixBracesWithPrettierAsync(relPath, newStr, ct);
                        if (prettierFixed != null)
                        {
                            newStr = prettierFixed;
                            await EmitLog(emitSse, "warn",
                                $"Prettier brace repair applied to {relPath} before write (attempt {attempt + 1}/{MaxAttempts})", ct: ct);
                        }
                        else
                        {
                            await EmitLog(emitSse, "warn",
                                $"Edit attempt {attempt + 1}/{MaxAttempts} found a brace imbalance in the candidate replacement for {relPath}, but the edit will still be attempted so the post-write verifier can decide whether the write is valid.", ct: ct);
                        }
                    }
                }
            }
            // Tree-sitter correction is handled in FormatSnippetAsync below
            if (replaced && string.IsNullOrWhiteSpace(newStr) && !string.IsNullOrWhiteSpace(oldStr) &&
                            !(step.Change ?? "").Contains("remove", StringComparison.OrdinalIgnoreCase) &&
                            !(step.Change ?? "").Contains("delete", StringComparison.OrdinalIgnoreCase))
            {
                var err = "newString is empty but the step does not ask to remove/delete code. " +
                          "This would delete the matched block. Provide the replacement code in newString.";
                await EmitLog(emitSse, "warn", $"Edit attempt {attempt + 1}/{MaxAttempts} failed for {relPath}: {err}", ct: ct);
                history.Add((oldStr!, newStr ?? "", err));
                if (string.Equals(AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""), AgentTextUtilities.NormalizeLineEndings(lastOld), StringComparison.Ordinal)) stuckCount++;
                else { stuckCount = 0; lastOld = AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""); }
                if (stuckCount >= 2) goto RecordFailure;
                continue;
            }
            if (!string.IsNullOrWhiteSpace(oldStr) &&
                AgentTextUtilities.NormalizeLineEndings(oldStr) == AgentTextUtilities.NormalizeLineEndings(newStr ?? ""))
            {
                var checkOldStr = AgentTextUtilities.NormalizeLineEndings(oldStr);
                var checkFileContent = AgentTextUtilities.NormalizeLineEndings(fileContent);
                if (checkFileContent.Contains(checkOldStr, StringComparison.Ordinal))
                {
                    await EmitLog(emitSse, "info", $"✓ Already done (no-op): {relPath} — code already present", ct: ct);
                    var r2 = new Dictionary<string, object?>
                    {
                        ["index"] = stepIndex,
                        ["type"] = "edit",
                        ["status"] = "skipped",
                        ["path"] = relPath,
                        ["reason"] = "already done",
                        ["planItemIndex"] = planItemIndex
                    };
                    if (emitSse) await SendSse(Response, "step", r2, ct);
                    allResults.Add(r2);
                    await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct, projectRoot: projectRoot);
                    return stepIndex + 1;
                }
                await EmitLog(emitSse, "warn", $"No-op edit for {relPath}: LLM produced no change. Retrying.", ct: ct);
                history.Add((oldStr!, newStr ?? "", "LLM produced a no-op edit — oldString and newString are identical. If the step asks to REMOVE code, set newString to an empty string or empty array."));
                if (string.Equals(AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""), AgentTextUtilities.NormalizeLineEndings(lastOld), StringComparison.Ordinal)) stuckCount++;
                else { stuckCount = 0; lastOld = AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""); }
                if (stuckCount >= 2) goto RecordFailure;
                continue;
            }
            if (!fromFormatC &&
                !string.IsNullOrWhiteSpace(oldStr) && !string.IsNullOrWhiteSpace(newStr) &&
                oldStr!.Length > newStr!.Length * 4 &&
                !oldStr.TrimStart().StartsWith('}') &&
                oldStr.Length > 200)
            {
                var err = $"oldString ({oldStr.Length}ch, {oldLines}L) is >4x newString ({newStr.Length}ch, {newLines}L) — " +
                    "LLM likely replaced a method instead of inserting alongside it. " +
                    "Use insertion pattern: oldString = the 1-2 anchor lines right BEFORE where the new code goes, " +
                    "newString = anchor lines (unchanged) + your new code after them.";
                await EmitLog(emitSse, "warn", err, new { step }, ct: ct);
                history.Add((oldStr, newStr ?? "", err));
                continue;
            }
            if (!string.IsNullOrWhiteSpace(oldStr) && !string.IsNullOrWhiteSpace(newStr))
            {
                string? wipeReason = null;
                if (wipeReason == null)
                {
                    var repaired = AgentTextUtilities.CollapseExcessiveBlankLines(newStr!);
                    if (repaired != newStr)
                    {
                        await EmitLog(emitSse, "warn",
                            $"Auto-repaired excessive blank lines in {relPath} — collapsed {((newStr?.Split('\n').Length ?? 0) - repaired.Split('\n').Length)} blank lines",
                            ct: ct);
                        newStr = repaired;
                    }
                }
                if (wipeReason == null)
                {
                    wipeReason = ContentEditHeuristics.DetectDuplicatePropertyAddition(oldStr!, newStr!, relPath);
                }
                if (wipeReason == null)
                {
                    // The mirror of the duplicate-key guard: an aggregation edit (the change
                    // description names a grouping verb) whose grouped output has FEWER entries
                    // than the flat input silently dropped rows — reject before it lands.
                    wipeReason = ContentEditHeuristics.DetectDroppedEntriesInGroupedOutput(oldStr!, newStr!, step.Change);
                }
                if (wipeReason == null)
                {
                    // For template edits, resolve bound properties against the sibling component
                    // (.component.ts) as well as the HTML itself: a binding referencing a member
                    // genuinely declared in the component must never false-positive as a typo of
                    // a similar template token, and a typo of a real TS member is caught even
                    // when the real name appears nowhere in the template.
                    var relatedTsContent = TryReadComponentTsContent(relPath, projectRoot);
                    wipeReason = ContentEditHeuristics.DetectHallucinatedProperties(oldStr!, newStr!, fileContent, relPath, relatedTsContent);
                }
                if (wipeReason == null)
                {
                    wipeReason = AgentEditHeuristics.DetectWrongSectionEdit(oldStr!, fileContent, step.Change ?? "", relPath);
                }
                if (wipeReason == null)
                {
                    wipeReason = await DetectMissingCreateTableAsync(oldStr!, newStr!, fileContent, relPath, projectRoot, emitSse, ct);
                }
                var changeLower = (step.Change ?? "").ToLowerInvariant();
                if (wipeReason == null && (changeLower.StartsWith("remove ") || changeLower.StartsWith("delete ")))
                {
                    var elementMatch = Regex.Match(step.Change ?? "", @"(?:remove|delete)\s+(?:the\s+)?([\w-]+)\s+(?:div|element|span|button|table|code|block|method)", RegexOptions.IgnoreCase);
                    if (elementMatch.Success)
                    {
                        var elementKeyword = elementMatch.Groups[1].Value;
                        if (!string.IsNullOrWhiteSpace(elementKeyword) &&
                            newStr!.Contains(elementKeyword, StringComparison.OrdinalIgnoreCase) &&
                            newStr.Length > oldStr!.Length)
                        {
                            wipeReason = $"ATOMIC STEP VIOLATION — Step asks to REMOVE '{elementKeyword}', but newString contains it and is longer than oldString. " +
                                         "For a REMOVE step, newString MUST be the anchor lines ONLY (the element is deleted). Do NOT add the element anywhere else in newString.";
                        }
                    }
                }
                if (wipeReason != null)
                {
                    if (wipeReason.StartsWith("SIGNATURE CHANGE", StringComparison.Ordinal))
                    {
                        var existingFn = CheckMethodExistsInFile(fileContent, newStr!);
                        if (existingFn != null)
                        {
                            await EmitLog(emitSse, "info",
                                $"✓ Already done: {relPath} — Function '{existingFn}' already exists in the file (guard detected attempted re-insertion)", ct: ct);
                            var r = new Dictionary<string, object?>
                            {
                                ["index"] = stepIndex,
                                ["type"] = "edit",
                                ["status"] = "skipped",
                                ["path"] = relPath,
                                ["reason"] = $"Function '{existingFn}' already exists in the file",
                                ["planItemIndex"] = planItemIndex
                            };
                            if (emitSse) await SendSse(Response, "step", r, ct);
                            allResults.Add(r);
                            await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct, projectRoot: projectRoot);
                            return stepIndex + 1;
                        }
                    }
                    await EmitLog(emitSse, "warn",
                        $"Guard triggered for {relPath}: {wipeReason}",
                        new
                        {
                            oldPreview = oldStr!.Length > 200 ? oldStr!.Substring(0, 200) + "..." : oldStr,
                            newPreview = newStr!.Length > 200 ? newStr!.Substring(0, 200) + "..." : newStr
                        },
                        ct: ct);
                    history.Add((oldStr!, newStr, wipeReason));
                    _ = Task.Run(async () =>
                    {
                        await _editKnowledge.RecordOutcomeAsync(projectRoot, relPath, step.Change ?? "", prompt ?? step.Change ?? "", oldStr, newStr, outcome: "abandoned", reason: wipeReason, ct);
                    }, CancellationToken.None);
                    if (string.Equals(AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""), AgentTextUtilities.NormalizeLineEndings(lastOld), StringComparison.Ordinal)) stuckCount++;
                    else { stuckCount = 0; lastOld = AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""); }
                    if (stuckCount >= 2) goto RecordFailure;
                    continue;
                }
            }
            if (!replaced)
            {
                var err = matchError ?? "oldString not found verbatim";
                if (!string.IsNullOrEmpty(snippet)) err += $". Nearby: {snippet}";
                // if (step.LineNumber > 0)
                // {
                //     var fileLinesArr = fileContent.Split('\n');
                //     var lineIdx = Math.Max(0, step.LineNumber - 1);
                //     var start = Math.Max(0, lineIdx - 10);
                //     var end = Math.Min(fileLinesArr.Length - 1, lineIdx + 10);
                //     var actualCode = string.Join("\n", fileLinesArr.Skip(start).Take(end - start + 1));
                //     err += $"\n⚠ TARGET LINE MISMATCH: The step targets line {step.LineNumber}, but your oldString was not found there. Here is the ACTUAL code around line {step.LineNumber}:\n```\n{actualCode}\n```\nCopy your oldString VERBATIM from this block.";
                // }
                await EmitLog(emitSse, "warn",
                    $"Edit attempt {attempt + 1}/{MaxAttempts} failed for {relPath}: {err}",
                    new { step }, ct: ct);
                // SURROUNDING-LINE RE-ANCHOR (deterministic, zero-LLM): a small plan oldString
                // (2-3 lines) that failed verbatim is first retried against each surrounding
                // line — shifted up/down, extended by the line above/below (the file gained a
                // line the plan missed), or trimmed of a stale first/last line. Only a unique,
                // confident alignment is applied, so tiny anchors get re-anchored cheaply
                // instead of escalating to a full LLM re-resolve (which risks the whole-section
                // rewrite failure mode). Falls through to the whole-file fuzzy match below when
                // no surrounding alignment is confident enough.
                // IDENTIFIER-GROUNDED RE-ANCHOR (deterministic, zero-LLM) — FIRST choice: the
                // LLM/plan oldString failed verbatim because of whitespace or line drift (the
                // benchmark-22 loop: the same 80-char oldString re-emitted 3× until the slot
                // valve threw). Instead of escalating (which repeats the same drifted anchor),
                // find where the anchor's OWN identifier actually lives in the file and rebuild
                // the block from the REAL file text — real indentation, real surrounding lines.
                // Grounded on an identifier the oldString itself names, it can never select an
                // unrelated block (a "tradeNotifsCount" line) the tolerant matcher would.
                var idReanchor = AnchorEditHeuristics.TryIdentifierAnchoredReanchor(
                    fileContent, oldStr!, step.LineNumber);
                var surroundingReanchor = idReanchor == null
                    ? AnchorEditHeuristics.TrySurroundingLineReanchor(
                        fileContent, oldStr!, step.LineNumber, step.Change)
                    : null;
                var correctedBlock = idReanchor?.correctedBlock
                    ?? surroundingReanchor?.correctedBlock
                    ?? AnchorEditHeuristics.BuildExactMatchBlock(fileContent, oldStr!, step.LineNumber, step.Change);
                if (correctedBlock != null && correctedBlock != oldStr)
                {
                    if (idReanchor != null)
                    {
                        await EmitLog(emitSse, "info",
                            $"🎯 Identifier-grounded re-anchor for {relPath}: found the anchor's own identifier at file line {idReanchor.Value.startLineIdx + 1} — rebuilt the block from the real file text (real indentation) instead of escalating to the LLM", ct: ct);
                    }
                    else if (surroundingReanchor != null)
                    {
                        await EmitLog(emitSse, "info",
                            $"↔ Surrounding-line re-anchor for {relPath}: matched {surroundingReanchor.Value.score} of {oldStr!.Split('\n').Length} anchor line(s) at file line {surroundingReanchor.Value.startLineIdx + 1} — applying file-exact block instead of escalating", ct: ct);
                    }
                    var relevanceKeywords = AgentDiscovery.ExtractDisambiguationKeywords(step.Change);
                    // The description keywords usually name the thing being ADDED (e.g. "Add
                    // moviesTodoCount property") — which lives in newString, not in the
                    // oldString-anchored correctedBlock. Check BOTH, or a perfectly good re-
                    // anchor of adjacent context is rejected as "unrelated" (the live
                    // navigation.component.ts movie-count failure).
                    var isRelevant = relevanceKeywords.Count == 0 ||
                        relevanceKeywords.Any(k =>
                            correctedBlock.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                            (newStr != null && newStr.Contains(k, StringComparison.OrdinalIgnoreCase)));
                    if (!isRelevant)
                    {
                        await EmitLog(emitSse, "warn",
                            $"Self-heal candidate for {relPath} shares no keywords with the change description " +
                            $"[{string.Join(", ", relevanceKeywords)}] — refusing to apply. Treating as already done.", ct: ct);
                        history.Add((oldStr!, newStr ?? "", "Self-heal candidate rejected: no relevant match found in file — target already absent."));
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(newStr) &&
                        correctedBlock.Split('\n').Length > newStr.Split('\n').Length + 4)
                    {
                        await EmitLog(emitSse, "warn",
                            $"Self-heal aborted: verbatim block ({correctedBlock.Split('\n').Length}L) is much larger than newString ({newStr.Split('\n').Length}L). LLM likely deleted code.", ct: ct);
                    }
                    else
                    {
                        await EmitLog(emitSse, "info",
                            $"Self-healing: found exact block in file (scoped to line {step.LineNumber}):\n{correctedBlock}",
                            ct: ct);
                        var corrIdx2 = fileContent.IndexOf(correctedBlock, StringComparison.Ordinal);
                        var indentNewStr = newStr ?? string.Empty;
                        if (corrIdx2 >= 0)
                        {
                            var allFileLines = fileContent.Split('\n');
                            var lineIdx2 = fileContent[..corrIdx2].Count(c => c == '\n');
                            indentNewStr = IndentReplacement(allFileLines, lineIdx2, indentNewStr,
                                isHtmlDomFile: HtmlDomEditor.IsHtmlDomFile(relPath));
                            indentNewStr = AgentDiffUtilities.ReconstructFromVerbatimDiff(correctedBlock, indentNewStr);
                        }
                        var (replaced2, newContent2, _, _) =
                            AnchorEditHeuristics.TryReplaceSafe(fileContent, correctedBlock, indentNewStr, step.LineNumber, step.Change);
                        if (replaced2)
                        {
                            var (approved2, _, _) =
                                VerifyEdit(correctedBlock, newStr ?? "", fileContent, newContent2, fromFormatC, relPath);
                            if (approved2)
                            {
                                await System.IO.File.WriteAllTextAsync(
                                    fullPath, newContent2, Encoding.UTF8, ct);
                                await EmitLog(emitSse, "success",
                                    $"✓ Edited {relPath} (self-healed)", step, ct: ct);
                                newStr = correctedBlock;
                                newContent = newContent2;
                                goto AfterSelfHeal;
                            }
                        }
                    }
                }
                history.Add((oldStr!, newStr ?? "", err));
                if (string.Equals(
                    AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""),
                    AgentTextUtilities.NormalizeLineEndings(lastOld),
                    StringComparison.Ordinal)) stuckCount++;
                else { stuckCount = 0; lastOld = AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""); }
                if (stuckCount >= 2)
                {
                    await EmitLog(emitSse, "error",
                        $"LLM keeps producing the same oldString — aborting {relPath}",
                        ct: ct);
                    goto RecordFailure;
                }
                continue;
            }
            var shrinkThreshold = fromFormatC ? 0.02 : 0.1;
            if (!string.IsNullOrEmpty(newStr) && oldStr!.Length > 0 && (double)newStr.Length / oldStr!.Length < shrinkThreshold)
            {
                var err = $"newString too short ({(double)newStr.Length / oldStr.Length:P1} of oldString length) — possible content deletion";
                await EmitLog(emitSse, "warn",
                    $"Edit attempt {attempt + 1}/{MaxAttempts} failed for {relPath}: {err}", ct: ct);
                history.Add((oldStr!, newStr ?? "", err));
                if (string.Equals(
                    AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""),
                    AgentTextUtilities.NormalizeLineEndings(lastOld),
                    StringComparison.Ordinal)) stuckCount++;
                else { stuckCount = 0; lastOld = AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""); }
                if (stuckCount >= 2) goto RecordFailure;
                continue;
            }
            var newStrLines = newStr?.Split('\n') ?? Array.Empty<string>();
            for (var i = 0; i < newStrLines.Length - 1; i++)
            {
                var line = newStrLines[i];
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("/*"))
                    continue;
                var singleQuoteCount = 0;
                var doubleQuoteCount = 0;
                for (var j = 0; j < line.Length; j++)
                {
                    if (line[j] == '\\' && j + 1 < line.Length) { j++; continue; }
                    if (line[j] == '\'') singleQuoteCount++;
                    if (line[j] == '"') doubleQuoteCount++;
                }
                if ((singleQuoteCount % 2 != 0 || doubleQuoteCount % 2 != 0) &&
                    (line.Contains("'\\n") || line.Contains("'\\t") || line.Contains("'\\r") ||
                     line.Contains("\"\\n") || line.Contains("\"\\t") || line.Contains("\"\\r")))
                {
                    var err = "Syntax error: Unclosed string literal. You split a string containing '\\n' across multiple lines. " +
                              "If a line contains a newline character inside a string literal (e.g. `parts.join('\\n')`), " +
                              "you MUST output the `\\n` escaped inside that single array element. NEVER split a line of code across multiple array elements.";
                    await EmitLog(emitSse, "warn",
                        $"Edit attempt {attempt + 1}/{MaxAttempts} failed for {relPath}: {err}", ct: ct);
                    history.Add((oldStr!, newStr ?? "", err));
                    if (string.Equals(
                        AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""),
                        AgentTextUtilities.NormalizeLineEndings(lastOld),
                        StringComparison.Ordinal)) stuckCount++;
                    else { stuckCount = 0; lastOld = AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""); }
                    if (stuckCount >= 2) goto RecordFailure;
                    goto continueResolveLoop;
                }
            }
            if (StructureEditHeuristics.ShouldBounceGarbageAnchor(oldStr, isDeterministicEdit))
            {
                var err = $"oldString starts with a standalone '}}' (just a closing brace) — it includes the previous method's closing brace. " +
                    "Set oldString to start AT the target method declaration, not before it.";
                await EmitLog(emitSse, "warn",
                    $"Edit attempt {attempt + 1}/{MaxAttempts} failed for {relPath}: {err}", ct: ct);
                history.Add((oldStr!, newStr ?? "", err));
                if (string.Equals(
                    AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""),
                    AgentTextUtilities.NormalizeLineEndings(lastOld),
                    StringComparison.Ordinal)) stuckCount++;
                else { stuckCount = 0; lastOld = AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""); }
                if (stuckCount >= 2) goto RecordFailure;
                continue;
            }
        continueResolveLoop:;
            if (replaced && !string.IsNullOrWhiteSpace(newStr))
            {
                if (fileExt == ".cs")
                {
                    var fixedSqlContent = AgentCodeFormatting.AutoFixSqlWhitespace(newContent);
                    if (fixedSqlContent != newContent)
                    {
                        await EmitLog(emitSse, "info", $"Pre-verify SQL fix: corrected spacing in {relPath}", ct: ct);
                        newContent = fixedSqlContent;
                        newStr = AgentCodeFormatting.AutoFixSqlWhitespace(newStr);
                    }
                }
                if (Path.GetExtension(relPath).Equals(".py", StringComparison.OrdinalIgnoreCase))
                {
                    var fixedPyContent = AgentCodeFormatting.AutoFixPythonStatements(newContent, relPath);
                    if (fixedPyContent != newContent)
                    {
                        await EmitLog(emitSse, "info", $"Pre-verify Python fix: split single-line statements in {relPath}", ct: ct);
                        newContent = fixedPyContent;
                        newStr = AgentCodeFormatting.AutoFixPythonStatements(newStr, relPath);
                    }
                }
            }
            if ((relPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                 relPath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(newStr))
            {
                var tsRelPath2 = Path.ChangeExtension(relPath, ".ts");
                var tsFullPath2 = Path.GetFullPath(
                    Path.Combine(projectRoot, tsRelPath2.Replace('/', Path.DirectorySeparatorChar)));
                if (System.IO.File.Exists(tsFullPath2))
                {
                    var tsContent2 = await System.IO.File.ReadAllTextAsync(tsFullPath2, Encoding.UTF8, ct);
                    var definedProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (System.Text.RegularExpressions.Match m in Regex.Matches(
                        tsContent2, @"^\s*(\w+)\s*:\s*\w+(?:<[^>]*>)?\[\]?\s*=\s*(?!null|undefined)([^;]+);",
                        RegexOptions.Multiline))
                    {
                        definedProps.Add(m.Groups[1].Value);
                    }
                    foreach (System.Text.RegularExpressions.Match m in Regex.Matches(
                        tsContent2, @"^\s*(\w+)\s*:\s*\w+\s*=\s*(?:''|""""|0|false|true|new\s+\w+|\[|\{);",
                        RegexOptions.Multiline))
                    {
                        definedProps.Add(m.Groups[1].Value);
                    }
                    if (definedProps.Count > 0)
                    {
                        var fixed2 = newStr;
                        foreach (var p in definedProps)
                        {
                            fixed2 = Regex.Replace(fixed2,
                                $@"\b{p}\?\s*\.\s*",
                                $"{p}.",
                                RegexOptions.IgnoreCase);
                        }
                        if (fixed2 != newStr)
                        {
                            await EmitLog(emitSse, "info",
                                $"Stripped unnecessary ?. from defined properties in {relPath}", ct: ct);
                            newStr = fixed2;
                            var (replaced3, newContent3, _, _) =
                                AnchorEditHeuristics.TryReplaceSafe(fileContent, oldStr!, fixed2, step.LineNumber, step.Change);
                            if (replaced3)
                            {
                                newContent = newContent3;
                                await System.IO.File.WriteAllTextAsync(
                                    fullPath, newContent3, Encoding.UTF8, ct);
                            }
                        }
                    }
                }
            }
            var serviceContractError = ValidateIntroducedServiceCalls(
                fileExt, fileContent, newContent, projectRoot);
            bool bypassVerifyForAppend = !string.IsNullOrWhiteSpace(newStr) &&
                AgentTextUtilities.NormalizeLineEndings(newContent).Contains(AgentTextUtilities.NormalizeLineEndings(newStr), StringComparison.Ordinal);
            // Deterministic batches synthesize a marker newStr ("(deterministic batch: N edits)") —
            // the batch path already validated every sub-edit via exact TryReplaceSafe matches, so
            // content verification is satisfied by construction. (LLM batches keep their existing
            // marker semantics untouched.)
            var isDeterministicBatch = newStr?.StartsWith("(deterministic batch:", StringComparison.Ordinal) == true;
            var (approved, verifyReason, _) =
                serviceContractError != null
                    ? (false, serviceContractError, 2)
                    : bypassVerify || bypassVerifyForAppend || isDeterministicBatch
                    ? (true, isDeterministicBatch
                        ? "Bypassed verify for deterministic batch — each sub-edit matched exactly"
                        : "Bypassed verify for successful append/insertion", 100) :
                (string.IsNullOrEmpty(oldStr) && string.IsNullOrWhiteSpace(fileContent))
                ? (true, "Bypassed verify for empty file insertion", 100)
                : VerifyEdit(oldStr!, newStr ?? "", fileContent, newContent, fromFormatC, relPath);
            if (!approved && verifyReason.Contains("SQL whitespace collapsed", StringComparison.OrdinalIgnoreCase))
            {
                var correctedContent = AgentCodeFormatting.AutoFixSqlWhitespace(newContent);
                if (correctedContent != newContent)
                {
                    var correctedNewStr = AgentCodeFormatting.AutoFixSqlWhitespace(newStr ?? "");
                    (approved, verifyReason, _) =
                        VerifyEdit(oldStr!, correctedNewStr, fileContent, correctedContent, fromFormatC, relPath);
                    if (approved)
                    {
                        newContent = correctedContent;
                        newStr = correctedNewStr;
                        await EmitLog(emitSse, "info",
                            $"SQL whitespace auto-corrected in {relPath}", ct: ct);
                    }
                    else if (verifyReason.Contains("identical", StringComparison.OrdinalIgnoreCase))
                    {
                        verifyReason =
                            "SQL whitespace auto-fix made your newCode IDENTICAL to the existing code — " +
                            "you reproduced the original method body without implementing the new functionality. " +
                            "Write a DIFFERENT method body that adds the logic described in CHANGE REQUIRED.";
                        newStr = correctedNewStr;
                    }
                }
            }
            if (!approved)
            {
                await EmitLog(emitSse, "warn", $"Verify failed for {relPath}: {verifyReason}", ct: ct);
                history.Add((oldStr!, newStr ?? "", verifyReason));
                var isIdenticalError =
                    verifyReason.Contains("IDENTICAL to the existing code", StringComparison.OrdinalIgnoreCase) ||
                    verifyReason.Contains("identical after normalization", StringComparison.OrdinalIgnoreCase);
                var trackBy = isIdenticalError
                    ? AgentTextUtilities.NormalizeLineEndings(newStr ?? "")
                    : AgentTextUtilities.NormalizeLineEndings(oldStr ?? "");
                if (string.Equals(trackBy, AgentTextUtilities.NormalizeLineEndings(lastOld), StringComparison.Ordinal))
                {
                    stuckCount++;
                }
                else
                {
                    stuckCount = 0;
                    lastOld = trackBy;
                }
                if (stuckCount >= 2) { goto RecordFailure; }
                continue;
            }
            if (!string.IsNullOrWhiteSpace(newStr)
                && !newStr.StartsWith("(deterministic batch:", StringComparison.Ordinal)
                && !newContent.Contains(AgentTextUtilities.NormalizeLineEndings(newStr), StringComparison.Ordinal))
            {
                var trimmedNew = string.Join("\n",
                    AgentTextUtilities.StripLineLeadingWhitespace(AgentTextUtilities.NormalizeLineEndings(newStr))
                        .Split('\n').Select(l => l.TrimEnd()));
                var trimmedContent = string.Join("\n",
                    AgentTextUtilities.StripLineLeadingWhitespace(newContent)
                        .Split('\n').Select(l => l.TrimEnd()));
                if (!trimmedContent.Contains(trimmedNew, StringComparison.Ordinal))
                {
                    var verr = "Replacement produced mismatched content — " +
                               "oldString matched wrong location";
                    await EmitLog(emitSse, "warn",
                        $"Verify failed for {relPath}: {verr}", step, ct: ct);
                    history.Add((oldStr!, newStr, verr));
                    if (string.Equals(
                        AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""),
                        AgentTextUtilities.NormalizeLineEndings(lastOld),
                        StringComparison.Ordinal)) stuckCount++;
                    else { stuckCount = 0; lastOld = AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""); }
                    if (stuckCount >= 2) goto RecordFailure;
                    continue;
                }
            }
            if (Path.GetExtension(relPath) is ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs")
            {
                newContent = NormalizeTypeScriptObjectLiterals(newContent);
            }
            if (Path.GetExtension(relPath).Equals(".py", StringComparison.OrdinalIgnoreCase))
            {
                var pyKeywords = "print|return|if|for|while|def|class|import|from|with|try|except|finally|raise|yield|assert|del|global|nonlocal|pass|break|continue";
                newContent = Regex.Replace(newContent, $@"\)\s+({pyKeywords})\b", ")\n$1");
                if (!string.IsNullOrWhiteSpace(newStr))
                {
                    newStr = Regex.Replace(newStr, $@"\)\s+({pyKeywords})\b", ")\n$1");
                }
            }
            var cssExt = Path.GetExtension(relPath).ToLowerInvariant();
            if (cssExt == ".css" || cssExt == ".scss" || cssExt == ".less")
            {
                newContent = LlmCssCleaner.Clean(newContent);
                if (!string.IsNullOrWhiteSpace(newStr))
                {
                    newStr = LlmCssCleaner.Clean(newStr);
                    newStr = LlmCssCleaner.FixCssStructure(newStr);
                }
                newContent = LlmCssCleaner.FixCssStructure(newContent);
            }
            preEditContent ??= fileContent;
            await SaveEditWithUndoAsync(fullPath, newContent, relPath, projectRoot, preEditContent, ct);
            if (fileExt == ".cs" && !string.IsNullOrWhiteSpace(newStr))
            {
                // For deterministic batches, newStr is the "(deterministic batch: ...)" MARKER —
                // scan the ACTUAL member snippets instead, so missing-type stub generation for
                // batch-added members behaves like single-edit adds (the marker text would scan
                // as a silent no-op).
                var scanNewCode = newStr.StartsWith("(deterministic batch:", StringComparison.Ordinal) && step.Edits is { Count: > 0 }
                    ? string.Join("\n", step.Edits.Select(e => e.NewString))
                    : newStr;
                var missing = ScanMissingTypes(newContent, scanNewCode);
                var stubsToAdd = new List<string>();
                foreach (var t in missing)
                {
                    var definition = FindTypeDefinitionInContext(t, explorationContext ?? "");
                    if (definition != null)
                    {
                        stubsToAdd.Add(definition);
                        continue;
                    }
                    if (t.EndsWith("Dto", StringComparison.OrdinalIgnoreCase) ||
                        t.EndsWith("DTO", StringComparison.Ordinal))
                        continue;
                    stubsToAdd.Add($"public class {t}\n{{\n}}");
                }
                if (stubsToAdd.Count > 0)
                {
                    newContent += "\n" + string.Join("\n\n", stubsToAdd);
                    await SaveEditWithUndoAsync(
                        fullPath, newContent, relPath, projectRoot, preEditContent, ct);
                    await EmitLog(emitSse, "info",
                        $"Appended missing type(s): {string.Join(", ", stubsToAdd.Select(s => ExtractTypeNameForLog(s)))}", ct: ct);
                }
            }
            if (fileExt == ".cs")
            {
                var writtenContent = System.IO.File.ReadAllText(fullPath, Encoding.UTF8);
                var beforeErrors = CountRoslynErrors(writtenContent);
                var fixedContent = AgentTextUtilities.PostEditCSharpFixup(writtenContent);
                if (fixedContent != writtenContent)
                {
                    var afterErrors = CountRoslynErrors(fixedContent);
                    if (afterErrors > beforeErrors)
                    {
                        await EmitLog(emitSse, "warn",
                            $"Post-edit fixup would introduce {afterErrors - beforeErrors} new error(s) in {relPath} — skipping fixup", ct: ct);
                        await EmitLog(emitSse, "warn",
                            $"  Before: {beforeErrors} errors, After: {afterErrors} errors", ct: ct);
                    }
                    else
                    {
                        await SaveEditWithUndoAsync(fullPath, fixedContent, relPath, projectRoot, preEditContent, ct);
                        newContent = fixedContent;
                        await EmitLog(emitSse, "info",
                            $"Post-edit fixup applied to {relPath} (verbatim escapes / DTO wrappers / doubled braces)", ct: ct);
                    }
                }
            }
            if (fileExt == ".cs" && !string.IsNullOrWhiteSpace(newContent))
            {
                try
                {
                    var syntaxTree = CSharpSyntaxTree.ParseText(newContent);
                    var diagnostics = syntaxTree.GetDiagnostics()
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Take(10)
                        .ToList();
                    if (diagnostics.Count > 0)
                    {
                        var preEditErrorCount = 0;
                        if (!string.IsNullOrWhiteSpace(preEditContent))
                        {
                            try
                            {
                                var preTree = CSharpSyntaxTree.ParseText(preEditContent);
                                preEditErrorCount = preTree.GetDiagnostics()
                                    .Count(d => d.Severity == DiagnosticSeverity.Error);
                            }
                            catch { }
                            var errorLines = diagnostics
                                .Select(d => $"  L{d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}")
                                .ToList();
                            if (diagnostics.Count > preEditErrorCount)
                            {
                                await SaveEditWithUndoAsync(fullPath, preEditContent, relPath, projectRoot, preEditContent, ct);
                                var roslynErr =
                                    $"ROSLYN SYNTAX ERRORS INTRODUCED — {diagnostics.Count} error(s) in {relPath} after edit " +
                                    $"(file had {preEditErrorCount} pre-existing). Edit REVERTED.\n" +
                                    string.Join("\n", errorLines) +
                                    "\n\nThe edit introduced C# compile errors. Common causes:\n" +
                                    "  • Duplicate method/endpoint definitions (the method may already exist from a prior step)\n" +
                                    "  • Missing closing braces — check that newCode has balanced { }\n" +
                                    "  • Wrong insertion point — the anchor method may not be where you think it is\n" +
                                    "  • The newCode contains a DIFFERENT method than what CHANGE REQUIRED asks for.\n" +
                                    "Re-read CHANGE REQUIRED carefully and produce the CORRECT method.";
                                await EmitLog(emitSse, "warn", roslynErr, ct: ct);
                                history.Add((oldStr!, newStr ?? "", roslynErr));
                                if (string.Equals(
                                    AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""),
                                    AgentTextUtilities.NormalizeLineEndings(lastOld),
                                    StringComparison.Ordinal)) stuckCount++;
                                else { stuckCount = 0; lastOld = AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""); }
                                if (stuckCount >= 2) goto RecordFailure;
                                continue;
                            }
                            else
                            {
                                await EmitLog(emitSse, "warn",
                                    $"Roslyn syntax errors in {relPath} after edit ({diagnostics.Count} found, {preEditErrorCount} pre-existing — not blocking):" +
                                    Environment.NewLine + string.Join(Environment.NewLine, errorLines), ct: ct);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    await EmitLog(emitSse, "warn",
                        $"Roslyn parse failed for {relPath}: {ex.Message}", ct: ct);
                }
            }
            if ((fileExt is ".js" or ".ts" or ".tsx" or ".jsx" or ".mjs" or ".cjs") && !string.IsNullOrWhiteSpace(newContent))
            {
                try
                {
                    var newUnbalanced = StructureEditHeuristics.HasUnbalancedBraces(newContent);
                    if (newUnbalanced)
                    {
                        var preBraceOk = !string.IsNullOrWhiteSpace(preEditContent) && !StructureEditHeuristics.HasUnbalancedBraces(preEditContent);
                        if (preBraceOk)
                        {
                            await EmitLog(emitSse, "warn",
                                $"Unbalanced braces detected in {relPath} after edit — deferring to the LLM verifier to decide whether the edit should be retained or abandoned.", ct: ct);
                        }
                        else
                        {
                            await EmitLog(emitSse, "warn",
                                $"Unbalanced braces in {relPath} after edit (pre-existing — not blocking)", ct: ct);
                        }
                    }
                }
                catch (Exception ex)
                {
                    await EmitLog(emitSse, "warn",
                        $"Brace balance check failed for {relPath}: {ex.Message}", ct: ct);
                }
            }
            if (!string.IsNullOrWhiteSpace(newStr) &&
                (fileExt is ".css" or ".scss" or ".less"))
            {
                var (mergedCss, mergeWarnings) = MergeDuplicateCssRules(newContent);
                if (mergedCss != newContent)
                {
                    newContent = mergedCss;
                    foreach (var w in mergeWarnings)
                        await EmitLog(emitSse, "warn", w, ct: ct);
                    await EmitLog(emitSse, "info",
                        $"Merged duplicate CSS selectors in {relPath}", ct: ct);
                }
                // Deterministic missing-dot repair: a selector naming a class defined in the
                // same file WITHOUT the '.' prefix (e.g. 'favoritesTable tbody tr td a {' when
                // the file has '.favouritesTable') silently never matches. The LLM verifier
                // cannot catch it — it only sees old/new snippets, never the real file — so
                // repair it against the ACTUAL content.
                var (repairedCss, repairWarnings) = CssSelectorRepair.RepairBareClassSelectors(newContent);
                if (repairedCss != newContent)
                {
                    newContent = repairedCss;
                    foreach (var w in repairWarnings)
                        await EmitLog(emitSse, "warn", w, ct: ct);
                    await EmitLog(emitSse, "info",
                        $"🔧 Auto-repaired bare CSS class selector(s) in {relPath}", ct: ct);
                }
            }
            if (!string.IsNullOrWhiteSpace(newStr) && !fromFormatC)
            {
                var fileLines = newContent.Split('\n');
                var preLines = !string.IsNullOrWhiteSpace(preEditContent) ? preEditContent.Split('\n') : null;
                var changed = false;
                foreach (var nLine in newStr.Split('\n'))
                {
                    var trimmed = nLine.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed)) continue;
                    var trimmedLower = trimmed.ToLowerInvariant();
                    var isCommentOrString = trimmedLower.StartsWith("//") || trimmedLower.StartsWith("/*") ||
                        trimmedLower.StartsWith("*") || trimmedLower.StartsWith("'") || trimmedLower.StartsWith("\"");
                    if (isCommentOrString) continue;
                    for (var i = 0; i < fileLines.Length; i++)
                    {
                        if (fileLines[i].Contains(trimmed, StringComparison.Ordinal))
                        {
                            if (preLines != null && i < preLines.Length && preLines[i] == fileLines[i])
                            {
                                break;
                            }
                            var before = fileLines[i];
                            var fixedLine = Regex.Replace(fileLines[i], @"\b(\w+)\s+\(", "$1(");
                            var stillComment = fixedLine.TrimStart().StartsWith("//") || fixedLine.TrimStart().StartsWith("/*") || fixedLine.TrimStart().StartsWith("*");
                            if (stillComment) break;
                            if (fixedLine != before)
                            {
                                fileLines[i] = fixedLine;
                                changed = true;
                            }
                            break;
                        }
                    }
                }
                if (changed)
                {
                    newContent = string.Join("\n", fileLines);
                    await System.IO.File.WriteAllTextAsync(fullPath, newContent, Encoding.UTF8, ct);
                }
            }
        AfterSelfHeal:
            if (!string.IsNullOrWhiteSpace(newStr) && !string.IsNullOrWhiteSpace(preEditContent))
            {
                if (onActivity != null)
                {
                    try { await onActivity("verifying"); } catch { }
                }
                // ── Deterministic Python syntax gate (pre-check) ──────────────────────
                // The real interpreter outranks the LLM verifier for .py files. A file that
                // compiles is provably free of syntax errors (the LLM's "syntax error"
                // abandonments on FORMAT C inserts are false positives), and a file that does
                // NOT compile is rejected with the ACTUAL compiler error instead of a vague
                // "syntax error in function definition" the retry cannot act on. Verdicts:
                //   keep    — edited file compiles cleanly
                //   abandon — edited file fails to compile and the failure is NEW (pre-edit
                //             content compiled, or failed with a DIFFERENT error)
                //   neutral — pre-edit content fails with the IDENTICAL error (pre-existing
                //             breakage, not caused by this edit) → LLM verdict decides, but
                //             the real error is surfaced in the reason
                var pyGate = (Verdict: "", Error: "");
                // Deterministic (server-synthesized) edits skip the gate — their content is
                // generated mechanically and already trusted.
                if (!isDeterministicEdit &&
                    relPath.EndsWith(".py", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(newContent))
                {
                    pyGate = await TryPythonSyntaxGateAsync(newContent, preEditContent, ct);
                }
                var (decisions, reasons, scores, needsExtraStepFlags, deterministicPlaceholderReject) =
                    isDeterministicEdit
                        ? (new List<string> { "keep", "keep", "keep" },
                           new List<string> { "Deterministic edit — old/new synthesized server-side; verification bypassed", string.Empty, string.Empty },
                           new List<int> { 100, 100, 100 },
                           new List<bool> { false, false, false }, false)
                        : pyGate.Verdict == "abandon"
                            ? (new List<string> { "abandon" },
                               new List<string> { "🐍 Deterministic Python syntax check FAILED — " + pyGate.Error },
                               new List<int> { 0 },
                               new List<bool> { false }, false)
                            : await RunLlmVerifyRoundsAsync(newStr, oldStr, relPath, prompt, step.Change,
                                preEditContent, newContent, emitSse, ct, attemptScores, explorationContext ?? "",
                                plan, planItemIndex, sqlMigrationNote, causalContext, runContext);
                stepNeedsExtraStep = needsExtraStepFlags.Any(f => f);
                stepExtraStepReason = reasons.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
                var roundsDone = decisions.Count;
                var keepCount = 0;
                var scoreSum = 0;
                for (int r = 0; r < roundsDone; r++)
                {
                    if (decisions[r] == "keep") keepCount++;
                    scoreSum += scores[r];
                }
                var avgScore = scoreSum / roundsDone;
                var llmGateDecision = keepCount >= 2 ? "keep" : "abandon";
                var truncatedReasons = new List<string>(reasons.Count);
                for (int r = 0; r < reasons.Count; r++)
                    truncatedReasons.Add(reasons[r].Length > 80 ? reasons[r][..80] + "…" : reasons[r]);
                var llmGateReason = $"Rounds: [{string.Join(", ", decisions)}] " +
                    $"scores [{string.Join(", ", scores)}] — final: {llmGateDecision} (avg {avgScore}/100). " +
                    $"Reasons: [{string.Join(" | ", truncatedReasons)}]";
                var llmGateScore = avgScore;
                // ── Deterministic Python syntax gate (post-LLM override) ───────────────
                // The compile result outranks the LLM verifier: a compiling file is KEPT even
                // when the LLM abandons on a syntax claim; a non-compiling file is rejected
                // with the real compiler error and cannot be flipped back to keep by the
                // heuristic overrides below.
                var pythonGateHardReject = false;
                if (pyGate.Verdict == "keep")
                {
                    if (llmGateDecision == "abandon")
                    {
                        await EmitLog(emitSse, "info",
                            $"  🐍 Deterministic Python gate: edited {relPath} compiles cleanly (python -m py_compile) — overriding LLM abandon to KEEP", ct: ct);
                        llmGateDecision = "keep";
                        llmGateScore = Math.Max(llmGateScore, 85);
                        llmGateReason = "🐍 Deterministic Python syntax gate PASSED (edited file compiles) — " + llmGateReason;
                    }
                }
                else if (pyGate.Verdict == "abandon")
                {
                    pythonGateHardReject = true;
                    llmGateDecision = "abandon";
                    llmGateScore = 0;
                    llmGateReason = "🐍 Deterministic Python syntax check FAILED — " + pyGate.Error;
                }
                else if (pyGate.Verdict == "neutral" && !string.IsNullOrEmpty(pyGate.Error))
                {
                    llmGateReason = "🐍 Python compile error is PRE-EXISTING (identical in the pre-edit file, unchanged by this edit): " + pyGate.Error + " — " + llmGateReason;
                }
                attemptScores.Add((attempt + 1, llmGateScore, llmGateReason, newStr));
                if (llmGateScore > bestScore)
                {
                    bestScore = llmGateScore;
                    bestAttempt = attempt;
                }
                await EmitLog(emitSse, "info",
                    $"  📊 Attempt {attempt + 1} multi-round: [{string.Join(", ", scores)}] avg: {avgScore}/100 (best so far: {bestScore}/100) — {llmGateDecision}",
                    new { attempt = attempt + 1, scores, averageScore = avgScore, decision = llmGateDecision, reason = llmGateReason },
                    ct: ct);
                if (plan?.Plan != null && planItemIndex >= 0)
                {
                    var extraStepCount = needsExtraStepFlags.Count(f => f);
                    if (extraStepCount >= 2)
                    {
                        var allReasons = string.Join(" ", reasons);
                        var methodMatches = Regex.Matches(allReasons,
                            @"(?:'([a-zA-Z]\w+)'|`([a-zA-Z]\w+)`|method\s+'?([a-zA-Z]\w+)'?|vm\.([a-zA-Z]\w+)|([a-zA-Z]\w+)\s*\()",
                            RegexOptions.IgnoreCase);
                        var mentionedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (System.Text.RegularExpressions.Match m in methodMatches)
                        {
                            for (int g = 1; g < m.Groups.Count; g++)
                            {
                                if (m.Groups[g].Success)
                                    mentionedNames.Add(m.Groups[g].Value);
                            }
                        }
                        var missingName = mentionedNames.FirstOrDefault();
                        if (!string.IsNullOrEmpty(missingName))
                        {
                            var fext = Path.GetExtension(relPath).ToLowerInvariant();
                            var targetFile = relPath;
                            if (fext is ".html" or ".htm")
                            {
                                var tsCandidate = Path.ChangeExtension(relPath, ".ts");
                                var jsCandidate = Path.ChangeExtension(relPath, ".js");
                                var tsPath = System.IO.Path.GetFullPath(Path.Combine(projectRoot, tsCandidate));
                                var jsPath = System.IO.Path.GetFullPath(Path.Combine(projectRoot, jsCandidate));
                                if (System.IO.File.Exists(tsPath)) targetFile = tsCandidate;
                                else if (System.IO.File.Exists(jsPath)) targetFile = jsCandidate;
                            }
                            var inheritedRefs = step.ReferenceFiles ?? new List<string>();
                            // If the referencing step carried an HTML sibling (e.g. music.component.html),
                            // also hand the resolver the .ts twin — the component logic (like isPopupPanelOpen)
                            // lives in the .ts file, not the template.
                            if (Path.GetExtension(targetFile).Equals(".ts", StringComparison.OrdinalIgnoreCase))
                            {
                                var htmlRef = inheritedRefs.FirstOrDefault(r =>
                                    r.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
                                if (htmlRef != null)
                                {
                                    var tsTwin = Path.ChangeExtension(htmlRef, ".ts");
                                    if (!inheritedRefs.Contains(tsTwin, StringComparer.OrdinalIgnoreCase))
                                        inheritedRefs = inheritedRefs.Concat(new[] { tsTwin }).ToList();
                                }
                            }
                            // Skip the synthetic step if the method already exists on disk — the
                            // resolver may have been seeded with a stale snapshot, and re-inserting
                            // would create a duplicate (compile error).
                            var syntheticTargetPath = System.IO.Path.GetFullPath(Path.Combine(projectRoot, targetFile.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                            if (System.IO.File.Exists(syntheticTargetPath) &&
                                MethodNameExistsInFile(await System.IO.File.ReadAllTextAsync(syntheticTargetPath, Encoding.UTF8, ct), missingName))
                            {
                                await EmitLog(emitSse, "info",
                                    $"  ⏭ Verifier synthetic step skipped: {missingName}() already exists on disk in {targetFile}", ct: ct);
                            }
                            else
                            {
                                var syntheticStep = new PlanStep
                                {
                                    File = targetFile,
                                    Change = $"Add implementation of {missingName}() method referenced in {System.IO.Path.GetFileName(relPath)} — mirror the pattern used by the referencing component's sibling file if one exists",
                                    TargetSymbol = missingName,
                                    LineNumber = 0,
                                    OldString = null,
                                    NewString = null,
                                    ReferenceFiles = inheritedRefs,
                                };
                                plan.Plan.Insert(planItemIndex + 1, syntheticStep);
                                await EmitLog(emitSse, "info",
                                    $"  🔄 Verifier ({extraStepCount}/3 needsExtraStep): auto-added synthetic step to implement {missingName}() in {targetFile}",
                                    ct: ct);
                            }
                        }
                        if (llmGateDecision == "abandon" && !deterministicPlaceholderReject && !pythonGateHardReject)
                        {
                            // Only keep when the edit is structurally sound (score 50+) —
                            // a broken edit should retry, not be kept with a synthetic step.
                            if (llmGateScore >= 50)
                            {
                                llmGateDecision = "keep";
                                llmGateScore = Math.Max(llmGateScore, 70);
                            }
                        }
                    }
                }
                if (llmGateDecision == "abandon" && oldStr != null && !deterministicPlaceholderReject && !pythonGateHardReject)
                {
                    // The needsExtraStep override ONLY applies when the edit is structurally
                    // sound (score 50+) but references a method that needs integration. If the
                    // verifier flagged syntax errors, undefined variables, or broken code (score
                    // below 50), the override must NOT keep a broken edit — the retry loop
                    // needs to regenerate a correct version.
                    var methodDecls = CountNewMethodsInNewCode(newStr ?? "", oldStr);
                    var isSyntacticallyBroken = llmGateScore < 50 ||
                        Regex.IsMatch(llmGateReason ?? "", @"syntax.error|incomplete.comment|undefined.variable|missing.method|undecl", RegexOptions.IgnoreCase);
                    if (methodDecls > 0 && !isSyntacticallyBroken)
                    {
                        await EmitLog(emitSse, "info",
                            $"  🔄 Verifier abandoned but edit adds at least {methodDecls} new method(s) — " +
                            $"overriding to keep with needsExtraStep=true", ct: ct);
                        llmGateDecision = "keep";
                        llmGateScore = Math.Max(llmGateScore, 70);
                        stepNeedsExtraStep = true;
                        stepExtraStepReason = "Need to integrate new method(s) with existing code";
                    }
                }
                if (llmGateDecision == "abandon")
                {
                    var abandonDiffPath = await SaveEditWithUndoAsync(fullPath, preEditContent, relPath, projectRoot, preEditContent, ct);
                    await EmitLog(emitSse, "warn",
                        $"⟲ LLM verify: ABANDON edit on {relPath} (score {llmGateScore}/100) — {llmGateReason}. " +
                        $"Reverted to pre-edit state; retrying. " +
                        $"Prior attempts: {attemptScores.Count}, best score: {bestScore}/100",
                        new { step, reason = llmGateReason, score = llmGateScore, bestScore, attemptScores }, ct: ct);
                    if (emitSse)
                    {
                        await SendSse(Response, "step", new
                        {
                            index = stepIndex,
                            type = "edit",
                            status = "verify-abandoned",
                            path = relPath,
                            reason = llmGateReason,
                            score = llmGateScore,
                            bestScore,
                            attempt = attempt + 1,
                            planItemIndex,
                            diffs = abandonDiffPath != null ? new List<string> { abandonDiffPath } : new List<string>()
                        }, ct);
                    }
                    var abandonError =
                        $"LLM verify ABANDONED (score {llmGateScore}/100): {llmGateReason}\n" +
                        $"═══ FAILED CODE THAT WAS REVERTED (score {llmGateScore}/100) ═══\n" +
                        $"{TruncateForLlm(newStr, 600)}\n" +
                        $"═══ END FAILED CODE ═══\n" +
                        $"DO NOT reproduce this code. It scored {llmGateScore}/100 because: {llmGateReason}.\n" +
                        $"Try a DIFFERENT approach. ";
                    if (llmGateReason.Contains("signature", StringComparison.OrdinalIgnoreCase))
                        abandonError += "PRESERVE the original method signature (return type, name, parameters). Only change the BODY.";
                    else if (llmGateReason.Contains("cache", StringComparison.OrdinalIgnoreCase) ||
                             llmGateReason.Contains("guard", StringComparison.OrdinalIgnoreCase))
                        abandonError += "PRESERVE all cache/guard lines (if/return/map.has/map.get/map.set). Only add NEW logic alongside them.";
                    else if (llmGateReason.Contains("invent", StringComparison.OrdinalIgnoreCase) ||
                             llmGateReason.Contains("undefined", StringComparison.OrdinalIgnoreCase) ||
                             llmGateReason.Contains("not exist", StringComparison.OrdinalIgnoreCase))
                        abandonError += "Use ONLY methods/properties that already exist in the file. Do NOT invent new identifiers.";
                    else
                        abandonError += $"Address this specific issue: {llmGateReason}";
                    if (attemptScores.Count > 0)
                    {
                        var trend = attemptScores.Count >= 2 && llmGateScore > attemptScores[^2].score
                            ? "↑ improving"
                            : attemptScores.Count >= 2 && llmGateScore < attemptScores[^2].score
                                ? "↓ getting worse — change strategy significantly"
                                : "→ stagnant — try a fundamentally different approach";
                        abandonError += $"\nScore trend: {trend}. Best so far: {bestScore}/100 on attempt {bestAttempt + 1}.";
                    }
                    history.Add((oldStr!, newStr ?? "", abandonError));
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _editKnowledge.RecordOutcomeAsync(projectRoot, relPath, step.Change ?? "", prompt ?? step.Change ?? "",
                             oldStr, newStr, outcome: "abandoned", reason: $"LLM verify (score {llmGateScore}): {llmGateReason}", ct);
                        }
                        catch { }
                    }, CancellationToken.None);
                    if (string.Equals(
                        AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""),
                        AgentTextUtilities.NormalizeLineEndings(lastOld),
                        StringComparison.Ordinal)) stuckCount++;
                    else { stuckCount = 0; lastOld = AgentTextUtilities.NormalizeLineEndings(oldStr ?? ""); }
                    if (attemptScores.Count >= 3)
                    {
                        var last3 = attemptScores.TakeLast(3).Select(a => a.score).ToList();
                        var allLow = last3.All(s => s < 40);
                        var noImprovement = last3.Distinct().Count() <= 2;
                        if (allLow && noImprovement)
                        {
                            await EmitLog(emitSse, "warn",
                                $"Score stagnation detected: last 3 attempts scored [{string.Join(", ", last3)}] — " +
                                $"entering replanning cycle with failure context",
                                ct: ct);
                            goto RecordFailure;
                        }
                    }
                    if (stuckCount >= 3)
                    {
                        await EmitLog(emitSse, "error",
                            $"LLM verify abandoned {stuckCount}x in a row for {relPath} — treating as failure",
                            ct: ct);
                        goto RecordFailure;
                    }
                    continue;
                }
                else if (llmGateDecision == "keep")
                {
                    await EmitLog(emitSse, "success",
                        $"✓ LLM verify: KEEP edit on {relPath} — score {llmGateScore}/100 — {llmGateReason}",
                        ct: ct);
                    if (emitSse)
                    {
                        await SendSse(Response, "step", new
                        {
                            index = stepIndex,
                            type = "edit",
                            status = "verify-kept",
                            path = relPath,
                            reason = llmGateReason,
                            score = llmGateScore,
                            planItemIndex
                        }, ct);
                    }
                    var regionFmtExt = Path.GetExtension(relPath)?.ToLowerInvariant();
                    if (regionFmtExt is ".css" or ".scss" or ".less" &&
                        !string.IsNullOrWhiteSpace(newStr) && CodeFormatterService.CanFormat(relPath))
                    {
                        try
                        {
                            var beforeFmt = newContent;
                            var fmtContent = await CssRegionFormatter.FormatAcceptedEditRegionAsync(relPath, newContent, oldStr, newStr, ct);
                            if (fmtContent != beforeFmt)
                            {
                                newContent = fmtContent;
                                await EmitLog(emitSse, "info",
                                    $"Formatted accepted edit region in {relPath} via external formatter", ct: ct);
                            }
                        }
                        catch (Exception ex)
                        {
                            await EmitLog(emitSse, "warn",
                                $"External formatter failed for {relPath}: {ex.Message} — skipping", ct: ct);
                        }
                    }
                    await System.IO.File.WriteAllTextAsync(fullPath, newContent, Encoding.UTF8, ct);
                }
                else
                {
                    await EmitLog(emitSse, "warn",
                        $"⚠ LLM verify returned error (defaulting to keep): {llmGateReason}", ct: ct);
                }
            }
            return await CompleteSuccessfulEditAsync(
                attempt, history, oldStr, newStr, step, prompt, projectRoot, relPath, fullPath,
                plan, planItemIndex, stepNeedsExtraStep, stepExtraStepReason, stepExtraStepFile,
                emitSse, ct, allResults, stepIndex, cardId, fileExt, runContext,
                preEditContent, newContent);
        }
    RecordFailure:
        return await HandleStepFailureAsync(history, attemptScores, bestScore, bestAttempt, relPath,
            step, stepIndex, planItemIndex, cardId, allResults, emitSse, ct, replanDepth,
            plan, prompt, attachedFiles, projectRoot, onActivity, runContext);
    }

    /// <summary>
    /// Rejects newly introduced calls to service methods that are not present in the referenced
    /// TypeScript service or that pass a provably invalid number of arguments. LLM verification
    /// is intentionally not used for this contract check: it must not approve invented APIs.
    /// </summary>
    public static string? ValidateIntroducedServiceCalls(
        string fileExtension, string oldContent, string newContent, string projectRoot)
    {
        if (fileExtension is not (".ts" or ".tsx" or ".js" or ".jsx")) return null;
        if (string.IsNullOrWhiteSpace(newContent)) return null;

        var callRegex = new Regex(@"\bthis\.(?<service>\w*Service)\.(?<method>\w+)\s*\(",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var oldCalls = callRegex.Matches(oldContent ?? "")
            .Cast<Match>()
            .Select(m => $"{m.Groups["service"].Value}.{m.Groups["method"].Value}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (Match call in callRegex.Matches(newContent))
        {
            var serviceProperty = call.Groups["service"].Value;
            var methodName = call.Groups["method"].Value;
            var callKey = $"{serviceProperty}.{methodName}";
            if (oldCalls.Contains(callKey)) continue;

            var className = char.ToUpperInvariant(serviceProperty[0]) + serviceProperty[1..];
            var servicePath = Directory.EnumerateFiles(projectRoot, "*.ts", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "node_modules" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) &&
                    !f.Contains(Path.DirectorySeparatorChar + "dist" + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(f =>
                {
                    try
                    {
                        var text = System.IO.File.ReadAllText(f, Encoding.UTF8);
                        return Regex.IsMatch(text,
                            $@"\b(?:export\s+)?(?:abstract\s+)?class\s+{Regex.Escape(className)}\b",
                            RegexOptions.IgnoreCase);
                    }
                    catch { return false; }
                });
            if (servicePath == null)
                return $"Introduced call this.{serviceProperty}.{methodName}(...), but the local service class '{className}' could not be found. Do not invent service APIs; resolve the imported service before editing.";

            string serviceContent;
            try { serviceContent = System.IO.File.ReadAllText(servicePath, Encoding.UTF8); }
            catch { continue; }

            var methodRegex = new Regex(
                $@"(?:^|[;{{}}\n])\s*(?:(?:public|private|protected|static|async|readonly|override)\s+)*" +
                $@"{Regex.Escape(methodName)}\s*(?:<[^>]*>)?\s*\((?<params>[^)]*)\)",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
            var declaration = methodRegex.Match(serviceContent);
            if (!declaration.Success)
            {
                var propertyMethodRegex = new Regex(
                    $@"\b{Regex.Escape(methodName)}\s*=\s*(?:async\s*)?\((?<params>[^)]*)\)\s*=>",
                    RegexOptions.IgnoreCase);
                declaration = propertyMethodRegex.Match(serviceContent);
            }
            if (!declaration.Success)
                return $"Introduced call this.{serviceProperty}.{methodName}(...), but {className}.ts has no method named '{methodName}'. Re-read the service API and use an existing method/signature.";

            var args = ExtractCallArguments(newContent, call.Index + call.Length);
            if (args == null) continue;
            var argumentCount = args.Count;
            var parameterCount = CountParameterParts(declaration.Groups["params"].Value);
            var requiredParameterCount = CountRequiredParameterParts(declaration.Groups["params"].Value);
            if (argumentCount < requiredParameterCount || argumentCount > parameterCount)
            {
                return $"Introduced call this.{serviceProperty}.{methodName} with {argumentCount} argument(s), but {className}.ts declares {requiredParameterCount}-{parameterCount} parameter(s). Use the exact existing service signature.";
            }
        }
        return null;
    }

    private static List<string>? ExtractCallArguments(string content, int start)
    {
        var depth = 1;
        var begin = start;
        var quote = '\0';
        var escaped = false;
        for (var i = start; i < content.Length; i++)
        {
            var c = content[i];
            if (quote != '\0')
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == quote) quote = (char)0;
                continue;
            }
            if (c == '\'' || c == '"' || c == '`') { quote = c; continue; }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0)
            {
                var raw = content[begin..i].Trim();
                if (raw.Length == 0) return new List<string>();
                return SplitTopLevelArguments(raw);
            }
        }
        return null;
    }

    private static List<string> SplitTopLevelArguments(string raw)
    {
        var result = new List<string>();
        var start = 0;
        var parens = 0;
        var brackets = 0;
        var braces = 0;
        var quote = '\0';
        var escaped = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (quote != '\0')
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == quote) quote = (char)0;
                continue;
            }
            if (c == '\'' || c == '"' || c == '`') { quote = c; continue; }
            if (c == '(') parens++; else if (c == ')') parens--;
            else if (c == '[') brackets++; else if (c == ']') brackets--;
            else if (c == '{') braces++; else if (c == '}') braces--;
            else if (c == ',' && parens == 0 && brackets == 0 && braces == 0)
            {
                result.Add(raw[start..i].Trim());
                start = i + 1;
            }
        }
        result.Add(raw[start..].Trim());
        return result;
    }

    private static int CountParameterParts(string raw) =>
        string.IsNullOrWhiteSpace(raw) ? 0 : SplitTopLevelArguments(raw).Count;

    private static int CountRequiredParameterParts(string raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? 0
            : SplitTopLevelArguments(raw).Count(p =>
            {
                var parameter = p.Trim();
                if (parameter.StartsWith("...", StringComparison.Ordinal) || parameter.Contains("=", StringComparison.Ordinal))
                    return false;
                var colon = parameter.IndexOf(':');
                var name = colon >= 0 ? parameter[..colon].Trim() : parameter;
                return !name.EndsWith("?", StringComparison.Ordinal);
            });

    // ── Deterministic Python syntax gate ─────────────────────────────────────────────
    /// <summary>
    /// Verdicts for the edited .py content vs. the pre-edit content:
    /// "keep" — edited file compiles (python -m py_compile exit 0);
    /// "abandon" — edited file fails AND the failure is new (pre-edit compiled, or failed
    ///             with a different error);
    /// "neutral" — pre-edit failed with the IDENTICAL normalized error (pre-existing
    ///             breakage this edit neither caused nor fixed) or python is unavailable.
    /// </summary>
    private async Task<(string Verdict, string Error)> TryPythonSyntaxGateAsync(
        string newContent, string preEditContent, CancellationToken ct)
    {
        try
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), "weaver-pyverify");
            Directory.CreateDirectory(tmpDir);
            var editedPath = Path.Combine(tmpDir, "edited_" + Guid.NewGuid().ToString("N") + ".py");
            await System.IO.File.WriteAllTextAsync(editedPath, newContent, new UTF8Encoding(false), ct);
            var (editedCode, editedErr) = await RunPyCompileAsync(editedPath, ct);
            try { System.IO.File.Delete(editedPath); } catch { }
            if (editedCode == 0) return ("keep", "");
            if (editedCode == -2) return ("neutral", ""); // python unavailable — no gate
            var editedMsg = NormalizePyCompileError(editedErr, editedPath);
            if (string.IsNullOrWhiteSpace(preEditContent))
                return ("abandon", editedMsg);
            var prePath = Path.Combine(tmpDir, "pre_" + Guid.NewGuid().ToString("N") + ".py");
            await System.IO.File.WriteAllTextAsync(prePath, preEditContent, new UTF8Encoding(false), ct);
            var (preCode, preErr) = await RunPyCompileAsync(prePath, ct);
            try { System.IO.File.Delete(prePath); } catch { }
            var preMsg = NormalizePyCompileError(preErr, prePath);
            var verdict = PythonSyntaxGateVerdict(editedCode, editedMsg, !string.IsNullOrWhiteSpace(preEditContent), preCode, preMsg);
            return (verdict, verdict == "abandon" ? editedMsg : "");
        }
        catch
        {
            return ("neutral", "");
        }
    }

    /// <summary>Pure verdict derivation for the Python syntax gate — unit-tested.
    /// Codes: 0 = compiles, -1 = compile failed, -2 = interpreter unavailable.</summary>
    public static string PythonSyntaxGateVerdict(
        int editedCode, string editedErrorNormalized,
        bool hasPreEditContent, int preCode, string preErrorNormalized)
    {
        if (editedCode == 0) return "keep";
        if (editedCode == -2) return "neutral"; // interpreter unavailable — no gate
        if (!hasPreEditContent) return "abandon";
        if (preCode == -2) return "neutral";
        if (preCode != 0 && string.Equals(preErrorNormalized, editedErrorNormalized, StringComparison.OrdinalIgnoreCase))
            return "neutral"; // identical pre-existing error — not caused by this edit
        return "abandon";
    }

    /// <summary>exit code 0 = compiles; -1 = compile failed; -2 = interpreter unavailable.</summary>
    private static async Task<(int Code, string Error)> RunPyCompileAsync(string filePath, CancellationToken ct)
    {
        var exe = ScraperEnvironmentService.StaticInterpreterAvailable("python")
            ? "python"
            : ScraperEnvironmentService.StaticInterpreterAvailable("python3")
                ? "python3"
                : null;
        if (exe == null) return (-2, "");
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "-m py_compile \"" + filePath + "\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return (-2, "");
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            if (!p.WaitForExit(20000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (-2, "");
            }
            var err = await stderrTask;
            var outp = await stdoutTask;
            return (p.ExitCode, (err + outp).Trim());
        }
        catch
        {
            return (-2, "");
        }
    }

    /// <summary>Strips the temp file path + collapses whitespace so pre-edit and edited
    /// compile errors compare equal when the SAME error appears at the SAME source line.</summary>
    public static string NormalizePyCompileError(string error, string filePath)
    {
        var msg = error.Replace(filePath.Replace('\\', '/'), "<file>").Replace(filePath, "<file>");
        msg = Regex.Replace(msg, @"\r\n?", "\n");
        msg = Regex.Replace(msg, @"\s+", " ").Trim();
        return msg;
    }
}
