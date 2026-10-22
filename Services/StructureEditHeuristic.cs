using System.Text.RegularExpressions;

namespace Weaver.Services;

using static Weaver.Services.AgentTextUtilities;

/// <summary>Structural and syntax-shape heuristics for edit safety.</summary>
public sealed class StructureEditHeuristic : IStructureEditHeuristic
{
    public string Family => "structure";

    public string? ExtractFullHtmlBlock(string fileContent, string oldStr)
    {
        var firstLine = oldStr.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.TrimStart();
        if (string.IsNullOrEmpty(firstLine) || !firstLine.StartsWith("<")) return null;
        var tagMatch = Regex.Match(firstLine, @"^<([a-zA-Z0-9]+)");
        if (!tagMatch.Success) return null;
        var tagName = tagMatch.Groups[1].Value;
        var normFile = NormalizeLineEndings(fileContent);
        var startIdx = normFile.IndexOf(firstLine, StringComparison.Ordinal);
        if (startIdx < 0) return null;
        var depth = 0;
        var pos = startIdx;
        while (pos < normFile.Length)
        {
            var nextOpen = normFile.IndexOf($"<{tagName}", pos, StringComparison.OrdinalIgnoreCase);
            var nextClose = normFile.IndexOf($"</{tagName}>", pos, StringComparison.OrdinalIgnoreCase);
            if (nextClose < 0) return null;
            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                var charAfter = nextOpen + tagName.Length + 1 < normFile.Length
                    ? normFile[nextOpen + tagName.Length + 1]
                    : '\0';
                if (charAfter == ' ' || charAfter == '>' || charAfter == '\t' || charAfter == '\n' || charAfter == '\r')
                {
                    depth++;
                }
                pos = nextOpen + tagName.Length + 1;
            }
            else
            {
                if (depth <= 0)
                {
                    var endIdx = nextClose + tagName.Length + 3;
                    return normFile.Substring(startIdx, endIdx - startIdx);
                }
                depth--;
                pos = nextClose + tagName.Length + 3;
            }
        }
        return null;
    }


    public void ScanBraceDepth(string s,
        ref int depth, ref bool inSingle, ref bool inDouble, ref bool inTemplate,
        ref bool inBlockComment)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var next = i + 1 < s.Length ? s[i + 1] : '\0';
            if (inBlockComment)
            {
                if (c == '*' && next == '/') { inBlockComment = false; i++; }
                continue;
            }
            if (inSingle)
            {
                if (c == '\\') i++;
                else if (c == '\'') inSingle = false;
                continue;
            }
            if (inDouble)
            {
                if (c == '\\') i++;
                else if (c == '"') inDouble = false;
                continue;
            }
            if (inTemplate)
            {
                if (c == '\\') i++;
                else if (c == '`') inTemplate = false;
                continue;
            }
            if (c == '/' && next == '/') return; // line comment: ignore rest
            if (c == '/' && next == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end >= 0) { i = end + 1; continue; }
                inBlockComment = true; // comment spans into following lines
                i = s.Length;
                continue;
            }
            if (c == '\'') { inSingle = true; continue; }
            if (c == '"') { inDouble = true; continue; }
            if (c == '`') { inTemplate = true; continue; }
            if (c == '{') depth++;
            else if (c == '}') depth = Math.Max(0, depth - 1);
        }
    }


    public int FindMatchingCloseDiv(string content, int openDivIdx)
    {
        if (openDivIdx < 0 || openDivIdx >= content.Length) return -1;
        var depth = 0;
        var pos = openDivIdx;
        while (pos < content.Length)
        {
            var nextOpen = content.IndexOf("<div", pos, StringComparison.OrdinalIgnoreCase);
            var nextClose = content.IndexOf("</div>", pos, StringComparison.OrdinalIgnoreCase);
            if (nextClose < 0) return -1;
            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                var charAfter = nextOpen + 4 < content.Length
                    ? content[nextOpen + 4]
                    : '\0';
                if (charAfter == ' ' || charAfter == '>' || charAfter == '\t' ||
                    charAfter == '\n' || charAfter == '\r')
                {
                    depth++;
                }
                pos = nextOpen + 4;
            }
            else
            {
                if (depth <= 0) return nextClose;
                depth--;
                pos = nextClose + 6;
            }
        }
        return -1;
    }


    public void AddInsertionLineCandidates(
        string[] lines, string changeLower, List<(int line, int score)> candidates)
    {
        var containerHints = new List<(string pattern, int weight)>();
        if (changeLower.Contains("faq-container") || changeLower.Contains("faq container"))
            containerHints.Add(("faq-container", 80));
        if (changeLower.Contains("discord-panel") || changeLower.Contains("discord panel"))
            containerHints.Add(("discord-panel", 75));
        if (changeLower.Contains("faq-content") || changeLower.Contains("faq content"))
            containerHints.Add(("faq-content", 78));
        if (containerHints.Count == 0 && changeLower.Contains("faq"))
        {
            containerHints.Add(("faq-container", 70));
            containerHints.Add(("discord-panel", 65));
        }
        for (var i = 0; i < lines.Length; i++)
        {
            var lineLower = lines[i].ToLowerInvariant();
            foreach (var (pattern, weight) in containerHints)
            {
                if (!lineLower.Contains(pattern, StringComparison.Ordinal)) continue;
                var score = weight;
                if (lineLower.Contains("faq", StringComparison.Ordinal)) score += 8;
                if (lineLower.Contains("popup-panel", StringComparison.Ordinal)) score -= 40;
                candidates.Add((i + 1, score));
                for (var j = i; j < Math.Min(i + 15, lines.Length); j++)
                {
                    var probe = lines[j];
                    if (probe.Contains("FAQ entries go here", StringComparison.OrdinalIgnoreCase))
                        candidates.Add((j + 1, weight + 35));
                    if (probe.TrimStart().StartsWith("</details>", StringComparison.OrdinalIgnoreCase))
                        candidates.Add((j + 1, weight + 20));
                }
            }
            if (lineLower.Contains("faq entries go here", StringComparison.Ordinal))
                candidates.Add((i + 1, 90));
        }
    }


    public bool IsBraceBalanced(string content) => !HasUnbalancedBraces(content);

    public bool HasUnbalancedBraces(string content)
    {
        var depth = 0;
        var inSingle = false;
        var inDouble = false;
        var inTemplate = false;
        var inLineComment = false;
        var inBlockComment = false;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            var n = i + 1 < content.Length ? content[i + 1] : '\0';
            if (inLineComment && c == '\n') { inLineComment = false; continue; }
            if (inBlockComment && c == '*' && n == '/') { inBlockComment = false; i++; continue; }
            if (inBlockComment) continue;
            if (inLineComment) continue;
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
                    if (depth < 0) return true;
                }
            }
        }
        return depth != 0;
    }


    /// <summary>
    /// True when an oldString is nothing but punctuation/symbol characters (e.g. "}", "{",
    /// ";", "})", "};", ",") — an anchor that can never be reliable: it matches dozens of
    /// places in any real file or deletes structural code. Used to bounce garbage anchors
    /// deterministically BEFORE any LLM round-trip or apply machinery runs.
    /// </summary>
    public bool IsBarePunctuationAnchor(string? oldStr)
    {
        if (string.IsNullOrWhiteSpace(oldStr)) return false;
        var trimmed = oldStr.Trim();
        // 7+ chars of punctuation isn't the classic bare-anchor shape ("}", "};", "},") —
        // leave those to the normal match-count machinery instead of over-blocking.
        if (trimmed.Length == 0 || trimmed.Length > 6) return false;
        return Regex.IsMatch(trimmed, @"^[\p{P}\p{S}]+$");
    }

    /// <summary>First non-blank line of a string, trimmed. Null when the string is blank.</summary>
    public string? FirstNonBlankLine(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s.Split('\n', '\r')
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))
            ?.Trim();
    }

    /// <summary>
    /// True when an anchor's first real line is a lone closing brace ("}", "})", "};") —
    /// the classic garbage shape where the model starts its oldString with the PREVIOUS
    /// block's closing brace before the real declaration, which either matches dozens of
    /// times or starts the replacement inside the wrong scope.
    /// </summary>
    public bool IsLoneClosingBraceFirstLine(string? s)
    {
        var first = FirstNonBlankLine(s);
        return first is "}" or "})" or "};";
    }

    /// <summary>
    /// Whether an oldString must be bounced before any apply attempt.
    /// For LLM-authored edits this is exactly the union of the two shape guards:
    /// bare-punctuation anchors ("}", "};") and any anchor whose first line is a lone
    /// closing brace (the classic garbage shape where the model grabs the previous block's
    /// close brace before the real declaration).
    /// For deterministic server-authored edits, multi-line anchors are NEVER bounced:
    /// the generator only emits exact, contiguous slices of the file — e.g. the last
    /// method's close brace immediately followed by the class's close brace ("  }\n}"),
    /// which is a legitimate, unique, correctly-placed anchor for an end-of-class insert
    /// and happens to be punctuation-only. Only single-line bare punctuation (a lone "}" —
    /// which matches the first close brace anywhere in the file) stays bounced for both.
    /// </summary>
    public bool ShouldBounceGarbageAnchor(string? oldStr, bool isDeterministic)
    {
        if (string.IsNullOrWhiteSpace(oldStr)) return false;
        if (isDeterministic)
            return !HasLineBreak(oldStr) && IsBarePunctuationAnchor(oldStr);
        return IsBarePunctuationAnchor(oldStr) || IsLoneClosingBraceFirstLine(oldStr);
    }

    private static bool HasLineBreak(string? s)
        => s != null && (s.Contains('\n') || s.Contains('\r'));
}
