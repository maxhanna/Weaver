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
    /// FORMAT D: Finds targetName (a code block) in the content and returns the matched text.
    /// targetName is matched as a whitespace-normalized block — the matched text
    /// is the corresponding substring from the file content. No div nesting walking.
    /// </summary>
    public static (string? matchedBlock, string? error) ResolveHtmlAnchor(
        string content, string targetName)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (null, "Empty content");
        if (string.IsNullOrWhiteSpace(targetName))
            return (null, "Empty targetName");

        var matchInfo = IndexOfNormalized(content, targetName);
        if (matchInfo.index < 0)
            return (null, $"Target not found in file");

        var matched = content.Substring(matchInfo.index, matchInfo.length);
        return (matched, null);
    }

    /// <summary>
    /// Searches for targetName in content with whitespace-normalized matching.
    /// Splits targetName into non-whitespace tokens, then builds a regex that allows
    /// flexible whitespace between tokens. Uses single-pass regex for speed.
    /// Returns the LAST matching position and length, or (-1, 0) if not found.
    /// Uses last match so that non-unique text prefers the last occurrence.
    /// </summary>
    private static (int index, int length) IndexOfNormalized(string content, string targetName)
    {
        // Fast path: try exact match first (reverse search for uniqueness near end)
        var exact = content.LastIndexOf(targetName, StringComparison.OrdinalIgnoreCase);
        if (exact >= 0) return (exact, targetName.Length);

        // Split target into non-whitespace tokens, escape each for regex
        var tokens = Regex.Matches(targetName, @"\S+")
            .Select(m => Regex.Escape(m.Value))
            .ToList();
        if (tokens.Count == 0) return (-1, 0);

        // Build regex: tokens separated by \s+ (flexible whitespace)
        var pattern = string.Join(@"\s+", tokens);
        var matches = Regex.Matches(content, pattern, RegexOptions.IgnoreCase);
        if (matches.Count == 0) return (-1, 0);
        // Return the LAST match (preferring content sections near the end)
        var last = matches[^1];
        return (last.Index, last.Length);
    }

    /// <summary>
    /// Strips leading whitespace-only lines and leading &lt;/div&gt; lines from newCode.
    /// LLMs sometimes include the parent section's closing &lt;/div&gt; tags in newCode;
    /// this method removes them to avoid duplication.
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
}
