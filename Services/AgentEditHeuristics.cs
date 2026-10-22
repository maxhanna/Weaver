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

/// <summary>Part of the split of the former AgentUtilities monolith.</summary>
public static class AgentEditHeuristics
{
    public static readonly string[] UnsafeEditMarkers = ContentEditHeuristic.UnsafeEditMarkers;

    public enum PreEditVerdict { Proceed, AlreadyDone, Irrelevant }

    internal static readonly HashSet<string> ControlFlowKeywords = ContentEditHeuristic.ControlFlowKeywords;
    internal static readonly Regex[] PlaceholderPatterns = ContentEditHeuristic.PlaceholderPatterns;
    private static readonly IContentEditHeuristic ContentHeuristics = new ContentEditHeuristic();
    private static readonly IFormattingEditHeuristic FormattingHeuristics = new FormattingEditHeuristic();
    private static readonly IStructureEditHeuristic StructureHeuristics = new StructureEditHeuristic();
    private static readonly IAnchorEditHeuristic AnchorHeuristics = new AnchorEditHeuristic(StructureHeuristics);

    public static string? DetectHallucinatedProperties(string oldStr, string newStr, string fileContent, string relPath,
        string? relatedFileContent = null) => ContentHeuristics.DetectHallucinatedProperties(oldStr, newStr, fileContent, relPath, relatedFileContent);

    internal static bool IsPlausibleTypo(string prop, string word) => ContentHeuristics.IsPlausibleTypo(prop, word);

    public static string? ExtractMostUniqueLine(string oldStr, string fileContent) =>
        AnchorHeuristics.ExtractMostUniqueLine(oldStr, fileContent);

    public static string? ExtractFullHtmlBlock(string fileContent, string oldStr) =>
        StructureHeuristics.ExtractFullHtmlBlock(fileContent, oldStr);

    public static string? PythonDeclarationKind(string source) => ContentHeuristics.PythonDeclarationKind(source);

    public static string? DetectDuplicatePropertyAddition(string oldStr, string newStr, string? relPath = null) =>
        ContentHeuristics.DetectDuplicatePropertyAddition(oldStr, newStr, relPath);

    public static string? DetectDroppedEntriesInGroupedOutput(string oldStr, string newStr, string? changeDesc = null) =>
        ContentHeuristics.DetectDroppedEntriesInGroupedOutput(oldStr, newStr, changeDesc);

    /// <summary>
    /// Counts code braces in a line while skipping braces inside single/double
    /// quoted strings, template literals, line comments, and block comments
    /// (including block comments that span multiple lines) — so `const s = "}";`,
    /// `${x} {`, or a `{` on a comment continuation line never corrupts nesting.
    /// </summary>
    internal static void ScanBraceDepth(string s,
        ref int depth, ref bool inSingle, ref bool inDouble, ref bool inTemplate,
        ref bool inBlockComment) => StructureHeuristics.ScanBraceDepth(
            s, ref depth, ref inSingle, ref inDouble, ref inTemplate, ref inBlockComment);

    public static (bool replaced, string newContent, string? matchError, string? snippet) TryReplaceSafe(
        string fileContent, string oldStr, string newStr, int targetLine = 0, string? changeDesc = null) =>
        AnchorHeuristics.TryReplaceSafe(fileContent, oldStr, newStr, targetLine, changeDesc);

    public static string? BuildExactMatchBlock(string fileContent, string oldStr, int targetLine = 0, string? changeDesc = null) =>
        AnchorHeuristics.BuildExactMatchBlock(fileContent, oldStr, targetLine, changeDesc);

    public static (string correctedBlock, int startLineIdx, int score)? TrySurroundingLineReanchor(
        string fileContent, string oldStr, int targetLine = 0, string? changeDesc = null,
        int maxAnchorLines = 3) => AnchorHeuristics.TrySurroundingLineReanchor(
            fileContent, oldStr, targetLine, changeDesc, maxAnchorLines);

    public static List<string> ExtractAnchorIdentifierTokens(string code) =>
        AnchorHeuristics.ExtractAnchorIdentifierTokens(code);

    public static (string correctedBlock, int startLineIdx, int score)? TryIdentifierAnchoredReanchor(
        string fileContent, string oldStr, int targetLine = 0) =>
        AnchorHeuristics.TryIdentifierAnchoredReanchor(fileContent, oldStr, targetLine);

    public static string? FindIdentifierGroundedLines(string fileContent, string oldStr) =>
        AnchorHeuristics.FindIdentifierGroundedLines(fileContent, oldStr);

    public static string? GetUnsafeEditPayloadReason(string oldString, string newString) =>
        ContentHeuristics.GetUnsafeEditPayloadReason(oldString, newString);

    public static bool LooksLikePlaceholderStub(string code, string? preExisting = null) =>
        ContentHeuristics.LooksLikePlaceholderStub(code, preExisting);

    public static string IndentReplacement(string[] fileLines, int start, string replacement, bool isHtmlDomFile = false) =>
        FormattingHeuristics.IndentReplacement(fileLines, start, replacement, isHtmlDomFile);

    /// <summary>
    /// Re-indents an oldString/newString replacement snippet so it sits at the
    /// matched block's base indentation, preserving relative nesting. HTML DOM
    /// files (by EXTENSION, never content sniffing) get tag-depth re-indentation;
    /// code files get brace-depth re-indentation. Content sniffing must not be
    /// used here: TS/JS generics like `Promise&lt;void&gt;` contain '&lt;void&gt;' and
    /// would be misdetected as HTML, flattening the entire snippet to base indent.
    /// </summary>

    public static List<string> ReindentReplacementSnippet(
        List<string> newLinesArr, List<string> oldLinesArr,
        List<string> fileLinesArr, int matchIdx, bool isHtmlDomFile) =>
        FormattingHeuristics.ReindentReplacementSnippet(newLinesArr, oldLinesArr, fileLinesArr, matchIdx, isHtmlDomFile);

    public static string? BuildExactMatchHint(string content, string oldString) =>
        AnchorHeuristics.BuildExactMatchHint(content, oldString);

    public static string? DetectWrongSectionEdit(
        string oldStr, string fileContent, string stepChange, string relPath)
    {
        if (string.IsNullOrWhiteSpace(oldStr) || string.IsNullOrWhiteSpace(stepChange))
        { return null; }
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        if (ext is not (".html" or ".htm" or ".cshtml" or ".razor" or ".vue" or ".svelte"))
        { return null; }
        var sectionRegex = new Regex(
            @"\*ngIf\s*=\s*""(\w+)\s*={2,3}\s*'([^']+)'""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var sections = new List<(string name, int divStart, int divEnd)>();
        foreach (Match m in sectionRegex.Matches(fileContent))
        {
            var name = m.Groups[2].Value;
            var divStart = fileContent.LastIndexOf("<div", m.Index, StringComparison.Ordinal);
            if (divStart < 0) continue;
            var divEnd = FindMatchingCloseDiv(fileContent, divStart);
            if (divEnd < 0) continue;
            sections.Add((name, divStart, divEnd));
        }
        if (sections.Count < 2) return null;
        var normFile = NormalizeLineEndings(fileContent);
        var normOld = NormalizeLineEndings(oldStr);
        var oldStrIdx = normFile.IndexOf(normOld, StringComparison.Ordinal);
        if (oldStrIdx < 0) return null;
        string? actualSection = null;
        foreach (var (name, divStart, divEnd) in sections)
        {
            if (oldStrIdx >= divStart && oldStrIdx <= divEnd)
            {
                actualSection = name;
                break;
            }
        }
        if (actualSection == null) { return null; }
        var stepLower = stepChange.ToLowerInvariant();
        string? targetSection = null;
        foreach (var (name, _, _) in sections)
        {
            if (Regex.IsMatch(stepLower, $@"\b{Regex.Escape(name.ToLowerInvariant())}\b"))
            {
                targetSection = name;
                break;
            }
        }
        if (targetSection == null) return null;
        if (string.Equals(actualSection, targetSection, StringComparison.OrdinalIgnoreCase))
        { return null; }
        var targetSectionEntry = sections.FirstOrDefault(s =>
            string.Equals(s.name, targetSection, StringComparison.OrdinalIgnoreCase));
        var error = new StringBuilder();
        error.AppendLine($"WRONG SECTION — the step description references the '{targetSection}' section, " +
                         $"but your oldString was found in the '{actualSection}' section.");
        error.AppendLine();
        error.AppendLine($"You MUST find the section marked with *ngIf=\"... === '{targetSection}'\" " +
                         $"and use lines from THAT section as your oldString.");
        error.AppendLine($"Do NOT edit the '{actualSection}' section.");
        if (targetSectionEntry.divEnd > targetSectionEntry.divStart)
        {
            var sectionContent = normFile.Substring(
                targetSectionEntry.divStart,
                Math.Min(targetSectionEntry.divEnd - targetSectionEntry.divStart + 6, 3000));
            var sectionLines = sectionContent.Split('\n');
            if (sectionLines.Length > 45)
            {
                sectionContent = string.Join('\n', sectionLines.Take(40)) +
                                 "\n... (section continues)";
            }
            error.AppendLine();
            error.AppendLine($"═══ CORRECT SECTION CONTENT (*ngIf=\"... === '{targetSection}'\") ═══");
            error.AppendLine("```html");
            error.AppendLine(sectionContent);
            error.AppendLine("```");
            error.AppendLine();
            error.AppendLine($"Pick a unique line from the CORRECT section above as your oldString.");
        }
        return error.ToString();
    }

    internal static int FindMatchingCloseDiv(string content, int openDivIdx) =>
        StructureHeuristics.FindMatchingCloseDiv(content, openDivIdx);

    public static int ResolveTargetLineNumber(
        string fileContent, string changeDesc, string? targetSymbol = null,
        string? estimatedLineRange = null, int plannerLineNumber = 0) =>
        AnchorHeuristics.ResolveTargetLineNumber(fileContent, changeDesc, targetSymbol,
            estimatedLineRange, plannerLineNumber);

    internal static void AddInsertionLineCandidates(
        string[] lines, string changeLower, List<(int line, int score)> candidates) =>
        StructureHeuristics.AddInsertionLineCandidates(lines, changeLower, candidates);

    internal static string? ExtractField(string text, string fieldName)
    {
        var pattern = $@"{fieldName}:\s*(.*?)(?=\s*(?:FILE:|CHANGE:|DESCRIPTION:|<<<|$))";
        var m = Regex.Match(text, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    public static bool IsBraceBalanced(string content) => StructureHeuristics.IsBraceBalanced(content);

    public static bool HasUnbalancedBraces(string content) => StructureHeuristics.HasUnbalancedBraces(content);

    public static bool IsBarePunctuationAnchor(string? oldStr) => StructureHeuristics.IsBarePunctuationAnchor(oldStr);

    public static string? FirstNonBlankLine(string? s) => StructureHeuristics.FirstNonBlankLine(s);

    public static bool IsLoneClosingBraceFirstLine(string? s) => StructureHeuristics.IsLoneClosingBraceFirstLine(s);

    public static bool ShouldBounceGarbageAnchor(string? oldStr, bool isDeterministic) =>
        StructureHeuristics.ShouldBounceGarbageAnchor(oldStr, isDeterministic);


}
