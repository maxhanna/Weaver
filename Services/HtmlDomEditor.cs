using System.Text;
using System.Text.RegularExpressions;

namespace Weaver.Services;

public static class HtmlDomEditor
{
    public static bool IsHtmlDomFile(string relPath)
    {
        var ext = Path.GetExtension(relPath)?.ToLowerInvariant();
        return ext is ".html" or ".htm" or ".cshtml" or ".razor";
    }

    /// <summary>
    /// FORMAT D: Finds targetName (a code block) in the content and returns the matched text
    /// plus its start position. The matched block is expanded to include the full indentation
    /// of the first line so that AutoIndentCode can detect the correct indent level.
    ///
    /// Optional stepChange/centerLine let the caller disambiguate between multiple candidate
    /// matches (e.g. the same-looking wrapper div appearing in several sections of the file) by
    /// keyword overlap and line proximity, the same way TryReplaceSafe already does for
    /// oldString/newString edits.
    /// </summary>
    public static (string? matchedBlock, int matchIndex, string? error) ResolveHtmlAnchor(
        string content, string targetName, string? stepChange = null, int centerLine = 0)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (null, -1, "Empty content");
        if (string.IsNullOrWhiteSpace(targetName))
            return (null, -1, "Empty targetName");

        var matchInfo = IndexOfNormalized(content, targetName, stepChange, centerLine);
        if (matchInfo.index < 0)
            return (null, -1, "Target not found in file");

        // Expand backward to start of line to capture indentation
        var lineStart = content.LastIndexOf('\n', matchInfo.index);
        lineStart = lineStart < 0 ? 0 : lineStart + 1;

        var adjustedStart = lineStart;
        var adjustedLength = (matchInfo.index - lineStart) + matchInfo.length;

        var matched = content.Substring(adjustedStart, adjustedLength);
        return (matched, matchInfo.index, null);
    }

    /// <summary>
    /// Gets the whitespace indentation of the line at the given position in content.
    /// </summary>
    public static string GetLineIndent(string content, int pos)
    {
        if (pos <= 0 || pos >= content.Length) return "";
        var lineStart = content.LastIndexOf('\n', pos);
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        var lineEnd = content.IndexOf('\n', pos);
        if (lineEnd < 0) lineEnd = content.Length;
        var line = content.Substring(lineStart, lineEnd - lineStart);
        var m = Regex.Match(line, @"^(\s*)");
        return m.Groups[1].Value;
    }

    /// <summary>
    /// If targetName starts with one or more &lt;/div&gt; lines (possibly with leading whitespace),
    /// strips them and returns the clean target. Also indicates how many lines were stripped.
    /// This handles the common LLM pattern where it includes the previous section's closing tags
    /// in targetName for context. When used with insertAfter, the caller should auto-switch to insertBefore.
    /// </summary>
    public static (string cleanTarget, int strippedLines) StripLeadingDivClosesFromTarget(string targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName))
            return (targetName, 0);

        var lines = targetName.Split('\n').ToList();
        var stripped = 0;
        while (lines.Count > 0)
        {
            var trimmed = lines[0].Trim();
            if (trimmed == "</div>" || string.IsNullOrEmpty(trimmed))
            {
                lines.RemoveAt(0);
                if (trimmed == "</div>") stripped++;
            }
            else
            {
                break;
            }
        }
        return (string.Join("\n", lines), stripped);
    }

    /// <summary>
    /// Strips leading &lt;/div&gt; lines from newCode (defensive cleanup).
    /// </summary>
    public static string StripLeadingClosingDivs(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return html;

        var lines = html.Split('\n').ToList();
        while (lines.Count > 0)
        {
            var trimmed = lines[0].Trim();
            if (trimmed == "</div>" || string.IsNullOrWhiteSpace(trimmed))
                lines.RemoveAt(0);
            else
                break;
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Searches for targetName in content using three progressively looser strategies:
    ///   1. Exact substring match.
    ///   2. Token-flexible-whitespace regex match (tolerates re-wrapped lines).
    ///   3. Fully whitespace-COLLAPSED match (tolerates the LLM inserting/omitting spaces
    ///      *within* what should be a single token, e.g. "?0.5 :1" vs "? 0.5 : 1" — this is
    ///      the #1 cause of "Target not found" on Angular attribute-heavy anchors).
    /// When a strategy yields multiple candidates, disambiguates using keyword overlap
    /// (from stepChange) and line-proximity (to centerLine), falling back to the LAST
    /// occurrence if no hints are supplied (preserves prior behavior).
    /// </summary>
    private static (int index, int length) IndexOfNormalized(
        string content, string targetName, string? stepChange, int centerLine)
    {
        // Strategy 1: exact match (all occurrences, case-insensitive)
        var exactCandidates = FindAllExact(content, targetName);
        if (exactCandidates.Count > 0)
            return PickBestCandidate(content, exactCandidates, stepChange, centerLine);

        // Strategy 2: token-based with flexible whitespace BETWEEN tokens
        var tokens = Regex.Matches(targetName, @"\S+")
            .Select(m => Regex.Escape(m.Value))
            .ToList();
        if (tokens.Count > 0)
        {
            var pattern = string.Join(@"\s+", tokens);
            try
            {
                var matches = Regex.Matches(content, pattern, RegexOptions.IgnoreCase);
                if (matches.Count > 0)
                {
                    var candidates = matches.Select(m => (m.Index, m.Length)).ToList();
                    return PickBestCandidate(content, candidates, stepChange, centerLine);
                }
            }
            catch (RegexParseException)
            {
                // fall through to collapsed matching
            }
        }

        // Strategy 3: whitespace-COLLAPSED match — strips ALL whitespace from both sides
        // and maps the match back to original file indices. This is the fallback that
        // rescues cases where token boundaries themselves differ (missing/extra internal
        // spaces), which strategy 2 cannot handle.
        var collapsedCandidates = FindAllCollapsed(content, targetName);
        if (collapsedCandidates.Count > 0)
            return PickBestCandidate(content, collapsedCandidates, stepChange, centerLine);

        return (-1, 0);
    }

    private static List<(int index, int length)> FindAllExact(string content, string targetName)
    {
        var result = new List<(int, int)>();
        var pos = 0;
        while (true)
        {
            var idx = content.IndexOf(targetName, pos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;
            result.Add((idx, targetName.Length));
            pos = idx + Math.Max(1, targetName.Length);
        }
        return result;
    }

    /// <summary>
    /// Finds all occurrences of targetName in content after stripping ALL whitespace from
    /// both strings, mapping matched ranges back to the original (non-collapsed) indices.
    /// </summary>
    private static List<(int index, int length)> FindAllCollapsed(string content, string targetName)
    {
        var result = new List<(int, int)>();

        var collapsedContent = new StringBuilder(content.Length);
        var origIndices = new List<int>(content.Length);
        for (var i = 0; i < content.Length; i++)
        {
            if (char.IsWhiteSpace(content[i])) continue;
            collapsedContent.Append(content[i]);
            origIndices.Add(i);
        }

        var collapsedTarget = new StringBuilder(targetName.Length);
        for (var i = 0; i < targetName.Length; i++)
        {
            if (char.IsWhiteSpace(targetName[i])) continue;
            collapsedTarget.Append(targetName[i]);
        }

        if (collapsedTarget.Length == 0) return result;

        var contentStr = collapsedContent.ToString();
        var targetStr = collapsedTarget.ToString();

        var searchFrom = 0;
        while (true)
        {
            var idx = contentStr.IndexOf(targetStr, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;

            var endCollapsedIdx = idx + targetStr.Length - 1;
            if (endCollapsedIdx >= origIndices.Count) break;

            var startOrig = origIndices[idx];
            var endOrig = origIndices[endCollapsedIdx];
            result.Add((startOrig, endOrig - startOrig + 1));

            searchFrom = idx + Math.Max(1, targetStr.Length);
        }

        return result;
    }

    /// <summary>
    /// Chooses among multiple candidate match locations. If stepChange/centerLine are
    /// supplied, scores each candidate by keyword overlap in its surrounding context and
    /// by line-distance to centerLine — the same heuristic TryReplaceSafe already uses for
    /// oldString/newString edits. Falls back to the LAST candidate (prior default behavior)
    /// when no hints are given or scoring can't distinguish them.
    /// </summary>
    private static (int index, int length) PickBestCandidate(
        string content, List<(int index, int length)> candidates, string? stepChange, int centerLine)
    {
        if (candidates.Count == 1) return candidates[0];

        var keywords = AgentUtilities.ExtractDisambiguationKeywords(stepChange);
        var hasKeywords = keywords.Count > 0;
        var hasLineHint = centerLine > 0;

        if (!hasKeywords && !hasLineHint)
            return candidates[^1]; // preserve prior "last match" default

        var best = candidates[^1];
        var bestScore = int.MinValue;

        foreach (var (index, length) in candidates)
        {
            var score = 0;

            if (hasKeywords)
            {
                var windowStart = Math.Max(0, index - 800);
                var windowLen = Math.Min(content.Length, index + length + 200) - windowStart;
                var window = content.Substring(windowStart, windowLen);
                score += keywords.Count(k => window.Contains(k, StringComparison.OrdinalIgnoreCase)) * 100;
            }

            if (hasLineHint)
            {
                var matchLine = content[..index].Count(c => c == '\n') + 1;
                var dist = Math.Abs(matchLine - centerLine);
                score -= dist; // closer to the hinted line is better
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = (index, length);
            }
        }

        return best;
    }
}