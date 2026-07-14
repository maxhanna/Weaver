using System.Text.RegularExpressions;

namespace Weaver.Services;

public static class HtmlDomEditor
{
    private static readonly HashSet<string> DirectionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "above", "before", "preceding", "after", "following", "below", "under", "beneath"
    };

    private static readonly HashSet<string> RefBoundaries = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "or", "but", "with", "using", "via", "by", "through", "for", "as", "like",
        "same", "similar", "matching", "following", "keeping"
    };

    public static (bool success, string newContent, string? error) InsertHtmlViaDom(
        string fileContent, string changeDescription, string htmlToInsert)
    {
        if (string.IsNullOrWhiteSpace(fileContent))
            return (false, fileContent, "Empty file content");
        if (string.IsNullOrWhiteSpace(htmlToInsert))
            return (false, fileContent, "Nothing to insert");

        if (fileContent.Contains("<!-- WEAVER_INSERT:0 -->", StringComparison.Ordinal))
        {
            var replaced = fileContent.Replace("<!-- WEAVER_INSERT:0 -->", htmlToInsert);
            return (true, replaced, null);
        }

        var insertion = FindInsertionPoint(fileContent, changeDescription);
        if (insertion.index < 0)
            return (false, fileContent, "Could not determine insertion point");

        return insertion.position == "before"
            ? (true, fileContent.Insert(insertion.index, htmlToInsert + "\n"), null)
            : (true, fileContent.Insert(insertion.index, "\n" + htmlToInsert), null);
    }

    public static (bool success, string content, string? error) InjectMarker(
        string fileContent, string changeDescription)
    {
        if (string.IsNullOrWhiteSpace(fileContent))
            return (false, fileContent, "Empty file content");

        var insertion = FindInsertionPoint(fileContent, changeDescription);
        if (insertion.index < 0)
            return (false, fileContent, "Could not determine insertion point");

        var marker = "<!-- WEAVER_INSERT:0 -->\n";
        return insertion.position == "before"
            ? (true, fileContent.Insert(insertion.index, marker), null)
            : (true, fileContent.Insert(insertion.index, "\n" + marker), null);
    }

    public static bool IsHtmlDomFile(string relPath)
    {
        var ext = Path.GetExtension(relPath)?.ToLowerInvariant();
        return ext is ".html" or ".htm" or ".cshtml" or ".razor";
    }

    private static (int index, string position) FindInsertionPoint(string content, string description)
    {
        var lower = description.ToLowerInvariant();
        var afterRef = ExtractReferent(lower, "after", "under", "below", "beneath");
        var beforeRef = ExtractReferent(lower, "before", "above", "preceding");

        if (beforeRef != null)
        {
            var pos = FindMatchPosition(content, beforeRef);
            if (pos >= 0)
            {
                var insertPos = LocateInsertBefore(content, pos);
                return (insertPos, "before");
            }
        }

        if (afterRef != null)
        {
            var pos = FindMatchPosition(content, afterRef);
            if (pos >= 0)
            {
                var insertPos = LocateInsertAfter(content, pos);
                return (insertPos, "after");
            }
        }

        var keywords = Regex.Matches(lower, @"[\w-]+")
            .Cast<Match>()
            .Select(m => m.Value)
            .Where(w => w.Length > 2 && !DirectionWords.Contains(w) && !RefBoundaries.Contains(w))
            .Distinct()
            .ToList();

        var bestMatch = (-1, 0);
        foreach (var kw in keywords)
        {
            var idx = content.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                var lineStart = content.LastIndexOf('\n', Math.Clamp(idx, 0, content.Length - 1));
                if (lineStart < 0) lineStart = 0;
                var score = kw.Length * 10;
                if (char.IsUpper(content[idx])) score += 5;
                if (score > bestMatch.Item2)
                    bestMatch = (lineStart, score);
                idx = content.IndexOf(kw, idx + 1, StringComparison.OrdinalIgnoreCase);
            }
        }

        return bestMatch.Item1 >= 0
            ? (bestMatch.Item1, "before")
            : (-1, "");
    }

    private static string? ExtractReferent(string lower, params string[] directionWords)
    {
        foreach (var word in directionWords)
        {
            var idx = lower.IndexOf(word, StringComparison.Ordinal);
            if (idx < 0) continue;

            var after = lower.Substring(idx + word.Length).TrimStart();
            if (after.Length == 0) continue;

            var endIdx = -1;
            var tokens = after.Split(' ');
            for (var i = 0; i < tokens.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(tokens[i])) continue;
                var clean = tokens[i].Trim().TrimEnd(',', '.', ';', ':', '!', '?');

                if (DirectionWords.Contains(clean))
                {
                    endIdx = after.IndexOf(tokens[i], StringComparison.Ordinal);
                    break;
                }

                if (RefBoundaries.Contains(clean) && i > 0)
                {
                    endIdx = after.IndexOf(tokens[i], StringComparison.Ordinal);
                    break;
                }

                if (clean == "and" && i + 1 < tokens.Length)
                {
                    var nextClean = tokens[i + 1].Trim().TrimEnd(',', '.', ';', ':', '!', '?');
                    if (DirectionWords.Contains(nextClean))
                    {
                        endIdx = after.IndexOf(tokens[i], StringComparison.Ordinal);
                        break;
                    }
                }
            }

            if (endIdx < 0)
                endIdx = after.Length;

            var referent = after.Substring(0, endIdx).Trim();
            if (referent.Length > 0)
            {
                var lastSpace = referent.LastIndexOf(' ');
                while (lastSpace > 0 && RefBoundaries.Contains(referent.Substring(lastSpace + 1)))
                {
                    referent = referent.Substring(0, lastSpace).Trim();
                    lastSpace = referent.LastIndexOf(' ');
                }
                return referent;
            }
        }

        return null;
    }

    private static int FindMatchPosition(string content, string referent)
    {
        if (string.IsNullOrWhiteSpace(referent))
            return -1;

        var idx = content.IndexOf(referent, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) return idx;

        var words = referent.Split(new[] { ' ', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim().TrimEnd(',', '.', ';', ':', '!', '?'))
            .Where(w => w.Length > 2)
            .ToArray();
        if (words.Length == 0) return -1;

        var results = new List<(int index, int score)>();
        var fi = content.IndexOf(words[0], StringComparison.OrdinalIgnoreCase);
        while (fi >= 0)
        {
            var score = words.Sum(w => FindOnLine(content, fi, w) >= 0 ? w.Length * 10 : 0);
            if (score > 0)
                results.Add((fi, score));
            fi = content.IndexOf(words[0], fi + 1, StringComparison.OrdinalIgnoreCase);
        }

        return results.OrderByDescending(r => r.score).FirstOrDefault().index;
    }

    private static int FindOnLine(string content, int nearPos, string word)
    {
        var lineStart = content.LastIndexOf('\n', Math.Clamp(nearPos, 0, content.Length - 1));
        if (lineStart < 0) lineStart = 0;
        var lineEnd = content.IndexOf('\n', nearPos);
        if (lineEnd < 0) lineEnd = content.Length;
        var line = content.Substring(lineStart, lineEnd - lineStart);
        var idx = line.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? lineStart + idx : -1;
    }

    private static int LocateInsertBefore(string content, int fromPos)
    {
        var lineStart = content.LastIndexOf('\n', Math.Clamp(fromPos, 0, content.Length - 1));
        if (lineStart < 0) lineStart = 0;

        var searchFrom = Math.Max(0, lineStart - 200);
        var preceding = content.Substring(searchFrom, lineStart - searchFrom);

        var divIdx = preceding.LastIndexOf("<div", StringComparison.OrdinalIgnoreCase);
        if (divIdx >= 0)
            return searchFrom + divIdx;

        var closeDivIdx = preceding.LastIndexOf("</div>", StringComparison.OrdinalIgnoreCase);
        if (closeDivIdx >= 0)
            return searchFrom + closeDivIdx + 6;

        return lineStart;
    }

    private static int LocateInsertAfter(string content, int fromPos)
    {
        var lineEnd = content.IndexOf('\n', fromPos);
        if (lineEnd < 0) lineEnd = content.Length;

        var searchLen = Math.Min(200, content.Length - lineEnd);
        var following = content.Substring(lineEnd, searchLen);

        var closeDivIdx = following.IndexOf("</div>", StringComparison.OrdinalIgnoreCase);
        if (closeDivIdx >= 0)
        {
            var afterClose = lineEnd + closeDivIdx + 6;
            var lineEndAfter = content.IndexOf('\n', afterClose);
            return lineEndAfter >= 0 ? lineEndAfter : afterClose;
        }

        return lineEnd;
    }
}
