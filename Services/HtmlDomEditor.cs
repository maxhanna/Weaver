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
    public static (string? matchedBlock, int matchIndex, string? error) ResolveHtmlAnchor(
    string content, string targetName, string? stepChange = null, int centerLine = 0,
    bool expandToClosingTags = true, bool expandToLineStart = true)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (null, -1, "Empty content");
        if (string.IsNullOrWhiteSpace(targetName))
            return (null, -1, "Empty targetName");
        var matchInfo = IndexOfNormalized(content, targetName, stepChange, centerLine);
        if (matchInfo.index < 0)
            return (null, -1, "Target not found in file");
        var adjustedStart = matchInfo.index;
        if (expandToLineStart)
        {
            var lineStart = content.LastIndexOf('\n', matchInfo.index);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            adjustedStart = lineStart;
        }
        var initialEndIndex = adjustedStart + (matchInfo.index - adjustedStart) + matchInfo.length;
        var finalEndIndex = initialEndIndex;
        if (expandToClosingTags)
        {
            var (expandedEndIndex, success) = ExpandToClosingTags(content, adjustedStart, initialEndIndex);
            if (success)
            {
                finalEndIndex = expandedEndIndex;
            }
        }
        var adjustedLength = finalEndIndex - adjustedStart;
        var matched = content.Substring(adjustedStart, adjustedLength);
        return (matched, adjustedStart, null);
    }
    private static (int endIndex, bool success) ExpandToClosingTags(string content, int startIndex, int initialEndIndex)
    {
        var stack = new Stack<string>();
        var voidElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr" };
        int i = startIndex;
        while (i < initialEndIndex || stack.Count > 0)
        {
            if (i >= content.Length) return (initialEndIndex, false);
            int nextTagStart = content.IndexOf('<', i);
            if (nextTagStart < 0) return (initialEndIndex, false);
            if (nextTagStart + 1 < content.Length)
            {
                char nextChar = content[nextTagStart + 1];
                if (!char.IsLetter(nextChar) && nextChar != '/' && nextChar != '!')
                {
                    i = nextTagStart + 1;
                    continue;
                }
            }
            else
            {
                return (initialEndIndex, false);
            }
            if (content[nextTagStart + 1] == '/')
            {
                int closeEnd = content.IndexOf('>', nextTagStart);
                if (closeEnd < 0) return (initialEndIndex, false);
                var tagName = content.Substring(nextTagStart + 2, closeEnd - (nextTagStart + 2)).Trim();
                if (stack.Count > 0 && stack.Peek() == tagName)
                {
                    stack.Pop();
                }
                i = closeEnd + 1;
            }
            else if (nextTagStart + 3 < content.Length && content.Substring(nextTagStart, 4) == "<!--")
            {
                int commentEnd = content.IndexOf("-->", nextTagStart);
                if (commentEnd < 0) return (initialEndIndex, false);
                i = commentEnd + 3;
            }
            else
            {
                int openEnd = content.IndexOf('>', nextTagStart);
                if (openEnd < 0) return (initialEndIndex, false);
                var tagContent = content.Substring(nextTagStart + 1, openEnd - (nextTagStart + 1));
                var tagNameMatch = Regex.Match(tagContent, @"^([a-zA-Z0-9-]+)");
                if (!tagNameMatch.Success)
                {
                    i = openEnd + 1;
                    continue;
                }
                var tagName = tagNameMatch.Groups[1].Value;
                bool isSelfClosing = tagContent.EndsWith("/") || voidElements.Contains(tagName);
                if (!isSelfClosing)
                {
                    stack.Push(tagName);
                }
                i = openEnd + 1;
            }
        }
        return (i, true);
    } 
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
    public static string StripLeadingClosingDivs(string html, string? targetName = null)
    {
        if (string.IsNullOrWhiteSpace(html))
            return html;
        int targetLeading = 0;
        if (targetName != null)
        {
            var targetLines = targetName.Split('\n');
            foreach (var line in targetLines)
            {
                var trimmed = line.Trim();
                if (trimmed == "</div>" || string.IsNullOrWhiteSpace(trimmed))
                    targetLeading++;
                else
                    break;
            }
        }
        var lines = html.Split('\n').ToList();
        int toStrip = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed == "</div>" || string.IsNullOrWhiteSpace(trimmed))
                toStrip++;
            else
                break;
        }
        int excess = toStrip - targetLeading;
        if (excess <= 0)
            return html;
        for (int i = 0; i < excess && lines.Count > 0; i++)
        {
            lines.RemoveAt(0);
        }
        return string.Join("\n", lines);
    }
    private static (int index, int length) IndexOfNormalized(
        string content, string targetName, string? stepChange, int centerLine)
    {
        var exactCandidates = FindAllExact(content, targetName);
        if (exactCandidates.Count > 0)
            return PickBestCandidate(content, exactCandidates, stepChange, centerLine);
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
            }
        }
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
    private static (int index, int length) PickBestCandidate(
        string content, List<(int index, int length)> candidates, string? stepChange, int centerLine)
    {
        if (candidates.Count == 1) return candidates[0];
        var keywords = AgentUtilities.ExtractDisambiguationKeywords(stepChange);
        var hasKeywords = keywords.Count > 0;
        var hasLineHint = centerLine > 0;
        if (!hasKeywords && !hasLineHint)
            return candidates[^1];
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
                score -= dist;
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