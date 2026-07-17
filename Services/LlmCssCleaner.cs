using System.Text.RegularExpressions;

namespace Weaver.Services;

public static class LlmCssCleaner
{
    private static readonly Regex SplitHexRx = new(@"(?<=[:\s])#([0-9a-fA-F]{1,2})\s+([0-9a-fA-F]{1,2})\s+([0-9a-fA-F]{1,2})(?:\s+([0-9a-fA-F]{1,2}))?", RegexOptions.Compiled);
    private static readonly Regex UnitRx = new(@"(\d+(?:\.\d+)?(?:px|rem|em|%|vh|vw|ms|s|deg|fr))(?=\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ZeroRx = new(@"(^|[^.\d])0+(?=\d)", RegexOptions.Compiled);
    private static readonly Regex CalcRx = new(@"calc\(([^)]+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CalcOpRx = new(@"\s*([+\-*/])\s*", RegexOptions.Compiled);
    private static readonly Regex DblSpaceRx = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex MissingColonRx = new(@"^(\s*[a-z-]+)\s+(?=\d|#|var\(--)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex TrailingCommaRx = new(@"(:[^;\n]+),\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex SmashedBraceRx = new(@"\}(?=[^\s}])", RegexOptions.Compiled);

    public static string Clean(string cssContent)
    {
        if (string.IsNullOrEmpty(cssContent)) return cssContent;

        string clean = cssContent;

        // 0. Fix split hex colors (#ab cd ef -> #abcdef)
        clean = SplitHexRx.Replace(clean, match =>
        {
            string hex = "#" + match.Groups[1].Value + match.Groups[2].Value + match.Groups[3].Value;
            if (match.Groups[4].Success)
                hex += match.Groups[4].Value;
            return hex;
        });

        // 1. Fix squished units (12px24px -> 12px 24px)
        clean = UnitRx.Replace(clean, "$1 ");

        // 2. Fix squished zeros (0000 -> 0 0 0 0)
        clean = ZeroRx.Replace(clean, "$10 ");

        // 3. Fix missing spaces inside calc()
        clean = CalcRx.Replace(clean, match => {
            string inner = match.Groups[1].Value;
            string spacedInner = CalcOpRx.Replace(inner, " $1 ");
            return $"calc({DblSpaceRx.Replace(spacedInner, " ")})";
        });

        // 4. Fix missing colons
        clean = MissingColonRx.Replace(clean, "$1: ");

        // 5. Fix illegal trailing commas
        clean = TrailingCommaRx.Replace(clean, "$1;");

        // 6. Fix smashed closing curly braces
        clean = SmashedBraceRx.Replace(clean, "}\n");

        return clean;
    }
}
