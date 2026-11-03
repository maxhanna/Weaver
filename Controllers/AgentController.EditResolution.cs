using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Weaver.Services;
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
using Weaver;
namespace Weaver.Controllers;

partial class AgentController
{
    private (string? oldStr, string? error) AstResolveEdit(string fullPath, string targetType, string targetName, bool returnTail = false)
    {
        if (!System.IO.File.Exists(fullPath))
            return (null, "File not found for AST edit");
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (ext is ".css" or ".scss" or ".less")
            return (null, "AST not supported for stylesheet files — use text-based edit instead");
        var sourceText = System.IO.File.ReadAllText(fullPath, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(sourceText))
            return (null, "File is empty");
        if (ext != ".cs" && AstCodeEditorService.IsSupportedExtension(ext))
        {
            var (astOldStr, _, astErr) = AstCodeEditorService.FindFunctionSource(sourceText, targetName, ext);
            if (!string.IsNullOrWhiteSpace(astOldStr))
                return (astOldStr, null);
            if (!string.IsNullOrWhiteSpace(astErr))
            {
                Console.WriteLine($"[AstResolveEdit] TreeSitter warning for '{targetName}' in {ext}: {astErr}");
            }
        }
        if (ext != ".cs")
        {
            var patterns = new List<(string label, Regex regex)>();
            if (string.Equals(targetType, "method", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(targetType, "function", StringComparison.OrdinalIgnoreCase))
            {
                patterns.Add(("Method/function",
                    new Regex(
                        $@"^\s*(?:(?:export|default|async|static|public|private|protected|get|set|readonly|override|abstract)\s+)*" +
                        $@"(?:(?:function\s+)|(?:[\w$.]+\.)?)?{Regex.Escape(targetName)}\s*(?:<[^>]*>)?\s*" +
                        $@"(?:\([^)]*\)|=\s*(?:async\s+)?function\s*\([^)]*\)|:\s*(?:async\s+)?function\s*\([^)]*\)|:\s*(?:async\s+)?\([^)]*\)\s*=>)" +
                        $@"\s*(?::\s*[^{{;]+?)?\s*(?:{{|=>)",
                        RegexOptions.Multiline)));
                if (ext == ".go")
                    patterns.Add(("Go function",
                        new Regex(
                            $@"^\s*func\s+(?:\(\s*\w+\s+\*?\w+\s*\)\s+)?{Regex.Escape(targetName)}\s*\(",
                            RegexOptions.Multiline)));
                if (ext == ".rs")
                    patterns.Add(("Rust fn",
                        new Regex(
                            $@"^\s*(?:pub(?:\([^)]+\))?\s+)?(?:async\s+)?(?:unsafe\s+)?fn\s+{Regex.Escape(targetName)}\s*[<(]",
                            RegexOptions.Multiline)));
                if (ext == ".swift")
                    patterns.Add(("Swift func",
                        new Regex(
                            $@"^\s*(?:(?:public|private|internal|open|fileprivate|override|static|class|mutating|nonmutating|dynamic|final|lazy)\s+)*func\s+{Regex.Escape(targetName)}\s*[<(]",
                            RegexOptions.Multiline)));
                if (ext is ".kt" or ".kts")
                    patterns.Add(("Kotlin fun",
                        new Regex(
                            $@"^\s*(?:(?:public|private|protected|internal|override|abstract|open|inline|suspend|tailrec|operator|infix)\s+)*fun\s+{Regex.Escape(targetName)}\s*[<(]",
                            RegexOptions.Multiline)));
                if (ext == ".php")
                    patterns.Add(("PHP function",
                        new Regex(
                            $@"^\s*(?:(?:public|private|protected|static|abstract|final)\s+)*function\s+{Regex.Escape(targetName)}\s*\(",
                            RegexOptions.Multiline)));
                if (ext == ".rb")
                    patterns.Add(("Ruby def",
                        new Regex(
                            $@"^\s*def\s+(?:self\.)?{Regex.Escape(targetName)}\s*[\(\s]",
                            RegexOptions.Multiline)));
                if (ext is ".css" or ".scss" or ".less")
                {
                    patterns.Add(("CSS/SCSS class",
                        new Regex(
                            $@"^\s*\.{Regex.Escape(targetName)}\s*{{",
                            RegexOptions.Multiline)));
                    patterns.Add(("CSS/SCSS id",
                        new Regex(
                            $@"^\s*#{Regex.Escape(targetName)}\s*{{",
                            RegexOptions.Multiline)));
                    patterns.Add(("CSS/SCSS tag",
                        new Regex(
                            $@"^\s*{Regex.Escape(targetName)}\s*{{",
                            RegexOptions.Multiline)));
                }
            }
            else if (string.Equals(targetType, "class", StringComparison.OrdinalIgnoreCase))
            {
                patterns.Add(("Class",
                    new Regex($@"^\s*(?:export\s+)?(?:default\s+)?(?:abstract\s+)?class\s+{Regex.Escape(targetName)}\b",
                        RegexOptions.Multiline)));
            }
            else if (string.Equals(targetType, "interface", StringComparison.OrdinalIgnoreCase))
            {
                patterns.Add(("Interface",
                    new Regex($@"^\s*(?:export\s+)?(?:default\s+)?interface\s+{Regex.Escape(targetName)}\b",
                        RegexOptions.Multiline)));
            }
            else if (string.Equals(targetType, "property", StringComparison.OrdinalIgnoreCase))
            {
                patterns.Add(("Property",
                    new Regex(
                        $@"^\s*(?:(?:public|private|protected|readonly|static)\s+)*{Regex.Escape(targetName)}\s*(?::\s*[^;=]+)?\s*(?:=|[;)])",
                        RegexOptions.Multiline)));
            }
            else
            {
                return (null, $"For {ext} files, only targetType 'method'/'function'/'class'/'interface'/'property' is supported. Got '{targetType}'.");
            }
            Match match = Match.Empty;
            string label = "";
            foreach (var (lbl, rx) in patterns)
            {
                match = rx.Match(sourceText);
                if (match.Success) { label = lbl; break; }
            }
            if (!match.Success)
            {
                if ((string.Equals(targetType, "method", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(targetType, "function", StringComparison.OrdinalIgnoreCase)) &&
                    ext is ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs")
                {
                    var propertyPattern = new Regex(
                        $@"^\s*(?:(?:public|private|protected|readonly|static)\s+)*{Regex.Escape(targetName)}\s*(?::\s*[^;=]+)?\s*(?:=|[;)])",
                        RegexOptions.Multiline);
                    var propMatch = propertyPattern.Match(sourceText);
                    if (propMatch.Success)
                        return (propMatch.Value, null);
                }
                var hint = ext is ".html" or ".htm" or ".cshtml" or ".razor" or ".json" or ".css" or ".svg"
                    ? $" {ext} files don't contain named symbols — use oldString/newString format instead"
                    : ext is ".yaml" or ".yml" or ".toml"
                    ? $" {ext} config files don't contain named symbols — use oldString/newString format instead"
                    : "";
                return (null, $"{(string.IsNullOrEmpty(label) ? "Symbol" : label)} '{targetName}' not found in {ext} file.{hint}");
            }
            var startIdx = match.Index;
            if (ext == ".rb")
            {
                var defLine = sourceText[..startIdx].Split('\n')[^1];
                var defIndent = AgentTextUtilities.GetLeadingWhitespace(defLine);
                var searchFrom = startIdx + match.Length;
                var endRx = new Regex($@"^{Regex.Escape(defIndent)}end\s*$", RegexOptions.Multiline);
                var endMatch = endRx.Match(sourceText, searchFrom);
                if (!endMatch.Success)
                    return (null, $"Could not find matching 'end' for def '{targetName}'");
                var resolved2 = sourceText[startIdx..(endMatch.Index + endMatch.Length)]
                    .Replace("\r\n", "\n").Replace("\r", "\n");
                if (returnTail)
                {
                    var ls = resolved2.Split('\n');
                    return (string.Join("\n", ls[^Math.Min(3, ls.Length)..]), null);
                }
                return (resolved2, null);
            }
            if (string.Equals(targetType, "property", StringComparison.OrdinalIgnoreCase) &&
                match.Value.TrimEnd().EndsWith(";"))
                return (match.Value, null);
            var afterMatch = startIdx + match.Length;
            var openDelimIdx = -1;
            char openDelim = '\0', closeDelim = '\0';
            if (string.Equals(targetType, "property", StringComparison.OrdinalIgnoreCase))
            {
                var bracketIdx = sourceText.IndexOf('[', afterMatch);
                var braceIdx = sourceText.IndexOf('{', afterMatch);
                if (bracketIdx >= 0 && (braceIdx < 0 || bracketIdx < braceIdx))
                {
                    openDelimIdx = bracketIdx;
                    openDelim = '[';
                    closeDelim = ']';
                }
                else if (braceIdx >= 0)
                {
                    openDelimIdx = braceIdx;
                    openDelim = '{';
                    closeDelim = '}';
                }
            }
            else
            {
                openDelimIdx = sourceText.IndexOf('{', startIdx);
                openDelim = '{';
                closeDelim = '}';
            }
            if (openDelimIdx < 0)
                return (null, $"{label} '{targetName}' has no opening brace/bracket");
            var braceDepth = 0;
            var inSingleQuote = false;
            var inDoubleQuote = false;
            var inTemplate = false;
            var inLineComment = false;
            var inBlockComment = false;
            var endIdx = -1;
            for (var i = openDelimIdx; i < sourceText.Length; i++)
            {
                var c = sourceText[i];
                var p = i > 0 ? sourceText[i - 1] : '\0';
                if (!inBlockComment && !inLineComment && !inTemplate)
                {
                    if (c == '\'' && !inDoubleQuote) { inSingleQuote = !inSingleQuote; continue; }
                    if (c == '"' && !inSingleQuote) { inDoubleQuote = !inDoubleQuote; continue; }
                }
                if (!inBlockComment && !inLineComment && !inSingleQuote && !inDoubleQuote)
                {
                    if (c == '`') { inTemplate = !inTemplate; continue; }
                }
                if (!inBlockComment && !inSingleQuote && !inDoubleQuote && !inTemplate)
                {
                    if (c == '/' && p == '/') { inLineComment = true; continue; }
                    if (c == '*' && p == '/') { inBlockComment = true; continue; }
                }
                if (inLineComment && c == '\n') { inLineComment = false; continue; }
                if (inBlockComment && c == '/' && p == '*') { inBlockComment = false; continue; }
                if (inLineComment || inBlockComment || inSingleQuote || inDoubleQuote || inTemplate) continue;
                if (c == openDelim) braceDepth++;
                else if (c == closeDelim)
                {
                    braceDepth--;
                    if (braceDepth == 0) { endIdx = i; break; }
                }
            }
            if (endIdx < 0)
                return (null, $"Could not find closing brace/bracket for {label} '{targetName}'");
            var resolved = sourceText[startIdx..(endIdx + 1)].Replace("\r\n", "\n").Replace("\r", "\n");
            if (returnTail)
            {
                var lines = resolved.Split('\n');
                var tailCount = Math.Min(3, lines.Length);
                return (string.Join("\n", lines[^tailCount..]), null);
            }
            return (resolved, null);
        }
        SyntaxTree tree;
        try { tree = CSharpSyntaxTree.ParseText(sourceText); }
        catch (Exception ex)
        {
            return (null, $"Failed to parse C# file: {ex.Message}");
        }
        var root = tree.GetRoot();
        SyntaxNode? targetNode = null;
        if (string.Equals(targetType, "method", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(targetType, "function", StringComparison.OrdinalIgnoreCase))
        {
            targetNode = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => string.Equals(m.Identifier.Text, targetName, StringComparison.Ordinal));
            if (targetNode == null)
            {
                targetNode = root.DescendantNodes()
                    .OfType<ConstructorDeclarationSyntax>()
                    .FirstOrDefault(c =>
                    {
                        var ct = c.Parent as TypeDeclarationSyntax;
                        return ct != null && string.Equals(ct.Identifier.Text, targetName, StringComparison.Ordinal);
                    });
                if (targetNode != null)
                {
                    Console.WriteLine($"[AstResolveEdit] Method '{targetName}' not found — resolved as constructor of class '{targetName}' instead");
                }
            }
        }
        else if (string.Equals(targetType, "class", StringComparison.OrdinalIgnoreCase))
        {
            targetNode = root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => string.Equals(c.Identifier.Text, targetName, StringComparison.Ordinal));
        }
        else if (string.Equals(targetType, "property", StringComparison.OrdinalIgnoreCase))
        {
            targetNode = root.DescendantNodes()
                .OfType<PropertyDeclarationSyntax>()
                .FirstOrDefault(p => string.Equals(p.Identifier.Text, targetName, StringComparison.Ordinal));
        }
        else if (string.Equals(targetType, "interface", StringComparison.OrdinalIgnoreCase))
        {
            targetNode = root.DescendantNodes()
                .OfType<InterfaceDeclarationSyntax>()
                .FirstOrDefault(i => string.Equals(i.Identifier.Text, targetName, StringComparison.Ordinal));
        }
        else if (string.Equals(targetType, "struct", StringComparison.OrdinalIgnoreCase))
        {
            targetNode = root.DescendantNodes()
                .OfType<StructDeclarationSyntax>()
                .FirstOrDefault(s => string.Equals(s.Identifier.Text, targetName, StringComparison.Ordinal));
        }
        else if (string.Equals(targetType, "record", StringComparison.OrdinalIgnoreCase))
        {
            targetNode = root.DescendantNodes()
                .OfType<RecordDeclarationSyntax>()
                .FirstOrDefault(r => string.Equals(r.Identifier.Text, targetName, StringComparison.Ordinal));
        }
        else if (string.Equals(targetType, "enum", StringComparison.OrdinalIgnoreCase))
        {
            targetNode = root.DescendantNodes()
                .OfType<EnumDeclarationSyntax>()
                .FirstOrDefault(e => string.Equals(e.Identifier.Text, targetName, StringComparison.Ordinal));
        }
        else if (string.Equals(targetType, "constructor", StringComparison.OrdinalIgnoreCase))
        {
            targetNode = root.DescendantNodes()
                .OfType<ConstructorDeclarationSyntax>()
                .FirstOrDefault(c =>
                {
                    var ct = c.Parent as TypeDeclarationSyntax;
                    return ct != null && string.Equals(ct.Identifier.Text, targetName, StringComparison.Ordinal);
                });
        }
        else
        {
            return (null, $"Unknown targetType '{targetType}'. Supported: method, class, property, interface, struct, record, enum, constructor");
        }
        if (targetNode == null)
        {
            if (string.Equals(targetType, "method", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(targetType, "function", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(targetType, "constructor", StringComparison.OrdinalIgnoreCase))
            {
                var hasTopLevel = root.DescendantNodes().OfType<GlobalStatementSyntax>().Any();
                if (hasTopLevel)
                    return (null,
                        $"'{targetName}' not found — this .cs file uses TOP-LEVEL STATEMENTS " +
                        "(C# 9+ Program.cs style; no class, no explicit Main). " +
                        "FORMAT C is unsupported here. Use oldString/newString: " +
                        "copy the exact lines to change verbatim from the file content shown in the prompt.");
            }
            var kind = char.ToUpper(targetType[0]) + targetType[1..];
            return (null, $"{kind} '{targetName}' not found in file");
        }
        if (returnTail)
        {
            var nodeBody = targetNode.ToString();
            var lines = nodeBody.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var tailCount = Math.Min(3, lines.Length);
            var tail = string.Join("\n", lines[^tailCount..]);
            return (tail, null);
        }
        var leading = targetNode.GetLeadingTrivia().ToFullString();
        var body = targetNode.ToString();
        var oldStr = leading + body;
        oldStr = oldStr.Replace("\r\n", "\n").Replace("\r", "\n");
        return (oldStr, null);
    }
    private static async Task<string> FormatSnippetAsync(string oldSource, string newCode, string? filePath, string? explicitBaseIndent = null)
    {
        var baseIndent = explicitBaseIndent;
        if (baseIndent == null)
        {
            var oldLines = oldSource.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var firstRealLine = oldLines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (firstRealLine == null) return newCode;
            baseIndent = Regex.Match(firstRealLine, @"^(\s*)").Value;
        }
        if (string.IsNullOrEmpty(baseIndent)) return newCode;

        string formatted;
        if (filePath != null && CodeFormatterService.CanFormat(filePath))
        {
            try
            {
                formatted = await CodeFormatterService.FormatAsync(filePath, newCode, CancellationToken.None);
            }
            catch
            {
                formatted = newCode;
            }
        }
        else
        {
            formatted = newCode;
        }

        // Python is indentation-significant: the generic min-indent realignment below counts
        // a tab as ONE character, so a block whose lines mix tabs and spaces (a very common
        // LLM emission) gets misaligned and the file dies with TabError. Rebuild the block
        // against the anchor's own indent unit, preserving RELATIVE depth.
        if (filePath != null && Path.GetExtension(filePath).Equals(".py", StringComparison.OrdinalIgnoreCase))
        {
            return AgentCodeFormatting.ReindentPythonBlock(formatted, baseIndent);
        }

        // Prettier can return the raw LLM snippet when the replacement is a fragment or
        // the local formatter is unavailable. For TypeScript/JavaScript that raw snippet
        // is often syntactically valid but flat, so min-indent normalization alone would
        // preserve the flattening. Rebuild only the replacement block from its braces,
        // using the original anchor as the indentation-style reference.
        if (filePath != null &&
            Path.GetExtension(filePath) is ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs")
        {
            var sourceLines = oldSource.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            if (formatted.Split('\n').Count(l => !string.IsNullOrWhiteSpace(l)) > 2 &&
                (formatted.Contains('{') || formatted.Contains('}')))
            {
                formatted = AgentCodeFormatting.AutoIndentFromFile(
                    formatted, baseIndent, sourceLines, 0);
            }
        }

        var lines = formatted.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var minIndent = int.MaxValue;
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                var indentLen = line.TakeWhile(char.IsWhiteSpace).Count();
                if (indentLen < minIndent) minIndent = indentLen;
            }
        }
        if (minIndent == int.MaxValue) minIndent = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                lines[i] = baseIndent + (minIndent < lines[i].Length ? lines[i].Substring(minIndent) : "");
            }
        }
        return string.Join("\n", lines);
    }
    internal static bool IsFullFileAllowed(bool fileExists, int contentLength)
        => !fileExists || contentLength < 500;

    private async Task<(string? oldStr, string? newStr, bool fullFile,
      string? fullContent, bool alreadyDone, string? error, bool fromFormatC)>
      ResolveEditForStep(PlanStep step, string projectRoot, bool emitSse,
        CancellationToken ct,
        List<(string old, string @new, string error)>? history = null,
        string? explorationContext = null,
        string? targetSymbol = null,
        string? originalPrompt = null,
        string? preservationDirective = null,
        AgentPlan? fullPlan = null,
        int planItemIndex = -1,
        string? filteredEditKnowledge = null,
        string? causalContext = null,
        string? forcedOldString = null,
        string? webResultsContext = null)
    {
        var cfg5 = await LoadConfigAsync();
        var relPath = step.File.Replace('\\', '/');
        var fullPath = Path.GetFullPath(
            Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
        var fileExists = System.IO.File.Exists(fullPath);
        var fileContent = fileExists
            ? await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct)
            : string.Empty;
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(causalContext))
        {
            sb.AppendLine(causalContext);
        }
        if (!string.IsNullOrWhiteSpace(originalPrompt))
        {
            sb.AppendLine("### ORIGINAL USER REQUEST (for context) ###");
            sb.AppendLine(originalPrompt);
            sb.AppendLine();
            sb.AppendLine("⚠ NOTE: The CHANGE REQUIRED below is a specific step derived from the request above. " +
                          "You MUST implement exactly what the step asks for, but ensure the result adheres to ALL " +
                          "specific details, locations, and constraints mentioned in the original request. " +
                          "For example, if the original request says 'under nicehash bot note', your edit MUST place the text near 'NiceHash'. " +
                          "If it says 'users need kraken api key', your edit MUST include that exact requirement.");
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(filteredEditKnowledge))
        {
            sb.AppendLine(filteredEditKnowledge);
        }
        if (!string.IsNullOrWhiteSpace(webResultsContext))
        {
            sb.AppendLine("### WEB RESULTS (fetched earlier in this run) ###");
            sb.AppendLine(webResultsContext);
            sb.AppendLine("⚠ RULE: The sections above are REAL fetched results. When newString needs titles, URLs, facts, " +
                          "dates, or data that come from the web, copy them EXACTLY from the WEB RESULTS above " +
                          "(verbatim titles and URLs). NEVER invent article titles, URLs, or facts that are not present here.");
            sb.AppendLine();
        }
        if (fullPlan?.Plan?.Count > 0 && planItemIndex >= 0)
        {
            var priorSteps = new StringBuilder();
            for (var i = 0; i < planItemIndex; i++)
            {
                if (i < fullPlan.Plan.Count)
                {
                    var p = fullPlan.Plan[i];
                    priorSteps.AppendLine($"  ✓ Step {i + 1} (DONE): [{p.File}] {p.Change}");
                }
            }
            if (priorSteps.Length > 0)
            {
                sb.AppendLine("### PRIOR STEPS CONTEXT (What has already been done in this plan) ###");
                sb.AppendLine(priorSteps.ToString());
                sb.AppendLine("⚠ CRITICAL RULE: If a prior step added a new method, property, or variable, you MUST use that EXACT symbol in your current edit. Do NOT reinvent the logic inline. Do NOT hallucinate alternative property names. For example, if a prior step added `isFileLimitReached()`, you MUST use `isFileLimitReached()` in your HTML/TS code, not `uploadFileList.length >= maxFileAttachments`.");
                sb.AppendLine();
            }
        }
        sb.AppendLine($"FILE: {relPath}");
        sb.AppendLine($"CHANGE REQUIRED: {step.Change}");

        if (!string.IsNullOrWhiteSpace(preservationDirective))
        {
            sb.AppendLine();
            sb.AppendLine("🛡️ MANDATORY PRESERVATION DIRECTIVE (from Sub-Agent Analysis)");
            sb.AppendLine(preservationDirective);
            sb.AppendLine("⚠ You MUST adhere to this directive. Do NOT invent new logic if the directive tells you to reuse existing patterns. Your edit will be rejected if you break these constraints.");
            sb.AppendLine();
        }
        sb.AppendLine(
            "⚠ RULE: REPLACE existing code — do NOT add new alongside existing. " +
                "If the change says \"instead of X use Y\", modify X to become Y. " +
                "Do NOT keep the old X and also add Y next to it. " +
            "⚠ RULE: NEVER INVENT type names. Every type (class/record/struct/interface) referenced in newString MUST exist in the project. " +
                "The RELATED FILE CONTEXT section above shows type definitions found across the project. " +
                "If a type exists there (e.g. CalendarEntry, UserInfo), use it — do NOT invent a similar type with a different name. " +
                "If you need a type that is NOT in the context or project, define it in the same edit by including the full class definition. " +
            "⚠ RULE: NEVER INVENT property names. Every `.PropertyName` you access on an object MUST exactly match a property " +
                "defined in that type's class. Example: CalendarEntry has properties [Id, Type, Note, Date, Ownership] — NOT Title or Description. " +
                "Cross-reference EVERY property access against the type definition in AUTO-ENRICHED CONTEXT before writing newString. " +
                "If the class definition shows `string? Note { get; set; }` then use `.Note`, not `.Description`. " +
                "If the class definition shows `string? Type { get; set; }` then use `.Type`, not `.Title`." +
            "⚠ RULE: Do not add comments inside new code." +
            "⚠ RULE: When adding pagination, filtering, or controls for a NEW data type (e.g., YouTube results), " +
                "create a NEW method dedicated to that data type. Do NOT repurpose an existing method that uses different " +
                "property names (e.g., `currentPage`/`totalPages`) and calls different APIs (`searchUrl`). " +
                "For example, if YouTube pagination needs `onYoutubePageChange`, create it — do NOT reuse `onPageChange` " +
                "because `onPageChange` sets `this.currentPage` and calls `this.searchUrl()`, which are specific to " +
                "crawler search results. A new method for YouTube would set `this.youtubeCurrentPage` and filter " +
                "`this.youtubeResults` locally without calling `searchUrl`." +
            "⚠ RULE: LOCATION ACCURACY & CONTEXT. If the CHANGE REQUIRED specifies a variable, array, or method name (e.g., 'in navigationItemDescriptions array'), " +
                "you MUST find and edit THAT specific location. Do not edit the first similar-looking code you find. " +
                "If there are multiple arrays with 'Crypto-Hub', find the one named 'navigationItemDescriptions'. " +
                "If the ORIGINAL USER REQUEST mentions 'under nicehash bot note', you MUST find the text containing 'NiceHash' and add the note there. " +
                "If the request mentions 'instructions can be found in the user settings', your added text MUST include that instruction. " +
                "Do NOT hallucinate generic text. Use the exact details from the ORIGINAL USER REQUEST." +
            "⚠ RULE: TEMPLATE LITERALS & PROPERTIES. " +
                "You CANNOT add a new property (like a second `content:` line) to an object that already has one. " +
                "If you need to add text to a backtick template literal (e.g. `content: \\`Some text\\``), you MUST:\n" +
                "  1. Set `oldString` to the ENTIRE existing property (e.g. `      content: \\`Crypto Hub does many...\\\n" +
                "      <ul>...</ul>\\\n" +
                "      <div>...NiceHash...</div>\\\n" +
                "      \\``)\n" +
                "  2. Set `newString` to that EXACT same property, but with your new text appended INSIDE the backticks before the closing \\`.\n" +
                "DO NOT take shortcuts. DO NOT add a new `content:` line above the existing one. ALWAYS modify the existing backtick block.");
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        // Classify once here — used by all downstream prompt sections, system prompt selection,
        // and escalation logic. This is the single source of truth for the whole method.
        var editStrategy = EditClassifier.Classify(step, fileExists, ext);
        var (langFamily, langSupportsFormatC, langHint) = AgentMethodInventory.GetLanguageProfile(ext);
        sb.AppendLine(langHint);

        if (ext == ".cs" && fileExists && !string.IsNullOrWhiteSpace(fileContent))
        {
            try
            {
                var tlTree = CSharpSyntaxTree.ParseText(fileContent);
                var tlRoot = tlTree.GetRoot();
                if (tlRoot.DescendantNodes().OfType<GlobalStatementSyntax>().Any())
                {
                    sb.AppendLine(
                        "⚠ OVERRIDE — TOP-LEVEL STATEMENTS FILE (Program.cs style): " +
                        "The C# hint above does NOT apply here. This file has no class " +
                        "declarations and no named methods, so FORMAT C will ALWAYS FAIL. " +
                        "You MUST use oldString/newString. " +
                        "Copy the exact lines to replace verbatim from the file content below, " +
                        "including every leading space. Use a 3–6 line anchor for uniqueness.");
                }
            }
            catch { }
        }
        var lineCount = fileContent.Split('\n').Length;
        var isLarge = fileContent.Length > cfg5.fileBodyTruncationChars;
        if (isLarge)
        {
            // For a classified ReplaceMethod step the change IS a whole-method rewrite by
            // definition, so the "use oldString/newString instead" advice below would directly
            // contradict the FORMAT C REPLACE directive. Only warn when the strategy is NOT
            // ReplaceMethod (small change inside a big method).
            if (editStrategy != EditStrategy.ReplaceMethod)
            {
                sb.AppendLine("⚠ LARGE FILE/METHOD WARNING: If the target method is super long (e.g., 100+ lines) and the change is small, " +
                              "using FORMAT C to rewrite the entire method will almost certainly cause hallucinations and break existing logic. " +
                              "You MUST use oldString/newString to target just the lines that need changing. Do NOT reinvent the wheel.");
                if (langSupportsFormatC && ext != ".cs")
                    sb.AppendLine("⚠ Only use FORMAT C (targetType/targetName) if you are rewriting the ENTIRE method body. For small changes, use oldString/newString.");
                else if (ext != ".cs")
                    sb.AppendLine("⚠ Large file — use a tight oldString (3–6 lines max). " +
                                  "The excerpt above is the ONLY portion shown; your oldString MUST appear in it.");
            }
            else
            {
                sb.AppendLine("⚠ LARGE FILE — this step is a classified ENTIRE-METHOD/CLASS REPLACEMENT, so FORMAT C REPLACE applies: " +
                              "preserve the existing signature and inline SQL verbatim in newCode; do NOT invent or drop logic outside the target method.");
            }
        }
        else if (ext is ".css" or ".scss" or ".sass")
        {
            sb.AppendLine("⚠ CSS FILE: preserve ALL whitespace in property values exactly " +
                          "(e.g. '0px 1px' must stay as two tokens with a space; 'rgba(255, 255, 255, 0.06)' must keep spaces after every comma).");
            if ((step.Change ?? "").Contains("Remove", StringComparison.OrdinalIgnoreCase) ||
                           (step.Change ?? "").Contains("Delete", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("⚠ CSS DELETION: To remove CSS rules, set `oldString` to the exact block of rules to remove, and set `newString` to an empty array `[]` or an empty string `\"\"`. Do NOT output the same code in both fields.");
            }
            if (fileExists && !string.IsNullOrWhiteSpace(fileContent))
            {
                var existingSelectors = ExtractTopLevelCssSelectors(fileContent);
                if (existingSelectors.Count > 0)
                {
                    sb.AppendLine("⚠ EXISTING CSS SELECTORS in this file — MODIFY these rules, do NOT add new rules with the same selector:");
                    foreach (var s in existingSelectors.Take(20))
                        sb.AppendLine($"    • {s}");
                    if (existingSelectors.Count > 20)
                        sb.AppendLine($"    • ... and {existingSelectors.Count - 20} more");
                    sb.AppendLine("  If the change asks you to update one of these (e.g. 'make .kanban-board wrap'), " +
                                  "set oldString to the EXISTING rule's body and modify it. Do NOT add a duplicate rule.");
                }
            }
        }
        else if (ext is ".ts" or ".tsx" or ".js" or ".jsx")
        {
            sb.AppendLine("⚠ TS/JS FILE: preserve ALL indentation exactly — " +
                          "methods inside a class body MUST be indented, nested blocks " +
                          "must be indented relative to their parent. Copy the leading " +
                          "whitespace from oldString character-for-character into newString.");
            var changeLowerTs = (step.Change ?? "").ToLowerInvariant();
            if (changeLowerTs.Contains("method") || changeLowerTs.Contains("handler") || changeLowerTs.Contains("function"))
            {
                sb.AppendLine("⚠ CRITICAL: The CHANGE REQUIRED asks to add a method/handler/function. " +
                              "Your `newString` MUST include the FULL method declaration (e.g., `methodName() { ... }`) " +
                              "in addition to any property changes. Do NOT only update properties and forget the method. " +
                              "If you are only adding a method, you can also use FORMAT C with insertAfter:true.");
            }
        }
        // ── Format hint for the user prompt — keyed off editStrategy (classified once above) ──
        if (ext == ".cs" || !fileExists)
        {
            switch (editStrategy)
            {
                case EditStrategy.FillClassBody:
                    sb.AppendLine("⚠ EDIT FORMAT: FORMAT C (targetType=\"class\", NO insertAfter) — filling an existing class with new properties.");
                    sb.AppendLine(BuildFormatCExamples(FormatCVariant.ClassFill));
                    break;
                case EditStrategy.InsertMethod:
                    sb.AppendLine("⚠ EDIT FORMAT: FORMAT C (insertAfter) — adding a new C# method/endpoint. Full example follows below in the FORMAT C section.");
                    break;
                case EditStrategy.ReplaceMethod:
                    sb.AppendLine("⚠ EDIT FORMAT: FORMAT C (REPLACE, NO insertAfter) — rewriting an entire existing C# method/class. Full example follows below in the FORMAT C section.");
                    break;
                case EditStrategy.DeleteLines:
                    sb.AppendLine("⚠ EDIT FORMAT: oldString/newString (deletion) — removing code.");
                    sb.AppendLine("  oldString = exact lines to delete (1-5 max). newString = empty.");
                    sb.AppendLine("  Do NOT include surrounding container lines — delete ONLY what's asked.");
                    break;
                case EditStrategy.CreateFile:
                    sb.AppendLine("⚠ FILE DOES NOT EXIST YET. Use fullFile format to create it with complete content.");
                    break;
                default:
                    if (ext == ".cs")
                    {
                        sb.AppendLine("⚠ EDIT FORMAT: oldString/newString (targeted edit) — modifying existing code.");
                        sb.AppendLine("  Copy 2-3 lines verbatim from the file as oldString.");
                        sb.AppendLine("  Include the line above and below your change as anchor context, repeating them unchanged in newString.");
                        sb.AppendLine("  SQL STRINGS: Preserve exact whitespace. 'INTERVAL 15 MINUTE' is correct, 'INTERVAL15MINUTE' is wrong.");
                    }
                    break;
            }
            sb.AppendLine();
        }
        else if (!fileExists)
        {
            sb.AppendLine("⚠ FILE DOES NOT EXIST YET. Use fullFile format to create it with complete content.");
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(explorationContext))
        {
            var distilled = AgentDiscovery.DistillExplorationContext(
                explorationContext, relPath, step.Change ?? "", targetSymbol);
            if (!string.IsNullOrWhiteSpace(distilled))
            {
                sb.AppendLine();
                sb.AppendLine("## RELATED FILE CONTEXT");
                sb.AppendLine("Types, interfaces, and relevant code from files read during exploration " +
                              "(target file is shown above; these are supporting files only):");
                sb.AppendLine(distilled);
                sb.AppendLine();
            }
            var typeNameMatch = Regex.Match(step.Change ?? "", @"\b([A-Z]\w*(?:Dto|DTO|Request|Response|Model|Data))\b");
            if (typeNameMatch.Success)
            {
                var paramType = typeNameMatch.Groups[1].Value;
                var typeSection = Regex.Match(explorationContext,
                    $@"(?:class|record|struct)\s+{Regex.Escape(paramType)}\b[^;{{]*(?:{{[^}}]*}})?",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (typeSection.Success)
                {
                    var props = Regex.Matches(typeSection.Value,
                        @"(?:public|private|protected|internal|readonly|static)?\s*(?:\w+(?:\[\])?(?:<[^>]*>)?)\s+(\w+)\s*\{\s*get;\s*set;\s*\}",
                        RegexOptions.Multiline)
                        .Select(m => m.Groups[1].Value)
                        .ToList();
                    if (props.Count > 0)
                    {
                        sb.AppendLine("⚠ TYPE EVIDENCE — Parameter type `" + paramType + "` has these EXACT properties.");
                        sb.AppendLine("  You MUST use these property names DIRECTLY on the parameter variable.");
                        sb.AppendLine("  Do NOT nest them under an invented wrapper (e.g. do NOT write `param.System?.OS` — write `param.OS`).");
                        sb.AppendLine("  Properties: " + string.Join(", ", props));
                        sb.AppendLine();
                    }
                }
            }
        }
        if (!fileExists)
        {
            sb.AppendLine("FILE DOES NOT EXIST YET. Use <<<FULL_FILE>>> to create it with complete content.");
        }
        else
        {
            if (isLarge)
            {
                sb.AppendLine($"FILE SIZE: {fileContent.Length} chars, {lineCount} lines. Showing relevant excerpt:");
                sb.AppendLine("```");
                sb.AppendLine(AgentDiscovery.ExtractRelevantExcerpt(fileContent, step.Change ?? "", step.OldString, cfg5.fileBodyTruncationChars, ext));
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine($"For CODE files ({string.Join(", ", new[] { ".cs", ".ts", ".js", ".java", ".go", ".rs", ".swift", ".kt", ".php", ".rb" })}): "
                    + "use FORMAT C (targetType/targetName/newCode) for replacing ENTIRE methods. "
                    + "For SMALL changes (1-5 lines), use oldString/newString even for code files — copy lines verbatim from the excerpt above. "
                    + "NEVER rewrite inline SQL queries — preserve them exactly as-is.");
                sb.AppendLine("For ALL other file types (except HTML): use oldString/newString.");
            }
            else
            {
                sb.AppendLine("CURRENT FILE CONTENT:");
                sb.AppendLine("```");
                sb.AppendLine(fileContent);
                sb.AppendLine("```");
            }
        }
        if (!string.IsNullOrWhiteSpace(forcedOldString))
        {
            sb.AppendLine();
            sb.AppendLine("⚠ PREDETERMINED OLDSTRING (use THIS exactly):");
            sb.AppendLine("```");
            sb.AppendLine(forcedOldString);
            sb.AppendLine("```");
            sb.AppendLine("The oldString above was AST-resolved from the file — it is the EXACT target method/function source.");
            sb.AppendLine("You MUST use this EXACT string as your `oldString` in the edit. Do NOT try to match partial code.");
            sb.AppendLine("Only provide `newString` — the replacement for this entire block. " +
                          "If replacing a method, newString MUST include a complete method declaration (signature + body).");
            sb.AppendLine();
        }
        sb.AppendLine();
        // ── Format directive for the output section — keyed off editStrategy ──────
        if (HtmlDomEditor.IsHtmlDomFile(relPath))
        {
            sb.AppendLine();
            sb.AppendLine("⚠ HTML FILE — FORMAT D is REQUIRED. This is the ONLY accepted format.");
            sb.AppendLine("  ⚠ CRITICAL: ONE THING PER EDIT. Each FORMAT D edit does ONE replacement OR ONE insertion.");
            sb.AppendLine("  Do NOT bundle multiple changes (wrapping sections, removing ngIf, adding wrappers, etc.) into one edit.");
            sb.AppendLine("  If multiple changes are needed, you MUST output them as separate edits in separate LLM responses.");
            sb.AppendLine("  Three modes:");
            if (EditClassifier.IsVariableSwap((step.Change ?? "").ToLowerInvariant(), step.TargetSymbol))
            {
                sb.AppendLine("  ⚠ SMALL VARIABLE/EXPRESSION SWAP DETECTED — this step replaces ONE token (e.g. `b` → `group`). " +
                              "Use TARGETED REPLACE below: targetName = the single line containing the token (verbatim), " +
                              "newCode = that line with ONLY the token swapped (plus any new lines). Do NOT reproduce " +
                              "the enclosing block/section.");
            }
            sb.AppendLine("  ⚠ TARGETED REPLACE (PREFERRED — the default): when the change is small (wrap one element, " +
                            "swap one line, tweak a single attribute), do NOT reproduce the whole enclosing section. " +
                            "Emit a REPLACE edit whose targetName is the ONE unique line you are changing and whose " +
                            "newCode is ONLY the replacement for that line (may be several lines):");
            sb.AppendLine("     {\"targetType\": \"html\", \"targetName\": \"<THE single line being replaced, verbatim>\", \"replace\": true, " +
                            "\"newCode\": [\"<replacement line 1>\", \"<replacement line 2>\"]}");
            sb.AppendLine("     Example: to wrap an ngFor item in a group header, targetName = the one line " +
                            "`<div *ngFor=\"let b of benchmarks\" class=\"benchmark-item\">` and newCode = the new " +
                            "wrapper opening + that same line. The rest of the section stays untouched — never " +
                            "re-emit it in newCode.");
            sb.AppendLine(BuildTargetedReplaceWorkedExample());
            sb.AppendLine();
            sb.AppendLine("  1. {\"targetType\": \"html\", \"targetName\": \"...\", \"replace\": true, \"newCode\": [...]} — REPLACE the matched code block with newCode.");
            sb.AppendLine("     REPLACE mode: newCode replaces ONLY the targetName block — do NOT include the parent " +
                            "tags or closing tags that remain unchanged (the system keeps them). Keep newCode as " +
                            "small as the change allows; a 1-line targetName + a few newCode lines is ideal.");
            sb.AppendLine("     REPLACE mode: targetName must be UNIQUE — it should be a single line that appears ONCE in the file.");
            sb.AppendLine("     REPLACE mode: do NOT include surrounding content in targetName — only the specific element or line to replace.");
            sb.AppendLine("  2. {\"targetType\": \"html\", \"targetName\": \"...\", \"insertAfter\": true, \"newCode\": [...]} — INSERT newCode AFTER the matched code block.");
            sb.AppendLine("     INSERT mode: newCode contains ONLY the new HTML to insert. Do NOT include any </div> closing tags.");
            sb.AppendLine("     INSERT mode: targetName must be EXACTLY one line from the file. Do NOT multi-line targetName in insert mode.");
            sb.AppendLine("  3. {\"targetType\": \"html\", \"targetName\": \"...\", \"replace\": true, \"newCode\": [...]} — REPLACE the matched code block with newCode.");
            sb.AppendLine("     Semantics: insertAfter:false → replace (when replace is absent); replace:false → insertAfter (when insertAfter is absent); no fields → insertBefore.");
            sb.AppendLine("  targetName is a CODE BLOCK — copy it VERBATIM from the file. " +
                             "Multi-line is OK ONLY when the change genuinely rewrites a whole block; for small " +
                             "changes use the single-line TARGETED REPLACE above. The system finds this block then " +
                             "inserts/replaces relative to it.");
            sb.AppendLine("  CRITICAL: newCode MUST NOT be empty when replace:true. " +
                            "With replace:true, newCode must contain the replacement for the targetName block only " +
                            "— do NOT re-emit unchanged sibling/parent content.");
            sb.AppendLine("  DO NOT output oldString, newString, or fullFile fields. DO NOT use oldString/newstring format.");
            sb.AppendLine("  ANCHOR SELECTION: prefer the SHORTEST unique line as targetName — a heading, " +
                            "a plain-text label (e.g. '<div class=\"groupDomainTitle\">YouTube Results</div>'), " +
                            "or a closing tag right before your insertion point. " +
                            "AVOID copying attribute-heavy divs (multiple [brackets], (parens), Angular pipes, or " +
                            "ternaries like `?0.5 :1`) as targetName — reproducing their exact internal spacing " +
                            "verbatim is unreliable and causes anchor-not-found errors.");
        }
        else if (editStrategy == EditStrategy.InsertMethod)
        {
            sb.AppendLine();
            sb.AppendLine("⚠ NEW METHOD INSERTION — You MUST use FORMAT C with insertAfter:true.");
            sb.AppendLine(BuildFormatCExamples(FormatCVariant.Insert));
        }
        else if (editStrategy == EditStrategy.ReplaceMethod)
        {
            sb.AppendLine();
            sb.AppendLine("⚠ ENTIRE METHOD/CLASS REPLACEMENT — You MUST use FORMAT C (REPLACE, NO insertAfter).");
            sb.AppendLine(BuildFormatCExamples(FormatCVariant.Replace));
        }
        else
        {
            sb.AppendLine("STRICT oldString SIZE LIMIT: MAXIMUM 10 lines. If you output more than 10 lines in oldString, the edit WILL fail.");
            if (EditClassifier.IsVariableSwap((step.Change ?? "").ToLowerInvariant(), step.TargetSymbol))
            {
                sb.AppendLine("⚠ SMALL VARIABLE/EXPRESSION SWAP DETECTED — this step replaces ONE token (e.g. `b` → `group`). " +
                              "oldString MUST be the single unique line containing that token, copied verbatim. " +
                              "newString = that same line with ONLY the token swapped (plus any new lines after it). " +
                              "Do NOT reproduce the enclosing block/section — a tiny anchor is all that is needed.");
            }
            sb.AppendLine(BuildTargetedReplaceWorkedExample());
            sb.AppendLine();
            sb.AppendLine("SMALL targeted edits (1-5 lines, e.g. add a column to SQL, add one property): PREFER oldString/newString. " +
                          "Include the line above/below for anchor context, repeat them unchanged in newString.");
            sb.AppendLine("For FULL method/class replacements (entire method body rewrite): use FORMAT C (targetType/targetName/newCode) " +
                          "with unchanged signature and preserve all inline SQL verbatim.");
            sb.AppendLine("For HTML files: use FORMAT D (targetType=\"html\", targetName=CODE BLOCK from the file, insertAfter=true/false, newCode=[...]). " +
                          "targetName is a code block copied verbatim from the file (can be multi-line). " +
                          "For insertAfter: the block is found and newCode is inserted after it. " +
                          "For replace: the block is replaced with newCode. " +
                          "For CSS, JSON, and other data files: use oldString/newString.");
            sb.AppendLine("▌ METHOD EDITS — CHOOSE THE RIGHT MODE:");
            sb.AppendLine("  • To ADD a NEW method (does not exist yet): use insertAfter:true with targetType=\"method\" and targetName of an EXISTING method.");
            sb.AppendLine("  • To REPLACE an entire EXISTING method: use FORMAT C (targetType=\"method\", targetName=\"MethodName\") WITHOUT insertAfter. " +
                          "PRESERVE the existing attributes, return type, name, and parameters verbatim in newCode. " +
                          "PRESERVE all existing inline SQL queries verbatim — never rewrite them.");
            sb.AppendLine("  • To MODIFY code WITHIN an existing method (add/change a few lines): use oldString/newString.");
            sb.AppendLine("  ⚠ NEVER use insertAfter:true when the method ALREADY EXISTS — that creates a DUPLICATE method, causing compilation errors.");
            sb.AppendLine("To ADD a single PROPERTY/FIELD/VARIABLE: use insertAfter with targetName = the EXACT line to insert after. " +
                          "PREFER a SHORT unique line (e.g. an import statement, a property declaration). " +
                          "Set insertAfter=true, newCode = the new code line(s). No targetType needed. " +
                          "Example: {\"targetName\": \"import { UserEventService } from '...';\", \"newCode\": \"declare var $: any;\", \"insertAfter\": true}");
            sb.AppendLine("To REPLACE an entire class: use FORMAT C (targetType=\"class\", targetName=\"ClassName\") with newCode containing the FULL class declaration.");
            sb.AppendLine("⚠ fullFile format: ONLY valid when the file does NOT exist yet. " +
                           "For existing files, fullFile WILL be rejected unless the file is very small (<500 chars). " +
                           "Use oldString/newString or FORMAT C for existing files.");
            sb.AppendLine("To APPEND to the end of the file: oldString = last 2-3 closing braces.");
        }
        if (history?.Count > 0)
        {
            var hadTruncation = history.Any(h => h.error.Contains("truncated", StringComparison.OrdinalIgnoreCase));
            sb.AppendLine();
            sb.AppendLine($"⚠ PREVIOUS {history.Count} ATTEMPT(S) FAILED. Learn from each failure:");
            for (var i = 0; i < history.Count; i++)
            {
                var h = history[i];
                sb.AppendLine($"\n--- Attempt {i + 1} — Error: {h.error} ---");
                if (editStrategy == EditStrategy.InsertMethod &&
                    (h.error.Contains("oldString", StringComparison.OrdinalIgnoreCase) ||
                     h.error.Contains("fullFile", StringComparison.OrdinalIgnoreCase) ||
                     h.error.Contains("STRICT MAXIMUM", StringComparison.OrdinalIgnoreCase) ||
                     h.error.Contains("does not contain any method", StringComparison.OrdinalIgnoreCase)))
                {
                    sb.AppendLine("  ⚠ NEW METHOD INSERTION: You MUST use FORMAT C with insertAfter:true.");
                    sb.AppendLine("  Do NOT use oldString/newString or fullFile — they WILL fail.");
                    sb.AppendLine("  You MUST output ONLY targetType/targetName/insertAfter/newCode — no oldString, no newString, no fullFile:");
                    sb.AppendLine(BuildFormatCExamples(FormatCVariant.Insert));
                }
                else if (h.error.Contains("IDENTICAL to the existing code", StringComparison.OrdinalIgnoreCase) ||
    h.error.Contains("identical after normalization", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("  ⚠ CRITICAL: Your newCode was IDENTICAL to the existing method — nothing changed.");
                    sb.AppendLine("  You reproduced code that is already in the file. This is NOT what CHANGE REQUIRED asks for.");
                    var priorDifferentAttempt = history
                        .Take(i)
                        .FirstOrDefault(prev =>
                            prev.error.Contains("Method signature changed", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(prev.@new));
                    if (priorDifferentAttempt != default)
                    {
                        sb.AppendLine();
                        sb.AppendLine("  REMINDER — you wrote DIFFERENT code earlier (attempt 1) but had the wrong signature.");
                        sb.AppendLine("  Use THAT logic, keeping the ORIGINAL method signature:");
                        sb.AppendLine("  ```");
                        sb.AppendLine($"  {priorDifferentAttempt.@new[..Math.Min(1000, priorDifferentAttempt.@new.Length)]}");
                        sb.AppendLine("  ```");
                        sb.AppendLine("  Change ONLY the first line to match the original return type. Keep all the body logic above.");
                    }
                    else
                    {
                        sb.AppendLine("  You MUST write a DIFFERENT method body that implements the new functionality.");
                        sb.AppendLine("  The existing method already fetches data. ADD the new logic on top of it.");
                        var priorSqlError = history
                            .Take(i)
                            .FirstOrDefault(prev =>
                                prev.error.Contains("SQL table(s)", StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrWhiteSpace(prev.old));
                        if (priorSqlError != default)
                        {
                            var returnLine = AgentMethodInventory.FindLastReturnLine(priorSqlError.old);
                            if (returnLine != null)
                            {
                                sb.AppendLine();
                                sb.AppendLine("  PREVIOUS ATTEMPT failed because you changed the SQL tables.");
                                sb.AppendLine("  Use oldString/newString anchored on the return statement to INSERT your");
                                sb.AppendLine("  new code BEFORE it, leaving the existing SQL untouched:");
                                sb.AppendLine($"  oldString: \"{returnLine.Trim()}\"");
                                sb.AppendLine($"  newString: \"<your new code here>");
                                sb.AppendLine($"{returnLine.Trim()}\"");
                            }
                            else
                            {
                                sb.AppendLine("  Do NOT copy the existing body — extend it with the required new behavior.");
                            }
                        }
                        else
                        {
                            sb.AppendLine("  Do NOT copy the existing body — extend it with the required new behavior.");
                        }
                    }
                }
                else if (h.error.Contains("WRONG SECTION", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("  ⚠ CRITICAL: You edited the WRONG SECTION of the HTML file.");
                    sb.AppendLine("  The file has multiple *ngIf sections (e.g., 'users', 'general', 'stories').");
                    sb.AppendLine("  The step description specifies WHICH section to edit — look for the section name");
                    sb.AppendLine("  in the step description and find the matching *ngIf directive in the file.");
                    sb.AppendLine();
                    sb.AppendLine("  The CORRECT SECTION CONTENT was shown in the error message above.");
                    sb.AppendLine("  Copy your oldString VERBATIM from that section — NOT from any other section.");
                    sb.AppendLine("  Do NOT edit a section just because it has similar structure to the target.");
                }
                else if (h.error.Contains("Method signature changed", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("  ⚠ Your new LOGIC was correct but you used the WRONG method signature.");
                    if (!string.IsNullOrWhiteSpace(h.@new))
                    {
                        sb.AppendLine("  Your code (the LOGIC is RIGHT — keep it):");
                        sb.AppendLine("  ```");
                        sb.AppendLine($"  {h.@new[..Math.Min(1000, h.@new.Length)]}");
                        sb.AppendLine("  ```");
                        sb.AppendLine("  Reuse this EXACT body. Change ONLY the method signature (first line) to match the original return type.");
                    }
                }
                else if (h.error.Contains("alreadyDone", StringComparison.OrdinalIgnoreCase) &&
                         h.error.Contains("missing keywords", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("  ⚠ CRITICAL: You returned {\"alreadyDone\": true} but the file does NOT contain the requested code.");
                    sb.AppendLine("  The CHANGE REQUIRED asks you to ADD new code that is not present in the file.");
                    sb.AppendLine("  Do NOT return alreadyDone — it will always be rejected because the method/endpoint does not exist yet.");
                    sb.AppendLine("  Do NOT return fullFile — it will also be rejected because it dumps the entire file.");
                    sb.AppendLine("  You MUST output the actual edit using FORMAT C with insertAfter:true — this is the ONLY accepted format.");
                    sb.AppendLine(BuildFormatCExamples(FormatCVariant.Insert));
                }
                else if (!string.IsNullOrWhiteSpace(h.old))
                {
                    sb.AppendLine($"  Your oldString was:");
                    sb.AppendLine($"  ```");
                    sb.AppendLine($"  {h.old[..Math.Min(400, h.old.Length)]}");
                    sb.AppendLine($"  ```");
                    var exactBlock = AnchorEditHeuristics.BuildExactMatchBlock(fileContent, h.old);
                    if (exactBlock != null)
                    {
                        sb.AppendLine($"  The EXACT lines from the file at the matched location (copy these VERBATIM for oldString):");
                        sb.AppendLine($"  ```");
                        sb.AppendLine($"  {exactBlock}");
                        sb.AppendLine($"  ```");
                    }
                    else
                    {
                        var hint = AnchorEditHeuristics.BuildExactMatchHint(fileContent, h.old);
                        if (hint != null)
                        {
                            sb.AppendLine($"  These lines in the file are SIMILAR to what you wrote:");
                            sb.AppendLine($"  {hint}");
                        }
                    }
                    // IDENTIFIER-GROUNDED RE-ANCHOR HINT: the oldString was NOT found verbatim
                    // (whitespace drift, extra/missing line). Show where the anchor's OWN
                    // identifier actually lives in the file so the model can copy the REAL
                    // lines — an "edit the edit" correction. Without this the model re-emits
                    // the same drifted anchor and burns the retry budget (the benchmark-22
                    // loop: the identical oldString 3× → abort).
                    if (h.error.Contains("not found verbatim", StringComparison.OrdinalIgnoreCase))
                    {
                        var groundedBlock = AnchorEditHeuristics.TryIdentifierAnchoredReanchor(
                            fileContent, h.old, 0)?.correctedBlock;
                        groundedBlock ??= AnchorEditHeuristics.FindIdentifierGroundedLines(fileContent, h.old);
                        if (!string.IsNullOrWhiteSpace(groundedBlock))
                        {
                            sb.AppendLine($"  Your oldString's anchor was NOT found verbatim (indentation/context differs). " +
                                          $"Copy THESE REAL lines from the file VERBATIM — with their real indentation — as your oldString:");
                            sb.AppendLine($"  ```");
                            sb.AppendLine($"  {groundedBlock}");
                            sb.AppendLine($"  ```");
                        }
                    }
                }
                else if (h.error.Contains("FORMAT C failed", StringComparison.OrdinalIgnoreCase) || h.error.Contains("not found in file", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("  You used FORMAT C but the symbol was not found. " +
                                  "This file has no named methods/classes for FORMAT C to target. " +
                                  "If this is NOT a .cs file, switch to oldString/newString: copy the EXACT lines from the file content, " +
                                  "verbatim including indentation, and set them as oldString. " +
                                  "If this IS a .cs file, the file may be empty or have no methods — use FORMAT C with a different targetName.");
                }
            }
            sb.AppendLine();
            if (hadTruncation)
            {
                sb.AppendLine("Previous response was too long and got truncated.");
                sb.AppendLine("If the file is .cs and you are adding a new method, use FORMAT C with insertAfter:true — it's compact.");
                sb.AppendLine("Otherwise, use <<<OLD>>> / <<<NEW>>> targeted edits — they are smaller and always fit.");
                sb.AppendLine("Do NOT use fullFile — it dumps the entire file which is too long.");
            }
            else
            {
                sb.AppendLine("COMMON FAILURES to avoid:");
                sb.AppendLine("- Did you ADD extra blank lines at the start or end of OLD? Trim them.");
                sb.AppendLine("- Did you ADD trailing spaces to lines in OLD? Trim trailing whitespace.");
                sb.AppendLine("- Did you change the indentation? Copy INDENTATION character-for-character from the file.");
                sb.AppendLine("- Did you write a shortened/paraphrased version? OLD must be a VERBATIM copy.");
                sb.AppendLine("- Is OLD too short (only 1 line)? Include 1-2 surrounding lines as ANCHOR context.");
                sb.AppendLine("- Look at the SIMILAR lines above — pick the closest one and copy it exactly.");
            }
        }
        sb.AppendLine();
        // ── Escalation directive — state machine replaces history.Count == 1/2/else ──
        if (history?.Count > 0)
        {
            var escalationLevel = EscalationStateMachine.Level(history.Count - 1);
            EscalationStateMachine.AppendEscalationDirective(
                sb, escalationLevel, editStrategy, ext,
                fileContent ?? "", step.Change ?? "", 0,
                cfg5.maxFullFileTokens * 4);
        }
        sb.AppendLine();
        // ── Final per-strategy instruction reminder ───────────────────────────────
        switch (editStrategy)
        {
            case EditStrategy.DeleteLines:
                sb.AppendLine("⚠ CRITICAL DELETION INSTRUCTION: You are deleting code. Your oldString MUST be EXACTLY the 1-5 lines of code being deleted. Set newString to an empty array []. Do NOT include the parent <div> or any surrounding lines in oldString. Output ONLY the exact lines to delete.");
                break;
            case EditStrategy.InsertMethod:
                sb.AppendLine("⚠ CRITICAL — NEW METHOD CREATION: You MUST use FORMAT C with insertAfter:true.");
                sb.AppendLine(BuildFormatCExamples(FormatCVariant.Insert));
                break;
            case EditStrategy.ReplaceMethod:
                sb.AppendLine("⚠ CRITICAL — ENTIRE METHOD/CLASS REPLACEMENT: You MUST use FORMAT C (REPLACE, NO insertAfter).");
                sb.AppendLine(BuildFormatCExamples(FormatCVariant.Replace));
                break;
        }

        if (ext is ".html" or ".htm" or ".cshtml" or ".razor" or ".vue" or ".svelte" && !string.IsNullOrWhiteSpace(fileContent))
        {
            var markers = Regex.Matches(fileContent, @"<!--\s*([^>]{3,80}?)\s*-->|<div[^>]*groupDomainTitle[^>]*>\s*([^<]+?)\s*</div>");
            if (markers.Count > 0)
            {
                sb.AppendLine("\n📐 HTML STRUCTURE MARKERS (use these as anchors):");
                foreach (Match m in markers)
                {
                    var lineNum = fileContent[..m.Index].Count(c => c == '\n') + 1;
                    var label = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    sb.AppendLine($"  Line {lineNum}: {label.Trim()}");
                }
                sb.AppendLine();
            }
        }
        if (HtmlDomEditor.IsHtmlDomFile(relPath))
        {
            sb.AppendLine("⚠ HTML FILE — Use FORMAT D: targetType=\"html\", targetName=CODE BLOCK, " +
                          "insertAfter=true/false, newCode=[your HTML lines]. " +
                          "⚠ ONE THING PER EDIT — do NOT bundle multiple changes into one edit. " +
                          "Each edit does ONE replacement OR ONE insertion. " +
                          "⚠ TARGETED REPLACE (default for small changes): targetName = the ONE unique line being " +
                          "changed, newCode = ONLY the replacement for that line (may be several lines). Do NOT " +
                          "reproduce the whole enclosing section — the unchanged lines stay put automatically. " +
                          "CRITICAL: targetName is a CODE BLOCK copied verbatim from the file. " +
                          "For insertAfter: use a SINGLE LINE as targetName. " +
                          "For replace: prefer a SINGLE unique line as targetName; multi-line is OK ONLY when the " +
                          "change rewrites a whole block. " +
                          "Copy the exact lines you want to replace or insert after. " +
                          "For insertAfter: the targetName block is found, and newCode is inserted AFTER that block. " +
                          "newCode in insertAfter mode should contain ONLY the new HTML. " +
                          "For replace: ONLY the targetName block is replaced with newCode (unchanged siblings/parents " +
                          "stay); newCode MUST NOT be empty.");
        }
        sb.AppendLine();
        // ── Pattern reference files: load the content the step says to mirror ──
        var referencePaths = new List<string>();
        if (step.ReferenceFiles != null)
            referencePaths.AddRange(step.ReferenceFiles);
        if (!string.IsNullOrWhiteSpace(step.Change))
        {
            foreach (Match rm in Regex.Matches(step.Change,
                @"([\w./\\-]+\.(?:ts|js|html|cs|css|py|java|go|cshtml|razor|vue))\b",
                RegexOptions.IgnoreCase))
            {
                var candidate = rm.Groups[1].Value.Replace('\\', '/');
                if (!referencePaths.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    referencePaths.Add(candidate);
            }
        }
        var refSb = new StringBuilder();
        foreach (var refPath in referencePaths)
        {
            var refRel = refPath.Replace('\\', '/');
            var refFull = Path.GetFullPath(Path.Combine(projectRoot, refRel.Replace('/', Path.DirectorySeparatorChar)));
            if (!AgentProjectUtilities.IsPathUnderRoot(refFull, projectRoot)) continue;
            if (!System.IO.File.Exists(refFull))
            {
                // Bare filename (e.g. "music.component.ts") mentioned in the change text
                // may live deeper in the tree — search project-wide for a same-named file.
                // Only accept an EXACT basename match; never fall back to an unrelated fuzzy hit.
                var similar = AgentDiscovery.FindSimilarFiles(refRel, projectRoot)
                    .FirstOrDefault(f => Path.GetFileName(f).Equals(Path.GetFileName(refRel), StringComparison.OrdinalIgnoreCase));
                if (similar == null) continue;
                refFull = Path.GetFullPath(similar);
                if (!AgentProjectUtilities.IsPathUnderRoot(refFull, projectRoot)) continue;
            }
            string refContent;
            try { refContent = await System.IO.File.ReadAllTextAsync(refFull, Encoding.UTF8, ct); }
            catch { continue; }
            if (string.IsNullOrWhiteSpace(refContent)) continue;
            if (refContent.Length > 20000) refContent = refContent[..20000];
            refSb.AppendLine();
            refSb.AppendLine($"### PATTERN REFERENCE FILE: {refRel} (replicate the relevant pattern from this file) ###");
            refSb.AppendLine("```");
            refSb.AppendLine(refContent);
            refSb.AppendLine("```");
        }
        if (refSb.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("⚠ PATTERN REFERENCE FILES — the change description references these files as the pattern to follow (e.g. 'like music.component.ts'). Study them and replicate the SAME structure and naming (methods, properties like isPopupPanelOpen, toggle patterns). Do NOT write placeholder stubs.");
            sb.AppendLine(refSb.ToString());
        }
        sb.AppendLine();
        sb.AppendLine("Output the edit now:");
        if (emitSse) { await SendSse(Response, "edit-resolve", new { }, ct); }
        // editStrategy was classified at the top of this method (after ext is known).
        // Build system prompt from it and fire the LLM call.
        var systemPrompt = BuildEditSystemPrompt(editStrategy);
        var (raw, _, resolveError2) = await CallLlmRawStreaming(systemPrompt, sb.ToString(), emitSse, ct, _infiniteTimeout, maxTokens: 4096);
        if (!string.IsNullOrWhiteSpace(resolveError2) && resolveError2.Contains("Repetition loop detected", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, false, null, false, resolveError2, false);
        }
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, null, false, null, false, "LLM returned empty response", false);
        }
        string? oldStr = null, newStr = null;
        try
        {
            var rawTrimmed = raw.Trim();
            if (rawTrimmed.StartsWith("```"))
            {
                var m = Regex.Match(rawTrimmed, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (m.Success) rawTrimmed = m.Groups[1].Value.Trim();
            }
            var jsonCandidates = ExtractAllJsonObjects(rawTrimmed);
            string? cleaned = null;
            JsonDocument? jDoc = null;
            var htmlCandidates = new List<JsonDocument>();
            foreach (var candidate in jsonCandidates)
            {
                var c = RepairJsonNewlines(candidate);
                c = Regex.Replace(c, @"""\s*\+\s*""", "");
                try
                {
                    var doc = JsonDocument.Parse(c);
                    if (doc.RootElement.TryGetProperty("targetType", out var candTt) &&
                        string.Equals(candTt.GetString(), "html", StringComparison.OrdinalIgnoreCase))
                    {
                        // The LLM sometimes emits several FORMAT D payloads in one response;
                        // collect them all so the html branch can try each until one resolves.
                        htmlCandidates.Add(doc);
                        // Only promote to jDoc (which gates the FORMAT D branch) a candidate that
                        // actually carries targetName + newCode — a malformed first payload must
                        // not block later valid ones from being tried.
                        if (jDoc == null &&
                            doc.RootElement.TryGetProperty("targetName", out _) &&
                            doc.RootElement.TryGetProperty("newCode", out _))
                        {
                            jDoc = doc;
                            cleaned = c;
                        }
                        continue;
                    }
                    if (doc.RootElement.TryGetProperty("targetType", out _) ||
                        doc.RootElement.TryGetProperty("oldString", out _) ||
                        doc.RootElement.TryGetProperty("fullFile", out _) ||
                        doc.RootElement.TryGetProperty("alreadyDone", out _) ||
                        (doc.RootElement.TryGetProperty("format", out var fmtEl) &&
                         string.Equals(fmtEl.GetString(), "method", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (jDoc == null)
                        {
                            jDoc = doc;
                            cleaned = c;
                        }
                        break;
                    }
                }
                catch { }
            }
            if (jDoc == null)
            {
                cleaned = ExtractFirstJsonObject(rawTrimmed);
                cleaned = RepairJsonNewlines(cleaned);
                cleaned = Regex.Replace(cleaned, @"""\s*\+\s*""", "");
                jDoc = JsonDocument.Parse(cleaned);
            }
            var jRoot = jDoc.RootElement;
            if (jRoot.TryGetProperty("alreadyDone", out var ad) && ad.GetBoolean())
            {
                var (verdict, _) = PreEditValidation(fileContent ?? "", step);
                if (verdict == PreEditVerdict.AlreadyDone)
                {
                    return (null, null, false, null, true, null, false);
                }
                var contentLower = (fileContent ?? "").ToLowerInvariant();
                var stopWords = new HashSet<string> {
                    "the", "and", "for", "with", "that", "this", "from", "into", "file",
                    "method", "function", "code", "step", "create", "modify", "update",
                    "change", "add", "remove", "delete", "implement", "ensure", "make",
                    "user", "their", "your", "will", "should", "must", "have", "been",
                    "which", "where", "when", "then", "them", "they", "were", "what",
                    "have", "has", "had", "does", "doing", "wants", "want"
                };
                var keywords = Regex.Matches((step.Change ?? "").ToLowerInvariant(), @"\b[a-z]{4,}\b")
                    .Select(m => m.Value)
                    .Where(w => !stopWords.Contains(w))
                    .Distinct()
                    .Take(4)
                    .ToList();
                var missingKeywords = keywords.Where(k => !contentLower.Contains(k)).ToList();
                if (missingKeywords.Count > 0)
                {
                    return (null, null, false, null, false,
                        $"LLM returned {{\"alreadyDone\": true}} but file content is missing keywords: [{string.Join(", ", missingKeywords)}]. " +
                        "Do NOT claim alreadyDone if the requested functionality is missing from the CURRENT FILE CONTENT above. " +
                        "Output the actual edit instead.", false);
                }
                return (null, null, false, null, true, null, false);
            }
            if (ext == ".cs" && editStrategy == EditStrategy.InsertMethod &&
                !jRoot.TryGetProperty("targetType", out _))
            {
                var hasFullFile = jRoot.TryGetProperty("fullFile", out _);
                return (null, null, false, null, false,
                    "C# NEW METHOD ENFORCEMENT: You MUST use FORMAT C (targetType/targetName/insertAfter) " +
                    "to add a new method in a .cs file. " +
                    (hasFullFile
                        ? "fullFile is also NOT allowed — it dumps the entire file which is too long and bypasses AST insertion."
                        : "oldString/newString is NOT allowed for C# method insertion. ") +
                    "Set targetType=\"method\", targetName to an EXISTING method name (e.g. the last method in the class), " +
                    "insertAfter=true, and newCode to the COMPLETE new method including attributes, signature, and body. " +
                    "Do NOT return alreadyDone or fullFile — ONLY FORMAT C with insertAfter:true will be accepted.", false);
            }
            if (jRoot.TryGetProperty("fullFile", out var ffVal))
            {
                var isNewCsMethod = editStrategy == EditStrategy.InsertMethod;
                if (isNewCsMethod)
                {
                    return (null, null, false, null, false,
                        "C# NEW METHOD ENFORCEMENT: fullFile is NOT allowed for adding methods in .cs. " +
                        "You MUST use FORMAT C with insertAfter:true. " +
                        "Set targetType=\"method\", targetName to an EXISTING method name (e.g. the last method in the class), " +
                        "insertAfter=true, and newCode to the COMPLETE new method including attributes, signature, and body. " +
                        "Do NOT return alreadyDone either — ONLY FORMAT C with insertAfter:true will be accepted.", false);
                }
                string? body = null;
                if (ffVal.ValueKind == JsonValueKind.String)
                    body = ffVal.GetString();
                else if (ffVal.ValueKind == JsonValueKind.Array)
                {
                    var lines = new List<string>();
                    foreach (var item in ffVal.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                            lines.Add(AgentTextUtilities.UnescapeString(item.GetString() ?? ""));
                    }
                    if (lines.Count > 0) body = string.Join("\n", lines);
                }
                if (!string.IsNullOrWhiteSpace(body))
                {
                    body = AgentTextUtilities.StripFullFileFence(body);
                    body = AgentCodeFormatting.AutoFixPythonStatements(body, relPath);
                    body = AgentTextUtilities.CleanVerbatimStringEscapes(body);
                    if (!IsFullFileAllowed(fileExists, fileContent.Length))
                    {
                        return (null, null, false, null, false,
                            "FULL_FILE_REJECTED: this existing file is not small enough for a full-file replacement. " +
                            "Use a targeted oldString/newString edit for the specific lines being changed, or FORMAT C with targetType/targetName/newCode only when replacing an entire method.", false);
                    }
                    return (null, null, true, body, false, null, false);
                }
            }
            var hasTargetType = jRoot.TryGetProperty("targetType", out var ttEl);
            var hasTargetName = jRoot.TryGetProperty("targetName", out var tnEl);
            var hasFmtNewCode = jRoot.TryGetProperty("newCode", out var ncEl);
            var hasInsertAfter = jRoot.TryGetProperty("insertAfter", out var iaEl);
            var insertAfter = hasInsertAfter && iaEl.GetBoolean();
            if ((hasTargetType && hasTargetName && hasFmtNewCode) ||
                (!hasTargetType && hasTargetName && hasFmtNewCode && insertAfter && System.IO.File.Exists(fullPath)))
            {
                var targetType = hasTargetType ? ttEl.GetString() : "code";
                var targetName = tnEl.GetString();
                var newCodeStr = ncEl.ValueKind == JsonValueKind.String
                        ? AgentTextUtilities.UnescapeString(ncEl.GetString() ?? "")
                    : ncEl.ValueKind == JsonValueKind.Array
                        ? string.Join("\n", ncEl.EnumerateArray().Select(e => AgentTextUtilities.UnescapeString(e.GetString() ?? "")))
                        : null;
                if (!string.IsNullOrWhiteSpace(targetName) && newCodeStr != null && (hasTargetType ? !string.IsNullOrWhiteSpace(targetType) : true))
                {
                    newCodeStr = AgentCodeFormatting.AutoFixPythonStatements(newCodeStr, relPath);
                    newCodeStr = AgentTextUtilities.CleanVerbatimStringEscapes(newCodeStr);

                    var hasReplace = jRoot.TryGetProperty("replace", out var rpEl);
                    var replaceSection = hasReplace && rpEl.GetBoolean();
                    if (string.Equals(targetType, "html", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!System.IO.File.Exists(fullPath))
                            return (null, null, false, null, false, $"FORMAT D failed: file not found '{relPath}'", false);
                        var sourceText = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                        // Try EVERY emitted FORMAT D payload in order — the first one may have a
                        // hallucinated targetName while a later payload is byte-exact.
                        var docsToTry = htmlCandidates.Count > 0
                            ? htmlCandidates
                            : new List<JsonDocument> { jDoc };
                        string? lastErr = null;
                        foreach (var candDoc in docsToTry)
                        {
                            var candRoot = candDoc.RootElement;
                            var candTargetName = candRoot.TryGetProperty("targetName", out var cTnEl) ? cTnEl.GetString() : null;
                            string? candNewCodeStr = null;
                            if (candRoot.TryGetProperty("newCode", out var cNcEl))
                            {
                                candNewCodeStr = cNcEl.ValueKind == JsonValueKind.String
                                    ? AgentTextUtilities.UnescapeString(cNcEl.GetString() ?? "")
                                    : cNcEl.ValueKind == JsonValueKind.Array
                                        ? string.Join("\n", cNcEl.EnumerateArray().Select(e => AgentTextUtilities.UnescapeString(e.GetString() ?? "")))
                                        : null;
                            }
                            if (string.IsNullOrWhiteSpace(candTargetName))
                                continue;
                            var candHasReplace = candRoot.TryGetProperty("replace", out var cRpEl);
                            var candReplaceSection = candHasReplace && cRpEl.GetBoolean();
                            var candHasInsertAfter = candRoot.TryGetProperty("insertAfter", out var cIaEl);
                            var candInsertAfter = candHasInsertAfter && cIaEl.GetBoolean();
                            var candWantsReplace = candReplaceSection ||
                                (candHasInsertAfter && !candInsertAfter && !candHasReplace) ||
                                (!candHasInsertAfter && !candHasReplace);
                            // DELETION INTENT: replace with an EMPTY newCode means "remove the matched
                            // block". Previously this candidate was skipped BEFORE ResolveHtmlAnchor ran,
                            // so a deletion payload ("replace":true, "newCode":[]) reported
                            // "targetName block not found" even when the block existed verbatim — the
                            // exact-match/normalized/fuzzy chain never got a chance. Resolve the anchor
                            // first and return a deletion edit (old=block, new="").
                            if (string.IsNullOrWhiteSpace(candNewCodeStr) && candWantsReplace)
                            {
                                var (delBlock, _, delErr) = HtmlDomEditor.ResolveHtmlAnchor(
                                    sourceText, candTargetName, step.Change, step.LineNumber,
                                    expandToClosingTags: false, true);
                                if (delBlock == null)
                                {
                                    // Anchor failed to resolve: distinguish "already removed" from
                                    // a hallucinated/drifted targetName. Only declare AlreadyDone when
                                    // the FULL target block is genuinely absent — a surviving fragment
                                    // is NOT evidence of a completed removal.
                                    var (delDone, delReason) = FormatDAlreadyDoneVerdict(sourceText, candTargetName, candNewCodeStr);
                                    if (delDone)
                                    {
                                        await EmitLog(emitSse, "info", $"✓ Already done: {relPath} — {delReason}", ct: ct);
                                        return (null, null, false, null, true, null, false);
                                    }
                                    lastErr = delErr;
                                    continue;
                                }
                                await EmitLog(emitSse, "info",
                                    $"🗑 FORMAT D deletion: empty newCode + replace intent resolved anchor in {relPath} — removing matched block", ct: ct);
                                return (delBlock, "", false, null, false, null, true);
                            }
                            if (string.IsNullOrWhiteSpace(candNewCodeStr))
                            {
                                lastErr = "FORMAT D failed: newCode is empty. You MUST provide the replacement HTML for the targetName block in newCode, not an empty array.";
                                continue;
                            }
                            var candRawNewCode = candNewCodeStr;
                            candNewCodeStr = HtmlDomEditor.StripLeadingClosingDivs(candNewCodeStr, candTargetName);
                            if (candNewCodeStr != candRawNewCode)
                            {
                                await EmitLog(emitSse, "warn",
                                    $"Stripped leading </div> lines from newCode for {relPath}", ct: ct);
                            }
                            if (string.IsNullOrWhiteSpace(candNewCodeStr))
                            {
                                lastErr = "FORMAT D failed: newCode is empty. You MUST provide the replacement HTML for the targetName block in newCode, not an empty array.";
                                continue;
                            }
                            if (!candNewCodeStr.Contains('<', StringComparison.Ordinal))
                            {
                                lastErr = "FORMAT D failed: newCode is incomplete (only closing tags). Generate the full HTML to insert.";
                                continue;
                            }
                            var (candDone, candDoneReason) = FormatDAlreadyDoneVerdict(sourceText, candTargetName, candNewCodeStr);
                            if (candDone)
                            {
                                await EmitLog(emitSse, "info", $"✓ Already done: {relPath} — {candDoneReason}", ct: ct);
                                return (null, null, false, null, true, null, false);
                            }
                            var (matchedBlock, _, htmlErr) = HtmlDomEditor.ResolveHtmlAnchor(sourceText, candTargetName, step.Change, step.LineNumber, expandToClosingTags: false, true);
                            if (matchedBlock == null)
                            {
                                lastErr = htmlErr;
                                continue;
                            }
                            if (candReplaceSection || (candHasInsertAfter && !candInsertAfter && !candHasReplace))
                            {
                                var indented = await FormatSnippetAsync(matchedBlock, candNewCodeStr, relPath);
                                return (matchedBlock, indented, false, null, false, null, true);
                            }
                            if (candInsertAfter || (candHasReplace && !candReplaceSection && !candHasInsertAfter))
                            {
                                var indented = await FormatSnippetAsync(matchedBlock, candNewCodeStr, relPath);
                                return (matchedBlock, matchedBlock + "\n" + indented, false, null, false, null, true);
                            }
                            return (matchedBlock, candNewCodeStr + "\n" + matchedBlock, false, null, false, null, true);
                        }
                        await EmitLog(emitSse, "warn",
                            $"FORMAT D: targetName block not found — {lastErr ?? "no candidates"}", ct: ct);
                        return (null, null, false, null, false,
                            $"FORMAT D failed: targetName block not found in {relPath}. " +
                            $"Copy the exact code block from the file as targetName. " +
                            "For a small change, use TARGETED REPLACE: targetName = the SINGLE unique line being " +
                            "changed (verbatim), newCode = only the replacement for that line — do NOT reproduce " +
                            "the whole section.", false);
                    }
                    if (insertAfter && System.IO.File.Exists(fullPath))
                    {
                        var sourceText = System.IO.File.ReadAllText(fullPath, Encoding.UTF8);
                        var (fullStr, astErr) = AstResolveEdit(fullPath, targetType!, targetName, returnTail: false);
                        if (fullStr != null)
                        {
                            var searchText = sourceText.Contains("\r\n")
                                ? sourceText.Replace("\r\n", "\n")
                                : sourceText;
                            var idx = searchText.IndexOf(fullStr, StringComparison.Ordinal);
                            if (idx >= 0)
                            {
                                var tsExt = ext is ".ts" or ".tsx" or ".js" or ".jsx";
                                if (tsExt && string.Equals(targetType, "method", StringComparison.OrdinalIgnoreCase))
                                {
                                    var declPattern = $@"(?:async\s+)?(?:private\s+|public\s+|protected\s+)?(?:static\s+)?{Regex.Escape(targetName)}\s*\(";
                                    if (Regex.IsMatch(newCodeStr, declPattern))
                                    {
                                        var replaced = await FormatSnippetAsync(fullStr, newCodeStr, relPath);
                                        return (fullStr, replaced, false, null, false, null, true);
                                    }
                                }
                                var indented = await FormatSnippetAsync(fullStr, newCodeStr, relPath);
                                var prefix = sourceText[..(idx + fullStr.Length)];
                                newStr = prefix + "\n\n" + indented;
                                return (prefix, newStr, false, null, false, null, true);
                            }
                        }
                        var idx2 = sourceText.IndexOf(targetName, StringComparison.Ordinal);
                        if (idx2 < 0)
                            idx2 = sourceText.IndexOf(targetName, StringComparison.OrdinalIgnoreCase);
                        if (idx2 >= 0)
                        {
                            var lineStart = sourceText.LastIndexOf('\n', idx2) + 1;
                            var lineEnd = sourceText.IndexOf('\n', idx2);
                            if (lineEnd < 0) lineEnd = sourceText.Length;
                            if (lineEnd > 0 && sourceText[lineEnd - 1] == '\r') lineEnd--;
                            var fullLine = sourceText[lineStart..lineEnd];
                            var indented = await FormatSnippetAsync(fullLine, newCodeStr, relPath);
                            var prefix = sourceText[..lineEnd];
                            newStr = prefix + "\n" + indented;
                            return (prefix, newStr, false, null, false, null, true);
                        }
                    }
                    if (insertAfter)
                    {
                        var (fullStr, astErr) = AstResolveEdit(fullPath, targetType!, targetName, returnTail: false);
                        if (fullStr == null &&
                            string.Equals(targetType, "method", StringComparison.OrdinalIgnoreCase) &&
                            System.IO.File.Exists(fullPath))
                        {
                            var sourceText = System.IO.File.ReadAllText(fullPath, Encoding.UTF8);
                            var methodMatches = Regex.Matches(sourceText,
                                @"(?:(?:public|private|protected|internal)\s+)?(?:(?:static|virtual|override|abstract|sealed|new|partial|async|unsafe)\s+)*(?:\w+(?:\[\])?(?:<[^>]*>)?)\s+(\w+)\s*\(");
                            if (methodMatches.Count > 0)
                            {
                                var lastMethod = methodMatches[^1];
                                var lastMethodName = lastMethod.Groups[1].Value;
                                (fullStr, astErr) = AstResolveEdit(fullPath, targetType!, lastMethodName, returnTail: false);
                                if (fullStr != null)
                                {
                                    targetName = lastMethodName;
                                    await EmitLog(emitSse, "info",
                                        $"  🎯 FORMAT C insertAfter: '{targetName}' not found, auto-resolved to last method '{lastMethodName}'", ct: ct);
                                }
                            }
                        }
                        if (fullStr == null)
                            return (null, null, false, null, false,
                                $"FORMAT C failed: targetType='{targetType}', targetName='{targetName}' — {astErr ?? "symbol not found in file"}. " +
                                "When using insertAfter:true, targetName MUST be an EXISTING method name found in the file. " +
                                "Do NOT use the new method's name as targetName.", false);
                        if (string.Equals(targetType, "class", StringComparison.OrdinalIgnoreCase))
                        {
                            var unit = new string(' ', AgentMethodInventory.DetectIndentWidth(fullStr));
                            var memberIndent = unit + unit;
                            var hasClassDecl = newCodeStr.Contains("class ", StringComparison.OrdinalIgnoreCase);
                            var body = hasClassDecl ? AgentTextUtilities.StripClassWrapper(newCodeStr) : newCodeStr;
                            var bodyLines = body.Split('\n');
                            var nonEmpty = bodyLines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                            var minBodyIndent = nonEmpty.Count > 0
                                ? nonEmpty.Min(l => Regex.Match(l, @"^(\s*)").Groups[1].Length)
                                : 0;
                            var indentedBodySb = new StringBuilder();
                            foreach (var line in bodyLines)
                            {
                                if (string.IsNullOrWhiteSpace(line))
                                {
                                    indentedBodySb.AppendLine();
                                }
                                else
                                {
                                    var trimmed = line.Length > minBodyIndent
                                        ? line.Substring(minBodyIndent)
                                        : line.TrimStart();
                                    indentedBodySb.Append(memberIndent).AppendLine(trimmed);
                                }
                            }
                            var bodyIndented = indentedBodySb.ToString().TrimEnd('\n', '\r');
                            var lastBrace = fullStr.LastIndexOf('}');
                            if (lastBrace >= 0)
                            {
                                newStr = fullStr[..lastBrace].TrimEnd() + "\n\n" + bodyIndented + "\n" + fullStr[lastBrace..];
                                return (fullStr, newStr, false, null, false, null, true);
                            }
                        }
                        var indented = await FormatSnippetAsync(fullStr, newCodeStr, relPath);
                        newStr = fullStr + "\n" + indented;
                        return (fullStr, newStr, false, null, false, null, true);
                    }
                    else
                    {
                        var addMethodMatch = Regex.Match(step.Change ?? "", @"Add\s+(?:a\s+)?(?:new\s+)?method\s+(\w+)", RegexOptions.IgnoreCase);
                        if (!addMethodMatch.Success)
                            addMethodMatch = Regex.Match(step.Change ?? "", @"(?:Add|Create|Implement|Insert|Define)\s+(?:a\s+)?(?:new\s+)?(\w+)\s+method\b", RegexOptions.IgnoreCase);
                        if (!addMethodMatch.Success)
                            addMethodMatch = Regex.Match(step.Change ?? "", @"(?:Add|Create|Implement|Insert|Define)\s+(?:a\s+)?(?:new\s+)?(\w+)\s*\(\s*\)", RegexOptions.IgnoreCase);
                        if (addMethodMatch.Success)
                        {
                            var requestedMethodName = addMethodMatch.Groups[1].Value;
                            if (!string.IsNullOrWhiteSpace(requestedMethodName) &&
                                requestedMethodName.Length > 2 &&
                                !string.Equals(requestedMethodName, "method", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(requestedMethodName, "new", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(targetName, requestedMethodName, StringComparison.OrdinalIgnoreCase) &&
                                !newCodeStr.Contains(requestedMethodName, StringComparison.OrdinalIgnoreCase))
                            {
                                return (null, null, false, null, false,
                                    $"WRONG METHOD — Step asks to add '{requestedMethodName}' but your newCode does NOT contain that name. " +
                                    $"You produced code for '{targetName}' instead. " +
                                    "Re-read CHANGE REQUIRED. The newCode MUST contain the method named in the step description. " +
                                    "Use insertAfter:true with an EXISTING method as targetName, and provide ONLY the new method in newCode.", false);
                            }
                        }
                        var (astOldStr, astErr) = AstResolveEdit(fullPath, targetType!, targetName, returnTail: false);
                        if (astOldStr != null)
                        {
                            var isClassTarget = string.Equals(targetType, "class", StringComparison.OrdinalIgnoreCase);
                            var hasClassDecl = newCodeStr.Contains("class ", StringComparison.OrdinalIgnoreCase);
                            if (isClassTarget && !hasClassDecl)
                            {
                                var lastBrace = astOldStr.LastIndexOf('}');
                                if (lastBrace >= 0)
                                {
                                    var unit = new string(' ', AgentMethodInventory.DetectIndentWidth(astOldStr));
                                    var methodIndent = unit + unit;
                                    var lines = newCodeStr.Split('\n');
                                    var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                                    var minIndent = nonEmpty.Count > 0
                                        ? nonEmpty.Min(l => Regex.Match(l, @"^(\s*)").Groups[1].Length)
                                        : 0;
                                    var indentedSb = new StringBuilder();
                                    foreach (var line in lines)
                                    {
                                        if (string.IsNullOrWhiteSpace(line))
                                        {
                                            indentedSb.AppendLine();
                                        }
                                        else
                                        {
                                            var trimmed = line.Length > minIndent
                                                ? line.Substring(minIndent)
                                                : line.TrimStart();
                                            indentedSb.Append(methodIndent).AppendLine(trimmed);
                                        }
                                    }
                                    var indentedNewCode = indentedSb.ToString().TrimEnd('\n', '\r');
                                    var mergedStr = astOldStr[..lastBrace].TrimEnd() + "\n\n" + indentedNewCode + "\n" + astOldStr[lastBrace..];
                                    return (astOldStr, mergedStr, false, null, false, null, true);
                                }
                            }
                            if (isClassTarget)
                            {
                                if (!string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase))
                                {
                                    return (null, null, false, null, false,
                                        $"targetType='class' REPLACE is not allowed for {ext} files — " +
                                        "it risks member duplication and truncation. " +
                                        "To ADD a method: use insertAfter:true with targetType='method' and an existing method name. " +
                                        "To ADD a property/field: use oldString/newString — set oldString to the last 1-2 lines " +
                                        "before the closing brace (e.g. the isMenuPanelOpen declaration), " +
                                        "and newString to those same lines followed by the new property.", false);
                                }
                                var body = hasClassDecl ? AgentTextUtilities.StripClassWrapper(newCodeStr) : newCodeStr;
                                if (!string.IsNullOrWhiteSpace(body))
                                {
                                    var unit = new string(' ', AgentMethodInventory.DetectIndentWidth(astOldStr));
                                    var bodyIndented = AgentCodeFormatting.ReindentToLevel(body, unit);
                                    var lastBrace = astOldStr.LastIndexOf('}');
                                    var openBrace = astOldStr.IndexOf('{');
                                    if (lastBrace >= 0 && openBrace >= 0 && openBrace < lastBrace)
                                    {
                                        var classHeader = astOldStr[..(openBrace + 1)];
                                        var mergedStr = classHeader.TrimEnd() + "\n" + bodyIndented.TrimEnd() + "\n" + astOldStr[lastBrace..];
                                        return (astOldStr, mergedStr, false, null, false, null, true);
                                    }
                                }
                            }
                            if (string.Equals(targetType, "method", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(targetType, "function", StringComparison.OrdinalIgnoreCase))
                            {
                                var isTypeScript = string.Equals(ext, ".ts", StringComparison.OrdinalIgnoreCase) ||
                                                   string.Equals(ext, ".tsx", StringComparison.OrdinalIgnoreCase) ||
                                                   string.Equals(ext, ".js", StringComparison.OrdinalIgnoreCase) ||
                                                   string.Equals(ext, ".jsx", StringComparison.OrdinalIgnoreCase);
                                Match? newMethodMatch = null;
                                if (!isTypeScript)
                                    newMethodMatch = MethodDeclRegex.Match(newCodeStr);
                                if (newMethodMatch?.Success == true)
                                {
                                    var newMethodName = newMethodMatch.Groups[1].Value;
                                    if (!string.IsNullOrWhiteSpace(newMethodName) &&
                                        !string.Equals(newMethodName, targetName, StringComparison.Ordinal))
                                    {
                                        if (!insertAfter)
                                        {
                                            return (null, null, false, null, false,
                                                $"METHOD NAME MISMATCH — targetName is '{targetName}' but newCode declares '{newMethodName}'. " +
                                                $"To replace '{targetName}', newCode MUST declare the SAME method name. " +
                                                $"To ADD '{newMethodName}' as a new method, use insertAfter:true.", false);
                                        }
                                        var oldFirstRealLine = astOldStr.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                                        var methodBaseIndent = oldFirstRealLine != null
                                            ? Regex.Match(oldFirstRealLine, @"^(\s*)").Groups[1].Value
                                            : "";
                                        if (string.Equals(ext, ".py", StringComparison.OrdinalIgnoreCase))
                                        {
                                            newStr = astOldStr + "\n\n" + AgentCodeFormatting.ReindentPythonBlock(newCodeStr, methodBaseIndent);
                                            return (astOldStr, newStr, false, null, false, null, true);
                                        }
                                        var lines = newCodeStr.Split('\n');
                                        var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                                        var minIndent = nonEmpty.Count > 0
                                            ? nonEmpty.Min(l => Regex.Match(l, @"^(\s*)").Groups[1].Length)
                                            : 0;
                                        var indentedSb = new StringBuilder();
                                        foreach (var line in lines)
                                        {
                                            if (string.IsNullOrWhiteSpace(line))
                                            {
                                                indentedSb.AppendLine();
                                            }
                                            else
                                            {
                                                var trimmed = line.Length > minIndent
                                                    ? line.Substring(minIndent)
                                                    : line.TrimStart();
                                                indentedSb.Append(methodBaseIndent).AppendLine(trimmed);
                                            }
                                        }
                                        var indentedNew = indentedSb.ToString().TrimEnd('\n', '\r');
                                        newStr = astOldStr + "\n\n" + indentedNew;
                                        return (astOldStr, newStr, false, null, false, null, true);
                                    }
                                }
                                if (newMethodMatch?.Success != true)
                                {
                                    var hasOwnSignature = newCodeStr.TrimStart().StartsWith(targetName + "(", StringComparison.Ordinal) ||
                                                          newCodeStr.Contains("\n" + targetName + "(", StringComparison.Ordinal) ||
                                                          newCodeStr.Contains("\n" + targetName + "<", StringComparison.Ordinal);
                                    if (!hasOwnSignature)
                                    {
                                        var openBracePos = astOldStr.IndexOf('{');
                                        var closeBracePos = astOldStr.LastIndexOf('}');
                                        if (openBracePos > 0 && closeBracePos > openBracePos)
                                        {
                                            var signature = astOldStr[..openBracePos].TrimEnd();
                                            var oldBody = astOldStr[(openBracePos + 1)..closeBracePos];
                                            var bodyIndent = "";
                                            foreach (var line in oldBody.Split('\n'))
                                                if (!string.IsNullOrWhiteSpace(line)) { bodyIndent = Regex.Match(line, @"^(\s*)").Value; break; }
                                            if (string.IsNullOrEmpty(bodyIndent))
                                            {
                                                var w = AgentMethodInventory.DetectIndentWidth(astOldStr);
                                                if (w <= 0) w = 2;
                                                bodyIndent = new string(' ', w) + new string(' ', w);
                                            }
                                            var reindented = await FormatSnippetAsync(bodyIndent + "x", newCodeStr.TrimStart(), relPath, explicitBaseIndent: bodyIndent);
                                            var closingIndent = Regex.Match(signature, @"^(\s*)").Value;
                                            newCodeStr = signature + " {\n" + reindented + "\n" + closingIndent + "}";
                                        }
                                    }
                                }
                            }
                            var fmtNewCode = newCodeStr;
                            if (string.Equals(Path.GetExtension(relPath), ".cs", StringComparison.OrdinalIgnoreCase)
                                && !fmtNewCode.Contains("@\"", StringComparison.Ordinal)   // ← never normalize verbatim strings
                                && !fmtNewCode.Contains("/*", StringComparison.Ordinal)    // ← never strip block comments
                                && !fmtNewCode.Contains("///", StringComparison.Ordinal))  // ← never strip XML doc comments
                            {
                                try
                                {
                                    var fmtTree = CSharpSyntaxTree.ParseText(fmtNewCode);
                                    fmtNewCode = fmtTree.GetRoot().NormalizeWhitespace().ToFullString();
                                }
                                catch { }
                            }
                            var indented = await FormatSnippetAsync(astOldStr, fmtNewCode, relPath);
                            return (astOldStr, indented, false, null, false, null, true);
                        }
                        return (null, null, false, null, false, $"FORMAT C failed: targetType='{targetType}', targetName='{targetName}' — {astErr ?? "symbol not found in file"}", false);
                    }
                }
            }
            {
                string? ResolveString(JsonElement el)
                {
                    if (el.ValueKind == JsonValueKind.String)
                        return el.GetString();
                    if (el.ValueKind == JsonValueKind.Array)
                    {
                        var lines = new List<string>();
                        foreach (var item in el.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                                lines.Add(item.GetString() ?? "");
                        }
                        return lines.Count > 0 ? string.Join("\n", lines) : null;
                    }
                    return null;
                }
                oldStr = jRoot.TryGetProperty("oldString", out var osEl) ? ResolveString(osEl) : null;
                newStr = jRoot.TryGetProperty("newString", out var nsEl) ? ResolveString(nsEl) : null;
                if (!string.IsNullOrWhiteSpace(forcedOldString))
                    oldStr = forcedOldString;
                if (!string.IsNullOrWhiteSpace(oldStr))
                    oldStr = FixAngularAttributeCasing(oldStr);
                if (!string.IsNullOrWhiteSpace(newStr))
                    newStr = FixAngularAttributeCasing(newStr);
                if (!string.IsNullOrWhiteSpace(newStr))
                {
                    var cleanedNewStr = AgentTextUtilities.CleanVerbatimStringEscapes(newStr);
                    if (!string.IsNullOrWhiteSpace(newStr))
                    {
                        newStr = AgentTextUtilities.StripSpuriousBlankLines(newStr);
                    }
                    if (cleanedNewStr != newStr)
                    {
                        newStr = cleanedNewStr;
                    }
                }
                if (!string.IsNullOrWhiteSpace(oldStr) &&
                    (Regex.IsMatch(oldStr, @"\.\.\.\s*\[?\s*\d*\s*lines?\s*omitted\]?", RegexOptions.IgnoreCase) ||
                     Regex.IsMatch(oldStr, @"\{\s*\.\.\.\s*\}")))
                {
                    var snippet = "";
                    if (step.LineNumber > 0 && !string.IsNullOrWhiteSpace(fileContent))
                    {
                        var contentLines = fileContent.Split('\n');
                        var start = Math.Max(0, step.LineNumber - 6);
                        var end = Math.Min(contentLines.Length, step.LineNumber + 6);
                        var actualLines = new List<string>();
                        for (var i = start; i < end; i++)
                            actualLines.Add($"{i + 1}: {contentLines[i]}");
                        snippet = "\n\nHere is the ACTUAL code around the target line — copy your oldString VERBATIM from here:\n```\n" +
                                  string.Join("\n", actualLines) + "\n```";
                    }
                    var fileExt = Path.GetExtension(relPath).ToLowerInvariant();
                    if (fileExt == ".cs" && !string.IsNullOrWhiteSpace(newStr))
                    {
                        var targetClassMatch = Regex.Match(oldStr, @"class\s+(\w+)");
                        if (targetClassMatch.Success)
                        {
                            var targetClassName = targetClassMatch.Groups[1].Value;
                            var (astOldStr, astErr) = AstResolveEdit(fullPath, "class", targetClassName, returnTail: false);
                            if (astOldStr != null)
                            {
                                var cleanNewStr = Regex.Replace(newStr,
                                    @"\.\.\.\s*\[?\s*\d*\s*lines?\s*omitted\]?[^\n]*\n?", "",
                                    RegexOptions.IgnoreCase);
                                cleanNewStr = Regex.Replace(cleanNewStr, @"\{\s*\.\.\.\s*\}\s*\n?", "");
                                cleanNewStr = cleanNewStr.TrimStart('\n', '\r');
                                var newClassRegex = new Regex(@"class\s+(\w+)");
                                var newClassMatch = newClassRegex.Match(cleanNewStr);
                                var insertStart = -1;
                                while (newClassMatch.Success)
                                {
                                    var className = newClassMatch.Groups[1].Value;
                                    if (!string.Equals(className, targetClassName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        insertStart = newClassMatch.Index;
                                        break;
                                    }
                                    newClassMatch = newClassMatch.NextMatch();
                                }
                                if (insertStart >= 0)
                                {
                                    var insertBody = cleanNewStr[insertStart..];
                                    if (!string.IsNullOrWhiteSpace(insertBody))
                                    {
                                        var indentedBody = await FormatSnippetAsync(astOldStr, insertBody, relPath);
                                        var mergedStr = astOldStr.TrimEnd('\n', '\r') + "\n\n" + indentedBody;
                                        return (astOldStr, mergedStr, false, null, false, null, true);
                                    }
                                }
                            }
                        }
                    }
                    return (null, null, false, null, false,
                        $"oldString contains truncation markers (e.g., '... [N lines omitted]' or '{{ ... }}'). " +
                        "You MUST output the EXACT, COMPLETE code verbatim. Do NOT abbreviate or truncate. " +
                        "oldString MUST be the literal 1-3 lines of code from the file, copied character-for-character." +
                        snippet, false);
                }
                newStr = AgentCodeFormatting.AutoFixPythonStatements(newStr ?? "", relPath);
                if (!string.IsNullOrWhiteSpace(newStr) && Path.GetExtension(relPath).Equals(".py", StringComparison.OrdinalIgnoreCase))
                {
                    var pyKeywords = "print|return|if|for|while|def|class|import|from|with|try|except|finally|raise|yield|assert|del|global|nonlocal|pass|break|continue";
                    newStr = Regex.Replace(newStr, $@"\)\s+({pyKeywords})\b", ")\n$1");
                }
            }
            if (string.IsNullOrWhiteSpace(oldStr) &&
                !string.IsNullOrWhiteSpace(newStr) &&
                fileExists &&
                string.IsNullOrWhiteSpace(fileContent))
            {
                oldStr = "";
                return (oldStr, newStr ?? "", false, null, false, null, false);
            }
            if (jRoot.TryGetProperty("format", out var fmtE) &&
                string.Equals(fmtE.GetString(), "method", StringComparison.OrdinalIgnoreCase))
            {
                var methodSymbol = jRoot.TryGetProperty("targetSymbol", out var msEl) ? msEl.GetString() : null;
                var newCodeVal = default(JsonElement);
                var hasNewCode = jRoot.TryGetProperty("newCode", out newCodeVal);
                string? newCodeStr2 = null;
                if (hasNewCode && newCodeVal.ValueKind == JsonValueKind.String)
                    newCodeStr2 = newCodeVal.GetString();
                else if (hasNewCode && newCodeVal.ValueKind == JsonValueKind.Array)
                {
                    var lines = new List<string>();
                    foreach (var item in newCodeVal.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                            lines.Add(item.GetString() ?? "");
                    }
                    if (lines.Count > 0) newCodeStr2 = string.Join("\n", lines);
                }
                if (string.IsNullOrWhiteSpace(methodSymbol))
                    return (null, null, false, null, false,
                        "format=method requires a targetSymbol field with the method name", false);
                if (string.IsNullOrWhiteSpace(newCodeStr2))
                    return (null, null, false, null, false,
                        "format=method requires a newCode field with the replacement method source", false);
                var methodExt = Path.GetExtension(relPath).ToLowerInvariant();
                string? astOld = null;
                foreach (var tryType in new[] { "method", "class" })
                {
                    (astOld, _) = AstResolveEdit(fullPath, tryType, methodSymbol);
                    if (astOld != null) break;
                }
                if (astOld == null)
                    return (null, null, false, null, false,
                        $"format=method: AST could not find '{methodSymbol}' in {relPath} — verify the method name", false);
                var formattedNewCode = AgentTextUtilities.NormalizeLineEndings(newCodeStr2.Trim());
                if (CodeFormatterService.CanFormat(methodExt))
                {
                    var fmtNew = await CodeFormatterService.FormatAsync("dummy" + methodExt, formattedNewCode, ct);
                    if (!string.IsNullOrWhiteSpace(fmtNew) && fmtNew.Length > 10)
                        formattedNewCode = AgentTextUtilities.NormalizeLineEndings(fmtNew.Trim());
                }
                return (AgentTextUtilities.NormalizeLineEndings(astOld), formattedNewCode, false, null, false, null, true);
            }
            if (!string.IsNullOrWhiteSpace(oldStr)) { return (oldStr, newStr ?? "", false, null, false, null, false); }
            return (null, null, false, null, false, "JSON has no oldString, targetType, fullFile, alreadyDone, or format=method field", false);
        }
        catch
        {
            if (raw.Contains(D_DONE, StringComparison.OrdinalIgnoreCase))
                return (null, null, false, null, true, null, false);
            var ffS = raw.IndexOf(D_FULL, StringComparison.OrdinalIgnoreCase);
            var ffE = raw.IndexOf(D_FULL_END, StringComparison.OrdinalIgnoreCase);
            if (ffS >= 0)
            {
                if (ffE < ffS)
                    return (null, null, false, null, false, "Response truncated — FULL_FILE not closed.", false);
                var body = raw[(ffS + D_FULL.Length)..ffE];
                body = StripFullFileFence(body);
                return (null, null, true, body, false, null, false);
            }
            var osMatch = Regex.Match(raw,
                @"""oldString""\s*:\s*\[([\s\S]*?)\]\s*,\s*""newString""\s*:\s*\[([\s\S]*?)\]",
                RegexOptions.IgnoreCase);
            if (osMatch.Success)
            {
                var oldRaw = osMatch.Groups[1].Value;
                var newRaw = osMatch.Groups[2].Value;
                var oldLines = ExtractQuotedStrings(oldRaw);
                var newLines = ExtractQuotedStrings(newRaw);
                if (oldLines.Count > 0)
                {
                    oldStr = string.Join("\n", oldLines);
                    newStr = string.Join("\n", newLines);
                    newStr = AgentCodeFormatting.AutoFixPythonStatements(newStr, relPath);
                    return (oldStr, newStr ?? "", false, null, false, null, false);
                }
            }
            var osStrMatch = Regex.Match(raw,
                @"""oldString""\s*:\s*""([\s\S]*?)""\s*,\s*""newString""\s*:\s*""([\s\S]*?)""",
                RegexOptions.IgnoreCase);
            if (osStrMatch.Success)
            {
                oldStr = osStrMatch.Groups[1].Value;
                newStr = osStrMatch.Groups[2].Value;
                return (oldStr, newStr ?? "", false, null, false, null, false);
            }
            var ttMatch = Regex.Match(raw,
                @"""targetType""\s*:\s*""(\w+)""", RegexOptions.IgnoreCase);
            var tnMatch = Regex.Match(raw,
               @"""targetName""\s*:\s*""((?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase);
            if (ttMatch.Success && tnMatch.Success)
            {
                var tt = ttMatch.Groups[1].Value;
                var tn = AgentJsonUtilities.UnescapeJsonString(tnMatch.Groups[1].Value);
                var ncIdx = raw.IndexOf("\"newCode\"", StringComparison.OrdinalIgnoreCase);
                if (ncIdx >= 0)
                {
                    var afterKey = raw[(ncIdx + "\"newCode\"".Length)..].TrimStart();
                    if (afterKey.StartsWith(":"))
                        afterKey = afterKey[1..].TrimStart();
                    string? newCodeStr = null;
                    if (afterKey.StartsWith("["))
                    {
                        var depth = 0;
                        for (var i = 0; i < afterKey.Length; i++)
                        {
                            if (afterKey[i] == '[') depth++;
                            else if (afterKey[i] == ']')
                            {
                                depth--; if (depth == 0)
                                {
                                    var lines = ExtractQuotedStrings(afterKey[1..i]);
                                    if (lines.Count > 0)
                                    {
                                        lines = lines.Select(l => AgentJsonUtilities.UnescapeJsonString(l)).ToList();
                                        newCodeStr = string.Join("\n", lines);
                                    }
                                    break;
                                }
                            }
                        }
                    }
                    else if (afterKey.StartsWith("\""))
                    {
                        var content = afterKey[1..];
                        for (var i = 0; i < content.Length; i++)
                        {
                            if (content[i] == '\\' && i + 1 < content.Length && content[i + 1] == '"') { i++; continue; }
                            if (content[i] == '"')
                            {
                                var nxt = i + 1 < content.Length ? content[i + 1] : '\0';
                                if (nxt == ',' || nxt == '}' || nxt == '\n' || nxt == '\r' || nxt == ' ' || nxt == '\0')
                                { newCodeStr = content[..i]; break; }
                            }
                        }
                        if (newCodeStr != null) newCodeStr = AgentJsonUtilities.UnescapeJsonString(newCodeStr);
                    }
                    if (!string.IsNullOrWhiteSpace(tt) && !string.IsNullOrWhiteSpace(tn) && newCodeStr != null)
                    {
                        newCodeStr = Regex.Replace(newCodeStr, @"""\s*\+\s*""", "");
                        var rawInsertAfterMatch = Regex.Match(raw, @"""insertAfter""\s*:\s*(true|false)", RegexOptions.IgnoreCase);
                        var hasInsertAfter = rawInsertAfterMatch.Success;
                        var insertAfter = rawInsertAfterMatch.Success && string.Equals(rawInsertAfterMatch.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
                        var rawReplaceMatch = Regex.Match(raw, @"""replace""\s*:\s*(true|false)", RegexOptions.IgnoreCase);
                        var hasReplace = rawReplaceMatch.Success;
                        var replaceSection = rawReplaceMatch.Success && string.Equals(rawReplaceMatch.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
                        if (string.Equals(tt, "html", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!System.IO.File.Exists(fullPath))
                                return (null, null, false, null, false, $"FORMAT D failed: file not found '{relPath}'", false);
                            var sourceText = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                            var wantsReplace = replaceSection ||
                                (hasInsertAfter && !insertAfter && !hasReplace) ||
                                (!hasInsertAfter && !hasReplace);
                            // DELETION INTENT: replace with empty newCode removes the matched block.
                            // Resolve the anchor (exact → normalized → collapsed → fuzzy chain) BEFORE
                            // rejecting, so a deletion payload never reports "targetName not found"
                            // for a block that exists verbatim.
                            if (string.IsNullOrWhiteSpace(newCodeStr) && wantsReplace)
                            {
                                var (delBlock, _, delErr) = HtmlDomEditor.ResolveHtmlAnchor(
                                    sourceText, tn, step.Change, step.LineNumber,
                                    expandToClosingTags: false, true);
                                if (delBlock == null)
                                {
                                    // Already-done verdict BEFORE erroring: distinguishes "already
                                    // removed" (full target block absent) from a hallucinated/drifted
                                    // targetName (present → retry with a better anchor).
                                    var (delDone, delReason) = FormatDAlreadyDoneVerdict(sourceText, tn, newCodeStr);
                                    if (delDone)
                                    {
                                        await EmitLog(emitSse, "info", $"✓ Already done: {relPath} — {delReason}", ct: ct);
                                        return (null, null, false, null, true, null, false);
                                    }
                                    return (null, null, false, null, false,
                                        $"FORMAT D failed: targetName block not found in {relPath}. " +
                                        $"Copy the exact code block from the file as targetName. " +
                                        "For a small change, use TARGETED REPLACE: targetName = the SINGLE unique line " +
                                        "being changed (verbatim), newCode = only the replacement for that line — do NOT " +
                                        "reproduce the whole section.", false);
                                }
                                return (delBlock, "", false, null, false, null, true);
                            }
                            newCodeStr = HtmlDomEditor.StripLeadingClosingDivs(newCodeStr, tn);
                            if (string.IsNullOrWhiteSpace(newCodeStr))
                            {
                                return (null, null, false, null, false,
                                    $"FORMAT D failed: newCode is empty — when replace:true, newCode MUST contain the replacement HTML for the targetName block.", false);
                            }
                            if (!newCodeStr.Contains('<', StringComparison.Ordinal))
                            {
                                return (null, null, false, null, false,
                                    $"FORMAT D failed: newCode is incomplete (only closing tags). Generate the full HTML to insert.", false);
                            }
                            var (fmtDone, fmtDoneReason) = FormatDAlreadyDoneVerdict(sourceText, tn, newCodeStr);
                            if (fmtDone)
                            {
                                await EmitLog(emitSse, "info", $"✓ Already done: {relPath} — {fmtDoneReason}", ct: ct);
                                return (null, null, false, null, true, null, false);
                            }
                            var (matchedBlock, matchIndex, htmlErr) = HtmlDomEditor.ResolveHtmlAnchor(sourceText, tn, step.Change, step.LineNumber);
                            if (matchedBlock == null)
                            {
                                return (null, null, false, null, false,
                                    $"FORMAT D failed: targetName block not found in {relPath}. " +
                                    $"Copy the exact code block from the file as targetName. " +
                                    "For a small change, use TARGETED REPLACE: targetName = the SINGLE unique line " +
                                    "being changed (verbatim), newCode = only the replacement for that line — do NOT " +
                                    "reproduce the whole section.", false);
                            }
                            if (replaceSection || (hasInsertAfter && !insertAfter && !hasReplace))
                            {
                                var indented = await FormatSnippetAsync(matchedBlock, newCodeStr, relPath);
                                return (matchedBlock, indented, false, null, false, null, true);
                            }
                            if (insertAfter || (hasReplace && !replaceSection && !hasInsertAfter))
                            {
                                var indented = await FormatSnippetAsync(matchedBlock, newCodeStr, relPath);
                                return (matchedBlock, matchedBlock + "\n" + indented, false, null, false, null, true);
                            }
                            return (matchedBlock, newCodeStr + "\n" + matchedBlock, false, null, false, null, true);
                        }
                        if (insertAfter && System.IO.File.Exists(fullPath))
                        {
                            var sourceText = System.IO.File.ReadAllText(fullPath, Encoding.UTF8);
                            var idx = sourceText.IndexOf(tn, StringComparison.Ordinal);
                            if (idx < 0) idx = sourceText.IndexOf(tn, StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0)
                            {
                                var afterAnchor = idx + tn.Length;
                                var indented = await FormatSnippetAsync(tn, newCodeStr, relPath);
                                newStr = sourceText[..afterAnchor] + "\n" + indented + sourceText[afterAnchor..];
                                return (tn, newStr, false, null, false, null, true);
                            }
                        }
                        if (insertAfter)
                        {
                            var (fullStr, astErr) = AstResolveEdit(fullPath, tt, tn, returnTail: false);
                            if (fullStr != null) { var indented = await FormatSnippetAsync(fullStr, newCodeStr, relPath); newStr = fullStr + "\n" + indented; return (fullStr, newStr, false, null, false, null, true); }
                        }
                        else
                        {
                            var (astOldStr, astErr) = AstResolveEdit(fullPath, tt, tn, returnTail: false);
                            if (astOldStr != null) { var indented = await FormatSnippetAsync(astOldStr, newCodeStr, relPath); return (astOldStr, indented, false, null, false, null, true); }
                        }
                    }
                }
            }
            var oS = raw.IndexOf(D_OLD, StringComparison.OrdinalIgnoreCase);
            var oE = raw.IndexOf(D_OLD_END, StringComparison.OrdinalIgnoreCase);
            var nS = raw.IndexOf(D_NEW, StringComparison.OrdinalIgnoreCase);
            var nE = raw.IndexOf(D_NEW_END, StringComparison.OrdinalIgnoreCase);
            if (oS < 0)
                return (null, null, false, null, false, "No edit markers found — check LLM output", false);
            if (oE < 0 || nS < 0 || nE < 0)
                return (null, null, false, null, false, "Response truncated — markers not closed", false);
            oldStr = raw[(oS + D_OLD.Length)..oE].TrimStart('\r', '\n').TrimEnd('\r', '\n');
            newStr = raw[(nS + D_NEW.Length)..nE].TrimStart('\r', '\n').TrimEnd('\r', '\n');
            newStr = AgentCodeFormatting.AutoFixPythonStatements(newStr, relPath);
            if (string.IsNullOrWhiteSpace(oldStr))
                return (null, null, false, null, false, "OLD section is empty", false);
            return (oldStr, newStr, false, null, false, null, false);
        }
    }
    private static readonly HashSet<string> _removalStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "all", "any", "this", "these", "those", "code", "class", "css", "line", "lines",
        "block", "button", "div", "element", "section", "it", "them", "its"
    };

    private static bool RemovalTargetStillPresent(string? change, string content, string? oldString)
    {
        if (string.IsNullOrWhiteSpace(change)) return false;
        var m = Regex.Match(change, @"(?:remove|delete)\s+(?:the\s+)?([\w.\-]+)", RegexOptions.IgnoreCase);
        var target = m.Success ? m.Groups[1].Value.Trim() : null;
        if (string.IsNullOrWhiteSpace(target) || target.Length < 2 || _removalStopwords.Contains(target))
        {
            var css = Regex.Match(change, @"\.([\w\-]+)|#([\w\-]+)");
            target = css.Success ? (css.Groups[1].Success ? css.Groups[1].Value : css.Groups[2].Value) : null;
        }

        if (string.IsNullOrWhiteSpace(target) || target.Length < 2 || _removalStopwords.Contains(target))
        {
            var oldSel = Regex.Match(oldString ?? "", @"^\s*[\.#]([\w\-]+)", RegexOptions.IgnoreCase);
            target = oldSel.Success ? oldSel.Groups[1].Value : null;
        }
        if (string.IsNullOrWhiteSpace(target) || target.Length < 2 || _removalStopwords.Contains(target))
            return false;
            
        return Regex.IsMatch(content, @"(?:^|[^\w\-])" + Regex.Escape(target) + @"(?:[^\w\-]|$)", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// FORMAT D already-done verdict — the removal-with-survivor rule from
    /// PreEditValidation applied to the FORMAT D payload path (targetType="html",
    /// targetName + empty/absent newCode = a deletion). A deletion expressed as
    /// replace-with-survivor (newCode is a fragment of targetName) must NOT be declared
    /// already-done just because the surviving fragment is present in the file — only the
    /// FULL targetName block being absent proves the removal already happened. Mirrors the
    /// PreEditValidation survivor-fragment fix for oldString/newString edits.
    /// </summary>
    private static (bool alreadyDone, string reason) FormatDAlreadyDoneVerdict(
        string sourceText, string? targetName, string? newCode)
    {
        // Empty/absent newCode = pure deletion: only the full target block being absent
        // means the removal already happened.
        if (string.IsNullOrWhiteSpace(newCode))
        {
            return FormatDTargetBlockAbsent(sourceText, targetName)
                ? (true, "FORMAT D deletion already applied — target block absent")
                : (false, "");
        }
        // newCode present: if it is a strict fragment of targetName (exact or
        // whitespace-collapsed), this is a removal-with-survivor — the survivor's presence
        // proves NOTHING; only the full target block being absent proves the removal done.
        if (IsSurvivorFragment(targetName, newCode))
        {
            return FormatDTargetBlockAbsent(sourceText, targetName)
                ? (true, "FORMAT D removal already applied — target block absent, survivor present")
                : (false, "");
        }
        // Plain insert/replace guard (unchanged behavior): newCode already present → done.
        return sourceText.Contains(newCode, StringComparison.OrdinalIgnoreCase)
            ? (true, "HTML block already present")
            : (false, "");
    }

    /// <summary>
    /// True when <paramref name="newBlock"/> is a STRICT fragment of
    /// <paramref name="oldBlock"/> (exact, or whitespace-collapsed with a min length so
    /// tiny tokens like <c>&lt;/div&gt;</c> never count) — the removal-with-survivor
    /// shape shared by PreEditValidation (oldString/newString) and the FORMAT D payload
    /// path (targetName/newCode). A deletion expressed as "replace block with a fragment
    /// of it" must not trip the insert already-done guard: only the FULL block being
    /// absent proves the removal already happened.
    /// </summary>
    private static bool IsSurvivorFragment(string? oldBlock, string? newBlock)
    {
        if (string.IsNullOrWhiteSpace(oldBlock) || string.IsNullOrWhiteSpace(newBlock)) return false;
        var oldNorm = AgentTextUtilities.NormalizeLineEndings(oldBlock);
        var newNorm = AgentTextUtilities.NormalizeLineEndings(newBlock);
        if (newNorm.Length >= oldNorm.Length) return false;
        if (oldNorm.Contains(newNorm, StringComparison.Ordinal)) return true;
        return newNorm.Length >= 3 &&
               AgentTextUtilities.CollapseWhitespace(oldNorm).Contains(
                   AgentTextUtilities.CollapseWhitespace(newNorm), StringComparison.Ordinal);
    }

    /// <summary>
    /// True when the FULL <paramref name="targetBlock"/> is genuinely absent from
    /// <paramref name="sourceText"/> — exact, trailing-trimmed, and whitespace-collapsed
    /// comparisons all fail. A surviving fragment is NOT evidence of absence; only the
    /// whole block being gone proves a FORMAT D deletion already applied. The collapsed
    /// fallback is conservative: a SHORT collapsed target (&lt; 15 chars) that doesn't
    /// collapse-match is treated as still-present rather than absent, because short
    /// snippets with intra-token whitespace drift can defeat collapsed matching — and a
    /// false "already done" would silently skip a removal that still needs applying.
    /// </summary>
    private static bool FormatDTargetBlockAbsent(string sourceText, string? targetBlock)
    {
        if (string.IsNullOrWhiteSpace(targetBlock)) return true;
        var target = AgentTextUtilities.NormalizeLineEndings(targetBlock);
        if (sourceText.Contains(target, StringComparison.Ordinal)) return false;
        var trimTarget = string.Join("\n", target.Split('\n').Select(l => l.TrimEnd()));
        var trimFile = string.Join("\n", sourceText.Split('\n').Select(l => l.TrimEnd()));
        if (trimFile.Contains(trimTarget, StringComparison.Ordinal)) return false;
        var collapsedTarget = AgentTextUtilities.CollapseWhitespace(target);
        // Positive collapsed match → present at any length.
        if (AgentTextUtilities.CollapseWhitespace(sourceText).Contains(collapsedTarget, StringComparison.Ordinal))
            return false;
        // Negative match: only trust it for long enough blocks; short snippets stay
        // "present" (conservative — never false-skip a removal).
        return collapsedTarget.Length >= 15;
    }

    /// <summary>
    /// THE single source of truth for "is this deletion already applied?" — shared by the
    /// executor guard (PreEditValidation) and the plan auditor (PlanPreAuditAsync) so they
    /// ALWAYS agree on removals. A removal is already-done ONLY when the FULL removal
    /// target is absent from the file: exact → trailing-trimmed → whitespace-collapsed, and
    /// keyword evidence confirms the target is gone. A surviving fragment (oldString =
    /// survivor + target → newString = survivor) is NOT evidence of a completed removal —
    /// only the full target block being absent proves it (the survivor-fragment rule).
    /// Covers all three deletion carriers: oldString/newString, FORMAT D
    /// (targetType=html + targetName + empty/absent newCode), and description-quoted code.
    /// </summary>
    // Shared reason produced by PreEditValidation/IsRemovalAlreadyApplied for a removal
    // whose target is absent. The hallucinated-removal guard matches on this exact text,
    // so it lives as a named constant — a reword breaks the guard's Contains() check.
    private const string RemovalTargetAbsentReason = "code to be removed is already absent from file";

    private static bool IsRemovalAlreadyApplied(string content, PlanStep step)
    {
        // FORMAT D payload: delegate to FormatDAlreadyDoneVerdict (the resolver's own
        // verdict) so ALL three shapes agree with the executor — pure deletion (empty/
        // absent newCode → full targetName block must be absent), replace-with-survivor
        // (newCode is a fragment of targetName → same full-block-absence rule), and plain
        // insert/replace (newCode already present).
        // PRECEDENCE: when a step carries BOTH FORMAT D fields (TargetType=html +
        // TargetName) AND OldString/NewString, the FORMAT D carrier wins and the
        // oldString evidence is ignored. This is deliberate — TargetName is the anchor
        // the executor resolves against, so it is the stronger authority on the removal.
        // ParseStepFromJson populates both only in edge cases; the verdict stays
        // deterministic by arm order.
        if (string.Equals(step.TargetType, "html", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(step.TargetName) && string.IsNullOrWhiteSpace(step.NewString))
        {
            var fmtNewCode = step.NewCode is { Count: > 0 } ? string.Join("\n", step.NewCode) : null;
            return FormatDAlreadyDoneVerdict(content, step.TargetName, fmtNewCode).alreadyDone;
        }
        // oldString-based deletion (incl. survivor-fragment shape).
        if (!string.IsNullOrWhiteSpace(step.OldString))
        {
            var oldStr = AgentTextUtilities.NormalizeLineEndings(step.OldString);
            if (content.Contains(oldStr, StringComparison.Ordinal)) return false;
            var trimOld = string.Join("\n", oldStr.Split('\n').Select(l => l.TrimEnd()));
            var trimFile = string.Join("\n", content.Split('\n').Select(l => l.TrimEnd()));
            if (trimFile.Contains(trimOld, StringComparison.Ordinal)) return false;
            // Keyword evidence: the removal target must be genuinely gone, not just drifted.
            return !RemovalTargetStillPresent(step.Change, content, step.OldString);
        }
        // Description-only carrier: an HTML block or quoted snippet in the change text.
        if (!string.IsNullOrWhiteSpace(step.Change))
        {
            var htmlMatch = Regex.Match(step.Change, @"<(\w+)\b[^>]*>.*?</\1>", RegexOptions.Singleline);
            string? codeToRemove = htmlMatch.Success ? htmlMatch.Value : null;
            if (string.IsNullOrWhiteSpace(codeToRemove))
            {
                var quoteMatch = Regex.Match(step.Change, @"`([^`]+)`|""([^""]+)""|'([^']+)'");
                if (quoteMatch.Success)
                {
                    codeToRemove = quoteMatch.Groups[1].Success ? quoteMatch.Groups[1].Value
                                 : quoteMatch.Groups[2].Success ? quoteMatch.Groups[2].Value
                                 : quoteMatch.Groups[3].Value;
                }
            }
            if (!string.IsNullOrWhiteSpace(codeToRemove) && codeToRemove.Length >= 20 &&
                !content.Contains(codeToRemove, StringComparison.Ordinal) &&
                !RemovalTargetStillPresent(step.Change, content, step.OldString))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Quick line-based inventory of the member/function names in a source file, used to
    /// feed corrective feedback when the planner tries to remove a symbol that doesn't
    /// exist (first-step hallucinated-removal guard). Best-effort regex — good enough to
    /// list real methods so the model can re-ground; not a parser.
    /// </summary>
    private static string ExtractMemberInventory(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rx = new Regex(
            @"(?m)^\s*(?:(?:export|public|private|protected|internal|async|static|get|set|readonly|override|abstract|virtual|function)\s+)*(?:[\w<>\[\],.?$]+\s+)*(?<name>[A-Za-z_$][\w$]*)\s*\(");
        foreach (Match m in rx.Matches(content))
        {
            var n = m.Groups["name"].Value;
            if (n.Length < 2) continue;
            if (n is "if" or "for" or "while" or "switch" or "catch" or "return" or "function" or "new" or "case" or "else" or "using" or "import" or "from" or "typeof" or "delete" or "void") continue;
            names.Add(n);
        }
        var list = names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(24).ToList();
        return list.Count == 0 ? "" : string.Join(", ", list);
    }

    private static bool HasConcreteEdit(PlanStep? step)
    {
        if (step == null) return false;
        if (!string.IsNullOrWhiteSpace(step.OldString)) return true;
        if (!string.IsNullOrWhiteSpace(step.NewString)) return true;
        if (step.Edits != null && step.Edits.Count > 0) return true;
        // FORMAT C/D: targetType + targetName + newCode is a concrete replacement payload.
        if (!string.IsNullOrWhiteSpace(step.TargetType) && !string.IsNullOrWhiteSpace(step.TargetName) &&
            step.NewCode is { Count: > 0 }) return true;
        // fullFile: complete file content is a concrete create-file payload.
        if (!string.IsNullOrWhiteSpace(step.FullFile)) return true;
        return false;
    }

    private static bool ShouldApplyDirectly(PlanStep? step)
    {
        if (step == null || !HasConcreteEdit(step)) return false;
        if (step.InsertAfter == true) return false;
        if (step.Edits is { Count: > 0 } && string.IsNullOrWhiteSpace(step.OldString)) return false;
        return true;
    }

    private static (PreEditVerdict verdict, string reason) PreEditValidation(string fileContent, PlanStep step)
    {
        if (string.IsNullOrWhiteSpace(fileContent))
        {
            return (PreEditVerdict.Proceed, "");
        }
        var content = AgentTextUtilities.NormalizeLineEndings(fileContent);
        var changeLower = (step.Change ?? "").ToLowerInvariant();
        if ((changeLower.StartsWith("create ") || changeLower.Contains("create a new") || changeLower.Contains("create new") || changeLower.Contains("add new")) &&
            changeLower.Contains("component"))
        {
            var compMatch = Regex.Match(step.Change ?? "", @"([A-Z]\w+Component)", RegexOptions.IgnoreCase);
            if (compMatch.Success)
            {
                var compName = compMatch.Groups[1].Value;
                if (Regex.IsMatch(content, $@"\b(class|export\s+class)\s+{Regex.Escape(compName)}\b", RegexOptions.IgnoreCase))
                {
                    return (PreEditVerdict.AlreadyDone, $"Component '{compName}' already exists in the file");
                }
            }
        }
        var stepExt = Path.GetExtension(step.File ?? "").ToLowerInvariant();
        if (stepExt is ".js" or ".jsx" or ".mjs" or ".cjs")
        {
            var jsMethodName = AgentMethodInventory.ExtractJsMethodNameFromChange(step.Change ?? "");
            if (!string.IsNullOrWhiteSpace(jsMethodName) &&
                jsMethodName.Length >= 2 &&
                !jsMethodName.Equals("function", StringComparison.OrdinalIgnoreCase) &&
                !jsMethodName.Equals("method", StringComparison.OrdinalIgnoreCase) &&
                !jsMethodName.Equals("handler", StringComparison.OrdinalIgnoreCase))
            {
                if (AgentMethodInventory.JsMethodExistsInContent(content, jsMethodName))
                {
                    return (PreEditVerdict.AlreadyDone,
                        $"JavaScript method '{jsMethodName}' already exists in {step.File}");
                }
                if (!string.IsNullOrWhiteSpace(step.NewString))
                {
                    var newName = AgentMethodInventory.ExtractJsMethodNameFromCode(step.NewString);
                    if (!string.IsNullOrWhiteSpace(newName) &&
                        !string.Equals(newName, jsMethodName, StringComparison.Ordinal) &&
                        AgentMethodInventory.JsMethodExistsInContent(content, newName))
                    {
                        return (PreEditVerdict.AlreadyDone,
                            $"JavaScript method '{newName}' (from newString) already exists in {step.File}");
                    }
                }
            }
        }
        if (changeLower.StartsWith("add ") && changeLower.Contains(" method"))
        {
            var methodMatch = Regex.Match(step.Change ?? "", @"(?:Add|Create)\s+(?:the\s+)?(\w+)\s+method", RegexOptions.IgnoreCase);
            if (!methodMatch.Success)
                methodMatch = Regex.Match(step.Change ?? "", @"method\s+named\s+(\w+)", RegexOptions.IgnoreCase);
            if (methodMatch.Success)
            {
                var methodName = methodMatch.Groups[1].Value;
                if (Regex.IsMatch(content, $@"\b(void|Task|async\s+Task|public|private|protected|internal)\s+.*\b{Regex.Escape(methodName)}\s*\(", RegexOptions.IgnoreCase))
                {
                    return (PreEditVerdict.AlreadyDone, $"Method '{methodName}' already exists in the file");
                }
            }
        }
        var fnNameRegex = Regex.Match(step.Change ?? "", @"(?:vm\.)?(\w+)\s*=\s*function", RegexOptions.IgnoreCase);
        if (!fnNameRegex.Success)
            fnNameRegex = Regex.Match(step.Change ?? "", @"function\s+(\w+)\s*\(", RegexOptions.IgnoreCase);
        if (!fnNameRegex.Success)
            fnNameRegex = Regex.Match(step.Change ?? "", @"ensure\s+(?:vm\.)?(\w+)\s+method", RegexOptions.IgnoreCase);
        if (!fnNameRegex.Success)
            fnNameRegex = Regex.Match(step.Change ?? "", @"implement\s+(\w+)\s+function", RegexOptions.IgnoreCase);
        if (fnNameRegex.Success)
        {
            var fnName = fnNameRegex.Groups[1].Value;
            if (fnName.Length > 2 && !fnName.Equals("function", StringComparison.OrdinalIgnoreCase))
            {
                var fnPattern = $@"(?:vm\.)?{Regex.Escape(fnName)}\s*(?:[:=])\s*function\s*\(";
                if (Regex.IsMatch(content, fnPattern, RegexOptions.IgnoreCase))
                {
                    return (PreEditVerdict.AlreadyDone, $"Function '{fnName}' already exists in the file");
                }
            }
        }
        if (changeLower.StartsWith("add ") || changeLower.StartsWith("insert ") || changeLower.StartsWith("move "))
        {
            var elementMatch = Regex.Match(step.Change ?? "", @"(?:add|insert|move)\s+(?:the\s+)?([\w-]+)\s+(?:div|element|span|button|table|code|block|method)", RegexOptions.IgnoreCase);
            var containerMatch = Regex.Match(step.Change ?? "", @"(?:inside|into|to|before|after|within)\s+(?:the\s+)?([\w-]+)\s+(?:div|container|element|section|method|class)", RegexOptions.IgnoreCase);
            if (elementMatch.Success && containerMatch.Success)
            {
                var elementKeyword = elementMatch.Groups[1].Value.ToLowerInvariant();
                var containerKeyword = containerMatch.Groups[1].Value.ToLowerInvariant();
                if (elementKeyword != containerKeyword && elementKeyword.Length > 2 && containerKeyword.Length > 2)
                {
                    var contentLower = content.ToLowerInvariant();
                    var containerIdx = contentLower.IndexOf(containerKeyword, StringComparison.Ordinal);
                    if (containerIdx >= 0)
                    {
                        var elementIdx = contentLower.IndexOf(elementKeyword, containerIdx, StringComparison.Ordinal);
                        if (elementIdx >= 0 && elementIdx - containerIdx < 500)
                        {
                            return (PreEditVerdict.AlreadyDone, $"'{elementKeyword}' already appears inside '{containerKeyword}' in the file");
                        }
                    }
                }
            }
        }
        if (changeLower.StartsWith("remove ") || changeLower.StartsWith("delete "))
        {
            // Shared with PlanPreAuditAsync: a removal is already-done ONLY when the FULL
            // removal target is absent (exact → trimmed → collapsed) AND keyword evidence is
            // gone. A survivor fragment (oldString = survivor + target → newString = survivor)
            // is NOT evidence of a completed removal — the executor and the auditor agree.
            if (IsRemovalAlreadyApplied(content, step))
                return (PreEditVerdict.AlreadyDone, RemovalTargetAbsentReason);
        }
        if (changeLower.StartsWith("move ") || changeLower.StartsWith("insert "))
        {
            if (!string.IsNullOrWhiteSpace(step.NewString))
            {
                var newStr = AgentTextUtilities.NormalizeLineEndings(step.NewString);
                if (content.Contains(newStr, StringComparison.Ordinal))
                    return (PreEditVerdict.AlreadyDone, "code already moved/inserted into file");
                var collapsedNew = CollapseWhitespace(newStr);
                if (collapsedNew.Length >= 15 &&
                    CollapseWhitespace(content).Contains(collapsedNew, StringComparison.Ordinal))
                    return (PreEditVerdict.AlreadyDone, "code already moved/inserted into file (whitespace differences only)");
            }
        }
        if (changeLower.StartsWith("add ") &&
            (changeLower.Contains(" property") || changeLower.Contains(" variable") || changeLower.Contains(" field")))
        {
            var propMatch = Regex.Match(step.Change ?? "", @"(?:Add|Create)\s+(?:the\s+)?(\w+)\s+(?:property|variable|field)", RegexOptions.IgnoreCase);
            if (propMatch.Success)
            {
                var propName = propMatch.Groups[1].Value;
                if (Regex.IsMatch(content, $@"\b{Regex.Escape(propName)}\b\s*[:=;]", RegexOptions.IgnoreCase))
                {
                    return (PreEditVerdict.AlreadyDone, $"Property/variable '{propName}' already exists in the file");
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(step.NewString))
        {
            var newStr = AgentTextUtilities.NormalizeLineEndings(step.NewString);
            // REMOVAL-WITH-SURVIVOR guard: the planner can express a deletion as
            // oldString = <survivor + target> → newString = <survivor> (the surviving context
            // fragment is emitted as the replacement). The survivor is trivially present in the
            // file even BEFORE the removal is applied — so finding newString here proves NOTHING
            // about whether the deletion already happened. Only the FULL oldString being absent
            // proves the removal is done. Skipping the survivor check here also lets the removal
            // branches above (remove/delete-prefixed changes) and the oldString-not-found path
            // below make the correct AlreadyDone decision against the actual removal target.
            var isSurvivorFragment = IsSurvivorFragment(step.OldString, newStr);
            if (!isSurvivorFragment && content.Contains(newStr, StringComparison.Ordinal))
                return (PreEditVerdict.AlreadyDone, "code already present in file");
            var collapsedNew = CollapseWhitespace(newStr);
            if (!isSurvivorFragment && collapsedNew.Length >= 15 &&
                CollapseWhitespace(content).Contains(collapsedNew, StringComparison.Ordinal))
                return (PreEditVerdict.AlreadyDone, "code already present in file (whitespace differences only)");
        }
        if (string.IsNullOrWhiteSpace(step.NewString) && !string.IsNullOrWhiteSpace(step.OldString))
        {
            var changeLower2 = (step.Change ?? "").Trim().ToLowerInvariant();
            if (_verifyPrefixes.Any(p => changeLower2.StartsWith(p)))
            {
                var oldStr = AgentTextUtilities.NormalizeLineEndings(step.OldString);
                if (content.Contains(oldStr, StringComparison.Ordinal))
                    return (PreEditVerdict.AlreadyDone, "step is verification-only — code already present");
            }
        }
        if (!string.IsNullOrWhiteSpace(step.OldString))
        {
            var oldStr = AgentTextUtilities.NormalizeLineEndings(step.OldString);
            if (!content.Contains(oldStr, StringComparison.Ordinal))
            {
                var trimOld = string.Join("\n", oldStr.Split('\n').Select(l => l.TrimEnd()));
                var trimFile = string.Join("\n", content.Split('\n').Select(l => l.TrimEnd()));
                if (!trimFile.Contains(trimOld, StringComparison.Ordinal))
                {
                    var fuzzy = AgentEditHeuristics.BuildExactMatchBlock(content, oldStr, step.LineNumber, step.Change);
                    if (fuzzy == null)
                    {
                        // A concrete oldString → newString replacement must still be ATTEMPTED:
                        // the tolerant apply matchers (line-based fuzzy + TryReplaceSafe) can absorb
                        // minor drift between the plan snapshot and the current file. Only skip the
                        // step (Irrelevant) when there is no replacement to apply.
                        if (string.IsNullOrWhiteSpace(step.NewString))
                        {
                            // Deletion with drifted oldString: if the removal target is STILL present
                            // in the file, let the tolerant matcher attempt it instead of declaring
                            // it Irrelevant (which would silently skip a plan-supplied deletion).
                            if (RemovalTargetStillPresent(step.Change, content, step.OldString))
                                return (PreEditVerdict.Proceed, "removal target still present — attempting tolerant deletion");
                            return (PreEditVerdict.Irrelevant, "oldString not found — context changed or already applied");
                        }
                    }
                }
            }
        }
        return (PreEditVerdict.Proceed, "");
    }
}
