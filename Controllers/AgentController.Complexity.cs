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
    /// <summary>
    /// Maps a complexity score (0-100) to the PLANNING/EDITING thinking token cap.
    /// This is deliberately SMALL and separate from the user's overall Thinking Max
    /// Tokens slider (which stays the budget for accumulated deep reasoning): the
    /// per-step pre-plan reasoning only needs to decide WHAT the step must contain
    /// (file, anchors, code to copy) — a tight 120-840 token cap keeps it concise
    /// and on-point instead of producing long meandering walls of text. Scales
    /// linearly with complexity so harder tasks get more room, but never above 840.
    /// </summary>
    private static int GetPlanningTokenCap(int complexityScore)
    {
        var ratio = Math.Clamp(complexityScore, 0, 100) / 100.0;
        // 120 tokens at trivial (0) complexity → 840 at extremely complex (100).
        var cap = (int)Math.Round(120 + ratio * 720);
        return Math.Clamp(cap, 120, 840);
    }

    /// <summary>
    /// Deterministic micro-assessment that keeps trivial tasks from being over-scored by the
    /// LLM assessor (e.g. "auto focus the new card's input" should be ~5, not 30). Returns a
    /// 0-100 estimate. A low value short-circuits the LLM call entirely.
    /// </summary>
    private static int HeuristicComplexityScore(string prompt)
    {
        var text = (prompt ?? string.Empty).Trim();
        if (text.Length == 0) return 20;
        var lower = text.ToLowerInvariant();

        // Large-task signals: never downgrade these below "Complex" no matter the wording.
        var largeSignals = new[]
        {
            "migration", "database", "new endpoint", "new api", "new route", "architecture", "refactor",
            "new class", "new component", "new subsystem", "authentication", "multi-file",
            "multiple files", "test suite", "deploy", "docker", "websocket", "background service"
        };
        if (largeSignals.Any(lower.Contains)) return 55;

        // Micro-task signals: single-line UI/formatting/comment tweaks with no real logic.
        var microSignals = new[]
        {
            "auto focus", "set focus", "focus the input", "focus the new", "focus new",
            "scroll into view", "scroll to", "add a comment", "add comment", "typo", "fix typo",
            "rename", "change the color", "change color", "add a placeholder", "placeholder",
            "tooltip", "change the label", "change label", "change the text", "change text",
            "capitalize", "uppercase", "make it bold", "make bold", "make it italic",
            "add padding", "increase padding", "center the", "align the", "button text"
        };
        var isMicro = microSignals.Any(lower.Contains);
        if (text.Length <= 40) return isMicro ? 5 : 10;
        if (text.Length <= 120) return isMicro ? 8 : 15;
        if (text.Length <= 350 && isMicro) return 10;
        // Length is a weak complexity signal, but very long detailed prompts are rarely trivial:
        // scale the default upward so the LLM clamp below never under-scores genuinely hard,
        // keyword-less tasks (e.g. a 1500-char feature description gets no ceiling at all).
        if (text.Length > 1500) return 45;
        if (text.Length > 700) return 38;
        return 30;
    }

    /// <summary>
    /// Maps a deterministic heuristic complexity score to an estimated number of atomic steps.
    /// Used when the LLM assessor is unavailable/disabled so the step budget still exists.
    /// </summary>
    private static int HeuristicAtomicStepEstimate(int heuristicScore)
    {
        return heuristicScore switch
        {
            <= 10 => 1,
            <= 25 => 2,
            <= 45 => 3,
            <= 65 => 4,
            <= 85 => 5,
            _ => 6
        };
    }

    /// <summary>
    /// Quick LLM call to assess task complexity (0-100) AND estimate how many atomic steps the
    /// task will need. Only called when extendThinking is enabled. Trivial tasks are decided
    /// deterministically with zero latency; larger tasks use the LLM, anchored by the heuristic
    /// so it cannot wildly over-score small prompts. The atomic-step estimate feeds a planning
    /// budget: once the plan reaches the estimate, the planner is strongly urged to stop rather
    /// than add fluff steps (a hallucination guard against over-planning).
    /// Returns (null, null) only when even the deterministic heuristic could not be computed.
    /// </summary>
    private async Task<(int? score, int? atomicSteps)> AssessComplexityAsync(string prompt, string? cardId, CancellationToken ct, bool heuristicOnly = false)
    {
        if (string.IsNullOrWhiteSpace(cardId)) return (null, null);
        if (_complexityScores.TryGetValue(cardId, out var cached))
        {
            return (cached, _atomicStepEstimates.TryGetValue(cardId, out var cachedSteps) ? cachedSteps : (int?)null);
        }

        try
        {
            var heuristic = HeuristicComplexityScore(prompt);

            // Trivial tasks (typo, comment, focus/scroll tweak, color/label/placeholder change) are
            // decided deterministically — no LLM round-trip, and they can never be mis-scored as
            // "Moderate" like "auto focus the new card input" was.
            if (heuristic <= 10)
            {
                _complexityScores[cardId] = heuristic;
                var microSteps = HeuristicAtomicStepEstimate(heuristic);
                _atomicStepEstimates[cardId] = microSteps;
                return (heuristic, microSteps);
            }

            // Attached-file tasks are scoped to the attached set: the LLM assessor would read the
            // whole task prompt (referencing files outside the attached set), so stick to the
            // deterministic heuristic for thinking-token budgeting instead.
            if (heuristicOnly)
            {
                _complexityScores[cardId] = heuristic;
                var scopedSteps = HeuristicAtomicStepEstimate(heuristic);
                _atomicStepEstimates[cardId] = scopedSteps;
                return (heuristic, scopedSteps);
            }

            var system = "You are a task complexity assessor. Given a coding task description, rate its complexity " +
                "from 0 to 100, AND estimate how many atomic steps (individual file edits / tool calls) it will " +
                "take to complete. Be strict and prefer LOW scores — most small UI, styling and single-function " +
                "changes are 0-10. Anchor points:\n" +
                "0-10: Trivial -- a one-line change, typo/comment, renaming, changing a color/label/placeholder, " +
                "adding auto-focus or scroll-into-view, a tiny CSS tweak. ~1 atomic step.\n" +
                "10-25: Simple -- adding a small property or field, changing a constant, a 2-5 line tweak in one file. 1-2 steps.\n" +
                "25-45: Moderate -- adding a method with real logic, modifying several related lines in one file. 2-3 steps.\n" +
                "45-65: Complex -- multi-file changes, new class/component, API endpoint, new route. 3-5 steps.\n" +
                "65-85: Very complex -- architectural changes, database migrations, new subsystems. 5-8 steps.\n" +
                "85-100: Extremely complex -- full feature implementation, system-wide refactoring. 8+ steps.\n\n" +
                "A task that touches a single function or a few lines of one file is never above 25 and never " +
                "needs more than 2 steps. When in doubt, score LOWER and estimate FEWER steps — an underestimate " +
                "is fine (it just encourages stopping early), an overestimate invites unnecessary busywork.\n\n" +
                "Output ONLY a single JSON object, no explanation, no markdown:\n" +
                "{\"score\": 0-100, \"atomicSteps\": N}";

            var user = $"Rate the complexity and estimate the atomic steps for this coding task " +
                $"(a deterministic heuristic estimated {heuristic}/100 — your score should be close to that " +
                $"unless the task is genuinely harder):\n\n{prompt}";

            var (raw, error) = await CallLlmRawText(system, user, false, ct,
                requestTimeout: _infiniteTimeout, maxTokens: 30);

            if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(raw))
            {
                // LLM unavailable: fall back to the heuristic instead of the full thinking budget.
                _complexityScores[cardId] = heuristic;
                var fallbackSteps = HeuristicAtomicStepEstimate(heuristic);
                _atomicStepEstimates[cardId] = fallbackSteps;
                return (heuristic, fallbackSteps);
            }

            var scoreMatch = Regex.Match(raw, @"""score""" + @"\s*:\s*(\d+)", RegexOptions.IgnoreCase);
            var stepsMatch = Regex.Match(raw, @"""atomicSteps""" + @"\s*:\s*(\d+)", RegexOptions.IgnoreCase);
            var score = heuristic;
            if (scoreMatch.Success && int.TryParse(scoreMatch.Groups[1].Value, out var parsedScore))
            {
                score = Math.Clamp(parsedScore, 0, 100);
                // Guard against the assessor over-scoring small prompts: the ceiling applies only
                // when the heuristic is a *confident* low (short text or micro-task signal). The
                // 30/38/45 defaults — and the 55 large-signal floor — release the LLM to score
                // genuinely complex keyword-less tasks freely.
                if (heuristic <= 20)
                    score = Math.Min(score, heuristic + 20);
            }
            var steps = HeuristicAtomicStepEstimate(score);
            if (stepsMatch.Success && int.TryParse(stepsMatch.Groups[1].Value, out var parsedSteps))
                steps = Math.Clamp(parsedSteps, 1, 30);
            _complexityScores[cardId] = score;
            _atomicStepEstimates[cardId] = steps;
            return (score, steps);
        }
        catch
        {
            return (null, null);
        }
    }
    private static string CapThinking(string raw)
    {
        var t = raw.Trim();
        t = Regex.Replace(t, @"^```[a-zA-Z]*\s*", "");
        t = Regex.Replace(t, @"\s*```$", "");
        const int max = 8000;
        return t.Length <= max ? t : t[..max] + "\n…[truncated]…";
    }

    /// <summary>
    /// Pre-plan extended reasoning: runs BEFORE the planner proposes the next step.
    /// Decides what the step must contain (file, anchors, code to copy) so the
    /// planner authors a concrete, grounded oldString/newString instead of guessing.
    /// Reasoning accumulates in the per-card store keyed "preplan:" so later steps
    /// build on earlier reasoning.
    /// </summary>
    private async Task<string?> ExtendThinkingPrePlanAsync(
        string? cardId, string prompt, string discoveryContext, List<PlanStep> planSoFar,
        string projectRoot, bool emitSse, CancellationToken ct,
        List<string>? attachedFiles = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cardId)) return null;
            var log = _stepThinkingStore.GetOrAdd("preplan:" + cardId, _ => new StringBuilder());
            string previous;
            lock (log) { previous = log.ToString(); }
            // Thinking is carried between steps via this store — left unchecked it racks up context
            // fast. The store accumulates one step's pre-plan reasoning at a time, each capped by
            // GetPlanningTokenCap (raised to 120-840 tokens with complexity). The compaction budget
            // scales with that per-step cap so deeper per-step reasoning is NOT compacted earlier now
            // that the caps are ~20% bigger: ThinkingStepsRetained × per-step-cap restores the same
            // step-count of retained reasoning the pre-raise budget gave at every complexity
            // (e.g. at 30/100 that's 336×15×4 = 20160 chars vs the old fixed 16384, which had held
            // ~14.6 steps before the raise but only ~12.2 after). The user's overall Thinking Max
            // Tokens slider stays the floor (trivial tasks are never compacted earlier than before)
            // and the clamp the ceiling (~4 chars/token).
            const int ThinkingStepsRetained = 15;
            var thinkCfg = await LoadConfigAsync();
            var perStepCapTokens = GetPlanningTokenCap(
                _complexityScores.TryGetValue(cardId ?? "", out var capScore) ? capScore : 100);
            var budgetTokens = Math.Clamp(
                Math.Max(thinkCfg.thinkingMaxTokens, perStepCapTokens * ThinkingStepsRetained), 256, 16384);
            var budgetChars = budgetTokens * 4;
            if (previous.Length > budgetChars)
            {
                if (thinkCfg.compactThinkingContext)
                {
                    var compacted = await CompactThinkingContextAsync(previous, emitSse, ct);
                    if (!string.IsNullOrWhiteSpace(compacted) && compacted.Length < previous.Length)
                    {
                        lock (log) { log.Clear(); log.Append(compacted); }
                        previous = compacted;
                        await EmitLog(emitSse, "metric",
                            $"🧠 Thinking context compacted: {budgetChars}+ chars of accumulated reasoning → {compacted.Length}-char recap (budget {budgetTokens} tokens)", ct: ct);
                    }
                    else
                    {
                        previous = "…[earlier reasoning truncated to thinking budget]…\n" + previous[^Math.Min(budgetChars, previous.Length)..];
                    }
                }
                else
                {
                    previous = "…[earlier reasoning truncated to thinking budget]…\n" + previous[^Math.Min(budgetChars, previous.Length)..];
                }
            }

            // When the user attached specific files, the thinking phase MUST reason only inside
            // those files — never the project at large. Build the RELEVANT PROJECT FILES section
            // from the attached files directly (their full contents), instead of letting the
            // discovery context (which may include skeleton paths / unrelated components) leak in.
            var hasAttached = attachedFiles != null && attachedFiles.Count > 0;
            var related = "";
            if (hasAttached)
            {
                // THINKING MUST NEVER SEE TRUNCATED CONTEXT: attached files are user-chosen and
                // are exactly what the reasoning engine has to work on (e.g. the ngOnInit body a
                // step asks about). Cutting them at a small per-file cap starved the engine of the
                // very method it needed (globe.component.ts was clipped before ngOnInit). Keep the
                // FULL file contents; a single generous guard only protects against pathological
                // attachment sets, never against a normal component file.
                const int attachedMaxChars = 200000;
                var attachedSb = new StringBuilder();
                foreach (var af in attachedFiles!)
                {
                    if (string.IsNullOrWhiteSpace(af)) continue;
                    var afRel = af.Replace('\\', '/');
                    var afFull = Path.GetFullPath(
                        Path.Combine(projectRoot, afRel.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
                    if (!System.IO.File.Exists(afFull)) continue;
                    var afContent = await System.IO.File.ReadAllTextAsync(afFull, Encoding.UTF8, ct);
                    var block = $"### read {afRel}\n```\n{afContent}\n```\n";
                    if (attachedSb.Length + block.Length > attachedMaxChars) break;
                    attachedSb.Append(block);
                }
                related = attachedSb.ToString().Trim();
            }
            else
            {
                related = SelectRelatedFilesForThinking(discoveryContext, prompt, null);
            }
            // Web results (from executed _web_search/_web_fetch steps) are external facts, NOT
            // repo files — so neither SelectRelatedFilesForThinking nor the attached-only rule
            // surfaces them. The reasoning engine MUST see what a search actually returned, or
            // the next step re-invents scraping instead of using the results (the original bug).
            // Append them explicitly in BOTH branches.
            var webSections = ExtractWebResultSectionsForThinking(discoveryContext);
            if (webSections.Length > 0)
                related = (related.Length > 0 ? related + "\n" : "") + webSections;

            var system =
                "You are the deep-reasoning engine of an autonomous coding agent. BEFORE the planner proposes the next step, " +
                "you produce EXTENDED, VERBOSE reasoning in plain prose about what the next step must do. Your reasoning is " +
                "handed to the planner, so be concrete and prescriptive — this is where the exact edit is decided.\n" +
                "Rules:\n" +
                "- Write in first person, like a senior engineer preparing a precise edit.\n" +
                "- Read PREVIOUS REASONING carefully and build on it. Never redo a step already committed in PLAN SO FAR.\n" +
                "- Ground every claim in the RELEVANT PROJECT FILES: quote real identifiers, method names, imports and exact " +
                "template markup. That is your source for what to copy, how it integrates, and which variables come into play. " +
                "Never invent names or guess structure.\n" +
                "- NEVER declare code 'broken' or plan a DELETION of any symbol (method, function, class, variable, property) " +
                "unless you can QUOTE that exact code block verbatim from the file content shown in this prompt (RELEVANT " +
                "PROJECT FILES / ATTACHED FILES). A deletion step is only valid when the code being removed is actually " +
                "visible in the file above. If the task references a method or symbol that is NOT present in the file content, " +
                "do not invent it and do not propose \"remove the broken <name>\" — state plainly that the symbol could not " +
                "be found in the file, and propose the next step grounded in the symbols that actually exist (e.g. create the " +
                "missing method, or edit a real method you can see and quote). Every deletion must be backed by a visible anchor.\n" +
                "- Decide: exactly which file to touch next, what to insert or change, the exact anchor text (oldString) to " +
                "match against the current file, and the exact replacement (newString). Think about what could go wrong — " +
                "missing imports, type errors, breaking call sites, duplicate anchors, CRLF line endings.\n" +
                "- WEB/DATA-FETCH TASKS: when the task needs CURRENT or LIVE data from an external URL/API (search results, " +
                "an API endpoint, the web), the next step is a STEP TOOL, not application code: \"_web_search\" (query in " +
                "change) or \"_web_fetch\" (the exact URL in change). NEVER direct the planner to write a Python/JS/" +
                "PowerShell scraper script (requests.get, urllib, fetch(), Invoke-RestMethod, HttpClient) or a _command that " +
                "runs one — the system fetches the URL itself and auto-writes the harvested data to the demanded file.\n" +
                "- WHOLE-METHOD STEPS USE FORMAT C: When the next step ADDS a brand-new method/function or REPLACES an ENTIRE " +
                "existing method, direct the planner to emit FORMAT C (targetType=\"method\", targetName, insertAfter:true for ADD, " +
                "newCode=COMPLETE new method) instead of oldString/newString — newCode carries only the new method, which saves " +
                "tokens and prevents mid-method truncation. Reserve oldString/newString for small targeted edits inside a method.\n" +
                "- End with a short 'NEXT STEP:' section — 2 to 4 concrete directives: exact file path, exact anchor to find, " +
                "and the code or variables to write. For whole-method ADD/REPLACE steps, state the FORMAT C fields explicitly.\n" +
                "- Output ONLY the reasoning prose. No JSON, no markdown fences, no code blocks.";
            if (hasAttached)
            {
                system += "\n" +
                    "- ATTACHED FILES ONLY: The user attached specific file(s). The RELEVANT PROJECT FILES section below " +
                    "contains ONLY those attached files, in full. Reason exclusively inside them — every file you name, every " +
                    "anchor you propose, every edit you describe MUST live in one of those attached files. Do NOT reference, " +
                    "invent, or speculate about any other file in the project (no global stylesheets, no sibling components, " +
                    "no imports from unrelated files). If the attached files don't contain something you need, say so and " +
                    "plan within what is attached.";
            }
            var user = BuildPrePlanThinkingUserPrompt(prompt, previous, planSoFar, related, hasAttached, webSections);

            var (raw, error) = await CallLlmRawText(system, user, emitSse, ct,
                requestTimeout: _infiniteTimeout,
                // Planning/editing reasoning gets the tight 120-840 token cap — the
                // overall Thinking Max Tokens slider only governs accumulated deep
                // thinking, NOT this per-step pre-plan output.
                maxTokens: GetPlanningTokenCap(
                    _complexityScores.TryGetValue(cardId ?? "", out var cs) ? cs : 100),
                // The cap is intentional, but a silent mid-sentence stop looks like a
                // transport bug to users watching the panel. Opt into the explicit
                // truncation marker so a budget cut is visible (and lands in the
                // reasoning handed to the planner) instead of masquerading as a hang.
                appendTruncationMarker: true);
            if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(raw))
            {
                // Attach the actual reason as detail so the agent panel and meeting UI can
                // surface WHY reasoning was skipped — a false-positive hallucination flag
                // (e.g. dense prose tripping the wall-of-text heuristic) is visible instead
                // of an unexplained warn line.
                var reason = error ?? "empty response";
                await EmitLog(emitSse, "warn",
                    $"Pre-plan reasoning skipped for step {planSoFar.Count + 1}: {reason}",
                    new { reason, rawLength = raw?.Length ?? 0 }, ct: ct);
                return null;
            }
            var cleaned = CapThinking(raw);
            if (cleaned.Length < 20)
            {
                var reason = $"produced only {cleaned.Length} usable char(s) from {raw.Length} raw char(s) after CapThinking";
                await EmitLog(emitSse, "warn",
                    $"Pre-plan reasoning skipped for step {planSoFar.Count + 1}: {reason}",
                    new { reason, rawLength = raw.Length, cleanedLength = cleaned.Length }, ct: ct);
                return null;
            }
            if (emitSse)
                await SendSse(Response, "step-thinking", new
                {
                    text = cleaned,
                    stepIndex = planSoFar.Count,
                    description = "pre-plan",
                    phase = "preplan"
                }, ct);
            await EmitLog(emitSse, "info",
                $"🧠 Pre-plan reasoning — step {planSoFar.Count + 1}: ({cleaned.Length} chars)", ct: ct);
            lock (log)
            {
                log.AppendLine();
                log.AppendLine($"### PRE-PLAN REASONING — STEP {planSoFar.Count + 1} ###");
                log.AppendLine(cleaned);
            }
            return cleaned;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await EmitLog(emitSse, "warn", $"Pre-plan thinking error: {ex.Message}",
                new { reason = ex.Message }, ct: ct);
            return null;
        }
    }

    /// <summary>
    /// Builds the user prompt for the pre-plan deep-reasoning round. Extracted from
    /// ExtendThinkingPrePlanAsync so the WEB RESULTS directive (and its absence for
    /// non-web runs) is unit-testable — mirroring BuildIncrementalStepUserPrompt's
    /// '### WEB RESULTS ARE IN CONTEXT ###' nudge on the planner side.
    /// </summary>
    internal static string BuildPrePlanThinkingUserPrompt(
        string prompt, string previous, List<PlanStep> planSoFar, string related, bool hasAttached, string webSections)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Produce your extended reasoning for the NEXT step the planner should propose.");
        sb.AppendLine();
        sb.AppendLine("### TASK ###");
        sb.AppendLine(string.IsNullOrWhiteSpace(prompt) ? "(no task text available)" : prompt);
        sb.AppendLine();
        sb.AppendLine("### PREVIOUS REASONING ###");
        sb.AppendLine(string.IsNullOrWhiteSpace(previous) ? "(none yet — this is the first step)" : previous);
        sb.AppendLine();
        sb.AppendLine("### PLAN SO FAR (committed steps — do NOT redo these) ###");
        if (planSoFar.Count == 0)
            sb.AppendLine("(none — this is the first step)");
        else
        {
            for (var i = 0; i < planSoFar.Count; i++)
                sb.AppendLine($"  Step {i + 1}: [{planSoFar[i].File}] {planSoFar[i].Change}");
        }
        if (!string.IsNullOrWhiteSpace(related))
        {
            sb.AppendLine();
            sb.AppendLine(hasAttached ? "### ATTACHED FILES (the ONLY files you may touch) ###" : "### RELEVANT PROJECT FILES ###");
            sb.AppendLine(hasAttached
                ? "These are the user's attached files, shown in full. Reason exclusively inside them — every edit must target one of these files."
                : "Files discovered for this task. Use these as your source of truth for what to copy and how to integrate.");
            sb.AppendLine(related);
        }
        // When an earlier _web_search/_web_fetch already ran, its REAL results ride in the
        // RELEVANT PROJECT FILES section under '### WEB RESULTS [...] ###'. Without an explicit
        // directive the reasoning engine silently ignores them and re-derives the research need
        // from the task text — "I need to fetch an AI news article from a reliable source…" —
        // which re-proposes the same _web_search on the very next planner turn (the observed
        // search-loop: every step's thinking starts over from scratch). Tell it the results are
        // authoritative FACTS to consume, never to re-derive. Gated on a committed web step,
        // mirroring the planner-side nudge: sections can only appear once a search/fetch ran.
        if (!string.IsNullOrWhiteSpace(webSections) && planSoFar.Any(s => IsWebStep(s.File)))
        {
            sb.AppendLine();
            sb.AppendLine("### EARLIER WEB SEARCH RESULTS (authoritative — do NOT re-search) ###");
            sb.AppendLine("An earlier _web_search/_web_fetch step ALREADY RAN — its actual returned titles/URLs are in the " +
                "RELEVANT PROJECT FILES section under the '### WEB RESULTS [...] ###' blocks. Those are REAL external " +
                "facts, not something to re-derive. Do NOT propose another _web_search with the same or similar query, " +
                "and do NOT plan scraping/fetching code to re-research what the search already returned. If the task " +
                "still needs an article's full content, direct the planner to _web_fetch a CONCRETE URL listed in those " +
                "results (never an invented one); if it needs a file written, draw its content from the titles/URLs/" +
                "summaries already returned. The task is not a research task anymore — it is a USE-THE-RESULTS task.");
        }
        // OS-filesystem tasks (desktop/downloads/documents/home or an absolute path) have NO repo
        // files to reason about, but the deep-reasoning system prompt is framed entirely around
        // repo edits (anchors, FORMAT C, imports). Without an explicit frame the engine improvises
        // script/infrastructure plans — "create main.py, import requests, pathlib…" — even though
        // the planner's actual next step is a plain _command write or a _web_fetch of a URL from
        // the results. Reframe the reasoning so the thinking aligns with the step. Gated on the
        // same detection as discovery (IsExternalFilesystemTask); attached files mean repo-edit
        // intent and win over the OS frame.
        if (!hasAttached && IsExternalFilesystemTask(prompt))
        {
            sb.AppendLine();
            sb.AppendLine("### OS-FILESYSTEM TASK — OUTSIDE THE REPOSITORY ###");
            sb.AppendLine("This task targets the OS filesystem (desktop/downloads/documents/home or an absolute path), " +
                "NOT the repository. There are no repo files to read, edit, or anchor against, and NO scripts or source " +
                "files to build. Do NOT plan Python/JS/C#/PowerShell scripts, 'main.py', imports, or scraping/fetching " +
                "infrastructure. The next step will be a single concrete _command (a direct file write, or a fetch of a " +
                "URL already listed in the web results) or a _web_fetch of a concrete URL from those results. Draw any " +
                "file content VERBATIM from the web results / task text — never invent article URLs or data.");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Resolves the real file path a step is about to touch. For real edit steps this
    /// is the step's File; for virtual steps (_create_file etc.) it is parsed out of
    /// the Change description, which typically contains the target path.
    /// </summary>
    private static string? ExtractTargetPath(string? file, string? change)
    {
        if (!string.IsNullOrWhiteSpace(file) && !file.StartsWith('_'))
            return file;
        if (!string.IsNullOrWhiteSpace(change))
        {
            var token = change.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(t => t.Contains('/') && t.Contains('.'));
            if (token != null)
                return token.TrimEnd(',', ';', ')', ']', '}', '"', '”', '`');
        }
        return null;
    }

    /// <summary>
    /// Pulls the most relevant sections out of the serialized discovery context
    /// (### read {path} fences) for the step's target file, so extended thinking can
    /// ground itself in real code: what to copy, how it integrates, which variables
    /// and imports come into play. Ranks files by (a) same-directory as the target,
    /// (b) shared name tokens, and (c) whether the file's path or content matches
    /// keywords from the task prompt — e.g. a task saying "like the music component"
    /// surfaces music.component.html even though it lives in a different directory.
    /// All sections up to the budget are included (ordered by relevance), because a
    /// cross-directory reference file is often exactly what the step must copy from.
    /// </summary>
    /// <summary>
    /// Pulls "### WEB RESULTS [...]" sections (appended by AppendWebResultsToDiscoveryContext
    /// after an executed _web_search/_web_fetch step) out of the discovery context so the
    /// deep pre-plan reasoning engine can reference what the search actually returned.
    /// </summary>
    private static string ExtractWebResultSectionsForThinking(string discoveryContext)
    {
        if (string.IsNullOrWhiteSpace(discoveryContext)) return "";
        var sb = new StringBuilder();
        // Stop only at REAL section boundaries (another WEB RESULTS block, a file section, or the
        // end) — a lookahead of bare "### " would truncate web content that itself contains
        // markdown headings (common in fetched articles). The header regex is lazy up to the
        // closing "] ###" so a query containing ']' cannot break the match.
        foreach (Match m in Regex.Matches(discoveryContext, @"### WEB RESULTS \[.*?\] ###[\s\S]*?(?=\n### WEB RESULTS |\n### read |\n### list |\z)"))
        {
            var section = m.Value.TrimEnd();
            if (section.Length > 0) sb.AppendLine(section);
        }
        return sb.ToString().Trim();
    }
    private static string SelectRelatedFilesForThinking(string discoveryContext, string prompt, string? targetPath, int maxChars = 200000)
    {
        if (string.IsNullOrWhiteSpace(discoveryContext)) return "";
        var sections = new List<(string path, string content)>();
        // Match both the discovery format ("### read {path}") and the light-bootstrap attached-file
        // format ("### {path}") so attached files are picked up for thinking/planning too.
        foreach (Match m in Regex.Matches(discoveryContext, @"### (?:read )?(?<path>[^\n`]+)\n```\n(?<content>[\s\S]*?)\n```"))
        {
            var p = m.Groups["path"].Value.Trim();
            var c = m.Groups["content"].Value;
            if (string.IsNullOrWhiteSpace(p) || string.IsNullOrWhiteSpace(c)) continue;
            // Only treat as a file section if it looks like a real file path (dir separator or an
            // extension), so non-file "### SECTION ###" headers followed by a fence aren't misread.
            var looksLikeFile = p.Contains('/') || p.Contains('\\') ||
                                Regex.IsMatch(p, @"[\w.-]+\.\w{1,8}\s*$");
            if (!looksLikeFile) continue;
            sections.Add((p, c));
        }
        if (sections.Count == 0) return "";

        var promptTokens = new HashSet<string>(
            AgentDiscovery.ExtractMeaningfulKeywords(prompt.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        string? targetDir = null;
        var targetName = "";
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            var t = targetPath.Replace('\\', '/');
            var idx = t.LastIndexOf('/');
            targetDir = idx > 0 ? t[..idx] : "";
            targetName = idx >= 0 ? t[(idx + 1)..] : t;
        }
        var targetTokens = targetName.Split(new[] { '.', '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= 2).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var scored = sections
            .Select(s =>
            {
                var p = s.path.Replace('\\', '/');
                var name = p.Contains('/') ? p[(p.LastIndexOf('/') + 1)..] : p;
                var dir = p.Contains('/') ? p[..p.LastIndexOf('/')] : "";
                var score = 0;
                if (targetDir != null && dir.Equals(targetDir, StringComparison.OrdinalIgnoreCase))
                    score += 100;
                else if (targetDir != null && dir.StartsWith(targetDir, StringComparison.OrdinalIgnoreCase))
                    score += 30;
                else if (targetDir != null && targetDir.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                    score += 20;
                var nameTokens = name.Split(new[] { '.', '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(x => x.Length >= 2).ToHashSet(StringComparer.OrdinalIgnoreCase);
                score += nameTokens.Count(targetTokens.Contains) * 50;
                if (targetDir != null && p.Equals(targetPath?.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    score -= 500;
                foreach (var token in promptTokens)
                {
                    if (p.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 25;
                    if (name.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 40;
                }
                var probe = s.content.Length > 6000 ? s.content[..6000] : s.content;
                foreach (var token in promptTokens)
                {
                    if (probe.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 45;
                }
                return (path: p, content: s.content, score);
            })
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        foreach (var s in scored)
        {
            if (sb.Length >= maxChars) break;
            // Full file contents — thinking must reason over the complete file, not a 6k-char
            // prefix that hides the very methods/structure the next step depends on.
            var content = s.content;
            var chunk = $"### read {s.path}\n```\n{content}\n```\n";
            if (sb.Length + chunk.Length > maxChars)
                sb.Append(chunk[..Math.Min(chunk.Length, maxChars - sb.Length)]);
            else
                sb.Append(chunk);
        }
        return sb.ToString().Trim();
    }
}
