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
using Weaver;

[ApiController]
[Route("api/agent")]
public class AgentController : ControllerBase
{
    private readonly IHttpClientFactory _clientFactory;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly TerminalService _terminal;
    private readonly FileHintsManager _fileHints;
    private readonly ConfigFileService _configFile;
    private readonly EmailService _emailService;
    private readonly BoardDataService _boardData;
    private const int MaxFileContextChars = 24_000;
    private const int MAX_COMMAND_ITERATIONS = 30;
    private const int MaxFullFileTokens = 4096; // 8192 max / 2 — fullFile must not exceed half the LLM's token limit
    private bool _lastConnectionCheckResult = true;
    private bool _gracefulStop;
    private static DateTime _nextConnectivityCheck = DateTime.MinValue;
    private static TimeSpan _infiniteTimeout = Timeout.InfiniteTimeSpan;
    private static readonly ConcurrentDictionary<string, PendingQuestion> _pendingQuestions = new();
    private static readonly ConcurrentDictionary<string, PendingContextReview> _pendingContextReviews = new();
    private static readonly ConcurrentDictionary<string, HashSet<int>> _cancelledSteps = new();
    private static readonly string[] UnsafeEditMarkers =
    {
        "…(truncated)", "â€¦(truncated)", "...(truncated)"
    };

    // ── Delimiter constants for edit resolution ────────────────────────────
    // Legacy delimiter constants — kept for fallback parsing
    private const string D_OLD = "<<<OLD>>>";
    private const string D_OLD_END = "<<<END_OLD>>>";
    private const string D_NEW = "<<<NEW>>>";
    private const string D_NEW_END = "<<<END_NEW>>>";
    private const string D_FULL = "<<<FULL_FILE>>>";
    private const string D_FULL_END = "<<<END_FULL_FILE>>>";
    private const string D_DONE = "<<<ALREADY_DONE>>>";

    private const string EditResolveSystemPrompt =
        "You are a surgical code editor. Output ONLY a JSON object.\n\n" +
        "FORMAT A — multi-line (output VERBATIM lines, one per array element — no escaping needed):\n" +
        "{\n" +
        "  \"oldString\": [\n" +
        "    \"  first line EXACTLY as it appears in the file\",\n" +
        "    \"  second line\",\n" +
        "    \"  third line\"\n" +
        "  ],\n" +
        "  \"newString\": [\n" +
        "    \"  first line\",\n" +
        "    \"  replacement second line\"\n" +
        "  ]\n" +
        "}\n\n" +
"FORMAT B — single-line (escape newlines as \\n):\n" +
"{\n" +
"  \"oldString\": \"line 1\\nline 2\\nline 3\",\n" +
"  \"newString\": \"replacement line 1\\nreplacement line 2\"\n" +
"}\n\n" +
"FORMAT C — AST-based (for any file — safest, system auto-extracts oldString from file):\n" +
"{\n" +
"  \"targetType\": \"method\",\n" +
"  \"targetName\": \"CalculateTotal\",\n" +
"  \"newCode\": \"    public int CalculateTotal() { return 42; }\"\n" +
"}\n" +
"  Supported targetType values: method, class, property, interface, struct, record, enum, constructor\n" +
"  newCode can be a string or array of lines (like FORMAT A).\n" +
"  The tool will parse the file's AST, find the named node, and replace its body.\n" +
"  INDENTATION in newCode: the first line (method signature) must start with the SAME indentation as the original method in the file.\n" +
"  Subsequent lines (method body) must be progressively indented: class indent + method indent + block indent.\n" +
"  Example — if the method is inside a class at 8 spaces, output newCode with 8 spaces before the signature\n" +
"  and each nested block adds 4 more spaces.\n\n" +
"INSERT-AFTER (add a new method without replacing existing ones):\n" +
"{\n" +
"  \"targetType\": \"method\",\n" +
"  \"targetName\": \"ExistingMethodName\",\n" +
"  \"insertAfter\": true,\n" +
"  \"newCode\": \"    public async Task<IActionResult> NewMethod() { ... }\"\n" +
"}\n" +
"  Use insertAfter:true to INSERT a new method/constructor RIGHT AFTER an existing one.\n" +
"  The target method is NOT replaced — the new code is added after its closing brace.\n" +
"  This is the SAFEST way to add a new method: no existing code is touched.\n" +
"  DO NOT use targetType=\"class\" to add methods — use insertAfter with targetType=\"method\" instead.\n\n" +
"FULL FILE:\n" +
        "{\n" +
        "  \"fullFile\": \"Complete file content (use array format for multi-line)\",\n" +
        "  \"fullFile\": [\"line1\", \"line2\"]\n" +
        "}\n\n" +
        "NO CHANGE:\n" +
        "{\n" +
        "  \"alreadyDone\": true\n" +
        "}\n\n" +
        "CRITICAL RULES:\n" +
        "1. oldString must exist VERBATIM in the file — copy character-for-character including EVERY leading space and tab (indentation). Do NOT strip or reduce indentation.\n" +
        "2. oldString must appear exactly ONCE in the file — include 2-3 surrounding lines as anchor context\n" +
        "3. NEVER put ... or […] or /* ... */ or any placeholder in oldString or newString\n" +
        "4. TRAILING WHITESPACE: you MAY omit trailing spaces at the end of each line in oldString. But LEADING whitespace (indentation) is REQUIRED — never remove it.\n" +
        "5. oldString must NOT have blank first/last lines — trim any empty lines\n" +
        "6. For insertions: include the line BEFORE as part of oldString, repeat it unchanged at the start of newString, then add the new lines after it\n" +
        "7. Each line's meaningful content (not counting leading whitespace) should be ≥ 8 characters — lines like `}`, `);`, `{` are too short and match everywhere. Always include enough context.\n" +
        "8. oldString must be ≥ 20 characters total — short strings cause false matches\n" +
        "9. Use FORMAT A (array) whenever the content has multiple lines — it is more reliable and needs no escaping" +
        "10. Output ONLY the JSON — no markdown, no code fences, no introductory text" +
         "11. INDENTATION: newString MUST use the EXACT SAME leading whitespace as oldString for every line. Open-brace ({) increases indent for following lines. Close-brace (}) decreases indent. Copy the leading whitespace character-for-character from oldString into newString.\n" +
           "12. FORMAT C (targetType/targetName/newCode) is for CODE files only (.cs, .ts, .js, .tsx, .jsx). " +
                "For non-C# code files, use targetType=\"method\" or targetType=\"function\" with targetName=\"{name}\". " +
                "For C# files, only FORMAT C is supported — oldString/newString will fail for C#. " +
                "For HTML, CSS, JSON, and other markup/data files, use oldString/newString — FORMAT C does NOT apply to those.\n" +
         "13. oldString STRICT LIMIT: MAXIMUM 10 lines. Outputting more than 10 lines causes UNIQUE ANCHOR matching to fail — the system CANNOT find 20+ lines verbatim.\n" +
         "14. To APPEND to the end of any file: oldString = last 2-3 closing braces only. Repeat them at the start of newString before your new code.\n" +
         "15. fullFile is ONLY for NEW files (files that don't exist yet). NEVER use fullFile for existing files.";

    public AgentController(
        IHttpClientFactory cf, IConfiguration config,
        IWebHostEnvironment env, TerminalService terminal, FileHintsManager fileHints,
        ConfigFileService configFile, EmailService emailService, BoardDataService boardData)
    {
        _clientFactory = cf; _config = config; _env = env; _terminal = terminal;
        _fileHints = fileHints; _configFile = configFile; _emailService = emailService;
        _boardData = boardData;
    }

    private string ResolveWorkspaceRoot()
    {
        var configuredRoot = _config.GetValue<string>("Editor:WorkspaceRoot");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            return Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.GetFullPath(Path.Combine(_env.ContentRootPath, configuredRoot));
        return Path.GetFullPath(Path.Combine(_env.ContentRootPath, ".."));
    }

    private string GetProjectRoot(string project)
    {
        var workspaceRoot = ResolveWorkspaceRoot();
        var projectSegment = string.IsNullOrWhiteSpace(project) ? "" :
            project.Trim().TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(workspaceRoot, projectSegment));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  SSE / LOGGING
    // ═══════════════════════════════════════════════════════════════════════

    private async Task EmitLog(bool emit, string level, string message, object? detail = null, CancellationToken ct = default)
    {
        if (!emit) return;
        await SendSse(Response, "log", new { ts = DateTime.UtcNow.ToString("o"), level, message, detail }, ct);
    }

    private static async Task SendSse(HttpResponse response, string eventName, object data, CancellationToken ct = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(data);
            await response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", ct);
            await response.Body.FlushAsync(ct);
        }
        catch (OperationCanceledException) { Console.WriteLine("ERROR, OperationCanceledException"); }
        catch (ObjectDisposedException) { Console.WriteLine("ERROR, ObjectDisposedException"); }
        catch (IOException) { Console.WriteLine("ERROR, IOException"); }
        catch (Exception) { Console.WriteLine("ERROR, Exception"); }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  EDIT RESOLUTION  (two-phase: plan describes WHAT, resolve finds HOW)
    // ═══════════════════════════════════════════════════════════════════════
    /// <summary>
    /// For large files, returns the excerpt most likely to contain the relevant edit target,
    /// centered around the plan's oldString or change-description keywords.
    /// Falls back to head+tail only when no location clue is available.
    /// </summary>
    private static string ExtractRelevantExcerpt(string fileContent, string changeDesc, string? planOldString)
    {
        const int RadiusLines = 60;
        var lines = fileContent.Split('\n');

        // ── Step 1: Always include structural header (imports + declaration) ──
        // Collect import lines (import/using/require) and the @Component/@Injectable/class line
        var structEnd = 0;
        var foundClassLine = -1;
        for (var i = 0; i < Math.Min(lines.Length, 80); i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("import ", StringComparison.Ordinal) ||
                trimmed.StartsWith("using ", StringComparison.Ordinal) ||
                trimmed.StartsWith("require(", StringComparison.Ordinal) ||
                trimmed.StartsWith("const ", StringComparison.Ordinal) ||
                trimmed.StartsWith("var ", StringComparison.Ordinal) ||
                trimmed.StartsWith("let ", StringComparison.Ordinal) ||
                trimmed.StartsWith("#include", StringComparison.Ordinal) ||
                trimmed.StartsWith("from ", StringComparison.Ordinal) ||
                trimmed.StartsWith("export ", StringComparison.Ordinal) ||
                trimmed == "")
                structEnd = i + 1;
            else if (trimmed.StartsWith("@Component", StringComparison.Ordinal) ||
                     trimmed.StartsWith("@Injectable", StringComparison.Ordinal) ||
                     trimmed.StartsWith("@Directive", StringComparison.Ordinal) ||
                     trimmed.StartsWith("@Pipe", StringComparison.Ordinal) ||
                     trimmed.StartsWith("@NgModule", StringComparison.Ordinal) ||
                     trimmed.StartsWith("public ") || trimmed.StartsWith("internal ") ||
                     trimmed.StartsWith("abstract class") || trimmed.StartsWith("class ") ||
                     trimmed.StartsWith("interface ") || trimmed.StartsWith("enum ") ||
                     trimmed.StartsWith("struct ") || trimmed.StartsWith("record ") ||
                     trimmed.StartsWith("function ") || trimmed.StartsWith("export function"))
            {
                foundClassLine = i;
                // Include the decorator line (i-1) if it starts with @
                if (i > 0 && lines[i - 1].TrimStart().StartsWith("@"))
                    structEnd = i + 1;
                else
                    structEnd = i + 1;
                // Don't break — keep looking for a class line if there's only a function/interface
            }
        }
        // If we found a real class/interface/enum/struct line, use it; else fall through
        if (foundClassLine >= 0) structEnd = Math.Max(structEnd, foundClassLine + 1);

        // ── Step 2: Find the target region ──
        var targetStart = -1;
        var targetEnd = -1;

        // Strategy A: anchor on the first long line of planOldString
        if (targetStart < 0 && !string.IsNullOrWhiteSpace(planOldString))
        {
            var anchor = planOldString.Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length >= 8);
            if (anchor != null)
            {
                for (var i = structEnd; i < lines.Length; i++)
                {
                    if (!lines[i].Contains(anchor, StringComparison.OrdinalIgnoreCase)) continue;
                    targetStart = Math.Max(structEnd, i - 10);
                    targetEnd = Math.Min(lines.Length, i + planOldString.Split('\n').Length + RadiusLines);
                    break;
                }
            }
        }

        // Strategy B: keyword scan on the change description
        if (targetStart < 0)
        {
            var keywords = AgentUtilities.ExtractMeaningfulKeywords(changeDesc.ToLowerInvariant())
                                         .Where(kw => kw.Length >= 5).ToList();
            if (keywords.Count > 0)
            {
                for (var i = structEnd; i < lines.Length; i++)
                {
                    if (!keywords.Any(kw => lines[i].Contains(kw, StringComparison.OrdinalIgnoreCase))) continue;
                    targetStart = Math.Max(structEnd, i - 20);
                    targetEnd = Math.Min(lines.Length, i + RadiusLines);
                    break;
                }
            }
        }

        // If no target found, include file from structEnd onwards
        if (targetStart < 0)
        {
            var hdr = lines.Take(structEnd).ToList();
            var body = string.Join('\n', lines.Skip(structEnd));
            if (body.Length > 8000)
                body = body[..8000] + $"\n... [lines {structEnd + 600}–{lines.Length} omitted]";
            return string.Join('\n', hdr) + "\n" + body;
        }

        // ── Step 3: Assemble ──
        var headLines = lines.Take(structEnd).ToList();
        var bodyLines = lines.Skip(structEnd).ToArray();

        // Map body-relative indices back to absolute
        var absStart = targetStart;
        var absEnd = targetEnd;

        var excerpt = string.Join('\n', lines.Skip(absStart).Take(absEnd - absStart));
        var header = string.Join('\n', headLines);

        var gapLines = absStart - structEnd;
        if (gapLines > 3)
            return header + $"\n... [lines {structEnd + 1}–{absStart} omitted]\n" + excerpt;
        else if (gapLines > 0)
            return header + "\n" + string.Join('\n', lines.Skip(structEnd).Take(gapLines)) + "\n" + excerpt;

        return header + "\n" + excerpt;
    }

    /// <summary>Use Roslyn to find a C# AST node and return its exact source text as oldString.</summary>
    private (string? oldStr, string? error) AstResolveEdit(string fullPath, string targetType, string targetName, bool returnTail = false)
    {
        if (!System.IO.File.Exists(fullPath))
            return (null, "File not found for AST edit");

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        var sourceText = System.IO.File.ReadAllText(fullPath, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(sourceText))
            return (null, "File is empty");

        // ── Non-C#: regex-based resolution (TypeScript, JS, etc.) ─────
        if (ext != ".cs")
        {
            if (!string.Equals(targetType, "method", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(targetType, "function", StringComparison.OrdinalIgnoreCase))
                return (null, $"For {ext} files, only targetType 'method'/'function' is supported. Got '{targetType}'.");

            // Must be a proper method declaration at line start (not inside a method body,
            // not an object property, not a variable assignment).
            // Pattern: line start + optional indent + optional modifiers + methodName(params) { or => {
            var pattern = $@"^\s*(?:(?:async|export)\s+)?(?:(?:public|private|protected|internal)\s+)?(?:(?:static|readonly)\s+)?(?:\w+\s+)?\b{Regex.Escape(targetName)}\s*\([^)]*\)\s*(?::\s*[^{{;]+)?\s*(?:{{|=>)";
            var match = Regex.Match(sourceText, pattern, RegexOptions.Multiline);
            if (!match.Success)
            {
                var hint = ext is ".html" or ".htm" or ".cshtml" or ".razor" or ".json" or ".css" or ".svg"
                    ? $" {ext} files don't contain methods — use oldString/newString format instead of targetType/targetName/newCode"
                    : "";
                return (null, $"Method/function '{targetName}' not found in {ext} file.{hint}");
            }

            var startIdx = match.Index;
            // Advance past any => to find the opening brace
            var openBraceIdx = sourceText.IndexOf('{', startIdx);
            if (openBraceIdx < 0)
                return (null, $"Method '{targetName}' has no opening brace");

            // Find the matching closing brace, skipping braces inside strings and comments
            var braceDepth = 0;
            var inSingleQuote = false;
            var inDoubleQuote = false;
            var inTemplate = false;
            var inLineComment = false;
            var inBlockComment = false;
            var endIdx = -1;
            for (var i = openBraceIdx; i < sourceText.Length; i++)
            {
                var c = sourceText[i];
                var p = i > 0 ? sourceText[i - 1] : '\0';

                // Track string/comment state
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

                if (c == '{') braceDepth++;
                else if (c == '}')
                {
                    braceDepth--;
                    if (braceDepth == 0) { endIdx = i; break; }
                }
            }
            if (endIdx < 0)
                return (null, $"Could not find closing brace for method '{targetName}'");

            var resolved = sourceText[startIdx..(endIdx + 1)].Replace("\r\n", "\n").Replace("\r", "\n");

            if (returnTail)
            {
                var lines = resolved.Split('\n');
                var tailCount = Math.Min(3, lines.Length);
                return (string.Join("\n", lines[^tailCount..]), null);
            }

            return (resolved, null);
        }

        // ── C#: Roslyn-based resolution ───────────────────────────────
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

    /// <summary>
    /// Detects the base indentation of the original AST node (method/class) and
    /// normalizes the LLM's newCode to match. If the LLM outputs flat code
    /// (1-3 spaces) but the file uses 8+ spaces, this shifts the entire block
    /// to the correct base while preserving relative internal nesting.
    /// </summary>
    private static string AutoIndentCode(string oldSource, string newCode)
    {
        var oldLines = oldSource.Split('\n');
        var firstRealLine = oldLines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (firstRealLine == null) return newCode;

        var baseIndent = Regex.Match(firstRealLine, @"^(\s*)").Value;
        if (string.IsNullOrEmpty(baseIndent)) return newCode;
        var baseIndentLen = baseIndent.Length;

        var newLines = newCode.Split('\n');
        if (newLines.Length <= 1) return newCode;

        // Find the minimum leading whitespace across all non-empty lines in newCode
        var nonEmpty = newLines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (nonEmpty.Count == 0) return newCode;
        var minNewIndent = nonEmpty.Min(l => Regex.Match(l, @"^(\s*)").Groups[1].Length);

        // If the new code's minimum indent is already >= the old code's base,
        // it's already properly indented — leave it alone
        if (minNewIndent >= baseIndentLen) return newCode;

        // Shift: strip the common minimum from newCode, then prepend the old base indent.
        // This preserves the relative indentation structure (try/catch nesting, etc.)
        var result = new List<string>();
        foreach (var line in newLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                result.Add(line);
            }
            else
            {
                // Strip up to minNewIndent chars of leading whitespace
                var trimmed = line.Length > minNewIndent
                    ? line.Substring(minNewIndent)
                    : line.TrimStart();
                result.Add(baseIndent + trimmed);
            }
        }
        var shifted = string.Join("\n", result);

        // If the shifted code has no relative nesting (all non-empty lines at the same
        // indent level), the LLM flattened the structure. Re-indent by brace depth.
        var shiftedLines = shifted.Split('\n');
        var distinctIndents = shiftedLines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => Regex.Match(l, @"^(\s*)").Groups[1].Length)
            .Distinct()
            .ToList();
        if (distinctIndents.Count <= 1)
            return ReindentByBraceDepth(shifted, baseIndent);

        return shifted;
    }

    /// <summary>
    /// Re-indents code by tracking brace depth, using baseIndent as the starting
    /// indentation. Accounts for strings and comments to avoid false brace matches.
    /// </summary>
    private static string ReindentByBraceDepth(string code, string baseIndent, int indentSize = 2)
    {
        var lines = code.Split('\n');
        var result = new List<string>();
        var depth = 0;
        var inSQ = false;
        var inDQ = false;
        var inTmpl = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                result.Add(line);
                continue;
            }

            // If line starts with closing brace, the content is at one less depth
            var effectiveDepth = trimmed[0] == '}' ? depth - 1 : depth;
            if (effectiveDepth < 0) effectiveDepth = 0;

            var indent = baseIndent + new string(' ', effectiveDepth * indentSize);
            result.Add(indent + trimmed);

            // Count braces on this line, tracking string/comment state
            for (var i = 0; i < trimmed.Length; i++)
            {
                var c = trimmed[i];
                var p = i > 0 ? trimmed[i - 1] : '\0';

                if (c == '\\') { i++; continue; }
                if (c == '\'' && !inDQ && !inTmpl) { inSQ = !inSQ; continue; }
                if (c == '"' && !inSQ && !inTmpl) { inDQ = !inDQ; continue; }
                if (c == '`' && !inSQ && !inDQ) { inTmpl = !inTmpl; continue; }
                if (c == '/' && p == '/' && !inSQ && !inDQ && !inTmpl) break;
                if (inSQ || inDQ || inTmpl) continue;
                if (c == '{') depth++;
                else if (c == '}') depth--;
            }
            if (depth < 0) depth = 0;
        }

        return string.Join("\n", result);
    }

    /// <summary>
    /// Makes a focused LLM call to resolve the exact edit for a single plan step.
    /// The LLM sees the real file content and outputs delimiter-format diff.
    /// </summary>
      private async Task<(string? oldStr, string? newStr, bool fullFile,
         string? fullContent, bool alreadyDone, string? error)>
         ResolveEditForStep(PlanStep step, string projectRoot, bool emitSse,
             CancellationToken ct,
             List<(string old, string @new, string error)>? history = null,
             string? explorationContext = null)
    {
        var relPath = step.File.Replace('\\', '/');
        var fullPath = Path.GetFullPath(
            Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));

        var fileExists = System.IO.File.Exists(fullPath);
        var fileContent = fileExists
            ? await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct)
            : string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"FILE: {relPath}");
        sb.AppendLine($"CHANGE REQUIRED: {step.Change}"); 

        // NEW: file-type hint so the LLM preserves structure
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        if (ext is ".html" or ".htm" or ".cshtml" or ".razor" or ".svg")
            sb.AppendLine("⚠ HTML FILE: preserve ALL relative indentation exactly — " +
                          "child elements must be indented MORE than their parent tag.");
        else if (ext is ".css" or ".scss" or ".sass")
            sb.AppendLine("⚠ CSS FILE: preserve ALL whitespace in property values exactly " +
                          "(e.g. '0px 1px' must stay as two tokens with a space).");
        else if (ext is ".ts" or ".tsx" or ".js" or ".jsx")
            sb.AppendLine("⚠ TS/JS FILE: preserve ALL indentation exactly — " +
                          "methods inside a class body MUST be indented, nested blocks " +
                          "must be indented relative to their parent. Copy the leading " +
                          "whitespace from oldString character-for-character into newString.");
        else if (ext == ".cs")
            sb.AppendLine("⚠ C# FILE: USE FORMAT C (targetType/targetName/newCode). This is the ONLY supported format for C# files. " +
                          "It uses AST parsing and bypasses text-matching entirely. Just name the method/class/property " +
                          "to replace or insert after and output its new body. " +
                          "Do NOT use oldString/newString for C# files — they WILL fail. " +
                          "To ADD a new method: use insertAfter:true with targetType=\"method\" and targetName of an existing method. " +
                          "Do NOT use targetType=\"class\" — that replaces the entire class. " +
                          "INDENTATION: newCode MUST include proper C# indentation — method signature at the class member level " +
                          "(typically 4 or 8 spaces), method body indented 4 spaces more, nested blocks (try/catch/using/if/for) " +
                          "each indented 4 spaces more than their parent. Copy the file's existing indentation pattern.");
        sb.AppendLine();

        //  Include exploration context if available — this is the most important
        //  signal the editor gets; the LLM should read it before the file content
        if (!string.IsNullOrWhiteSpace(explorationContext))
        {
            sb.AppendLine();
            sb.AppendLine("## EXPLORATION CONTEXT");
            sb.AppendLine("The following files were read during the exploration phase to " +
                          "understand exactly what needs to change. Use this context " +
                          "to locate the precise edit target:");
            var ctxPreview = explorationContext.Length > 14_000
                ? explorationContext[..14_000] + "\n... [exploration context truncated]"
                : explorationContext;
            sb.AppendLine(ctxPreview);
            sb.AppendLine();
        }

        if (!fileExists)
        {
            sb.AppendLine("FILE DOES NOT EXIST YET. Use <<<FULL_FILE>>> to create it with complete content.");
        }
        else
        {
            var lineCount = fileContent.Split('\n').Length;
            var isLarge = fileContent.Length > 3000 || lineCount > 80;
            if (isLarge)
            {
                sb.AppendLine($"FILE SIZE: {fileContent.Length} chars, {lineCount} lines. Showing relevant excerpt:");
                sb.AppendLine("```");
                sb.AppendLine(ExtractRelevantExcerpt(fileContent, step.Change, step.OldString));
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine("⚠ Do NOT output the full file. Use FORMAT C (targetType/targetName/newCode) for CODE files (.cs, .ts, .js, .tsx, .jsx). " +
                              "For HTML, CSS, JSON, and other markup/data files, use oldString/newString instead — those files don't have methods/classes to target with FORMAT C.");
            }
            else
            {
                sb.AppendLine("CURRENT FILE CONTENT:");
                sb.AppendLine("```");
                sb.AppendLine(fileContent);
                sb.AppendLine("```");
            }
        }

        // Always encourage small, focused oldStrings
        sb.AppendLine();
        sb.AppendLine("STRICT oldString SIZE LIMIT: MAXIMUM 10 lines. If you output more than 10 lines in oldString, the edit WILL fail.");
        sb.AppendLine("For CODE files (.cs, .ts, .js, .tsx, .jsx): use FORMAT C (targetType/targetName/newCode) to avoid text-matching issues.");
        sb.AppendLine("For HTML, CSS, JSON, and other markup/data files: use oldString/newString — those files don't have methods/classes for FORMAT C.");
        sb.AppendLine("To ADD a new method: use insertAfter:true with targetType=\"method\" and targetName of an existing method.");
        sb.AppendLine("To REPLACE a method: use FORMAT C (targetType=\"method\", targetName=\"MethodName\") without insertAfter.");
        sb.AppendLine("To APPEND to the end of the file: oldString = last 2-3 closing braces.");

        if (history?.Count > 0)
        {
            var hadTruncation = history.Any(h => h.error.Contains("truncated", StringComparison.OrdinalIgnoreCase));
            sb.AppendLine();
            sb.AppendLine($"⚠ PREVIOUS {history.Count} ATTEMPT(S) FAILED. Learn from each failure:");
            for (var i = 0; i < history.Count; i++)
            {
                var h = history[i];
                sb.AppendLine($"\n--- Attempt {i + 1} — Error: {h.error} ---");
                if (!string.IsNullOrWhiteSpace(h.old))
                {
                    sb.AppendLine($"  Your oldString was:");
                    sb.AppendLine($"  ```");
                    sb.AppendLine($"  {h.old[..Math.Min(400, h.old.Length)]}");
                    sb.AppendLine($"  ```");
                    // Show exact file lines at the fuzzy-match location for verbatim copying
                    var exactBlock = BuildExactMatchBlock(fileContent, h.old);
                    if (exactBlock != null)
                    {
                        sb.AppendLine($"  The EXACT lines from the file at the matched location (copy these VERBATIM for oldString):");
                        sb.AppendLine($"  ```");
                        sb.AppendLine($"  {exactBlock}");
                        sb.AppendLine($"  ```");
                    }
                    else
                    {
                        // Show what lines in the file were close (fallback)
                        var hint = BuildExactMatchHint(fileContent, h.old);
                        if (hint != null)
                        {
                            sb.AppendLine($"  These lines in the file are SIMILAR to what you wrote:");
                            sb.AppendLine($"  {hint}");
                        }
                    }
                }
            }
            sb.AppendLine();
            if (hadTruncation)
            {
                sb.AppendLine("Previous FULL_FILE response was too long and got truncated.");
                sb.AppendLine("Use <<<OLD>>> / <<<NEW>>> targeted edits instead — they are smaller and always fit.");
                sb.AppendLine("If multiple changes are needed, make one small edit at a time.");
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
        sb.AppendLine("Output the edit now:");

        if (emitSse)
            await SendSse(Response, "edit-resolve", new { }, ct);
        var (raw, _, _) = await CallLlmRawStreaming(EditResolveSystemPrompt, sb.ToString(), emitSse, ct, _infiniteTimeout, maxTokens: 8192);

        if (string.IsNullOrWhiteSpace(raw))
            return (null, null, false, null, false, "LLM returned empty response");

        string? oldStr = null, newStr = null;

        // Try JSON first
        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (m.Success) cleaned = m.Groups[1].Value.Trim();
            }
            var fb = cleaned.IndexOf('{');
            var lb = cleaned.LastIndexOf('}');
            if (fb >= 0 && lb > fb)
                cleaned = cleaned[fb..(lb + 1)];

            // Pre-process: escape literal newlines inside JSON string values so parsing doesn't choke
            // The LLM often outputs raw newlines instead of \n in string values
            cleaned = RepairJsonNewlines(cleaned);

            using var jDoc = JsonDocument.Parse(cleaned);
            var jRoot = jDoc.RootElement;

            // Already done
            if (jRoot.TryGetProperty("alreadyDone", out var ad) && ad.GetBoolean())
                return (null, null, false, null, true, null);

            // Full file (string or array)
            if (jRoot.TryGetProperty("fullFile", out var ff))
            {
                string? body = null;
                if (ff.ValueKind == JsonValueKind.String)
                    body = ff.GetString();
                else if (ff.ValueKind == JsonValueKind.Array)
                {
                    var lines = new List<string>();
                    foreach (var item in ff.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                            lines.Add(item.GetString() ?? "");
                    }
                    if (lines.Count > 0) body = string.Join("\n", lines);
                }
                if (!string.IsNullOrWhiteSpace(body))
                {
                    body = StripFullFileFence(body);
                    return (null, null, true, body, false, null);
                }
            }

            // AST-based edit (C# only): targetType + targetName + newCode
            if (jRoot.TryGetProperty("targetType", out var ttEl) &&
                jRoot.TryGetProperty("targetName", out var tnEl) &&
                jRoot.TryGetProperty("newCode", out var ncEl))
            {
                var targetType = ttEl.GetString();
                var targetName = tnEl.GetString();
                var newCodeStr = ncEl.ValueKind == JsonValueKind.String ? ncEl.GetString()
                    : ncEl.ValueKind == JsonValueKind.Array
                        ? string.Join("\n", ncEl.EnumerateArray().Select(e => e.GetString() ?? ""))
                        : null;

                if (!string.IsNullOrWhiteSpace(targetType) && !string.IsNullOrWhiteSpace(targetName) && newCodeStr != null)
                {
                    var insertAfter = jRoot.TryGetProperty("insertAfter", out var iaEl) && iaEl.GetBoolean();

                    if (insertAfter)
                    {
                        // INSERT mode: retrieve the FULL method text and append newCode after it.
                        // oldStr = the full existing method (guaranteed unique in the file via AST),
                        // newStr = the same method text + newline + indented newCode.
                        // This avoids fragile tail matching (closing braces match wrong locations).
                        var (fullStr, astErr) = AstResolveEdit(fullPath, targetType, targetName, returnTail: false);
                        if (fullStr == null)
                            return (null, null, false, null, false, astErr ?? "AST resolution failed");

                        var indented = AutoIndentCode(fullStr, newCodeStr);
                        newStr = fullStr + "\n" + indented;
                        return (fullStr, newStr, false, null, false, null);
                    }
                    else
                    {
                        // REPLACE mode: find the full node and replace it entirely
                        var (astOldStr, astErr) = AstResolveEdit(fullPath, targetType, targetName, returnTail: false);
                        if (astOldStr != null)
                        {
                            var indented = AutoIndentCode(astOldStr, newCodeStr);
                            return (astOldStr, indented, false, null, false, null);
                        }
                        return (null, null, false, null, false, astErr ?? "AST resolution failed");
                    }
                }
            }

            // Targeted edit — support both string and array formats
            // String:  "oldString": "line1\nline2"
            // Array:   "oldString": ["line1", "line2"]
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
            }

            if (!string.IsNullOrWhiteSpace(oldStr))
                return (oldStr, newStr ?? "", false, null, false, null);

            return (null, null, false, null, false, "JSON has no oldString, targetType, fullFile, or alreadyDone field");
        }
        catch
        {
            // Fallback: legacy delimiter format
            if (raw.Contains(D_DONE, StringComparison.OrdinalIgnoreCase))
                return (null, null, false, null, true, null);

            var ffS = raw.IndexOf(D_FULL, StringComparison.OrdinalIgnoreCase);
            var ffE = raw.IndexOf(D_FULL_END, StringComparison.OrdinalIgnoreCase);
            if (ffS >= 0)
            {
                if (ffE < ffS)
                    return (null, null, false, null, false, "Response truncated — FULL_FILE not closed.");
                var body = raw[(ffS + D_FULL.Length)..ffE];
                body = StripFullFileFence(body);
                return (null, null, true, body, false, null);
            }

            // Regex fallback: try to extract oldString/newString from malformed JSON
            // where unescaped quotes break JSON parsing (common with HTML attributes).
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
                    return (oldStr, newStr ?? "", false, null, false, null);
                }
            }

            // Also try non-array (string value) format
            var osStrMatch = Regex.Match(raw,
                @"""oldString""\s*:\s*""([\s\S]*?)""\s*,\s*""newString""\s*:\s*""([\s\S]*?)""",
                RegexOptions.IgnoreCase);
            if (osStrMatch.Success)
            {
                oldStr = osStrMatch.Groups[1].Value;
                newStr = osStrMatch.Groups[2].Value;
                return (oldStr, newStr ?? "", false, null, false, null);
            }

            // FORMAT C fallback: extract targetType/targetName/newCode from malformed JSON
            var ttMatch = Regex.Match(raw,
                @"""targetType""\s*:\s*""(\w+)""", RegexOptions.IgnoreCase);
            var tnMatch = Regex.Match(raw,
                @"""targetName""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
            if (ttMatch.Success && tnMatch.Success)
            {
                var tt = ttMatch.Groups[1].Value;
                var tn = tnMatch.Groups[2].Value;
                var ncIdx = raw.IndexOf("\"newCode\"", StringComparison.OrdinalIgnoreCase);
                if (ncIdx >= 0)
                {
                    var afterKey = raw[(ncIdx + "\"newCode\"".Length)..].TrimStart();
                    if (afterKey.StartsWith(":"))
                        afterKey = afterKey[1..].TrimStart();

                    string? newCodeStr = null;
                    if (afterKey.StartsWith("["))
                    {
                        // Array format: find matching ]
                        var depth = 0;
                        for (var i = 0; i < afterKey.Length; i++)
                        {
                            if (afterKey[i] == '[') depth++;
                            else if (afterKey[i] == ']') { depth--; if (depth == 0) { var lines = ExtractQuotedStrings(afterKey[1..i]); if (lines.Count > 0) newCodeStr = string.Join("\n", lines); break; } }
                        }
                    }
                    else if (afterKey.StartsWith("\""))
                    {
                        // String format: find closing " before , or } or \n
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
                    }

                    if (!string.IsNullOrWhiteSpace(tt) && !string.IsNullOrWhiteSpace(tn) && newCodeStr != null)
                    {
                        var insertAfter = Regex.Match(raw, @"""insertAfter""\s*:\s*true", RegexOptions.IgnoreCase).Success;
                        if (insertAfter)
                        {
                            var (fullStr, astErr) = AstResolveEdit(fullPath, tt, tn, returnTail: false);
                            if (fullStr != null) { var indented = AutoIndentCode(fullStr, newCodeStr); newStr = fullStr + "\n" + indented; return (fullStr, newStr, false, null, false, null); }
                        }
                        else
                        {
                            var (astOldStr, astErr) = AstResolveEdit(fullPath, tt, tn, returnTail: false);
                            if (astOldStr != null) { var indented = AutoIndentCode(astOldStr, newCodeStr); return (astOldStr, indented, false, null, false, null); }
                        }
                    }
                }
            }

            var oS = raw.IndexOf(D_OLD, StringComparison.OrdinalIgnoreCase);
            var oE = raw.IndexOf(D_OLD_END, StringComparison.OrdinalIgnoreCase);
            var nS = raw.IndexOf(D_NEW, StringComparison.OrdinalIgnoreCase);
            var nE = raw.IndexOf(D_NEW_END, StringComparison.OrdinalIgnoreCase);

            if (oS < 0)
                return (null, null, false, null, false, "No edit markers found — check LLM output");
            if (oE < 0 || nS < 0 || nE < 0)
                return (null, null, false, null, false, "Response truncated — markers not closed");

            oldStr = raw[(oS + D_OLD.Length)..oE].TrimStart('\r', '\n').TrimEnd('\r', '\n');
            newStr = raw[(nS + D_NEW.Length)..nE].TrimStart('\r', '\n').TrimEnd('\r', '\n');

            if (string.IsNullOrWhiteSpace(oldStr))
                return (null, null, false, null, false, "OLD section is empty");

            return (oldStr, newStr, false, null, false, null);
        }
    }

    private enum PreEditVerdict { Proceed, AlreadyDone, Irrelevant }

    /// <summary>
    /// Quick pre-edit validation: checks if the edit is still relevant before
    /// calling the LLM. Verifies oldString still exists, newString isn't already
    /// present, and the edit makes sense given the current file content.
    /// </summary>
    private static (PreEditVerdict verdict, string reason) PreEditValidation(string fileContent, PlanStep step)
    {
        if (string.IsNullOrWhiteSpace(fileContent))
            return (PreEditVerdict.Proceed, "");

        var content = AgentUtilities.NormalizeLineEndings(fileContent);

        // Already done: newString already exists in the file
        if (!string.IsNullOrWhiteSpace(step.NewString))
        {
            var newStr = AgentUtilities.NormalizeLineEndings(step.NewString);
            if (content.Contains(newStr, StringComparison.Ordinal))
                return (PreEditVerdict.AlreadyDone, "code already present in file");
        }

        // Gone stale: oldString no longer exists in the file
        if (!string.IsNullOrWhiteSpace(step.OldString))
        {
            var oldStr = AgentUtilities.NormalizeLineEndings(step.OldString);
            if (!content.Contains(oldStr, StringComparison.Ordinal))
            {
                // Try trimmed (trailing whitespace removed)
                var trimOld = string.Join("\n", oldStr.Split('\n').Select(l => l.TrimEnd()));
                var trimFile = string.Join("\n", content.Split('\n').Select(l => l.TrimEnd()));
                if (!trimFile.Contains(trimOld, StringComparison.Ordinal))
                    return (PreEditVerdict.Irrelevant, "oldString not found — context changed or already applied");
            }
        }

        return (PreEditVerdict.Proceed, "");
    }

    private static readonly HashSet<string> _builtInTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "string", "int", "long", "double", "float", "decimal", "bool", "char", "byte",
        "short", "uint", "ulong", "ushort", "sbyte", "object", "dynamic", "void",
        "Task", "ValueTask", "Task<T>", "ValueTask<T>", "IActionResult", "ActionResult",
        "OkResult", "OkObjectResult", "BadRequestResult", "BadRequestObjectResult",
        "NotFoundResult", "StatusCodeResult", "ObjectResult", "RedirectResult",
        "FileResult", "ContentResult", "JsonResult", "IEnumerable<T>", "IQueryable<T>",
        "List<T>", "Dictionary<TKey,TValue>", "HashSet<T>", "IList<T>", "ICollection<T>",
        "HttpResponse", "HttpRequest", "CancellationToken", "DateTime", "TimeSpan",
        "Guid", "Uri", "Exception", "InvalidOperationException", "ArgumentNullException",
        "ArgumentException", "NotSupportedException", "MySqlConnection", "MySqlCommand",
        "MySqlDataReader", "MySqlParameter", "MySqlDbType", "DbConnection", "DbCommand",
        "HttpClient", "HttpContent", "HttpMethod", "HttpStatusCode"
    };

    /// <summary>
    /// After an edit is applied, scans the new code for referenced types that
    /// don't exist in the file. Appends minimal class definitions at the bottom.
    /// </summary>
    private static List<string> ScanMissingTypes(string fullFileContent, string newCode)
    {
        // Existing type declarations in the file
        var declaredTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(fullFileContent,
            @"\b(class|record|struct|enum|interface)\s+([A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Multiline))
            declaredTypes.Add(m.Groups[2].Value);

        // Namespaces in using directives (we won't flag types from known namespaces)
        var usingNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(fullFileContent, @"using\s+([A-Za-z_.][A-Za-z0-9_.]*)\s*;"))
            usingNamespaces.Add(m.Groups[1].Value);

        // Extract all PascalCase identifiers from the new code
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(newCode, @"\b[A-Z][a-zA-Z0-9_]+\b"))
        {
            var name = m.Value;
            if (name.Length < 3) continue;
            if (_builtInTypes.Contains(name)) continue;
            if (declaredTypes.Contains(name)) continue;
            // Skip anything that starts with a known namespace prefix
            if (usingNamespaces.Any(ns => name.StartsWith(ns.Split('.').Last(), StringComparison.OrdinalIgnoreCase)))
                continue;
            candidates.Add(name);
        }

        // Heuristic: only flag types that follow heuristics for DTO/request/response
        // or appear after [FromBody] or as a generic argument
        var result = new List<string>();
        foreach (var c in candidates)
        {
            // Pattern 1: [FromBody] TypeName
            var fbPattern = @"\[FromBody\]\s+\b" + Regex.Escape(c) + @"\b";
            if (Regex.IsMatch(newCode, fbPattern))
            { result.Add(c); continue; }

            // Pattern 2: ends with Request / Response / Dto / Model / Result / Args / Data
            if (c.EndsWith("Request", StringComparison.OrdinalIgnoreCase) ||
                c.EndsWith("Response", StringComparison.OrdinalIgnoreCase) ||
                c.EndsWith("Dto", StringComparison.OrdinalIgnoreCase) ||
                c.EndsWith("Model", StringComparison.OrdinalIgnoreCase) ||
                c.EndsWith("Result", StringComparison.OrdinalIgnoreCase))
            { result.Add(c); continue; }

            // Pattern 3: used as generic type argument <TypeName>
            var genericPattern = @"<" + Regex.Escape(c) + @"\s*>";
            if (Regex.IsMatch(newCode, genericPattern))
            { result.Add(c); continue; }
        }

        return result.Distinct().ToList();
    }

 
/// <summary>
/// Holds the result of the per-step exploration loop: an enriched step
/// (with a precise change description), accumulated file context, and
/// metadata about what was discovered.
/// </summary>
private sealed class StepExplorationResult
    {
        public PlanStep EnrichedStep { get; init; } = new();
        public string ExplorationContext { get; init; } = "";
        public List<string> FilesRead { get; init; } = new();
        public string RefinedChange { get; init; } = "";
        public string? TargetSymbol { get; init; }
        public string? EstimatedLineRange { get; init; }
        public int Confidence { get; init; }
        public int RoundsCompleted { get; init; }
    }

    /// <summary>
    /// Lightweight DTO for parsing the exploration LLM's JSON response.
    /// </summary>
    private sealed class StepExplorationResponse
    {
        public bool Ready { get; init; }
        public List<string> FilesToRead { get; init; } = new();
        public string? RefinedChange { get; init; }
        public string? TargetSymbol { get; init; }
        public string? LineRange { get; init; }
        public int Confidence { get; init; }
    }

    /// <summary>
    /// Runs an iterative exploration loop for one plan step before the edit is
    /// applied. Mimics OpenCode's behavior: read the target file, ask the LLM
    /// what related files are needed, read them, repeat until the LLM is
    /// confident it can describe the change precisely. Returns an enriched step
    /// (refined change description + optional AST-resolved oldString) and the
    /// accumulated multi-file context to pass to ResolveEditForStep.
    /// </summary>
    private async Task<StepExplorationResult> RunStepExplorationLoop(
        PlanStep step,
        string projectRoot,
        string originalPrompt,
        AgentPlan? fullPlan,
        int planItemIndex,
        bool emitSse,
        CancellationToken ct,
        string? cardId = null)
    {
        // ── Guard: skip if the plan already has precise edit info ─────────
        if (!string.IsNullOrWhiteSpace(step.OldString) &&
            !string.IsNullOrWhiteSpace(step.NewString))
        {
            return new StepExplorationResult
            {
                EnrichedStep = step,
                FilesRead = new List<string>(),
                RefinedChange = step.Change,
                Confidence = 100
            };
        }

        const int MaxRounds = 4;
        const int MaxContextChars = 22_000;
        const int ConfidenceThreshold = 80; // stop early once the LLM is this confident

        var relPath = step.File.Replace('\\', '/');
        var fullPath = Path.GetFullPath(
            Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));

        var ctx = new StringBuilder();
        var filesRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var refinedChange = step.Change;
        string? targetSymbol = null;
        string? lineRange = null;
        var confidence = 0;
        var roundsCompleted = 0;

        await EmitLog(emitSse, "info", $"🔍 Exploring: {relPath}", ct: ct);

        // Signal "exploring" to the frontend immediately so the step card updates
        if (emitSse)
            await SendSse(Response, "step", new
            {
                index = planItemIndex,
                type = "edit",
                status = "exploring",
                path = relPath,
                description = step.Change,
                planItemIndex
            }, ct);

        await PersistStepStatusAsync(cardId, planItemIndex, "exploring", emitSse, ct);

        // ── Step 1: Always read the target file first ─────────────────────
        if (System.IO.File.Exists(fullPath) &&
            AgentUtilities.IsPathUnderRoot(fullPath, projectRoot))
        {
            var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
            // For large files use the relevance-focused excerpt so context is targeted
            var excerpt = content.Length > 5_000
                ? ExtractRelevantExcerpt(content, step.Change, step.OldString)
                : content;

            ctx.AppendLine($"### TARGET FILE: {relPath}  ({content.Length:N0} chars total)");
            ctx.AppendLine("```");
            ctx.AppendLine(excerpt);
            ctx.AppendLine("```");
            ctx.AppendLine();
            filesRead.Add(relPath);
            await EmitLog(emitSse, "info", $"  📄 {relPath}", ct: ct);
        }

        // ── Step 2: Iterative exploration rounds ─────────────────────────
        for (var round = 0; round < MaxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            roundsCompleted = round + 1;

            if (emitSse)
                await SendSse(Response, "step-explore", new
                {
                    planItemIndex,
                    round,
                    filesRead = filesRead.ToList(),
                    message = $"Exploration round {round + 1}/{MaxRounds}"
                }, ct);

            var (raw, _, _) = await CallLlmRaw(
                BuildStepExplorationSystemPrompt(),
                BuildStepExplorationPrompt(
                    step, originalPrompt, fullPlan, planItemIndex,
                    ctx.ToString(), filesRead, round),
                ct, TimeSpan.FromSeconds(35), maxTokens: 1024);

            if (string.IsNullOrWhiteSpace(raw)) break;

            var parsed = ParseStepExplorationResponse(raw);

            // Capture refined description whenever the LLM produces one
            if (!string.IsNullOrWhiteSpace(parsed.RefinedChange))
            {
                refinedChange = parsed.RefinedChange;
                targetSymbol = parsed.TargetSymbol;
                lineRange = parsed.LineRange;
                confidence = parsed.Confidence;
            }

            // Stop early when the LLM is confident or has nothing new to request
            if (parsed.Ready || parsed.Confidence >= ConfidenceThreshold)
            {
                await EmitLog(emitSse, "info",
                    $"  ✓ Ready — round {round + 1}, confidence {parsed.Confidence}%", ct: ct);
                break;
            }
            if (parsed.FilesToRead.Count == 0)
            {
                await EmitLog(emitSse, "info",
                    $"  ✓ No more files requested (round {round + 1})", ct: ct);
                break;
            }

            // ── Read the files the LLM requested (max 3 per round) ───────
            var newlyRead = 0;
            foreach (var requested in parsed.FilesToRead.Take(3))
            {
                if (filesRead.Contains(requested)) continue;

                var fp = Path.GetFullPath(
                    Path.Combine(projectRoot, requested.Replace('/', Path.DirectorySeparatorChar)));

                if (!System.IO.File.Exists(fp) ||
                    !AgentUtilities.IsPathUnderRoot(fp, projectRoot))
                {
                    await EmitLog(emitSse, "warn",
                        $"  ⚠ Not found: {requested}", ct: ct);
                    continue;
                }

                var fc = await System.IO.File.ReadAllTextAsync(fp, Encoding.UTF8, ct);
                var excerpt = fc.Length > 3_500
                    ? ExtractRelevantExcerpt(fc, step.Change, step.OldString)
                    : fc;

                // Guard total context size so we don't overflow the LLM window
                if (ctx.Length + excerpt.Length > MaxContextChars)
                {
                    var budget = MaxContextChars - ctx.Length;
                    if (budget < 400)
                    {
                        await EmitLog(emitSse, "info",
                            "  Context budget exhausted", ct: ct);
                        goto ExplorationComplete;
                    }
                    excerpt = excerpt[..budget] + "\n... [context limit]";
                }

                ctx.AppendLine($"### {requested}");
                ctx.AppendLine("```");
                ctx.AppendLine(excerpt);
                ctx.AppendLine("```");
                ctx.AppendLine();
                filesRead.Add(requested);
                newlyRead++;
                await EmitLog(emitSse, "info", $"  📄 {requested}", ct: ct);
            }

            if (newlyRead == 0) break;
        }

    ExplorationComplete:

        // ── Step 3: If a specific symbol was identified, resolve it via ───
        //    AST so the editor gets the exact current source as oldString.
        string? astOldStringHint = null;
        if (!string.IsNullOrWhiteSpace(targetSymbol) &&
            System.IO.File.Exists(fullPath))
        {
            var ext = Path.GetExtension(relPath).ToLowerInvariant();
            // Supported for .cs (Roslyn) and .ts/.js (regex)
            var supportedExt = ext is ".cs" or ".ts" or ".js" or ".tsx" or ".jsx";
            if (supportedExt)
            {
                var (astOld, astErr) = AstResolveEdit(fullPath, "method", targetSymbol);
                if (astOld != null)
                {
                    astOldStringHint = astOld;
                    await EmitLog(emitSse, "info",
                        $"  🎯 AST resolved '{targetSymbol}' " +
                        $"({astOld.Split('\n').Length} lines)", ct: ct);
                }
                else if (!string.IsNullOrWhiteSpace(astErr))
                {
                    await EmitLog(emitSse, "info",
                        $"  AST hint failed ({astErr}) — will use text matching", ct: ct);
                }
            }
        }

        // ── Step 4: Build the enriched step ──────────────────────────────
        var enrichedStep = new PlanStep
        {
            File = step.File,
            Change = string.IsNullOrWhiteSpace(refinedChange) ? step.Change : refinedChange,
            Priority = step.Priority,
            OldString = astOldStringHint ?? step.OldString ?? "",
            NewString = step.NewString ?? ""
        };

        // ── Step 5: Persist all exploration details to boarddata ──────────
        await PersistStepExplorationAsync(cardId, planItemIndex, new
        {
            status = "ready",
            filesRead = filesRead.ToList(),
            rounds = roundsCompleted,
            refinedChange,
            originalChange = step.Change,
            targetSymbol,
            estimatedLineRange = lineRange,
            confidence,
            astResolved = astOldStringHint != null
        }, emitSse, ct);

        await EmitLog(emitSse, "info",
            $"  ✅ Exploration done — {filesRead.Count} file(s), confidence {confidence}%",
            ct: ct);

        return new StepExplorationResult
        {
            EnrichedStep = enrichedStep,
            ExplorationContext = ctx.ToString(),
            FilesRead = filesRead.ToList(),
            RefinedChange = refinedChange,
            TargetSymbol = targetSymbol,
            EstimatedLineRange = lineRange,
            Confidence = confidence,
            RoundsCompleted = roundsCompleted
        };
    }

    // ── Exploration prompt builders ───────────────────────────────────────

    private static string BuildStepExplorationSystemPrompt() =>
        "You are a surgical code exploration agent. Before a code change is applied, " +
        "your job is to understand exactly what needs to change and precisely where.\n\n" +
        "You are given the original task, the full plan (so you understand what came before " +
        "and after), the specific step, and the files already read.\n\n" +
        "Output ONLY valid JSON — exactly one of these two forms:\n\n" +
        "NEED MORE CONTEXT:\n" +
        "{\n" +
        "  \"ready\": false,\n" +
        "  \"filesToRead\": [\"relative/path/file.ext\"],\n" +
        "  \"reasoning\": \"I need to see X to understand how Y is wired\"\n" +
        "}\n\n" +
        "READY TO EDIT:\n" +
        "{\n" +
        "  \"ready\": true,\n" +
        "  \"refinedChange\": \"In [MethodName] (around line N): replace [exact old code description] " +
        "with [exact new code description]. [Full explanation of the change]\",\n" +
        "  \"targetSymbol\": \"methodOrFunctionName\",\n" +
        "  \"estimatedLineRange\": \"~150-175\",\n" +
        "  \"confidence\": 90\n" +
        "}\n\n" +
        "RULES:\n" +
        "1. filesToRead: only files DIRECTLY needed for THIS step — no tangential reads\n" +
        "2. Never request a file already listed under 'files already read'\n" +
        "3. Max 3 files per request; prefer the most likely to contain the relevant code\n" +
        "4. refinedChange MUST: name the exact method/function/component, describe the " +
        "exact code block being replaced, describe the replacement code — zero ambiguity\n" +
        "5. targetSymbol: the identifier of the specific method/function/class being changed\n" +
        "6. confidence 0-100: if < 70, request more files rather than guessing\n" +
        "7. If the target file already has enough context (small file, obvious location), " +
        "go ready=true on round 1 with a precise refinedChange";

    private static string BuildStepExplorationPrompt(
        PlanStep step,
        string originalPrompt,
        AgentPlan? fullPlan,
        int stepIdx,
        string explorationContext,
        HashSet<string> alreadyRead,
        int round)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## ORIGINAL TASK");
        sb.AppendLine(originalPrompt);
        sb.AppendLine();

        // Full plan gives the LLM critical "what came before / what comes next" context
        if (fullPlan?.Plan?.Count > 0)
        {
            sb.AppendLine("## FULL PLAN");
            for (var i = 0; i < fullPlan.Plan.Count; i++)
            {
                var p = fullPlan.Plan[i];
                var marker = i == stepIdx ? "→ [CURRENT]"
                           : i < stepIdx ? "✓ [DONE]"
                                         : "  [PENDING]";
                sb.AppendLine($"  {marker} Step {i + 1}: {p.File} — {p.Change}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## STEP TO IMPLEMENT");
        sb.AppendLine($"File:   {step.File}");
        sb.AppendLine($"Change: {step.Change}");
        sb.AppendLine();

        sb.AppendLine("## FILES ALREADY READ");
        if (alreadyRead.Count > 0)
            foreach (var f in alreadyRead) sb.AppendLine($"  - {f}");
        else
            sb.AppendLine("  (none yet)");
        sb.AppendLine();

        sb.AppendLine("## FILE CONTENTS");
        sb.AppendLine(string.IsNullOrWhiteSpace(explorationContext)
            ? "(no files read yet)"
            : explorationContext);

        sb.AppendLine();
        if (round == 0)
        {
            sb.AppendLine("ROUND 1 — you have read the target file.");
            sb.AppendLine("Question: can you precisely describe the edit from this file alone?");
            sb.AppendLine("  YES → ready=true + detailed refinedChange + targetSymbol");
            sb.AppendLine("  NO  → ready=false + list the specific related files needed " +
                          "(services, interfaces, imports, component class, etc.)");
        }
        else
        {
            sb.AppendLine($"ROUND {round + 1} — you have read {alreadyRead.Count} file(s).");
            sb.AppendLine("Do you now have enough context to produce an unambiguous edit description?");
            sb.AppendLine("  YES → ready=true + refinedChange naming the exact method and code change");
            sb.AppendLine("  NO  → ready=false + list remaining files (max 3, must not repeat)");
        }

        return sb.ToString();
    }

    private static StepExplorationResponse ParseStepExplorationResponse(string raw)
    {
        var empty = new StepExplorationResponse { FilesToRead = new List<string>() };
        if (string.IsNullOrWhiteSpace(raw)) return empty;
        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```",
                    RegexOptions.IgnoreCase);
                if (m.Success) cleaned = m.Groups[1].Value.Trim();
            }
            var fb = cleaned.IndexOf('{'); var lb = cleaned.LastIndexOf('}');
            if (fb >= 0 && lb > fb) cleaned = cleaned[fb..(lb + 1)];

            using var doc = JsonDocument.Parse(cleaned,
                new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;

            var ready = root.TryGetProperty("ready", out var rEl) && rEl.GetBoolean();

            var files = new List<string>();
            if (root.TryGetProperty("filesToRead", out var fArr) &&
                fArr.ValueKind == JsonValueKind.Array)
                foreach (var f in fArr.EnumerateArray())
                {
                    var s = f.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        files.Add(s.Replace('\\', '/'));
                }

            var refined = root.TryGetProperty("refinedChange",
                out var rcEl) ? rcEl.GetString() : null;
            var symbol = root.TryGetProperty("targetSymbol",
                out var tsEl) ? tsEl.GetString() : null;
            var range = root.TryGetProperty("estimatedLineRange",
                out var lrEl) ? lrEl.GetString() : null;
            var conf = root.TryGetProperty("confidence", out var cEl) &&
                          cEl.ValueKind == JsonValueKind.Number ? cEl.GetInt32() : 0;

            return new StepExplorationResponse
            {
                Ready = ready,
                FilesToRead = files,
                RefinedChange = refined,
                TargetSymbol = symbol,
                LineRange = range,
                Confidence = conf
            };
        }
        catch { return empty; }
    }

    // ── Boarddata persistence helpers ─────────────────────────────────────

    /// <summary>
    /// Writes full exploration data (filesRead, refinedChange, targetSymbol, etc.)
    /// to the boarddata step object so the frontend can display exploration details.
    /// </summary>
    private async Task PersistStepExplorationAsync(
        string? cardId, int planItemIndex, object explorationData,
        bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardId) || planItemIndex < 0) return;
        try
        {
            var raw = await _boardData.LoadRawAsync();
            if (string.IsNullOrWhiteSpace(raw)) return;
            using var jsonDoc = JsonDocument.Parse(raw);
            var root = JsonNode.Parse(jsonDoc.RootElement.GetRawText())?.AsObject();
            if (root == null) return;

            foreach (var column in new[] { "todo", "doing", "done", "selfImproving" })
            {
                if (!root.TryGetPropertyValue(column, out var colNode) ||
                    colNode is not JsonArray colItems) continue;

                foreach (var item in colItems)
                {
                    if (item is not JsonObject card ||
                        card["id"]?.GetValue<string>() != cardId) continue;
                    if (card["_plan"] is not JsonObject plan ||
                        plan["items"] is not JsonArray items) continue;

                    var target = items.FirstOrDefault(i =>
                        i is JsonObject o &&
                        o["index"]?.GetValue<int>() == planItemIndex);
                    if (target is not JsonObject stepObj) continue;

                    stepObj["exploration"] = JsonNode.Parse(
                        JsonSerializer.Serialize(explorationData));
                    stepObj["status"] = "ready";

                    await _boardData.SaveRawAsync(
                        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    if (emitSse)
                        await SendSse(Response, "refresh", new
                        {
                            target = "boarddata",
                            reason = "step-exploration-complete",
                            cardId,
                            planItemIndex
                        }, ct);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to persist step exploration",
                new { cardId, planItemIndex, error = ex.Message });
        }
    }

    /// <summary>
    /// Lightweight status-only update for a boarddata step (e.g. "exploring",
    /// "applying") without rewriting the full exploration data blob.
    /// </summary>
    private async Task PersistStepStatusAsync(
        string? cardId, int planItemIndex, string status,
        bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardId) || planItemIndex < 0) return;
        try
        {
            var raw = await _boardData.LoadRawAsync();
            if (string.IsNullOrWhiteSpace(raw)) return;
            using var jsonDoc = JsonDocument.Parse(raw);
            var root = JsonNode.Parse(jsonDoc.RootElement.GetRawText())?.AsObject();
            if (root == null) return;

            foreach (var column in new[] { "todo", "doing", "done", "selfImproving" })
            {
                if (!root.TryGetPropertyValue(column, out var colNode) ||
                    colNode is not JsonArray colItems) continue;
                foreach (var item in colItems)
                {
                    if (item is not JsonObject card ||
                        card["id"]?.GetValue<string>() != cardId) continue;
                    if (card["_plan"] is not JsonObject plan ||
                        plan["items"] is not JsonArray items) continue;
                    var target = items.FirstOrDefault(i =>
                        i is JsonObject o && o["index"]?.GetValue<int>() == planItemIndex);
                    if (target is not JsonObject stepObj) continue;
                    stepObj["status"] = status;
                    await _boardData.SaveRawAsync(
                        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    if (emitSse)
                        await SendSse(Response, "refresh", new
                        {
                            target = "boarddata",
                            reason = "step-status-update",
                            cardId,
                            planItemIndex,
                            status
                        }, ct);
                    return;
                }
            }
        }
        catch { /* non-critical */ }
    }


    private async Task<int> ResolveAndApplyEdit(
        PlanStep step,
        string projectRoot,
        bool emitSse,
        CancellationToken ct,
        List<object> allResults,
        int stepIndex,
        string? prompt = null,   // NEW — original task prompt
        AgentPlan? plan = null,   // NEW — full plan for context
        int planItemIndex = -1,
        string? cardId = null)
    {
        var relPath = step.File.Replace('\\', '/');
        var fullPath = Path.GetFullPath(
            Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));

        await EmitLog(emitSse, "info",
            $"▶ Resolving: {relPath} — {step.Change}", ct: ct);

        if (emitSse)
            await SendSse(Response, "step", new
            {
                index = stepIndex,
                type = "edit",
                status = "running",
                path = relPath,
                description = step.Change,
                planItemIndex
            }, ct);

        // ── Pre-edit validation ───────────────────────────────────────────
        if (System.IO.File.Exists(fullPath))
        {
            var currentContent = await System.IO.File.ReadAllTextAsync(
                fullPath, Encoding.UTF8, ct);
            var (verdict, reason) = PreEditValidation(currentContent, step);
            if (verdict == PreEditVerdict.AlreadyDone)
            {
                await EmitLog(emitSse, "info",
                    $"✓ Already done: {relPath} — {reason}", ct: ct);
                var r = new Dictionary<string, object?>
                {
                    ["index"] = stepIndex,
                    ["type"] = "edit",
                    ["status"] = "skipped",
                    ["path"] = relPath,
                    ["reason"] = reason,
                    ["planItemIndex"] = planItemIndex
                };
                if (emitSse) await SendSse(Response, "step", r, ct);
                allResults.Add(r);
                await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct);
                return stepIndex + 1;
            }
            if (verdict == PreEditVerdict.Irrelevant)
            {
                await EmitLog(emitSse, "warn",
                    $"⏭ Skipping {relPath} — {reason}", ct: ct);
                var r = new Dictionary<string, object?>
                {
                    ["index"] = stepIndex,
                    ["type"] = "edit",
                    ["status"] = "skipped",
                    ["path"] = relPath,
                    ["reason"] = reason,
                    ["planItemIndex"] = planItemIndex
                };
                if (emitSse) await SendSse(Response, "step", r, ct);
                allResults.Add(r);
                return stepIndex + 1;
            }
        }

        // ── EXPLORATION LOOP (NEW) ────────────────────────────────────────
        // Build rich context iteratively before attempting the edit.
        // The enriched step has a precise change description; if a target
        // symbol was found via AST its source becomes the planOldStr hint.
        var exploration = await RunStepExplorationLoop(
            step, projectRoot,
            prompt ?? step.Change,   // fall back to the step's own description
            plan, planItemIndex, emitSse, ct, cardId);

        step = exploration.EnrichedStep;
        var explorationContext = exploration.ExplorationContext;

        // Signal "applying" now that exploration is complete
        await PersistStepStatusAsync(cardId, planItemIndex, "applying", emitSse, ct);

        // ── Resolve + apply loop (existing logic, now fed richer context) ─
        var history = new List<(string old, string @new, string error)>();
        var planOldStr = step.OldString;   // may be AST-resolved by exploration
        var planNewStr = step.NewString;
        var planOldTried = false;
        var stuckCount = 0;
        var resolveStuckCount = 0;
        var lastResolveError = "";
        var lastOld = "";
        const int MaxAttempts = 20;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            string? oldStr = null, newStr = null, resolveError = null;
            bool fullFile = false, alreadyDone = false;
            string? fullContent = null;

            // Attempt 0: use plan-provided (or AST-resolved) oldString directly
            if (attempt == 0 && !string.IsNullOrWhiteSpace(planOldStr) && !planOldTried)
            {
                planOldTried = true;
                oldStr = AgentUtilities.NormalizeLineEndings(planOldStr);
                newStr = AgentUtilities.NormalizeLineEndings(planNewStr ?? "");
                await EmitLog(emitSse, "info",
                    $"Using plan-provided edit for {relPath}", ct: ct);
            }
            else
            {
                if (attempt > 0)
                    await EmitLog(emitSse, "warn",
                        $"Resolve retry {attempt + 1} for {relPath}",
                        new { step, projectRoot }, ct: ct);

                // Pass the rich exploration context so the edit-resolve LLM
                // has far better information than just the file excerpt
                (oldStr, newStr, fullFile, fullContent, alreadyDone, resolveError) =
                    await ResolveEditForStep(
                        step, projectRoot, emitSse, ct, history,
                        explorationContext: explorationContext);
            }

            if (resolveError != null)
            {
                await EmitLog(emitSse, "warn",
                    $"Resolve attempt {attempt + 1}/{MaxAttempts}: {resolveError}",
                    new { resolveError, fullContent }, ct: ct);
                history.Add((step.OldString ?? "", step.NewString ?? "", resolveError));

                if (resolveError == lastResolveError) resolveStuckCount++;
                else { resolveStuckCount = 0; lastResolveError = resolveError; }
                if (resolveStuckCount >= 3)
                {
                    await EmitLog(emitSse, "error",
                        $"LLM keeps failing to produce valid edit output — aborting {relPath}",
                        ct: ct);
                    goto RecordFailure;
                }
                continue;
            }

            if (alreadyDone)
            {
                await EmitLog(emitSse, "info", $"✓ Already done: {relPath}", ct: ct);
                var r = new Dictionary<string, object?>
                {
                    ["index"] = stepIndex,
                    ["type"] = "edit",
                    ["status"] = "skipped",
                    ["path"] = relPath,
                    ["reason"] = "already done",
                    ["planItemIndex"] = planItemIndex
                };
                if (emitSse) await SendSse(Response, "step", r, ct);
                allResults.Add(r);
                return stepIndex + 1;
            }

            // ── Full file replacement ─────────────────────────────────
            if (fullFile && fullContent != null)
            {
                var fileAlreadyExists = System.IO.File.Exists(fullPath);
                if (fileAlreadyExists)
                {
                    var e = "LLM incorrectly used fullFile for existing file — " +
                            "use oldString/newString targeted edits only";
                    await EmitLog(emitSse, "error", e, ct: ct);
                    history.Add((step.OldString ?? "", step.NewString ?? "", e));
                    continue;
                }
                if (fullContent.Length > MaxFullFileTokens * 4)
                {
                    await EmitLog(emitSse, "warn",
                        $"fullFile too large ({fullContent.Length} chars) — skipping",
                        ct: ct);
                    continue;
                }
                stepIndex = await ApplyFullFile(
                    fullContent, step, fullPath, relPath,
                    projectRoot, stepIndex, planItemIndex, cardId, emitSse, ct, allResults);
                return stepIndex;
            }

            // ── Targeted replacement ──────────────────────────────────
            var fileContent = System.IO.File.Exists(fullPath)
                ? await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct)
                : string.Empty;

            var (replaced, newContent, matchError, snippet) =
                TryReplaceSafe(fileContent, oldStr!, newStr ?? string.Empty);

            if (!replaced)
            {
                var err = matchError ?? "oldString not found verbatim";
                if (!string.IsNullOrEmpty(snippet)) err += $". Nearby: {snippet}";
                await EmitLog(emitSse, "warn",
                    $"Edit attempt {attempt + 1}/{MaxAttempts} failed for {relPath}: {err}",
                    ct: ct);

                // Self-heal: extract verbatim file lines at the fuzzy match location
                var correctedBlock = BuildExactMatchBlock(fileContent, oldStr!);
                if (correctedBlock != null && correctedBlock != oldStr)
                {
                    await EmitLog(emitSse, "info",
                        $"Self-healing: exact block from file ({correctedBlock.Length} chars)",
                        ct: ct);
                    var (replaced2, newContent2, _, _) =
                        TryReplaceSafe(fileContent, correctedBlock, newStr ?? string.Empty);
                    if (replaced2)
                    {
                        var (approved2, _, _) =
                            VerifyEdit(correctedBlock, newStr ?? "", fileContent, newContent2);
                        if (approved2)
                        {
                            await System.IO.File.WriteAllTextAsync(
                                fullPath, newContent2, Encoding.UTF8, ct);
                            await EmitLog(emitSse, "success",
                                $"✓ Edited {relPath} (self-healed)", ct: ct);
                            var r2 = new Dictionary<string, object?>();
                            PopulateEditResult(r2, "modified", relPath,
                                correctedBlock, newStr ?? "", "self-healed");
                            r2["index"] = stepIndex; r2["planItemIndex"] = planItemIndex;
                            if (emitSse) await SendSse(Response, "step", r2, ct);
                            allResults.Add(r2);
                            await PersistBoardDataPlanStepAsync(
                                cardId, planItemIndex, emitSse, ct);
                            return stepIndex + 1;
                        }
                    }
                }

                history.Add((oldStr!, newStr ?? "", err));

                if (string.Equals(oldStr, lastOld, StringComparison.Ordinal)) stuckCount++;
                else { stuckCount = 0; lastOld = oldStr ?? ""; }
                if (stuckCount >= 3)
                {
                    await EmitLog(emitSse, "error",
                        $"LLM keeps producing the same oldString — aborting {relPath}",
                        ct: ct);
                    goto RecordFailure;
                }
                continue;
            }

            var (approved, verifyReason, _) =
                VerifyEdit(oldStr!, newStr ?? "", fileContent, newContent);
            if (!approved)
            {
                await EmitLog(emitSse, "warn",
                    $"Verify failed for {relPath}: {verifyReason}", ct: ct);
                history.Add((oldStr!, newStr ?? "", verifyReason));
                if (string.Equals(oldStr, lastOld, StringComparison.Ordinal)) stuckCount++;
                else { stuckCount = 0; lastOld = oldStr ?? ""; }
                if (stuckCount >= 3) goto RecordFailure;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(newStr) &&
                !newContent.Contains(newStr, StringComparison.Ordinal))
            {
                var strippedNew = StripLineLeadingWhitespace(newStr);
                var strippedContent = StripLineLeadingWhitespace(newContent);
                if (!strippedContent.Contains(strippedNew, StringComparison.Ordinal))
                {
                    var verr = "Replacement produced mismatched content — " +
                               "oldString matched wrong location";
                    await EmitLog(emitSse, "warn",
                        $"Verify failed for {relPath}: {verr}", ct: ct);
                    history.Add((oldStr!, newStr, verr));
                    continue;
                }
            }

            // Auto-format C# before writing
            var fileExt = Path.GetExtension(relPath).ToLowerInvariant();
            if (fileExt == ".cs")
            {
                try
                {
                    var fmtTree = CSharpSyntaxTree.ParseText(newContent);
                    newContent = fmtTree.GetRoot().NormalizeWhitespace().ToFullString();
                }
                catch { /* non-critical */ }
            }

            await System.IO.File.WriteAllTextAsync(fullPath, newContent, Encoding.UTF8, ct);

            // Post-edit: append stubs for any missing C# types referenced in the edit
            if (fileExt == ".cs" && !string.IsNullOrWhiteSpace(newStr))
            {
                var missing = ScanMissingTypes(newContent, newStr);
                if (missing.Count > 0)
                {
                    newContent += "\n" + string.Join("\n\n",
                        missing.Select(t => $"public class {t}\n{{\n}}"));
                    await System.IO.File.WriteAllTextAsync(
                        fullPath, newContent, Encoding.UTF8, ct);
                    await EmitLog(emitSse, "info",
                        $"Appended missing type(s): {string.Join(", ", missing)}", ct: ct);
                }
            }

            await EmitLog(emitSse, "success", $"✓ Edited {relPath}", ct: ct);
            var result = new Dictionary<string, object?>();
            PopulateEditResult(result, "modified", relPath, oldStr, newStr ?? "", "");
            result["index"] = stepIndex; result["planItemIndex"] = planItemIndex;
            if (emitSse) await SendSse(Response, "step", result, ct);
            allResults.Add(result);
            await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct);
            return stepIndex + 1;
        }

    RecordFailure:
        var lastErr = history.Count > 0 ? history[^1].error : "resolve failed";
        await EmitLog(emitSse, "error",
            $"✗ All resolve attempts failed for {relPath}: {lastErr}", ct: ct);
        var fail = new Dictionary<string, object?>
        {
            ["index"] = stepIndex,
            ["type"] = "edit",
            ["status"] = "error",
            ["path"] = relPath,
            ["error"] = lastErr,
            ["planItemIndex"] = planItemIndex
        };
        if (emitSse) await SendSse(Response, "step", fail, ct);
        allResults.Add(fail);
        return stepIndex + 1;
    }

    private async Task PersistBoardDataPlanStepAsync(string? cardId, int planItemIndex, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardId) || planItemIndex < 0)
            return;

        try
        {
            var raw = await _boardData.LoadRawAsync();
            if (string.IsNullOrWhiteSpace(raw)) return;

            using var jsonDoc = JsonDocument.Parse(raw);
            var root = JsonNode.Parse(jsonDoc.RootElement.GetRawText())?.AsObject();
            if (root == null) return;

            var columns = new[] { "todo", "doing", "done", "selfImproving" };
            foreach (var column in columns)
            {
                if (!root.TryGetPropertyValue(column, out var columnNode) || columnNode is not JsonArray columnItems)
                    continue;

                foreach (var item in columnItems)
                {
                    if (item is not JsonObject cardObj || cardObj["id"]?.GetValue<string>() != cardId)
                        continue;

                    if (cardObj["_plan"] is not JsonObject planObj || planObj["items"] is not JsonArray items)
                        continue;

                    var target = items.FirstOrDefault(i => i is JsonObject obj && obj["index"]?.GetValue<int>() == planItemIndex);
                    if (target is JsonObject stepObj)
                    {
                        stepObj["done"] = true;
                        var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                        await _boardData.SaveRawAsync(saved);
                        if (emitSse)
                        {
                            await SendSse(Response, "refresh", new
                            {
                                target = "boarddata",
                                reason = "plan-step-completed",
                                cardId,
                                planItemIndex
                            }, ct);
                        }
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to persist boarddata plan progress", new { cardId, planItemIndex, error = ex.Message });
        }
    }

    private async Task AttachFilesToCardAsync(string? cardId, List<string> filePaths, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardId) || filePaths == null || filePaths.Count == 0)
            return;
        try
        {
            var raw = await _boardData.LoadRawAsync();
            if (string.IsNullOrWhiteSpace(raw)) return;
            using var jsonDoc = JsonDocument.Parse(raw);
            var root = JsonNode.Parse(jsonDoc.RootElement.GetRawText())?.AsObject();
            if (root == null) return;
            var columns = new[] { "todo", "doing", "done", "selfImproving" };
            foreach (var column in columns)
            {
                if (!root.TryGetPropertyValue(column, out var columnNode) || columnNode is not JsonArray columnItems)
                    continue;
                foreach (var item in columnItems)
                {
                    if (item is not JsonObject cardObj || cardObj["id"]?.GetValue<string>() != cardId)
                        continue;
                    var attached = cardObj["attached"] as JsonArray ?? new JsonArray();
                    foreach (var fp in filePaths)
                        if (!attached.Any(a => a?.GetValue<string>() == fp))
                            attached.Add(JsonValue.Create(fp));
                    cardObj["attached"] = attached;
                    var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await _boardData.SaveRawAsync(saved);
                    if (emitSse)
                        await SendSse(Response, "refresh", new { target = "boarddata", reason = "files-attached", cardId }, ct);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to attach files to card", new { cardId, error = ex.Message });
        }
    }

    private async Task<(AgentPlan? plan, HashSet<int>? completedIndices)> LoadPlanFromBoardDataAsync(string? cardId)
    {
        if (string.IsNullOrWhiteSpace(cardId))
            return (null, null);

        try
        {
            var raw = await _boardData.LoadRawAsync();
            if (string.IsNullOrWhiteSpace(raw)) return (null, null);

            using var jsonDoc = JsonDocument.Parse(raw);
            var root = JsonNode.Parse(jsonDoc.RootElement.GetRawText())?.AsObject();
            if (root == null) return (null, null);

            var columns = new[] { "todo", "doing", "done", "selfImproving" };
            foreach (var column in columns)
            {
                if (!root.TryGetPropertyValue(column, out var columnNode) || columnNode is not JsonArray columnItems)
                    continue;

                foreach (var item in columnItems)
                {
                    if (item is not JsonObject cardObj || cardObj["id"]?.GetValue<string>() != cardId)
                        continue;

                    if (cardObj["_plan"] is not JsonObject planObj)
                        continue;

                    var itemsArr = planObj["items"] as JsonArray;
                    if (itemsArr == null || itemsArr.Count == 0)
                        continue;

                    var steps = new List<PlanStep>();
                    var completed = new HashSet<int>();
                    for (var i = 0; i < itemsArr.Count; i++)
                    {
                        if (itemsArr[i] is not JsonObject si) continue;
                        var step = new PlanStep
                        {
                            File = si["file"]?.GetValue<string>() ?? "",
                            Change = si["change"]?.GetValue<string>() ?? "",
                            Priority = si["priority"]?.GetValue<int>() ?? 1,
                            OldString = si["oldString"]?.GetValue<string>() ?? "",
                            NewString = si["newString"]?.GetValue<string>() ?? ""
                        };
                        // Use stored index if available, otherwise position in array
                        var idx = si["index"]?.GetValue<int>() ?? i;
                        steps.Add(step);
                        var done = si["done"]?.GetValue<bool>() ?? false;
                        if (done) completed.Add(idx);
                    }

                    if (steps.Count == 0) return (null, null);

                    var plan = new AgentPlan
                    {
                        Summary = planObj["summary"]?.GetValue<string>() ?? "",
                        Plan = steps
                    };

                    return (plan, completed.Count > 0 ? completed : null);
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to load plan from board data", new { cardId, error = ex.Message });
        }

        return (null, null);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  PLANNING — simplified, no oldString/newString in plan
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Lightweight planning prompt.  Steps contain only FILE + CHANGE (description).
    /// The actual edit (oldString/newString) is resolved per-step during execution.
    /// </summary>
    private static string BuildPlanningPrompt() =>
        "You are a software-engineering agent. Plan only 1-2 SMALL focused steps at a time.\n" +
        "Output ONLY valid JSON — no markdown fences, no extra text.\n\n" +
        "### STEP TYPES (the \"file\" field) ###\n" +
        "  \"relative/path.ext\"  — Edit an existing file (must be in discovery context). Do NOT include oldString/newString — they will be resolved at execution time.\n" +
        "  \"_command\"            — Run a terminal command; put the full command in \"change\". SAFETY: only use _command if the task requires terminal operations (fetching data, creating files outside the project). NEVER use mkdir/rmdir/del for project files — use edit steps instead.\n" +
        "  \"_create_file\"        — Create a new file: put full file content in \"newString\", leave \"oldString\" empty\n" +
        "  \"_web_search\"         — Search the web; put the query in \"change\"\n" +
        "  \"_web_fetch\"          — Fetch a URL; put the full URL in \"change\"\n" +
        "  \"_git\"                — Git operation (commit/pull/push/branch/revert)\n" +
        "  \"_rename_file\"        — Rename: put \"oldpath → newpath\" in \"change\"\n" +
        "  \"_delete_file\"        — Delete a file path in \"change\"\n" +
        "  \"_show\"               — Display text to the user (use last)\n" +
        "  \"_done\"               — Task is already complete; put reason in \"change\"\n" +
        "  \"_checkpoint\"         — Split large refactor into phases\n\n" +
        "### RULES ###\n" +
        "1. Only reference files that exist in the discovery context.\n" +
        "2. Plan AT MOST 2 steps. Smaller steps are better — you will be re-invoked to add more.\n" +
        "3. WEB FIRST: add a _web_search step if you need current API docs or recent data.\n" +
        "4. COMMANDS BEFORE EDITS: if a file must exist first, add _command BEFORE the edit step.\n" +
        "5. SELF-STOP: emit a single _done step if the code already satisfies the requirement.\n" +
        "6. Score: Score from 0-100 (0 being lowest) of how confident you are the steps completely solve the task. \n" +
        "7. Each step must be ONE focused change — do not combine unrelated edits. The change field must be extremely precise. Ex: On line 50 Create method testMethod and populate test variable with extremely precise details about what to change. \n" +
        "8. If the user stated any constraints (e.g. 'do not use x'), include them verbatim in the 'change' field.\n" +
        "9. If the file path contains \"\\\\\" escape it for JSON: use \"path/to/file.ext\"\n" +
        "10. The exact edit content (oldString/newString) will be resolved later when the file content is available — just describe what to change in the \"change\" field. Be very precise, use 1-5 lines to describe the change.\n\n" +
        "### OUTPUT FORMAT ###\n" +
        "{\n" +
        "  \"thinking\": \"1-2 lines: which file needs changing and why\",\n" +
        "  \"summary\": \"one sentence: what this step accomplishes\",\n" +
        "  \"score\": <0-100>,\n" +
        "  \"plan\": [\n" +
        "    {\n" +
        "      \"file\": \"wwwroot/app.js\",\n" +
        "      \"change\": \"Modify confirmFilePicker to append files to existing list\"\n" +
        "    }\n" +
        "  ]\n" +
        "}";

    /// <summary>Quick LLM validation of plan steps before execution. Returns null if valid, or a reason string if invalid.</summary>
    private async Task<string?> ValidatePlanAsync(string userPrompt, AgentPlan plan, CancellationToken ct)
    {
        // Safety check: ask user to confirm _command steps that modify project structure
        if (plan?.Plan != null)
        {
            foreach (var step in plan.Plan)
            {
                if (string.Equals(step.File, "_command", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(step.Change))
                {
                    var cmd = step.Change.ToLowerInvariant();
                    if (cmd.Contains("mkdir") || cmd.Contains("rmdir") || cmd.Contains("rm -rf") ||
                        cmd.Contains("del /") || cmd.Contains("rd /"))
                    {
                        if (!cmd.Contains("desktop") && !cmd.Contains("temp") && !cmd.Contains("tmp") &&
                            !cmd.Contains("download") && !cmd.Contains("$home") && !cmd.Contains("~"))
                        {
                            var answer = await AskUserAsync(
                                $"The plan includes a potentially unsafe command:\n\n`{step.Change}`\n\nThis could modify project structure. Allow it?",
                                new List<QuestionField>
                                {
                                    new() { Key = "confirm", Label = "Allow this command?", Type = "select", DefaultValue = "no" }
                                }, ct);
                            if (answer.Count == 0) // timeout — user didn't respond
                            {
                                _gracefulStop = true;
                                return null; // skip this card gracefully
                            }
                            var confirmed = answer.TryGetValue("confirm", out var val) &&
                                           val?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true;
                            if (!confirmed)
                                return $"_command step uses '{cmd.Split(' ')[0]}' which may modify project structure — user rejected the command. Replanning.";
                        }
                    }
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("You are validating a code-change plan. Determine if the plan makes sense and is complete given the user's request.");
        sb.AppendLine("Check each step for:");
        sb.AppendLine("- Does the file path look reasonable for the change?");
        sb.AppendLine("- Is the change description clear and actionable?");
        sb.AppendLine("- Are steps in the right order (commands before edits)?");
        sb.AppendLine("- Are any steps identical (invalid)?");
        sb.AppendLine("- Are any steps incomplete?");
        sb.AppendLine("- Does the plan actually address the user's request?");
        sb.AppendLine();
        sb.AppendLine("Respond with a single JSON object:");
        sb.AppendLine("{\"valid\": true}  or  {\"valid\": false, \"reason\": \"short explanation of what's wrong\"}");
        sb.AppendLine();
        sb.AppendLine("### USER REQUEST ###");
        sb.AppendLine(userPrompt);
        sb.AppendLine();
        sb.AppendLine("### PLAN ###");
        sb.AppendLine(JsonSerializer.Serialize(plan.Plan, new JsonSerializerOptions { WriteIndented = true }));

        var (raw, _, err) = await CallLlmRaw(
            "You validate code-change plans. Output ONLY a JSON object with a \"valid\" boolean and optional \"reason\". No extra text, no markdown fences.",
            sb.ToString(), ct, TimeSpan.FromSeconds(30), maxTokens: 256);

        if (!string.IsNullOrWhiteSpace(err) || string.IsNullOrWhiteSpace(raw))
            return null; // validation inconclusive — proceed anyway

        var cleaned = raw.Trim();
        if (cleaned.StartsWith('{') == false)
        {
            var fb = cleaned.IndexOf('{');
            var lb = cleaned.LastIndexOf('}');
            if (fb >= 0 && lb > fb) cleaned = cleaned[fb..(lb + 1)];
        }

        try
        {
            using var jDoc = JsonDocument.Parse(cleaned);
            var root = jDoc.RootElement;
            if (root.TryGetProperty("valid", out var valid) && valid.ValueKind == JsonValueKind.False)
            {
                var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : "Plan validation failed";
                return reason;
            }
        }
        catch { /* parse error — proceed anyway */ }

        return null; // valid or inconclusive
    }

    private async Task<AgentPlan?> AnalyzePromptAndPlanCodeChanges(
        string prompt, string discoveryContext, string projectRoot, bool emitSse,
        CancellationToken ct = default, string? steeringContext = null)
    {
        var planningPrompt = BuildPlanningPrompt();

        var userPrompt = new StringBuilder();
        userPrompt.AppendLine("### TASK ###");
        userPrompt.AppendLine(prompt);
        if (!string.IsNullOrWhiteSpace(steeringContext))
        {
            userPrompt.AppendLine();
            userPrompt.AppendLine("### USER STEERING ###");
            userPrompt.AppendLine(steeringContext);
        }
        userPrompt.AppendLine();
        userPrompt.AppendLine("### PROJECT ROOT ###");
        userPrompt.AppendLine(projectRoot);
        userPrompt.AppendLine("### DISCOVERY CONTEXT (only use paths listed here) ###");
        userPrompt.AppendLine(BuildPlannerDiscoveryContext(discoveryContext));
        

        Console.WriteLine($"### CALLING LLM WITH PROMPT >>> {planningPrompt} >>> {userPrompt}");

        var (raw, _, llmError) = await CallLlmRawStreaming(
            planningPrompt, userPrompt.ToString(), emitSse, ct,
            requestTimeout: _infiniteTimeout, maxTokens: 2048);

        if (string.IsNullOrWhiteSpace(raw))
        {
            await EmitLog(emitSse, "error",
                $"LLM returned empty plan response: {llmError ?? "no content"}", ct: ct);
            return null;
        }

        // Try JSON first, fall back to delimiter format
        AgentPlan? plan = ParsePlan(raw);
        if (plan == null && (raw.Contains("<<<STEP", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("### STEP", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("STEP", StringComparison.OrdinalIgnoreCase)))
            plan = AgentUtilities.ParseDelimitedPlan(raw);

        if (plan == null)
        {
            bool containsLLMError = false;
            bool containsLLMLoading = false;
            if (!string.IsNullOrEmpty(raw)) {
                if (raw.ToLower().Contains("error")) {
                    containsLLMError = true;
                }
                if (raw.ToLower().Contains("loading model")) {
                    containsLLMLoading = true;
                }
            }
            string errorMessage = containsLLMLoading ? " Model Loading. Please retry after a short period of time."
                                    : containsLLMError ? " LLM Returned Error state. Check LLM." 
                                    : "";
            await EmitLog(emitSse, "error", "Failed to parse plan." + errorMessage, raw, ct: ct);
            return null;
        }

        // Check for missing web search
        var webViolation = DetectMissingWebSearch(prompt, plan);
        if (webViolation != null)
            await EmitLog(emitSse, "warn", $"Plan may need web search: {webViolation}", ct: ct);

        await EmitLog(emitSse, "info",
            $"Plan: {plan.Plan.Count} step(s) — score {plan.Score}/100", new { plan }, ct: ct);

        return plan;
    }

    private static bool LooksLikeTruncated(string raw)
    {
        var opens = raw.Count(c => c is '{' or '[');
        var closes = raw.Count(c => c is '}' or ']');
        if (opens > closes + 1) return true;
        var lastLine = raw.Split('\n')[^1];
        return Regex.Matches(lastLine, @"(?<!\\)""").Count % 2 != 0;
    }

    /// <summary>Check if file content looks truncated (unbalanced braces = LLM hit token limit).</summary>
    private static bool IsFullFileTruncated(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var opens = content.Count(c => c == '{');
        var closes = content.Count(c => c == '}');
        return opens > closes;
    }

    private async Task<(AgentPlan? plan, string? error)> ParseAndScore(
        string raw, bool emitSse, CancellationToken ct)
    {
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```"))
        {
            var m = Regex.Match(cleaned, @"```(?:text|json)?\s*([\s\S]*?)```",
                RegexOptions.IgnoreCase);
            cleaned = m.Success ? m.Groups[1].Value.Trim() : cleaned.TrimStart('`');
        }

        AgentPlan? parsed = null;
        if (cleaned.Contains("<<<STEP", StringComparison.OrdinalIgnoreCase))
            parsed = AgentUtilities.ParseDelimitedPlan(cleaned);
        if (parsed == null)
            parsed = ParsePlan(cleaned);

        if (parsed == null)
        {
            await EmitLog(emitSse, "error", "Failed to parse plan.", cleaned, ct: ct);
            return (null, "Response was unparseable.");
        }

        // Size violations only matter if oldString is present (legacy plans)
        var violations = GetPlanSizeViolations(parsed);
        if (violations.Count > 0)
        {
            // With the new resolve architecture, oversized oldStrings are handled
            // at execution time — just warn, don't penalise the score
            await EmitLog(emitSse, "warn",
                $"{violations.Count} oversized anchor(s) — will attempt resolve at execution time",
                ct: ct);
        }

        return (parsed, null);
    }

    private static string? DetectMissingWebSearch(string prompt, AgentPlan plan)
    {
        var lower = prompt.ToLowerInvariant();
        var triggers = new[] { "search for", "look up", "find out", "up to date", "up-to-date" };
        var hit = triggers.FirstOrDefault(t => lower.Contains(t));
        if (hit == null) return null;
        var hasWebStep = plan.Plan?.Any(s =>
            s.File.Equals("_web_search", StringComparison.OrdinalIgnoreCase) ||
            s.File.Equals("_web_fetch", StringComparison.OrdinalIgnoreCase)) ?? false;
        if (hasWebStep) return null;
        return $"Prompt contains \"{hit}\" but plan has no _web_search step.";
    }

    private static string BuildPlannerDiscoveryContext(string fullDiscovery)
    {
        if (string.IsNullOrWhiteSpace(fullDiscovery)) return fullDiscovery;
        const int MaxLinesPerFile = 200;
        const int MaxFiles = 20;
        var result = new StringBuilder();
        var sections = Regex.Split(fullDiscovery, @"(?=### \S)");
        var fileCount = 0;
        foreach (var section in sections)
        {
            if (string.IsNullOrWhiteSpace(section)) continue;
            var trimmed = section.TrimStart();
            if (!trimmed.StartsWith("### read") && !trimmed.StartsWith("### list"))
            {
                result.AppendLine(section.TrimEnd());
                continue;
            }
            if (fileCount >= MaxFiles)
            {
                result.AppendLine("...(additional files omitted from planner context)");
                break;
            }
            var lines = section.Split('\n');
            result.AppendLine(lines[0]);
            var body = lines.Skip(1).ToArray();
            if (body.Length <= MaxLinesPerFile)
                result.AppendLine(string.Join('\n', body));
            else
            {
                result.AppendLine(string.Join('\n', body.Take(MaxLinesPerFile)));
                result.AppendLine($"...(truncated — full content used during execution)");
            }
            result.AppendLine();
            fileCount++;
        }
        return result.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  BOOTSTRAP DISCOVERY
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<(string discoveryText, List<object> steps)> RunLightBootstrap(
        List<string> attachedFiles, string projectRoot, bool emitSse, CancellationToken ct = default)
    {
        await EmitLog(emitSse, "info", "Fast-path bootstrap: reading attached files only");

        var files = (attachedFiles ?? new List<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .ToList();

        if (files.Count == 0) return ("", new List<object>());

        // Phase 1: Read ALL files reliably — no SSE interleaved
        var sb = new StringBuilder();
        sb.AppendLine("Attached files (edit these paths only):");
        foreach (var f in files)
            sb.AppendLine($"  - {f.Replace('\\', '/')}");

        var allResults = new List<object>();
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var relPath = f.Replace('\\', '/');
            var fullPath = Path.GetFullPath(
                Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));

            var result = new Dictionary<string, object?>
            {
                ["index"] = allResults.Count,
                ["type"] = "read",
                ["description"] = $"Read attached {f}",
                ["status"] = "running"
            };

            try
            {
                if (!AgentUtilities.IsPathUnderRoot(fullPath, projectRoot))
                {
                    result["status"] = "error";
                    result["error"] = "Path outside root";
                }
                else if (!System.IO.File.Exists(fullPath))
                {
                    result["status"] = "error";
                    result["error"] = "File not found";
                }
                else
                {
                    result["path"] = relPath;
                    result["output"] = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                    result["status"] = "done";
                    sb.AppendLine($"\n### {relPath}\n```\n{result["output"]}\n```");
                }
            }
            catch (Exception ex)
            {
                result["status"] = "error";
                result["error"] = ex.Message;
            }

            result["status"] = AgentUtilities.NormalizeUiStatus(result["status"]?.ToString());
            allResults.Add(result);
        }

        // Phase 2: Emit a single log + batch SSE event (no per-file events —
        // rapid-fire SSE writes silently drop events on certain ASP.NET Core builds)
        if (emitSse)
        {
            var succeeded = allResults.Count(r =>
                r is Dictionary<string, object?> d &&
                d.GetValueOrDefault("status")?.ToString() == "done");
            var total = allResults.Count;

            await EmitLog(emitSse, "info",
                $"Read {total} attached file(s), {succeeded} succeeded");

            var fileList = allResults
                .Select(r => r is Dictionary<string, object?> d
                    ? new { index = d.GetValueOrDefault("index"), path = d.GetValueOrDefault("path"), status = d.GetValueOrDefault("status") }
                    : null)
                .Where(x => x != null)
                .ToList();

            await SendSse(Response, "batch-read", new
            {
                total,
                succeeded,
                files = fileList
            }, ct);
        }

        return (sb.ToString(), allResults);
    }

    private async Task<(string discoveryText, List<object> steps)> RunBootstrapDiscovery(
        string prompt, string projectRoot, bool emitSse,
        List<string>? attachedFiles = null, CancellationToken ct = default)
    {
        if (attachedFiles != null && attachedFiles.Count > 0)
            return await RunLightBootstrap(attachedFiles, projectRoot, emitSse, ct);

        await EmitLog(emitSse, "info", "Phase 1 — DISCOVER: enumerating project files…", ct: ct);
        var allSteps = new List<object>();

        var listStep = new AgentStep { Index = 0, Type = "list", Path = "", Description = "Auto: list project root" };
        var listResults = await ExecuteDiscoveryStepsConcurrent(
            new List<AgentStep> { listStep }, projectRoot, 0, emitSse);
        allSteps.AddRange(listResults);

        if (!Directory.Exists(projectRoot)) return ("", allSteps);

        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "node_modules", ".git", "bin", "obj", "dist", ".angular", "packages", ".vs", ".idea" };

        var allFiles = Directory.EnumerateFiles(projectRoot, "*.*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/'))
            .Where(rel => !skipDirs.Any(d =>
                rel.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase) ||
                rel.Contains("/" + d + "/", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (allFiles.Count == 0) return ("", allSteps);

        var hintedFiles = _fileHints.GetFilesForPrompt(prompt, projectRoot)
            .Where(f => allFiles.Any(a => string.Equals(a, f, StringComparison.OrdinalIgnoreCase)))
            .Take(4).ToList();

        var heuristicCandidates = ApplyTaskTypeHeuristics(prompt, allFiles);
        var candidatePool = hintedFiles
            .Concat(heuristicCandidates)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(60).ToList();

        List<string> toRead;
        if (candidatePool.Count <= 6)
        {
            toRead = candidatePool;
            await EmitLog(emitSse, "info",
                $"Phase 1 — {candidatePool.Count} candidate(s), reading all directly", ct: ct);
        }
        else
        {
            await EmitLog(emitSse, "info",
                $"Phase 1 — selecting from {candidatePool.Count} candidates…", ct: ct);
            var selected = await SelectRelevantFilesWithLlm(prompt, candidatePool, emitSse, ct);
            toRead = hintedFiles.Concat(selected)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        }

        toRead = toRead.Where(f =>
        {
            var full = Path.GetFullPath(Path.Combine(projectRoot, f.Replace('/', Path.DirectorySeparatorChar)));
            return System.IO.File.Exists(full) && AgentUtilities.IsPathUnderRoot(full, projectRoot);
        }).ToList();

        await EmitLog(emitSse, "info",
            $"Phase 1 — reading {toRead.Count} file(s): {string.Join(", ", toRead)}", ct: ct);

        if (toRead.Count > 0)
        {
            var readPlan = toRead.Select((f, i) => new AgentStep
            {
                Index = i,
                Type = "read",
                Path = f,
                Description = $"Auto: read {f}",
                Prompt = prompt
            }).ToList();

            var readResults = await ExecuteDiscoveryStepsConcurrent(
                readPlan, projectRoot, allSteps.Count, emitSse);
            allSteps.AddRange(readResults);
            foreach (var f in toRead) _fileHints.LearnFromGrepOutput(prompt, f, projectRoot);
        }

        var sb = new StringBuilder();
        sb.AppendLine("ONLY use paths that appear below. Do NOT invent paths.");
        sb.AppendLine();
        foreach (var item in allSteps)
        {
            if (item is not Dictionary<string, object?> r) continue;
            var type = r.TryGetValue("type", out var t) ? t?.ToString() : "";
            if (!r.TryGetValue("output", out var output) ||
                output == null || string.IsNullOrEmpty(output.ToString())) continue;
            sb.AppendLine($"### {type} {r.GetValueOrDefault("path") ?? r.GetValueOrDefault("description")}");
            sb.AppendLine(output.ToString());
            sb.AppendLine();
        }

        await EmitLog(emitSse, "info",
            $"Phase 1 complete — {allSteps.Count} step(s), {toRead.Count} file(s) read", ct: ct);
        return (sb.ToString(), allSteps);
    }

    private static List<string> ApplyTaskTypeHeuristics(string prompt, List<string> allFiles)
    {
        var lower = prompt.ToLowerInvariant();
        var isStyle = Regex.IsMatch(lower, @"\b(style|css|color|theme|layout|spacing|font|design|ui|ux|brand|visual|margin|padding|border|shadow|panel|card)\b");
        var isHtml = Regex.IsMatch(lower, @"\b(html|template|page|view|markup|modal|popup|section|div)\b");
        var isJs = Regex.IsMatch(lower, @"\b(javascript|script|function|event|click|toggle|show|hide|angular|react|vue|component|state|behavior)\b");
        var isBackend = Regex.IsMatch(lower, @"\b(api|endpoint|controller|service|database|model|route|logic|backend|server|c#|csharp|dotnet)\b");
        var isConfig = Regex.IsMatch(lower, @"\b(config|setting|option|appsettings|environment|json)\b");
        var keywords = AgentUtilities.ExtractMeaningfulKeywords(lower);

        var scored = allFiles.Select(f =>
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            var nameLow = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
            var pathLow = f.ToLowerInvariant();
            var score = 0;
            if (isStyle) { if (ext is ".css" or ".scss" or ".sass") score += 120; else if (ext is ".html") score += 60; else if (ext is ".js") score += 20; }
            if (isHtml) { if (ext is ".html" or ".htm") score += 120; else if (ext is ".css") score += 50; else if (ext is ".js") score += 30; }
            if (isJs) { if (ext is ".js" or ".ts" or ".jsx" or ".tsx") score += 120; else if (ext is ".html") score += 40; }
            if (isBackend) { if (ext == ".cs") score += 120; else if (ext == ".json") score += 30; }
            if (isConfig) { if (ext is ".json" or ".yaml" or ".yml") score += 120; }
            foreach (var kw in keywords) if (nameLow.Contains(kw)) score += 50;
            if ((isStyle || isHtml || isJs) && pathLow.StartsWith("wwwroot/")) score += 25;
            if (nameLow.Contains("agentcontroller")) score -= 200;
            if (nameLow == "filehints") score -= 200;
            if (pathLow.EndsWith(".min.js") || pathLow.EndsWith(".min.css")) score -= 300;
            if (ext is ".dll" or ".exe" or ".pdb" or ".nupkg" or ".lock") score -= 1000;
            return (file: f, score);
        })
        .Where(x => x.score > 0).OrderByDescending(x => x.score).Take(50).Select(x => x.file).ToList();

        if (scored.Count == 0)
            scored = allFiles.Where(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                var ext = Path.GetExtension(f).ToLowerInvariant();
                return name is "index" or "app" or "main" or "program" or "startup" or "styles" or "global" or "layout"
                    && ext is ".html" or ".js" or ".ts" or ".css" or ".cs";
            }).Take(10).ToList();

        return scored;
    }

    private static List<string> ExtractMeaningfulKeywords(string lower) =>
        AgentUtilities.ExtractMeaningfulKeywords(lower);

    private async Task<List<string>> SelectRelevantFilesWithLlm(
        string prompt, List<string> candidates, bool emitSse, CancellationToken ct)
    {
        if (candidates.Count == 0) return new List<string>();
        const string system =
            "You are a file relevance selector. Given a task and files, pick 3-7 most likely to need editing. " +
            "Output ONLY valid JSON, no markdown: {\"files\": [\"path1\", \"path2\"]}";
        var user = $"Task: {prompt}\n\nFiles:\n{string.Join("\n", candidates)}\n\nSelect 3-7 max.";
        var (raw, _, err) = await CallLlmRaw(system, user, ct, TimeSpan.FromSeconds(25));
        if (string.IsNullOrWhiteSpace(raw))
            return candidates.Take(6).ToList();
        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (m.Success) cleaned = m.Groups[1].Value.Trim();
            }
            var s = cleaned.IndexOf('{'); var e = cleaned.LastIndexOf('}');
            if (s >= 0 && e > s) cleaned = cleaned[s..(e + 1)];
            using var doc = JsonDocument.Parse(cleaned);
            if (doc.RootElement.TryGetProperty("files", out var filesEl) &&
                filesEl.ValueKind == JsonValueKind.Array)
            {
                var selected = filesEl.EnumerateArray()
                    .Select(el => el.GetString()?.Replace('\\', '/') ?? "")
                    .Where(f => !string.IsNullOrWhiteSpace(f) &&
                                candidates.Any(c => string.Equals(c, f, StringComparison.OrdinalIgnoreCase)))
                    .Take(7).ToList();
                if (selected.Count > 0) return selected;
            }
        }
        catch { }
        return candidates.Take(6).ToList();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  PLAN PARSING
    // ═══════════════════════════════════════════════════════════════════════
    private AgentPlan DeduplicatePlan(AgentPlan plan)
    {
        if (plan?.Plan == null || plan.Plan.Count == 0)
            return plan;

        var seen = new HashSet<string>();
        var unique = new List<PlanStep>();

        foreach (var step in plan.Plan)
        {
            // Only dedupe steps that contain both oldString and newString
            var key = step.File + "\n" + step.OldString + "\n" + step.NewString;

            if (!seen.Contains(key))
            {
                seen.Add(key);
                unique.Add(step);
            }
        }

        plan.Plan = unique;
        return plan;
    }

    public AgentPlan? ParsePlan(string jsonString)
    {
        if (string.IsNullOrWhiteSpace(jsonString)) return null;
        var cleaned = jsonString.Trim();
        if (cleaned.StartsWith("```"))
        {
            var fm = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            cleaned = fm.Success ? fm.Groups[1].Value.Trim() : cleaned.TrimStart('`');
        }
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        var truncRepaired = AgentUtilities.TryRepairTruncatedPlanJson(cleaned);
        if (truncRepaired != null)
        {
            var truncOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true };
            foreach (var candidate in AgentUtilities.GeneratePlanJsonCandidates(truncRepaired))
            {
                try { 
                    var deserializedPlan = JsonSerializer.Deserialize<AgentPlan>(candidate, truncOpts); 
                    if (deserializedPlan?.Plan?.Count > 0) 
                    return DeduplicatePlan(deserializedPlan); 
                } 
                catch { }
            }
        }
        var jsonBlocks = AgentUtilities.ExtractJsonBlocks(cleaned).Where(LooksLikePlanJson).OrderByDescending(b => b.Length).ToList();
        if (LooksLikePlanJson(cleaned) && cleaned.StartsWith("{"))
        {
            jsonBlocks.Insert(0, cleaned); 
        }
        var fb = cleaned.IndexOf('{'); 
        var lb = cleaned.LastIndexOf('}');
        if (fb >= 0 && lb > fb) { 
            var bc = cleaned[fb..(lb + 1)]; 
            if (LooksLikePlanJson(bc)) {
                jsonBlocks.Add(bc); 
            } 
        }
        foreach (var candidate in jsonBlocks.Distinct())
        {
            foreach (var repaired in AgentUtilities.GeneratePlanJsonCandidates(candidate))
            {
                try { 
                    var result = JsonSerializer.Deserialize<AgentPlan>(repaired, opts); 
                    if (result?.Plan != null) {
                        return DeduplicatePlan(result); 
                    } 
                } catch { }
            }
        } 
        var arrayCandidates = new List<string> { cleaned };
        var f2 = cleaned.IndexOf('['); var l2 = cleaned.LastIndexOf(']');
        if (f2 >= 0 && l2 > f2) arrayCandidates.Add(cleaned[f2..(l2 + 1)]);
        foreach (var block in arrayCandidates.Distinct())
        {
            try
            {
                var c = block.Trim();
                if (!c.StartsWith("[")) continue;
                var steps = JsonSerializer.Deserialize<List<PlanStep>>(c, opts);
                    if (steps is { Count: > 0 }) return new AgentPlan { Summary = "Parsed array", Plan = steps, Score = 0 };
            }
            catch { }
        }
        return null;
    }

    private static bool LooksLikePlanJson(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        Regex.IsMatch(text, @"""?plan""?\s*:", RegexOptions.IgnoreCase);

    // ═══════════════════════════════════════════════════════════════════════
    //  ORCHESTRATOR
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<(List<object> allSteps, AgentPlan? plan, bool complete)> Orchestrate(
        string prompt, string projectRoot, bool emitSse, CancellationToken ct = default,
        List<string>? attachedFiles = null, bool skipContextReview = false,
        string? steeringContext = null, bool skipQualityCheck = false,
        AgentPlan? existingPlan = null, HashSet<int>? completedStepIndices = null,
        string? cardId = null)
    {
        _gracefulStop = false;

        if (!await CheckLlmConnectivity(projectRoot, emitSse, ct))
            throw new InvalidOperationException("LLM connectivity check failed.");

        var fastPlan = AgentUtilities.TryDetectSimpleIntent(prompt);
        if (fastPlan != null)
        {
            var steps = await QuickPipeline(prompt, projectRoot, emitSse, fastPlan, ct,
                cardId: cardId);
            return (steps, fastPlan, true);
        }

        // ── Fast-path: "fix the build" — skip discovery, go straight to repair ─
        var fixBuildMatch = Regex.Match(prompt.ToLowerInvariant(),
            @"fix\s+(all\s+)?(the\s+)?build\s+(errors?|warnings?|issues?)");
        if (fixBuildMatch.Success)
        {
            await EmitLog(emitSse, "info", "Build repair prompt detected — running repair pipeline.", ct: ct);
            var cfg = await _configFile.LoadConfigAsync();
            var cmds = ParseBuildCommands(cfg.buildCommands);
            string? buildOutput = null;
            if (cmds.Count > 0)
            {
                _terminal.Start();
                foreach (var cmd in cmds)
                {
                    await _terminal.SendCommandAsync(cmd, projectRoot);
                    await Task.Delay(3000);
                }
                buildOutput = _terminal.ReadAll();
            }
            var resultSteps = new List<object>();
            await RunRepairPlan(projectRoot, emitSse, ct, prompt, buildOutput ?? "", resultSteps);
            return (resultSteps, null, true);
        }

        // ── Resume from existing plan (skip replanning) ─────────────────────
        if (existingPlan != null && existingPlan.Plan.Count > 0)
        {
            var doneCount = completedStepIndices?.Count ?? 0;
            await EmitLog(emitSse, "info", $"Using existing plan — {existingPlan.Plan.Count} step(s), {doneCount} already done", existingPlan, ct: ct);
            if (emitSse)
                await SendSse(Response, "plan",
                    new { thinking = existingPlan.Thinking, summary = existingPlan.Summary,
                          items = existingPlan.Plan, resumed = true }, ct);

            var resumeSteps = new List<object>();
            await ExecutePlan(prompt, projectRoot, emitSse, "", existingPlan, ct, resumeSteps,
                steeringContext: steeringContext, attachedFiles: attachedFiles,
                completedStepIndices: completedStepIndices, cardId: cardId);

            return (resumeSteps, existingPlan, resumeSteps.Count > 0);
        }

        var (pipelineType, cmdScore, editScore) = AgentUtilities.ClassifyTask(prompt);
        await EmitLog(emitSse, "info",
            $"Router → {pipelineType}",
            new { CommandScore = cmdScore, EditScore = editScore }, ct: ct);

        // ── LLM verification of pipeline choice ────────────────────────────
        var verifyPrompt = $"Verify this routing decision.\n\nTask: \"{prompt}\"\nRouter selected: {pipelineType} (commandScore={cmdScore}, editScore={editScore})\n\nPipeline types:\n- CommandExecution: terminal commands, web fetches, create/modify files on filesystem (no code editing)\n- UnifiedPipeline: code editing, project source changes\n\nIs this routing correct? If the task has BOTH a data-fetching/filesystem component AND a code-editing component, suggest chaining (CommandExecution first to fetch data and save temp files inside the project, then UnifiedPipeline to edit code using those files).\n\nReply ONLY with JSON:\n{{\"decision\": \"confirm\"}}\n{{\"decision\": \"override\", \"pipeline\": \"CommandExecution|UnifiedPipeline\"}}\n{{\"decision\": \"chain\", \"stages\": [{{\"pipeline\": \"CommandExecution\", \"summary\": \"...\"}}, {{\"pipeline\": \"UnifiedPipeline\", \"summary\": \"...\"}}]}}";

        var (vRaw, _, vErr) = await CallLlmRaw(
            "You verify task routing. Output only JSON.",
            verifyPrompt, ct, TimeSpan.FromSeconds(15), maxTokens: 256);

        PipelineType? chainedNext = null;
        List<(PipelineType Pipeline, string Summary)>? stages = null;

        if (!string.IsNullOrWhiteSpace(vRaw))
        {
            var vClean = vRaw.Trim();
            if (vClean.StartsWith("```")) { var m = Regex.Match(vClean, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) vClean = m.Groups[1].Value.Trim(); }
            try
            {
                using var vDoc = JsonDocument.Parse(vClean, new JsonDocumentOptions { AllowTrailingCommas = true });
                var vRoot = vDoc.RootElement;
                var decision = vRoot.TryGetProperty("decision", out var d) ? d.GetString() : null;
                if (decision == "override" && vRoot.TryGetProperty("pipeline", out var ov))
                {
                    var overridePipeline = ov.GetString();
                    pipelineType = overridePipeline.ToLowerInvariant() switch
                    {
                        "unifiedpipeline" or "unified" or "codeedit" => PipelineType.CodeEdit,
                        "commandexecution" or "command" => PipelineType.CommandExecution,
                        _ => pipelineType
                    };
                    if (pipelineType != (AgentUtilities.ClassifyTask(prompt).Type))
                        await EmitLog(emitSse, "info", $"LLM override → {pipelineType}", ct: ct);
                }
                else if (decision == "chain" && vRoot.TryGetProperty("stages", out var stArr) && stArr.ValueKind == JsonValueKind.Array)
                {
                    stages = new List<(PipelineType, string)>();
                    foreach (var st in stArr.EnumerateArray())
                    {
                        var stP = st.TryGetProperty("pipeline", out var sp) ? sp.GetString() : null;
                        var stSum = st.TryGetProperty("summary", out var ss) ? ss.GetString() : "";
                        PipelineType? parsed = stP?.ToLowerInvariant() switch
                        {
                            "unifiedpipeline" or "unified" or "codeedit" => PipelineType.CodeEdit,
                            "commandexecution" or "command" => PipelineType.CommandExecution,
                            _ => null
                        };
                        if (parsed.HasValue) stages.Add((parsed.Value, stSum ?? ""));
                    }
                    if (stages.Count >= 2)
                    {
                        pipelineType = stages[0].Pipeline;
                        chainedNext = stages[1].Pipeline;
                        await EmitLog(emitSse, "info", $"LLM chain: {stages[0].Pipeline} → {stages[1].Pipeline}", ct: ct);
                    }
                }
            }
            catch { }
        }

        // ── Run pipeline(s) ────────────────────────────────────────────────
        List<object> allSteps = new();
        AgentPlan? plan = null;

        if (pipelineType == PipelineType.CommandExecution)
        {
            var result = await CommandExecutionPipeline(prompt, projectRoot, emitSse, ct,
                steeringContext: steeringContext, cardId: cardId);
            allSteps = result.steps;
            plan = result.plan;

            // If chaining to CodeEdit (UnifiedPipeline), collect created files and attach to card
            if (chainedNext == PipelineType.CodeEdit)
            {
                // Collect file paths from plan steps and command outputs
                var createdFiles = new List<string>();
                if (plan?.Plan?.Count > 0)
                {
                    foreach (var p in plan.Plan)
                    {
                        if (string.IsNullOrWhiteSpace(p.File) || p.File.StartsWith("_")) continue;
                        var resolved = System.IO.File.Exists(p.File)
                            ? p.File
                            : System.IO.File.Exists(Path.GetFullPath(Path.Combine(projectRoot, p.File.Replace('/', Path.DirectorySeparatorChar))))
                                ? Path.GetFullPath(Path.Combine(projectRoot, p.File.Replace('/', Path.DirectorySeparatorChar)))
                                : null;
                        if (resolved != null) createdFiles.Add(resolved);
                    }
                }
                // Also scan step results for file creation commands
                foreach (var s in allSteps.OfType<Dictionary<string, object?>>())
                {
                    var cmd = s.GetValueOrDefault("command")?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(cmd)) continue;
                    var pathMatch = Regex.Match(cmd, @"(?:Set-Content|Out-File|>)\s+['""]?([\w./\\:-]+\.\w+)['""]?");
                    if (pathMatch.Success)
                    {
                        var fp = pathMatch.Groups[1].Value;
                        if (!createdFiles.Contains(fp) && (System.IO.File.Exists(fp) || System.IO.File.Exists(Path.Combine(projectRoot, fp))))
                            createdFiles.Add(System.IO.File.Exists(fp) ? fp : Path.GetFullPath(Path.Combine(projectRoot, fp)));
                    }
                }

                if (createdFiles.Count > 0)
                {
                    await EmitLog(emitSse, "info", $"Chaining: {createdFiles.Count} file(s) from CommandExecution → UnifiedPipeline", ct: ct);
                    // Attach to card
                    await AttachFilesToCardAsync(cardId, createdFiles, emitSse, ct);
                    // Append to attachedFiles
                    var combinedAttachments = (attachedFiles ?? new List<string>())
                        .Concat(createdFiles)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var chainResult = await UnifiedPipeline(prompt, projectRoot, emitSse, ct,
                        attachedFiles: combinedAttachments, skipContextReview: skipContextReview,
                        steeringContext: $"Previous stage created files: {string.Join(", ", createdFiles)}. Task: {prompt}",
                        cardId: cardId);
                    allSteps.AddRange(chainResult.steps);
                    plan = chainResult.plan ?? plan;
                }
            }
        }
        else
        {
            var (unifiedSteps, unifiedPlan) = await UnifiedPipeline(prompt, projectRoot, emitSse, ct,
                attachedFiles: attachedFiles, skipContextReview: skipContextReview,
                steeringContext: steeringContext, cardId: cardId);
            allSteps = unifiedSteps;
            plan = unifiedPlan;
        }

        if (_gracefulStop)
        {
            _gracefulStop = false;
            return (allSteps, plan, false);
        }

        // ── Quality check ─────────────────────────────────────────────────
        bool complete = true;
        if (!skipQualityCheck && allSteps.Count > 0)
        {
            var hasDone = allSteps.OfType<Dictionary<string, object?>>()
                .Any(s => s.TryGetValue("type", out var t) && t?.ToString() == "done_signal");
            // If PostExecuteVerify already confirmed task completion, skip redundant quality check
            var verified = allSteps.OfType<Dictionary<string, object?>>()
                .Any(s => s.TryGetValue("type", out var t) && t?.ToString() == "verified_complete");

            if (verified) hasDone = true;

            if (!hasDone)
            {
                var (ok, reason) = await AssessCompletion(prompt, allSteps, projectRoot, ct, plan, attachedFiles: attachedFiles);
                complete = ok;
                if (!ok)
                {
                    await EmitLog(emitSse, "warn", $"Quality check: {reason}", ct: ct);

                    // Build completedStepIndices from existing results
                    var doneIndices = new HashSet<int>();
                    for (var i = 0; i < (plan?.Plan?.Count ?? 0); i++)
                    {
                        var step = plan!.Plan[i];
                        var result = allSteps.OfType<Dictionary<string, object?>>()
                            .LastOrDefault(s => string.Equals(s.GetValueOrDefault("path")?.ToString(), step.File, StringComparison.OrdinalIgnoreCase) &&
                                s.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created" or "skipped" &&
                                s.GetValueOrDefault("type")?.ToString() is "edit" or "create" or "rename");
                        if (result != null) doneIndices.Add(i);
                    }

                    var hasIncomplete = plan != null && doneIndices.Count < plan.Plan.Count;

                    if (hasIncomplete)
                    {
                        // Retry incomplete steps from the existing plan first
                        await EmitLog(emitSse, "info",
                            $"Replan: retrying {plan!.Plan.Count - doneIndices.Count} incomplete step(s)…", ct: ct);
                        var retryResults = new List<object>();
                        await ExecutePlan(prompt, projectRoot, emitSse, "", plan, ct, retryResults,
                            steeringContext: steeringContext, attachedFiles: attachedFiles,
                            completedStepIndices: doneIndices, cardId: cardId);
                        allSteps.AddRange(retryResults);

                        var (ok2, _) = await AssessCompletion(prompt, allSteps, projectRoot, ct, plan, attachedFiles: attachedFiles);
                        complete = ok2;
                    }

                    // Rebuild doneIndices after retry for the check below
                    if (!complete && plan?.Plan?.Count > 0)
                    {
                        for (var i = 0; i < plan.Plan.Count; i++)
                        {
                            var step = plan.Plan[i];
                            var result = allSteps.OfType<Dictionary<string, object?>>()
                                .LastOrDefault(s => string.Equals(s.GetValueOrDefault("path")?.ToString(), step.File, StringComparison.OrdinalIgnoreCase) &&
                                    s.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created" or "skipped" &&
                                    s.GetValueOrDefault("type")?.ToString() is "edit" or "create" or "rename");
                            if (result != null) doneIndices.Add(i);
                        }
                    }

                    // Only generate new steps if all original steps are done but quality check still failed
                    if (!complete && (plan?.Plan?.Count == 0 || doneIndices.Count == (plan?.Plan?.Count ?? 0)))
                    {
                        await EmitLog(emitSse, "info", "All steps done — generating additional steps…", ct: ct);
                        var newSteps = await GenerateReplanStepsAsync(prompt, allSteps, plan,
                            steeringContext, projectRoot, emitSse, ct,
                            attachedFiles: attachedFiles, qualityCheckReason: reason);
                        if (newSteps?.Count > 0)
                        {
                            plan = MergePlans(plan ?? new AgentPlan(), new AgentPlan { Plan = newSteps });
                            if (emitSse)
                                await SendSse(Response, "plan",
                                    new { thinking = plan.Thinking, summary = "Replan: added steps", items = plan.Plan }, ct);

                            // Rebuild doneIndices with merged plan and execute remaining
                            var mergedDone = new HashSet<int>();
                            for (var i = 0; i < plan.Plan.Count; i++)
                            {
                                var step = plan.Plan[i];
                                var result = allSteps.OfType<Dictionary<string, object?>>()
                                    .LastOrDefault(s => string.Equals(s.GetValueOrDefault("path")?.ToString(), step.File, StringComparison.OrdinalIgnoreCase) &&
                                        s.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created" or "skipped" &&
                                        s.GetValueOrDefault("type")?.ToString() is "edit" or "create" or "rename");
                                if (result != null) mergedDone.Add(i);
                            }

                            var newResults = new List<object>();
                            await ExecutePlan(prompt, projectRoot, emitSse, "", plan, ct, newResults,
                                steeringContext: steeringContext, attachedFiles: attachedFiles,
                                completedStepIndices: mergedDone, cardId: cardId);
                            allSteps.AddRange(newResults);

                            var (ok3, _) = await AssessCompletion(prompt, allSteps, projectRoot, ct, plan, attachedFiles: attachedFiles);
                            complete = ok3;
                        }
                        else
                        {
                            await EmitLog(emitSse, "warn", "No additional steps needed — stopping.", ct: ct);
                            complete = plan?.Plan?.All(p =>
                            {
                                var result = allSteps.OfType<Dictionary<string, object?>>()
                                    .LastOrDefault(s => string.Equals(s.GetValueOrDefault("path")?.ToString(), p.File, StringComparison.OrdinalIgnoreCase) &&
                                        s.GetValueOrDefault("type")?.ToString() is "edit" or "create" or "rename");
                                return result != null && result.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created" or "skipped";
                            }) ?? true;
                        }
                    }
                }
                else
                {
                    await EmitLog(emitSse, "success", "Quality check passed.", ct: ct);
                }
            }
        }

        bool isEdited = allSteps.OfType<Dictionary<string, object?>>().Any(s => s.GetValueOrDefault("type")?.ToString() == "edit"); 
        // ── Build check ───────────────────────────────────────────────────
        bool buildOk = true;
        if (allSteps.Count > 0 && isEdited)
        {
            var cfg = await _configFile.LoadConfigAsync();
            var cmds = ParseBuildCommands(cfg.buildCommands);
            if (cmds.Count > 0)
            {
                if (emitSse)
                    await SendSse(Response, "phase",
                        new { phase = "build", message = $"Running {cmds.Count} build command(s)" }, ct);
                foreach (var cmd in cmds)
                {
                    var ok = await RunSmartBuildCheck(projectRoot, cmd, emitSse, ct);
                    if (!ok) { buildOk = false; }
                }
            }
        }

        // ── Repair pipeline ──────────────────────────────────────────────
        if (!buildOk && isEdited)
        {
            var answer = await AskUserAsync(
                "Build errors detected. Would you like the AI to analyze and attempt to fix them?",
                new List<QuestionField>
                {
                    new() { Key = "confirm", Label = "Auto-repair build errors?", Type = "select", DefaultValue = "no" }
                }, ct);
            var wantsRepair = answer.Count > 0 &&
                answer.TryGetValue("confirm", out var val) &&
                val?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true;
            if (wantsRepair)
                await RepairPipeline(projectRoot, emitSse, ct, prompt, steeringContext);
        }

        return (allSteps, plan, complete);
    }

    private static string BuildFailedEditHistory(List<object> allSteps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Failures from previous execution:");
        var failures = allSteps.OfType<Dictionary<string, object?>>()
            .Where(s => s.TryGetValue("type", out var t) && t?.ToString() == "edit" &&
                        (!s.TryGetValue("status", out var st) || st?.ToString() != "done"))
            .Take(8).ToList();
        if (failures.Count == 0) { sb.AppendLine("- No failed edits."); return sb.ToString(); }
        foreach (var f in failures)
        {
            sb.AppendLine($"- {f.GetValueOrDefault("path")}: {f.GetValueOrDefault("error") ?? f.GetValueOrDefault("status")}");
            if (f.TryGetValue("snippet", out var sn) && sn != null) sb.AppendLine($"  Nearby: {sn}");
        }
        return sb.ToString();
    }

    private static string PreviewForPrompt(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars] + "\n[truncated]";

    private static List<string> ParseBuildCommands(string buildCommands)
    {
        if (string.IsNullOrWhiteSpace(buildCommands)) return new List<string>();
        try { var arr = JsonSerializer.Deserialize<List<string>>(buildCommands); if (arr?.Count > 0) return arr.Where(c => !string.IsNullOrWhiteSpace(c)).ToList(); }
        catch { }
        return new List<string> { buildCommands.Trim() };
    }

    private static string BuildReplanPrompt(string originalPrompt, List<string> history, string? steeringContext = null,
        AgentPlan? existingPlan = null, List<object>? executedSteps = null,
        string qualityCheckReason = "", string fileContents = "")
    {
        var sb = new StringBuilder();
        sb.AppendLine("Previous plan did not fully complete. You must ONLY plan the FEWEST new steps needed.");
        sb.AppendLine("IMPORTANT: Only plan steps that address specific failures below. Do NOT repeat existing steps.");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(steeringContext)) { sb.AppendLine("## Steering"); sb.AppendLine(steeringContext); sb.AppendLine(); }

        // Show what was already planned and their results
        if (existingPlan?.Plan?.Count > 0)
        {
            sb.AppendLine("## Existing plan with results");
            foreach (var step in existingPlan.Plan)
            {
                // Look up the result for this step
                string? status = null;
                if (executedSteps != null)
                {
                    var result = executedSteps.OfType<Dictionary<string, object?>>()
                        .LastOrDefault(s =>
                            string.Equals(s.GetValueOrDefault("path")?.ToString(), step.File, StringComparison.OrdinalIgnoreCase));
                    if (result != null)
                        status = result.GetValueOrDefault("status")?.ToString();
                }
                var tag = status switch
                {
                    "done" or "modified" or "created" => "✓ DONE",
                    "skipped" => "○ SKIPPED",
                    "error" => "✗ FAILED",
                    _ => "… PENDING"
                };
                sb.AppendLine($"  {tag} {step.File}: {step.Change}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Original task"); sb.AppendLine(originalPrompt); sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(qualityCheckReason))
        {
            sb.AppendLine("## Quality check assessment");
            sb.AppendLine(qualityCheckReason);
            sb.AppendLine();
        }

        sb.AppendLine("## What went wrong");
        foreach (var h in history) sb.AppendLine(h);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(fileContents))
        {
            sb.AppendLine("## Current file contents");
            sb.Append(fileContents);
            sb.AppendLine();
        }

        sb.AppendLine("Add at most 1-2 new steps to fix ONLY the issues identified. If everything is done, return an EMPTY plan with no steps.");
        return sb.ToString();
    }

    /// <summary>
    /// Lightweight replan: asks the LLM for only the additional PlanSteps needed,
    /// without running discovery or full planning again.
    /// </summary>
    private async Task<List<PlanStep>?> GenerateReplanStepsAsync(
        string originalPrompt, List<object> executedSteps, AgentPlan? existingPlan,
        string? steeringContext, string projectRoot, bool emitSse, CancellationToken ct,
        List<string>? attachedFiles = null, string qualityCheckReason = "")
    {
        var failHist = BuildFailedEditHistory(executedSteps);

        // Read current content of all files that were modified or attached, for richer context
        var fileContents = new StringBuilder();
        var pathsToRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in executedSteps.OfType<Dictionary<string, object?>>())
        {
            var p = step.GetValueOrDefault("path")?.ToString();
            if (!string.IsNullOrWhiteSpace(p)) pathsToRead.Add(p.Replace('\\', '/'));
        }
        if (attachedFiles != null)
        {
            foreach (var f in attachedFiles) pathsToRead.Add(f.Replace('\\', '/'));
        }
        foreach (var relPath in pathsToRead.Take(8))
        {
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath));
            if (!System.IO.File.Exists(fullPath)) continue;
            var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
            fileContents.AppendLine($"### {relPath}\n```\n{content}\n```\n");
        }

        var replanPrompt = BuildReplanPrompt(originalPrompt, new List<string> { failHist },
            steeringContext, existingPlan, executedSteps, qualityCheckReason, fileContents.ToString());

        var (raw, _, llmError) = await CallLlmRaw(
            "You are a plan-fixer. Output ONLY valid JSON with a 'plan' array. Example: {\"plan\": [{\"file\": \"path/to/file.js\", \"change\": \"describe the change\", \"priority\": 1}]}. Max 1-2 steps. Empty array if all done.",
            replanPrompt, ct, TimeSpan.FromSeconds(30));

        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```")) { var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) cleaned = m.Groups[1].Value.Trim(); }
            var s2 = cleaned.IndexOf('{'); var e2 = cleaned.LastIndexOf('}');
            if (s2 >= 0 && e2 > s2) cleaned = cleaned[s2..(e2 + 1)];
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            if (!root.TryGetProperty("plan", out var planEl) || planEl.ValueKind != JsonValueKind.Array)
                return null;

            var steps = new List<PlanStep>();
            foreach (var item in planEl.EnumerateArray())
            {
                var file = item.TryGetProperty("file", out var f) ? f.GetString() : null;
                var change = item.TryGetProperty("change", out var c) ? c.GetString() : null;
                var priority = item.TryGetProperty("priority", out var p) ? p.GetInt32() : 1;
                if (!string.IsNullOrWhiteSpace(file) && !string.IsNullOrWhiteSpace(change))
                    steps.Add(new PlanStep { File = file, Change = change, Priority = priority });
            }
            return steps.Count > 0 ? steps : null;
        }
        catch
        {
            await EmitLog(emitSse, "warn", "Failed to parse replan steps from LLM response", ct: ct);
            return null;
        }
    }

    private async Task<List<object>> QuickPipeline(
        string prompt, string projectRoot, bool emitSse, AgentPlan fastPlan, CancellationToken ct,
        string? cardId = null)
    {
        await EmitLog(emitSse, "info", $"Fast-path → {fastPlan.Summary}", ct: ct);
        if (emitSse)
            await SendSse(Response, "plan",
                new { thinking = fastPlan.Thinking, summary = fastPlan.Summary, items = fastPlan.Plan }, ct);
        var allResults = new List<object>();
        await ExecutePlan(prompt, projectRoot, emitSse, "", fastPlan, ct, allResults,
            cardId: cardId);
        return allResults;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  PLANNING CONVERGENCE LOOP
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Minimum planner self-confidence (0-100) required to stop iterating and execute.</summary>
    private const int PlanScoreThreshold = 75;
    /// <summary>Upper bound on planning iterations so a low-scoring/exploring model still terminates.</summary>
    private const int MaxPlanningIterations = 4;

    /// <summary>
    /// Iterates planning until the planner is confident (score ≥ threshold) or the iteration
    /// budget is exhausted, gathering more context via _explore steps in between. This replaces
    /// the old single-shot Plan → Explore → Replan sequence: the number of loops is now data-driven
    /// off the planner's own score, so confident plans stop early and uncertain ones gather more
    /// context before committing — without an open-ended "invent more work" path.
    /// </summary>
    private async Task<(AgentPlan plan, string discoveryContext)> RunPlanningConvergenceLoop(
        string prompt, string discoveryContext, string projectRoot, bool emitSse,
        CancellationToken ct, string? steeringContext)
    {
        AgentPlan? best = null;
        var steering = steeringContext;

        for (var iter = 1; iter <= MaxPlanningIterations; iter++)
        {
            var plan = await AnalyzePromptAndPlanCodeChanges(
                prompt, discoveryContext, projectRoot, emitSse, ct, steering);

            if (plan == null || plan.Plan.Count == 0)
            {
                if (best != null) break; // reuse the last good plan rather than failing
                throw new InvalidOperationException("LLM returned an empty or unparseable plan.");
            }

            // If the planner asked to read more files, gather that context and replan.
            // Exploration rounds never count as a converged plan, so _explore steps can
            // never leak into the executable plan.
            var exploreSteps = plan.Plan
                .Where(p => p.File.Equals("_explore", StringComparison.OrdinalIgnoreCase)).ToList();
            if (exploreSteps.Count > 0)
            {
                await EmitLog(emitSse, "info",
                    $"Planning {iter}/{MaxPlanningIterations}: planner requested {exploreSteps.Count} exploration target(s) — gathering context…", ct: ct);
                discoveryContext = await ExplorationPipeline(exploreSteps, discoveryContext, projectRoot, emitSse, ct);
                if (iter == MaxPlanningIterations)
                    steering = AppendExploreSteering(steeringContext); // last shot: force a real plan
                continue;
            }

            if (best == null || plan.Score > best.Score) best = plan;

            await EmitLog(emitSse, "info",
                $"Planning {iter}/{MaxPlanningIterations} — score {plan.Score}/100 ({plan.Plan.Count} step(s))",
                new { plan.Score }, ct: ct);

            if (plan.Score >= PlanScoreThreshold)
            {
                await EmitLog(emitSse, "success",
                    $"Plan converged: score {plan.Score} ≥ {PlanScoreThreshold}.", ct: ct);
                best = plan;
                break;
            }

            if (iter < MaxPlanningIterations)
            {
                await EmitLog(emitSse, "info",
                    $"Plan score {plan.Score} below {PlanScoreThreshold} — refining…", ct: ct);
                steering = BuildLowScoreSteering(plan, steeringContext);
            }
            else
            {
                await EmitLog(emitSse, "warn",
                    $"Planning budget exhausted at score {best!.Score} — proceeding with best plan.", ct: ct);
            }
        }

        // Only-ever-explored fallback: force one final plan with no further exploration.
        if (best == null)
        {
            var forced = await AnalyzePromptAndPlanCodeChanges(
                prompt, discoveryContext, projectRoot, emitSse, ct, AppendExploreSteering(steeringContext));
            best = forced?.Plan.Count > 0
                ? forced
                : throw new InvalidOperationException("Planner did not produce an actionable plan after exploration.");
            best.Plan = best.Plan
                .Where(p => !p.File.Equals("_explore", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return (best, discoveryContext);
    }

    /// <summary>Steering that nudges a low-confidence planner to gather context or sharpen steps — never to invent extra work.</summary>
    private static string BuildLowScoreSteering(AgentPlan plan, string? prior)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Your previous plan scored {plan.Score}/100, below the confidence threshold of {PlanScoreThreshold}.");
        sb.AppendLine("Raise your confidence by EITHER:");
        sb.AppendLine("  • Emitting _explore steps (a file path or glob in \"change\") to read files you are unsure about, OR");
        sb.AppendLine("  • Making each step more precise so you are confident it fully solves the task.");
        sb.AppendLine("Plan ONLY what the user's request requires — do not invent extra files, features, or refactors.");
        if (!string.IsNullOrWhiteSpace(prior)) { sb.AppendLine(); sb.AppendLine(prior); }
        return sb.ToString();
    }

    /// <summary>Steering that forces the planner to stop exploring and emit the final edit plan.</summary>
    private static string AppendExploreSteering(string? prior)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You have already explored the relevant files. Produce the final edit plan now.");
        sb.AppendLine("Do NOT emit any more _explore steps. Plan only the edits the task requires — no extra work.");
        if (!string.IsNullOrWhiteSpace(prior)) { sb.AppendLine(); sb.AppendLine(prior); }
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  UNIFIED PIPELINE  (discover → plan → execute)
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<(List<object> steps, AgentPlan plan)> UnifiedPipeline(
        string prompt, string projectRoot, bool emitSse, CancellationToken ct,
        List<string>? attachedFiles = null,
        bool skipContextReview = false,
        string? steeringContext = null,
        string? cardId = null)
    {
        var allSteps = new List<object>();

        // Phase 1: Discover
        await EmitLog(emitSse, "info", "Phase 1 — DISCOVER", new { prompt, attachedFiles, steeringContext, cardId }, ct: ct);
        var (discoveryContext, ds) = await RunBootstrapDiscovery(prompt, projectRoot, emitSse, attachedFiles, ct);
        allSteps.AddRange(ds);

        // Context review (let user trim files before planning)
        if (emitSse && !skipContextReview)
            discoveryContext = await RunContextReview(ds, discoveryContext, allSteps, ct);

        // Phase 2: Plan
        await EmitLog(emitSse, "info", "Phase 2 — PLAN", ct: ct);
        if (emitSse)
            await SendSse(Response, "phase",
                new { phase = "plan", message = "Planning...", contextSize = discoveryContext.Length, prompt }, ct);

        var (plan, convergedContext) = await RunPlanningConvergenceLoop(
            prompt, discoveryContext, projectRoot, emitSse, ct, steeringContext);
        discoveryContext = convergedContext;

        if (emitSse && !string.IsNullOrWhiteSpace(plan.Thinking))
            await SendSse(Response, "thinking", new { text = plan.Thinking }, ct);

        await EmitLog(emitSse, "info",
            $"Plan: {plan.Plan.Count} step(s) — {string.Join(", ", plan.Plan.Select(p => p.File))}",
            new { plan }, ct: ct);

        if (emitSse)
            await SendSse(Response, "plan",
                new { thinking = plan.Thinking, summary = plan.Summary, items = plan.Plan }, ct);

        allSteps.Add(new Dictionary<string, object?>
        {
            ["index"] = allSteps.Count,
            ["type"] = "plan",
            ["status"] = "complete",
            ["description"] = "Plan complete"
        });

        // Phase 2.75: Plan validation — final safety gate after convergence.
        var validationReason = await ValidatePlanAsync(prompt, plan, ct);
        if (_gracefulStop)
        {
            await EmitLog(emitSse, "warn", "User did not respond to command confirmation — skipping card.", ct: ct);
            return (allSteps, plan);
        }
        if (validationReason != null)
        {
            await EmitLog(emitSse, "warn",
                $"Plan validation failed: {validationReason} — replanning…", ct: ct);
            var validationSteering = $"A reviewer flagged the previous plan: {validationReason}. " +
                "Fix exactly that issue — do not add unrelated files, features, or refactors." +
                (string.IsNullOrWhiteSpace(steeringContext) ? "" : $"\n\n{steeringContext}");
            var replan = await AnalyzePromptAndPlanCodeChanges(
                prompt, discoveryContext, projectRoot, emitSse, ct, validationSteering);
            if (replan != null && replan.Plan.Count > 0)
            {
                plan = MergePlans(plan, replan);
                if (emitSse)
                    await SendSse(Response, "plan",
                        new { thinking = plan.Thinking, summary = plan.Summary, items = plan.Plan }, ct);
            }
        } else {
            await EmitLog(emitSse, "success", $"Plan validation passed.", ct: ct);
        }

        // Phase 3: Execute
        await EmitLog(emitSse, "info", "Phase 3 — EXECUTE", ct: ct);
        if (emitSse)
            await SendSse(Response, "phase", new { phase = "execute", message = "Executing plan…" }, ct);

        await ExecutePlan(prompt, projectRoot, emitSse, discoveryContext, plan, ct, allSteps,
            steeringContext: steeringContext, attachedFiles: attachedFiles,
            cardId: cardId);

        // ── Post-execution verification: re-check with LLM that task is 100% complete ──
        var (taskComplete, verificationDetails) = await PostExecuteVerify(prompt, projectRoot, emitSse, allSteps, ct);
        if (!taskComplete)
        {
            await EmitLog(emitSse, "warn", $"Post-execution verification: {verificationDetails}. Re-planning...", ct: ct);
            var freshContext = AgentUtilities.BuildDiscoveryTextFromSteps(allSteps);
            // Include verification failures as steering so the LLM knows what to fix
            var verifySteering = steeringContext;
            if (!string.IsNullOrWhiteSpace(verificationDetails))
                verifySteering = $"Fix these issues:\n{verificationDetails}\n\n{(string.IsNullOrWhiteSpace(steeringContext) ? "" : steeringContext)}";
            var replan = await AnalyzePromptAndPlanCodeChanges(
                prompt, freshContext, projectRoot, emitSse, ct, verifySteering);
            if (replan != null && replan.Plan.Count > 0)
            {
                await ExecutePlan(prompt, projectRoot, emitSse, freshContext, replan, ct, allSteps,
                    steeringContext: verifySteering, cardId: cardId);
            }
        }
        else
        {
            await EmitLog(emitSse, "success", "Post-execution verification: task is 100% complete.", ct: ct);
            // Signal to Orchestrate that PostExecuteVerify already confirmed completion,
            // so it can skip the redundant AssessCompletion check.
            allSteps.Add(new Dictionary<string, object?> { ["type"] = "verified_complete", ["status"] = "done" });
        }

        return (allSteps, plan);
    }

    private async Task<Dictionary<string, string>> AskUserAsync(string question, List<QuestionField>? fields = null, CancellationToken ct = default)
    {
        var qId = Guid.NewGuid().ToString();
        var pending = new PendingQuestion
        {
            Id = qId,
            Question = question,
            Fields = fields ?? new List<QuestionField>(),
            CreatedUtc = DateTime.UtcNow,
            Answer = new TaskCompletionSource<Dictionary<string, string>>()
        };
        _pendingQuestions[qId] = pending;

        await SendSse(Response, "ask-question", new
        {
            id = qId,
            question = pending.Question,
            fields = pending.Fields.Select(f => new { f.Key, f.Label, f.Type, f.DefaultValue }).ToList()
        }, ct);

        try
        {
            var answers = await pending.Answer.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
            return answers;
        }
        catch (TimeoutException) { return new Dictionary<string, string>(); }
        catch (OperationCanceledException) { return new Dictionary<string, string>(); }
        finally { _pendingQuestions.TryRemove(qId, out _); }
    }

    private async Task<string> RunContextReview(
        List<object> ds, string discoveryContext, List<object> allSteps, CancellationToken ct)
    {
        var readFiles = ds.OfType<Dictionary<string, object?>>()
            .Where(s => s.TryGetValue("type", out var t) && t?.ToString() == "read")
            .Select(s => s.GetValueOrDefault("path")?.ToString())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (readFiles.Count == 0) return discoveryContext;

        var reviewId = Guid.NewGuid().ToString();
        var review = new PendingContextReview
        {
            Id = reviewId,
            Files = readFiles.Where(f => f != null).ToList()!,
            CreatedUtc = DateTime.UtcNow,
            Answer = new TaskCompletionSource<List<string>>()
        };
        _pendingContextReviews[reviewId] = review;

        await SendSse(Response, "context-review", new
        {
            id = reviewId,
            files = readFiles.Select(f => new { path = f }).ToList(),
            contextSize = discoveryContext.Length
        }, ct);

        try
        {
            var confirmedFiles = await review.Answer.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            var confirmedSet = new HashSet<string>(confirmedFiles, StringComparer.OrdinalIgnoreCase);
            if (confirmedFiles.Count < readFiles.Count)
            {
                var filtered = ds.Where(item =>
                {
                    if (item is not Dictionary<string, object?> r) return true;
                    var type = r.TryGetValue("type", out var t) ? t?.ToString() : "";
                    if (type != "read") return true;
                    var p = r.GetValueOrDefault("path")?.ToString();
                    return !string.IsNullOrWhiteSpace(p) && confirmedSet.Contains(p);
                }).ToList();
                allSteps.Clear(); allSteps.AddRange(filtered);
                return AgentUtilities.BuildDiscoveryTextFromSteps(filtered);
            }
        }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }
        finally { _pendingContextReviews.TryRemove(reviewId, out _); }

        return discoveryContext;
    }

    private async Task<string> ExplorationPipeline(
        List<PlanStep> exploreSteps, string discoveryContext,
        string projectRoot, bool emitSse, CancellationToken ct)
    {
        var enriched = new StringBuilder(discoveryContext);
        enriched.AppendLine();
        foreach (var step in exploreSteps)
        {
            var target = step.Change?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(target)) continue;
            await EmitLog(emitSse, "info", $"Exploring: {target}", ct: ct);
            if (target.Contains('*') || target.Contains('?'))
            {
                var sep = Path.DirectorySeparatorChar;
                var pattern = target.Replace('/', sep);
                var dir = Path.GetDirectoryName(pattern) ?? ".";
                var searchDir = Path.GetFullPath(Path.Combine(projectRoot, dir));
                if (!Directory.Exists(searchDir)) continue;
                foreach (var match in Directory.EnumerateFiles(searchDir, Path.GetFileName(pattern), SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/')).Take(10))
                {
                    var fp = Path.GetFullPath(Path.Combine(projectRoot, match.Replace('/', sep)));
                    if (!System.IO.File.Exists(fp)) continue;
                    var content = await System.IO.File.ReadAllTextAsync(fp, Encoding.UTF8, ct);
                    enriched.AppendLine($"### {match}\n```\n{content}\n```\n");
                }
            }
            else
            {
                var fp = Path.GetFullPath(Path.Combine(projectRoot, target.Replace('/', Path.DirectorySeparatorChar)));
                if (System.IO.File.Exists(fp) && AgentUtilities.IsPathUnderRoot(fp, projectRoot))
                {
                    var content = await System.IO.File.ReadAllTextAsync(fp, Encoding.UTF8, ct);
                    enriched.AppendLine($"### {target}\n```\n{content}\n```\n");
                }
            }
        }
        return enriched.ToString();
    }

    /// <summary>
    /// After all plan steps execute, re-reads modified files and asks the LLM
    /// to verify whether the original task is 100% complete. Returns false if
    /// the LLM identifies remaining work.
    /// </summary>
    private async Task<(bool complete, string details)> PostExecuteVerify(
        string originalPrompt, string projectRoot, bool emitSse,
        List<object> allResults, CancellationToken ct)
    {
        // Collect all files that were modified/skipped by the plan
        var modifiedPaths = allResults
            .OfType<Dictionary<string, object?>>()
            .Where(r => r.TryGetValue("type", out var t) && t?.ToString() == "edit")
            .Select(r => r.GetValueOrDefault("path")?.ToString())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (modifiedPaths.Count == 0) return (true, ""); // nothing to verify

        var sb = new StringBuilder();
        sb.AppendLine("### ORIGINAL TASK ###");
        sb.AppendLine(originalPrompt);
        sb.AppendLine();
        sb.AppendLine("### CURRENT STATE OF MODIFIED FILES ###");

        // Track related type definition files to include for type-consistency checks
        var typeFilesToInclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relPath in modifiedPaths)
        {
            var fullPath = Path.GetFullPath(
                Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
            if (System.IO.File.Exists(fullPath))
            {
                var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                sb.AppendLine($"\n### {relPath}");
                sb.AppendLine("```");
                sb.AppendLine(content);
                sb.AppendLine("```");

                // Resolve local imports to find related type definitions
                var ext = Path.GetExtension(relPath).ToLowerInvariant();
                if (ext is ".ts" or ".tsx")
                {
                    foreach (var importLine in content.Split('\n')
                        .Where(l => l.TrimStart().StartsWith("import ", StringComparison.Ordinal)))
                    {
                        var m = Regex.Match(importLine, @"from\s+['""]([^'""]+)['""]");
                        if (!m.Success) continue;
                        var importPath = m.Groups[1].Value;
                        if (importPath.StartsWith("."))
                        {
                            // Resolve relative import to file path
                            var baseDir = Path.GetDirectoryName(fullPath) ?? "";
                            var resolved = Path.GetFullPath(Path.Combine(baseDir, importPath));
                            // Try .ts, .tsx, /index.ts, /index.tsx
                            foreach (var suffix in new[] { ".ts", ".tsx", "/index.ts", "/index.tsx" })
                            {
                                var candidate = resolved + suffix;
                                if (System.IO.File.Exists(candidate))
                                {
                                    typeFilesToInclude.Add(candidate);
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }

        // Include resolved type definition files (limit to 5 to avoid context overload)
        if (typeFilesToInclude.Count > 0)
        {
            sb.AppendLine("\n### RELATED TYPE DEFINITIONS ###");
            var count = 0;
            foreach (var typeFullPath in typeFilesToInclude.Take(5))
            {
                var content = await System.IO.File.ReadAllTextAsync(typeFullPath, Encoding.UTF8, ct);
                var rel = Path.GetRelativePath(projectRoot, typeFullPath).Replace('\\', '/');
                sb.AppendLine($"\n### {rel}");
                sb.AppendLine("```");
                sb.AppendLine(content);
                sb.AppendLine("```");
                count++;
            }
            if (typeFilesToInclude.Count > count)
                sb.AppendLine($"\n... and {typeFilesToInclude.Count - count} more type files (omitted)");
        }

        sb.AppendLine();
        sb.AppendLine("Based on the original task above and the current state of all modified files and their type definitions,");
        sb.AppendLine("check for ALL of the following:");
        sb.AppendLine("1. Is the original task fully implemented?");
        sb.AppendLine("2. Do ALL property accesses in the code exist on their respective types/interfaces?");
        sb.AppendLine("   (e.g. obj.someProperty — does 'someProperty' exist in the type of 'obj'?)");
        sb.AppendLine("3. Are ALL referenced methods, functions, and classes defined or imported?");
        sb.AppendLine("4. Are ALL imports present for every type used?");
        sb.AppendLine("5. Would the code compile without errors?");
        sb.AppendLine();
        sb.AppendLine("Answer with a single JSON object:");
        sb.AppendLine("{ \"complete\": true|false, \"reason\": \"short explanation\", \"issues\": [\"issue1\", \"issue2\"] }");
        sb.AppendLine("Set complete=true only if the task is fully implemented AND the code would compile.");
        sb.AppendLine("Set complete=false if anything is missing, broken, or would cause compilation errors.");
        sb.AppendLine("Include a brief list of specific issues in the 'issues' array when complete=false.");

        var verifySystemPrompt = "You are a strict QA verifier for TypeScript/C# code. Check whether a programming task is 100% complete AND whether the code would compile. Pay special attention to properties accessed on typed objects — verify each one exists in the type definition. Output ONLY a JSON object with 'complete' (bool), 'reason' (string), and 'issues' (array of strings).";

        var (raw, _, error) = await CallLlmRawStreaming(
            verifySystemPrompt, sb.ToString(), emitSse, ct,
            requestTimeout: TimeSpan.FromMinutes(3), maxTokens: 512);

        if (string.IsNullOrWhiteSpace(raw))
        {
            await EmitLog(emitSse, "warn", $"Verification LLM returned empty: {error}", ct: ct);
            return (true, ""); // can't verify — assume ok
        }

        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (m.Success) cleaned = m.Groups[1].Value.Trim();
            }
            var fb = cleaned.IndexOf('{');
            var lb = cleaned.LastIndexOf('}');
            if (fb >= 0 && lb > fb) cleaned = cleaned[fb..(lb + 1)];

            using var doc = JsonDocument.Parse(cleaned);
            if (doc.RootElement.TryGetProperty("complete", out var completeEl))
            {
                var isComplete = completeEl.GetBoolean();
                var reason = doc.RootElement.TryGetProperty("reason", out var rEl) ? rEl.GetString() : "";
                var issues = doc.RootElement.TryGetProperty("issues", out var iEl) && iEl.ValueKind == JsonValueKind.Array
                    ? string.Join("; ", iEl.EnumerateArray().Select(e => e.GetString() ?? ""))
                    : "";
                var details = reason + (string.IsNullOrWhiteSpace(issues) ? "" : $"\nIssues: {issues}");
                await EmitLog(emitSse, isComplete ? "info" : "warn",
                    $"Verification: complete={isComplete}, reason={reason}{(string.IsNullOrWhiteSpace(issues) ? "" : $", issues=[{issues}]")}", ct: ct);
                return (isComplete, details);
            }
        }
        catch { }

        return (true, ""); // parse failure — assume ok
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  EXECUTE PLAN  — progressive checklist, per-step resolve+retry
    // ═══════════════════════════════════════════════════════════════════════

    private async Task ExecutePlan(
        string prompt, string projectRoot, bool emitSse, string discoveryContext,
        AgentPlan plan, CancellationToken ct, List<object> allResults,
        string? steeringContext = null, List<string>? attachedFiles = null,
        HashSet<int>? completedStepIndices = null, string? cardId = null)
    {
        var stepIndex = 0;
        var planItems = plan.Plan.ToList();
        var webCtx = new StringBuilder();
        var checkpointCount = 0;
        const int MaxCheckpoints = 3;
        completedStepIndices ??= new HashSet<int>();

        for (var itemIdx = 0; itemIdx < planItems.Count; itemIdx++)
        {
            ct.ThrowIfCancellationRequested();

            var item = planItems[itemIdx];

            // Skip steps already completed in a previous run
            if (completedStepIndices.Contains(itemIdx))
            {
                if (emitSse)
                    await SendSse(Response, "step", new
                    {
                        index = stepIndex,
                        type = "plan",
                        description = item.Change,
                        path = item.File,
                        status = "done",
                        skipped = true,
                        planItemIndex = itemIdx,
                        message = "Already completed in a previous run"
                    }, ct);
                stepIndex++;
                continue;
            }

            // Skip steps cancelled by user
            if (!string.IsNullOrWhiteSpace(cardId) && _cancelledSteps.TryGetValue(cardId, out var cancelled))
            {
                bool isCancelled;
                lock (cancelled) { isCancelled = cancelled.Contains(itemIdx); }
                if (isCancelled)
                {
                    if (emitSse)
                        await SendSse(Response, "step", new
                        {
                            index = stepIndex,
                            type = "plan",
                            description = item.Change,
                            path = item.File,
                            status = "skipped",
                            planItemIndex = itemIdx,
                            message = "Cancelled by user"
                        }, ct);
                    stepIndex++;
                    continue;
                }
            }

            var planFile = item.File;
            var changeDesc = item.Change;

            // ── Special markers ───────────────────────────────────────────

            if (planFile.Equals("_done", StringComparison.OrdinalIgnoreCase))
            {
                await EmitLog(emitSse, "success", $"Task self-reported complete: {changeDesc}", ct: ct);
                if (emitSse) await SendSse(Response, "done_signal", new { message = changeDesc }, ct);
                allResults.Add(new Dictionary<string, object?> { ["type"] = "done_signal", ["status"] = "done", ["output"] = changeDesc });
                return;
            }

            if (planFile.Equals("_checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                if (++checkpointCount > MaxCheckpoints) { await EmitLog(emitSse, "warn", "Max checkpoints reached", ct: ct); continue; }
                await EmitLog(emitSse, "info", $"Checkpoint {checkpointCount}/{MaxCheckpoints}: {changeDesc}", ct: ct);
                if (emitSse) await SendSse(Response, "phase", new { phase = "checkpoint", message = $"Checkpoint {checkpointCount}" }, ct);
                allResults.Add(new Dictionary<string, object?> { ["type"] = "checkpoint", ["status"] = "done", ["output"] = changeDesc });
                var remaining = planItems.Skip(itemIdx + 1).ToList();
                if (remaining.Count > 0)
                {
                    var newSteps = await CheckpointReplan(prompt, discoveryContext, remaining, allResults, projectRoot, emitSse, ct, steeringContext);
                    if (newSteps?.Count > 0)
                    {
                        planItems = MergePlanSteps(planItems, newSteps);
                        if (emitSse) await SendSse(Response, "plan", new { summary = $"Phase {checkpointCount + 1}", items = planItems }, ct);
                    }
                }
                continue;
            }

            if (planFile.Equals("_continue", StringComparison.OrdinalIgnoreCase))
            {
                await EmitLog(emitSse, "info", $"Continuation: {changeDesc}", ct: ct);
                allResults.Add(new Dictionary<string, object?> { ["type"] = "continue_signal", ["status"] = "done", ["output"] = changeDesc });
                continue;
            }

            if (planFile.Equals("_rename", StringComparison.OrdinalIgnoreCase) ||
                planFile.Equals("_rename_file", StringComparison.OrdinalIgnoreCase))
            {
                stepIndex = await ExecuteRenameFromChange(changeDesc, projectRoot, emitSse, ct, allResults, stepIndex);
                continue;
            }

            if (planFile.Equals("_delete_file", StringComparison.OrdinalIgnoreCase))
            {
                var target = changeDesc.Trim().Trim('"', '\'').Replace('\\', '/');
                var fullPath = Path.GetFullPath(Path.Combine(projectRoot, target.Replace('/', Path.DirectorySeparatorChar)));
                if (AgentUtilities.IsPathUnderRoot(fullPath, projectRoot) && System.IO.File.Exists(fullPath))
                {
                    System.IO.File.Delete(fullPath);
                    await EmitLog(emitSse, "success", $"Deleted {target}", ct: ct);
                    allResults.Add(new Dictionary<string, object?> { ["type"] = "rename", ["status"] = "done", ["path"] = target, ["editAction"] = "deleted" });
                }
                else await EmitLog(emitSse, "warn", $"Delete target not found: {target}", ct: ct);
                continue;
            }

            if (planFile.Equals("_git", StringComparison.OrdinalIgnoreCase))
            {
                stepIndex = await ExecuteGitStep(changeDesc, projectRoot, emitSse, ct, allResults, stepIndex);
                continue;
            }

            if (planFile.Equals("_show", StringComparison.OrdinalIgnoreCase) ||
                planFile.Equals("_display", StringComparison.OrdinalIgnoreCase))
            {
                var text = changeDesc.Trim().Trim('`', '"', '\'');
                await EmitLog(emitSse, "info", text, ct: ct);
                if (emitSse) await SendSse(Response, "show", new { text }, ct);
                allResults.Add(new Dictionary<string, object?> { ["status"] = "done", ["type"] = "show", ["output"] = text });
                continue;
            }

            if (planFile.Equals("_create_file", StringComparison.OrdinalIgnoreCase))
            {
                await EmitLog(emitSse, "info", $"Creating file: {changeDesc}", ct: ct);
                var cr = await HandleCreateFile(changeDesc, projectRoot, prompt, discoveryContext, stepIndex, emitSse, ct, null, attachedFiles);
                stepIndex += cr.stepsCount; allResults.AddRange(cr.results);
                continue;
            }

            if (planFile.Equals("_ping", StringComparison.OrdinalIgnoreCase))
            { stepIndex = await ExecutePingStep(changeDesc, projectRoot, emitSse, ct, allResults, stepIndex); continue; }

            if (planFile.Equals("_package_install", StringComparison.OrdinalIgnoreCase))
            { stepIndex = await ExecutePackageInstallStep(changeDesc, projectRoot, emitSse, ct, allResults, stepIndex); continue; }

            if (planFile.Equals("_command", StringComparison.OrdinalIgnoreCase))
            {
                var cmd = changeDesc.Trim().Trim('`', '"', '\'');
                if (!string.IsNullOrWhiteSpace(cmd))
                {
                    await EmitLog(emitSse, "info", $"Command: {cmd}", ct: ct);
                    _terminal.Start();
                    var cs = new AgentStep { Index = 0, Type = "command", Command = cmd, Description = cmd };
                    var cr = await ExecuteSteps(new List<AgentStep> { cs }, projectRoot, stepIndex, emitSse, ct);
                    stepIndex += cr.Count; allResults.AddRange(cr);
                }
                continue;
            }

            if (planFile.Equals("_web_search", StringComparison.OrdinalIgnoreCase) ||
                planFile.Equals("_web_fetch", StringComparison.OrdinalIgnoreCase))
            {
                (stepIndex, discoveryContext) = await ExecuteWebPlanStep(planFile, changeDesc, prompt, projectRoot, emitSse, ct,
                    allResults, planItems, itemIdx, stepIndex, discoveryContext, webCtx);
                continue;
            }

            if (planFile.Equals("_move_file", StringComparison.OrdinalIgnoreCase))
            {
                var dst = AgentUtilities.ExtractTargetPath(changeDesc, planFile, projectRoot);
                if (dst != null)
                {
                    var rs = new AgentStep { Index = 0, Type = "rename", Path = planFile, ToPath = dst, Description = $"Move {planFile} → {dst}" };
                    var rr = await ExecuteSteps(new List<AgentStep> { rs }, projectRoot, stepIndex, emitSse, ct);
                    stepIndex += rr.Count; allResults.AddRange(rr);
                }
                continue;
            }

            // ── File edit: resolve then apply ─────────────────────────────
            if (AgentUtilities.IsRelativePath(planFile))
            {
                // ResolveAndApplyEdit handles plan-provided oldString + unbounded LLM retries internally
                var prevCount = allResults.Count;
                stepIndex = await ResolveAndApplyEdit(item, projectRoot, emitSse,
                    ct, allResults, stepIndex,
                    prompt, plan,     
                    itemIdx, cardId);

                if (allResults.Count > prevCount &&
                    allResults[^1] is Dictionary<string, object?> lastDict &&
                    lastDict.TryGetValue("status", out var st) && st?.ToString() == "error")
                {
                    await EmitLog(emitSse, "error",
                        $"✗ Step permanently failed for {planFile} — {lastDict.GetValueOrDefault("error")}", ct: ct);
                }
                else if (allResults.Count > prevCount)
                {
                    // Edit succeeded — check if more work from the original prompt remains
                    // by re-running the planner against current file state.
                    var remainingSteps = planItems.Skip(itemIdx + 1)
                        .Where(p => !string.IsNullOrWhiteSpace(p.File)).ToList();
                    if (remainingSteps.Count == 0)
                    {
                        var moreSteps = await GenerateReplanStepsAsync(prompt, allResults, plan,
                            steeringContext, projectRoot, emitSse, ct, attachedFiles: attachedFiles);
                        if (moreSteps != null && moreSteps.Count > 0)
                        {
                            planItems = MergePlanSteps(planItems, moreSteps);
                            if (emitSse)
                                await SendSse(Response, "plan",
                                    new { summary = $"Added {moreSteps.Count} step(s) after verifying prompt", items = planItems }, ct);
                        }
                    }
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(planFile))
            {
                await EmitLog(emitSse, "warn", "Plan item with empty file — skipping", new { item }, ct: ct);
            }
        }
    }

    private async Task<int> ExecuteRenameFromChange(
        string changeDesc, string projectRoot, bool emitSse, CancellationToken ct,
        List<object> allResults, int stepIndex)
    {
        string? src = null, dst = null;
        var arrow = changeDesc.IndexOf('→');
        if (arrow > 0) { src = changeDesc[..arrow].Trim(); dst = changeDesc[(arrow + 1)..].Trim(); }
        else
        {
            var toIdx = changeDesc.LastIndexOf(" to ", StringComparison.OrdinalIgnoreCase);
            if (toIdx > 0) { src = changeDesc[..toIdx].Trim(); dst = changeDesc[(toIdx + 4)..].Trim(' ', '"', '\''); }
        }
        if (!string.IsNullOrWhiteSpace(src) && !string.IsNullOrWhiteSpace(dst))
        {
            src = src.Replace('\\', '/').Trim('/');
            dst = dst.Replace('\\', '/').TrimEnd('/');
            if (!dst.Contains('/') && src.Contains('/'))
                dst = src[..(src.LastIndexOf('/') + 1)] + dst;
            var rs = new AgentStep { Index = 0, Type = "rename", Path = src, ToPath = dst, Description = $"Rename {src} → {dst}" };
            var rr = await ExecuteSteps(new List<AgentStep> { rs }, projectRoot, stepIndex, emitSse, ct);
            stepIndex += rr.Count; allResults.AddRange(rr);
        }
        else await EmitLog(emitSse, "error", $"_rename: could not parse src/dst from: {changeDesc}", ct: ct);
        return stepIndex;
    }

    private async Task<int> ExecuteGitStep(
        string changeDesc, string projectRoot, bool emitSse, CancellationToken ct,
        List<object> allResults, int stepIndex)
    {
        var lower = (changeDesc.Trim().Trim('`', '"', '\'') + " ").ToLowerInvariant();
        string gitCmd;
        if (lower.StartsWith("commit") || lower.Contains("commit all"))
        {
            var mm = Regex.Match(changeDesc, "\"([^\"]+)\"");
            var msg = mm.Success ? mm.Groups[1].Value : $"Auto-commit {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            gitCmd = $"git add -A && git commit -m \"{msg.Replace("\"", "\\\"")}\"";
        }
        else if (lower.StartsWith("revert") || lower.Contains("discard")) gitCmd = "git checkout -- .";
        else if (lower.StartsWith("pull")) gitCmd = "git pull";
        else if (lower.StartsWith("sync") || lower.Contains("push")) gitCmd = "git pull && git push";
        else
        {
            gitCmd = changeDesc.Trim().Trim('`', '"', '\'');
            if (!gitCmd.StartsWith("git ", StringComparison.OrdinalIgnoreCase)) gitCmd = "git " + gitCmd;
        }
        await EmitLog(emitSse, "info", $"Git: {gitCmd}", ct: ct);
        _terminal.Start();
        var gs = new AgentStep { Index = 0, Type = "command", Command = gitCmd, Description = gitCmd };
        var gr = await ExecuteSteps(new List<AgentStep> { gs }, projectRoot, stepIndex, emitSse, ct);
        stepIndex += gr.Count; allResults.AddRange(gr);
        return stepIndex;
    }

    private async Task<int> ExecutePingStep(
        string changeDesc, string projectRoot, bool emitSse, CancellationToken ct,
        List<object> allResults, int stepIndex)
    {
        var pingCmd = changeDesc.Trim().Trim('`', '"', '\'');
        if (pingCmd.Contains("<llamaUrl>", StringComparison.OrdinalIgnoreCase))
        {
            var baseUrl = await GetLlamaBaseUrl();
            var uri = new Uri(baseUrl);
            pingCmd = OperatingSystem.IsWindows()
                ? $"powershell -Command \"Test-NetConnection {uri.Host} -Port {uri.Port} -WarningAction SilentlyContinue | Select-Object TcpTestSucceeded | Format-List\""
                : $"nc -zv -w 2 {uri.Host} {uri.Port} 2>&1";
        }
        await EmitLog(emitSse, "info", $"Ping: {pingCmd}", ct: ct);
        _terminal.Start();
        var cs = new AgentStep { Index = 0, Type = "command", Command = pingCmd, Description = pingCmd };
        var cr = await ExecuteSteps(new List<AgentStep> { cs }, projectRoot, stepIndex, emitSse, ct);
        stepIndex += cr.Count; allResults.AddRange(cr);
        return stepIndex;
    }

    private async Task<int> ExecutePackageInstallStep(
        string changeDesc, string projectRoot, bool emitSse, CancellationToken ct,
        List<object> allResults, int stepIndex)
    {
        var installCmd = changeDesc.Trim().Trim('`', '"', '\'');
        await EmitLog(emitSse, "info", $"Package install: {installCmd}", ct: ct);
        _terminal.Start();
        var cs = new AgentStep { Index = 0, Type = "command", Command = installCmd, Description = installCmd };
        var cr = await ExecuteSteps(new List<AgentStep> { cs }, projectRoot, stepIndex, emitSse, ct);
        stepIndex += cr.Count; allResults.AddRange(cr);
        return stepIndex;
    }

    private async Task<(int stepIndex, string discoveryContext)> ExecuteWebPlanStep(
        string planFile, string changeDesc, string prompt,
        string projectRoot, bool emitSse, CancellationToken ct,
        List<object> allResults, List<PlanStep> planItems, int itemIdx,
        int stepIndex, string discoveryContext, StringBuilder webCtx)
    {
        var isSearch = planFile.Equals("_web_search", StringComparison.OrdinalIgnoreCase);
        var query = changeDesc.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return (stepIndex, discoveryContext);
        await EmitLog(emitSse, "info", $"Web {(isSearch ? "search" : "fetch")}: {query}", ct: ct);
        var (outp, err) = isSearch ? await WebSearchAsync(query, ct) : await WebFetchAsync(query, ct);
        var curIdx = stepIndex;
        var wr = new Dictionary<string, object?>
        {
            ["index"] = curIdx,
            ["type"] = planFile,
            [isSearch ? "query" : "url"] = query,
            ["status"] = err == null ? "done" : "error",
            ["output"] = outp
        };
        allResults.Add(wr);
        if (emitSse) await SendSse(Response, "step", wr, ct);
        if (!string.IsNullOrWhiteSpace(outp) && outp.Length > 80)
            webCtx.AppendLine($"\n## Web [{query}]\n{outp}");
        var nextIsWeb = itemIdx + 1 < planItems.Count &&
            (planItems[itemIdx + 1].File.Equals("_web_search", StringComparison.OrdinalIgnoreCase) ||
             planItems[itemIdx + 1].File.Equals("_web_fetch", StringComparison.OrdinalIgnoreCase));
        if (!nextIsWeb && webCtx.Length > 0)
        {
            var remaining = planItems.Skip(itemIdx + 1).ToList();
            if (remaining.Any(r => AgentUtilities.IsRelativePath(r.File ?? "") || r.File == "_create_file"))
            {
                var uctx = discoveryContext + "\n\n" + webCtx;
                var rp = await ReplanRemainingSteps(prompt, remaining, uctx, emitSse, ct);
                if (rp?.Count > 0)
                {
                    planItems = MergePlanSteps(planItems, rp);
                    discoveryContext = uctx;
                    if (emitSse)
                        await SendSse(Response, "plan", new { summary = "Plan updated after web results", items = planItems }, ct);
                }
                webCtx.Clear();
            }
        }
        return (stepIndex + 1, discoveryContext);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  EDIT APPLICATION
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<(bool applied, string reason, int score)> ApplyEdit(
        PlanStep item, string projectRoot, bool emitSse, CancellationToken ct)
    {
        var relPath = item.File.Replace('\\', '/');
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!AgentUtilities.IsPathUnderRoot(fullPath, projectRoot)) return (false, "Path outside project root", 0);
        if (!System.IO.File.Exists(fullPath)) return (false, "File not found", 0);
        if (string.IsNullOrWhiteSpace(item.OldString)) return (false, "No oldString provided", 0);
        if ((item.OldString ?? "").Trim() == (item.NewString ?? "").Trim()) return (false, "oldString and newString identical", 3);

        var unsafeReason = GetUnsafeEditPayloadReason(item.OldString ?? "", item.NewString ?? "");
        if (unsafeReason != null) return (false, unsafeReason, 0);

        var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8);
        var (replaced, newContent, matchError, snippet) =
            TryReplaceSafe(content, item.OldString!, item.NewString ?? "");
        if (!replaced)
        {
            var reason = matchError ?? "oldString not found";
            if (!string.IsNullOrWhiteSpace(snippet)) reason += $". Nearby: {snippet}";
            return (false, reason, 1);
        }

        var (approved, verifyReason, score) =
            VerifyEdit(item.OldString!, item.NewString ?? "", content, newContent);
        if (!approved) return (false, verifyReason, score);

        await System.IO.File.WriteAllTextAsync(fullPath, newContent, Encoding.UTF8);
        await EmitLog(emitSse, "success", $"✔ Edited {relPath}", ct: ct);
        return (true, "", 10);
    }

    private static (bool approved, string reason, int score) VerifyEdit(
        string oldString, string newString, string oldContent, string newContent)
    {
        if (oldContent == newContent) return (false, "Edit produced no change", 3);

        // Guard: never write empty content when the original was non-empty
        if (!string.IsNullOrWhiteSpace(oldContent) && string.IsNullOrWhiteSpace(newContent))
            return (false, "Edit would produce empty file — rejected to prevent data loss", 1);

        // Guard: if the result is less than 10% of the original size, the edit likely
        // matched too broadly (e.g. oldString matched the entire file by accident).
        if (oldContent.Length > 200 && newContent.Length > 0 &&
            newContent.Length < oldContent.Length * 0.10)
            return (false, $"Edit would reduce file by {100 - (int)(newContent.Length * 100.0 / oldContent.Length)}% — suspicious content loss", 1);

        var normOld = AgentUtilities.NormalizeLineEndings(oldString);
        var normNew = AgentUtilities.NormalizeLineEndings(newString);
        var normOldContent = AgentUtilities.NormalizeLineEndings(oldContent);
        var normNewContent = AgentUtilities.NormalizeLineEndings(newContent);

        // newString must be present in result
        if (!string.IsNullOrEmpty(normNew) &&
            !normNewContent.Contains(normNew, StringComparison.Ordinal))
        {
            // Indentation may have been adjusted — retry with leading whitespace stripped from each line
            var strippedNew = StripLineLeadingWhitespace(normNew);
            var strippedContent = StripLineLeadingWhitespace(normNewContent);
            if (!strippedContent.Contains(strippedNew, StringComparison.Ordinal))
                return (false, "newString not found after replacement", 4);
        }

        // oldString should not appear as-frequently in the result
        // (it was supposed to be replaced). Only check this for non-trivial oldStrings.
        // Skip if oldString is contained inside newString — that means this is an
        // additive insert (insertAfter, append), not a replacement, so oldString
        // should still be present.
        if (!string.IsNullOrEmpty(normOld) && normOld.Length >= 10 && !normNew.Contains(normOld))
        {
            // Strip leading whitespace from each line for indentation-aware comparison
            var strippedOld = StripLineLeadingWhitespace(normOld);
            var strippedOldContent = StripLineLeadingWhitespace(normOldContent);
            var strippedNewContent = StripLineLeadingWhitespace(normNewContent);

            var oldCount = 0; var newCount = 0; var pos = 0;
            while ((pos = strippedOldContent.IndexOf(strippedOld, pos, StringComparison.Ordinal)) >= 0)
            { oldCount++; pos += strippedOld.Length; }
            pos = 0;
            while ((pos = strippedNewContent.IndexOf(strippedOld, pos, StringComparison.Ordinal)) >= 0)
            { newCount++; pos += strippedOld.Length; }
            // If oldString appears the same number of times, the edit was a no-op
            if (oldCount > 0 && newCount >= oldCount)
                return (false, "oldString still fully present after replacement — edit hit wrong location", 4);
        }

        // Verify the replacement actually changed the target area:
        // oldString and newString should be meaningfully different
        if (string.Equals(normOld.Trim(), normNew.Trim(), StringComparison.Ordinal))
            return (false, "oldString and newString are identical after normalization", 3);

        return (true, "Programmatic check passed", 10);
    }

    private static string StripLineLeadingWhitespace(string s)
    {
        var lines = s.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimStart();
        return string.Join("\n", lines);
    }

    private async Task<bool> ApplyEditWithRetry(
        PlanStep item, string projectRoot, bool emitSse, CancellationToken ct)
    {
        var relPath = item.File.Replace('\\', '/');
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!System.IO.File.Exists(fullPath)) return false;

        var history = new List<(string oldString, string newString, int score, string reason)>();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                var freshContent = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8);
                var corrected = await CorrectEdit(
                    "", relPath, freshContent, item.Change, history, attempt, emitSse, ct);
                if (corrected == null) break;
                if ((corrected.Value.oldString ?? "").Trim() == (corrected.Value.newString ?? "").Trim())
                {
                    await EmitLog(emitSse, "warn",
                        $"CorrectEdit returned identical strings for {relPath} — stopping retries", ct: ct);
                    break;
                }
                item.OldString = corrected.Value.oldString!;
                item.NewString = corrected.Value.newString!;
            }

            var (applied, reason, score) = await ApplyEdit(item, projectRoot, emitSse, ct);
            if (applied) return true;

            history.Add((item.OldString!, item.NewString ?? "", score, reason));
            if (attempt < 2)
                await EmitLog(emitSse, "warn",
                    $"Attempt {attempt + 1}/3 failed for {relPath}: {reason}", ct: ct);
            else
                await EmitLog(emitSse, "error",
                    $"All 3 attempts failed for {relPath}: {reason}", ct: ct);
        }
        return false;
    }

    private async Task<(string oldString, string newString)?> CorrectEdit(
        string originalPrompt, string relPath, string fileContent, string changeDesc,
        List<(string oldString, string newString, int score, string reason)> history,
        int attempt, bool emitSse, CancellationToken ct)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(originalPrompt)) { sb.AppendLine("## Original task"); sb.AppendLine(originalPrompt); sb.AppendLine(); }
        if (!string.IsNullOrWhiteSpace(changeDesc)) { sb.AppendLine("## Planned change"); sb.AppendLine(changeDesc); sb.AppendLine(); }
        sb.AppendLine($"## File: {relPath}");
        sb.AppendLine();
        sb.AppendLine(BuildEditCorrectionContext(fileContent, changeDesc, history));
        sb.AppendLine();
        sb.AppendLine("### Previous failed attempts:");
        for (var i = 0; i < history.Count; i++)
        {
            var h = history[i];
            sb.AppendLine($"--- Attempt {i + 1} — Score {h.score}/10 ---");
            sb.AppendLine($"Reason: {h.reason}");
            sb.AppendLine($"oldString:\n```\n{RemoveUnsafeEditMarkersForPrompt(h.oldString)}\n```");
            sb.AppendLine($"newString:\n```\n{RemoveUnsafeEditMarkersForPrompt(h.newString)}\n```");
        }
        sb.AppendLine("Produce corrected oldString/newString.");
        sb.AppendLine("- oldString must exist verbatim in the file");
        sb.AppendLine("- Match exact whitespace and indentation");

        const string system = @"You are an edit-correction agent. Output ONLY valid JSON:
{""oldString"": ""exact code from file"", ""newString"": ""replacement code""}
Rules: oldString MUST exist verbatim. Escape newlines as \n. Never return identical strings.";

        var (raw, _, err) = await CallLlmRaw(system, sb.ToString(), ct, TimeSpan.FromSeconds(30));
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            var (os, ns, parseError) = AgentUtilities.ExtractEditFromCodeGen(raw);
            if (string.IsNullOrWhiteSpace(os)) return null;
            return (os, ns ?? "");
        }
        catch
        {
            await EmitLog(emitSse, "warn", $"Failed to parse correction for {relPath}", ct: ct);
            return null;
        }
    }

    private static string BuildEditCorrectionContext(
        string fileContent, string changeDesc,
        List<(string oldString, string newString, int score, string reason)> history)
    {
        var normalized = AgentUtilities.NormalizeLineEndings(fileContent);
        var sb = new StringBuilder();
        if (normalized.Length <= MaxFileContextChars)
        {
            sb.AppendLine("### Current file content\n```");
            sb.AppendLine(normalized);
            sb.AppendLine("```");
            return sb.ToString();
        }
        var tokens = ExtractCorrectionTokens(changeDesc, history);
        var lines = normalized.Split('\n');
        var hitLines = new SortedSet<int>();
        if (tokens.Count > 0)
            for (var i = 0; i < lines.Length; i++)
                if (tokens.Any(t => lines[i].Contains(t, StringComparison.OrdinalIgnoreCase)))
                    hitLines.Add(i);
        var windows = new List<(int start, int end)>();
        foreach (var hit in hitLines.Take(12))
        {
            var start = Math.Max(0, hit - 30); var end = Math.Min(lines.Length - 1, hit + 30);
            if (windows.Count > 0 && start <= windows[^1].end + 5) windows[^1] = (windows[^1].start, Math.Max(windows[^1].end, end));
            else windows.Add((start, end));
        }
        if (windows.Count == 0) { windows.Add((0, Math.Min(lines.Length - 1, 180))); }
        sb.AppendLine("### Current file excerpts");
        var usedChars = 0;
        foreach (var w in windows)
        {
            var excerpt = string.Join('\n', lines.Skip(w.start).Take(w.end - w.start + 1));
            if (usedChars + excerpt.Length > MaxFileContextChars) excerpt = excerpt[..Math.Max(0, MaxFileContextChars - usedChars)];
            if (string.IsNullOrWhiteSpace(excerpt)) break;
            sb.AppendLine($"Lines {w.start + 1}-{w.end + 1}:\n```\n{excerpt}\n```");
            usedChars += excerpt.Length;
            if (usedChars >= MaxFileContextChars) break;
        }
        return sb.ToString();
    }

    private static HashSet<string> ExtractCorrectionTokens(
        string changeDesc,
        List<(string oldString, string newString, int score, string reason)> history)
    {
        var common = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "string","public","private","protected","internal","static","readonly","return",
            "await","async","using","namespace","class","void","var","new","null","true","false",
            "this","base","file","change","oldString","newString","reason","score"
        };
        var text = new StringBuilder(changeDesc ?? "");
        foreach (var h in history) { text.AppendLine(h.oldString); text.AppendLine(h.newString); }
        return Regex.Matches(text.ToString(), @"\b[A-Za-z_][A-Za-z0-9_]{2,}\b")
            .Select(m => m.Value).Where(t => !common.Contains(t))
            .OrderByDescending(t => t.Length).Take(40)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<List<PlanStep>?> ReplanRemainingSteps(
        string originalPrompt, List<PlanStep> remaining,
        string updatedContext, bool emitSse, CancellationToken ct)
    {
        if (remaining.Count == 0) return null;
        var sb = new StringBuilder();
        sb.AppendLine("Revise remaining steps given web results. Keep ALL existing steps and add any new ones needed. Original task: " + originalPrompt);
        foreach (var s in remaining) sb.AppendLine($"  {s.File}: {s.Change}");
        sb.AppendLine(updatedContext);
        const string sys = "Revise remaining execution steps. NEVER remove existing steps. Output ONLY JSON: {\"plan\":[{\"file\":\"...\",\"change\":\"...\",\"priority\":1}]}";
        var (raw, _, _) = await CallLlmRaw(sys, sb.ToString(), ct, _infiniteTimeout, maxTokens: 2048);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```")) { var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) cleaned = m.Groups[1].Value.Trim(); }
        var parsed = ParsePlan(cleaned);
        return parsed?.Plan?.Count > 0 ? parsed.Plan : null;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  COMMAND EXECUTION PIPELINE
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<(List<object> steps, AgentPlan? plan)> CommandExecutionPipeline(
        string prompt, string projectRoot, bool emitSse, CancellationToken ct,
        string? steeringContext = null, string? cardId = null)
    {
        var steps = new List<object>();
        var fastPlan = AgentUtilities.TryDetectSimpleIntent(prompt);
        if (fastPlan != null)
        {
            await EmitLog(emitSse, "info", $"CommandExecution (fast): {fastPlan.Plan.Count} step(s)", ct: ct);
            if (emitSse) await SendSse(Response, "plan", new { thinking = fastPlan.Thinking, summary = fastPlan.Summary, items = fastPlan.Plan }, ct);
            await ExecutePlan(prompt, projectRoot, emitSse, "", fastPlan, ct, steps);
            return (steps, fastPlan);
        }

        await EmitLog(emitSse, "info", "CommandExecution (agentic): LLM has terminal control", ct: ct);
        _terminal.Start();

        var isWindows = OperatingSystem.IsWindows();
        var shellName = isWindows ? "PowerShell" : "Bash";
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        var baseInstructions = new StringBuilder();
        baseInstructions.AppendLine("You are a terminal automation agent. You have full terminal access.");
        baseInstructions.AppendLine($"You are running on {shellName} ({Environment.OSVersion}).");
        baseInstructions.AppendLine("Output ONLY valid JSON. Options:");
        baseInstructions.AppendLine("  {\"cmd\": \"the full command\"}        # PREFERRED — use curl/Invoke-WebRequest for API calls");
        baseInstructions.AppendLine("  {\"web_fetch\": \"url\"}               # PREFERRED — fetch a known URL directly");
        baseInstructions.AppendLine("  {\"web_search\": \"query\"}            # LAST RESORT — only if you don't know the URL");
        baseInstructions.AppendLine("  {\"message\": \"answer for user\"}");
        baseInstructions.AppendLine("  {\"plan\": [{\"file\": \"command/web_search/web_fetch\", \"change\": \"description\"}]}  # First: create a plan of steps");
        baseInstructions.AppendLine("  {\"done\": true, \"summary\": \"what was accomplished\"}");
        baseInstructions.AppendLine($"Desktop: {desktopPath}");
        baseInstructions.AppendLine($"Project: {projectRoot}");
        baseInstructions.AppendLine("CRITICAL: Each cmd runs in a separate PowerShell session — state does NOT persist between commands. If you read data in one cmd and need it in the next, save to a temp file: Get-Content ... | Set-Content _temp_step1.txt");
        baseInstructions.AppendLine("If this task's results will feed into a subsequent code-editing step, save output files INSIDE the project directory (use a temp path like \"_temp_data.json\") so the next pipeline can read them. The file will be attached to the card automatically.");
        baseInstructions.AppendLine("NEVER use mkdir for files — use New-Item -ItemType File -Path \"<path>\" -Force");
        baseInstructions.AppendLine("NEVER use cd/Set-Location — use absolute paths");
        baseInstructions.AppendLine("For well-known REST APIs (pokeapi.co, jsonplaceholder, github api, etc.) use Invoke-RestMethod/curl via cmd — NOT web_search. web_search is only for finding URLs or info you don't already know.");
        if (isWindows)
        {
            baseInstructions.AppendLine("You are on WINDOWS PowerShell. Platform differences:");
            baseInstructions.AppendLine("  - Use `Invoke-RestMethod <url>` NOT curl (curl is an alias for Invoke-WebRequest in PowerShell)");
            baseInstructions.AppendLine("  - Use `ConvertFrom-Json` ONLY with curl/Invoke-WebRequest (raw JSON). Invoke-RestMethod already parses JSON — do NOT pipe it to ConvertFrom-Json.");
            baseInstructions.AppendLine("  - Use `| Set-Content -Path <file>` or `| Out-File -FilePath <file>` NOT > redirect");
            baseInstructions.AppendLine("  - Working example: Invoke-RestMethod https://pokeapi.co/api/v2/pokemon?limit=1000 | Select-Object -ExpandProperty results | ForEach-Object { $_.name } | Set-Content C:\\Users\\Saint\\Desktop\\pokemon.csv");
        }
        else
        {
            baseInstructions.AppendLine("You are on LINUX/MAC Bash. Use curl + jq.");
        }
        if (!string.IsNullOrWhiteSpace(steeringContext)) { baseInstructions.AppendLine("### Steering ###"); baseInstructions.AppendLine(steeringContext); }
        baseInstructions.AppendLine($"Task: {prompt}");

        // ── Phase 1: Plan ──────────────────────────────────────────────────
        var planPrompt = new StringBuilder();
        planPrompt.Append(baseInstructions);
        planPrompt.AppendLine("\nFIRST, output a plan as {\"plan\": [...]} listing each step you will take. Then you will execute each step one by one. When all steps are done, output {\"done\": true}.");

        var (planRaw, _, planErr) = await CallLlmRaw(
            "You are a terminal agent. Output only JSON.",
            planPrompt.ToString(), ct, TimeSpan.FromSeconds(30));

        List<PlanStep>? planSteps = null;
        if (!string.IsNullOrWhiteSpace(planRaw))
        {
            var cleaned = planRaw.Trim();
            if (cleaned.StartsWith("```")) { var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) cleaned = m.Groups[1].Value.Trim(); }
            foreach (var candidate in new[] { cleaned }.Concat(AgentUtilities.ExtractJsonBlocks(cleaned)))
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(candidate, new JsonDocumentOptions { AllowTrailingCommas = true });
                    var root = doc.RootElement;
                    if (root.TryGetProperty("plan", out var pArr) && pArr.ValueKind == JsonValueKind.Array && pArr.GetArrayLength() > 0)
                    {
                        planSteps = new List<PlanStep>();
                        foreach (var item in pArr.EnumerateArray())
                            planSteps.Add(new PlanStep
                            {
                                File = item.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "",
                                Change = item.TryGetProperty("change", out var c) ? c.GetString() ?? "" : ""
                            });
                        break;
                    }
                }
                catch { }
            }
        }

        if (planSteps != null && planSteps.Count > 0)
        {
            await EmitLog(emitSse, "info", $"Plan: {planSteps.Count} step(s) — {string.Join(", ", planSteps.Select(p => p.File))}", ct: ct);
            if (emitSse)
                await SendSse(Response, "plan", new
                {
                    thinking = "Planned steps",
                    summary = string.Join(" → ", planSteps.Select(p => p.Change)),
                    items = planSteps.Select(p => new { file = p.File, change = p.Change, priority = 1 }).ToList()
                }, ct);
        }
        else
        {
            await EmitLog(emitSse, "warn", "No plan produced — proceeding with reactive loop.", ct: ct);
        }

        // ── Phase 2: Execute ──────────────────────────────────────────────
        const int maxIter = MAX_COMMAND_ITERATIONS;
        var stepIndex = 0; string? summary = null;
        var usedSearchQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var completedPlanSteps = new HashSet<int>();
        var totalPlanSteps = planSteps?.Count ?? 0;

        var conversation = new StringBuilder();
        conversation.Append(baseInstructions);
        if (planSteps != null && planSteps.Count > 0)
        {
            conversation.AppendLine("\n### PLAN ###");
            for (var pi = 0; pi < planSteps.Count; pi++)
                conversation.AppendLine($"  Step {pi + 1}: [{planSteps[pi].File}] {planSteps[pi].Change}");
            conversation.AppendLine("### END PLAN ###");
            conversation.AppendLine("\nExecute steps in order. After each step succeeds, output {\"step\": N} to mark it done.");
            conversation.AppendLine("When all steps are done, output {\"done\": true}.");
        }

        const int maxReplan = 3;
        var replanCount = 0;
        var consecutiveErrors = 0;

        // Outer replan loop: execute → verify → replan if needed
        while (replanCount <= maxReplan)
        {
            var executionDone = false;
            for (var i = 0; i < MAX_COMMAND_ITERATIONS && !executionDone; i++)
            {
            ct.ThrowIfCancellationRequested();

            if (totalPlanSteps > 0)
            {
                var next = completedPlanSteps.Count;
                if (next < totalPlanSteps)
                {
                    if (consecutiveErrors >= 3)
                    {
                        conversation.AppendLine($"\n[Step {next + 1}/{totalPlanSteps} keeps failing — skip to next step or use a different approach]");
                        consecutiveErrors = 0;
                    }
                    else
                    {
                        conversation.AppendLine($"\n[Current: Step {next + 1}/{totalPlanSteps} — {planSteps![next].Change}]");
                    }
                }
                else
                    conversation.AppendLine($"\n[All {totalPlanSteps} steps completed — output done]");
            }

            AgentUtilities.CompactConversation(conversation);

            var (raw, _, err) = await CallLlmRaw(
                "You are a terminal agent. Output only JSON.",
                conversation.ToString(), ct, TimeSpan.FromSeconds(30));

            if (string.IsNullOrWhiteSpace(raw)) { summary ??= "Completed with issues"; break; }

            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```")) { var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) cleaned = m.Groups[1].Value.Trim(); }

            var jsonOpts = new JsonDocumentOptions { AllowTrailingCommas = true };
            string? jsonToParse = null;
            var candidates = new List<string> { cleaned };
            foreach (var block in AgentUtilities.ExtractJsonBlocks(cleaned)) if (!candidates.Contains(block)) candidates.Add(block);
            foreach (var c in candidates.ToList()) { var rep = AgentUtilities.RepairJsonString(c); if (rep != null && !candidates.Contains(rep)) candidates.Add(rep); }
            foreach (var candidate in candidates) { if (string.IsNullOrWhiteSpace(candidate)) continue; try { JsonDocument.Parse(candidate, jsonOpts); jsonToParse = candidate; break; } catch (JsonException) { } }

            if (jsonToParse != null)
            {
                using var doc = JsonDocument.Parse(jsonToParse, jsonOpts);
                var root = doc.RootElement;

                if (root.TryGetProperty("step", out var stepEl) && stepEl.ValueKind == JsonValueKind.Number)
                {
                    var stepNum = stepEl.GetInt32();
                    if (stepNum >= 1 && stepNum <= totalPlanSteps && completedPlanSteps.Add(stepNum - 1))
                    {
                        conversation.AppendLine($"→ Step {stepNum} marked done.");
                        if (emitSse)
                            await SendSse(Response, "step", new { index = stepIndex, type = "plan_step", planItemIndex = stepNum - 1, status = "done" }, ct);
                        await PersistBoardDataPlanStepAsync(cardId, stepNum - 1, emitSse, ct);
                    }
                }

                if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
                { summary = root.TryGetProperty("summary", out var s) ? s.GetString() : "Task complete"; break; }

                if (root.TryGetProperty("cmd", out var cmdEl) || root.TryGetProperty("command", out cmdEl))
                {
                    var cmd = cmdEl.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(cmd)) { conversation.AppendLine("Empty command — try again."); continue; }
                    if ((cmd.Contains('\n') || cmd.Contains('\r')) && !cmd.Contains("@\""))
                    {
                        var san = cmd.Replace("\r\n", "; ").Replace("\r", "; ").Replace("\n", "; ");
                        await EmitLog(emitSse, "info", $"⚠ newlines in cmd — joined", ct: ct);
                        cmd = san;
                    }
                    var cmdLower = cmd.TrimStart().ToLowerInvariant();
                    if (cmdLower.StartsWith("mkdir") && Regex.IsMatch(cmd, @"\.\w{2,4}[""'\s]|\.\w{2,4}$"))
                    { conversation.AppendLine($"REJECTED: mkdir creates DIRECTORIES. Use: New-Item -ItemType File -Path \"<path>\" -Force"); continue; }
                    if (cmdLower == "cd" || cmdLower.StartsWith("cd ") || cmdLower.Contains("set-location"))
                    { conversation.AppendLine($"REJECTED: cd/Set-Location not supported. Use absolute paths."); continue; }

                    var beforeLen = _terminal.ReadAll().Length;
                    await _terminal.SendCommandAsync(cmd, projectRoot);
                    var marker = $"__DONE_{Guid.NewGuid():N}__";
                    await _terminal.WriteStdinAsync($"echo '{marker}'");
                    var timeout2 = DateTime.UtcNow.AddMinutes(10);
                    while (!ct.IsCancellationRequested && DateTime.UtcNow < timeout2)
                    { await Task.Delay(500); if (_terminal.ReadAll().Contains(marker)) break; }
                    var fullOut = _terminal.ReadAll();
                    var freshOut = beforeLen < fullOut.Length ? fullOut[beforeLen..] : "";
                    freshOut = string.Join("\n", (freshOut ?? "").Split('\n').Where(l => !l.Contains("__DONE_")));
                    var isError = !string.IsNullOrWhiteSpace(freshOut) &&
                        Regex.IsMatch(freshOut.ToLowerInvariant(),
                            @"not recognized|not found|cannot find|terminate|error|exception|failed|access denied|permission denied");
                    var result = new Dictionary<string, object?>
                    { ["index"] = stepIndex++, ["type"] = "command", ["command"] = cmd, ["status"] = isError ? "error" : "done", ["output"] = freshOut };
                    steps.Add(result);
                    if (emitSse) await SendSse(Response, "step", result, ct);
                    conversation.AppendLine($"Command [{i + 1}]: {cmd}");
                    conversation.AppendLine(isError ? "⚠ Error:" : "Output:");
                    conversation.AppendLine(freshOut);
                    if (isError && freshOut.Contains("ConvertFrom-Json"))
                        conversation.AppendLine("💡 Hint: Invoke-RestMethod already parses JSON — remove ConvertFrom-Json from the pipeline.");
                    if (isError && freshOut.Contains("already exists"))
                        conversation.AppendLine("💡 Hint: The file already exists. Use -Force flag or a different path.");
                    if (isError) consecutiveErrors++;
                    else await AdvanceStepAsync();
                    continue;
                }

                if (root.TryGetProperty("web_search", out var searchEl))
                {
                    var query = searchEl.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(query)) { conversation.AppendLine("Empty query."); continue; }
                    if (!usedSearchQueries.Add(query)) { conversation.AppendLine($"Already searched for \"{query}\". Use the results above."); continue; }
                    var (searchOut, _) = await WebSearchAsync(query, ct);
                    var wr = new Dictionary<string, object?> { ["index"] = stepIndex++, ["type"] = "web_search", ["query"] = query, ["status"] = "done", ["output"] = searchOut };
                    steps.Add(wr); if (emitSse) await SendSse(Response, "step", wr, ct);
                    conversation.AppendLine($"Web search [{i + 1}]: {query}\nResults:\n{searchOut}");
                    await AdvanceStepAsync();
                    continue;
                }

                if (root.TryGetProperty("web_fetch", out var fetchEl))
                {
                    var url = fetchEl.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(url)) { conversation.AppendLine("Empty URL."); continue; }
                    // Validate URL format
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                        (uri.Scheme != "http" && uri.Scheme != "https"))
                    {
                        conversation.AppendLine($"Invalid URL: \"{url}\" — must be http/https. Provide a real URL.");
                        consecutiveErrors++;
                        continue;
                    }
                    var (fetchOut, fetchErr) = await WebFetchAsync(url, ct);
                    var isFetchError = fetchOut.StartsWith("HTTP 4") || fetchOut.StartsWith("HTTP 5") ||
                        (!string.IsNullOrWhiteSpace(fetchErr) && (fetchErr.Contains("404") || fetchErr.Contains("500")));
                    var fr = new Dictionary<string, object?> { ["index"] = stepIndex++, ["type"] = "web_fetch", ["url"] = url, ["status"] = isFetchError ? "error" : "done", ["output"] = fetchOut };
                    steps.Add(fr); if (emitSse) await SendSse(Response, "step", fr, ct);
                    if (isFetchError)
                    {
                        conversation.AppendLine($"⚠ Fetch error [{i + 1}]: {url}\n{fetchOut}");
                        consecutiveErrors++;
                    }
                    else
                    {
                        conversation.AppendLine($"Fetch [{i + 1}]: {url}\n{fetchOut}");
                        await AdvanceStepAsync();
                    }
                    continue;
                }

                if (root.TryGetProperty("message", out var msgEl) || root.TryGetProperty("result", out msgEl))
                {
                    var msgText = msgEl.GetString() ?? "";
                    var mr = new Dictionary<string, object?> { ["index"] = stepIndex++, ["type"] = "message", ["output"] = msgText };
                    steps.Add(mr); if (emitSse) await SendSse(Response, "step", mr, ct);
                    conversation.AppendLine($"Message: {msgText}");
                    continue;
                }

                conversation.AppendLine("Unrecognized JSON — use cmd, web_search, web_fetch, message, done, or plan.");
                continue;
            }

            // Helper: advance plan step if current step not yet marked done
            async Task AdvanceStepAsync()
            {
                if (totalPlanSteps > 0 && completedPlanSteps.Count < totalPlanSteps)
                {
                    var stepNum = completedPlanSteps.Count;
                    if (completedPlanSteps.Add(stepNum))
                    {
                        conversation.AppendLine($"→ Step {stepNum + 1} completed.");
                        if (emitSse)
                            await SendSse(Response, "step", new { index = stepIndex, type = "plan_step", planItemIndex = stepNum, status = "done" }, ct);
                        await PersistBoardDataPlanStepAsync(cardId, stepNum, emitSse, ct);
                    }
                }
                consecutiveErrors = 0;
            }

            var fallback = cleaned.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(fallback) && fallback.Length < 500)
            {
                var bl = _terminal.ReadAll().Length;
                await _terminal.SendCommandAsync(fallback, projectRoot);
                await Task.Delay(3000);
                var out2 = _terminal.ReadAll();
                var fresh2 = bl < out2.Length ? out2[bl..] : "";
                conversation.AppendLine($"Tried: {fallback}\nOutput:\n{fresh2}");
                steps.Add(new Dictionary<string, object?> { ["index"] = stepIndex++, ["type"] = "command", ["command"] = fallback, ["status"] = "done", ["output"] = fresh2 });
                continue;
            }
            conversation.AppendLine("Could not parse — use valid JSON.");
        } // end of execution for-loop

            // ── Verify completion (inside while loop) ──────────────────────
            if (steps.Count == 0) break;

            var verifyPrompt = $"Task: {prompt}\n\nSteps executed ({steps.Count} total):\n";
            foreach (var s in steps.OfType<Dictionary<string, object?>>())
                verifyPrompt += $"  [{s.GetValueOrDefault("type")}] {s.GetValueOrDefault("command") ?? s.GetValueOrDefault("query") ?? s.GetValueOrDefault("url") ?? ""}: {s.GetValueOrDefault("status")}\n";
            verifyPrompt += "\nIs the task fully complete? Reply ONLY: {\"complete\": true} or {\"complete\": false, \"reason\": \"what's missing\"}";

            var (vRaw, _, vErr) = await CallLlmRaw(
                "You verify task completion. Output only JSON.",
                verifyPrompt, ct, TimeSpan.FromSeconds(15), maxTokens: 256);

            var taskComplete = true;
            if (!string.IsNullOrWhiteSpace(vRaw))
            {
                try
                {
                    using var vDoc = JsonDocument.Parse(vRaw.Trim(), new JsonDocumentOptions { AllowTrailingCommas = true });
                    var vRoot = vDoc.RootElement;
                    if (vRoot.TryGetProperty("complete", out var vc) && vc.ValueKind == JsonValueKind.False)
                    {
                        taskComplete = false;
                        var reason = vRoot.TryGetProperty("reason", out var vr) ? vr.GetString() : "unknown";
                        await EmitLog(emitSse, "warn", $"Task verification: incomplete — {reason}", ct: ct);

                        if (replanCount >= maxReplan)
                        {
                            await EmitLog(emitSse, "warn", $"Max replans ({maxReplan}) reached — stopping.", ct: ct);
                            break;
                        }
                        replanCount++;

                        var replanPrompt = $"Original task: {prompt}\nVerification says: {reason}\n\nGenerate NEW plan steps. Output ONLY: {{\"plan\": [{{\"file\": \"command/web_search/web_fetch\", \"change\": \"what to do\"}}]}}";
                        var (rRaw, _, rErr) = await CallLlmRaw(
                            "You generate additional plan steps. Output only JSON.",
                            replanPrompt, ct, TimeSpan.FromSeconds(15), maxTokens: 1024);

                        List<PlanStep>? extraSteps = null;
                        if (!string.IsNullOrWhiteSpace(rRaw))
                        {
                            var rCleaned = rRaw.Trim();
                            if (rCleaned.StartsWith("```")) { var m = Regex.Match(rCleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) rCleaned = m.Groups[1].Value.Trim(); }
                            foreach (var candidate in new[] { rCleaned }.Concat(AgentUtilities.ExtractJsonBlocks(rCleaned)))
                            {
                                if (string.IsNullOrWhiteSpace(candidate)) continue;
                                try
                                {
                                    using var rDoc = JsonDocument.Parse(candidate, new JsonDocumentOptions { AllowTrailingCommas = true });
                                    if (rDoc.RootElement.TryGetProperty("plan", out var rpArr) && rpArr.ValueKind == JsonValueKind.Array)
                                    {
                                        extraSteps = new List<PlanStep>();
                                        foreach (var item in rpArr.EnumerateArray())
                                            extraSteps.Add(new PlanStep
                                            {
                                                File = item.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "",
                                                Change = item.TryGetProperty("change", out var c) ? c.GetString() ?? "" : ""
                                            });
                                        break;
                                    }
                                }
                                catch { }
                            }
                        }

                        if (extraSteps != null && extraSteps.Count > 0)
                        {
                            await EmitLog(emitSse, "info", $"Replan #{replanCount}: {extraSteps.Count} additional step(s)", ct: ct);
                            planSteps ??= new List<PlanStep>();
                            planSteps.AddRange(extraSteps);
                            totalPlanSteps = planSteps.Count;

                            if (emitSse)
                                await SendSse(Response, "plan", new
                                {
                                    thinking = "Replanned steps",
                                    summary = string.Join(" → ", planSteps.Select(p => p.Change)),
                                    items = planSteps.Select(p => new { file = p.File, change = p.Change, priority = 1 }).ToList()
                                }, ct);

                            conversation.AppendLine($"\n### ADDITIONAL STEPS ({extraSteps.Count}) ###");
                            for (var pi = 0; pi < extraSteps.Count; pi++)
                                conversation.AppendLine($"  Step {totalPlanSteps - extraSteps.Count + pi + 1}: [{extraSteps[pi].File}] {extraSteps[pi].Change}");
                            conversation.AppendLine("### END ADDITIONAL STEPS ###");
                            conversation.AppendLine("Execute only the NEW steps above, then output done.");
                            continue; // restart outer while loop (re-execute)
                        }
                    }
                }
                catch { }
            }

            if (taskComplete)
            {
                await EmitLog(emitSse, "success", "Task verification: complete.", ct: ct);
                break;
            }
            break; // replan didn't produce steps — stop
        } // end of while (replan loop)

        summary ??= $"Command execution completed ({steps.Count} steps)";
        await EmitLog(emitSse, "info", summary, steps, ct: ct);

        // Add done_signal so Orchestrate's quality check skips
        steps.Add(new Dictionary<string, object?> { ["type"] = "done_signal", ["status"] = "done" });

        // Return the actual plan so the frontend can match steps to plan items
        var agentPlan = planSteps != null && planSteps.Count > 0
            ? new AgentPlan { Plan = planSteps, Summary = summary, Thinking = "Command execution plan" }
            : null;
        return (steps, agentPlan);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FILE CREATION
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<(List<object> results, int stepsCount)> HandleCreateFile(
        string changeDesc, string projectRoot, string originalPrompt, string discoveryContext,
        int idx, bool emitSse, CancellationToken ct,
        string? explicitRelPath = null, List<string>? attachedFiles = null)
    {
        var results = new List<object>();
        var targetRelPath = explicitRelPath;
        if (string.IsNullOrWhiteSpace(targetRelPath))
        {
            var namedMatch = Regex.Match(changeDesc, @"(?:new\s+)?file\s+(?:called|named)?\s*['""`]?([\w./\\-]+\.[\w.-]+)['""`]?", RegexOptions.IgnoreCase);
            if (namedMatch.Success) targetRelPath = namedMatch.Groups[1].Value.Replace('\\', '/');
        }
        if (string.IsNullOrWhiteSpace(targetRelPath)) { var pm = Regex.Match(changeDesc, @"[\w/\\]+\.[\w]+"); if (pm.Success) targetRelPath = pm.Value.Replace('\\', '/'); }
        if (string.IsNullOrWhiteSpace(targetRelPath)) { var dm = Regex.Match(changeDesc, @"\.[\w-]+(?:\.[\w-]+)*"); if (dm.Success) targetRelPath = dm.Value; }
        if (string.IsNullOrWhiteSpace(targetRelPath)) targetRelPath = "newfile.txt";
        targetRelPath = targetRelPath.Replace('\\', '/');
        if (!targetRelPath.Contains('/'))
        { var folder = AgentUtilities.InferTargetFolder(targetRelPath, projectRoot); if (!string.IsNullOrWhiteSpace(folder)) targetRelPath = folder + targetRelPath; }

        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, targetRelPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!AgentUtilities.IsPathUnderRoot(fullPath, projectRoot))
        { await EmitLog(emitSse, "error", $"Create target {targetRelPath} is outside project root", ct: ct); return (results, 0); }

        await EmitLog(emitSse, "info", $"Generating content for: {targetRelPath}", ct: ct);

        var contentPrompt = $"Generate COMPLETE content for: {targetRelPath}\nTask: {originalPrompt}\nDescription: {changeDesc}\nContext:\n{discoveryContext}\n\nOutput ONLY the raw file content — no markdown, no fences, no explanation.";
        var (content, _, _) = await CallLlmRaw(
            "Output ONLY raw file content — no markdown, no code fences, no explanation.",
            contentPrompt, ct, _infiniteTimeout);

        var cleaned = StripFullFileFence(content ?? "");
        if (string.IsNullOrWhiteSpace(cleaned)) { await EmitLog(emitSse, "warn", $"Empty content for {targetRelPath}", ct: ct); return (results, 0); }

        var parentDir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir)) Directory.CreateDirectory(parentDir);
        await System.IO.File.WriteAllTextAsync(fullPath, cleaned, Encoding.UTF8);
        await EmitLog(emitSse, "success", $"Created {targetRelPath} ({cleaned.Length} chars)", ct: ct);
        attachedFiles?.Add(fullPath);

        if (emitSse) await SendSse(Response, "result", new { type = "create", path = targetRelPath, chars = cleaned.Length }, ct);
        results.Add(new Dictionary<string, object?> { ["status"] = "done", ["path"] = targetRelPath, ["output"] = cleaned, ["type"] = "create" });
        return (results, 1);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  HTTP ENDPOINTS
    // ═══════════════════════════════════════════════════════════════════════

    [HttpPost("execute")]
    public async Task<IActionResult> Execute([FromBody] AgentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt)) return BadRequest("Prompt is required");
        var projectRoot = GetProjectRoot(req.Project);
        await EmitLog(true, "info", "Orchestrating Request.", new { projectRoot, task = req.Prompt });

        var (allSteps, plan, complete) = await Orchestrate(req.Prompt, projectRoot, emitSse: false);
        return Ok(new
        {
            summary = plan?.Summary ?? "",
            thinking = plan?.Thinking ?? "",
            complete,
            steps = allSteps,
            filesEdited = ExtractFilesEdited(allSteps)
        });
    }

    [HttpPost("apply")]
    public async Task<IActionResult> ApplyEdits([FromBody] ApplyEditsRequest req)
    {
        if (req.Edits == null || req.Edits.Count == 0) return BadRequest(new { error = "No edits provided" });
        var projectRoot = GetProjectRoot(req.Project);
        var editResults = await ApplyEditsDirect(req.Edits, projectRoot);
        var commandResults = new List<object>();
        if (req.Commands != null && req.Commands.Count > 0)
        {
            _terminal.Start();
            foreach (var cmd in req.Commands)
            {
                try
                {
                    await _terminal.SendCommandAsync(cmd.Command, projectRoot);
                    await Task.Delay(800);
                    commandResults.Add(new { command = cmd.Command, status = "done", output = _terminal.ReadLastLines(50) });
                }
                catch (Exception ex) { commandResults.Add(new { command = cmd.Command, status = "error", error = ex.Message }); }
            }
        }
        return Ok(new { edits = editResults, commands = commandResults });
    }

    [HttpPost("execute-stream")]
    public async Task ExecuteStream([FromBody] AgentRequest req)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        Response.Headers["Connection"] = "keep-alive";
        var bufferingFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
        bufferingFeature?.DisableBuffering();
        await Response.StartAsync(Response.HttpContext.RequestAborted);

        var keepaliveCts = CancellationTokenSource.CreateLinkedTokenSource(Response.HttpContext.RequestAborted);
        var keepaliveTask = Task.Run(async () =>
        {
            while (!keepaliveCts.Token.IsCancellationRequested)
            {
                try { await Task.Delay(15000, keepaliveCts.Token); await Response.WriteAsync(":\n\n", keepaliveCts.Token); await Response.Body.FlushAsync(keepaliveCts.Token); }
                catch { break; }
            }
        }, keepaliveCts.Token);

        if (string.IsNullOrWhiteSpace(req.Prompt))
        {
            await SendSse(Response, "error", new { message = "Prompt is required" });
            await SendSse(Response, "done", new { });
            keepaliveCts.Cancel(); try { await keepaliveTask; } catch { }
            return;
        }

        try
        {
            var projectRoot = GetProjectRoot(req.Project);
            await SendSse(Response, "phase", new { phase = "start", projectRoot });
            await EmitLog(true, "info", "Agent started", new { projectRoot, task = req.Prompt });

            // Load existing plan from board data (by cardId) — no need for frontend to pass it
            AgentPlan? existingPlan = null;
            HashSet<int>? completedIndices = null;
            if (!string.IsNullOrWhiteSpace(req.CardId))
            {
                var (loadedPlan, loadedCompleted) = await LoadPlanFromBoardDataAsync(req.CardId);
                existingPlan = loadedPlan;
                completedIndices = loadedCompleted;
            } 

            var (allSteps, plan, complete) = await Orchestrate(
                req.Prompt, projectRoot, emitSse: true,
                ct: Response.HttpContext.RequestAborted,
                attachedFiles: req.Files?.Count > 0 ? req.Files : null,
                steeringContext: req.SteeringContext,
                existingPlan: existingPlan,
                completedStepIndices: completedIndices,
                cardId: req.CardId);

            var filesEdited = ExtractFilesEdited(allSteps);
            var editsApplied = AgentUtilities.HasSuccessfulEdits(allSteps);

            await SendSse(Response, "done", new
            {
                summary = plan?.Summary ?? "",
                thinking = plan?.Thinking ?? "",
                complete,
                editsApplied,
                incomplete = AgentUtilities.TaskExpectsFileChanges(req.Prompt) && !complete,
                warning = !complete && AgentUtilities.TaskExpectsFileChanges(req.Prompt)
                    ? (editsApplied ? "Task may be incomplete. Please review."
                                    : "No files were modified.")
                    : (string?)null,
                steps = allSteps,
                filesEdited
            });

            if (req.SelfImproving)
            {
                try { await RunSelfImprovingPipeline(req.Prompt, projectRoot, allSteps, plan, complete, editsApplied); }
                catch (Exception siEx) { await EmitLog(true, "warn", $"Self-improving error: {siEx.Message}"); }
            }
        }
        catch (Exception ex)
        {
            await SendSse(Response, "error", new { message = ex.Message });
            await SendSse(Response, "done", new { incomplete = true, summary = ex.Message });
        }
        finally
        {
            keepaliveCts.Cancel(); try { await keepaliveTask; } catch { }
            // Clean up cancelled steps tracking for this card
            if (!string.IsNullOrWhiteSpace(req.CardId))
                _cancelledSteps.TryRemove(req.CardId, out _);
        }
    }

    [HttpGet("questions/pending")]
    public IActionResult GetPendingQuestions()
    {
        var list = _pendingQuestions.Values.OrderBy(q => q.CreatedUtc)
            .Select(q => new { q.Id, q.Question, q.Fields, q.CreatedUtc }).ToList();
        return Ok(new { questions = list });
    }

    [HttpPost("questions/answer")]
    public async Task<IActionResult> AnswerQuestion([FromBody] QuestionAnswerRequest req)
    {
        if (!_pendingQuestions.TryRemove(req.Id, out var pending))
            return NotFound("Question not found or already answered");
        pending.Answer.TrySetResult(req.Answers);
        return Ok(new { status = "answered" });
    }

    [HttpPost("context-review/confirm")]
    public IActionResult ConfirmContextReview([FromBody] ContextReviewAnswer req)
    {
        if (!_pendingContextReviews.TryRemove(req.Id, out var pending))
            return NotFound("Context review not found or already answered");
        pending.Answer.TrySetResult(req.Files ?? pending.Files);
        return Ok(new { status = "confirmed" });
    }

    [HttpPost("cancel-step")]
    public IActionResult CancelPlanStep([FromBody] CancelStepRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.CardId))
            return BadRequest("cardId is required");
        var steps = _cancelledSteps.GetOrAdd(req.CardId, _ => new HashSet<int>());
        lock (steps) { steps.Add(req.StepIndex); }
        return Ok(new { status = "cancelled", cardId = req.CardId, stepIndex = req.StepIndex });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  LLM CONNECTIVITY + CALL HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<bool> CheckLlmConnectivity(string projectRoot, bool emitSse, CancellationToken ct)
    {
        if (_nextConnectivityCheck != DateTime.MinValue &&
            DateTime.UtcNow - _nextConnectivityCheck < TimeSpan.FromMinutes(5))
        {
            await EmitLog(emitSse, "info", "Skipping connectivity check (cached)", ct: ct);
            return _lastConnectionCheckResult;
        }
        var baseUrl = await GetLlamaBaseUrl();
        _lastConnectionCheckResult = await CheckForConnectivity(projectRoot, emitSse, baseUrl, ct);
        _nextConnectivityCheck = DateTime.UtcNow.AddMinutes(5);
        return _lastConnectionCheckResult;
    }

    private async Task<bool> CheckForConnectivity(
        string projectRoot, bool emitSse, string baseUrl, CancellationToken ct)
    {
        var uri = new Uri(baseUrl);
        await EmitLog(emitSse, "info", $"Connectivity check: {uri.Host}:{uri.Port}", ct: ct);
        var tcpCmd = OperatingSystem.IsWindows()
            ? $"powershell -Command \"Test-NetConnection {uri.Host} -Port {uri.Port} -WarningAction SilentlyContinue | Select-Object TcpTestSucceeded | Format-List\""
            : $"nc -zv -w 2 {uri.Host} {uri.Port} 2>&1";
        var step = new AgentStep { Index = 0, Type = "command", Command = tcpCmd, Description = "tcp check" };
        var results = await ExecuteSteps(new List<AgentStep> { step }, projectRoot, 0, emitSse, ct);
        var first = results.FirstOrDefault() as Dictionary<string, object?>;
        var output = first?.TryGetValue("output", out var o) == true ? o?.ToString() ?? "" : "";
        var succeeded = output.Contains("TcpTestSucceeded : True", StringComparison.OrdinalIgnoreCase) ||
                        output.Contains("succeeded", StringComparison.OrdinalIgnoreCase) ||
                        output.Contains("HTTP 200", StringComparison.Ordinal);
        if (succeeded) { await EmitLog(emitSse, "info", $"LLM reachable", ct: ct); return true; }
        // Fallback: just try an HTTP call
        try
        {
            var client = _clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var resp = await client.GetAsync(baseUrl + "/api/tags", ct);
            if (resp.IsSuccessStatusCode || (int)resp.StatusCode < 500)
            { await EmitLog(emitSse, "info", $"LLM reachable via HTTP", ct: ct); return true; }
        }
        catch { }
        await EmitLog(emitSse, "error", $"LLM unreachable at {uri.Host}:{uri.Port}", ct: ct);
        return false;
    }

    private async Task<string> GetLlamaBaseUrl()
    {
        var cfg = await _configFile.LoadConfigAsync();
        return (cfg.llamaUrl ?? "http://localhost:8080").TrimEnd('/');
    }

    private async Task<(string raw, AgentResponse? response, string? error)> CallLlmRaw(
        string systemPrompt, string userMessage, CancellationToken ct = default,
        TimeSpan? requestTimeout = null, int? maxTokens = null)
    {
        var baseUrl = await GetLlamaBaseUrl();
        var model = _config.GetValue<string>("Ai:Model") ?? "medgemma:4b";
        var client = _clientFactory.CreateClient("llama");
        client.Timeout = _infiniteTimeout;
        var messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user",   content = userMessage  }
        };
        var timeout = requestTimeout ?? TimeSpan.FromMinutes(30);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        return await CallLlmNonStreaming(client, baseUrl + "/v1/chat/completions", model, messages, linkedCts.Token, maxTokens);
    }

    private async Task<(string raw, AgentResponse? response, string? error)> CallLlmRawStreaming(
        string systemPrompt, string userMessage, bool emitSse, CancellationToken ct = default,
        TimeSpan? requestTimeout = null, int? maxTokens = null)
    {
        var baseUrl = await GetLlamaBaseUrl();
        var model = _config.GetValue<string>("Ai:Model") ?? "medgemma:4b";
        var client = _clientFactory.CreateClient("llama");
        client.Timeout = _infiniteTimeout;
        var messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user",   content = userMessage  }
        };
        var timeout = requestTimeout ?? TimeSpan.FromMinutes(30);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        return await CallLlmStreaming(client, baseUrl + "/v1/chat/completions", model, messages, linkedCts.Token, maxTokens, emitSse);
    }

    private async Task<(string raw, AgentResponse? parsed, string? error)> CallLlmNonStreaming(
        HttpClient client, string target, string model, object messages,
        CancellationToken ct = default, int? maxTokens = null)
    {
        var mt = maxTokens ?? 2048;
        var reqBody = new { model, messages, stream = false, temperature = 0.05, max_tokens = mt };
        var httpContent = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");
        try
        {
            var resp = await client.PostAsync(target, httpContent, ct);
            var respText = await resp.Content.ReadAsStringAsync(ct);
            var llmContent = ExtractLlmContent(respText);
            if (string.IsNullOrWhiteSpace(llmContent)) return (respText, null, "Empty LLM response");
            var parsed = ParseAgentResponse(llmContent);
            return (llmContent, parsed, parsed == null ? "JSON parse failed" : null);
        }
        catch (TaskCanceledException) { return ("", null, "LLM request timed out"); }
        catch (Exception ex) { return ("", null, ex.Message); }
    }

    private async Task<(string raw, AgentResponse? parsed, string? error)> CallLlmStreaming(
        HttpClient client, string target, string model, object messages,
        CancellationToken ct = default, int? maxTokens = null, bool emitSse = false)
    {
        var mt = maxTokens ?? 2048;
        var reqBody = new { model, messages, stream = true, temperature = 0.05, max_tokens = mt };
        var httpContent = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = httpContent };
            var resp = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            { var t2 = await resp.Content.ReadAsStringAsync(ct); return (t2, null, $"HTTP {resp.StatusCode}"); }

            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var reader = new StreamReader(stream);
            var sb = new StringBuilder();

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (line == null || string.IsNullOrWhiteSpace(line)) continue;
                if (line.Contains("[DONE]")) break;
                if (!line.StartsWith("data: ")) continue;
                var data = line[6..].Trim();
                if (string.IsNullOrWhiteSpace(data)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                    {
                        var choice = choices[0];
                        if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content))
                        {
                            var token = content.GetString();
                            if (!string.IsNullOrWhiteSpace(token))
                            {
                                if (emitSse) await SendSse(Response, "token", new { token }, ct);
                                sb.Append(token);
                            }
                        }
                    }
                }
                catch { }
            }

            var raw = sb.ToString();
            if (string.IsNullOrWhiteSpace(raw)) return ("", null, "Empty LLM response");
            var parsed2 = ParseAgentResponse(raw);
            return (raw, parsed2, parsed2 == null ? "JSON parse failed" : null);
        }
        catch (TaskCanceledException) { return ("", null, "LLM request timed out"); }
        catch (Exception ex) { return ("", null, ex.Message); }
    }

    private async Task<(string raw, string? error)> CallLlmRawText(
        string systemPrompt, string userMessage, CancellationToken ct = default,
        TimeSpan? requestTimeout = null, int? maxTokens = null)
    {
        try
        {
            var baseUrl = await GetLlamaBaseUrl();
            var model = _config.GetValue<string>("Ai:Model") ?? "medgemma:4b";
            var client = _clientFactory.CreateClient("llama");
            client.Timeout = _infiniteTimeout;
            var messages = new object[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userMessage } };
            var timeout = requestTimeout ?? TimeSpan.FromMinutes(30);
            using var tCts = new CancellationTokenSource(timeout);
            using var lCts = CancellationTokenSource.CreateLinkedTokenSource(ct, tCts.Token);
            var reqBody = new { model, messages, stream = false, temperature = 0.0, max_tokens = maxTokens ?? MaxFileContextChars / 2 };
            var httpContent = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(baseUrl + "/v1/chat/completions", httpContent, lCts.Token);
            var respText = await resp.Content.ReadAsStringAsync(lCts.Token);
            var raw = ExtractLlmContent(respText);
            if (string.IsNullOrWhiteSpace(raw)) return (respText, "Empty LLM response");
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("```")) { var m = Regex.Match(trimmed, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) trimmed = m.Groups[1].Value.Trim(); }
            return (trimmed, null);
        }
        catch (TaskCanceledException) { return ("", "LLM request timed out"); }
        catch (Exception ex) { return ("", ex.Message); }
    }

    private static string ExtractLlmContent(string respText)
    {
        try
        {
            using var doc = JsonDocument.Parse(respText);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
                    return c.GetString() ?? "";
            }
        }
        catch { }
        return "";
    }

    private static List<object> ExtractFilesEdited(List<object> steps)
    {
        var result = steps.OfType<Dictionary<string, object?>>()
            .Where(s => s.TryGetValue("type", out var t) && (t?.ToString() == "edit" || t?.ToString() == "rename") &&
                        s.TryGetValue("status", out var st) && st?.ToString() == "done")
            .Select(s => (object)new
            {
                path = s.GetValueOrDefault("path"),
                action = s.GetValueOrDefault("editAction"),
                toPath = s.GetValueOrDefault("toPath"),
                linesAdded = s.GetValueOrDefault("linesAdded"),
                linesRemoved = s.GetValueOrDefault("linesRemoved"),
                preview = s.GetValueOrDefault("diffPreview")
            }).ToList();
        if (result.Count > 0) return result;
        foreach (var step in steps)
        {
            if (step is Dictionary<string, object?>) continue;
            try
            {
                var json = JsonSerializer.Serialize(step);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : "";
                var status = root.TryGetProperty("status", out var st) ? st.GetString() : "";
                if ((type == "edit" || type == "rename") && status == "done")
                    result.Add(new { path = root.TryGetProperty("path", out var p) ? p.GetString() : null, action = (string?)null, toPath = (string?)null, linesAdded = 0, linesRemoved = 0, preview = (string?)null });
            }
            catch { }
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  STEP EXECUTORS
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<List<object>> ExecuteSteps(
        List<AgentStep> steps, string projectRoot, int indexOffset, bool emitSse,
        CancellationToken ct = default)
    {
        var results = new List<object>();
        var terminalStarted = false;
        var editContentCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in steps)
        {
            var displayIndex = indexOffset + step.Index;
            var result = new Dictionary<string, object?>
            {
                ["index"] = displayIndex,
                ["type"] = step.Type,
                ["description"] = step.Description,
                ["status"] = "running"
            };
            if (emitSse)
            {
                var label = step.Description ?? step.Path ?? step.Command ?? step.Query ?? step.Pattern ?? "";
                await EmitLog(emitSse, "step", $"▶ {step.Type}: {label}", new {result}, ct: ct);
                await SendSse(Response, "step", result, ct);
            }
            try
            {
                switch (step.Type?.ToLowerInvariant())
                {
                    case "edit": await ExecuteEditStep(step, projectRoot, result, editContentCache); break;
                    case "command": if (!terminalStarted) { _terminal.Start(); terminalStarted = true; } await ExecuteCommandStep(step, projectRoot, result); break;
                    case "rename": await ExecuteRenameStep(step, projectRoot, result); break;
                    case "read": await ExecuteReadStep(step, projectRoot, result); break;
                    case "list": await ExecuteListStep(step, projectRoot, result); break;
                    case "glob": await ExecuteGlobStep(step, projectRoot, result); break;
                    case "grep": await ExecuteGrepStep(step, projectRoot, result); break;
                    case "web": case "web_search": case "web_fetch": await ExecuteWebStep(step, result); break;
                    default: result["status"] = "error"; result["error"] = $"Unknown step type: {step.Type}"; break;
                }
                await EmitLog(true, "log", "Raw Step Result", result, ct); 
            }
            catch (Exception ex) { result["status"] = "error"; result["error"] = ex.Message; }
            result["status"] = AgentUtilities.NormalizeUiStatus(result["status"]?.ToString());
            results.Add(result);
            if (emitSse)
            {
                var st = result["status"]?.ToString() ?? "?";
                var outputRaw = result.GetValueOrDefault("output")?.ToString();
                var outputPreview = outputRaw != null && outputRaw.Length > 200 ? outputRaw[..200] + "…" : outputRaw;
                await EmitLog(emitSse, st == "error" ? "error" : "info", $"✓ {step.Type} ({st})",
                    new { path = result.GetValueOrDefault("path"), error = result.GetValueOrDefault("error"), output = outputPreview }, ct: ct);
                await SendSse(Response, "step", result, ct);
            }
        }
        return results;
    }

    private async Task<List<object>> ExecuteDiscoveryStepsConcurrent(
        List<AgentStep> steps, string projectRoot, int indexOffset, bool emitSse)
    {
        var count = steps.Count;
        var results = new Dictionary<string, object?>[count];
        for (var i = 0; i < count; i++)
        {
            var step = steps[i];
            var displayIndex = indexOffset + step.Index;
            var result = new Dictionary<string, object?>
            { ["index"] = displayIndex, ["type"] = step.Type, ["description"] = step.Description, ["status"] = "running" };
            results[i] = result;
            if (emitSse)
            {
                await EmitLog(emitSse, "step", $"▶ {step.Type}: {step.Description ?? step.Path ?? ""}");
                await SendSse(Response, "step", result);
            }
        }
        var tasks = steps.Select((step, i) => Task.Run(async () =>
        {
            var result = results[i];
            try
            {
                switch (step.Type?.ToLowerInvariant())
                {
                    case "list": await ExecuteListStep(step, projectRoot, result); break;
                    case "grep": await ExecuteGrepStep(step, projectRoot, result); break;
                    case "read": await ExecuteReadStep(step, projectRoot, result); break;
                    default: result["status"] = "error"; result["error"] = $"Unknown: {step.Type}"; break;
                }
            } 
            catch (Exception ex) { result["status"] = "error"; result["error"] = ex.Message; }
            await EmitLog(true, "log", "Raw Discovery Step Result", result); 
            result["status"] = AgentUtilities.NormalizeUiStatus(result["status"]?.ToString());
        }));
        await Task.WhenAll(tasks);
        for (var i = 0; i < count; i++)
        {
            if (emitSse)
            {
                var st = results[i]["status"]?.ToString() ?? "?";
                await EmitLog(emitSse, st == "error" ? "error" : "info", $"✓ {steps[i].Type} ({st})", new { path = results[i].GetValueOrDefault("path"), error = results[i].GetValueOrDefault("error") });
                await SendSse(Response, "step", results[i]);
            }
        }
        return results.Cast<object>().ToList();
    }

    private async Task ExecuteEditStep(
        AgentStep step, string projectRoot, Dictionary<string, object?> result,
        Dictionary<string, string>? contentCache = null)
    {
        var rawPath = (step.Path ?? "").Replace('/', Path.DirectorySeparatorChar);
        var isAbs = rawPath.Contains(":\\") || rawPath.StartsWith('/') || rawPath.StartsWith('\\');
        var targetPath = isAbs ? Path.GetFullPath(rawPath) : Path.GetFullPath(Path.Combine(projectRoot, rawPath));
        if (!isAbs && !AgentUtilities.IsPathUnderRoot(targetPath, projectRoot))
        { result["status"] = "error"; result["error"] = "Path outside project root"; return; }

        result["path"] = step.Path;
        var oldString = step.OldString ?? ""; var newString = step.NewString ?? "";
        var unsafeReason = GetUnsafeEditPayloadReason(oldString, newString);
        if (unsafeReason != null) { result["status"] = "error"; result["error"] = unsafeReason; return; }

        string content;
        if (contentCache != null && contentCache.TryGetValue(targetPath, out var cached)) content = cached;
        else
        {
            if (!System.IO.File.Exists(targetPath))
            {
                if (string.IsNullOrEmpty(oldString) && !string.IsNullOrEmpty(newString))
                {
                    var d = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) Directory.CreateDirectory(d);
                    await System.IO.File.WriteAllTextAsync(targetPath, newString, Encoding.UTF8);
                    result["oldStartLine"] = 0;
                    PopulateEditResult(result, "created", step.Path!, null, newString, newString);
                    if (contentCache != null) contentCache[targetPath] = newString;
                    return;
                }
                result["status"] = "error"; result["error"] = $"File does not exist: {step.Path}";
                result["suggestions"] = AgentUtilities.FindSimilarFiles(step.Path ?? "", projectRoot);
                return;
            }
            content = await System.IO.File.ReadAllTextAsync(targetPath, Encoding.UTF8);
        }

        if (string.IsNullOrEmpty(oldString))
        {
            content += newString;
            await System.IO.File.WriteAllTextAsync(targetPath, content, Encoding.UTF8);
            if (contentCache != null) contentCache[targetPath] = content;
            PopulateEditResult(result, "modified", step.Path!, null, newString, newString);
            return;
        }

        var (replaced, newContent, matchError, snippet) = TryReplaceSafe(content, oldString, newString);
        if (!replaced)
        {
            result["status"] = "error"; result["error"] = matchError ?? "oldString not found";
            if (snippet != null) result["snippet"] = snippet;
            result["oldStringPreview"] = oldString;
            return;
        }

        if (AgentUtilities.NormalizeLineEndings(newContent) == AgentUtilities.NormalizeLineEndings(content))
        { result["status"] = "skipped"; result["path"] = step.Path; return; }

        var normOld = AgentUtilities.NormalizeLineEndings(content);
        var normNew = AgentUtilities.NormalizeLineEndings(newContent);
        var minLen = Math.Min(normOld.Length, normNew.Length);
        var diffIdx = 0;
        while (diffIdx < minLen && normOld[diffIdx] == normNew[diffIdx]) diffIdx++;
        result["oldStartLine"] = normOld[..diffIdx].Count(c => c == '\n');

        await System.IO.File.WriteAllTextAsync(targetPath, newContent, Encoding.UTF8);
        if (contentCache != null) contentCache[targetPath] = newContent;
        PopulateEditResult(result, "modified", step.Path!, oldString, newString, newContent);
    }

    private static List<string> GetPlanSizeViolations(AgentPlan plan)
    {
        var violations = new List<string>();
        for (var i = 0; i < plan.Plan.Count; i++)
        {
            var step = plan.Plan[i];
            if (!AgentUtilities.IsRelativePath(step.File ?? "")) continue;
            var old = step.OldString ?? "";
            var lines = old.Split('\n').Length; var chars = old.Length;
            if (lines > 10 || chars > 400)
                violations.Add($"Step {i + 1} ({step.File}): oldString is {lines} lines/{chars} chars — will be resolved via focused call");
        }
        return violations;
    }

    private async Task<(bool isComplete, string reason)> AssessCompletion(
        string prompt, List<object> executedSteps, string projectRoot, CancellationToken ct,
        AgentPlan? plan = null, List<string>? attachedFiles = null)
    {
        var editSteps = executedSteps.OfType<Dictionary<string, object?>>()
            .Where(s => s.TryGetValue("type", out var t) && t?.ToString() == "edit").ToList();
        if (editSteps.Count == 0) return (true, "No edit steps — command-only task");

        var failed = editSteps.Where(s => !s.TryGetValue("status", out var st) || st?.ToString() is not ("done" or "skipped")).ToList();
        if (failed.Count > 0)
        {
            var failedPaths = string.Join(", ", failed.Select(f => f.GetValueOrDefault("path")?.ToString() ?? "?").Distinct());
            return (false, $"{failed.Count} edit step(s) failed: {failedPaths}");
        }

        var sb = new StringBuilder();
        sb.AppendLine("## Task"); sb.AppendLine(prompt); sb.AppendLine();

        if (plan?.Plan?.Count > 0)
        {
            sb.AppendLine("## Planned steps");
            foreach (var step in plan.Plan)
                sb.AppendLine($"- {step.File}: {step.Change}");
            sb.AppendLine();
        }

        sb.AppendLine("## Edit results");
        foreach (var s in editSteps.Take(10))
        {
            var path = s.GetValueOrDefault("path")?.ToString() ?? "?";
            var status = s.TryGetValue("status", out var st) ? st?.ToString() : "?";
            var error = s.TryGetValue("error", out var e) ? e?.ToString() : null;
            sb.AppendLine($"- {path}: {status}{(error != null ? $" → {error}" : "")}");
        }
        sb.AppendLine();

        // Collect paths of files modified by edit steps
        var modifiedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in editSteps)
        {
            var p = s.GetValueOrDefault("path")?.ToString();
            if (!string.IsNullOrWhiteSpace(p))
                modifiedSet.Add(p.Replace('\\', '/'));
        }

        // Attached files that were NOT modified: show their current content
        // The LLM must check whether each one still needs changes for the task
        if (attachedFiles != null && attachedFiles.Count > 0)
        {
            sb.AppendLine("## Unmodified attached files (check each one — does it still need changes to complete the task?)");
            foreach (var relPath in attachedFiles)
            {
                var normalized = relPath.Replace('\\', '/');
                if (modifiedSet.Contains(normalized)) continue; // skip modified — shown fresh below
                var fullPath = Path.GetFullPath(Path.Combine(projectRoot, normalized));
                if (!System.IO.File.Exists(fullPath)) continue;
                var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                sb.AppendLine($"### {normalized}\n```\n{content}\n```\n");
            }
        }

        // All modified files: refetch fresh from disk (no truncation)
        var allModifiedPaths = editSteps
            .Where(s => s.TryGetValue("status", out var st) && st?.ToString() == "done")
            .Select(s => s.GetValueOrDefault("path")?.ToString())
            .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();

        if (allModifiedPaths.Count > 0)
        {
            sb.AppendLine("## Modified files (current state after edits)");
            foreach (var relPath in allModifiedPaths)
            {
                var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath!.Replace('/', Path.DirectorySeparatorChar)));
                if (!System.IO.File.Exists(fullPath)) { sb.AppendLine($"### {relPath}\n*File not found*\n"); continue; }
                var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                sb.AppendLine($"### {relPath}\n```\n{content}\n```\n");
            }
        }

        sb.AppendLine(@"Evaluate the code changes against the original task. Check for:
1. Does the code solve the original task completely?
2. Are there any bugs, syntax errors, or logic issues in the modified files?
3. Are there any missing pieces or regressions?
4. Check every file in ""Unmodified attached files"" — does any of them need changes to satisfy the task? If yes, the task is NOT complete.
5. If the planned steps covered only part of the task (e.g. only backend but not frontend), report it as incomplete.

Respond with JSON only:
```json
{
  ""complete"": true|false,
  ""reason"": ""one sentence summary"",
  ""issues"": [""description of each bug or remaining work""]
}
```");

        const string sys = @"You are a thorough code reviewer and task completion verifier. Examine the original task, the changes made, and the current state of all files. Check for bugs, logic errors, syntax mistakes, and whether the task requirements are fully met. Pay special attention to files that were NOT modified — if any of them need changes to complete the task, mark complete=false. Output ONLY valid JSON in the format specified.";

        var (raw, _, _) = await CallLlmRaw(sys, sb.ToString(), ct, TimeSpan.FromSeconds(30));
        if (string.IsNullOrWhiteSpace(raw)) return (failed.Count == 0, "Assessment timed out");

        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```")) { var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) cleaned = m.Groups[1].Value.Trim(); }
            var s2 = cleaned.IndexOf('{'); var e2 = cleaned.LastIndexOf('}');
            if (s2 >= 0 && e2 > s2) cleaned = cleaned[s2..(e2 + 1)];
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            var isComplete = root.TryGetProperty("complete", out var c) && c.ValueKind == JsonValueKind.True;
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            // Collect issues into the reason for richer feedback
            if (root.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array)
            {
                var issueList = new List<string>();
                foreach (var issue in issues.EnumerateArray())
                {
                    if (issue.ValueKind == JsonValueKind.String)
                        issueList.Add(issue.GetString() ?? "");
                }
                if (issueList.Count > 0)
                    reason = reason + " | Issues: " + string.Join("; ", issueList);
            }
            return (isComplete, reason);
        }
        catch { return (failed.Count == 0, "Could not parse assessment"); }
    }

    private static AgentPlan MergePlans(AgentPlan? original, AgentPlan? replan)
    {
        if (original == null) return replan ?? new AgentPlan();
        if (replan == null) return original;
        var merged = new AgentPlan
        {
            Thinking = !string.IsNullOrWhiteSpace(replan.Thinking) ? replan.Thinking : original.Thinking,
            Summary = !string.IsNullOrWhiteSpace(replan.Summary) ? replan.Summary : original.Summary,
            Score = replan.Score > 0 ? replan.Score : original.Score,
            Plan = MergePlanSteps(original.Plan, replan.Plan)
        };
        return merged;
    }

    private static List<PlanStep> MergePlanSteps(IEnumerable<PlanStep> existing, IEnumerable<PlanStep> additions)
    {
        var result = new List<PlanStep>(existing);
        var existingKeys = new HashSet<string>(existing.Select(s => $"{s.File}|||{s.Change}"), StringComparer.OrdinalIgnoreCase);
        foreach (var step in additions)
        {
            var key = $"{step.File}|||{step.Change}";
            if (existingKeys.Add(key))
                result.Add(step);
        }
        return result;
    }

    private async Task<List<PlanStep>?> CheckpointReplan(
        string originalPrompt, string currentDiscoveryContext, List<PlanStep> remainingSteps,
        List<object> completedResults, string projectRoot, bool emitSse, CancellationToken ct,
        string? steeringContext = null)
    {
        var modifiedPaths = completedResults.OfType<Dictionary<string, object?>>()
            .Where(r => r.TryGetValue("type", out var t) && t?.ToString() is "edit" or "create" &&
                        r.TryGetValue("status", out var s) && s?.ToString() == "done")
            .Select(r => r.GetValueOrDefault("path")?.ToString())
            .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();

        await EmitLog(emitSse, "info", $"Checkpoint: refreshing {modifiedPaths.Count} file(s)…", ct: ct);
        var enriched = new StringBuilder(currentDiscoveryContext);
        enriched.AppendLine("\n## CHECKPOINT — current file states");
        foreach (var relPath in modifiedPaths)
        {
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath!.Replace('/', Path.DirectorySeparatorChar)));
            if (!System.IO.File.Exists(fullPath)) continue;
            var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
            enriched.AppendLine($"\n### {relPath} (post-phase)\n```\n{content}\n```");
        }
        if (remainingSteps.Count == 0) return null;

        var remainDesc = new StringBuilder("Intended remaining work (KEEP ALL of these — only add new ones):\n");
        foreach (var step in remainingSteps) remainDesc.AppendLine($"- {step.File}: {step.Change}");
        var replanPrompt = $"## Original task\n{originalPrompt}\n\n{remainDesc}" +
            (string.IsNullOrWhiteSpace(steeringContext) ? "" : $"\n## Steering\n{steeringContext}");

        var newPlan = await AnalyzePromptAndPlanCodeChanges(
            replanPrompt, enriched.ToString(), projectRoot, emitSse, ct, steeringContext);
        return newPlan?.Plan;
    }

    private async Task ExecuteRenameStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var srcRel = (step.Path ?? "").Replace('\\', '/');
        var dstRel = (step.ToPath ?? "").Replace('\\', '/');
        var srcPath = Path.GetFullPath(Path.Combine(projectRoot, srcRel.Replace('/', Path.DirectorySeparatorChar)));
        var dstPath = Path.GetFullPath(Path.Combine(projectRoot, dstRel.Replace('/', Path.DirectorySeparatorChar)));
        result["path"] = srcRel; result["toPath"] = dstRel;
        if (!AgentUtilities.IsPathUnderRoot(srcPath, projectRoot) || !AgentUtilities.IsPathUnderRoot(dstPath, projectRoot))
        { result["status"] = "error"; result["error"] = "Path outside project root"; return; }
        if (!System.IO.File.Exists(srcPath)) { result["status"] = "error"; result["error"] = $"Source not found: {srcRel}"; return; }
        if (System.IO.File.Exists(dstPath)) { result["status"] = "error"; result["error"] = $"Destination exists: {dstRel}"; return; }
        try
        {
            var dir = Path.GetDirectoryName(dstPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            System.IO.File.Move(srcPath, dstPath);
            result["status"] = "done"; result["editAction"] = "renamed";
        }
        catch (Exception ex) { result["status"] = "error"; result["error"] = ex.Message; }
    }

    private static void PopulateEditResult(
        Dictionary<string, object?> result, string action, string path,
        string? oldStr, string? newStr, string writtenContent)
    {
        result["type"] = "edit";
        result["status"] = "done";
        result["editAction"] = action;
        result["path"] = path;
        result["linesRemoved"] = (oldStr ?? "").Split('\n').Length;
        result["linesAdded"] = (newStr ?? "").Split('\n').Length;
        if (!string.IsNullOrEmpty(oldStr)) result["oldStringPreview"] = oldStr;
        if (!string.IsNullOrEmpty(newStr)) result["newStringPreview"] = newStr;
        result["diffPreview"] = AgentUtilities.BuildDiffPreview(oldStr, newStr);
        result["oldLines"] = (oldStr ?? "").Split('\n');
        result["newLines"] = (newStr ?? "").Split('\n');
    }

    // ── Multi-strategy string replacement (progressive fallback chain) ────

    private static string RepairJsonNewlines(string json)
    {
        // The LLM often outputs literal newlines inside JSON string values instead of \n,
        // or unescaped double quotes (especially in HTML attributes like *ngFor="...").
        // Walk through character by character tracking string state to fix both issues.
        var sb = new StringBuilder(json.Length);
        var inString = false;
        var escaped = false;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (escaped) { sb.Append(c); escaped = false; continue; }
            if (c == '\\' && inString) { sb.Append(c); escaped = true; continue; }
            if (c == '"' && !escaped)
            {
                // A " outside a string always toggles inString (valid delimiter).
                if (!inString) { inString = true; sb.Append(c); continue; }
                // Inside a string: this " could be an unescaped content quote (HTML attr)
                // or the closing delimiter. Peek ahead: if followed by , ] } : \s or EOF within
                // a short window, treat as delimiter; otherwise escape it.
                var lookahead = json.Length > i + 1 ? json[i + 1] : '\0';
                if (lookahead == ',' || lookahead == ']' || lookahead == '}' ||
                    lookahead == ':' || lookahead == '\t' ||
                    lookahead == '\n' || lookahead == '\r' || lookahead == ' ')
                {
                    inString = false; sb.Append(c);
                }
                else
                {
                    sb.Append("\\\""); // escape it
                }
                continue;
            }
            if (inString && (c == '\n' || c == '\r'))
            {
                sb.Append("\\n");
                if (c == '\r' && i + 1 < json.Length && json[i + 1] == '\n') i++;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static List<string> ExtractQuotedStrings(string raw)
    {
        var result = new List<string>();
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim().TrimEnd(',');
            if (trimmed.Length < 2 || !trimmed.StartsWith("\"")) continue;
            var lastQuote = trimmed.LastIndexOf('"');
            if (lastQuote <= 0) continue;
            result.Add(trimmed.Substring(1, lastQuote - 1).Replace("\\\"", "\""));
        }
        return result;
    }

    private static string CollapseWhitespace(string s) =>
        string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static int FindLineBlock(string[] fileLines, string[] patternLines, StringComparison cmp)
    {
        if (patternLines.Length == 0 || fileLines.Length < patternLines.Length) return -1;
        for (var fi = 0; fi <= fileLines.Length - patternLines.Length; fi++)
        {
            var match = true;
            for (var li = 0; li < patternLines.Length; li++)
            {
                if (!string.Equals(fileLines[fi + li], patternLines[li], cmp))
                { match = false; break; }
            }
            if (match) return fi;
        }
        return -1;
    }

    private static int ComputeLevenshteinDistance(string a, string b)
    {
        var m = a.Length; var n = b.Length;
        if (m == 0) return n; if (n == 0) return m;
        var d = new int[n + 1];
        for (var i = 0; i <= n; i++) d[i] = i;
        for (var i = 1; i <= m; i++)
        {
            var prev = d[0]; d[0] = i;
            for (var j = 1; j <= n; j++)
            {
                var temp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1),
                    prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = temp;
            }
        }
        return d[n];
    }

    private static double ComputeLineSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 1.0;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
        var aNorm = a.Trim().ToLowerInvariant();
        var bNorm = b.Trim().ToLowerInvariant();
        var maxLen = Math.Max(aNorm.Length, bNorm.Length);
        if (maxLen == 0) return 1.0;
        // For short strings use exact char comparison, for longer use Levenshtein
        if (maxLen <= 80)
            return 1.0 - (double)ComputeLevenshteinDistance(aNorm, bNorm) / maxLen;
        // For long strings, check common prefix ratio
        var common = 0; var minLen = Math.Min(aNorm.Length, bNorm.Length);
        for (var i = 0; i < minLen; i++) { if (aNorm[i] == bNorm[i]) common++; else break; }
        return (double)common / maxLen;
    }

    private static (int lineIdx, double score, bool hasExactLine) FindBestFuzzyBlock(string[] fileLines, string[] oldLines)
    {
        if (oldLines.Length == 0 || fileLines.Length < oldLines.Length) return (-1, 0, false);
        // Collapse whitespace so indentation/style differences don't penalize similarity
        var collapsedFile = fileLines.Select(CollapseWhitespace).ToArray();
        var collapsedOld = oldLines.Select(CollapseWhitespace).ToArray();
        var bestScore = 0.0; var bestIdx = -1; var bestHasExact = false;
        for (var fi = 0; fi <= collapsedFile.Length - collapsedOld.Length; fi++)
        {
            var totalSim = 0.0; var anyExact = false;
            for (var li = 0; li < collapsedOld.Length; li++)
            {
                var sim = ComputeLineSimilarity(collapsedFile[fi + li], collapsedOld[li]);
                totalSim += sim;
                if (sim >= 0.95) anyExact = true;
            }
            var avg = totalSim / collapsedOld.Length;
            if (avg > bestScore) { bestScore = avg; bestIdx = fi; bestHasExact = anyExact; }
        }
        return (bestIdx, bestScore, bestHasExact);
    }

    private static (int lineIdx, double score, int exactCount) FindBestAnchorLineBlock(string[] fileLines, string[] oldLines)
    {
        if (oldLines.Length == 0) return (-1, 0, 0);

        // A line is a candidate anchor only if it's long enough to be distinctive
        var candidates = oldLines
            .Select((l, i) => (line: l.Trim(), idx: i))
            .Where(x => x.line.Length >= 8)
            .OrderByDescending(x => x.line.Length)
            .ToList();

        foreach (var anchor in candidates)
        {
            var matches = new List<int>();
            for (var fi = 0; fi < fileLines.Length; fi++)
            {
                if (string.Equals(fileLines[fi].Trim(), anchor.line, StringComparison.Ordinal))
                    matches.Add(fi);
            }
            if (matches.Count != 1) continue;

            var filePos = matches[0] - anchor.idx;
            if (filePos < 0 || filePos + oldLines.Length > fileLines.Length) continue;

            var totalSim = 0.0; var exactCount = 0;
            for (var li = 0; li < oldLines.Length; li++)
            {
                var sim = ComputeLineSimilarity(fileLines[filePos + li], oldLines[li]);
                totalSim += sim;
                if (sim >= 0.95) exactCount++;
            }
            var avg = totalSim / oldLines.Length;
            if (avg >= 0.80 && exactCount >= 2)
                return (filePos, avg, exactCount);
        }
        return (-1, 0, 0);
    }

    private static (bool ok, string content, string? error, string? snippet) TryReplaceSafe(
        string content, string oldString, string newString)
    {
        content = AgentUtilities.NormalizeLineEndings(content);
        oldString = AgentUtilities.NormalizeLineEndings(oldString);
        newString = AgentUtilities.NormalizeLineEndings(newString);

        if (string.IsNullOrEmpty(oldString))
            return (false, content, "oldString is empty", null);

        var fileLines = content.Split('\n');
        var oldLines = oldString.Split('\n');
        if (oldLines.Length == 0) return (false, content, "oldString is empty", null);

        // ── S1: Exact ordinal match ─────────────────────────────────────────
        var idx = content.IndexOf(oldString, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var sec = content.IndexOf(oldString, idx + oldString.Length, StringComparison.Ordinal);
            if (sec >= 0) return (false, content,
                "oldString appears more than once — use a longer unique anchor", null);
            // Use ReplaceLineBlock to apply indentation correction (AutoIndentFromFile)
            var startLine = content[..idx].Count(c => c == '\n');
            return (true, ReplaceLineBlock(fileLines, startLine, oldLines.Length, newString), null, null);
        }

        // ── S2: Trailing-whitespace-trimmed exact match ────────────────────
        var trimmedOld = oldLines.Select(l => l.TrimEnd()).ToArray();
        var trimmedFile = fileLines.Select(l => l.TrimEnd()).ToArray();
        var matchLine = FindLineBlock(trimmedFile, trimmedOld, StringComparison.Ordinal);
        if (matchLine >= 0)
            return (true, ReplaceLineBlock(fileLines, matchLine, oldLines.Length, newString), null, null);

        // ── S3: Whitespace-collapsed case-sensitive match ──────────────────
        var wsOld = oldLines.Select(l => CollapseWhitespace(l)).ToArray();
        var wsFile = fileLines.Select(l => CollapseWhitespace(l)).ToArray();
        matchLine = FindLineBlock(wsFile, wsOld, StringComparison.Ordinal);
        if (matchLine >= 0)
            return (true, ReplaceLineBlock(fileLines, matchLine, oldLines.Length, newString), null, null);

        // ── S4: Whitespace-collapsed case-insensitive match ────────────────
        matchLine = FindLineBlock(wsFile, wsOld, StringComparison.OrdinalIgnoreCase);
        if (matchLine >= 0)
            return (true, ReplaceLineBlock(fileLines, matchLine, oldLines.Length, newString), null, null);

        // ── Guard: reject oldStrings that are too short or generic for fuzzy strategies ──
        var meaningfulChars = oldLines.Sum(l => l.Trim().Length);
        var maxMeaningfulLine = oldLines.Max(l => l.Trim().Length);
        if (meaningfulChars < 20 || maxMeaningfulLine < 8)
        {
            return (false, content,
                $"oldString too short or generic ({meaningfulChars} meaningful chars, longest line {maxMeaningfulLine} chars)", null);
        }

        // ── Fuzzy strategies: apply edit at high confidence ────────────────
        // S5 and S6 APPLY the edit when similarity is high enough.
        // The caller (ResolveAndApplyEdit) runs VerifyEdit on the result,
        // which catches any wrong-location matches.

        // S5: Fuzzy Levenshtein block match (high threshold → apply)
        {
            var (fl, score, hasExact) = FindBestFuzzyBlock(fileLines, oldLines);
            if (fl >= 0 && score >= 0.95)
            {
                var snippet = $"fuzzy {score:P0} at line {fl + 1}";
                return (true, ReplaceLineBlock(fileLines, fl, oldLines.Length, newString), null, snippet);
            }
            // Lower threshold → report location hint only (don't apply)
            if (fl >= 0 && score >= 0.88)
            {
                return (false, content, "oldString not found verbatim in file",
                    $"fuzzy {score:P0} at line {fl + 1}: too low confidence ({score:P0} < 95%)");
            }
        }

        // S6: Anchor-line block match (high threshold → apply)
        {
            var (al, score, exactCount) = FindBestAnchorLineBlock(fileLines, oldLines);
            if (al >= 0 && score >= 0.90 && exactCount >= 2)
            {
                var snippet = $"anchor {score:P0} at line {al + 1} ({exactCount} exact line(s))";
                return (true, ReplaceLineBlock(fileLines, al, oldLines.Length, newString), null, snippet);
            }
        }

        // S7: Single-line exact match (safe to apply — trimmed line appears exactly once)
        if (oldLines.Length == 1)
        {
            var trimmed = oldLines[0].Trim();
            if (trimmed.Length >= 15)
            {
                var occ = 0; var occIdx = -1;
                for (var fi = 0; fi < fileLines.Length; fi++)
                {
                    if (string.Equals(fileLines[fi].Trim(), trimmed, StringComparison.Ordinal))
                    { occ++; occIdx = fi; }
                }
                if (occ == 1)
                    return (true, ReplaceLineBlock(fileLines, occIdx, 1, newString),
                        null, $"single-line: line {occIdx + 1}");
            }
        }

        var hint = BuildExactMatchHint(content, oldString);
        return (false, content, "oldString not found verbatim in file", hint);
    }

    private static string StripFullFileFence(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var cleaned = value.Replace("\r\n", "\n");
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = cleaned.IndexOf('\n');
            if (firstNewline >= 0)
                cleaned = cleaned[(firstNewline + 1)..];
            else
                return string.Empty;
        }

        if (cleaned.EndsWith("```", StringComparison.Ordinal))
            cleaned = cleaned[..^3];

        return cleaned.TrimStart('\n').TrimEnd('\n');
    }

    private static string? BuildExactMatchHint(string content, string oldString)
    {
        var fileLines = content.Split('\n');
        var oldLines = oldString.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length >= 8)  // skip short generic lines like "}", ");"
            .ToList();
        if (oldLines.Count == 0 || fileLines.Length == 0) return null;

        var results = new List<(int fileIdx, double score, string line)>();
        for (var fi = 0; fi < fileLines.Length; fi++)
        {
            var fLine = fileLines[fi];
            if (fLine.Trim().Length < 8) continue;  // skip short file lines too
            var bestSim = oldLines.Max(o => ComputeLineSimilarity(fLine, o));
            if (bestSim >= 0.50)
                results.Add((fi, bestSim, fLine));
        }

        // Take the top 3 highest-scoring results, prefer longer lines for disambiguation
        var best = results
            .OrderByDescending(r => r.score)
            .ThenByDescending(r => r.line.Trim().Length)
            .Take(3)
            .ToList();
        if (best.Count == 0) return null;

        var detail = best.Select(b =>
            $"  ({(b.score * 100):F0}% match) line {b.fileIdx + 1}: {b.line}");
        return string.Join('\n', detail);
    }

    /// <summary>Find where oldString fuzzily matches the file and return the verbatim file lines for copying.</summary>
    private static string? BuildExactMatchBlock(string content, string oldString)
    {
        var fileLines = content.Split('\n');
        var oldLines = oldString.Split('\n');
        if (oldLines.Length < 2 || fileLines.Length < oldLines.Length) return null;

        // Use collapsed-whitespace fuzzy matching (same logic as FindBestFuzzyBlock)
        var collapsedFile = fileLines.Select(CollapseWhitespace).ToArray();
        var collapsedOld = oldLines.Select(CollapseWhitespace).ToArray();

        var bestScore = 0.0; var bestIdx = -1;
        for (var fi = 0; fi <= collapsedFile.Length - collapsedOld.Length; fi++)
        {
            var totalSim = 0.0;
            for (var li = 0; li < collapsedOld.Length; li++)
                totalSim += ComputeLineSimilarity(collapsedFile[fi + li], collapsedOld[li]);
            var avg = totalSim / collapsedOld.Length;
            if (avg > bestScore) { bestScore = avg; bestIdx = fi; }
        }

        if (bestIdx < 0 || bestScore < 0.85) return null;

        return string.Join("\n", fileLines.Skip(bestIdx).Take(oldLines.Length));
    }

    private static string? GetUnsafeEditPayloadReason(string oldString, string newString)
    {
        foreach (var marker in UnsafeEditMarkers)
            if (oldString.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                newString.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return $"Edit contains placeholder marker '{marker}'.";
        return null;
    }

    private static string RemoveUnsafeEditMarkersForPrompt(string value)
    {
        foreach (var marker in UnsafeEditMarkers)
            value = value.Replace(marker, "[placeholder removed]", StringComparison.OrdinalIgnoreCase);
        return value;
    }

    private static string ReplaceLineBlock(string[] fileLines, int start, int count, string replacement)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < start; i++) { sb.Append(fileLines[i]); sb.Append('\n'); }
        sb.Append(IndentReplacement(fileLines, start, replacement));
        for (var i = start + count; i < fileLines.Length; i++) { sb.Append('\n'); sb.Append(fileLines[i]); }
        return sb.ToString();
    }

    private static bool IsHtmlLikeContent(string content) =>
     content.Contains('<') && Regex.IsMatch(content, @"</?\w+[\s/>]");

    private static string IndentReplacement(string[] fileLines, int start, string replacement)
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

        var baseShifted = false;
        if (replBaseIndent != null && replBaseIndent != fileIndent)
        {
            baseShifted = true;
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

        // HTML/template files use tag-based nesting
        if (IsHtmlLikeContent(replacement))
            return AutoIndentHtml(string.Join("\n", replLines), fileIndent);

        return AutoIndentFromFile(string.Join("\n", replLines), fileIndent, fileLines, start);
    }
    private static readonly HashSet<string> VoidHtmlElements = new(StringComparer.OrdinalIgnoreCase)
{
    "area", "base", "br", "col", "embed", "hr", "img", "input",
    "link", "meta", "param", "source", "track", "wbr"
};

    /// <summary>
    /// Applies HTML-tag-depth indentation when the LLM produces flat output.
    /// If the replacement already has relative indentation (multiple distinct indent levels)
    /// it is returned unchanged. Otherwise, each line is placed at the correct tag depth.
    /// </summary>
    private static string AutoIndentHtml(string html, string baseIndent)
    {
        const string IndentStep = "  "; // 2 spaces per nesting level
        var lines = html.Split('\n');

        // If the content already has relative structure, preserve it exactly
        var distinctDepths = lines
            .Where(l => l.Trim().Length > 0)
            .Select(l => GetLeadingWhitespace(l).Length)
            .Distinct().Count();
        if (distinctDepths > 1) return html;

        var depth = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0) continue;

            // Closing tag → dedent BEFORE placing this line
            if (Regex.IsMatch(trimmed, @"^</[\w-]"))
                depth = Math.Max(0, depth - 1);

            lines[i] = baseIndent + new string(' ', depth * IndentStep.Length) + trimmed;

            // Opening tag: indent the NEXT line if this tag doesn't close on the same line
            var tagMatch = Regex.Match(trimmed, @"^<([\w-]+)[\s>]");
            if (tagMatch.Success)
            {
                var tag = tagMatch.Groups[1].Value;
                var isSelfClosing = trimmed.EndsWith("/>") || VoidHtmlElements.Contains(tag);
                var closedInline = trimmed.Contains($"</{tag}>");
                var isClosing = trimmed.StartsWith("</");
                var isComment = trimmed.StartsWith("<!--");
                if (!isSelfClosing && !closedInline && !isClosing && !isComment)
                    depth++;
            }
        }

        return string.Join("\n", lines);
    }
    /// <summary>Auto-indent replacement lines based on brace depth, using the file's indent style.</summary>
    private static string AutoIndentFromFile(string replacement, string fileIndent, string[] fileLines, int start)
    {
        // Infer indent size from the file (difference between parent and child indent levels)
        var indentSize = InferIndentSize(fileLines, start);
        if (indentSize <= 0) return replacement;

        var lines = replacement.Split('\n');
        var depth = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;

            var trimmed = lines[i].TrimStart();
            // Compute indent depth for THIS line only (don't mutate depth yet)
            var lineDepth = depth;
            if (trimmed.StartsWith("}"))
                lineDepth = Math.Max(0, lineDepth - 1);

            var expectedIndent = fileIndent + new string(' ', lineDepth * indentSize);
            var lineIndent = GetLeadingWhitespace(lines[i]);
            if (lineIndent != expectedIndent)
                lines[i] = expectedIndent + trimmed;

            // Update depth for the NEXT line by counting braces in this line
            foreach (var c in trimmed)
            {
                if (c == '{') depth++;
                if (c == '}') depth = Math.Max(0, depth - 1);
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>Infer the file's indent size (e.g. 2 or 4) by sampling indentation deltas.</summary>
    private static int InferIndentSize(string[] fileLines, int start)
    {
        var sampleStart = Math.Max(0, start - 5);
        var sampleEnd = Math.Min(fileLines.Length, start + 20);
        var deltas = new List<int>();
        for (var i = sampleStart + 1; i < sampleEnd; i++)
        {
            var prev = GetLeadingWhitespace(fileLines[i - 1]).Length;
            var curr = GetLeadingWhitespace(fileLines[i]).Length;
            var delta = Math.Abs(curr - prev);
            if (delta > 0 && delta <= 8)
                deltas.Add(delta);
        }
        if (deltas.Count == 0) return 2; // default
        // Use mode (most common delta) — more reliable than average for mixed indentation
        var mode = deltas.GroupBy(d => d).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
        return mode < 2 ? 2 : mode > 4 ? 4 : mode;
    }

    private static string GetLeadingWhitespace(string s)
    {
        var i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        return s[..i];
    }

    /// <summary>Apply brace-depth indentation to a full-file replacement using the original file's indent style.</summary>
    private static string AutoIndentFullFile(string fullContent, string[] originalLines)
    {
        var indentSize = InferIndentSize(originalLines, 0);
        if (indentSize <= 0) return fullContent;

        var lines = fullContent.Split('\n');
        var depth = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;

            var trimmed = lines[i].TrimStart();
            var lineDepth = depth;
            if (trimmed.StartsWith("}"))
                lineDepth = Math.Max(0, lineDepth - 1);

            var expectedIndent = new string(' ', lineDepth * indentSize);
            var lineIndent = GetLeadingWhitespace(lines[i]);
            if (lineIndent != expectedIndent)
                lines[i] = expectedIndent + trimmed;

            foreach (var c in trimmed)
            {
                if (c == '{') depth++;
                if (c == '}') depth = Math.Max(0, depth - 1);
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Multi-pass continuation for fullFile replacements that exceed the LLM's token limit.
    /// Detects truncation (unbalanced braces) and re-invokes the LLM to continue.
    /// </summary>
    private async Task<string> EnsureCompleteFullFile(string partialContent, PlanStep step,
        string fullPath, string projectRoot, bool emitSse, CancellationToken ct,
        List<(string old, string @new, string error)>? history = null)
    {
        if (!IsFullFileTruncated(partialContent))
            return partialContent;

        var accumulated = partialContent;
        var relPath = step.File.Replace('\\', '/');
        var maxPasses = 5;

        for (var pass = 0; pass < maxPasses; pass++)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"You are continuing a full-file replacement that was interrupted (token limit reached).");
            sb.AppendLine();
            sb.AppendLine($"FILE: {relPath}");
            sb.AppendLine($"CHANGE REQUIRED: {step.Change}");
            sb.AppendLine();
            sb.AppendLine("Here is the PARTIAL output you have generated so far (starting from the last complete brace-balanced point):");
            sb.AppendLine("```");
            // Find the last complete brace-balanced prefix for context
            var continuationStart = FindLastBalancedPrefix(accumulated);
            sb.AppendLine(continuationStart.Length > 2000
                ? continuationStart[^2000..] + "\n... (truncated view — the partial file is already written to disk)"
                : continuationStart);
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("Continue from where you left off. Output ONLY the REMAINING content — do NOT repeat any already-generated lines.");
            sb.AppendLine("The complete file must have balanced braces (equal number of { and }).");
            sb.AppendLine();
            sb.AppendLine("Output the continuation now (as raw text, no JSON, no markdown fences):");

            var continuationPrompt = sb.ToString();
            var continuationSystem =
                "You are a code completion assistant. Continue the partial file from where it was interrupted. " +
                "Output ONLY the remaining lines needed to complete the file. " +
                "Do NOT repeat any already-output content. The file uses brace-based indentation (C#/JS/TS style).";

            var (raw, _, _) = await CallLlmRaw(continuationSystem, continuationPrompt, ct,
                TimeSpan.FromSeconds(45), maxTokens: 8192);

            if (string.IsNullOrWhiteSpace(raw))
            {
                await EmitLog(emitSse, "warn",
                    $"Full-file continuation pass {pass + 1} returned empty — stopping", ct: ct);
                break;
            }

            // Strip any leading markdown fences or JSON wrapper the LLM might add
            raw = StripFullFileFence(raw);

            accumulated += "\n" + raw;

            if (!IsFullFileTruncated(accumulated))
            {
                await EmitLog(emitSse, "info",
                    $"Full-file complete after {pass + 2} pass(es) ({accumulated.Length} chars)", ct: ct);
                return accumulated;
            }
        }

        await EmitLog(emitSse, "warn",
            $"Full-file may still be truncated after {maxPasses} continuation passes — brace count: " +
            $"{accumulated.Count(c => c == '{')} / {accumulated.Count(c => c == '}')}", ct: ct);
        return accumulated;
    }

    /// <summary>Find the last position where braces are balanced in partial content.</summary>
    private static string FindLastBalancedPrefix(string content)
    {
        var depth = 0;
        var lastBalanced = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '{') depth++;
            if (content[i] == '}') depth = Math.Max(0, depth - 1);
            if (depth == 0) lastBalanced = i + 1;
        }
        return content[..Math.Max(lastBalanced, content.Length / 2)];
    }

    /// <summary>Apply a fullFile replacement: continuation, indent correction, write, and SSE. Only for NEW files.</summary>
    private async Task<int> ApplyFullFile(string fullContent, PlanStep step, string fullPath, string relPath,
        string projectRoot, int stepIndex, int planItemIndex, string? cardId, bool emitSse, CancellationToken ct,
        List<object> allResults)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        fullContent = await EnsureCompleteFullFile(fullContent, step, fullPath, projectRoot, emitSse, ct);

        var existingLines = System.IO.File.Exists(fullPath)
            ? await System.IO.File.ReadAllLinesAsync(fullPath, Encoding.UTF8, ct)
            : null;
        if (existingLines != null && existingLines.Length > 0)
            fullContent = AutoIndentFullFile(fullContent, existingLines);

        await System.IO.File.WriteAllTextAsync(fullPath, fullContent, Encoding.UTF8, ct);
        await EmitLog(emitSse, "success", $"✓ Written {relPath} ({fullContent.Length} chars)", ct: ct);
        var r = new Dictionary<string, object?>();
        PopulateEditResult(r, "modified", relPath, null, fullContent, "");
        r["index"] = stepIndex;
        r["planItemIndex"] = planItemIndex;
        if (emitSse) await SendSse(Response, "step", r, ct);
        allResults.Add(r);
        await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct);
        return stepIndex + 1;
    }

    private async Task ExecuteCommandStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var command = step.Command ?? "";
        if (string.IsNullOrWhiteSpace(command)) { result["status"] = "error"; result["error"] = "No command"; return; }
        var beforeLen = _terminal.ReadAll().Length;
        await _terminal.SendCommandAsync(command, projectRoot);
        var prevLen = beforeLen; var stableMs = 0;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(500);
            var curLen = _terminal.ReadAll().Length;
            if (curLen == prevLen) { stableMs += 500; if (stableMs >= 3000) break; }
            else { stableMs = 0; prevLen = curLen; }
        }
        result["status"] = "done"; result["command"] = command;
        var fullOutput = _terminal.ReadAll();
        result["output"] = beforeLen >= 0 && beforeLen < fullOutput.Length ? fullOutput[beforeLen..] : "";
        result["snippet"] = result["output"] as string ?? "";
    }

    private async Task<(string output, string? error)> WebSearchAsync(string query, CancellationToken ct)
    {
        try
        {
            var client = _clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(1);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            var apiUrl = "https://api.duckduckgo.com/?q=" + Uri.EscapeDataString(query) + "&format=json&no_html=1&skip_disambig=1";
            var resp = await client.GetAsync(apiUrl, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var sb = new StringBuilder();
            if (root.TryGetProperty("AbstractText", out var abs) && abs.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(abs.GetString()))
            { sb.AppendLine("## Summary"); sb.AppendLine(abs.GetString()); if (root.TryGetProperty("AbstractURL", out var url)) sb.AppendLine($"Source: {url.GetString()}"); sb.AppendLine(); }
            if (root.TryGetProperty("Answer", out var ans) && ans.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(ans.GetString()))
                sb.AppendLine($"Answer: {ans.GetString()}");
            if (root.TryGetProperty("RelatedTopics", out var topics) && topics.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine("## Results"); var count = 0;
                foreach (var topic in topics.EnumerateArray())
                {
                    if (count >= 10) break;
                    if (topic.TryGetProperty("Text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        var u = topic.TryGetProperty("FirstURL", out var fu) ? fu.GetString() : "";
                        sb.AppendLine($"  - {text.GetString()}{(string.IsNullOrWhiteSpace(u) ? "" : $" ({u})")}"); count++;
                    }
                }
            }
            return (sb.Length > 0 ? sb.ToString() : "(no results)", null);
        }
        catch (Exception ex) { return ("", ex.Message); }
    }

    private async Task<(string output, string? error)> WebFetchAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = _clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            var resp = await client.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "text/plain";
            if (contentType.Contains("html")) body = Regex.Replace(body, "<[^>]+>", " ");
            return ($"HTTP {(int)resp.StatusCode}\n{body.Trim()}", null);
        }
        catch (Exception ex) { return ("", ex.Message); }
    }

    private async Task ExecuteReadStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var relPath = (step.Path ?? "").Replace('/', Path.DirectorySeparatorChar);
        var targetPath = Path.GetFullPath(Path.Combine(projectRoot, relPath));
        if (!AgentUtilities.IsPathUnderRoot(targetPath, projectRoot)) { 
            result["status"] = "error"; 
            result["error"] = "Path outside root"; 
            return;
        }
        if (!System.IO.File.Exists(targetPath)) { 
            result["status"] = "error"; 
            result["error"] = "File not found"; 
            return; 
        }
        result["path"] = step.Path;
        result["output"] = await System.IO.File.ReadAllTextAsync(targetPath, Encoding.UTF8);
        result["status"] = "done";
    }

    private Task ExecuteListStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var relPath = string.IsNullOrWhiteSpace(step.Path) ? "" : step.Path.Replace('/', Path.DirectorySeparatorChar);
        var targetPath = Path.GetFullPath(Path.Combine(projectRoot, relPath));
        if (!AgentUtilities.IsPathUnderRoot(targetPath, projectRoot)) { result["status"] = "error"; result["error"] = "Path outside root"; return Task.CompletedTask; }
        if (!Directory.Exists(targetPath)) { result["status"] = "error"; result["error"] = "Directory not found"; return Task.CompletedTask; }
        var entries = Directory.GetFileSystemEntries(targetPath)
            .Select(e => (Directory.Exists(e) ? "[dir]  " : "[file] ") + Path.GetFileName(e))
            .OrderBy(x => x).Take(200);
        result["status"] = "done"; result["path"] = step.Path ?? ".";
        result["output"] = string.Join("\n", entries);
        return Task.CompletedTask;
    }

    private Task ExecuteGlobStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var pattern = (step.Pattern ?? step.Path ?? "*").Replace('\\', '/');
        result["path"] = pattern;
        try
        {
            IEnumerable<string> files;
            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                var parts = pattern.Split('/'); var filePattern = parts[^1];
                var dirParts = parts.Length > 1 ? parts[..^1] : Array.Empty<string>();
                var hasRec = dirParts.Any(p => p == "**");
                var dirClean = dirParts.Where(p => p != "**").ToList();
                if (dirClean.Count == 0 || hasRec)
                    files = Directory.EnumerateFiles(projectRoot, filePattern == "**" ? "*" : filePattern, SearchOption.AllDirectories);
                else
                {
                    var searchRoot = Path.GetFullPath(Path.Combine(projectRoot, string.Join(Path.DirectorySeparatorChar, dirClean)));
                    if (!AgentUtilities.IsPathUnderRoot(searchRoot, projectRoot)) throw new InvalidOperationException("Pattern outside root");
                    files = Directory.EnumerateFiles(searchRoot, filePattern, SearchOption.AllDirectories);
                }
            }
            else
            {
                var single = Path.GetFullPath(Path.Combine(projectRoot, pattern));
                files = System.IO.File.Exists(single) ? new[] { single } : Array.Empty<string>();
            }
            var list = files.Where(f => AgentUtilities.IsPathUnderRoot(f, projectRoot)).Take(100)
                .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/')).ToList();
            result["status"] = "done"; result["output"] = list.Count == 0 ? "(no matches)" : string.Join("\n", list);
        }
        catch (Exception ex) { result["status"] = "error"; result["error"] = ex.Message; }
        return Task.CompletedTask;
    }

    private Task ExecuteGrepStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var query = step.Query ?? step.Pattern ?? "";
        result["path"] = step.Path ?? ""; result["query"] = query;
        if (string.IsNullOrWhiteSpace(query)) { result["status"] = "error"; result["error"] = "grep requires query"; return Task.CompletedTask; }
        var searchRoot = projectRoot;
        if (!string.IsNullOrWhiteSpace(step.Path))
        {
            searchRoot = Path.GetFullPath(Path.Combine(projectRoot, step.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!AgentUtilities.IsPathUnderRoot(searchRoot, projectRoot)) { result["status"] = "error"; result["error"] = "Path outside root"; return Task.CompletedTask; }
        }
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules", ".git", "bin", "obj", "dist", ".angular" };
        var matches = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories))
            {
                if (!AgentUtilities.IsPathUnderRoot(file, projectRoot)) continue;
                if (skipDirs.Any(d => file.Contains(Path.DirectorySeparatorChar + d + Path.DirectorySeparatorChar))) continue;
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > 500_000) continue;
                    var lines = System.IO.File.ReadAllLines(file);
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (!lines[i].Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                        matches.Add($"{Path.GetRelativePath(projectRoot, file).Replace('\\', '/')}:{i + 1}: {lines[i].Trim()}");
                        if (matches.Count >= 50) break;
                    }
                }
                catch { }
                if (matches.Count >= 50) break;
            }
            result["status"] = "done";
            result["output"] = matches.Count == 0 ? "(no matches)" : string.Join("\n", matches);
        }
        catch (Exception ex) { result["status"] = "error"; result["error"] = ex.Message; }
        return Task.CompletedTask;
    }

    private async Task ExecuteWebStep(AgentStep step, Dictionary<string, object?> result)
    {
        var isFetch = step.Type is "web_fetch";
        var target = step.Url ?? step.Path ?? "";
        var query = step.Query ?? "";
        try
        {
            var client = _clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            if (isFetch || (!string.IsNullOrWhiteSpace(target) && Uri.TryCreate(target, UriKind.Absolute, out _)))
            {
                var url = Uri.TryCreate(target, UriKind.Absolute, out var pu) ? pu : new Uri(target);
                var resp = await client.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();
                var ct2 = resp.Content.Headers.ContentType?.MediaType ?? "text/plain";
                if (ct2.Contains("html")) body = Regex.Replace(body, "<[^>]+>", " ");
                result["status"] = "done"; result["url"] = url.ToString();
                result["output"] = $"HTTP {(int)resp.StatusCode}\n{body.Trim()}";
            }
            else
            {
                var search = !string.IsNullOrWhiteSpace(query) ? query : target;
                if (string.IsNullOrWhiteSpace(search)) { result["status"] = "error"; result["error"] = "web_search requires query"; return; }
                var (searchOut, _) = await WebSearchAsync(search, CancellationToken.None);
                result["status"] = "done"; result["query"] = search; result["output"] = searchOut;
            }
        }
        catch (Exception ex) { result["status"] = "error"; result["error"] = ex.Message; }
    }

    private static void AppendPlanToConversation(StringBuilder conversation, List<PlanStep> steps, int startIndex, int totalCount)
    {
        conversation.AppendLine("\n### PLAN ###");
        for (var pi = 0; pi < steps.Count; pi++)
            conversation.AppendLine($"  Step {startIndex + pi}: [{steps[pi].File}] {steps[pi].Change}");
        conversation.AppendLine("### END PLAN ###");
    }

    private async Task<List<EditResult>> ApplyEditsDirect(List<EditAction> edits, string projectRoot)
    {
        var results = new List<EditResult>();
        var fileGroups = new Dictionary<string, List<EditAction>>(StringComparer.OrdinalIgnoreCase);
        var fileOrder = new List<string>();
        foreach (var edit in edits)
        {
            if (!fileGroups.ContainsKey(edit.Path)) { fileGroups[edit.Path] = new(); fileOrder.Add(edit.Path); }
            fileGroups[edit.Path].Add(edit);
        }
        foreach (var filePath in fileOrder)
        {
            var fileEdits = fileGroups[filePath];
            var targetPath = Path.GetFullPath(Path.Combine(projectRoot, filePath));
            if (!AgentUtilities.IsPathUnderRoot(targetPath, projectRoot))
            { foreach (var _ in fileEdits) results.Add(new EditResult { Path = filePath, Status = "skipped", Error = "Path outside root" }); continue; }
            string content = "";
            var fileExists = System.IO.File.Exists(targetPath);
            if (fileExists) content = await System.IO.File.ReadAllTextAsync(targetPath, Encoding.UTF8);
            else if (fileEdits.Any(e => !string.IsNullOrEmpty(e.OldString)))
            { foreach (var e in fileEdits) results.Add(new EditResult { Path = filePath, Status = "skipped", Error = "File does not exist" }); continue; }
            var hasError = false;
            foreach (var edit in fileEdits)
            {
                var ur = GetUnsafeEditPayloadReason(edit.OldString, edit.NewString ?? "");
                if (ur != null) { results.Add(new EditResult { Path = filePath, Status = "error", Error = ur }); hasError = true; break; }
                if (!fileExists && string.IsNullOrEmpty(edit.OldString)) { content = edit.NewString ?? ""; continue; }
                if (string.IsNullOrEmpty(edit.OldString)) { content += edit.NewString ?? ""; continue; }
                var (ok, newContent, err, snippet) = TryReplaceSafe(content, edit.OldString, edit.NewString ?? "");
                if (!ok)
                {
                    var fullErr = err;
                    if (!string.IsNullOrEmpty(snippet)) fullErr += $". Nearby: {snippet}";
                    results.Add(new EditResult { Path = filePath, Status = "error", Error = fullErr });
                    hasError = true; break;
                }
                content = newContent;
            }
            if (!hasError)
            {
                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                await System.IO.File.WriteAllTextAsync(targetPath, content, Encoding.UTF8);
                results.Add(new EditResult { Path = filePath, Status = "written" });
            }
        }
        return results;
    }

    private static AgentResponse? ParseAgentResponse(string raw)
    {
        var jsonStr = raw.Trim();
        if (jsonStr.StartsWith("```")) { var m = Regex.Match(jsonStr, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) jsonStr = m.Groups[1].Value.Trim(); }
        var start = jsonStr.IndexOf('{'); var end = jsonStr.LastIndexOf('}');
        if (start >= 0 && end > start) jsonStr = jsonStr[start..(end + 1)];
        try
        {
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var steps = JsonSerializer.Deserialize<List<AgentStep>>(jsonStr, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (steps?.Count > 0) return new AgentResponse { Steps = steps, Summary = "Parsed array" };
            }
            if (root.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
            {
                var steps = JsonSerializer.Deserialize<List<AgentStep>>(stepsEl.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (steps?.Count > 0)
                {
                    var thinking = root.TryGetProperty("thinking", out var th) ? th.GetString() ?? "" : "";
                    var summary = root.TryGetProperty("summary", out var sm) ? sm.GetString() ?? "" : "";
                    var complete = root.TryGetProperty("complete", out var cp) && cp.ValueKind == JsonValueKind.True;
                    return new AgentResponse { Thinking = thinking, Summary = summary, Complete = complete, Steps = steps };
                }
            }
        }
        catch { }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  BUILD CHECK + SELF-IMPROVING
    // ═══════════════════════════════════════════════════════════════════════

    /// <returns>true if build passed, false if build still has issues</returns>
    private async Task<bool> RunSmartBuildCheck(string projectRoot, string buildCmd, bool emitSse, CancellationToken ct)
    {
        const string systemPrompt = @"You are a build checker. Analyze the build output.
Output ONLY valid JSON (no markdown):
{""decision"": ""done""|""command""|""ask_user"", ""summary"": ""brief"", ""command"": ""cmd if needed"", ""userQuestion"": ""question if needed""}
done = build OK; command = run this to fix; ask_user = need input";

        _terminal.Start();
        await EmitLog(emitSse, "info", $"Build check: {buildCmd}", ct: ct);
        var iteration = 0; const int maxIter = 5;

        while (iteration < maxIter)
        {
            iteration++;
            var beforeLen = _terminal.ReadAll().Length;
            await _terminal.SendCommandAsync(buildCmd, projectRoot);
            var prevLen = beforeLen;
            for (var i = 0; i < 30; i++) { await Task.Delay(500); var cl = _terminal.ReadAll().Length; if (cl == prevLen) break; prevLen = cl; }
            var output = _terminal.ReadAll();
            var fresh = beforeLen < output.Length ? output[beforeLen..] : output;

            var userPrompt = $"Build command: {buildCmd}\nOutput:\n```\n{fresh}\n```\nIteration: {iteration}/{maxIter}";
            var (raw, err) = await CallLlmRawText(systemPrompt, userPrompt, ct);
            if (string.IsNullOrWhiteSpace(raw)) { await EmitLog(emitSse, "warn", $"Build check LLM failed: {err}", ct: ct); break; }

            var decision = ParseBuildCheckResponse(raw);
            if (decision == null) { await EmitLog(emitSse, "warn", "Could not parse build check response", ct: ct); break; }

            switch (decision.Decision)
            {
                case "done": await EmitLog(emitSse, "success", $"Build OK: {decision.Summary}", ct: ct); return true;
                case "command":
                    if (!string.IsNullOrWhiteSpace(decision.Command))
                    {
                        await EmitLog(emitSse, "info", $"Build fix: {decision.Command}", ct: ct); 
                        await _terminal.SendCommandAsync(decision.Command, projectRoot); 
                        await Task.Delay(2000); 
                    }
                    continue;
                case "ask_user":
                    await EmitLog(emitSse, "info", $"Build needs user input: {decision.Summary}", ct: ct);
                    return false;
                default: return false;
            }
        }
        await EmitLog(emitSse, "warn", $"Build check inconclusive after {maxIter} iterations", ct: ct);
        return false;
    }

    private static BuildCheckDecision? ParseBuildCheckResponse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var json = raw.Trim();
        if (json.StartsWith("```")) { var m = Regex.Match(json, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) json = m.Groups[1].Value.Trim(); }
        try { return JsonSerializer.Deserialize<BuildCheckDecision>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch
        {
            var rep = AgentUtilities.RepairJsonString(json);
            if (rep != null)
                try { return JsonSerializer.Deserialize<BuildCheckDecision>(rep, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
                catch { }
        }
        return null;
    }

    private async Task RepairPipeline(
        string projectRoot, bool emitSse, CancellationToken ct,
        string originalPrompt, string? steeringContext)
    {
        var buildOutput = _terminal.ReadAll();
        var resultSteps = new List<object>();
        await RunRepairPlan(projectRoot, emitSse, ct, originalPrompt, buildOutput, resultSteps, steeringContext);

        var cfg = await _configFile.LoadConfigAsync();
        var cmds = ParseBuildCommands(cfg.buildCommands);
        bool repairOk = true;
        foreach (var cmd in cmds)
        {
            var ok = await RunSmartBuildCheck(projectRoot, cmd, emitSse, ct);
            if (!ok) { repairOk = false; }
        }

        if (repairOk)
            await EmitLog(emitSse, "success", "RepairPipeline: build fixed successfully.", ct: ct);
        else
            await EmitLog(emitSse, "warn", "RepairPipeline: build still has errors after repair attempt.", ct: ct);

        if (emitSse)
            await SendSse(Response, "done_signal", new { message = "Build repair completed" }, ct);
    }

    private async Task RunRepairPlan(
        string projectRoot, bool emitSse, CancellationToken ct,
        string prompt, string buildOutput, List<object> resultSteps,
        string? steeringContext = null)
    {
        await EmitLog(emitSse, "info", "RunRepairPlan: analyzing build errors…", ct: ct);
        if (emitSse)
            await SendSse(Response, "phase", new { phase = "repair", message = "Analyzing build errors and planning fixes…" }, ct);

        var tail = buildOutput.Length > 8000 ? buildOutput[^8000..] : buildOutput;
        var repairPrompt = $"BUILD OUTPUT:\n```\n{tail}\n```\n\nAnalyze the build output above, identify compilation errors, and fix them by editing the source files. Do not add new features — only fix compilation errors/warnings.";
        var repairSteering = $"BUILD REPAIR: Fix the compilation errors shown in the build output. {(string.IsNullOrWhiteSpace(steeringContext) ? "" : $"\nOriginal task: {steeringContext}")}";

        var plan = await AnalyzePromptAndPlanCodeChanges(
            repairPrompt, tail, projectRoot, emitSse, ct, repairSteering);

        if (plan == null || plan.Plan.Count == 0)
        {
            await EmitLog(emitSse, "warn", "RunRepairPlan: no repair plan generated.", ct: ct);
            return;
        }

        if (emitSse)
            await SendSse(Response, "plan",
                new { thinking = plan.Thinking, summary = $"Build repair: {plan.Summary}", items = plan.Plan }, ct);

        await ExecutePlan(repairPrompt, projectRoot, emitSse, tail, plan, ct, resultSteps,
            steeringContext: repairSteering);
    }

    private async Task RunSelfImprovingPipeline(
        string prompt, string projectRoot, List<object> allSteps,
        AgentPlan? plan, bool complete, bool editsApplied)
    {
        var filePath = Path.Combine(projectRoot, "improvementdata.json");
        List<JsonElement> features = new();
        if (System.IO.File.Exists(filePath))
        {
            try
            {
                var ex = await System.IO.File.ReadAllTextAsync(filePath);
                var root = JsonSerializer.Deserialize<JsonElement>(ex);
                if (root.TryGetProperty("features", out var feats) && feats.ValueKind == JsonValueKind.Array)
                    features = feats.EnumerateArray().ToList();
            }
            catch { }
        }
        var now = DateTime.UtcNow.ToString("o");
        var filesEdited = ExtractFilesEdited(allSteps);
        var filePaths = filesEdited.Select(f =>
        {
            if (f is Dictionary<string, object?> d && d.TryGetValue("path", out var p) && p is string ps) return ps;
            if (f is JsonElement je && je.TryGetProperty("path", out var pp)) return pp.GetString() ?? "";
            return "";
        }).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();

        var entry = new Dictionary<string, object?> { ["description"] = plan?.Summary ?? "No summary", ["complete"] = complete && editsApplied, ["date"] = now };
        var existIdx = features.FindIndex(f => f.TryGetProperty("feature", out var ft) && ft.GetString() == prompt);
        Dictionary<string, object?> featureEntry;
        List<object> improvements;
        if (existIdx >= 0)
        {
            featureEntry = JsonSerializer.Deserialize<Dictionary<string, object?>>(features[existIdx].GetRawText()) ?? new();
            improvements = new List<object>();
            featureEntry["lastUpdated"] = now;
        }
        else
        {
            featureEntry = new Dictionary<string, object?> { ["feature"] = prompt, ["files"] = filePaths, ["improvements"] = new List<object>(), ["lastUpdated"] = now };
            improvements = new List<object>();
        }
        improvements.Add(entry); featureEntry["improvements"] = improvements;
        if (existIdx >= 0) features[existIdx] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(featureEntry));
        else features.Add(JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(featureEntry)));

        var output = new Dictionary<string, object?> { ["features"] = features.Select(f => JsonSerializer.Deserialize<Dictionary<string, object?>>(f.GetRawText())).ToList() };
        var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        await System.IO.File.WriteAllTextAsync(filePath, json);
        await EmitLog(true, "info", $"Self-improving data written for: {prompt}");
    }
}