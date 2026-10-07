using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using Weaver;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Full-chain corpus for the FORMAT D HTML payload route — the LLM-payload path the
/// plan-based fuzz (FormatCCorpusTests) skips because it builds PlanSteps with
/// AST-resolved oldString directly. Here the step is composed from raw payload fields
/// exactly as the agent does: <c>AgentPlanningPolicy.ParseStepFromJson</c> (the same
/// composition helper the planner pipeline calls) maps targetType/targetName/insertAfter/newCode/fullFile onto a PlanStep — the
/// targetName→TargetSymbol mapping and fullFile→NewString mapping are the load-bearing
/// composition rules. Then the FORMAT D apply chain runs the agent's exact branch
/// (AgentController ~1317 / ~1954): join newCode → StripLeadingClosingDivs → already-done
/// guard → ResolveHtmlAnchor → strategy-keyed composition keyed off the payload's
/// insertAfter/replace flags → TryReplaceSafe. (The real branch also runs the
/// AutoFixPythonStatements + CleanVerbatimStringEscapes no-op passes before stripping;
/// the corpus content is pure HTML so they are provably no-ops and deliberately skipped.)
///
/// The claim locked here: a FORMAT D payload produces the applied file equal to the
/// PURE insertion — the matched anchor replaced by exactly the composed block, the
/// inserted block byte-present and re-resolvable, siblings byte-identical, and
/// re-applying the identical payload hits the already-done guard (no double insert).
/// </summary>
public class FormatDPayloadCorpusTests
{
    // ── The real composition (AgentPlanningPolicy.ParseStepFromJson) ─────────

    /// <summary>
    /// Calls the actual <c>AgentPlanningPolicy.ParseStepFromJson</c> (13 params incl.
    /// optionals) — the same composition the executor uses to map a
    /// raw payload onto a PlanStep. Calling the policy directly keeps the test pinned
    /// to the real
    /// mapping (targetName→TargetSymbol, fullFile→NewString, newCode preservation,
    /// _edit/ prefix normalization) instead of a drifting re-implementation.
    /// </summary>
    private static PlanStep InvokeParseStepFromJson(
        string file, string change, string? targetSymbol, int line, string? oldString, string? newString,
        List<string> refFiles, List<EditPair> edits,
        string? targetType = null, string? targetName = null, bool? insertAfter = null,
        List<string>? newCode = null, string? fullFile = null)
        => AgentPlanningPolicy.ParseStepFromJson(file, change, targetSymbol, line, oldString, newString,
            refFiles, edits, targetType, targetName, insertAfter, newCode, fullFile);

    /// <summary>
    /// Invokes the actual <c>AgentController.HasConcreteEdit</c> (private static) — the
    /// routing gate the executor uses to decide whether a step is a CONCRETE edit payload
    /// (oldString/newString/edits/targetType+targetName+newCode/fullFile) or a
    /// RESOLUTION-DRIVEN step. A FORMAT D payload with an empty/absent newCode MUST return
    /// false — locking the routing claim that the deletion corpus depends on.
    /// </summary>
    private static bool InvokeHasConcreteEdit(PlanStep step)
    {
        var method = typeof(AgentController).GetMethod(
            "HasConcreteEdit", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("HasConcreteEdit not found");
        return (bool)(method.Invoke(null, new object?[] { step }) ?? false);
    }

    // ── Generators ───────────────────────────────────────────────────────────

    /// <summary>A card block at 2-space base indent with a unique heading token.</summary>
    private static string CardBlock(int docIdx, int cardIdx) =>
        $"  <div class=\"card\" id=\"card-{docIdx}-{cardIdx}\">\n" +
        $"    <h2>Heading{docIdx}{cardIdx}</h2>\n" +
        $"    <p>Body {docIdx}-{cardIdx}</p>\n" +
        $"  </div>";

    /// <summary>A full doc: header + 2-4 cards separated by blank lines.</summary>
    private static string BuildDoc(int docIdx, int cardCount)
    {
        var parts = new List<string> { "<main>" };
        for (var k = 0; k < cardCount; k++)
            parts.Add(CardBlock(docIdx, k));
        parts.Add("</main>");
        return string.Join("\n", parts);
    }

    /// <summary>Prefix every line of a block with <paramref name="indent"/> — used to
    /// nest cards inside a container so anchors live at a deeper base indent.</summary>
    private static string IndentBlock(string block, string indent)
    {
        return string.Join("\n",
            block.Replace("\r\n", "\n").Split('\n').Select(l => indent + l));
    }

    /// <summary>A full doc with cards nested in a 2-space-indented container, so each
    /// card sits at 4-space base indent while a 2-space-indented payload is
    /// under-indented for it.</summary>
    private static string BuildNestedDoc(int docIdx, int cardCount)
    {
        var parts = new List<string> { "<main>", "  <div class=\"panel\">" };
        for (var k = 0; k < cardCount; k++)
            parts.Add(IndentBlock(CardBlock(docIdx, k), "  "));
        parts.Add("  </div>");
        parts.Add("</main>");
        return string.Join("\n", parts);
    }

    /// <summary>
    /// Apply a drift variant to a card block so the EXACT link of the anchor-resolution
    /// fallback chain provably fails and a specific deeper link must engage:
    /// 0 → whitespace-widened opening tag (the normalized \S+-token path still matches),
    /// 1 → space inserted before the opening tag's '&gt;' (the normalized \s+ join between
    /// the id token and '&gt;' can no longer match → the collapsed path must engage),
    /// 2 → hallucinated class value, id kept exact (all byte paths fail → the fuzzy
    /// attribute path must pick the id-matching card as the unique best-score winner).
    /// </summary>
    private static string DriftCardBlock(string cleanBlock, int driftMode, int docIdx, int targetIdx)
    {
        return driftMode switch
        {
            // 0 — normalized: extra whitespace between attributes; token sequence unchanged.
            0 => cleanBlock.Replace("<div class=\"card\"", "<div   class=\"card\"   "),
            // 1 — collapsed: '>' separated from the id token — the normalized \s+ join
            //     (`id="card-..."\s+>`) can't match the file's adjacent '>'.
            1 => cleanBlock.Replace($"id=\"card-{docIdx}-{targetIdx}\">",
                                    $"id=\"card-{docIdx}-{targetIdx}\" >"),
            // 2 — fuzzy: hallucinated class (absent from the doc), id kept exact so the
            //     intended card scores 1 (id) vs every sibling's 0 (class + id both drift).
            _ => cleanBlock.Replace("class=\"card\"", "class=\"hallucinated\""),
        };
    }

    // ── FORMAT D compose chain ───────────────────────────────────────────────

    /// <summary>
    /// Mirrors the agent's FORMAT D branch byte-for-byte (AgentController ~1954):
    /// StripLeadingClosingDivs first, then the already-done guard, then anchor
    /// resolution, then the strategy-keyed composition driven by the payload's
    /// insertAfter/replace flags — replace gets the realigned block, insertAfter
    /// appends it after the anchor, insertBefore prefixes the RAW newCode before the
    /// anchor (no realign), exactly like the agent. Returns the composed
    /// (oldString, newString) plus whether the already-done guard short-circuited.
    /// </summary>
    private static (string oldStr, string newStr, bool alreadyDone) ComposeFormatDPayload(
        string html, string targetName, string newCode, string changeDesc,
        bool hasInsertAfter, bool insertAfter, bool hasReplace, bool replaceSection)
    {
        newCode = HtmlDomEditor.StripLeadingClosingDivs(newCode, targetName);
        if (html.Contains(newCode, StringComparison.OrdinalIgnoreCase))
            return ("", "", true);

        var (matchedBlock, _, htmlErr) = HtmlDomEditor.ResolveHtmlAnchor(html, targetName, changeDesc);
        if (matchedBlock == null)
            throw new Xunit.Sdk.XunitException($"anchor did not resolve: {htmlErr}");

        var indented = FuzzHarness.FormatSnippetRealign(matchedBlock, newCode);

        // Agent's exact branch logic:
        //   replace flag OR (insertAfter present and false)          → replace
        //   insertAfter true OR (replace present and false)          → insertAfter
        //   otherwise                                                → insertBefore
        if (replaceSection || (hasInsertAfter && !insertAfter && !hasReplace))
            return (matchedBlock, indented, false); // replace
        if (insertAfter || (hasReplace && !replaceSection && !hasInsertAfter))
            return (matchedBlock, matchedBlock + "\n" + indented, false); // insertAfter
        return (matchedBlock, newCode + "\n" + matchedBlock, false); // insertBefore — raw newCode
    }

    // ── Deterministic tests ──────────────────────────────────────────────────

    [Fact]
    public void ParseStepFromJson_FormatDPayload_MapsFieldsToPlanStep()
    {
        var newCode = new List<string> { "  <div>", "    new card", "  </div>" };
        var step = InvokeParseStepFromJson(
            "gen/partial.html", "add a new card after the target",
            targetSymbol: null, line: 42, oldString: null, newString: null,
            refFiles: new List<string>(), edits: new List<EditPair>(),
            targetType: "html", targetName: "  <div class=\"card\" id=\"card-7-0\">",
            insertAfter: true, newCode: newCode);

        Assert.Equal("gen/partial.html", step.File);
        Assert.Equal("html", step.TargetType);
        Assert.Equal("  <div class=\"card\" id=\"card-7-0\">", step.TargetName);
        // targetName → TargetSymbol (blank targetSymbol falls back to targetName)
        Assert.Equal(step.TargetName, step.TargetSymbol);
        Assert.True(step.InsertAfter);
        Assert.NotNull(step.NewCode);
        Assert.Equal(newCode, step.NewCode);
        Assert.Equal(42, step.LineNumber);
        Assert.Equal(1, step.Priority);
        Assert.Null(step.FullFile);
        Assert.Null(step.NewString); // no fullFile → not surfaced onto NewString
    }

    [Fact]
    public void ParseStepFromJson_TargetSymbolPreferredOverTargetName()
    {
        var step = InvokeParseStepFromJson(
            "gen/x.html", "update the section", targetSymbol: "explicitSymbol",
            line: 0, oldString: null, newString: null,
            refFiles: new List<string>(), edits: new List<EditPair>(),
            targetType: "html", targetName: "  <section>", insertAfter: null, newCode: null);
        Assert.Equal("explicitSymbol", step.TargetSymbol);
        Assert.Equal("  <section>", step.TargetName);
    }

    [Fact]
    public void ParseStepFromJson_FullFile_SurfacedOntoNewString()
    {
        const string full = "<html>\n  <body>complete file</body>\n</html>";
        var step = InvokeParseStepFromJson(
            "gen/new.html", "create the page", targetSymbol: null,
            line: 0, oldString: null, newString: null,
            refFiles: new List<string>(), edits: new List<EditPair>(),
            fullFile: full);
        Assert.Equal(full, step.FullFile);
        Assert.Equal(full, step.NewString); // the _create_file path sees it as NewString
        Assert.Null(step.NewCode);
        Assert.Null(step.InsertAfter);
    }

    [Fact]
    public void ParseStepFromJson_EditPrefixStripped()
    {
        var step = InvokeParseStepFromJson(
            "_edit/wwwroot/kanban.html", "add a card", targetSymbol: null,
            line: 0, oldString: null, newString: null,
            refFiles: new List<string>(), edits: new List<EditPair>(),
            targetType: "html", targetName: "<div>");
        Assert.Equal("wwwroot/kanban.html", step.File);
    }

    [Fact]
    public void FormatDPayload_InsertAfter_AppliesPureInsertion()
    {
        const int docIdx = 10;
        var html = BuildDoc(docIdx, cardCount: 3);
        var targetName = CardBlock(docIdx, 1);
        var newCode = CardBlock(docIdx, 99);
        var (oldStr, newStr, alreadyDone) = ComposeFormatDPayload(
            html, targetName, newCode, "add a new card after the middle card",
            hasInsertAfter: true, insertAfter: true, hasReplace: false, replaceSection: false);

        Assert.False(alreadyDone);
        Assert.Equal(targetName, oldStr);
        var (replaced, applied, err, _) = AgentEditHeuristics.TryReplaceSafe(html, oldStr, newStr, 0, "add a card");
        Assert.True(replaced, $"TryReplaceSafe failed: {err}");
        Assert.Equal(html.Replace(oldStr, newStr), applied); // pure insertion
        // Inserted block byte-present once, siblings byte-identical.
        Assert.Equal(1, CountOccurrences(applied, newCode));
        Assert.Equal(1, CountOccurrences(applied, CardBlock(docIdx, 0)));
        Assert.Equal(1, CountOccurrences(applied, CardBlock(docIdx, 2)));
        // Anchor still present (insertAfter keeps it) — block is now anchor + new.
        Assert.Equal(1, CountOccurrences(applied, targetName));
    }

    [Fact]
    public void FormatDPayload_Replace_AppliesPureReplacement()
    {
        const int docIdx = 11;
        var html = BuildDoc(docIdx, cardCount: 3);
        var targetName = CardBlock(docIdx, 0);
        var newCode = CardBlock(docIdx, 88);
        var (oldStr, newStr, alreadyDone) = ComposeFormatDPayload(
            html, targetName, newCode, "replace the first card with the new one",
            hasInsertAfter: false, insertAfter: false, hasReplace: true, replaceSection: true);

        Assert.False(alreadyDone);
        Assert.Equal(targetName, oldStr);
        var (replaced, applied, err, _) = AgentEditHeuristics.TryReplaceSafe(html, oldStr, newStr, 0, "replace a card");
        Assert.True(replaced, $"TryReplaceSafe failed: {err}");
        Assert.Equal(html.Replace(oldStr, newStr), applied); // pure replacement
        // Target gone, new block present once, siblings byte-identical.
        Assert.Equal(0, CountOccurrences(applied, targetName));
        Assert.Equal(1, CountOccurrences(applied, newCode));
        Assert.Equal(1, CountOccurrences(applied, CardBlock(docIdx, 1)));
        Assert.Equal(1, CountOccurrences(applied, CardBlock(docIdx, 2)));
    }

    [Fact]
    public void FormatDPayload_InsertBefore_UsesRawNewCodeNoRealign()
    {
        const int docIdx = 12;
        var html = BuildDoc(docIdx, cardCount: 2);
        var targetName = CardBlock(docIdx, 0);
        // Deliberately over-indented newCode — insertBefore must NOT realign it
        // (the agent's insertBefore branch uses raw newCodeStr).
        var newCode = "        " + CardBlock(docIdx, 77).Replace("\n", "\n        ");
        var (oldStr, newStr, alreadyDone) = ComposeFormatDPayload(
            html, targetName, newCode, "add a new card before the first card",
            hasInsertAfter: false, insertAfter: false, hasReplace: false, replaceSection: false);

        Assert.False(alreadyDone);
        Assert.Equal(targetName, oldStr);
        // Pure prefix: raw newCode + "\n" + anchor.
        Assert.Equal(newCode + "\n" + targetName, newStr);
        var (replaced, applied, err, _) = AgentEditHeuristics.TryReplaceSafe(html, oldStr, newStr, 0, "add a card");
        Assert.True(replaced, $"TryReplaceSafe failed: {err}");
        Assert.Equal(html.Replace(oldStr, newStr), applied);
        // The raw over-indented block is present unchanged (no realign for insertBefore).
        Assert.Equal(1, CountOccurrences(applied, newCode));
    }

    [Fact]
    public void FormatDPayload_StripLeadingClosingDivs_BeforeCompose()
    {
        const int docIdx = 13;
        var html = BuildDoc(docIdx, cardCount: 2);
        var targetName = CardBlock(docIdx, 1);
        // LLM emits a stray leading </div> — StripLeadingClosingDivs must remove it
        // (targetLeading = 0 for a card anchor), so the composed block has no doubled closer.
        var newCode = "</div>\n" + CardBlock(docIdx, 66);
        var (oldStr, newStr, alreadyDone) = ComposeFormatDPayload(
            html, targetName, newCode, "add a new card after the target",
            hasInsertAfter: true, insertAfter: true, hasReplace: false, replaceSection: false);

        Assert.False(alreadyDone);
        // The stray closer is stripped, so the composed string is byte-exactly
        // anchor + "\n" + the CLEAN card (no doubled closer anywhere).
        Assert.Equal(targetName + "\n" + CardBlock(docIdx, 66), newStr);
        Assert.Equal(targetName, oldStr);
        var (replaced, applied, err, _) = AgentEditHeuristics.TryReplaceSafe(html, oldStr, newStr, 0, "add a card");
        Assert.True(replaced, $"TryReplaceSafe failed: {err}");
        Assert.Equal(html.Replace(oldStr, newStr), applied);
        Assert.Equal(1, CountOccurrences(applied, CardBlock(docIdx, 66)));
    }

    [Fact]
    public void FormatDPayload_AlreadyDone_ShortCircuits()
    {
        const int docIdx = 14;
        var html = BuildDoc(docIdx, cardCount: 2);
        // newCode already present in the file → guard fires, nothing composed.
        var (_, _, alreadyDone) = ComposeFormatDPayload(
            html, CardBlock(docIdx, 0), CardBlock(docIdx, 1), "add a duplicate",
            hasInsertAfter: true, insertAfter: true, hasReplace: false, replaceSection: false);
        Assert.True(alreadyDone);
    }

    // ── Fuzz corpus ──────────────────────────────────────────────────────────

    /// <summary>
    /// 45 seeded docs cycling all four payload flag combinations (insertAfter /
    /// replace / neither / insertAfter:false) plus a stray-leading-</div> drift
    /// variant, each run through the COMPLETE payload route: ParseStepFromJson
    /// (reflection) → StripLeadingClosingDivs → already-done guard → ResolveHtmlAnchor
    /// → flag-keyed compose → TryReplaceSafe. Asserts the applied file equals the pure
    /// insertion, the inserted block re-resolves as its own anchor, siblings stay
    /// byte-identical, and re-running the identical payload hits the already-done guard.
    /// </summary>
    [Fact]
    public void Fuzz_FormatDPayload_FullChain_PureInsertion()
    {
        const int docCount = 45;
        const int seed = 55555;
        const int prime = 104729;
        var checkedDocs = 0;
        var afterHits = 0;
        var replaceHits = 0;
        var beforeHits = 0;

        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(seed, i, prime);
            var docIdx = 100 + i;
            var cardCount = 2 + rng.Next(3); // 2-4 cards
            var html = BuildDoc(docIdx, cardCount);
            var targetIdx = rng.Next(cardCount);
            var targetName = CardBlock(docIdx, targetIdx);
            var newCode = CardBlock(docIdx, 500 + i);
            // Stray leading </div> on ~1 in 3 docs — must be stripped pre-compose.
            if (i % 3 == 0)
                newCode = "</div>\n" + newCode;

            // Flag combination per doc — all four branches of the agent's compose logic.
            (bool hasInsertAfter, bool insertAfter, bool hasReplace, bool replaceSection) flags;
            switch (i % 4)
            {
                case 0: flags = (true,  true,  false, false); break; // insertAfter
                case 1: flags = (false, false, true,  true);  break; // replace
                case 2: flags = (false, false, false, false); break; // insertBefore
                default: flags = (true,  false, false, false); break; // insertAfter:false → replace
            }
            // NOTE: the i%4==3 combo (insertAfter:false, no replace flag) drives the
            // REPLACE branch per the agent's logic, but its change-desc says "before" —
            // intentional: the anchor is unique so ResolveHtmlAnchor's keyword path
            // never engages, and this keeps the desc/flag matrix honest to the real
            // payloads the agent emits (which don't always pair desc with flags).
            var change = flags.insertAfter ? "add a new card after the target"
                : flags.replaceSection ? "replace the target card with the new one"
                : "add a new card before the target";

            // 1. Compose the PlanStep through the REAL ParseStepFromJson.
            var step = InvokeParseStepFromJson(
                $"gen/payload_{i:D2}.html", change, targetSymbol: null, line: 0,
                oldString: null, newString: null,
                refFiles: new List<string>(), edits: new List<EditPair>(),
                targetType: "html", targetName: targetName, insertAfter: flags.insertAfter,
                newCode: newCode.Split('\n').ToList());
            Assert.Equal("html", step.TargetType);
            Assert.Equal(targetName, step.TargetSymbol);
            Assert.Equal(flags.insertAfter, step.InsertAfter);
            Assert.NotNull(step.NewCode);
            Assert.Equal(newCode.Split('\n').ToList(), step.NewCode);

            // 2. Run the FORMAT D apply chain on the composed payload fields.
            var payloadNewCode = string.Join("\n", step.NewCode!);
            var (oldStr, newStr, alreadyDone) = ComposeFormatDPayload(
                html, step.TargetName!, payloadNewCode, change,
                flags.hasInsertAfter, flags.insertAfter, flags.hasReplace, flags.replaceSection);

            Assert.False(alreadyDone, $"doc #{i}: newCode must not already be present");
            Assert.Equal(1, CountOccurrences(html, oldStr)); // unique anchor
            var (replaced, applied, err, _) = AgentEditHeuristics.TryReplaceSafe(html, oldStr, newStr, 0, change);
            Assert.True(replaced, $"doc #{i}: TryReplaceSafe failed: {err}");

            // 3. Pure insertion — applied equals the single-anchor substitution.
            Assert.Equal(html.Replace(oldStr, newStr), applied);

            // 4. Inserted block byte-present once and re-resolves as its own anchor.
            var cleanNewCode = payloadNewCode;
            cleanNewCode = HtmlDomEditor.StripLeadingClosingDivs(cleanNewCode, step.TargetName);
            Assert.Equal(1, CountOccurrences(applied, cleanNewCode));
            var (reResolved, _, reErr) = HtmlDomEditor.ResolveHtmlAnchor(applied, cleanNewCode, change);
            Assert.NotNull(reResolved);
            Assert.Null(reErr);

            // 5. Sibling cards byte-identical (every card that isn't the anchor).
            for (var k = 0; k < cardCount; k++)
            {
                if (k == targetIdx) continue;
                Assert.True(CountOccurrences(applied, CardBlock(docIdx, k)) == 1,
                    $"doc #{i}: sibling card {k} was disturbed");
            }

            // 6. Re-running the identical payload hits the already-done guard — no double insert.
            var (_, _, reAlready) = ComposeFormatDPayload(
                applied, step.TargetName!, payloadNewCode, change,
                flags.hasInsertAfter, flags.insertAfter, flags.hasReplace, flags.replaceSection);
            Assert.True(reAlready, $"doc #{i}: re-apply must hit already-done");

            // Tally by effective strategy — mirroring the agent's branch conditions
            // exactly (replaceSection OR insertAfter:false → replace; else insertAfter;
            // else insertBefore), so the branch counters can't drift from the compose.
            var isReplaceBranch = flags.replaceSection || (flags.hasInsertAfter && !flags.insertAfter && !flags.hasReplace);
            var isAfterBranch = !isReplaceBranch && (flags.insertAfter || (flags.hasReplace && !flags.replaceSection && !flags.hasInsertAfter));
            if (isAfterBranch) afterHits++;
            else if (isReplaceBranch) replaceHits++;
            else beforeHits++;
            checkedDocs++;
        }

        FuzzHarness.AssertAllDocsChecked(checkedDocs, docCount, "FORMAT D payload corpus");
        FuzzHarness.AssertExercised(afterHits, "no fuzz doc exercised the insertAfter branch");
        FuzzHarness.AssertExercised(replaceHits, "no fuzz doc exercised the replace branch");
        FuzzHarness.AssertExercised(beforeHits, "no fuzz doc exercised the insertBefore branch");
        // Exact rotation for 45 docs (i%4): 0→insertAfter (12), 1→replace (11),
        // 2→insertBefore (11), 3→replace via insertAfter:false (11). Pinning the exact
        // counts catches any drift between the tally and the branch logic itself.
        Assert.Equal(12, afterHits);
        Assert.Equal(22, replaceHits);
        Assert.Equal(11, beforeHits);
    }

    /// <summary>
    /// Corpus for the already-done guard's FALSE-POSITIVE boundary: the LLM re-emits
    /// a FORMAT D payload whose newCode is ALREADY 2-space-indented (the realigned
    /// form the file would contain after a prior insert) but is byte-absent from the
    /// file — the guard (`html.Contains(newCode, OrdinalIgnoreCase)` on the RAW
    /// pre-realign payload) must NOT fire; the insert must still land. Every 3rd doc
    /// nests the cards in a 2-space container (anchors at 4-space), so the pre-indented
    /// newCode is UNDER-indented for the anchor and the realign path must bump it to
    /// the anchor's base indent — while the guard still stays silent. Per doc: the raw
    /// guard predicate is false; the compose already-done flag is false; the insert
    /// lands as the pure substitution with the realigned block byte-present; re-applying
    /// the IDENTICAL pre-indented payload on the applied file DOES hit the guard (the
    /// block is present now — no double insert). This locks the "re-indented
    /// re-application isn't swallowed" guarantee end-to-end.
    /// </summary>
    [Fact]
    public void Fuzz_FormatDPayload_AlreadyIndentedNewCode_GuardDoesNotSwallow()
    {
        const int docCount = 30;
        const int seed = 60_610;
        const int prime = 104729;
        var checkedDocs = 0;
        var afterHits = 0;
        var replaceHits = 0;
        var beforeHits = 0;
        var nestedHits = 0;

        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(seed, i, prime);
            var docIdx = 200 + i;
            var cardCount = 2 + rng.Next(3); // 2-4 cards
            var isNested = i % 3 == 0;
            var html = isNested ? BuildNestedDoc(docIdx, cardCount) : BuildDoc(docIdx, cardCount);
            var targetIdx = rng.Next(cardCount);
            var targetName = isNested
                ? IndentBlock(CardBlock(docIdx, targetIdx), "  ")
                : CardBlock(docIdx, targetIdx);

            // The re-indented payload: newCode ALREADY at the file's card style
            // (2-space base indent) with a unique heading token — byte-absent.
            var newCode = CardBlock(docIdx, 900 + i);

            // HEADLINE: the raw already-done predicate must be FALSE — indentation
            // alone must never make an absent block look present.
            Assert.False(html.Contains(newCode, StringComparison.OrdinalIgnoreCase),
                $"doc #{i}: already-done guard false-positived on an ABSENT pre-indented block (nested={isNested})");

            // Flag combination per doc — all four branches of the agent's compose logic.
            (bool hasInsertAfter, bool insertAfter, bool hasReplace, bool replaceSection) flags;
            switch (i % 4)
            {
                case 0: flags = (true,  true,  false, false); break; // insertAfter
                case 1: flags = (false, false, true,  true);  break; // replace
                case 2: flags = (false, false, false, false); break; // insertBefore
                default: flags = (true,  false, false, false); break; // insertAfter:false → replace
            }
            var change = flags.insertAfter ? "add a new card after the target"
                : flags.replaceSection ? "replace the target card with the new one"
                : "add a new card before the target";

            // Compose — the guard must NOT swallow the absent pre-indented payload.
            var (oldStr, newStr, alreadyDone) = ComposeFormatDPayload(
                html, targetName, newCode, change,
                flags.hasInsertAfter, flags.insertAfter, flags.hasReplace, flags.replaceSection);
            Assert.False(alreadyDone, $"doc #{i}: compose guard swallowed an absent pre-indented insert");
            Assert.Equal(1, CountOccurrences(html, oldStr)); // unique anchor

            // Apply — the insert lands as the PURE substitution (realign bumps the
            // under-indented newCode to the anchor's 4-space base when nested).
            var (replaced, applied, err, _) = AgentEditHeuristics.TryReplaceSafe(html, oldStr, newStr, 0, change);
            Assert.True(replaced, $"doc #{i}: TryReplaceSafe failed: {err}");
            Assert.Equal(html.Replace(oldStr, newStr), applied);

            // The inserted block is byte-present in the form the branch composed:
            // raw newCode for insertBefore (no realign), realigned otherwise.
            var isReplaceBranch = flags.replaceSection || (flags.hasInsertAfter && !flags.insertAfter && !flags.hasReplace);
            var isAfterBranch = !isReplaceBranch && (flags.insertAfter || (flags.hasReplace && !flags.replaceSection && !flags.hasInsertAfter));
            var presentForm = isAfterBranch || isReplaceBranch
                ? FuzzHarness.FormatSnippetRealign(targetName, newCode)
                : newCode;
            Assert.Equal(1, CountOccurrences(applied, presentForm)); // composed form byte-present

            // Re-applying the IDENTICAL pre-indented payload: the compose's already-done
            // flag must EXACTLY mirror the raw byte predicate on the applied file. When
            // realign is a no-op (flat anchor, payload already at base indent) or the
            // branch inserts raw (insertBefore), the block is byte-present → guard fires
            // (no double insert). When the anchor is nested and realign bumps the payload
            // deeper, the RAW 2-space newCode is no longer a byte-substring → the guard
            // correctly stays silent (it checks raw bytes, never indentation-shifted
            // content). Divergence would mean the guard fired on indentation alone.
            var rawPresentInApplied = applied.Contains(newCode, StringComparison.OrdinalIgnoreCase);
            var (_, _, reAlready) = ComposeFormatDPayload(
                applied, targetName, newCode, change,
                flags.hasInsertAfter, flags.insertAfter, flags.hasReplace, flags.replaceSection);
            Assert.True(reAlready == rawPresentInApplied,
                $"doc #{i}: already-done flag {reAlready} diverged from raw-present {rawPresentInApplied} (nested={isNested})");

            // Sibling cards byte-identical (every card that isn't the anchor).
            for (var k = 0; k < cardCount; k++)
            {
                if (k == targetIdx) continue;
                var sibling = isNested ? IndentBlock(CardBlock(docIdx, k), "  ") : CardBlock(docIdx, k);
                Assert.Equal(1, CountOccurrences(applied, sibling)); // sibling byte-identical
            }

            if (isAfterBranch) afterHits++;
            else if (isReplaceBranch) replaceHits++;
            else beforeHits++;
            if (isNested) nestedHits++;
            checkedDocs++;
        }

        FuzzHarness.AssertAllDocsChecked(checkedDocs, docCount, "pre-indented newCode guard corpus");
        FuzzHarness.AssertExercised(afterHits, "no doc exercised the insertAfter branch");
        FuzzHarness.AssertExercised(replaceHits, "no doc exercised the replace branch");
        FuzzHarness.AssertExercised(beforeHits, "no doc exercised the insertBefore branch");
        FuzzHarness.AssertExercised(nestedHits, "no doc exercised the nested-container variant");
        // Exact rotation for 30 docs (i%4): 0→insertAfter (8), 1→replace (8),
        // 2→insertBefore (7), 3→replace via insertAfter:false (7). Pinning the exact
        // counts catches drift between the tally and the branch logic itself.
        Assert.Equal(8, afterHits);
        Assert.Equal(15, replaceHits);
        Assert.Equal(7, beforeHits);
    }

    /// <summary>
    /// The FORMAT D payload corpus wired through the REAL anchor-resolution fallback
    /// chain (exact → normalized → collapsed → fuzzy): targetNames are generated with
    /// whitespace/attribute drift so the exact link is provably broken, and the anchor
    /// must still resolve to the INTENDED block — then the pure-insertion claim holds
    /// end-to-end through the real HtmlDomEditor.ResolveHtmlAnchor. Every doc cycles
    /// one drift link (normalized / collapsed / fuzzy) and one payload flag combination.
    /// </summary>
    [Fact]
    public void Fuzz_FormatDPayload_AnchorDrift_ResolvesThroughFallbackChain()
    {
        const int docCount = 45;
        const int seed = 60_613;
        const int prime = 104729;
        var checkedDocs = 0;
        var normalizedHits = 0;
        var collapsedHits = 0;
        var fuzzyHits = 0;

        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(seed, i, prime);
            var docIdx = 300 + i;
            var cardCount = 2 + rng.Next(3); // 2-4 cards
            var html = BuildDoc(docIdx, cardCount);
            var targetIdx = rng.Next(cardCount);
            var cleanTarget = CardBlock(docIdx, targetIdx);
            var newCode = CardBlock(docIdx, 700 + i);

            // Drift link per doc — normalized (0) / collapsed (1) / fuzzy (2), cycled on
            // i%3 (coprime with the i%4 flag cadence → every drift × branch pair appears
            // ~3-4× across the 45 docs).
            var driftMode = i % 3;
            var driftedTarget = DriftCardBlock(cleanTarget, driftMode, docIdx, targetIdx);

            // Self-verifying: the drift MUST break the exact link — otherwise the fallback
            // never engaged and the anchor claims below would be vacuous.
            Assert.False(html.Contains(driftedTarget, StringComparison.Ordinal),
                $"doc #{i}: drift {driftMode} failed to break the exact match");
            if (driftMode == 2)
                Assert.DoesNotContain("hallucinated", html); // fuzzy relies on the id alone

            // Pin the ENGAGED fallback link, not just the resolved outcome: the normalized
            // token-join (exact mirror of IndexOfNormalized's \S+-token pattern) must be LIVE
            // for drift 0 and provably DEAD for drifts 1 and 2 — so a future re-ordering of
            // the exact → normalized → collapsed → fuzzy chain cannot silently re-route a
            // drift variant past its intended link without this corpus noticing.
            var normPattern = string.Join(@"\s+", Regex.Matches(driftedTarget, @"\S+")
                .Select(m => Regex.Escape(m.Value)));
            Assert.Equal(driftMode == 0, Regex.IsMatch(html, normPattern, RegexOptions.IgnoreCase));

            // Flag combination per doc — all four branches of the agent's compose logic.
            (bool hasInsertAfter, bool insertAfter, bool hasReplace, bool replaceSection) flags;
            switch (i % 4)
            {
                case 0: flags = (true,  true,  false, false); break; // insertAfter
                case 1: flags = (false, false, true,  true);  break; // replace
                case 2: flags = (false, false, false, false); break; // insertBefore
                default: flags = (true,  false, false, false); break; // insertAfter:false → replace
            }
            var change = flags.insertAfter ? "add a new card after the target"
                : flags.replaceSection ? "replace the target card with the new one"
                : "add a new card before the target";

            // 1. Compose the PlanStep through the REAL ParseStepFromJson with the DRIFTED
            //    targetName — the payload route pins the mapping exactly as the agent runs it.
            var step = InvokeParseStepFromJson(
                $"gen/drift_{i:D2}.html", change, targetSymbol: null, line: 0,
                oldString: null, newString: null,
                refFiles: new List<string>(), edits: new List<EditPair>(),
                targetType: "html", targetName: driftedTarget, insertAfter: flags.insertAfter,
                newCode: newCode.Split('\n').ToList());
            Assert.Equal(driftedTarget, step.TargetName);

            // 2. Run the FORMAT D apply chain on the composed payload fields.
            var payloadNewCode = string.Join("\n", step.NewCode!);
            var (oldStr, newStr, alreadyDone) = ComposeFormatDPayload(
                html, step.TargetName!, payloadNewCode, change,
                flags.hasInsertAfter, flags.insertAfter, flags.hasReplace, flags.replaceSection);

            Assert.False(alreadyDone, $"doc #{i}: newCode must not already be present");

            // 3. HEADLINE — the fallback chain resolved to the INTENDED CLEAN block, not
            //    the drifted bytes and not a sibling. All three links return the full card
            //    (expandToLineStart + expandToClosingTags); the fuzzy link additionally
            //    picks the id-matching card as the unique best-score winner.
            Assert.Equal(cleanTarget, oldStr);
            Assert.Equal(1, CountOccurrences(html, oldStr)); // unique anchor

            var (replaced, applied, err, _) = AgentEditHeuristics.TryReplaceSafe(html, oldStr, newStr, 0, change);
            Assert.True(replaced, $"doc #{i}: TryReplaceSafe failed: {err}");

            // 4. Pure insertion — applied equals the single-anchor substitution.
            Assert.Equal(html.Replace(oldStr, newStr), applied);

            // 5. Inserted block byte-present once and re-resolves as its own anchor.
            Assert.Equal(1, CountOccurrences(applied, newCode));
            var (reResolved, _, reErr) = HtmlDomEditor.ResolveHtmlAnchor(applied, newCode, change);
            Assert.NotNull(reResolved);
            Assert.Null(reErr);

            // 6. Sibling cards byte-identical (every card that isn't the anchor).
            for (var k = 0; k < cardCount; k++)
            {
                if (k == targetIdx) continue;
                Assert.True(CountOccurrences(applied, CardBlock(docIdx, k)) == 1,
                    $"doc #{i}: sibling card {k} was disturbed");
            }

            // 7. Re-running the IDENTICAL drifted payload hits the already-done guard.
            var (_, _, reAlready) = ComposeFormatDPayload(
                applied, step.TargetName!, payloadNewCode, change,
                flags.hasInsertAfter, flags.insertAfter, flags.hasReplace, flags.replaceSection);
            Assert.True(reAlready, $"doc #{i}: re-apply must hit already-done");

            if (driftMode == 0) normalizedHits++;
            else if (driftMode == 1) collapsedHits++;
            else fuzzyHits++;
            checkedDocs++;
        }

        FuzzHarness.AssertAllDocsChecked(checkedDocs, docCount, "FORMAT D anchor-drift corpus");
        // Exact rotation for 45 docs (i%3): 15 per drift link — every fallback link fires.
        Assert.Equal(15, normalizedHits);
        Assert.Equal(15, collapsedHits);
        Assert.Equal(15, fuzzyHits);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  DELETE CORPUS VIA THE FORMAT D PAYLOAD ROUTE — targetType=html + targetName + no newCode
    // ═══════════════════════════════════════════════════════════════════════════
    // The LLM-payload analog of HtmlDomEditorTests' Fuzz_DeleteHtml_..._RemovesOnlyExactElement:
    // the SAME duplicate-similar-card docs and anti-over-match guarantees, but the step is
    // composed through the REAL ParseStepFromJson with targetType=html + targetName and an
    // EMPTY/ABSENT newCode — the exact shape a planner emits for a deletion intent. The
    // routing claim locked here: HasConcreteEdit(step) is FALSE for such a payload (no
    // concrete oldString/newString/newCode fields surface), so the plan path MUST flow
    // through the resolution-driven DeleteLines chain — Classify → ClassifyIntent → Decide
    // → ResolveHtmlAnchor(TargetSymbol, Change) → TryReplaceSafe(block, "", LineNumber,
    // Change). (Separately, the RAW LLM-payload compose branches — AgentController ~1317 /
    // ~1954 — now ALSO resolve a replace intent with empty newCode as a deletion; see
    // FormatD_EmptyNewCodeReplace_* below. Both routes must strip exactly the target block.)
    // Every field of the chain is sourced from the composed step (TargetSymbol = targetName
    // via the targetName→TargetSymbol mapping),
    // and the same five variants + invariants are asserted: byte-length delta == exactly the
    // target block, the &lt;div class="card"&gt; count drops by exactly 1, all siblings
    // byte-identical, and the no-context variant refuses.

    /// <summary>
    /// The resolution-driven deletion chain the agent runs for a payload step whose
    /// HasConcreteEdit is FALSE (targetType=html + targetName, NO newCode): the change
    /// classifies to DeleteLines/DeleteContent, the anchor resolves through the REAL
    /// HtmlDomEditor.ResolveHtmlAnchor using the payload's TargetSymbol (the
    /// targetName→TargetSymbol mapping from ParseStepFromJson) with the change's keyword
    /// disambiguation, then TryReplaceSafe is called with the step's line + change —
    /// exactly the DOM delete path (AgentController:4529). Mirrors
    /// HtmlDomEditorTests.RunHtmlDeleteChain byte-for-byte, sourcing every field from the
    /// composed PlanStep instead of raw arguments.
    /// </summary>
    private static (bool replaced, string finalContent, string? error, string matchedBlock) RunPayloadDeleteChain(
        string html, PlanStep step)
    {
        Assert.Equal(EditStrategy.DeleteLines, EditClassifier.Classify(step, fileExists: true, ".html"));
        var intent = EditClassifier.ClassifyIntent(step, ".html");
        Assert.Equal(EditIntentKind.DeleteContent, intent.Kind);
        var decision = EditStrategyResolver.Decide(step.File, true, html, step.Change, intent);
        Assert.Equal(EditStrategy.DeleteLines, decision.Strategy);

        // TargetSymbol is ALWAYS set (targetName→TargetSymbol mapping in ParseStepFromJson) —
        // the dead `?? step.TargetName` fallback would only hide a mapping regression.
        var anchor = step.TargetSymbol!;
        var (matchedBlock, _, htmlErr) = HtmlDomEditor.ResolveHtmlAnchor(html, anchor, step.Change);
        if (matchedBlock == null) throw new Xunit.Sdk.XunitException($"anchor did not resolve: {htmlErr}");
        var (replaced, applied, matchError, _) = AgentEditHeuristics.TryReplaceSafe(
            html, matchedBlock, "", step.LineNumber, step.Change);
        return (replaced, applied, matchError, matchedBlock);
    }

    /// <summary>
    /// 30 seeded docs mirroring the delete corpus' five variants (unique target, keyword
    /// dup pair, line dup pair, no-context refusal, short-anchor + heading keyword), each
    /// payload composed via the REAL ParseStepFromJson with targetType=html + targetName
    /// and an EMPTY/ABSENT newCode. Asserts the payload mapping (targetName→TargetSymbol,
    /// no concrete fields surfaced), the routing gate (HasConcreteEdit false), and the same
    /// anti-over-match guarantees through the resolution-driven deletion chain.
    /// </summary>
    [Fact]
    public void Fuzz_FormatDPayload_EmptyNewCode_RoutesToDeletionChain()
    {
        const int docCount = 30;
        const int seed = 60_615;
        const int prime = 104729;
        var checkedDocs = 0;
        var uniqueRemovals = 0;
        var keywordRemovals = 0;
        var lineRemovals = 0;
        var refusals = 0;
        var shortAnchorRemovals = 0;

        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(seed, i, prime);
            var docIdx = 500 + i;
            var variant = i % 5;
            // Rotate per 5-doc cycle (not per index) so every variant samples ALL five
            // marker words across the corpus — RNG-independent, so variants 0-3 docs stay
            // byte-identical regardless of the chosen marker.
            var marker = FuzzHarness.DeleteMarkers[(i / 5) % FuzzHarness.DeleteMarkers.Length];

            // Shared 5-variant doc builder — byte-identical to the direct-chain delete
            // corpus (HtmlDomEditorTests) so the two routes stay in lockstep.
            var (html, parts, expectedTarget, targetName, targetLine, dup) =
                FuzzHarness.BuildDeleteCorpusDoc(rng, i, docIdx, variant, marker);
            var change = variant switch
            {
                // Variants 0/2/3 need no keyword (unique single match / line hint / refusal)
                // — their phrases are stopword-only so ExtractDisambiguationKeywords yields
                // nothing and the disambiguation is driven by the variant's path. Variants 1
                // and 4 carry the marker keyword — for 4 it names the target card's heading.
                0 => "remove the block",
                1 => $"remove the {marker} block",
                2 => "remove the block",
                3 => "remove the block",
                4 => $"remove the {marker} block",
                // Unreachable (variant = i % 5 ∈ [0,4]) — kept only for int exhaustiveness.
                _ => $"remove the {marker} block",
            };

            // 1. Compose the PlanStep through the REAL ParseStepFromJson — targetType=html +
            //    targetName with an EMPTY/ABSENT newCode (the deletion-intent payload shape).
            //    Cycling both spellings (absent vs. empty array): ParseStepFromJson maps an
            //    empty list to null, so both must land on the identical resolution-driven step.
            var newCodePayload = i % 2 == 0 ? null : new List<string>();
            var step = InvokeParseStepFromJson(
                $"gen/del_{i:D2}.html", change, targetSymbol: null, line: targetLine,
                oldString: null, newString: null,
                refFiles: new List<string>(), edits: new List<EditPair>(),
                targetType: "html", targetName: targetName, insertAfter: null,
                newCode: newCodePayload, fullFile: null);

            // The payload mapping the executor sees: targetType/targetName preserved,
            // targetName→TargetSymbol, empty newCode normalized to null, and NO concrete
            // edit fields surfaced (no oldString/newString/fullFile/edits).
            Assert.Equal("html", step.TargetType);
            Assert.Equal(targetName, step.TargetName);
            Assert.Equal(targetName, step.TargetSymbol);
            Assert.Equal(targetLine, step.LineNumber);
            Assert.Null(step.NewCode);
            Assert.Null(step.NewString);
            Assert.Null(step.OldString);
            Assert.Null(step.FullFile);

            // 2. ROUTING GATE: HasConcreteEdit(step) must be FALSE — with no newCode the
            //    payload is NOT a concrete FORMAT D edit, so the executor takes the
            //    resolution-driven deletion path below. (The compose branches handle empty
            //    newCode deletions for RAW LLM payloads; this plan-level step has no concrete
            //    fields and routes through the chain.)
            Assert.False(InvokeHasConcreteEdit(step),
                $"doc #{i}: empty-newCode payload must not be a concrete edit");

            // 3. The SAME deletion chain the delete corpus runs — but every field sourced
            //    from the composed step (TargetSymbol as the anchor, Change, LineNumber).
            var (replaced, applied, error, matchedBlock) = RunPayloadDeleteChain(html, step);

            // The anchor resolved to the intended block bytes (full card) in every variant:
            // variants 0-3 anchor on the full block verbatim, variant 4 anchors on the SHORT
            // <div class="card"> tag and expands to the intended card via its keyword window.
            if (variant == 4)
                Assert.Equal(expectedTarget, matchedBlock);
            else
                Assert.Equal(targetName, matchedBlock);

            switch (variant)
            {
                case 0:
                    Assert.True(replaced, $"doc #{i} unique target must delete: {error}");
                    Assert.Equal(html.Length - targetName.Length, applied.Length);
                    Assert.DoesNotContain(targetName, applied, StringComparison.Ordinal);
                    // Every sibling card survives byte-identical.
                    foreach (var part in parts.Skip(1))
                        Assert.Equal(1, CountOccurrences(applied, part));
                    uniqueRemovals++;
                    break;
                case 1:
                    Assert.True(replaced, $"doc #{i} keyword target must delete: {error}");
                    Assert.Equal(html.Length - dup!.Length, applied.Length);
                    Assert.Contains($"<!-- {marker} -->", applied, StringComparison.Ordinal);
                    Assert.Equal(1, CountOccurrences(applied, dup)); // one dup survives
                    // The MARKED (first) duplicate was removed — the survivor is the second,
                    // shifted left by exactly dup.Length.
                    Assert.Equal(FuzzHarness.NthIndexOf(html, dup, 2) - dup.Length, applied.IndexOf(dup, StringComparison.Ordinal));
                    keywordRemovals++;
                    break;
                case 2:
                    Assert.True(replaced, $"doc #{i} line target must delete: {error}");
                    Assert.Equal(html.Length - dup!.Length, applied.Length);
                    Assert.Equal(1, CountOccurrences(applied, dup));
                    // The NEAREST (second) duplicate was removed — the survivor is the first,
                    // at its original position.
                    Assert.Equal(FuzzHarness.NthIndexOf(html, dup, 1), applied.IndexOf(dup, StringComparison.Ordinal));
                    lineRemovals++;
                    break;
                case 4:
                    Assert.True(replaced, $"doc #{i} short-anchor keyword target must delete: {error}");
                    Assert.Equal(html.Length - expectedTarget!.Length, applied.Length);
                    Assert.DoesNotContain(expectedTarget, applied, StringComparison.Ordinal);
                    // The keyword window picked the marker-heading card among ALL
                    // same-tag/class siblings — the block the short anchor expanded to IS it.
                    Assert.Contains(marker, matchedBlock, StringComparison.OrdinalIgnoreCase);
                    // The marker word appears NOWHERE outside the target card — the keyword
                    // was a genuine disambiguator, not a coincidental sibling substring.
                    Assert.DoesNotContain(marker, html.Replace(expectedTarget!, ""), StringComparison.OrdinalIgnoreCase);
                    // Non-vacuity: without the keyword the resolver's no-hint fallback picks
                    // candidates[^1] = the LAST sibling, NOT the target — proving the keyword
                    // window (not document order) selected the right card.
                    var (noHintBlock, _, noHintErr) = HtmlDomEditor.ResolveHtmlAnchor(html, "<div class=\"card\">");
                    Assert.Null(noHintErr);
                    Assert.NotEqual(expectedTarget, noHintBlock);
                    // Every sibling card survives byte-identical.
                    foreach (var part in parts.Skip(1).Where(p => p.Contains("<h2>")))
                        Assert.Equal(1, CountOccurrences(applied, part));
                    shortAnchorRemovals++;
                    break;
                default:
                    // Variant 3 — no context at all → must refuse and leave the file byte-identical.
                    Assert.False(replaced, $"doc #{i} duplicate with no context must refuse");
                    Assert.Equal(html, applied);
                    Assert.NotNull(error);
                    Assert.Contains("times in file", error);
                    refusals++;
                    break;
            }

            // Every success case: exactly one card removed — never a sibling over-match.
            if (replaced)
                Assert.Equal(CountOccurrences(html, "<div class=\"card\">") - 1,
                             CountOccurrences(applied, "<div class=\"card\">"));
            checkedDocs++;
        }

        FuzzHarness.AssertAllDocsChecked(checkedDocs, docCount, "FORMAT D empty-newCode delete corpus");
        FuzzHarness.AssertExercised(uniqueRemovals, "no doc exercised the unique-target payload deletion path");
        FuzzHarness.AssertExercised(keywordRemovals, "no doc exercised the keyword-disambiguated payload deletion path");
        FuzzHarness.AssertExercised(lineRemovals, "no doc exercised the target-line-disambiguated payload deletion path");
        FuzzHarness.AssertExercised(refusals, "no doc exercised the payload duplicate-refusal path");
        FuzzHarness.AssertExercised(shortAnchorRemovals, "no doc exercised the short-anchor keyword payload deletion path");
    }

    // ── FORMAT D DELETION-INTENT REGRESSION (compose branch) ───────────────────
    // BUG (fixed): an LLM deletion payload — {"targetType":"html","targetName":"…",
    // "replace":true,"newCode":[]} — was rejected by the FORMAT D compose branch BEFORE
    // ResolveHtmlAnchor ran, so it logged "FORMAT D: targetName block not found — no
    // candidates" even when the target block existed byte-verbatim in the file (the
    // kanban.html priority-badge failure: targetName was the exact
    // <span class="card-tag" ng-if="card.priority" …>{{card.priority}}</span> at line 65,
    // but the agent reported it unfindable). The compose branch must treat empty newCode +
    // replace intent as a DELETION: resolve the anchor first, return (block, "").

    /// <summary>
    /// The compose-branch deletion decision, mirroring the production fix: when the payload
    /// requests replace (or defaults to it) with an EMPTY newCode, resolve the anchor through
    /// the real <c>HtmlDomEditor.ResolveHtmlAnchor</c> fallback chain (exact → normalized →
    /// collapsed → fuzzy) and, if found, produce a deletion edit (old=block, new="") instead of
    /// reporting "targetName block not found". Asserted for the exact log scenario: a targetName
    /// with a leading space + the priority-badge markup, which only the normalized/fuzzy chain
    /// can match — the regression that the old skip-before-resolve order masked.
    /// </summary>
    [Fact]
    public void FormatD_EmptyNewCodeReplace_ResolvesAnchorAndDeletesBlock()
    {
        // Byte-mirror of wwwroot/kanban.html's To Do card section (line ~65): the priority
        // badge span inside a card-tags div, plus sibling spans that must survive.
        const string todoCard = "  <div class=\"card\" id=\"c1\">\n" +
            "    <textarea>text</textarea>\n" +
            "    <div class=\"card-tags\">\n" +
            "      <span class=\"card-tag\" ng-if=\"card.ready\">READY</span>\n" +
            "      <span class=\"card-tag\" ng-if=\"card.priority\" ng-class=\"'priority-'+card.priority\">{{card.priority}}</span>\n" +
            "      <span class=\"card-tag\" ng-if=\"card.flagged\">FLAGGED</span>\n" +
            "    </div>\n" +
            "  </div>";
        const string siblingCard = "  <div class=\"card\" id=\"c2\">\n" +
            "    <textarea>other</textarea>\n" +
            "    <div class=\"card-tags\">\n" +
            "      <span class=\"card-tag\" ng-if=\"card.priority\" ng-class=\"'priority-'+card.priority\">{{card.priority}}</span>\n" +
            "    </div>\n" +
            "  </div>";
        var html = "<main>\n" + todoCard + "\n" + siblingCard + "\n</main>";
        // The LLM emitted the targetName WITH a leading space — only the whitespace-normalized
        // chain can match it against the 6-space-indented file line.
        const string targetName = " <span class=\"card-tag\" ng-if=\"card.priority\" ng-class=\"'priority-'+card.priority\">{{card.priority}}</span>";
        const string change = "Remove priority badge span element from card tags section in To Do column only";

        // 1. The anchor MUST resolve through the real fallback chain — never "not found".
        var (matchedBlock, _, htmlErr) = HtmlDomEditor.ResolveHtmlAnchor(html, targetName, change);
        Assert.NotNull(matchedBlock);
        Assert.Null(htmlErr);
        // expandToClosingTags:false + expandToLineStart:true matches exactly the span line.
        var span = matchedBlock!.Trim();
        Assert.Contains("card.priority", span, StringComparison.Ordinal);
        Assert.StartsWith("<span class=\"card-tag\"", span, StringComparison.Ordinal);

        // 2. The compose-branch deletion decision: replace intent + empty newCode → (block, "").
        //    Applying it must remove EXACTLY the target span — siblings survive byte-identical.
        var (replaced, applied, matchError, _) = AgentEditHeuristics.TryReplaceSafe(html, matchedBlock, "", 0, change);
        Assert.True(replaced, $"deletion must apply: {matchError}");
        // The todo card's badge line was deleted; the sibling card's identical badge
        // survives — so exactly ONE full priority-badge span remains.
        Assert.Contains("READY", applied, StringComparison.Ordinal);
        Assert.Contains("FLAGGED", applied, StringComparison.Ordinal);
        Assert.Contains("id=\"c2\"", applied, StringComparison.Ordinal);
        Assert.Contains("<textarea>other</textarea>", applied, StringComparison.Ordinal);
        // Exactly ONE priority badge span remains (the sibling card's) — the todo card's
        // badge was removed. Count whole spans, not the "card.priority" substring (which
        // appears 3x inside each surviving span: ng-if, ng-class, interpolation).
        Assert.Equal(1, CountOccurrences(applied, "<span class=\"card-tag\" ng-if=\"card.priority\""));
    }

    /// <summary>
    /// The fuzzy-chain half of the same regression: a deletion payload whose targetName drifts
    /// (whitespace + hallucinated attribute order) must STILL resolve to the intended span via
    /// the normalized → collapsed → fuzzy fallback, then delete only that span.
    /// </summary>
    [Fact]
    public void FormatD_EmptyNewCodeReplace_AnchorDrift_StillDeletes()
    {
        const string span = "<span class=\"card-tag\" ng-if=\"card.priority\" ng-class=\"'priority-'+card.priority\">{{card.priority}}</span>";
        var html = "<main>\n" +
            "  <div class=\"card\">\n" +
            "    <div class=\"card-tags\">\n" +
            "      " + span + "\n" +
            "    </div>\n" +
            "  </div>\n" +
            "  <div class=\"card\">\n" +
            "    <div class=\"card-tags\">\n" +
            "      " + span + "\n" +
            "    </div>\n" +
            "  </div>\n" +
            "</main>";
        // Drift: extra whitespace between attributes + reordered ng-if/ng-class — byte-exact
        // fails, the normalized \S+-token path (or fuzzy attribute keys) must still match.
        const string driftedTarget = "<span  class=\"card-tag\"  ng-class=\"'priority-'+card.priority\"  ng-if=\"card.priority\">{{card.priority}}</span>";
        var (matchedBlock, _, err) = HtmlDomEditor.ResolveHtmlAnchor(html, driftedTarget, "remove the priority badge");
        Assert.NotNull(matchedBlock);
        Assert.Null(err);
        var (replaced, applied, matchError, _) = AgentEditHeuristics.TryReplaceSafe(html, matchedBlock!, "", 0, "remove the priority badge");
        Assert.True(replaced, $"deletion must apply: {matchError}");
        // Exactly ONE priority badge span survives (the second card's) after the drifted
        // anchor deleted only the first — count whole spans, not the substring (3x/span).
        Assert.Equal(1, CountOccurrences(applied, "<span class=\"card-tag\" ng-if=\"card.priority\""));
    }

    private static int CountOccurrences(string content, string block)
    {
        var count = 0;
        var pos = 0;
        while ((pos = content.IndexOf(block, pos, StringComparison.Ordinal)) >= 0)
        {
            count++;
            pos += block.Length;
        }
        return count;
    }


    // ═══════════════════════════════════════════════════════════════════════════
    //  CODE-FILE DELETE CORPUS VIA THE FORMAT D PAYLOAD ROUTE — targetType=method + targetName + no newCode
    // ═══════════════════════════════════════════════════════════════════════════
    // The non-HTML analog of the HTML delete corpus above: the payload is composed
    // through ParseStepFromJson with targetType=method + targetName + empty
    // newCode, targeting a .ts/.js/.cs file. HasConcreteEdit(step) is FALSE (same gate),
    // so the step routes through the resolution-driven deletion chain — but instead of
    // HtmlDomEditor.ResolveHtmlAnchor, the executor calls AgentController.AstResolveEdit
    // (private instance method, reads the file from disk, resolves via regex for .ts/.js
    // or Roslyn for .cs, returns the full leading-trivia + body source). Then
    // TryReplaceSafe(block, "", LineNumber, Change) removes exactly the resolved block.

    /// <summary>
    /// Invokes the real AgentController.AstResolveEdit (private instance method,
    /// reads the file from disk, resolves via regex for .ts/.js or Roslyn for .cs).
    /// Uses RuntimeHelpers.GetUninitializedObject to skip the DI constructor.
    /// </summary>
    private static (string? oldStr, string? error) InvokeAstResolveEdit(
        string fullPath, string targetType, string targetName)
    {
        var controller = RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        var method = typeof(AgentController).GetMethod(
            "AstResolveEdit",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("AstResolveEdit not found");
        var result = method.Invoke(controller,
            new object?[] { fullPath, targetType, targetName, false });
        var tuple = ((string? oldStr, string? error))result!;
        return tuple;
    }

    /// <summary>
    /// The full code-file deletion chain: write the source to a temp file, resolve the
    /// anchor via AstResolveEdit, then remove it via TryReplaceSafe.
    /// </summary>
    private static (bool replaced, string finalContent, string? error, string? oldStr) RunCodeDeleteChain(
        string source, string ext, string targetType, string targetName, int targetLine, string change)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "weaver-code-delete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var relPath = $"gen/module{ext}";
            var fullPath = Path.Combine(tempDir, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, source);

            var (oldStr, astErr) = InvokeAstResolveEdit(fullPath, targetType, targetName);
            if (oldStr == null)
                return (false, source, astErr, null);

            var (replaced, applied, matchError, _) = AgentEditHeuristics.TryReplaceSafe(
                source, oldStr, "", targetLine, change);
            return (replaced, applied, matchError, oldStr);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>Generate a .ts/.js code file with methods. When <paramref name="dupName"/>
    /// is set, the first TWO methods use that name with IDENTICAL bodies (byte-identical
    /// blocks, so TryReplaceSafe's duplicate detection fires). RNG-drawn sibling methods
    /// use the docIdx-scoped suffix.</summary>
    private static string BuildTsCodeFile(int docIdx, int methodCount, string? dupName = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Generated code file for corpus testing");
        sb.AppendLine();
        for (var k = 0; k < methodCount; k++)
        {
            var name = dupName != null && k < 2 ? dupName : $"method{docIdx}_{k}";
            // The first two methods share an IDENTICAL body (byte-identical blocks)
            // when dupName is set — the content differs only in the method name.
            var body = dupName != null && k < 2 ? $"// dup body {docIdx}" : $"// body {docIdx}-{k}";
            sb.AppendLine($"    private {name}(): void {{");
            sb.AppendLine($"        {body}");
            sb.AppendLine($"        return;");
            sb.AppendLine("    }");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Generate a .cs code file with a class and methods. When <paramref name="dupName"/>
    /// is set, the first TWO methods use that name with IDENTICAL bodies (byte-identical
    /// blocks, so TryReplaceSafe's duplicate detection fires).</summary>
    private static string BuildCsCodeFile(int docIdx, int methodCount, string? dupName = null, string? className = null)
    {
        var sb = new StringBuilder();
        var cls = className ?? $"CorpusClass{docIdx}";            sb.AppendLine("public class " + cls);
        sb.AppendLine("{");
        for (var k = 0; k < methodCount; k++)
        {
            var name = dupName != null && k < 2 ? dupName : $"method{docIdx}_{k}";
            // The first two methods share an IDENTICAL body (byte-identical blocks)
            // when dupName is set — the content differs only in the method name.
            var body = dupName != null && k < 2 ? $"// dup body {docIdx}" : $"// body {docIdx}-{k}";
            sb.AppendLine($"    public void {name}()");
            sb.AppendLine("    {");
            sb.AppendLine($"        {body}");
            sb.AppendLine("    }");
            sb.AppendLine();
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Write a code snippet to a temp file and return the full path. Caller
    /// responsible for cleanup.
    /// </summary>
    private static string WriteTempSource(string source, string ext)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "weaver-code-delete-temp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var fullPath = Path.Combine(tempDir, $"module{ext}");
        File.WriteAllText(fullPath, source);
        return fullPath;
    }

    /// <summary>
    /// 30 seeded docs cycling .ts, .js, and .cs extensions, each with a 5-variant
    /// structure: 0 unique target (removed, siblings intact), 1 byte-identical dup
    /// pair + keyword marker comment (marked occurrence removed, sibling survives),
    /// 2 dup pair + target line (nearest removed), 3 dup pair + no context (must
    /// refuse), 4 same-name target (all methods share a method name — the regex
    /// resolves the first occurrence, which is removed). Asserts the payload mapping
    /// (targetType/targetName preserved, targetName→TargetSymbol, newCode=null),
    /// the routing gate (HasConcreteEdit false), and the same anti-over-match
    /// guarantees through the AST-resolution chain.
    /// </summary>
    [Fact]
    public void Fuzz_FormatDPayload_EmptyNewCode_CodeFile_RoutesToAstResolution()
    {
        const int docCount = 30;
        const int seed = 60_620;
        const int prime = 104729;
        var checkedDocs = 0;
        var uniqueRemovals = 0;
        var keywordRemovals = 0;
        var lineRemovals = 0;
        var refusals = 0;
        var sameNameRemovals = 0;

        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(seed, i, prime);
            var docIdx = 600 + i;
            var variant = i % 5;

            // Cycle through .ts, .js, .cs per 3-doc group so all three extensions
            // are exercised (coprime with 5, so every variant × extension pair
            // appears at least once).
            var ext = (i % 3) switch { 0 => ".ts", 1 => ".js", _ => ".cs" };
            var isCs = ext == ".cs";
            var targetType = "method";

            var marker = FuzzHarness.DeleteMarkers[(i / 5) % FuzzHarness.DeleteMarkers.Length];
            var methodCount = 2 + rng.Next(3);
            string source, targetName;
            int targetLine = 0;
            string? dupName = null;

            switch (variant)
            {
                case 0:
                    targetName = $"method{docIdx}_0";
                    source = isCs ? BuildCsCodeFile(docIdx, methodCount) : BuildTsCodeFile(docIdx, methodCount);
                    break;
                case 1:
                    dupName = $"dup{i}";
                    targetName = dupName;
                    source = "// <!-- " + marker + " -->\n" + (isCs
                        ? BuildCsCodeFile(docIdx, methodCount, dupName: dupName)
                        : BuildTsCodeFile(docIdx, methodCount, dupName: dupName));
                    break;
                case 2:
                    dupName = $"dup{i}";
                    targetName = dupName;
                    source = isCs
                        ? BuildCsCodeFile(docIdx, methodCount, dupName: dupName)
                        : BuildTsCodeFile(docIdx, methodCount, dupName: dupName);
                    // The second occurrence of the same-named method starts around
                    // line 8 (comment + blank + class/open + method1 + blank + method2).
                    targetLine = 8;
                    break;
                case 3:
                    dupName = $"dup{i}";
                    targetName = dupName;
                    source = isCs
                        ? BuildCsCodeFile(docIdx, methodCount, dupName: dupName)
                        : BuildTsCodeFile(docIdx, methodCount, dupName: dupName);
                    break;
                default:
                    // Variant 4 — same-name target: all methods share the same name.
                    // The regex resolves the first occurrence; set targetLine to point
                    // at the first method (line ~4) so TryReplaceSafe picks the first
                    // match (distance 0) and removes it.
                    targetName = $"method{docIdx}_0";
                    source = isCs
                        ? BuildCsCodeFile(docIdx, methodCount, dupName: targetName)
                        : BuildTsCodeFile(docIdx, methodCount, dupName: targetName);
                    targetLine = 4;
                    break;
            }

            var change = variant switch
            {
                0 => "remove the method",
                1 => "remove the " + marker + " method",
                2 => "remove the method",
                3 => "remove the method",
                _ => "remove the first method",
            };

            // 1. Compose the PlanStep through the REAL ParseStepFromJson.
            var step = InvokeParseStepFromJson(
                $"gen/module{ext}", change, targetSymbol: null, line: targetLine,
                oldString: null, newString: null,
                refFiles: new List<string>(), edits: new List<EditPair>(),
                targetType: targetType, targetName: targetName, insertAfter: null,
                newCode: null, fullFile: null);

            // 2. Payload mapping assertions.
            Assert.Equal(targetType, step.TargetType);
            Assert.Equal(targetName, step.TargetName);
            Assert.Equal(targetName, step.TargetSymbol);
            Assert.Equal(targetLine, step.LineNumber);
            Assert.Null(step.NewCode);
            Assert.Null(step.NewString);
            Assert.Null(step.OldString);
            Assert.Null(step.FullFile);

            // 3. ROUTING GATE: HasConcreteEdit is false (same gate as HTML).
            Assert.False(InvokeHasConcreteEdit(step),
                $"doc #{i}: empty-newCode payload must not be a concrete edit");

            // 4. Run the AST-resolution deletion chain.
            var (replaced, applied, error, oldStr) = RunCodeDeleteChain(
                source, ext, targetType, targetName, targetLine, change);

            switch (variant)
            {
                case 0:
                    Assert.True(replaced, $"doc #{i} unique target must delete: {error}");
                    Assert.NotNull(oldStr);
                    Assert.DoesNotContain(oldStr!, applied, StringComparison.Ordinal);
                    uniqueRemovals++;
                    break;
                case 1:
                    Assert.True(replaced, $"doc #{i} keyword target must delete: {error}");
                    Assert.NotNull(oldStr);
                    // The marker comment is NOT part of the method body — it's a separate
                    // line before the class. It survives the edit. The oldStr (the first
                    // method with the duplicate name) is gone.
                    Assert.Contains("// <!-- " + marker + " -->", applied, StringComparison.Ordinal);
                    // The second occurrence of the same method name survives.
                    Assert.Contains(targetName, applied, StringComparison.Ordinal);
                    keywordRemovals++;
                    break;
                case 2:
                    Assert.True(replaced, $"doc #{i} line target must delete: {error}");
                    Assert.NotNull(oldStr);
                    Assert.Contains(targetName, applied, StringComparison.Ordinal);
                    lineRemovals++;
                    break;
                case 3:
                    Assert.False(replaced, $"doc #{i} duplicate with no context must refuse");
                    Assert.Equal(source, applied);
                    Assert.NotNull(error);
                    Assert.Contains("times in file", error);
                    refusals++;
                    break;
                default:
                    Assert.True(replaced, $"doc #{i} same-name target must delete: {error}");
                    Assert.NotNull(oldStr);
                    // The first occurrence (resolved by AstResolveEdit's regex/Roslyn) was
                    // removed. The file got shorter by at least the oldStr length. The
                    // method name still appears (sibling methods survive).
                    Assert.True(applied.Length < source.Length,
                        $"doc #{i}: applied length {applied.Length} >= source length {source.Length}");
                    Assert.Contains(targetName, applied, StringComparison.Ordinal);
                    sameNameRemovals++;
                    break;
            }

            checkedDocs++;
        }

        FuzzHarness.AssertAllDocsChecked(checkedDocs, docCount, "FORMAT D code-file empty-newCode delete corpus");
        FuzzHarness.AssertExercised(uniqueRemovals, "no doc exercised the unique-target code-file deletion path");
        FuzzHarness.AssertExercised(keywordRemovals, "no doc exercised the keyword-disambiguated code-file deletion path");
        FuzzHarness.AssertExercised(lineRemovals, "no doc exercised the target-line-disambiguated code-file deletion path");
        FuzzHarness.AssertExercised(refusals, "no doc exercised the code-file duplicate-refusal path");
        FuzzHarness.AssertExercised(sameNameRemovals, "no doc exercised the same-name code-file deletion path");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  FORMAT D ALREADY-DONE — removal-with-survivor protection (kanban priority badge)
    // ═══════════════════════════════════════════════════════════════════════════
    // Mirrors the wild bug: the kanban.html "remove the priority badge" deletion arrived
    // as a FORMAT D payload whose targetName was the FULL block (survivor BENCH span +
    // priority badge span) and newCode was either empty (pure deletion) or the surviving
    // fragment. The generic "HTML block already present" guard must NOT fire just because
    // the survivor fragment is present — only the FULL targetName block being absent
    // proves the removal already happened. This locks the FormatDAlreadyDoneVerdict
    // helper the resolver now consults in both FORMAT D branches (~1317 / ~1954).

    /// <summary>
    /// Invokes the real <c>AgentController.FormatDAlreadyDoneVerdict</c> (private static)
    /// — the shared already-done verdict used by both FORMAT D execution branches.
    /// </summary>
    private static (bool alreadyDone, string reason) InvokeFormatDAlreadyDoneVerdict(
        string sourceText, string? targetName, string? newCode)
    {
        var method = typeof(AgentController).GetMethod(
            "FormatDAlreadyDoneVerdict", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("FormatDAlreadyDoneVerdict not found");
        var result = method.Invoke(null, new object?[] { sourceText, targetName, newCode })
            ?? throw new InvalidOperationException("FormatDAlreadyDoneVerdict returned null");
        return ((bool, string))result;
    }

    /// <summary>Byte-mirror of the kanban.html To Do card card-tags section: a BENCH
    /// survivor span + the priority badge span that the wild deletion targeted.</summary>
    private static string BuildKanbanTagsDoc(int cardIdx)
    {
        return "  <div class=\"card\" id=\"c" + cardIdx + "\">\n" +
            "    <textarea>text</textarea>\n" +
            "    <div class=\"card-tags\">\n" +
            "      <span class=\"card-tag tag-bench\" ng-if=\"card._benchmark\" style=\"color:#e5c07b;font-weight:700;\">BENCH</span>\n" +
            "      <span class=\"card-tag\" ng-if=\"card.priority\" ng-class=\"'priority-'+card.priority\">{{card.priority}}</span>\n" +
            "      <span class=\"card-tag\" ng-if=\"card.flagged\">FLAGGED</span>\n" +
            "    </div>\n" +
            "  </div>";
    }

    [Fact]
    public void Fuzz_FormatD_RemovalWithSurvivor_GuardChecksFullBlockNotFragment()
    {
        const int docCount = 24;
        var checkedDocs = 0;
        for (var i = 0; i < docCount; i++)
        {
            var html = "<main>\n" + BuildKanbanTagsDoc(i) + "\n</main>";
            var survivor = "      <span class=\"card-tag tag-bench\" ng-if=\"card._benchmark\" style=\"color:#e5c07b;font-weight:700;\">BENCH</span>";
            var targetBlock = "      <span class=\"card-tag\" ng-if=\"card.priority\" ng-class=\"'priority-'+card.priority\">{{card.priority}}</span>";
            var fullBlock = survivor + "\n" + targetBlock;

            // Case A — pure deletion (empty/absent newCode): targetName = the priority span
            // itself. The block is present → NOT already done (the removal must proceed).
            var (aDone, aReason) = InvokeFormatDAlreadyDoneVerdict(html, targetBlock, "");
            Assert.False(aDone, $"doc #{i}: deletion payload on present target must NOT be already-done: {aReason}");

            // Case B — replace-with-survivor (newCode = surviving fragment): targetName is
            // the FULL block (BENCH + priority), newCode is the BENCH fragment. The survivor
            // IS present, but the full target block still is too → NOT already done.
            var (bDone, bReason) = InvokeFormatDAlreadyDoneVerdict(html, fullBlock, survivor);
            Assert.False(bDone, $"doc #{i}: survivor fragment + present full block must NOT be already-done: {bReason}");

            // Apply the Case B removal for real: resolve the full block anchor, replace it
            // with the survivor → the priority badge is gone, BENCH + FLAGGED survive.
            var (matched, _, err) = HtmlDomEditor.ResolveHtmlAnchor(html, fullBlock, "Remove the priority badge");
            Assert.NotNull(matched);
            Assert.Null(err);
            var (replaced, applied, matchError, _) = AgentEditHeuristics.TryReplaceSafe(html, matched!, survivor, 0, "Remove the priority badge");
            Assert.True(replaced, $"doc #{i}: survivor replace must apply: {matchError}");
            Assert.Contains("BENCH", applied, StringComparison.Ordinal);
            Assert.Contains("FLAGGED", applied, StringComparison.Ordinal);
            Assert.DoesNotContain("card.priority", applied);

            // AFTER the removal: the full target block is gone → BOTH verdicts flip to
            // already-done (no double work on re-run), and the survivor fragment alone is
            // NOT mistaken for "still present" evidence.
            var (aDone2, aReason2) = InvokeFormatDAlreadyDoneVerdict(applied, targetBlock, "");
            Assert.True(aDone2, $"doc #{i}: deleted target must now be already-done: {aReason2}");
            var (bDone2, bReason2) = InvokeFormatDAlreadyDoneVerdict(applied, fullBlock, survivor);
            Assert.True(bDone2, $"doc #{i}: removed full block must now be already-done: {bReason2}");
            Assert.Contains("absent", aReason2, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("absent", bReason2, StringComparison.OrdinalIgnoreCase);

            checkedDocs++;
        }
        FuzzHarness.AssertAllDocsChecked(checkedDocs, docCount, "FORMAT D removal-with-survivor guard corpus");
    }

    [Fact]
    public void FormatD_AlreadyDone_WhitespaceDriftedSurvivor_AlsoUsesFullBlock()
    {
        // The COLLAPSED-fragment path: newCode is NOT a verbatim substring of targetName
        // (indentation drift between the emitted survivor and the same lines inside the
        // full target block), but whitespace-collapsed it IS a strict fragment. The guard
        // must still use the FULL target block's absence — not the drifted survivor.
        var html = "<main>\n" +
            "  <div class=\"card-tags\">\n" +
            "      <span class=\"card-tag tag-bench\">BENCH</span>\n" +
            "      <span class=\"card-tag\" ng-if=\"card.priority\">{{card.priority}}</span>\n" +
            "  </div>\n" +
            "</main>";
        // targetName = the full 6-space-indented block (BENCH + priority lines).
        var fullBlock = "      <span class=\"card-tag tag-bench\">BENCH</span>\n" +
            "      <span class=\"card-tag\" ng-if=\"card.priority\">{{card.priority}}</span>";
        // newCode = the survivor re-indented to 4 spaces — NOT an exact substring of
        // fullBlock, but an exact substring of the file (the file's BENCH line IS
        // 6-space-indented, so this drifted survivor is absent verbatim too — only the
        // collapsed comparison links it).
        var driftedSurvivor = "    <span class=\"card-tag tag-bench\">BENCH</span>";

        // Survivor (drifted) present + full target block present → NOT already done.
        var (notDone, notReason) = InvokeFormatDAlreadyDoneVerdict(html, fullBlock, driftedSurvivor);
        Assert.False(notDone, $"drifted survivor + present full block must NOT be already-done: {notReason}");

        // Apply the real removal: resolve the full block anchor, replace with the drifted
        // survivor → priority badge gone, BENCH survives.
        var (matched, _, err) = HtmlDomEditor.ResolveHtmlAnchor(html, fullBlock, "Remove the priority badge");
        Assert.NotNull(matched);
        Assert.Null(err);
        var (replaced, applied, matchError, _) = AgentEditHeuristics.TryReplaceSafe(html, matched!, driftedSurvivor, 0, "Remove the priority badge");
        Assert.True(replaced, $"drifted survivor replace must apply: {matchError}");
        Assert.Contains("BENCH", applied, StringComparison.Ordinal);
        Assert.DoesNotContain("card.priority", applied);

        // After removal: full target block gone → already-done, even though the drifted
        // survivor remains.
        var (done, doneReason) = InvokeFormatDAlreadyDoneVerdict(applied, fullBlock, driftedSurvivor);
        Assert.True(done, $"removed full block must now be already-done: {doneReason}");
        Assert.Contains("absent", doneReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatD_AlreadyDone_PlainInsertStillTripsGuard()
    {
        // NEGATIVE CONTROL — the guard is NOT disabled: a genuine insertion whose newCode
        // is already present (and is NOT a fragment of targetName) still trips AlreadyDone.
        var html = "<main>\n  <div class=\"panel\">x</div>\n</main>";
        var (done, reason) = InvokeFormatDAlreadyDoneVerdict(html, "<div class=\"panel\">", "<div class=\"panel\">x</div>");
        Assert.True(done, $"plain insert of present code must be already-done: {reason}");
        Assert.Contains("already present", reason, StringComparison.OrdinalIgnoreCase);
    }
}
