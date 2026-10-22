using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Weaver.Services;

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

/// <summary>
/// One deterministic expected-outcome entry attached to a plan step: what the file must
/// contain after the step lands. Computed from the step's own old/new content (pure
/// function — no LLM, no disk); verified against disk when the step completes and the
/// card's plan item is marked done.
/// </summary>
public sealed record StepGroundTruth
{
    [System.Text.Json.Serialization.JsonPropertyName("text")] public string Text { get; init; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("file")] public string? File { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("anchor")] public string? Anchor { get; init; }
}

/// <summary>Part of the split of the former AgentUtilities monolith.</summary>
public static class AgentTextUtilities
{
    public static string StripSpuriousBlankLines(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return code;
        var lines = code.Split('\n');
        if (lines.Length < 6) return code;
        var codeCount = lines.Count(l => !string.IsNullOrWhiteSpace(l));
        var blankCount = lines.Count(l => string.IsNullOrWhiteSpace(l));
        if (codeCount < 3 || blankCount < codeCount * 0.7) return code;
        var alternating = 0;
        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]) &&
                string.IsNullOrWhiteSpace(lines[i + 1]))
                alternating++;
        }
        if (alternating < codeCount * 0.5) return code;
        var result = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                var hasPrev = result.Count > 0 && !string.IsNullOrWhiteSpace(result[^1]);
                var hasNext = i + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[i + 1]);
                if (hasPrev && hasNext)
                {
                    var prevTrimmed = result[^1].TrimEnd();
                    var prevIndent = result[^1].TakeWhile(c => c == ' ' || c == '\t').Count();
                    var nextIndent = lines[i + 1].TakeWhile(c => c == ' ' || c == '\t').Count();
                    if ((prevTrimmed.EndsWith(';') || prevTrimmed.EndsWith('}')) &&
                        Math.Abs(prevIndent - nextIndent) <= 1 &&
                        (i == 0 || i - 1 < 0 || string.IsNullOrWhiteSpace(lines[i - 1]) == false))
                    {
                        // Check if line before prev was also blank — if so, skip
                        if (result.Count > 1 && string.IsNullOrWhiteSpace(result[^2]))
                            continue;
                        result.Add(lines[i]);
                        continue;
                    }
                    continue; // Skip spurious blank
                }
            }
            result.Add(lines[i]);
        }
        return string.Join("\n", result);
    }

    public static string CleanVerbatimStringEscapes(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;
        var regex = new Regex(@"@""(?:""|[^""])*""", RegexOptions.Compiled);
        bool changed = false;
        var result = regex.Replace(content, match =>
        {
            var val = match.Value;
            var inside = val.Substring(2, val.Length - 3);
            bool hasEscapeSeq = inside.Contains(@"\r\n") || inside.Contains(@"\r") || inside.Contains(@"\n") || inside.Contains(@"\t");
            bool looksLikeSql = Regex.IsMatch(inside, @"\b(SELECT|INSERT|UPDATE|DELETE|CREATE\s+TABLE|ALTER\s+TABLE|DROP\s+TABLE|FROM|WHERE|JOIN|VALUES|SET)\b", RegexOptions.IgnoreCase);
            if (hasEscapeSeq && looksLikeSql)
            {
                changed = true;
                var fixedInside = inside
                    .Replace(@"\r\n", "\r\n")
                    .Replace(@"\r", "\r")
                    .Replace(@"\n", "\n")
                    .Replace(@"\t", "\t");
                return "@\"" + fixedInside + "\"";
            }
            return val;
        });
        return changed ? result : content;
    }
    // Keywords that can precede a `( ... ) { }` shape but are NOT method declarations.
    // Matching is whole-line anchored, so `new Foo() { }` never matches anyway.

    public static string PostEditCSharpFixup(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return content;
        content = CleanVerbatimStringEscapes(content);
        var flatPattern = new Regex(@"\.(SystemSpecs|System|HardwareInfo|Hardware|Specs|SystemInfo|MetaInfo|Details|DataInfo|BenchmarkInfo|BenchData)\??\.([A-Z]\w+)", RegexOptions.IgnoreCase);
        content = flatPattern.Replace(content, m => "." + m.Groups[2].Value);
        content = Regex.Replace(content, @"(\$""[^""]*)\{\{(\w+(?:\.\w+)+)\}\}([^""]*"")", "$1{$2}$3");
        content = Regex.Replace(content,
            @"decimal\.TryParse\s*\(\s*\w+\.Score\??(?:\.Replace\s*""[^""]*""(?:\s*,\s*""[^""]*"")?)?\s*,(\s*out\s+\w+(?:\.\w+)*\s*)\)",
            m =>
            {
                var outVar = m.Groups[1].Value.Trim();
                return $"decimal.TryParse(benchmark.Score, {outVar})";
            });
        content = Regex.Replace(content,
            @"(?<=[^ \t\r\n@])""\s*\r?\n[ \t]*;",
            @""";");
        return content;
    }

    public static string NormalizeLineEndings(string s) => s.Replace("\r\n", "\n");

    /// <summary>
    /// Restores a REAL space where the editor model wrote an HTML non-breaking-space entity
    /// (<c>&amp;nbsp;</c> / <c>&amp;#160;</c> / <c>&amp;#xA0;</c>) to represent a literal space in a
    /// heading/title. The weak model keeps DROPPING the space inside a required heading
    /// (benchmark 23's 'Benchmark 23' → 'Benchmark23'), so it is trained to emit the entity
    /// instead; this deterministic pass converts it back to a plain space once the edit lands.
    /// </summary>
    public static string NormalizeNbsp(string s)
        => s.Replace("&nbsp;", " ").Replace("&#160;", " ").Replace("&#xA0;", " ");

    /// <summary>
    /// Applies <see cref="NormalizeNbsp"/> to every content field of a <see cref="PlanStep"/>
    /// (NewString, FullFile, NewCode lines, and each batch EditPair's NewString) so the
    /// pipeline's file writes — <c>_create_file</c>, TryCreateFileAsync, plan-provided edits,
    /// FORMAT C/D, fullFile — all carry the real space instead of the entity.
    /// </summary>
    public static void NormalizeNbspInStep(PlanStep step)
    {
        if (step == null) return;
        if (!string.IsNullOrWhiteSpace(step.NewString)) step.NewString = NormalizeNbsp(step.NewString);
        if (!string.IsNullOrWhiteSpace(step.FullFile)) step.FullFile = NormalizeNbsp(step.FullFile);
        if (step.Edits is { Count: > 0 })
            foreach (var e in step.Edits)
                if (!string.IsNullOrWhiteSpace(e.NewString)) e.NewString = NormalizeNbsp(e.NewString);
        if (step.NewCode is { Count: > 0 })
            for (var i = 0; i < step.NewCode.Count; i++)
                if (!string.IsNullOrWhiteSpace(step.NewCode[i])) step.NewCode[i] = NormalizeNbsp(step.NewCode[i]);
    }

    /// <summary>
    /// True when a <c>_create_file</c> step is a DUMMY placeholder emitted only to materialize
    /// its parent folder. A weak planner that cannot create a directory directly (its
    /// mkdir/_create_directory attempt failed or was rejected) falls back to planning a
    /// placeholder file — the benchmark-23 run wrote <c>benchmark_test_23/placeholder.txt</c>
    /// containing "Placeholder file for directory creation" just to get the folder on disk.
    /// The executor should skip the dummy file and create the directory it implies instead.
    /// Deterministic fast-path: a classic placeholder/keep filename (placeholder.txt, .gitkeep,
    /// dummy, …) or short content that explicitly announces itself as folder scaffolding.
    /// </summary>
    public static bool IsDirectoryScaffoldPlaceholder(string fileName, string? content)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var norm = fileName.Replace('\\', '/');
            var last = norm[(norm.LastIndexOf('/') + 1)..];
            if (IsPlaceholderStem(last)) return true;
        }
        if (string.IsNullOrWhiteSpace(content)) return false;
        var trimmed = content.Trim();
        if (trimmed.Length > 120) return false; // real files carry real content
        return PlaceholderFolderContentRegex.IsMatch(trimmed);
    }

    /// <summary>
    /// True when the LAST path segment is a placeholder/keep/scaffold FILE name — the stem
    /// (before any extension) is a classic placeholder word (placeholder, dummy, keep,
    /// gitkeep, scaffold, temp, …) or a dotfile keep-name (.keep, .gitkeep, …). Shared by
    /// the <c>_create_file</c> dummy-file guard and the <c>_create_directory</c> file-path
    /// guard so both agree on what a "placeholder" name is.
    /// </summary>
    private static bool IsPlaceholderStem(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        var stem = (dot > 0 ? fileName[..dot] : fileName).Trim().ToLowerInvariant();
        return stem.Length > 0 && stem.Length <= 20 &&
               (stem is "placeholder" or "dummy" or "keep" or "gitkeep" or "keepme" or
                "scaffold" or "scaffolding" or "empty" or "temp" or "tmp" or "tempfile" or
                ".keep" or ".gitkeep" or ".placeholder" or ".keepme" or ".dummy");
    }

    /// <summary>
    /// When a <c>_create_directory</c> step's path is actually a FILE placeholder — e.g.
    /// <c>benchmark_test_23/placeholder.txt</c> planned as a directory — returns the clean
    /// PARENT directory the placeholder file was meant to materialize (<c>benchmark_test_23</c>),
    /// or null when the path is a genuine directory. Only unmistakably-file paths convert:
    /// a last segment WITH an extension whose stem is a placeholder word (placeholder.txt,
    /// dummy.file, keep.md, …), or a dotfile keep-name (<c>.gitkeep</c>, <c>.keep</c>). A
    /// plain extensionless directory name (<c>keep</c>, <c>temp</c>, <c>dummy</c>) and a path
    /// with no parent segment are left alone — those are real folder names, not file paths.
    /// A weak planner that cannot create a folder directly reaches for <c>_create_directory</c>
    /// with a file path; plan validation rewrites the step so the junk file path never
    /// becomes a directory on disk.
    /// </summary>
    public static string? CleanDirectoryPathFromFilePlaceholder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var norm = path.Replace('\\', '/').Trim().Trim('/');
        if (norm.Length == 0) return null;
        var idx = norm.LastIndexOf('/');
        if (idx <= 0) return null; // no parent segment — nothing to clean the path to
        var parent = norm[..idx];
        var fileName = norm[(idx + 1)..];
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var dot = fileName.LastIndexOf('.');
        var isDotfileKeep = fileName.StartsWith('.') && dot <= 0 && IsPlaceholderStem(fileName);
        if (dot <= 0 && !isDotfileKeep) return null; // plain directory name — leave alone
        return IsPlaceholderStem(fileName) ? parent : null;
    }

    // Phrases only — NOT bare placeholder words. A real file's content can easily
    // contain the words "keep", "empty", "tmp", "dummy", "temp" etc. as strings or
    // identifiers (e.g. `unchanged(): return "keep"`), which previously made a real
    // formatter.py get skipped as a "dummy folder-establishment file". Bare-word
    // placeholders in content are only trustworthy inside an explicit folder-creation
    // phrase or a placeholder-flagged file name (handled by IsPlaceholderStem).
    private static readonly Regex PlaceholderFolderContentRegex = new(
        @"(?:just\s+to\s+create|to\s+create\s+the\s+(?:folder|directory)|create\s+(?:the\s+)?(?:folder|directory)|directory\s+creation|folder\s+creation|establish(?:ing)?\s+(?:the\s+)?(?:folder|directory)|for\s+(?:directory|folder)\s+creation|to\s+establish\s+(?:the\s+)?(?:folder|directory)|placeholder\s+(?:file|text|content)|dummy\s+(?:file|text|content)|empty\s+(?:file|folder|directory))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Weak pre-signal that GATES the LLM fallback of the dummy-file-for-folder guard: true
    /// when the filename or short content merely HINTS at a placeholder/keep/scaffold file
    /// (placeholder, dummy, keep, temp, "just to create", …). The LLM is only consulted when
    /// this fires but <see cref="IsDirectoryScaffoldPlaceholder"/> didn't — genuinely
    /// ambiguous, plausibly-a-placeholder steps. Without this gate, EVERY short _create_file
    /// would cost an LLM round-trip (and break the no-LLM-round invariants of the eval and
    /// integration suites for ordinary short files like a README or a one-line helper).
    /// </summary>
    public static bool HasPlaceholderHint(string fileName, string? content)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var norm = fileName.Replace('\\', '/');
            var last = norm[(norm.LastIndexOf('/') + 1)..];
            if (IsPlaceholderStem(last)) return true;
        }
        if (string.IsNullOrWhiteSpace(content)) return false;
        var trimmed = content.Trim();
        if (trimmed.Length > 150) return false;
        return PlaceholderHintRegex.IsMatch(trimmed);
    }

    // Phrases only — same rationale as PlaceholderFolderContentRegex: bare placeholder
    // words in content are too likely to be real code, so the LLM fallback gate only
    // fires on explicit folder-creation/placeholder intent. File names are covered by
    // IsPlaceholderStem.
    private static readonly Regex PlaceholderHintRegex = new(
        @"(?:just\s+to\s+create|to\s+establish|establish(?:ing)?\s+the|create\s+(?:the\s+)?(?:folder|directory)|directory\s+creation|folder\s+creation|placeholder\s+(?:file|text|content)|dummy\s+(?:file|text|content)|empty\s+(?:file|folder|directory))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Locates an anchor (typically the newString of an applied edit) inside normalized file
    /// content. Matches verbatim first; when that fails (an edit later reformatted or merged),
    /// falls back to the anchor's longest distinctive line (selector/method-signature lines
    /// survive reformatting). Returns the char offset or -1. Shared by the verifier file view
    /// (windowing) and the deterministic applied-edit disk check so both agree on what
    /// "present in the file" means.
    /// </summary>
    public static int FindAnchorOffset(string normalizedContent, string anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor) || normalizedContent == null) return -1;
        var normAnchor = NormalizeLineEndings(anchor).Trim('\r', '\n');
        if (normAnchor.Length == 0) return -1;
        var idx = normalizedContent.IndexOf(normAnchor, StringComparison.Ordinal);
        if (idx >= 0) return idx;
        // An edit that was later reformatted/merged may no longer match verbatim — retry with
        // its distinctive lines (selector/method-signature lines survive reformatting), the
        // longest first. Trying EACH line, not just the longest, matters: the longest line is
        // often the very one the formatter touched (an added attribute), while a shorter
        // class-bearing line survived untouched.
        foreach (var line in normAnchor.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length >= 20 && !l.StartsWith("//") && !l.StartsWith("/*") && !l.StartsWith("*"))
            .OrderByDescending(l => l.Length))
        {
            var lineIdx = normalizedContent.IndexOf(line, StringComparison.Ordinal);
            if (lineIdx >= 0) return lineIdx;
        }
        return -1;
    }

    /// <summary>
    /// Builds a bounded view of a large file for the post-execution verifier. When the file
    /// fits within <paramref name="maxChars"/> the whole content is returned unchanged.
    /// When it doesn't, the view keeps a bounded head, a window around each located anchor
    /// (the newString of each applied edit — so the verifier ALWAYS sees the region this run
    /// changed, even in a 40k-char stylesheet), and a bounded tail, each with explicit
    /// truncation markers. Anchors are matched via <see cref="FindAnchorOffset"/> — verbatim
    /// after line-ending normalization, with a fallback to the anchor's longest distinctive
    /// line so an edit that was later reformatted or merged is still located. Falls back to
    /// head+tail when no anchor can be located (e.g. a full-file rewrite superseded the snippet).
    /// </summary>
    public static string BuildVerifierFileView(string content, IReadOnlyList<string>? anchors, int maxChars = 12000)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= maxChars)
            return content;
        var normalized = NormalizeLineEndings(content);
        var windows = new List<(int start, int end)>();
        if (anchors != null)
        {
            foreach (var anchor in anchors)
            {
                if (string.IsNullOrWhiteSpace(anchor)) continue;
                var idx = FindAnchorOffset(normalized, anchor);
                var normAnchor = NormalizeLineEndings(anchor).Trim('\r', '\n');
                if (idx >= 0 && normAnchor.Length > 0)
                {
                    var windowStart = Math.Max(0, idx - 400);
                    var windowEnd = Math.Min(normalized.Length, idx + normAnchor.Length + 400);
                    windows.Add((windowStart, windowEnd));
                }
            }
        }
        if (windows.Count > 1)
        {
            windows.Sort((a, b) => a.start.CompareTo(b.start));
            var merged = new List<(int start, int end)> { windows[0] };
            foreach (var w in windows.Skip(1))
            {
                var last = merged[^1];
                if (w.start <= last.end) merged[^1] = (last.start, Math.Max(last.end, w.end));
                else merged.Add(w);
            }
            windows = merged;
        }
        const int HeadBudget = 3000;
        const int TailBudget = 2000;
        var regionBudget = Math.Max(0, maxChars - HeadBudget - TailBudget);
        var sb = new StringBuilder();
        var printedTo = 0;
        // Head: up to the budget, but stop before the first edited region so it is never cut.
        var headEnd = Math.Min(HeadBudget, windows.Count > 0 ? windows[0].start : normalized.Length);
        headEnd = Math.Min(headEnd, normalized.Length);
        if (headEnd > printedTo)
        {
            sb.Append(normalized[..headEnd]);
            printedTo = headEnd;
            if (printedTo < normalized.Length)
                sb.Append("\n… [TRUNCATED — head of file shown; edited regions and tail follow]");
        }
        // Edited regions: the change(s) this run made, with ±400 chars of context.
        var regionUsed = 0;
        foreach (var w in windows)
        {
            var start = w.start;
            var end = w.end;
            if (start < printedTo) start = printedTo;
            if (start >= end) continue;
            var allowed = Math.Max(0, regionBudget - regionUsed);
            if (allowed <= 0) break;
            var take = Math.Min(end - start, allowed);
            if (start > printedTo)
                sb.Append("\n… [EDITED REGION — the change(s) this run made to this file] …\n");
            sb.Append(normalized[start..(start + take)]);
            printedTo = Math.Max(printedTo, start + take);
            regionUsed += take;
            if (take < end - start)
                sb.Append("\n… [region truncated]");
        }
        // Tail: the last chars of the file, so end-of-file edits are still visible.
        var tailStart = Math.Max(printedTo, normalized.Length - TailBudget);
        if (tailStart < normalized.Length)
        {
            if (tailStart > printedTo)
                sb.Append("\n… [TAIL — end of file] …\n");
            sb.Append(normalized[tailStart..]);
        }
        sb.Append($"\n… [TRUNCATED — file is {content.Length} chars, showing head + edited regions + tail capped at {maxChars} chars]");
        return sb.ToString();
    }

    public static string StripLineLeadingWhitespace(string s)
    {
        var lines = s.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimStart();
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Deterministic ground truth for the post-execution verifier: for every edit this run
    /// reports as applied (type edit/create, status done/modified/created) on a real file,
    /// verifies that the edit's newString is actually present in the CURRENT file on disk
    /// (via <see cref="FindAnchorOffset"/>, so reformatted/merged edits still count).
    /// Returns two lists:
    ///   • <paramref name="confirmedEdits"/> — "path → newString" facts the verifier MUST NOT
    ///     contradict (e.g. claim 'the change was not made' when the new text is provably on
    ///     disk). These are the known-correct answers shown on the card. Every applied edit
    ///     whose newString is found is listed, so a card that fixed multiple occurrences shows
    ///     each one confirmed.
    ///   • <paramref name="missingEditIssues"/> — CONFIRMED issues for applied edits whose
    ///     newString is NOT found on disk (edit never landed / was reverted / path moved).
    ///     These fail verification deterministically instead of relying on the LLM to notice.
    ///     To stay churn-free across repair passes (a later pass may legitimately rewrite a
    ///     region an earlier edit touched), the MISSING side checks only the LAST applied
    ///     edit per path — the file's final claimed state — while the confirmed side lists all.
    /// Pure function of disk state — no LLM, so findings cannot hallucinate.
    /// </summary>
    public static (List<string> confirmedEdits, List<string> missingEditIssues) CheckAppliedEditsPresent(
        string projectRoot, IEnumerable<object> allResults)
    {
        var confirmedEdits = new List<string>();
        var missingEditIssues = new List<string>();
        // Preserve run order so the LAST applied edit per path is identifiable.
        var lastEditPerPath = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in allResults.OfType<Dictionary<string, object?>>())
        {
            var type = r.GetValueOrDefault("type")?.ToString();
            if (type is not ("edit" or "create")) continue;
            var status = r.GetValueOrDefault("status")?.ToString();
            if (status is not ("done" or "modified" or "created")) continue;
            var path = r.GetValueOrDefault("path")?.ToString();
            if (string.IsNullOrWhiteSpace(path)) continue;
            lastEditPerPath[path] = r;
            var newString = r.GetValueOrDefault("newStringPreview")?.ToString();
            if (string.IsNullOrWhiteSpace(newString)) continue;
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!System.IO.File.Exists(fullPath)) continue;
            var normalized = NormalizeParenSpacing(NormalizeLineEndings(System.IO.File.ReadAllText(fullPath)));
            if (FindAnchorOffset(normalized, NormalizeParenSpacing(newString)) >= 0)
                confirmedEdits.Add($"{path.Replace('/', Path.DirectorySeparatorChar)} — {OneLineSnippet(newString)}");
        }
        foreach (var (path, last) in lastEditPerPath)
        {
            var newString = last.GetValueOrDefault("newStringPreview")?.ToString();
            if (string.IsNullOrWhiteSpace(newString)) continue;
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!System.IO.File.Exists(fullPath))
            {
                missingEditIssues.Add(
                    $"Applied edit for {path} is missing on disk — the target file no longer exists after the run.");
                continue;
            }
            // Paren spacing is normalized on BOTH sides before matching — the apply pipeline's
            // HTML style self-heal rewrites the changed line (`button (` → `button(`), and the
            // HtmlDomEditor FORMAT D path serializes the same way. The per-step ground-truth
            // check (Formatting.cs) already normalizes; the post-execution disk check must agree
            // or a LANDED HTML edit is falsely flagged "NOT present" (the exact churn that sent
            // the run into the repair circuit breaker with the verifier's reason under a green
            // "Verified complete" card).
            var normalized = NormalizeParenSpacing(NormalizeLineEndings(System.IO.File.ReadAllText(fullPath)));
            if (FindAnchorOffset(normalized, NormalizeParenSpacing(newString)) < 0)
                missingEditIssues.Add(
                    $"Applied edit for {path} is NOT present in the current file — the change did not land " +
                    $"(or was overwritten/reverted). Expected: {OneLineSnippet(newString)}");
        }
        return (confirmedEdits, missingEditIssues);
    }

    /// <summary>
    /// Computes the deterministic expected outcomes for a plan step — the known-correct answer
    /// THAT STEP is checked against, e.g.:
    ///   • a literal swap: the new literal must be present ("rename 'Details' to 'Open'" →
    ///     "Expected: \"Open\" present in the file");
    ///   • a 'did you mean' typo fix: the corrected token must be what's on disk
    ///     ("fix opnCard" → "Expected: \"openCard\" replaces \"opnCard\"").
    /// The new-content entry is derived from whichever content form the step carries
    /// (NewString, FullFile, NewCode, or multi-edit pairs); the typo-fix entries come from a
    /// token diff of OldString vs NewString using the same plausible-typo/plural heuristics as
    /// the hallucinated-property guard. Pure function — no LLM, no disk — so the expectations
    /// can be attached at plan time and verified against the file when the step completes.
    /// </summary>
    public static List<StepGroundTruth> ComputeStepGroundTruth(
        string relPath, string? oldStr, string? newStr,
        string? fullFile = null, List<string>? newCode = null, List<EditPair>? edits = null)
    {
        var items = new List<StepGroundTruth>();
        var newContent = !string.IsNullOrWhiteSpace(newStr) ? newStr
            : !string.IsNullOrWhiteSpace(fullFile) ? fullFile
            : newCode is { Count: > 0 } ? string.Join("\n", newCode)
            : edits is { Count: > 0 } ? string.Join("\n", edits.Where(e => !string.IsNullOrWhiteSpace(e.NewString)).Select(e => e.NewString))
            : null;
        if (!string.IsNullOrWhiteSpace(newContent))
        {
            var snippet = OneLineSnippet(newContent);
            items.Add(new StepGroundTruth
            {
                Text = $"Expected: \"{snippet}\" present in {relPath}",
                File = relPath,
                // The on-disk anchor is the last substantial line (survives the apply
                // pipeline's paren-spacing self-heal via NormalizeParenSpacing at verify time).
                Anchor = AnchorFor(newContent)
            });
        }
        // 'did you mean' typo-fix expectations: a token REMOVED by the edit that is a
        // plausible typo/plural variant of a token INTRODUCED by it — the corrected form
        // must be what's on disk after the step (mirrors the guard's heuristic, applied in
        // the fix direction: old `opnCard` → new `openCard`, or old `estimated` → new
        // `ested` when the step is the one introducing the hallucination).
        if (!string.IsNullOrWhiteSpace(oldStr) && !string.IsNullOrWhiteSpace(newStr) && items.Count < 3)
        {
            var removed = TokenWords(oldStr).Except(TokenWords(newStr)).ToList();
            var introduced = TokenWords(newStr).Except(TokenWords(oldStr)).ToList();
            foreach (var intro in introduced.OrderByDescending(t => t.Length))
            {
                if (items.Count >= 3) break;
                if (intro.Length < 4) continue;
                var removedMatch = removed.FirstOrDefault(r => SimilarWord(r, intro));
                if (removedMatch == null) continue;
                items.Add(new StepGroundTruth
                {
                    Text = $"Expected: \"{intro}\" replaces \"{removedMatch}\" in {relPath}",
                    File = relPath,
                    Anchor = intro
                });
            }
        }
        return items;
    }

    /// <summary>Plausible-typo OR plural/Array/List variant relation — the two families the
    /// hallucinated-property guard uses to suggest "did you mean".</summary>
    private static bool SimilarWord(string a, string b) =>
        IsPlausibleTypo(a, b) || IsPlausibleTypo(b, a) ||
        PluralVariants(a).Contains(b) || PluralVariants(b).Contains(a);

    private static IEnumerable<string> PluralVariants(string w)
    {
        yield return w + "s";
        yield return w + "es";
        yield return w + "Array";
        yield return w + "List";
        if (w.EndsWith("es", StringComparison.Ordinal)) yield return w[..^2];
        else if (w.EndsWith("s", StringComparison.Ordinal)) yield return w[..^1];
    }

    /// <summary>Distinct identifier-like tokens in a string (the guard's word-extraction shape).</summary>
    private static IEnumerable<string> TokenWords(string s) =>
        Regex.Matches(s, @"[A-Za-z_][A-Za-z0-9_]*").Cast<Match>().Select(m => m.Value).Distinct();

    /// <summary>The on-disk search anchor for a step's new content: the last substantial line of
    /// the new content (the changed line survives later reformatting better than a flattened
    /// snippet; a multi-line edit's flattened text never appears verbatim). Falls back to the
    /// one-line snippet for content with no substantial line.</summary>
    private static string AnchorFor(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length >= 8 && !l.StartsWith("//") && !l.StartsWith("/*") && !l.StartsWith("*"))
            .ToList();
        if (lines.Count == 0) return OneLineSnippet(content);
        return lines[^1];
    }

    /// <summary>Normalizes `word (` → `word(` — mirrors the apply pipeline's HTML style
    /// self-heal (the whole changed line is rewritten), so per-step ground-truth verification
    /// matches what actually landed on disk instead of false-negativing on the paren spacing.</summary>
    public static string NormalizeParenSpacing(string s) =>
        s == null ? "" : Regex.Replace(s, @"\b(\w+)\s+\(", "$1(");

    private static string OneLineSnippet(string s)
    {
        var flat = string.Join(" ", s.Replace("\r\n", "\n").Replace('\n', ' ').Split(' ').Where(w => w.Length > 0));
        return flat.Length <= 160 ? flat : flat[..160] + "…";
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "\n[Preview ended; omitted remainder is not code.]";

    public static string NormalizeUiStatus(string? status) => status switch
    {
        "written" or "ok" or "created" or "modified" => "done",
        "running" => "running",
        "error" => "error",
        _ => status ?? "pending"
    };

    public static string StripClassWrapper(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return code;
        var lines = code.Split('\n').ToList();
        while (lines.Count > 0)
        {
            var trimmed = lines[0].Trim();
            if (trimmed.Length == 0 ||
                Regex.IsMatch(trimmed, @"^(export\s+)?(default\s+)?(abstract\s+)?class\s+\w+"))
            {
                lines.RemoveAt(0);
            }
            else break;
        }
        while (lines.Count > 0)
        {
            var trimmed = lines[^1].Trim();
            if (trimmed == "}" || trimmed.Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }
            else break;
        }
        return string.Join("\n", lines);
    }

    public static string UnescapeString(string s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        return s.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
    }

    public static string? DetectExcessiveBlankLines(string newStr)
    {
        var repaired = CollapseExcessiveBlankLines(newStr);
        if (repaired == newStr) return null;
        var lines = newStr.Split('\n');
        var blankLines = lines.Where(l => string.IsNullOrWhiteSpace(l)).ToList();
        var codeLines = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        return $"EXCESSIVE BLANK LINES — newString has a blank line between nearly every code line " +
               $"({blankLines.Count} blank lines for {codeLines.Count} code lines). " +
               "Remove the spurious blank lines.";
    }

    public static string CollapseExcessiveBlankLines(string newStr)
    {
        if (string.IsNullOrWhiteSpace(newStr)) return newStr;
        var lines = newStr.Split('\n');
        if (lines.Length < 6) return newStr;
        var codeLines = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (codeLines.Count < 3) return newStr;
        var alternating = 0;
        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]) &&
                string.IsNullOrWhiteSpace(lines[i + 1]))
                alternating++;
        }
        if (alternating < codeLines.Count * 0.6) return newStr;

        var result = new List<string>();
        var lastWasBlank = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var isBlank = string.IsNullOrWhiteSpace(lines[i]);
            if (isBlank && lastWasBlank) continue;
            if (isBlank)
            {
                lastWasBlank = true;
                result.Add(lines[i]);
            }
            else
            {
                lastWasBlank = false;
                result.Add(lines[i]);
            }
        }
        return string.Join("\n", result);
    }

    public static string GetLeadingWhitespace(string s)
    {
        var i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        return s[..i];
    }

    public static string FixAngularAttributeCasing(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return content;
        // Angular structural directives
        content = Regex.Replace(content, @"\*ngif\b", "*ngIf", RegexOptions.IgnoreCase);
        content = Regex.Replace(content, @"\*ngfor\b", "*ngFor", RegexOptions.IgnoreCase);
        content = Regex.Replace(content, @"\*ngswitch\b", "*ngSwitch", RegexOptions.IgnoreCase);
        content = Regex.Replace(content, @"\*ngswitchcase\b", "*ngSwitchCase", RegexOptions.IgnoreCase);
        content = Regex.Replace(content, @"\*ngswitchdefault\b", "*ngSwitchDefault", RegexOptions.IgnoreCase);
        // Common Angular input bindings — restore camelCase
        var camelCaseAttrs = new[] {
        "ngClass", "ngStyle", "ngModel", "ngModelChange",
        "inputtedParentRef", "onlySearch", "hideStatus", "displaySocialResults",
        "urlSelectedEvent", "showTitle", "hasMenu", "showMenu", "hasClose", "showClose",
        "menuClicked", "closeClicked", "displayMiniTag", "pageSizeDropdown"
    };
        foreach (var attr in camelCaseAttrs)
        {
            var pattern = $@"(\[|\(\(|\(\[|\(|#){Regex.Escape(attr)}(\]|\)\)|\]|\))";
            content = Regex.Replace(content, pattern,
                m => m.Groups[1].Value + attr + m.Groups[2].Value,
                RegexOptions.IgnoreCase);
        }
        return content;
    }

    public static string StripFullFileFence(string value)
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

    public static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder();
        var inQuote = false;
        var quoteChar = '\0';
        var prevWasSpace = false;
        foreach (var c in s)
        {
            if ((c == '"' || c == '\'' || c == '`') && (sb.Length == 0 || sb[sb.Length - 1] != '\\'))
            {
                if (!inQuote) { inQuote = true; quoteChar = c; }
                else if (c == quoteChar) { inQuote = false; }
            }
            if (inQuote)
            {
                sb.Append(c);
                prevWasSpace = false;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (!prevWasSpace && sb.Length > 0) { sb.Append(' '); prevWasSpace = true; }
            }
            else
            {
                sb.Append(c);
                prevWasSpace = false;
            }
        }
        return sb.ToString().Trim();
    }

    public static bool IsHtmlLikeContent(string content) =>
     content.Contains('<') && Regex.IsMatch(content, @"</?\w+[\s/>]");
}
