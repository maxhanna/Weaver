using System.Text.RegularExpressions;

namespace Weaver.Services;

using static Weaver.Services.AgentCodeFormatting;
using static Weaver.Services.AgentTextUtilities;

/// <summary>Formatting and indentation heuristics for edit replacements.</summary>
public sealed class FormattingEditHeuristic : IFormattingEditHeuristic
{
    public string Family => "formatting";

    public string IndentReplacement(string[] fileLines, int start, string replacement, bool isHtmlDomFile = false)
    {
        if (string.IsNullOrEmpty(replacement) || start >= fileLines.Length)
            return replacement;
        var fileIndent = GetLeadingWhitespace(fileLines[start]);
        if (fileIndent.Length == 0)
            return replacement;
        var replLines = replacement.Split('\n');
        var replBaseIndent = replLines.Where(l => l.Length > 0)
                                      .Select(GetLeadingWhitespace)
                                      .FirstOrDefault();
        if (replBaseIndent != null && replBaseIndent != fileIndent)
        {
            for (var i = 0; i < replLines.Length; i++)
            {
                if (replLines[i].Length == 0) continue;
                var lineIndent = GetLeadingWhitespace(replLines[i]);
                if (lineIndent.StartsWith(replBaseIndent, StringComparison.Ordinal))
                {
                    var excess = lineIndent[replBaseIndent.Length..];
                    replLines[i] = fileIndent + excess + replLines[i][lineIndent.Length..];
                }
                else
                {
                    replLines[i] = fileIndent + replLines[i];
                }
            }
        }
        if (isHtmlDomFile && IsHtmlLikeContent(replacement) && replLines.Length > 5)
        {
            return AutoIndentHtml(string.Join("\n", replLines), fileIndent);
        }
        var joined = string.Join("\n", replLines);
        var distinctIndentDepths = replLines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => GetLeadingWhitespace(l).Length)
            .Distinct()
            .Count();
        return distinctIndentDepths <= 1 && replLines.Length > 2
            ? AutoIndentFromFile(joined, fileIndent, fileLines, start)
            : joined;
    }

    public List<string> ReindentReplacementSnippet(
        List<string> newLinesArr, List<string> oldLinesArr,
        List<string> fileLinesArr, int matchIdx, bool isHtmlDomFile)
    {
        var finalNewLines = new List<string>();
        if (newLinesArr.Count == 0) return finalNewLines;
        var baseIndent = Regex.Match(fileLinesArr[matchIdx], @"^(\s*)").Value;
        var oldBaseIndent = Regex.Match(oldLinesArr[0], @"^(\s*)").Value;
        foreach (var nl in newLinesArr)
        {
            if (string.IsNullOrWhiteSpace(nl))
            {
                finalNewLines.Add(nl);
                continue;
            }
            var currentOldIndent = Regex.Match(nl, @"^(\s*)").Value;
            string relativeIndent = currentOldIndent.Length >= oldBaseIndent.Length
                ? currentOldIndent.Substring(oldBaseIndent.Length)
                : "";
            finalNewLines.Add(baseIndent + relativeIndent + nl.TrimStart());
        }
        var rawNew = string.Join("\n", finalNewLines);
        if (isHtmlDomFile && IsHtmlLikeContent(rawNew) && finalNewLines.Count > 5)
        {
            var stripped = finalNewLines
                .Select(l => string.IsNullOrWhiteSpace(l) ? "" : l.TrimStart())
                .ToList();
            var fixedHtml = AutoIndentHtml(string.Join("\n", stripped), baseIndent);
            finalNewLines = fixedHtml.Split('\n').ToList();
        }
        var rawAfter = string.Join("\n", finalNewLines);
        if (!isHtmlDomFile && (rawAfter.Contains('{') || rawAfter.Contains('}')) &&
            finalNewLines.Count > 2)
        {
            var fixedBraces = AutoIndentFromFile(
                string.Join("\n", finalNewLines), baseIndent,
                fileLinesArr.ToArray(), matchIdx);
            finalNewLines = fixedBraces.Split('\n').ToList();
        }
        return finalNewLines;
    }

}
