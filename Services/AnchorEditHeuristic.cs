using System.Text;
using System.Text.RegularExpressions;

namespace Weaver.Services;

using static Weaver.Services.AgentTextUtilities;
using static Weaver.Services.AgentDiscovery;
using static Weaver.Services.AgentDiffUtilities;

/// <summary>Anchor matching and drift-recovery heuristics for edit resolution.</summary>
public sealed class AnchorEditHeuristic : IAnchorEditHeuristic
{
    private readonly IStructureEditHeuristic _structure;

    public AnchorEditHeuristic(IStructureEditHeuristic structure)
    {
        _structure = structure;
    }

    public string Family => "anchor";

    public string? ExtractMostUniqueLine(string oldStr, string fileContent)
    {
        var normFile = NormalizeLineEndings(fileContent);
        var oldLines = oldStr.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (oldLines.Count <= 1) return null;
        string? bestLine = null;
        int bestCount = int.MaxValue;
        foreach (var line in oldLines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 15) continue;
            var count = normFile.Split(new[] { trimmed }, StringSplitOptions.None).Length - 1;
            if (count < bestCount)
            {
                bestCount = count;
                bestLine = line;
            }
        }
        return bestLine;
    }


    public (bool replaced, string newContent, string? matchError, string? snippet) TryReplaceSafe(
     string fileContent, string oldStr, string newStr, int targetLine = 0, string? changeDesc = null)
    {
        if (string.IsNullOrEmpty(oldStr) && string.IsNullOrEmpty(fileContent) && !string.IsNullOrEmpty(newStr))
            return (true, newStr, null, null);
        if (string.IsNullOrEmpty(oldStr) && !string.IsNullOrEmpty(fileContent))
        {
            return (false, fileContent,
                "oldString is empty but the file is non-empty — refusing to perform an unbounded replacement. " +
                "Provide a non-empty, specific anchor.", null);
        }
        var normFile = NormalizeLineEndings(fileContent);
        var normOld = NormalizeLineEndings(oldStr).TrimEnd('\r');
        var matches = new List<int>();
        var searchPos = 0;
        var maxIterations = normFile.Length + 2;
        var iterations = 0;
        while (iterations++ < maxIterations)
        {
            var idx = normFile.IndexOf(normOld, searchPos, StringComparison.Ordinal);
            if (idx < 0) break;
            matches.Add(idx);
            searchPos = idx + Math.Max(1, normOld.Length);
        }
        if (matches.Count == 1)
        {
            var normNew = NormalizeLineEndings(newStr);
            return (true, normFile[..matches[0]] + normNew + normFile[(matches[0] + normOld.Length)..], null, null);
        }
        if (matches.Count > 1)
        {
            // COUNTING GUARD: an ALL-OCCURRENCE request ("rename every occurrence of X",
            // "replace all instances") with an ambiguous anchor must NOT guess a single
            // victim — applying to one occurrence renames only 1 of N and silently corrupts
            // the data. This fires before keyword scoring so a description naming the symbol
            // (which always appears in the file and would "disambiguate" to the last match)
            // can never smuggle a partial rename through. Rejected here, the resolver retries
            // with a unique anchor instead.
            {
                // Only when there is NO line hint (targetLine <= 0). Line-numbered batch
                // edits (deterministic multi-match, "update all N defaults") legitimately use
                // each sub-edit's line number as the disambiguation and must stay exempt.
                var changeLower2 = (changeDesc ?? string.Empty).ToLowerInvariant();
                if (targetLine <= 0 && Regex.IsMatch(changeLower2,
                        @"\b(every|each|all|any|both)\b|\boccurrence\b|\beverywhere\b|\bthroughout\b|\ball\s+(?:instances|occurrences|usages)\b"))
                {
                    var cfFirstLine = normOld.Split('\n')[0].Trim();
                    var cfUniqueLine = ExtractMostUniqueLine(normOld, normFile);
                    var cfErr = $"oldString found {matches.Count} times in file and the change requests an ALL-OCCURRENCE edit — " +
                                "applying to one occurrence would rename only 1 of " + matches.Count + " and corrupt the data. " +
                                "Provide a UNIQUE anchor (the whole file, or the full line with its unique surroundings) so every " +
                                "occurrence is replaced in one edit.";
                    if (cfUniqueLine != null)
                        cfErr += $" OR use ONLY this unique line as your entire oldString: `{cfUniqueLine.Trim()}`";
                    return (false, fileContent, cfErr, cfFirstLine);
                }
            }
            int chosenIdx = -1;
            var keywords = ExtractDisambiguationKeywords(changeDesc);
            if (keywords.Count > 0)
            {
                int bestContextScore = -1;
                for (int i = 0; i < matches.Count; i++)
                {
                    var lookbackStart = Math.Max(0, matches[i] - 2000);
                    var context = normFile.Substring(lookbackStart, matches[i] - lookbackStart).ToLowerInvariant();
                    var score = keywords.Count(k => context.Contains(k));
                    if (score > bestContextScore)
                    {
                        bestContextScore = score;
                        chosenIdx = i;
                    }
                }
            }
            if (chosenIdx == -1 && targetLine > 0)
            {
                var bestDist = int.MaxValue;
                for (int i = 0; i < matches.Count; i++)
                {
                    var matchLine = normFile[..matches[i]].Count(c => c == '\n') + 1;
                    var dist = Math.Abs(matchLine - targetLine);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        chosenIdx = i;
                    }
                }
                if (bestDist > 50) chosenIdx = -1;
            }
            if (chosenIdx >= 0)
            {
                var normNew = NormalizeLineEndings(newStr);
                return (true, normFile[..matches[chosenIdx]] + normNew + normFile[(matches[chosenIdx] + normOld.Length)..], null, null);
            }
            var firstLine = normOld.Split('\n')[0].Trim();
            var uniqueLine = ExtractMostUniqueLine(normOld, normFile);
            var err = $"oldString found {matches.Count} times in file — include more surrounding lines as anchor context.";
            if (uniqueLine != null)
                err += $" OR use ONLY this unique line as your entire oldString: `{uniqueLine.Trim()}`";
            return (false, fileContent, err, firstLine);
        }
        // FUZZY FALLBACK — blank-line tolerant. LLMs routinely emit oldStrings with stray
        // blank lines (a phantom EMPTY line inserted between two real lines, or a leading/
        // trailing one), so the verbatim ordinal match above can fail even though every REAL
        // line is present in order. Match line-by-line: blank lines are skipped on BOTH sides
        // (the phantom blank the model inserted, or a blank the file has that the model
        // dropped), and every real line must compare EXACTLY (Ordinal). Indentation drift is
        // deliberately NOT absorbed here: the deterministic-batch G1 contract requires a
        // drifted anchor to fail so it re-anchors against the CURRENT file text, and the
        // re-anchor chain (TryIdentifierAnchoredReanchor / TrySurroundingLineReanchor /
        // BuildExactMatchBlock) rebuilds the block from the file's REAL indentation — never
        // leaking the model's own. An ambiguous sequence (the same lines in two places) is
        // disambiguated by the change keywords / line hint and otherwise falls through to
        // that re-anchor machinery instead of guessing. The replacement span is the file's
        // REAL text from the first matched line through the last (trailing newline NOT
        // consumed), so real indentation and interior blank lines are preserved.
        var oldRealLines = normOld.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (oldRealLines.Count > 0)
        {
            var fileLines = normFile.Split('\n');
            var lineOffsets = new int[fileLines.Length];
            var off = 0;
            for (var li = 0; li < fileLines.Length; li++)
            {
                lineOffsets[li] = off;
                off += fileLines[li].Length + 1;
            }
            var fuzzyCandidates = new List<(int start, int last)>();
            for (var startLine = 0; startLine < fileLines.Length; startLine++)
            {
                if (!string.Equals(fileLines[startLine], oldRealLines[0], StringComparison.Ordinal)) continue;
                var fIdx = startLine;
                var oIdx = 0;
                var lastMatchLine = -1;
                while (fIdx < fileLines.Length && oIdx < oldRealLines.Count)
                {
                    if (string.IsNullOrWhiteSpace(fileLines[fIdx])) { fIdx++; continue; }
                    if (!string.Equals(fileLines[fIdx], oldRealLines[oIdx], StringComparison.Ordinal)) break;
                    lastMatchLine = fIdx;
                    fIdx++;
                    oIdx++;
                }
                if (oIdx == oldRealLines.Count && lastMatchLine >= 0)
                    fuzzyCandidates.Add((startLine, lastMatchLine));
            }
            var chosenFuzzy = -1;
            if (fuzzyCandidates.Count == 1)
            {
                chosenFuzzy = 0;
            }
            else if (fuzzyCandidates.Count > 1)
            {
                // Ambiguous fuzzy sequence — disambiguate like the verbatim multi-match path
                // (change keywords in the preceding context, then the line hint). No confident
                // winner → fall through to the caller's re-anchor machinery.
                var keywords = ExtractDisambiguationKeywords(changeDesc);
                if (keywords.Count > 0)
                {
                    int bestContextScore = -1;
                    for (var i = 0; i < fuzzyCandidates.Count; i++)
                    {
                        var candOffset = lineOffsets[fuzzyCandidates[i].start];
                        var lookbackStart = Math.Max(0, candOffset - 2000);
                        var context = normFile.Substring(lookbackStart, candOffset - lookbackStart).ToLowerInvariant();
                        var score = keywords.Count(k => context.Contains(k));
                        if (score > bestContextScore)
                        {
                            bestContextScore = score;
                            chosenFuzzy = i;
                        }
                    }
                }
                if (chosenFuzzy == -1 && targetLine > 0)
                {
                    var bestDist = int.MaxValue;
                    for (var i = 0; i < fuzzyCandidates.Count; i++)
                    {
                        var dist = Math.Abs((fuzzyCandidates[i].start + 1) - targetLine);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            chosenFuzzy = i;
                        }
                    }
                    if (bestDist > 50) chosenFuzzy = -1;
                }
            }
            if (chosenFuzzy >= 0)
            {
                var (fs, fl) = fuzzyCandidates[chosenFuzzy];
                var startOffset = lineOffsets[fs];
                // Span ends exactly at the last matched line — the trailing newline stays in
                // the file, so the blank-line structure AFTER the anchor is preserved (parity
                // with the verbatim path, where an oldString without a trailing newline does
                // not consume the next newline either).
                var endOffset = lineOffsets[fl] + fileLines[fl].Length;
                var normNew = NormalizeLineEndings(newStr);
                return (true, normFile[..startOffset] + normNew + normFile[endOffset..], null, null);
            }
        }
        return (false, fileContent, "oldString not found verbatim in file", null);
    }


    public string? BuildExactMatchBlock(string fileContent, string oldStr, int targetLine = 0, string? changeDesc = null)
    {
        if (string.IsNullOrWhiteSpace(oldStr)) return null;
        var normFile = NormalizeLineEndings(fileContent);
        var changeLower = (changeDesc ?? "").ToLowerInvariant();
        bool isRemoval = changeLower.Contains("remove") ||
            (changeLower.Contains("delete") && !Regex.IsMatch(changeLower, @"\b(add|create|insert|implement)\b"));
        if (!isRemoval)
        {
            var htmlBlock = _structure.ExtractFullHtmlBlock(normFile, oldStr);
            if (htmlBlock != null) return htmlBlock;
        }
        var normOld = NormalizeLineEndings(oldStr);
        var oldLines = normOld.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (oldLines.Count == 0) return null;
        var fileLines = normFile.Split('\n');
        var candidates = new List<(int startIdx, int score)>();
        for (var i = 0; i < fileLines.Length; i++)
        {
            var score = 0;
            var fIdx = i;
            var oIdx = 0;
            while (fIdx < fileLines.Length && oIdx < oldLines.Count)
            {
                var fileTrim = fileLines[fIdx].Trim();
                var oldTrim = oldLines[oIdx].Trim();
                if (fileTrim == oldTrim || fileTrim.StartsWith(oldTrim) || oldTrim.StartsWith(fileTrim))
                {
                    score++;
                    fIdx++;
                    oIdx++;
                }
                else if (string.IsNullOrEmpty(fileTrim) || string.IsNullOrEmpty(oldTrim))
                {
                    if (string.IsNullOrEmpty(fileTrim)) fIdx++;
                    if (string.IsNullOrEmpty(oldTrim)) oIdx++;
                }
                else
                {
                    break;
                }
            }
            if (score >= Math.Max(1, (int)Math.Ceiling(oldLines.Count * 0.75)))
            {
                // Reject candidates nested inside method/block bodies: class-level declarations
                // sit at a shallower nesting depth. A candidate at depth > 1 is inside a
                // method, loop, or conditional — not where a property should be inserted.
                var depth = 0;
                for (var d = 0; d < i; d++)
                {
                    var ch = fileLines[d];
                    for (var c = 0; c < ch.Length; c++)
                    {
                        if (ch[c] == '{') depth++;
                        else if (ch[c] == '}') depth--;
                    }
                }
                if (depth <= 1)
                    candidates.Add((i, score));
            }
        }
        if (candidates.Count == 0) return null;
        int chosenCandidate = -1;
        if (candidates.Count == 1)
        {
            chosenCandidate = 0;
        }
        else
        {
            var keywords = ExtractDisambiguationKeywords(changeDesc);
            if (keywords.Count > 0)
            {
                int bestContextScore = -1;
                for (int i = 0; i < candidates.Count; i++)
                {
                    var startIdx = candidates[i].startIdx;
                    var lookbackStart = Math.Max(0, startIdx - 50);
                    var context = string.Join("\n", fileLines.Skip(lookbackStart).Take(startIdx - lookbackStart)).ToLowerInvariant();
                    var score = keywords.Count(k => context.Contains(k));
                    if (score > bestContextScore)
                    {
                        bestContextScore = score;
                        chosenCandidate = i;
                    }
                }
            }
            if (chosenCandidate == -1 && targetLine > 0)
            {
                int bestDist = int.MaxValue;
                for (int i = 0; i < candidates.Count; i++)
                {
                    var dist = Math.Abs(candidates[i].startIdx + 1 - targetLine);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        chosenCandidate = i;
                    }
                }
                if (bestDist > 50) chosenCandidate = -1;
            }
        }
        if (chosenCandidate >= 0)
        {
            var bestStart = candidates[chosenCandidate].startIdx;
            var endIdx = Math.Min(fileLines.Length, bestStart + oldLines.Count);
            var joined = string.Join("\n", fileLines.Skip(bestStart).Take(endIdx - bestStart));
            return string.IsNullOrWhiteSpace(joined) ? null : joined;
        }
        return null;
    }


    /// <summary>
    /// Small-anchor "surrounding line" re-anchor. When a small plan oldString (2-3 lines)
    /// fails to match verbatim at apply time, try re-anchoring it against each surrounding
    /// line of the file: shift the anchor up/down by a line, extend it by the line above or
    /// below (the file gained a line the plan missed), or trim its first/last line (the plan
    /// included a stale line that no longer exists). Returns the file-EXACT replacement block
    /// plus its 0-based start line when exactly ONE surrounding alignment is confident — the
    /// caller applies it deterministically instead of escalating to the LLM resolver.
    /// Returns null when the anchor is not small, nothing aligns confidently, or the best
    /// alignment is ambiguous (a tie means the re-anchor would be guesswork).
    /// </summary>
    public (string correctedBlock, int startLineIdx, int score)? TrySurroundingLineReanchor(
        string fileContent, string oldStr, int targetLine = 0, string? changeDesc = null,
        int maxAnchorLines = 3)
    {
        if (string.IsNullOrWhiteSpace(oldStr)) return null;
        var normFile = NormalizeLineEndings(fileContent);
        var normOld = NormalizeLineEndings(oldStr);
        var oldLines = normOld.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        // Only SMALL anchors get the surrounding-line retry: 2-3 lines (the planner's RULE 17
        // shape). A 1-line anchor has no surrounding-line structure to exploit — the apply
        // loop's whole-file tolerant matcher plus BuildExactMatchBlock already cover its drift.
        if (oldLines.Count < 2 || oldLines.Count > maxAnchorLines) return null;
        var fileLines = normFile.Split('\n');
        // NOTE: no file-length guard against the anchor length here — a trim shape (plan has
        // a stale line) legitimately makes the file SHORTER than the plan. The per-shape
        // bounds checks below keep every window inside the file; a file too small for every
        // shape simply yields no candidates.
        if (fileLines.Length < 2) return null;
        var n = oldLines.Count;

        // Anchor position: the planner's stated line when given (0-indexed), else the best
        // global scan. The surrounding search stays near it — big drift is BuildExactMatchBlock's
        // (whole-file) job, or the LLM resolver's.
        var baseIdx = targetLine > 0
            ? Math.Clamp(targetLine - 1, 0, Math.Max(0, fileLines.Length - 1))
            : FindBestAnchorPosition(fileLines, oldLines);
        if (baseIdx < 0) return null;

        var window = maxAnchorLines;
        // Dedupe by block text: the same file window can be reached by multiple (delta, shape)
        // combos (e.g. extend-above at delta 0 == extend-below at delta -1) with identical
        // scores — those are ONE candidate, not ambiguity.
        var byBlock = new Dictionary<string, (int start, int score)>(StringComparer.Ordinal);
        for (var delta = -window; delta <= window; delta++)
        {
            foreach (var (start, len) in EnumerateAnchorShapes(baseIdx + delta, n))
            {
                if (len < 1 || start < 0 || start + len > fileLines.Length) continue;
                var score = ScoreAnchorWindowAlignment(oldLines, fileLines, start, len);
                if (score < 0) continue;
                var block = string.Join("\n", fileLines.Skip(start).Take(len));
                if (!byBlock.TryGetValue(block, out var existing) || score > existing.score)
                    byBlock[block] = (start, score);
            }
        }
        if (byBlock.Count == 0) return null;
        var ranked = byBlock
            .Select(kv => (block: kv.Key, start: kv.Value.start, score: kv.Value.score))
            .OrderByDescending(c => c.score)
            .ThenBy(c => Math.Abs(c.start - baseIdx))
            .ToList();
        var best = ranked[0];
        // Ambiguity guard: a second alignment with the SAME score means the re-anchor is
        // guesswork — escalate instead of applying at a coin-flip location.
        if (ranked.Count > 1 && ranked[1].score == best.score) return null;
        if (string.Equals(best.block, normOld, StringComparison.Ordinal)) return null;
        return (best.block, best.start, best.score);
    }

    /// <summary>Anchor shapes tried at each surrounding offset: same length, extended above,
    /// extended below, trimmed first line, trimmed last line. Trim shapes only fire for
    /// n ≥ 3 anchors: trimming a 2-line plan leaves just ONE verified line (as weak as a
    /// 1-line anchor, which is deliberately excluded) and ties with the same-length drift
    /// case, manufacturing ambiguity.
    /// </summary>
    private static IEnumerable<(int start, int len)> EnumerateAnchorShapes(int basePos, int n)
    {
        yield return (basePos, n);          // same length, shifted by delta
        yield return (basePos - 1, n + 1);  // extended above — file gained a line before the anchor
        yield return (basePos, n + 1);      // extended below — file gained a line after the anchor
        if (n >= 3)
        {
            yield return (basePos + 1, n - 1);  // trimmed first — plan included a stale first line
            yield return (basePos, n - 1);      // trimmed last — plan included a stale last line
        }
    }

    /// <summary>
    /// Scores a candidate window against the plan anchor. Returns -1 when the alignment is
    /// not confident: same-length allows ONE drifted line (≥ n-1 matches); extend/trim shapes
    /// require FULL alignment (every line matched, with the extra line dropped from one side).
    /// The strict full-alignment bar for extend/trim is what makes those shapes safe — a plan
    /// line that drifted outright (not just an extra/missing line) escalates instead.
    /// </summary>
    private static int ScoreAnchorWindowAlignment(List<string> plan, string[] fileLines, int start, int len)
    {
        var n = plan.Count;
        if (len == n)
        {
            var score = 0;
            for (var i = 0; i < n; i++)
                if (LinesTolerantlyMatch(plan[i], fileLines[start + i])) score++;
            return score >= Math.Max(1, n - 1) ? score : -1;
        }
        if (len == n + 1)
        {
            // File gained a line: the plan must align with the window minus ONE line (the
            // dropped line is the extra one the plan never saw) — every plan line must match.
            for (var drop = 0; drop <= n; drop++)
            {
                var score = 0;
                var wi = 0;
                for (var pi = 0; pi < n; pi++)
                {
                    if (wi == drop) wi++; // skip the dropped window line
                    if (wi >= len) break;
                    if (LinesTolerantlyMatch(plan[pi], fileLines[start + wi])) score++;
                    wi++;
                }
                if (score == n) return n;
            }
            return -1;
        }
        if (len == n - 1)
        {
            // Plan included a stale line: the window must align with the plan minus ONE line
            // — every window line must match.
            for (var drop = 0; drop < n; drop++)
            {
                var score = 0;
                var wi = 0;
                for (var pi = 0; pi < n; pi++)
                {
                    if (pi == drop) continue;
                    if (wi >= len) break;
                    if (LinesTolerantlyMatch(plan[pi], fileLines[start + wi])) score++;
                    wi++;
                }
                if (score == n - 1) return n - 1;
            }
            return -1;
        }
        return -1;
    }

    /// <summary>Best whole-file position for a small anchor (used when the planner gave no
    /// line number): the offset where the most plan lines match in sequence.</summary>
    private static int FindBestAnchorPosition(string[] fileLines, List<string> plan)
    {
        var bestIdx = -1;
        var bestScore = -1;
        for (var i = 0; i < fileLines.Length; i++)
        {
            var score = 0;
            for (var j = 0; j < plan.Count && i + j < fileLines.Length; j++)
                if (LinesTolerantlyMatch(plan[j], fileLines[i + j])) score++;
            if (score > bestScore) { bestScore = score; bestIdx = i; }
        }
        return bestScore > 0 ? bestIdx : 0;
    }

    /// <summary>Tolerant per-line comparison for the surrounding-line re-anchor: trimmed
    /// equality, whitespace-stripped equality, case-insensitive equality, or a prefix match
    /// of a ≥4-char token (short prefixes like "b" over-match "button" and are excluded).</summary>
    private static bool LinesTolerantlyMatch(string a, string b)
    {
        var at = a.Trim();
        var bt = b.Trim();
        if (at == bt) return true;
        if (Regex.Replace(at, @"\s+", "") == Regex.Replace(bt, @"\s+", "")) return true;
        if (string.Equals(at, bt, StringComparison.OrdinalIgnoreCase)) return true;
        return at.Length >= 4 && bt.Length >= 4 &&
               (at.StartsWith(bt, StringComparison.Ordinal) || bt.StartsWith(at, StringComparison.Ordinal));
    }

    /// <summary>Words too generic to serve as an anchor identifier (type names, modifiers,
    /// keywords) — "musicTodoCount" anchors an edit; "number"/"null" would match every
    /// declaration in the file.</summary>
    private static readonly HashSet<string> AnchorStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "number", "null", "true", "false", "void", "string", "boolean", "object", "array",
        "public", "private", "protected", "readonly", "static", "const", "let", "var",
        "this", "return", "import", "export", "async", "await", "default", "class",
        "interface", "function", "new", "undefined", "value", "index", "count", "list",
        "data", "items", "item", "name", "type", "date", "time"
    };

    /// <summary>Identifier tokens (≥5 chars, snake/camel/Pascal/dotted-safe) extracted from
    /// <paramref name="code"/>, minus generic type/keyword words. These are the anchor words
    /// the edit itself names — the most trustworthy pointer to where the edit belongs.</summary>
    public List<string> ExtractAnchorIdentifierTokens(string code)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(code)) return result;
        foreach (Match m in Regex.Matches(code, @"\b[A-Za-z_][A-Za-z0-9_]{4,}\b"))
        {
            if (AnchorStopWords.Contains(m.Value)) continue;
            result.Add(m.Value);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Grounds an oldString to the real file: picks its RAREST identifier token (the
    /// most distinctive word the edit itself names) and returns that token plus every file
    /// line containing it (whole-word). Returns null when the oldString carries no usable
    /// identifier or the token is too common (&gt; <paramref name="maxOccurrences"/>) to be
    /// distinctive. This is the "introspect on the new variable name" step — instead of
    /// re-bouncing a drifted anchor to the LLM, find where the anchor's own word actually
    /// lives in the file.</summary>
    private (string token, List<int> lineIdxs)? GroundAnchorToken(
        string fileContent, string oldStr, int maxOccurrences = 3)
    {
        var tokens = ExtractAnchorIdentifierTokens(oldStr);
        if (tokens.Count == 0) return null;
        string? best = null;
        var bestCount = int.MaxValue;
        foreach (var t in tokens)
        {
            var c = CountWordOccurrences(fileContent, t);
            if (c > 0 && c < bestCount) { bestCount = c; best = t; }
        }
        if (best == null || bestCount > maxOccurrences) return null;
        var fileLines = fileContent.Split('\n');
        var lines = new List<int>();
        for (var i = 0; i < fileLines.Length; i++)
            if (ContainsWord(fileLines[i], best)) lines.Add(i);
        return lines.Count > 0 ? (best, lines) : null;
    }

    /// <summary>
    /// Identifier-grounded re-anchor for a drifted oldString. When the anchor failed verbatim
    /// (whitespace/line drift — e.g. the model dropped indentation, or the file gained a
    /// line), this finds the anchor's OWN identifier in the real file and rebuilds the
    /// replacement block from the ACTUAL file text (real indentation, real surrounding
    /// lines), so the edit applies deterministically instead of escalating to the LLM — which
    /// re-emits the same drifted anchor and burns the retry budget (the benchmark-22 loop:
    /// the same 80-char oldString 3× → abort). Because the block is grounded on an identifier
    /// the OLDSTRING itself names, it can never select an unrelated block (e.g. a
    /// "tradeNotifsCount" line) the tolerant matcher would otherwise pick.
    /// Returns null when grounding is ambiguous, the oldString's sibling lines don't map to
    /// real lines (a fabricated line — the LLM must see the real file and fix its own
    /// anchor), or the alignment is not confident.
    /// </summary>
    public (string correctedBlock, int startLineIdx, int score)? TryIdentifierAnchoredReanchor(
        string fileContent, string oldStr, int targetLine = 0)
    {
        if (string.IsNullOrWhiteSpace(oldStr)) return null;
        var normFile = NormalizeLineEndings(fileContent);
        var normOld = NormalizeLineEndings(oldStr);
        var oldLines = normOld.Split('\n'); // keep blanks — the walk aligns positionally
        var grounded = GroundAnchorToken(normFile, normOld);
        if (grounded == null) return null;
        var (token, candidates) = grounded.Value;

        var aIdx = ResolveAnchorLine(candidates, normFile, normOld, token, targetLine);
        if (aIdx == null) return null;
        var fileLines = normFile.Split('\n');

        // Which old line does the anchor file line correspond to? It must tolerantly match
        // one of the old lines (the token line is that line's content).
        var anchorOldIdx = -1;
        for (var oi = 0; oi < oldLines.Length; oi++)
            if (!string.IsNullOrWhiteSpace(oldLines[oi]) && LinesTolerantlyMatch(oldLines[oi], fileLines[aIdx.Value]))
            { anchorOldIdx = oi; break; }
        if (anchorOldIdx < 0) return null;

        var startIdx = aIdx.Value - anchorOldIdx;
        if (startIdx < 0) return null;

        // Blank-tolerant positional walk: every non-blank old line must map to a matching real
        // line in order (blank differences on either side are absorbed). Any real mismatch
        // (e.g. a fabricated sibling line) → null → the resolver gets the real-content hint.
        var fi = startIdx;
        var o = 0;
        while (o < oldLines.Length)
        {
            if (fi >= fileLines.Length) return null;
            var fBlank = string.IsNullOrWhiteSpace(fileLines[fi]);
            var oBlank = string.IsNullOrWhiteSpace(oldLines[o]);
            if (fBlank && oBlank) { fi++; o++; continue; }
            if (fBlank) { fi++; continue; }
            if (oBlank) { o++; continue; }
            if (!LinesTolerantlyMatch(oldLines[o], fileLines[fi])) return null;
            fi++; o++;
        }
        var block = string.Join("\n", fileLines.Skip(startIdx).Take(fi - startIdx));
        if (string.IsNullOrWhiteSpace(block)) return null;
        if (string.Equals(block, normOld, StringComparison.Ordinal)) return null; // already exact
        return (block, startIdx, oldLines.Count(l => !string.IsNullOrWhiteSpace(l)));
    }

    /// <summary>Resolves which candidate file line is the anchor. One candidate wins outright;
    /// otherwise the candidate whose line trimmed-equals the old line containing the token
    /// (the DECLARATION vs identical-token USAGE lines) wins; otherwise a targetLine hint
    /// picks the nearest (within 50 lines). Ambiguous → null.</summary>
    private static int? ResolveAnchorLine(
        List<int> candidates, string normFile, string normOld, string token, int targetLine)
    {
        if (candidates.Count == 1) return candidates[0];
        var oldLines = normOld.Split('\n');
        var oiOfToken = -1;
        for (var oi = 0; oi < oldLines.Length; oi++)
            if (!string.IsNullOrWhiteSpace(oldLines[oi]) && ContainsWord(oldLines[oi], token))
            { oiOfToken = oi; break; }
        if (oiOfToken >= 0)
        {
            var fileLines = normFile.Split('\n');
            var oldTrim = oldLines[oiOfToken].Trim();
            var trimmed = candidates
                .Where(i => string.Equals(fileLines[i].Trim(), oldTrim, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (trimmed.Count == 1) return trimmed[0];
        }
        if (targetLine > 0)
        {
            var nearest = candidates.OrderBy(i => Math.Abs(i + 1 - targetLine)).First();
            if (Math.Abs(nearest + 1 - targetLine) <= 50) return nearest;
        }
        return null;
    }

    /// <summary>Display-only probe for the resolver feedback: returns the REAL file lines
    /// around the oldString's most distinctive identifier (the anchor line ± its neighbors),
    /// so the model can copy the correct verbatim text when its own oldString drifted.
    /// Unlike <see cref="TryIdentifierAnchoredReanchor"/> it does not require the sibling
    /// lines to map — it shows where the anchor lives even when the rest of the oldString
    /// was fabricated (the model then fixes its own anchor — an "edit an edit").</summary>
    public string? FindIdentifierGroundedLines(string fileContent, string oldStr)
    {
        if (string.IsNullOrWhiteSpace(oldStr)) return null;
        var normFile = NormalizeLineEndings(fileContent);
        var normOld = NormalizeLineEndings(oldStr);
        var grounded = GroundAnchorToken(normFile, normOld);
        if (grounded == null) return null;
        var (token, candidates) = grounded.Value;
        var best = ResolveAnchorLine(candidates, normFile, normOld, token, 0);
        if (best == null) return null;
        var fileLines = normFile.Split('\n');
        var start = Math.Max(0, best.Value - 1);
        var end = Math.Min(fileLines.Length, best.Value + 2);
        return string.Join("\n", fileLines.Skip(start).Take(end - start));
    }

    private static int CountWordOccurrences(string text, string word) =>
        Regex.Matches(text, @"\b" + Regex.Escape(word) + @"\b", RegexOptions.IgnoreCase).Count;

    private static bool ContainsWord(string line, string word) =>
        Regex.IsMatch(line, @"\b" + Regex.Escape(word) + @"\b", RegexOptions.IgnoreCase);


    public string? BuildExactMatchHint(string content, string oldString)
    {
        var fileLines = content.Split('\n');
        var oldLines = oldString.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length >= 8)
            .ToList();
        if (oldLines.Count == 0 || fileLines.Length == 0) return null;
        bool IsTrivialLine(string line)
        {
            var t = line.Trim();
            if (t.Length < 12) return true;
            var meaningful = new string(t.Where(char.IsLetterOrDigit).ToArray());
            if (meaningful.Length < 12) return true;
            if (Regex.IsMatch(t, @"^\s*[\w-]+\s*:\s*[\w\d#.()-]+\s*;?\s*$"))
            {
                return true;
            }
            return false;
        }
        var results = new List<(int fileIdx, double score, string line)>();
        for (var fi = 0; fi < fileLines.Length; fi++)
        {
            var fLine = fileLines[fi];
            if (IsTrivialLine(fLine)) continue;
            var bestSim = oldLines.Max(o => ComputeLineSimilarity(fLine, o));
            if (bestSim >= 0.50)
                results.Add((fi, bestSim, fLine));
        }
        var best = results
            .OrderByDescending(r => r.score)
            .ThenByDescending(r => r.line.Trim().Length)
            .Take(3)
            .ToList();
        if (best.Count == 0) return null;
        var sb = new StringBuilder();
        foreach (var b in best)
        {
            sb.AppendLine($"  ({(b.score * 100):F0}% match) line {b.fileIdx + 1}: {b.line}");
            var llmLine = oldLines
                .OrderByDescending(o => ComputeLineSimilarity(b.line, o))
                .FirstOrDefault();
            if (llmLine != null && llmLine != b.line.Trim())
            {
                var fileTrimmed = b.line.Trim();
                var diff = DescribeLineDiff(llmLine, fileTrimmed);
                if (diff != null)
                    sb.AppendLine($"    └ DIFF: {diff}");
            }
        }
        return sb.ToString();
    }


    public int ResolveTargetLineNumber(
        string fileContent,
        string changeDesc,
        string? targetSymbol = null,
        string? estimatedLineRange = null,
        int plannerLineNumber = 0)
    {
        if (string.IsNullOrWhiteSpace(fileContent) || string.IsNullOrWhiteSpace(changeDesc))
            return plannerLineNumber > 0 ? plannerLineNumber : 0;
        var lines = fileContent.Split('\n');
        var candidates = new List<(int line, int score)>();
        var changeLower = changeDesc.ToLowerInvariant();
        var isInsertion = Regex.IsMatch(changeDesc,
            @"\b(add|insert|append|expand|include|new)\b", RegexOptions.IgnoreCase);
        if (!string.IsNullOrWhiteSpace(estimatedLineRange))
        {
            var rangeMatch = Regex.Match(estimatedLineRange, @"(\d+)\s*[-–~]\s*(\d+)");
            if (rangeMatch.Success &&
                int.TryParse(rangeMatch.Groups[1].Value, out var rangeStart) &&
                int.TryParse(rangeMatch.Groups[2].Value, out var rangeEnd))
            {
                var mid = (rangeStart + rangeEnd) / 2;
                if (mid >= 1 && mid <= lines.Length)
                    candidates.Add((mid, 100));
            }
            else
            {
                var singleMatch = Regex.Match(estimatedLineRange, @"(\d+)");
                if (singleMatch.Success &&
                    int.TryParse(singleMatch.Groups[1].Value, out var singleLine) &&
                    singleLine >= 1 && singleLine <= lines.Length)
                    candidates.Add((singleLine, 95));
            }
        }
        if (!string.IsNullOrWhiteSpace(targetSymbol))
        {
            var isCallTarget = Regex.IsMatch(changeDesc, @"\bcall\b", RegexOptions.IgnoreCase);
            var declPattern = $@"\b(class|record|struct)\s+{Regex.Escape(targetSymbol)}\b";
            // Method declaration: symbol(params) { with opening brace on same line
            var methodDeclPattern = $@"\b(?:async\s+|private\s+|public\s+|protected\s+)?{Regex.Escape(targetSymbol)}\s*\([^)]*\)\s*\{{";
            var symPattern = $@"\b{Regex.Escape(targetSymbol)}\s*[\(<{{]";
            var foundDecl = false;
            for (var i = 0; i < lines.Length; i++)
            {
                if (Regex.IsMatch(lines[i], declPattern, RegexOptions.IgnoreCase))
                {
                    candidates.Add((i + 1, 200));
                    foundDecl = true;
                    break;
                }
            }
            if (!foundDecl && !isCallTarget)
            {
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!Regex.IsMatch(lines[i], methodDeclPattern, RegexOptions.IgnoreCase)) continue;
                    candidates.Add((i + 1, 190));
                    foundDecl = true;
                    break;
                }
            }
            if (!foundDecl)
            {
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!Regex.IsMatch(lines[i], symPattern, RegexOptions.IgnoreCase)) continue;
                    // When change targets a "call", skip matches where symbol starts the line (declaration)
                    if (isCallTarget && Regex.IsMatch(lines[i], $@"^\s*(?:async\s+|private\s+|public\s+|protected\s+)?{Regex.Escape(targetSymbol)}\s*\(", RegexOptions.IgnoreCase))
                        continue;
                    var score = 180;
                    // Prefer call sites with complex arguments (template literals or very long lines) --
                    // these are likely the actual target of a replacement, not trivial validation calls
                    if (lines[i].Contains("${")) score += 10;
                    else if (lines[i].Length > 100) score += 5;
                    candidates.Add((i + 1, score));
                }
            }
        }
        if (isInsertion)
        {
            _structure.AddInsertionLineCandidates(lines, changeLower, candidates);
        }
        else
        {
            foreach (Match qm in Regex.Matches(changeDesc, @"['""]([^'""]{4,})['""]"))
            {
                var quoted = qm.Groups[1].Value;
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains(quoted, StringComparison.OrdinalIgnoreCase))
                        candidates.Add((i + 1, 160));
                }
            }
        }
        var keywords = ExtractMeaningfulKeywords(changeLower).Where(w => w.Length >= 4).ToList();
        if (keywords.Count > 0)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var lineLower = lines[i].ToLowerInvariant();
                var hitCount = keywords.Count(w => lineLower.Contains(w));
                if (hitCount >= 2)
                    candidates.Add((i + 1, 20 + hitCount * 5));
            }
        }
        if (changeDesc.TrimStart().StartsWith("<"))
        {
            var textFragments = Regex.Matches(changeDesc, @">([^<]{8,})<")
                .Select(m => m.Groups[1].Value.Trim())
                .Where(t => t.Length >= 8)
                .OrderByDescending(t => t.Length)
                .ToList();
            if (textFragments.Count > 0)
            {
                for (var i = 0; i < lines.Length; i++)
                {
                    if (textFragments.Any(t => lines[i].Contains(t, StringComparison.Ordinal)))
                        candidates.Add((i + 1, 170));
                }
            }
        }
        if (plannerLineNumber > 0 && plannerLineNumber <= lines.Length)
        {
            candidates.Add((plannerLineNumber, 50));
        }
        if (candidates.Count == 0)
            return 0;
        var best = candidates
            .GroupBy(c => c.line)
            .Select(g => (line: g.Key, score: g.Max(x => x.score)))
            .OrderByDescending(x => x.score)
            .ThenBy(x => plannerLineNumber > 0 ? Math.Abs(x.line - plannerLineNumber) : x.line)
            .First();
        System.Diagnostics.Debug.WriteLine($"[ResolveTargetLineNumber] targetSymbol={targetSymbol}, changeDesc={changeDesc?.Substring(0, Math.Min(80, changeDesc?.Length ?? 0))}, plannerLine={plannerLineNumber}, best=line {best.line} score {best.score}, all={string.Join(";", candidates.Select(c => $"{c.line}({c.score})").Distinct())}");
        return best.line;
    }


}
