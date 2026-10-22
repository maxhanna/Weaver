namespace Weaver.Services;

/// <summary>Common identity for a focused edit-heuristic family.</summary>
public interface IEditHeuristic
{
    string Family { get; }
}

/// <summary>Content and semantic guards used before applying an edit.</summary>
public interface IContentEditHeuristic : IEditHeuristic
{
    string? DetectHallucinatedProperties(string oldStr, string newStr, string fileContent,
        string relPath, string? relatedFileContent = null);
    bool IsPlausibleTypo(string prop, string word);
    string? PythonDeclarationKind(string source);
    string? DetectDuplicatePropertyAddition(string oldStr, string newStr, string? relPath = null);
    string? DetectDroppedEntriesInGroupedOutput(string oldStr, string newStr, string? changeDesc = null);
    string? GetUnsafeEditPayloadReason(string oldString, string newString);
    bool LooksLikePlaceholderStub(string code, string? preExisting = null);
}

/// <summary>Formatting and indentation heuristics for replacement snippets.</summary>
public interface IFormattingEditHeuristic : IEditHeuristic
{
    string IndentReplacement(string[] fileLines, int start, string replacement, bool isHtmlDomFile = false);
    List<string> ReindentReplacementSnippet(List<string> newLinesArr, List<string> oldLinesArr,
        List<string> fileLinesArr, int matchIdx, bool isHtmlDomFile);
}

/// <summary>Structural and syntax-shape guards used by the edit pipeline.</summary>
public interface IStructureEditHeuristic : IEditHeuristic
{
    string? ExtractFullHtmlBlock(string fileContent, string oldStr);
    void ScanBraceDepth(string s, ref int depth, ref bool inSingle, ref bool inDouble,
        ref bool inTemplate, ref bool inBlockComment);
    int FindMatchingCloseDiv(string content, int openDivIdx);
    void AddInsertionLineCandidates(string[] lines, string changeLower,
        List<(int line, int score)> candidates);
    bool IsBraceBalanced(string content);
    bool HasUnbalancedBraces(string content);
    bool IsBarePunctuationAnchor(string? oldStr);
    string? FirstNonBlankLine(string? s);
    bool IsLoneClosingBraceFirstLine(string? s);
    bool ShouldBounceGarbageAnchor(string? oldStr, bool isDeterministic);
}

/// <summary>Anchor matching and drift-recovery heuristics for edit resolution.</summary>
public interface IAnchorEditHeuristic : IEditHeuristic
{
    (bool replaced, string newContent, string? matchError, string? snippet) TryReplaceSafe(
        string fileContent, string oldStr, string newStr, int targetLine = 0,
        string? changeDesc = null);
    string? ExtractMostUniqueLine(string oldStr, string fileContent);
    string? BuildExactMatchBlock(string fileContent, string oldStr, int targetLine = 0,
        string? changeDesc = null);
    (string correctedBlock, int startLineIdx, int score)? TrySurroundingLineReanchor(
        string fileContent, string oldStr, int targetLine = 0, string? changeDesc = null,
        int maxAnchorLines = 3);
    List<string> ExtractAnchorIdentifierTokens(string code);
    (string correctedBlock, int startLineIdx, int score)? TryIdentifierAnchoredReanchor(
        string fileContent, string oldStr, int targetLine = 0);
    string? FindIdentifierGroundedLines(string fileContent, string oldStr);
    string? BuildExactMatchHint(string content, string oldString);
    int ResolveTargetLineNumber(string fileContent, string changeDesc,
        string? targetSymbol = null, string? estimatedLineRange = null,
        int plannerLineNumber = 0);
}
