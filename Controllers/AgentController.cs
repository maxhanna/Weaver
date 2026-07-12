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

namespace Weaver.Controllers;

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
    private readonly EditKnowledgeService _editKnowledge;
    private const int MAX_INCREMENTAL_STEPS = 24;
    private const int MAX_INCREMENTAL_SUBPLANS = 8;
    private const int MAX_STEP_REGEN_ATTEMPTS = 3;
    private const int MAX_COMMAND_ITERATIONS = 30;
    private FrontendConfig? _cfgCache;
    private DateTime _cfgCacheTime = DateTime.MinValue;

    private async Task<FrontendConfig> LoadConfigAsync()
    {
        if (_cfgCache == null || (DateTime.UtcNow - _cfgCacheTime).TotalSeconds > 3)
        {
            _cfgCache = await _configFile.LoadConfigAsync();
            _cfgCacheTime = DateTime.UtcNow;
        }
        return _cfgCache;
    }

    private bool _lastConnectionCheckResult = true;
    private bool _gracefulStop;
    private static DateTime _nextConnectivityCheck = DateTime.MinValue;
    private static TimeSpan _infiniteTimeout = Timeout.InfiniteTimeSpan;
    private static readonly ConcurrentDictionary<string, PendingQuestion> _pendingQuestions = new();
    private static readonly ConcurrentDictionary<string, PendingContextReview> _pendingContextReviews = new();
    private static readonly ConcurrentDictionary<string, HashSet<int>> _cancelledSteps = new();
    private const int PLAN_SCORE_THRESHOLD = 65;
    private const int MAX_PLANNING_ITERATIONS = 3;
    private const int MAX_LINES_PER_DISCOVERY_FILE = 10000;
    private const int MAX_DISCOVERY_FILES = 20;
    private static readonly string[] UnsafeEditMarkers =
    {
        "…(truncated)", "â€¦(truncated)", "...(truncated)"
    };

    private const string D_OLD = "<<<OLD>>>";
    private const string D_OLD_END = "<<<END_OLD>>>";
    private const string D_NEW = "<<<NEW>>>";
    private const string D_NEW_END = "<<<END_NEW>>>";
    private const string D_FULL = "<<<FULL_FILE>>>";
    private const string D_FULL_END = "<<<END_FULL_FILE>>>";
    private const string D_DONE = "<<<ALREADY_DONE>>>";

    private static string BuildEditSystemPrompt(string editFormat)
    {
        var intro = "You are a surgical code editor. Output ONLY a JSON object.\n\n";

        var formatSection = editFormat switch
        {
            "format_c_insert" =>
                "You MUST use FORMAT C to add a new method (insertAfter with an existing method as anchor):\n" +
                "{\n" +
                "  \"targetType\": \"method\",\n" +
                "  \"targetName\": \"ExistingMethodName\",\n" +
                "  \"insertAfter\": true,\n" +
                "  \"newCode\": [\"    [HttpPost(\\\"route\\\")]\", \"    public async Task<IActionResult> NewMethod() {\", \"        ...\", \"    }\"]\n" +
                "}\n" +
                "  - targetType MUST be \"method\".\n" +
                "  - targetName MUST be an EXISTING method name in the file (copy it VERBATIM from the file content).\n" +
                "  - insertAfter MUST be true.\n" +
                "  - newCode is the COMPLETE new method including attributes, signature, and body. Can be a string or array of lines.\n" +
                "  - Do NOT use any other format. Do NOT return alreadyDone. Do NOT use fullFile.\n\n",

            "delete" =>
                "You are DELETING code. Use this format:\n" +
                "{\n" +
                "  \"oldString\": [\"  line to delete\", \"  another line\"],\n" +
                "  \"newString\": []\n" +
                "}\n" +
                "  - oldString = EXACT lines to remove (1-5 lines max). Copy verbatim from the file.\n" +
                "  - newString = empty array [].\n" +
                "  - NEVER include surrounding container lines — delete ONLY what's asked.\n\n",

            "full_file" =>
                "This is a NEW FILE (does not exist yet). Use full-file replacement:\n" +
                "{\n" +
                "  \"fullFile\": [\"...entire file content...\"]\n" +
                "}\n" +
                "  - fullFile MUST contain EVERY line of the new file.\n" +
                "  - Use array format for multi-line content.\n\n",

            "format_c_class_fill" =>
                "You MUST use FORMAT C to fill an EXISTING class body with new property/field declarations:\n" +
                "{\n" +
                "  \"targetType\": \"class\",\n" +
                "  \"targetName\": \"ExistingClassName\",\n" +
                "  \"newCode\": [\"    public string? Token { get; set; }\", \"    public string? Date { get; set; }\"]\n" +
                "}\n" +
                "  - targetType MUST be \"class\".\n" +
                "  - targetName MUST be the EXISTING class name copied VERBATIM from the file (e.g. the class already shown as empty `{ }` in the file content below).\n" +
                "  - newCode MUST contain ONLY the new property/field lines to insert inside the class body — one per line.\n" +
                "  - Do NOT repeat the class declaration or braces in newCode. Do NOT set insertAfter.\n" +
                "  - Do NOT use oldString/newString. Do NOT use fullFile. Do NOT touch any other method in the file.\n\n",

            _ =>
                "Use oldString/newString targeted edit format:\n" +
                "{\n" +
                "  \"oldString\": [\"  line1 EXACTLY as in file\", \"  line2\"],\n" +
                "  \"newString\": [\"  line1\", \"  replacement line2\"]\n" +
                "}\n" +
                "  CRITICAL: Each array element is ONE line of code. NEVER split a line across multiple elements.\n" +
                "  TRAILING WHITESPACE: You MAY omit trailing spaces at the END of a line of code (before the newline). " +
                "But LEADING whitespace (indentation) is REQUIRED.\n" +
                "  For single-line: {\"oldString\": \"line1\\nline2\", \"newString\": \"replacement line 1\\nreplacement line 2\"}\n\n"
        };

        var commonRules =
            "CRITICAL RULES:\n" +
            "1. oldString must exist VERBATIM in the file — copy character-for-character including EVERY leading space and tab (indentation).\n" +
            "2. oldString MUST be 1-5 lines. NEVER include surrounding containers.\n" +
            "3. NEVER use placeholders (... or […] or /* ... */) in oldString or newString\n" +
            "4. oldString must NOT have blank first/last lines — trim any empty lines\n" +
            "5. Each line's meaningful content should be ≥ 8 characters — lines like `}`, `);`, `{` are too short.\n" +
            "6. Output ONLY the JSON — no markdown, no code fences, no introductory text\n" +
            "7. NO-OP PREVENTION (CRITICAL): oldString and newString MUST be DIFFERENT. " +
                "If the step asks you to ADD or INSERT code, the new code MUST appear in newString but MUST NOT appear in oldString.\n" +
            "8. INDENTATION: newString MUST use the EXACT SAME leading whitespace as oldString for every line.\n" +
            "9. MODIFY the existing, don't ADD new alongside the existing. If you see duplicate functionality in newString, REMOVE the old duplicate part.\n" +
            "10. NEVER INVENT type names or property names. Every type/property you reference MUST exist in the project.\n" +
            "11. SPACING — tokens concatenated without spaces are the #1 cause of bad edits. Verify EVERY token boundary.\n" +
            "12. ATOMIC STEPS: Execute EXACTLY what the CHANGE REQUIRED asks for — no more, no less.\n" +
            "13. If your change introduces a new SQL table, include a CREATE TABLE IF NOT EXISTS statement BEFORE any INSERT/UPDATE.\n" +
            "14. NEVER write `{{ex.Message}}` inside an interpolated string — use `{ex.Message}` with single braces.\n";

        return intro + formatSection + commonRules;
    }

    public AgentController(
        IHttpClientFactory cf, IConfiguration config,
        IWebHostEnvironment env, TerminalService terminal, FileHintsManager fileHints,
        ConfigFileService configFile, EmailService emailService, BoardDataService boardData)
    {
        _clientFactory = cf; _config = config; _env = env; _terminal = terminal;
        _fileHints = fileHints; _configFile = configFile; _emailService = emailService;
        _boardData = boardData;
        var weaverDataDir = Path.Combine(_env.ContentRootPath, "data");
        _editKnowledge = new EditKnowledgeService(
            weaverDataDir,
            llmCaller: async (sys, usr, ct) =>
            {
                var (raw, _, err) = await CallLlmRawStreaming(sys, usr, false, ct,
                    requestTimeout: TimeSpan.FromMinutes(1), maxTokens: 512);
                return (raw, err);
            },
            logger: (lvl, msg) =>
            {
                Task.Run(async () =>
                {
                    try { await EmitLog(false, lvl, msg, ct: CancellationToken.None); }
                    catch { }
                });
            });
    }

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
        catch (OperationCanceledException e) { Console.WriteLine($"ERROR, OperationCanceledException. Message: {e.Message}"); }
        catch (ObjectDisposedException e) { Console.WriteLine($"ERROR, ObjectDisposedException. Message: {e.Message}"); }
        catch (IOException e) { Console.WriteLine($"ERROR, IOException. Message: {e.Message}"); }
        catch (Exception e) { Console.WriteLine($"ERROR, Exception. Message: {e.Message}"); }
    }

    private (string? oldStr, string? error) AstResolveEdit(string fullPath, string targetType, string targetName, bool returnTail = false)
    {
        if (!System.IO.File.Exists(fullPath))
            return (null, "File not found for AST edit");

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        var sourceText = System.IO.File.ReadAllText(fullPath, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(sourceText))
            return (null, "File is empty");


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
                var defIndent = AgentUtilities.GetLeadingWhitespace(defLine);

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

            var openBraceIdx = sourceText.IndexOf('{', startIdx);
            if (openBraceIdx < 0)
                return (null, $"{label} '{targetName}' has no opening brace");


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
                return (null, $"Could not find closing brace for {label} '{targetName}'");

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

    private static string DetectIndentUnit(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) { return "    "; }

        var lines = source.Split('\n');
        foreach (var line in lines)
        {
            if (line.Length > 0 && line[0] == '\t') { return "\t"; }
        }

        return new string(' ', AgentUtilities.DetectIndentWidth(source));
    }
    private static string AutoIndentCode(string oldSource, string newCode, string? filePath = null)
    {
        var oldLines = oldSource.Split('\n');
        var firstRealLine = oldLines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (firstRealLine == null) return newCode;

        var baseIndent = Regex.Match(firstRealLine, @"^(\s*)").Value;
        if (string.IsNullOrEmpty(baseIndent)) return newCode;
        var baseIndentLen = baseIndent.Length;

        var newLines = newCode.Split('\n');
        if (newLines.Length <= 1) return newCode;

        var nonEmpty = newLines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (nonEmpty.Count == 0) return newCode;
        var minNewIndent = nonEmpty.Min(l => Regex.Match(l, @"^(\s*)").Groups[1].Length);

        if (minNewIndent >= baseIndentLen) return newCode;

        var result = new List<string>();
        foreach (var line in newLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                result.Add(line);
            }
            else
            {
                var trimmed = line.Length > minNewIndent
                    ? line.Substring(minNewIndent)
                    : line.TrimStart();
                result.Add(baseIndent + trimmed);
            }
        }
        var shifted = string.Join("\n", result);

        var shiftedLines = shifted.Split('\n');
        var distinctIndents = shiftedLines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => Regex.Match(l, @"^(\s*)").Groups[1].Length)
            .Distinct()
            .ToList();

        if (distinctIndents.Count <= 1
            && !AgentUtilities.IsWhitespaceSignificant(filePath))
        {
            var ext = Path.GetExtension(filePath ?? "").ToLowerInvariant();

            if (ext is ".html" or ".htm" or ".cshtml" or ".razor")
            {
                return ReindentHtmlTags(newCode, baseIndent, DetectIndentUnit(oldSource));
            }

            var reindented = AgentUtilities.ReindentByBraceDepth(newCode, baseIndent, DetectIndentUnit(oldSource));

            if (ext is ".ts" or ".tsx" or ".js" or ".jsx" or ".cs" or ".java" or ".go" or ".kt" or ".php" or ".rb")
            {
                reindented = AgentUtilities.FixMultilineParenIndentation(reindented);
            }
            return reindented;
        }

        var ext2 = Path.GetExtension(filePath ?? "").ToLowerInvariant();
        if (ext2 is ".ts" or ".tsx" or ".js" or ".jsx" or ".cs" or ".java" or ".go" or ".kt" or ".php" or ".rb")
        {
            shifted = AgentUtilities.FixMultilineParenIndentation(shifted);
        }

        return shifted;
    }
    private static string ReindentHtmlTags(string code, string baseIndent, string indentUnit = "  ")
    {
        var lines = code.Split('\n');
        var result = new List<string>();
        var depth = 0;
        var inTag = false;
        var voidElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr" };

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                result.Add(line);
                continue;
            }

            int lineDepthChange = 0;
            bool startsWithClosing = trimmed.StartsWith("</");
            int i = 0;

            while (i < trimmed.Length)
            {
                if (inTag)
                {
                    var closeIdx = trimmed.IndexOf('>', i);
                    if (closeIdx >= 0)
                    {
                        inTag = false;
                        i = closeIdx + 1;
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    var openIdx = trimmed.IndexOf('<', i);
                    if (openIdx >= 0)
                    {
                        if (openIdx + 1 < trimmed.Length && trimmed[openIdx + 1] == '/')
                        {
                            lineDepthChange--;
                            var closeIdx = trimmed.IndexOf('>', openIdx);
                            if (closeIdx >= 0) i = closeIdx + 1;
                            else { inTag = true; break; }
                        }
                        else
                        {
                            var closeIdx = trimmed.IndexOf('>', openIdx);
                            if (closeIdx >= 0)
                            {
                                if (trimmed[closeIdx - 1] != '/')
                                {
                                    var tagContent = trimmed.Substring(openIdx + 1, closeIdx - openIdx - 1);
                                    var tagName = tagContent.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.TrimEnd('/');
                                    if (tagName != null && !voidElements.Contains(tagName))
                                    {
                                        lineDepthChange++;
                                    }
                                }
                                i = closeIdx + 1;
                            }
                            else
                            {
                                inTag = true;
                                break;
                            }
                        }
                    }
                    else break;
                }
            }

            if (startsWithClosing && lineDepthChange < 0)
            {
                depth = Math.Max(0, depth - 1);
                lineDepthChange++;
            }

            var indent = baseIndent + string.Concat(Enumerable.Repeat(indentUnit, depth));
            result.Add(indent + trimmed);

            depth = Math.Max(0, depth + lineDepthChange);
        }

        return string.Join("\n", result);
    }


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
          string? filteredEditKnowledge = null)
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
        if (step.LineNumber > 0)
            sb.AppendLine($"TARGET LINE: {step.LineNumber}");
        if (!string.IsNullOrWhiteSpace(preservationDirective))
        {
            sb.AppendLine();
            sb.AppendLine("🛡️ MANDATORY PRESERVATION DIRECTIVE (from Sub-Agent Analysis)");
            sb.AppendLine(preservationDirective);
            sb.AppendLine("⚠ You MUST adhere to this directive. Do NOT invent new logic if the directive tells you to reuse existing patterns. Your edit will be rejected if you break these constraints.");
            sb.AppendLine();
        }
        sb.AppendLine("⚠ RULE: REPLACE existing code — do NOT add new alongside existing. " +
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
        var (langFamily, langSupportsFormatC, langHint) = AgentUtilities.GetLanguageProfile(ext);
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
        var isLarge = fileContent.Length > 3000 || lineCount > 80;
        if (isLarge)
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
            sb.AppendLine("⚠ TS/JS FILE: preserve ALL indentation exactly — " +
                          "methods inside a class body MUST be indented, nested blocks " +
                          "must be indented relative to their parent. Copy the leading " +
                          "whitespace from oldString character-for-character into newString.");
        var changeLowerForFormat = (step.Change ?? "").ToLowerInvariant();
        if (ext == ".cs")
        {
            var isNewCsMethod = fileExists &&
                Regex.IsMatch(changeLowerForFormat, @"\b(add|create|implement|define|insert|new)\b.{0,60}\b(method|endpoint|action|route)\b");
            var isHttpEndpoint = (step.Change ?? "").Contains("[Http", StringComparison.OrdinalIgnoreCase);
            var isDeletion = changeLowerForFormat.Contains("remove") || changeLowerForFormat.Contains("delete");
            var isClassFillHint = !isNewCsMethod && Regex.IsMatch(changeLowerForFormat, @"\bclass\b") &&
                (Regex.IsMatch(changeLowerForFormat, @"\bfill\s+in\b") || Regex.IsMatch(changeLowerForFormat, @"\bpopulate\b") ||
                 (Regex.IsMatch(changeLowerForFormat, @"\bpropert") && changeLowerForFormat.Count(c => c == ',') >= 2));
            if (isClassFillHint)
            {
                sb.AppendLine("⚠ EDIT FORMAT: FORMAT C (targetType=\"class\", NO insertAfter) — filling an existing class with new properties.");
                sb.AppendLine("  Set targetType=\"class\", targetName to the EXISTING class name, and newCode to ONLY the new property lines.");
                sb.AppendLine("  Do NOT repeat the class declaration/braces. Do NOT touch any other method in the file. Do NOT use oldString/newString for this.");
            }
            else if (isNewCsMethod || isHttpEndpoint)
            {
                sb.AppendLine("⚠ EDIT FORMAT: FORMAT C (insertAfter) — adding a new C# method/endpoint.");
                sb.AppendLine("  You MUST use targetType=\"method\", targetName=existing method, insertAfter=true, newCode=complete method.");
                sb.AppendLine("  Preserve existing attributes, return type, name, and parameters verbatim in newCode.");
                sb.AppendLine("  Do NOT use oldString/newString or fullFile — ONLY FORMAT C with insertAfter:true.");
            }
            else if (isDeletion)
            {
                sb.AppendLine("⚠ EDIT FORMAT: oldString/newString (deletion) — removing code.");
                sb.AppendLine("  oldString = exact lines to delete (1-5 max). newString = empty.");
                sb.AppendLine("  Do NOT include surrounding container lines — delete ONLY what's asked.");
            }
            else
            {
                sb.AppendLine("⚠ EDIT FORMAT: oldString/newString (targeted edit) — modifying existing code.");
                sb.AppendLine("  Copy 2-3 lines verbatim from the file as oldString.");
                sb.AppendLine("  Include the line above and below your change as anchor context, repeating them unchanged in newString.");
                sb.AppendLine("  SQL STRINGS: Preserve exact whitespace. 'INTERVAL 15 MINUTE' is correct, 'INTERVAL15MINUTE' is wrong.");
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
            var distilled = AgentUtilities.DistillExplorationContext(
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
                sb.AppendLine(AgentUtilities.ExtractRelevantExcerpt(fileContent, step.Change ?? "", step.OldString, cfg5.fileBodyTruncationChars));
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine($"For CODE files ({string.Join(", ", new[] { ".cs", ".ts", ".js", ".java", ".go", ".rs", ".swift", ".kt", ".php", ".rb" })}): "
                    + "use FORMAT C (targetType/targetName/newCode) for replacing ENTIRE methods. "
                    + "For SMALL changes (1-5 lines), use oldString/newString even for code files — copy lines verbatim from the excerpt above. "
                    + "NEVER rewrite inline SQL queries — preserve them exactly as-is.");
                sb.AppendLine("For ALL other file types: use oldString/newString — FORMAT C does not apply.");
            }
            else
            {
                sb.AppendLine("CURRENT FILE CONTENT:");
                sb.AppendLine("```");
                sb.AppendLine(fileContent);
                sb.AppendLine("```");
            }
        }

        sb.AppendLine();
        sb.AppendLine("STRICT oldString SIZE LIMIT: MAXIMUM 10 lines. If you output more than 10 lines in oldString, the edit WILL fail.");
        sb.AppendLine("SMALL targeted edits (1-5 lines, e.g. add a column to SQL, add one property): PREFER oldString/newString. " +
                      "Include the line above/below for anchor context, repeat them unchanged in newString.");
        sb.AppendLine("For FULL method/class replacements (entire method body rewrite): use FORMAT C (targetType/targetName/newCode) " +
                      "with unchanged signature and preserve all inline SQL verbatim.");
        sb.AppendLine("For HTML, CSS, JSON, and other markup/data files: use oldString/newString — those files don't have methods/classes for FORMAT C.");
        sb.AppendLine("To ADD a new method/CONSTRUCTOR: use insertAfter:true with targetType=\"method\" and targetName of an existing method.");
        sb.AppendLine("To REPLACE a method: use FORMAT C (targetType=\"method\", targetName=\"MethodName\") without insertAfter. " +
                      "PRESERVE the existing attributes, return type, name, and parameters verbatim in newCode. " +
                      "PRESERVE all existing inline SQL queries verbatim — never rewrite them.");
        sb.AppendLine("To ADD a PROPERTY/FIELD: NEVER use targetType=\"class\". Instead, use oldString/newString. " +
                      "Set oldString to the LAST 1-2 EXISTING property/method declarations at the end of the class body " +
                      "(copy them VERBATIM from the file), and set newString to those lines followed by your new line(s). " +
                      "Example: if adding `foo: string` and the last existing line before the closing `}` is `bar: number`, " +
                      "oldString = the line containing `bar` (with exact indentation), newString = that line + newline + your new `foo` line.");
        sb.AppendLine("To INSERT a new HTML element/block (or move an existing one): oldString MUST be the 1-2 lines IMMEDIATELY BEFORE the insertion point. " +
                      "newString MUST be those exact same anchor lines PLUS the new element(s) appended after them. " +
                      "CRITICAL: The new element MUST NOT be present in oldString. If oldString and newString are identical, the edit is a no-op and will fail.");
        sb.AppendLine("To REPLACE an entire class: use FORMAT C (targetType=\"class\", targetName=\"ClassName\") with newCode containing the FULL class declaration.");
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
                if (h.error.Contains("IDENTICAL to the existing code", StringComparison.OrdinalIgnoreCase) ||
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
                            var returnLine = AgentUtilities.FindLastReturnLine(priorSqlError.old);
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
                    sb.AppendLine("  Set targetType=\"method\", targetName to an EXISTING method name, insertAfter=true, and newCode to your new method.");
                }
                else if (!string.IsNullOrWhiteSpace(h.old))
                {
                    sb.AppendLine($"  Your oldString was:");
                    sb.AppendLine($"  ```");
                    sb.AppendLine($"  {h.old[..Math.Min(400, h.old.Length)]}");
                    sb.AppendLine($"  ```");
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
                        var hint = BuildExactMatchHint(fileContent, h.old);
                        if (hint != null)
                        {
                            sb.AppendLine($"  These lines in the file are SIMILAR to what you wrote:");
                            sb.AppendLine($"  {hint}");
                        }
                    }
                    var targetSectionHint = AgentUtilities.ExtractVerbatimTargetSection(
                        fileContent, step.Change ?? "", 10, centerLine: step.LineNumber > 0 ? step.LineNumber : 0);
                    if (!string.IsNullOrWhiteSpace(targetSectionHint))
                    {
                        sb.AppendLine();
                        sb.AppendLine("  ⚡ ACTUAL TARGET SECTION (verbatim lines near your intended edit):");
                        sb.AppendLine("  Pick the MOST UNIQUE single line below as your ENTIRE oldString — copy it character-for-character:");
                        sb.AppendLine("  ```");
                        foreach (var tLine in targetSectionHint.Split('\n'))
                            sb.AppendLine($"  {tLine}");
                        sb.AppendLine("  ```");
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

        if (history?.Count > 0)
        {
            sb.AppendLine("⚠ ESCALATION DIRECTIVE — your previous attempt(s) failed. You MUST change approach:");
            if (history.Count == 1)
            {
                sb.AppendLine("  STRATEGY: VERBATIM_COPY.");
                sb.AppendLine("  • Do NOT retype oldString from memory — the file content above is authoritative.");
                sb.AppendLine("  • Open the FILE CONTENT block, find the EXACT lines you want to replace, and");
                sb.AppendLine("    copy them character-for-character into oldString. Include every space, comma,");
                sb.AppendLine("    and indentation character. The DIFF hints above show what you got wrong.");
                sb.AppendLine("  • If the file shows 'rgba(255, 255, 255, 0.03)' (with spaces), your oldString MUST");
                sb.AppendLine("    contain 'rgba(255, 255, 255, 0.03)' — NOT 'rgba(255,255,255,0.03)'.");
            }
            else if (history.Count == 2)
            {
                sb.AppendLine("  STRATEGY: SINGLE_LINE_ANCHOR.");
                sb.AppendLine("  • Drop your multi-line oldString. Pick the SINGLE most distinctive line in the");
                sb.AppendLine("    target region (longest line with the most unique tokens) as your oldString.");
                sb.AppendLine("  • Add ONE line above OR below for anchor context — no more.");
                sb.AppendLine("  • Example: if you want to add a flex-wrap property to a .kanban-board rule,");
                sb.AppendLine("    use `  display: flex;` as oldString and `  display: flex;\\n  flex-wrap: wrap;` as newString.");
                sb.AppendLine("  • DO NOT include the entire rule block — that's what failed last time.");
            }
            else
            {
                if (ext is ".html" or ".htm" or ".cshtml" or ".razor" or ".vue" or ".svelte")
                {
                    sb.AppendLine("  STRATEGY: HTML_PINPOINT — fullFile is BLOCKED for HTML/Angular templates.");
                    sb.AppendLine("  The LLM generates wrong component structure when given fullFile for Angular. Instead:");
                    sb.AppendLine("  1. Look at the TARGET SECTION shown in the history above.");
                    sb.AppendLine("  2. Pick the SINGLE most unique line there (longest, appears only ONCE in the whole file).");
                    sb.AppendLine("  3. Use that one line VERBATIM as your entire oldString (≥20 chars).");
                    sb.AppendLine("  4. In newString: include that unchanged line, then add your new elements around it.");
                    sb.AppendLine("  ⚠ Do NOT use <div class=\"popupPanelActions\"> alone — it appears multiple times. Use a more specific line.");
                    sb.AppendLine("  ⚠ Do NOT output fullFile — it will be rejected.");
                    var targetSectionForEscalation = AgentUtilities.ExtractVerbatimTargetSection(
                        fileContent, step.Change ?? "", 8, centerLine: step.LineNumber > 0 ? step.LineNumber : 0);
                    if (targetSectionForEscalation != null)
                    {
                        sb.AppendLine();
                        sb.AppendLine("  TARGET SECTION — pick the single most unique line here as your ENTIRE oldString:");
                        sb.AppendLine("  ```html");
                        sb.AppendLine(targetSectionForEscalation);
                        sb.AppendLine("  ```");
                    }
                }
                else
                {
                    var ch = (step.Change ?? "").ToLowerInvariant();
                    var isNewCsMethod = ext == ".cs" &&
                        (ch.Contains("add") || ch.Contains("create") || ch.Contains("insert") || ch.Contains("new") || ch.Contains("define") || ch.Contains("implement")) &&
                        (ch.Contains("method") || ch.Contains("endpoint") || ch.Contains("action") || ch.Contains("route") ||
                         (step.Change ?? "").Contains("[Http", StringComparison.OrdinalIgnoreCase));

                    if (isNewCsMethod)
                    {
                        sb.AppendLine("  STRATEGY: FORMAT_C_INSERTION.");
                        sb.AppendLine("  • You MUST use FORMAT C with insertAfter:true.");
                        sb.AppendLine("  • Set targetType=\"method\", targetName to an EXISTING method name");
                        sb.AppendLine("    (e.g. the LAST method in the class), insertAfter=true, and newCode");
                        sb.AppendLine("    to the COMPLETE new method including [HttpPost] attribute, signature, and body.");
                        sb.AppendLine("  • Do NOT use fullFile and do NOT return alreadyDone — both will be rejected.");
                    }
                    else
                    {
                        sb.AppendLine("  STRATEGY: LINE_RANGE_REPLACEMENT.");
                        sb.AppendLine("  • Your oldString/newString approach has failed 3+ times. SWITCH FORMATS.");
                        sb.AppendLine("  • Look at the FILE CONTENT block. Identify the line numbers of the region to replace.");
                        sb.AppendLine("  • Output a JSON object with this exact shape:");
                        sb.AppendLine("    {");
                        sb.AppendLine("      \"fullFile\": [\"...entire file content with your changes applied...\"]");
                        sb.AppendLine("    }");
                        sb.AppendLine("  • The fullFile MUST contain EVERY line of the file, with your changes applied.");
                        sb.AppendLine("    Do NOT omit any lines — this is a full-file replacement.");
                        sb.AppendLine("  • This bypasses oldString matching entirely, so it cannot fail on whitespace.");
                    }
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine();
        var changeLower = (step.Change ?? "").ToLowerInvariant();
        var isActualDeletion = changeLower.Contains("remove") ||
            (changeLower.Contains("delete") && !Regex.IsMatch(changeLower, @"\b(add|create|insert|implement)\b"));
        if (isActualDeletion)
        {
            sb.AppendLine("⚠ CRITICAL DELETION INSTRUCTION: You are deleting code. Your oldString MUST be EXACTLY the 1-5 lines of code being deleted. Set newString to an empty array []. Do NOT include the parent <div> or any surrounding lines in oldString. Output ONLY the exact lines to delete.");
        }
        else if (ext == ".cs" && (
            Regex.IsMatch(changeLower, @"\b(add|create|insert|new|define|implement)\b.{0,60}\b(method|endpoint|action|route)\b") ||
            Regex.IsMatch(step.Change ?? "", @"\[Http(Get|Post|Put|Delete|Patch)\]") ||
            Regex.IsMatch(step.Change ?? "", @"\b(add|create|implement|replace|define|modify|change|update)\s+(the\s+)?[A-Z]\w+\s+method\b", RegexOptions.IgnoreCase)))
        {
            sb.AppendLine("⚠ CRITICAL — .cs NEW METHOD CREATION: You MUST use FORMAT C with insertAfter:true.");
            sb.AppendLine("  Set targetType=\"method\", targetName to an EXISTING method name that already exists");
            sb.AppendLine("  in the file (e.g. the LAST method in the class, like \"SaveSettings\"),");
            sb.AppendLine("  insertAfter=true, and newCode to the COMPLETE new method including [HttpPost] attribute,");
            sb.AppendLine("  signature, and body. Do NOT use oldString/newString — it will be rejected.");
            sb.AppendLine("  Do NOT use fullFile — it will also be rejected because it dumps the entire file.");
            sb.AppendLine("  Do NOT return alreadyDone — the method does not exist yet.");
            sb.AppendLine("  ONLY FORMAT C with insertAfter:true will be accepted.");
            sb.AppendLine("  Example: {\"targetType\": \"method\", \"targetName\": \"SaveSettings\", \"insertAfter\": true, \"newCode\": [...]}");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(fileContent))
        {
            var verbatimSection = AgentUtilities.ExtractVerbatimTargetSection(
                fileContent, step.Change ?? "", contextLines: 10, centerLine: step.LineNumber > 0 ? step.LineNumber : 0);
            if (!string.IsNullOrWhiteSpace(verbatimSection))
            {
                sb.AppendLine("⚡ ACTUAL TARGET SECTION (copy oldString character-for-character from here):");
                sb.AppendLine("```");
                sb.AppendLine(verbatimSection);
                sb.AppendLine("```");
                sb.AppendLine("Your oldString MUST be copied character-for-character from one or more lines in this section.");
                sb.AppendLine();
            }
        }

        sb.AppendLine();
        sb.AppendLine("Output the edit now:");
        if (emitSse)
            await SendSse(Response, "edit-resolve", new { }, ct);

        var classIsNewCsMethod = ext == ".cs" && fileExists && (
          Regex.IsMatch(changeLower, @"\b(add|create|implement|define|insert|new)\b.{0,60}\b(method|endpoint|action|route)\b") ||
          (step.Change ?? "").Contains("[Http", StringComparison.OrdinalIgnoreCase));

        var isClassPropertyFill = ext == ".cs" && fileExists && !classIsNewCsMethod &&
            Regex.IsMatch(changeLower, @"\bclass\b") &&
            (Regex.IsMatch(changeLower, @"\bfill\s+in\b") ||
             Regex.IsMatch(changeLower, @"\bpopulate\b") ||
             Regex.IsMatch(changeLower, @"\ball\s+(?:the\s+)?(?:specified|required|listed)\s+propert") ||
             (Regex.IsMatch(changeLower, @"\bpropert") && changeLower.Count(c => c == ',') >= 2));

        string editFormat;
        if (!fileExists)
        {
            editFormat = "full_file";
        }
        else if (classIsNewCsMethod)
        {
            editFormat = "format_c_insert";
        }
        else if (isClassPropertyFill)
        {
            editFormat = "format_c_class_fill";
        }
        else if (changeLower.Contains("remove") ||
            (changeLower.Contains("delete") && !Regex.IsMatch(changeLower, @"\b(add|create|insert|implement)\b")))
        {
            editFormat = "delete";
        }
        else
        {
            editFormat = "old_new";
        }

        var systemPrompt = BuildEditSystemPrompt(editFormat);
        var (raw, _, resolveError2) = await CallLlmRawStreaming(systemPrompt, sb.ToString(), emitSse, ct, _infiniteTimeout, maxTokens: 1536);

        if (!string.IsNullOrWhiteSpace(resolveError2) && resolveError2.Contains("Repetition loop detected", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, false, null, false, resolveError2, false);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, null, false, null, false, "LLM returned empty response", false);
        }

        if (raw.Length > 6000 || raw.Split('\n').Length > 80)
        {
            return (null, null, false, null, false,
                "LLM response was too long (" + raw.Length + " chars) and likely truncated due to a repetition loop. " +
                "Do NOT output massive blocks, do NOT use fullFile, and do NOT return alreadyDone. " +
                "Use FORMAT C with insertAfter:true and newCode as an array of lines — this is the ONLY accepted format.", false);
        }


        string? oldStr = null, newStr = null;

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
            {
                cleaned = cleaned[fb..(lb + 1)];
            }
            cleaned = RepairJsonNewlines(cleaned);

            using var jDoc = JsonDocument.Parse(cleaned);
            var jRoot = jDoc.RootElement;
            if (jRoot.TryGetProperty("alreadyDone", out var ad) && ad.GetBoolean())
            {
                var (verdict, _) = PreEditValidation(fileContent, step);
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

                var keywords = Regex.Matches(changeLower, @"\b[a-z]{4,}\b")
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



            if (ext == ".cs" && classIsNewCsMethod &&
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
                var isNewCsMethod = ext == ".cs" && classIsNewCsMethod;

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
                            lines.Add(AgentUtilities.UnescapeString(item.GetString() ?? ""));
                    }
                    if (lines.Count > 0) body = string.Join("\n", lines);
                }
                if (!string.IsNullOrWhiteSpace(body))
                {
                    body = StripFullFileFence(body);
                    body = AgentUtilities.AutoFixPythonStatements(body, relPath);
                    body = AgentUtilities.CleanVerbatimStringEscapes(body);

                    return (null, null, true, body, false, null, false);
                }
            }

            if (jRoot.TryGetProperty("targetType", out var ttEl) &&
                jRoot.TryGetProperty("targetName", out var tnEl) &&
                jRoot.TryGetProperty("newCode", out var ncEl))
            {
                var targetType = ttEl.GetString();
                var targetName = tnEl.GetString();
                var newCodeStr = ncEl.ValueKind == JsonValueKind.String
                        ? AgentUtilities.UnescapeString(ncEl.GetString() ?? "")
                    : ncEl.ValueKind == JsonValueKind.Array
                        ? string.Join("\n", ncEl.EnumerateArray().Select(e => AgentUtilities.UnescapeString(e.GetString() ?? "")))
                        : null;

                if (!string.IsNullOrWhiteSpace(targetType) && !string.IsNullOrWhiteSpace(targetName) && newCodeStr != null)
                {
                    newCodeStr = AgentUtilities.AutoFixPythonStatements(newCodeStr, relPath);
                    newCodeStr = AgentUtilities.CleanVerbatimStringEscapes(newCodeStr);

                    var insertAfter = jRoot.TryGetProperty("insertAfter", out var iaEl) && iaEl.GetBoolean();

                    if (insertAfter)
                    {
                        var (fullStr, astErr) = AstResolveEdit(fullPath, targetType, targetName, returnTail: false);
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
                                (fullStr, astErr) = AstResolveEdit(fullPath, targetType, lastMethodName, returnTail: false);
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
                            var unit = DetectIndentUnit(fullStr);
                            var memberIndent = unit + unit;
                            var hasClassDecl = newCodeStr.Contains("class ", StringComparison.OrdinalIgnoreCase);
                            var body = hasClassDecl ? AgentUtilities.StripClassWrapper(newCodeStr) : newCodeStr;

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
                        var indented = AutoIndentCode(fullStr, newCodeStr, relPath);
                        newStr = fullStr + "\n" + indented;
                        return (fullStr, newStr, false, null, false, null, true);
                    }
                    else
                    {
                        var addMethodMatch = Regex.Match(step.Change ?? "", @"Add\s+(?:a\s+)?(?:new\s+)?method\s+(\w+)", RegexOptions.IgnoreCase);
                        if (addMethodMatch.Success)
                        {
                            var requestedMethodName = addMethodMatch.Groups[1].Value;
                            if (!string.IsNullOrWhiteSpace(requestedMethodName) &&
                                !string.Equals(targetName, requestedMethodName, StringComparison.OrdinalIgnoreCase) &&
                                !newCodeStr.Contains(requestedMethodName, StringComparison.OrdinalIgnoreCase))
                            {
                                return (null, null, false, null, false,
                                    $"WRONG METHOD MODIFIED — Step asks to add '{requestedMethodName}' but you targeted '{targetName}' and did not include '{requestedMethodName}' in newCode. " +
                                    "Use insertAfter:true with an existing method, and provide ONLY the new method in newCode.", false);
                            }
                        }

                        var (astOldStr, astErr) = AstResolveEdit(fullPath, targetType, targetName, returnTail: false);
                        if (astOldStr != null)
                        {
                            var isClassTarget = string.Equals(targetType, "class", StringComparison.OrdinalIgnoreCase);
                            var hasClassDecl = newCodeStr.Contains("class ", StringComparison.OrdinalIgnoreCase);
                            if (isClassTarget && !hasClassDecl)
                            {
                                var lastBrace = astOldStr.LastIndexOf('}');
                                if (lastBrace >= 0)
                                {
                                    var unit = DetectIndentUnit(astOldStr);
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

                                var body = hasClassDecl ? AgentUtilities.StripClassWrapper(newCodeStr) : newCodeStr;
                                if (!string.IsNullOrWhiteSpace(body))
                                {
                                    var unit = DetectIndentUnit(astOldStr);
                                    var bodyIndented = AgentUtilities.ReindentToLevel(body, unit);
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
                                var newMethodMatch = MethodDeclRegex.Match(newCodeStr);
                                if (newMethodMatch.Success)
                                {
                                    var newMethodName = newMethodMatch.Groups[1].Value;
                                    if (!string.IsNullOrWhiteSpace(newMethodName) &&
                                        !string.Equals(newMethodName, targetName, StringComparison.Ordinal))
                                    {
                                        var oldFirstRealLine = astOldStr.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                                        var methodBaseIndent = oldFirstRealLine != null
                                            ? Regex.Match(oldFirstRealLine, @"^(\s*)").Groups[1].Value
                                            : "";

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
                            var indented = AutoIndentCode(astOldStr, fmtNewCode, relPath);
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

                if (!string.IsNullOrWhiteSpace(newStr))
                {
                    var cleanedNewStr = AgentUtilities.CleanVerbatimStringEscapes(newStr);
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
                                        var indentedBody = AutoIndentCode(astOldStr, insertBody, relPath);
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


                if (!string.IsNullOrWhiteSpace(oldStr) && oldStr.Split('\n').Length > 15)
                {
                    return (null, null, false, null, false,
                        $"oldString is {oldStr.Split('\n').Length} lines long — STRICT MAXIMUM IS 10 LINES. " +
                        "You outputted a massive block which caused token exhaustion/truncation. " +
                        "Use a 1-3 line anchor targeting the exact line to change.", false);
                }

                newStr = AgentUtilities.AutoFixPythonStatements(newStr ?? "", relPath);

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

            if (!string.IsNullOrWhiteSpace(oldStr)) { return (oldStr, newStr ?? "", false, null, false, null, false); }


            return (null, null, false, null, false, "JSON has no oldString, targetType, fullFile, or alreadyDone field", false);
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
                    newStr = AgentUtilities.AutoFixPythonStatements(newStr, relPath);

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
                @"""targetName""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
            if (ttMatch.Success && tnMatch.Success)
            {
                var tt = ttMatch.Groups[1].Value;
                var tn = tnMatch.Groups[1].Value;
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
                            else if (afterKey[i] == ']') { depth--; if (depth == 0) { var lines = ExtractQuotedStrings(afterKey[1..i]); if (lines.Count > 0) newCodeStr = string.Join("\n", lines); break; } }
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
                    }

                    if (!string.IsNullOrWhiteSpace(tt) && !string.IsNullOrWhiteSpace(tn) && newCodeStr != null)
                    {
                        var insertAfter = Regex.Match(raw, @"""insertAfter""\s*:\s*true", RegexOptions.IgnoreCase).Success;
                        if (insertAfter)
                        {
                            var (fullStr, astErr) = AstResolveEdit(fullPath, tt, tn, returnTail: false);
                            if (fullStr != null) { var indented = AutoIndentCode(fullStr, newCodeStr, relPath); newStr = fullStr + "\n" + indented; return (fullStr, newStr, false, null, false, null, true); }
                        }
                        else
                        {
                            var (astOldStr, astErr) = AstResolveEdit(fullPath, tt, tn, returnTail: false);
                            if (astOldStr != null) { var indented = AutoIndentCode(astOldStr, newCodeStr, relPath); return (astOldStr, indented, false, null, false, null, true); }
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
            newStr = AgentUtilities.AutoFixPythonStatements(newStr, relPath);

            if (string.IsNullOrWhiteSpace(oldStr))
                return (null, null, false, null, false, "OLD section is empty", false);

            return (oldStr, newStr, false, null, false, null, false);

        }
    }

    private enum PreEditVerdict { Proceed, AlreadyDone, Irrelevant }
    private static readonly string[] _verifyPrefixes = {
        "ensure", "verify", "make sure", "confirm", "validate",
        "check", "guarantee", "see if", "determine if", "review"
    };

    private static (PreEditVerdict verdict, string reason) PreEditValidation(string fileContent, PlanStep step)
    {
        if (string.IsNullOrWhiteSpace(fileContent))
            return (PreEditVerdict.Proceed, "");

        var content = AgentUtilities.NormalizeLineEndings(fileContent);
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
            var jsMethodName = AgentUtilities.ExtractJsMethodNameFromChange(step.Change ?? "");
            if (!string.IsNullOrWhiteSpace(jsMethodName) &&
                jsMethodName.Length >= 2 &&
                !jsMethodName.Equals("function", StringComparison.OrdinalIgnoreCase) &&
                !jsMethodName.Equals("method", StringComparison.OrdinalIgnoreCase) &&
                !jsMethodName.Equals("handler", StringComparison.OrdinalIgnoreCase))
            {
                if (AgentUtilities.JsMethodExistsInContent(content, jsMethodName))
                {
                    return (PreEditVerdict.AlreadyDone,
                        $"JavaScript method '{jsMethodName}' already exists in {step.File}");
                }

                if (!string.IsNullOrWhiteSpace(step.NewString))
                {
                    var newName = AgentUtilities.ExtractJsMethodNameFromCode(step.NewString);
                    if (!string.IsNullOrWhiteSpace(newName) &&
                        !string.Equals(newName, jsMethodName, StringComparison.Ordinal) &&
                        AgentUtilities.JsMethodExistsInContent(content, newName))
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
            if (!string.IsNullOrWhiteSpace(step.OldString))
            {
                var oldStr = AgentUtilities.NormalizeLineEndings(step.OldString);
                if (!content.Contains(oldStr, StringComparison.Ordinal))
                {
                    var trimOld = string.Join("\n", oldStr.Split('\n').Select(l => l.TrimEnd()));
                    var trimFile = string.Join("\n", content.Split('\n').Select(l => l.TrimEnd()));
                    if (!trimFile.Contains(trimOld, StringComparison.Ordinal))
                        return (PreEditVerdict.AlreadyDone, "code to be removed is already absent from file");
                }
            }
            else if (!string.IsNullOrWhiteSpace(step.Change))
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
                    !content.Contains(codeToRemove, StringComparison.Ordinal))
                    return (PreEditVerdict.AlreadyDone, "code to be removed is already absent from file");
            }
        }
        if (changeLower.StartsWith("move ") || changeLower.StartsWith("insert "))
        {
            if (!string.IsNullOrWhiteSpace(step.NewString))
            {
                var newStr = AgentUtilities.NormalizeLineEndings(step.NewString);
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
            var newStr = AgentUtilities.NormalizeLineEndings(step.NewString);
            if (content.Contains(newStr, StringComparison.Ordinal))
                return (PreEditVerdict.AlreadyDone, "code already present in file");

            var collapsedNew = CollapseWhitespace(newStr);
            if (collapsedNew.Length >= 15 &&
                CollapseWhitespace(content).Contains(collapsedNew, StringComparison.Ordinal))
                return (PreEditVerdict.AlreadyDone, "code already present in file (whitespace differences only)");
        }

        if (string.IsNullOrWhiteSpace(step.NewString) && !string.IsNullOrWhiteSpace(step.OldString))
        {
            var changeLower2 = (step.Change ?? "").Trim().ToLowerInvariant();
            if (_verifyPrefixes.Any(p => changeLower2.StartsWith(p)))
            {
                var oldStr = AgentUtilities.NormalizeLineEndings(step.OldString);
                if (content.Contains(oldStr, StringComparison.Ordinal))
                    return (PreEditVerdict.AlreadyDone, "step is verification-only — code already present");
            }
        }

        if (!string.IsNullOrWhiteSpace(step.OldString))
        {
            var oldStr = AgentUtilities.NormalizeLineEndings(step.OldString);
            if (!content.Contains(oldStr, StringComparison.Ordinal))
            {

                var trimOld = string.Join("\n", oldStr.Split('\n').Select(l => l.TrimEnd()));
                var trimFile = string.Join("\n", content.Split('\n').Select(l => l.TrimEnd()));
                if (!trimFile.Contains(trimOld, StringComparison.Ordinal))
                    return (PreEditVerdict.Irrelevant, "oldString not found — context changed or already applied");
            }
        }

        return (PreEditVerdict.Proceed, "");
    }

    private async Task<PlanAuditResult?> PlanPreAuditAsync(
     AgentPlan plan, string projectRoot, bool emitSse,
     CancellationToken ct, string? originalPrompt = null)
    {
        if (plan?.Plan == null || plan.Plan.Count == 0) return null;

        var auditSteps = new List<AuditPlanStepResult>();
        var sb = new StringBuilder();

        sb.AppendLine("You are auditing a code-change plan BEFORE execution. Your job: detect problems that would waste time or cause bugs.");
        sb.AppendLine();
        sb.AppendLine("For EACH step in the plan, examine the target file's content, the original prompt, and determine:");
        sb.AppendLine("1. alreadyDone = true/false — Is the proposed change ALREADY present in the file?");
        sb.AppendLine("2. needsDecoupling = true/false — Does this step combine TWO OR MORE distinct");
        sb.AppendLine("   changes at DIFFERENT LOCATIONS that should be separate steps?");
        sb.AppendLine();
        sb.AppendLine("   PATTERNS THAT NEED DECOUPLING:");
        sb.AppendLine("   a) Cross-file: \"Add property to X AND wire up in Y\" → 2 steps");
        sb.AppendLine("   b) Move: \"Move X from A to B\" → 2 steps (add at B, remove from A)");
        sb.AppendLine("   c) SAME-FILE MULTI-LOCATION: \"Add field AND initialize in constructor AND add method\"");
        sb.AppendLine("      → 3 steps (field decl, constructor init, method def)");
        sb.AppendLine("   d) MIRROR/COPY: If the step says 'mirror X' or 'copy pattern from X', it usually requires");
        sb.AppendLine("      modifying EXISTING elements (like a title bar) to emit events, AND adding the new UI structure.");
        sb.AppendLine("      This MUST be split into separate steps for each location (e.g., 1. Modify title bar, 2. Add panel).");
        sb.AppendLine("   e) WRAPPING: \"Wrap X in a container\" → 2 steps (open tag + close tag)");
        sb.AppendLine("   f) INSERT + WIRE: If a step says to insert/mirror/copy a UI component AND also");
        sb.AppendLine("      mentions wiring it up (event binding, click handler, attribute on a DIFFERENT");
        sb.AppendLine("      existing element), that is TWO steps: one to add the new structure, one to");
        sb.AppendLine("      modify the existing element to wire it up. Example:");
        sb.AppendLine("      \"Insert popup panel AND add menuClicked event binding to title bar\"");
        sb.AppendLine("      → 2 steps: 1. Add popup panel HTML, 2. Add menuClicked binding to title bar");
        sb.AppendLine("      Look for phrases like 'including event binding', 'with wiring', 'wire up',");
        sb.AppendLine("      'add X and connect Y', 'matching event syntax', 'including click handler'.");
        sb.AppendLine();
        sb.AppendLine("   Example: \"Add _fifteenMinuteTimer field and initialize it in the constructor,");
        sb.AppendLine("   then add a RunFifteenMinuteTasks method\"");
        sb.AppendLine("   needsDecoupling = true. decoupledSteps = [");
        sb.AppendLine("     { file: \"...\", change: \"Add _fifteenMinuteTimer field declaration after the last existing timer field\" },");
        sb.AppendLine("     { file: \"...\", change: \"Initialize _fifteenMinuteTimer in the constructor after existing timer initializations\" },");
        sb.AppendLine("     { file: \"...\", change: \"Add RunFifteenMinuteTasks method after the last existing RunXxxTasks method\" }");
        sb.AppendLine("   ]");
        sb.AppendLine();
        sb.AppendLine("   Example: \"Add isMenuPanelOpen property and showMenuPanel() and closeMenuPanel() methods\"");
        sb.AppendLine("   needsDecoupling = true. decoupledSteps = [");
        sb.AppendLine("     { file: \"...\", change: \"Add isMenuPanelOpen property declaration after the last existing property\" },");
        sb.AppendLine("     { file: \"...\", change: \"Add showMenuPanel() method after the last existing method\" },");
        sb.AppendLine("     { file: \"...\", change: \"Add closeMenuPanel() method after showMenuPanel()\" }");
        sb.AppendLine("   ]");
        sb.AppendLine();
        sb.AppendLine("   CRITICAL: Even within the SAME FILE, if a step requires changes at DIFFERENT");
        sb.AppendLine("   LOCATIONS (e.g., modify a title bar AND add a popup panel at the bottom), that is MULTIPLE distinct edits.");
        sb.AppendLine("   Combining them forces the editor into massive block replacements that fail. Each location needs its own step.");
        sb.AppendLine("   When a step says it will 'insert X and wire it up' or 'add Y including event binding on Z',");
        sb.AppendLine("   the wiring/event-binding is a SEPARATE location from the new structure — split it off.");
        sb.AppendLine();
        sb.AppendLine("   g) DECLARE + DISPLAY + POPULATE: If steps add a property/field that shows fetched data");
        sb.AppendLine("      (e.g., 'youtubeTotalResults') and display it in a template, but NO step assigns/populates");
        sb.AppendLine("      that property after the data fetch, GENERATE a missing step to wire it up.");
        sb.AppendLine("      Example: Step 1 adds 'youtubeTotalResults: number = 0;'. Step 2 adds '{{youtubeTotalResults}}'");
        sb.AppendLine("      to the template. The file has 'youtubeResults: YoutubeVideo[]' populated by a search method.");
        sb.AppendLine("      A step 3 is MISSING: 'Set youtubeTotalResults = youtubeResults.length in the search");
        sb.AppendLine("      method after the YouTube results are returned.' — add it to decoupledSteps.");
        sb.AppendLine("      Look for properties declared as counters/display variables with no assignment");
        sb.AppendLine("      in any method body, whose value can be derived from an existing data source.");
        sb.AppendLine();
        sb.AppendLine("   h) REJECT COMMENT/EXPLANATION STEPS: If a step says to 'add a comment block' or");
        sb.AppendLine("      'add an explanation' or 'add documentation' or 'create a script comment'");
        sb.AppendLine("      that is NOT functional code (no logic change, no new method, no new endpoint),");
        sb.AppendLine("      set alreadyDone = true and reason = 'Comment-only step — not a functional change.'");
        sb.AppendLine("      These steps waste execution time and confuse the editor. The verification");
        sb.AppendLine("      system already handles SQL table schema detection; table creation SQL belongs");
        sb.AppendLine("      INSIDE the method that uses the table, not as a standalone comment.");
        sb.AppendLine("      Example: 'Create BenchmarksTableCreation SQL comment block before GetVersion'");
        sb.AppendLine("      → alreadyDone = true.");
        sb.AppendLine();
        sb.AppendLine("   DO NOT decouple if the step is a single coherent edit in one location (e.g., 'Modify the CalculateTotal method to include tax').");
        sb.AppendLine("   DO NOT decouple filling in a class/record/struct with multiple properties or fields.");
        sb.AppendLine("   'Fill in UserDTO with Name, Email, Age' is ONE step — add all properties at once in the same location.");
        sb.AppendLine("   'Add properties Token, Date, Score to BenchmarkDataDTO' is ONE step, not 12 individual property steps.");
        sb.AppendLine("   Multiple properties grouped in the same step are intentionally one coherent change.");
        sb.AppendLine("   DO NOT decouple method route attributes from the method itself. In C#/Java/Python, route attributes");
        sb.AppendLine("   ([HttpGet], @app.get, @RequestMapping, etc.) ARE part of the method declaration at the same");
        sb.AppendLine("   location — they are NOT a separate 'endpoint registration'. 'Add GetBenchmarks method with [HttpGet]'");
        sb.AppendLine("   is ONE step, not two.");
        sb.AppendLine("   i) CREATE TABLE IS NOT A SEPARATE STEP: If a step mentions adding a method/endpoint that");
        sb.AppendLine("      inserts/updates data AND also mentions creating the table, that is ONE step.");
        sb.AppendLine("      The CREATE TABLE IF NOT EXISTS statement MUST be inline inside the method body,");
        sb.AppendLine("      BEFORE the INSERT/UPDATE/SELECT. Do NOT split the table creation into its own");
        sb.AppendLine("      step — they belong at the same location (inside the method body).");
        sb.AppendLine("      BAD: Step 1: \"Create Benchmarks table\", Step 2: \"Add PostBenchmarks method with INSERT\"");
        sb.AppendLine("      GOOD: \"Add PostBenchmarks method with inline CREATE TABLE IF NOT EXISTS and INSERT\"");
        sb.AppendLine();
        sb.AppendLine("SPECIAL RULE FOR REMOVAL/DELETE STEPS:");
        sb.AppendLine("  For steps that say 'Remove X' or 'Delete X':");
        sb.AppendLine("  - alreadyDone = true ONLY if X is NOT present in the file content shown below.");
        sb.AppendLine("  - If X IS present in the file (even once), alreadyDone MUST be false.");
        sb.AppendLine("  - Do NOT reason about 'whether it would be present after other steps run'.");
        sb.AppendLine("  - Check the ACTUAL file content shown above — search for the exact code pattern.");
        sb.AppendLine("  - If the file content is truncated or shows excerpts, do NOT assume the pattern is absent — set alreadyDone = false if you can't see the whole file.");
        sb.AppendLine();
        sb.AppendLine("Output ONLY valid JSON:");
        sb.AppendLine("{");
        sb.AppendLine("  \"steps\": [");
        sb.AppendLine("    {");
        sb.AppendLine("      \"index\": 0,");
        sb.AppendLine("      \"alreadyDone\": false,");
        sb.AppendLine("      \"needsDecoupling\": false,");
        sb.AppendLine("      \"reason\": \"\",");
        sb.AppendLine("      \"decoupledSteps\": []");
        sb.AppendLine("    }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(originalPrompt))
        {
            sb.AppendLine("### ORIGINAL TASK ###");
            sb.AppendLine(originalPrompt);
            sb.AppendLine();
        }

        for (var i = 0; i < plan.Plan.Count; i++)
        {
            var step = plan.Plan[i];
            sb.AppendLine($"--- STEP {i + 1} ---");
            sb.AppendLine($"File:   {step.File}");
            sb.AppendLine($"Change: {step.Change}");
            sb.AppendLine();

            if (AgentUtilities.IsRelativePath(step.File) && !AgentUtilities.IsSpecialMarker(step.File))
            {
                var relPath = step.File.Replace('\\', '/');
                var fullPath = Path.GetFullPath(
                    Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
                if (System.IO.File.Exists(fullPath) && AgentUtilities.IsPathUnderRoot(fullPath, projectRoot))
                {
                    var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                    var changeLower = (step.Change ?? "").ToLowerInvariant();

                    sb.AppendLine("TARGET FILE CONTENT:");
                    sb.AppendLine("```");
                    if (content.Length > 8000)
                    {
                        if (changeLower.Contains("remove") ||
                            (changeLower.Contains("delete") && !Regex.IsMatch(changeLower, @"\b(add|create|insert|implement)\b")))
                        {

                            var keywords = Regex.Matches(step.Change ?? "", @"[\w-]+")
                                .Select(m => m.Value)
                                .Where(w => w.Length > 4 && !new HashSet<string> { "remove", "delete" }.Contains(w.ToLowerInvariant()))
                                .Take(3).ToList();

                            var sb2 = new StringBuilder();
                            var lines = content.Split('\n');
                            for (var li = 0; li < lines.Length; li++)
                            {
                                if (keywords.Any(k => lines[li].Contains(k, StringComparison.OrdinalIgnoreCase)))
                                {
                                    var start = Math.Max(0, li - 2);
                                    var end = Math.Min(lines.Length - 1, li + 2);
                                    sb2.AppendLine($"--- around line {li + 1} ---");
                                    for (var j = start; j <= end; j++)
                                        sb2.AppendLine($"{j + 1}: {lines[j]}");
                                    sb2.AppendLine();
                                }
                            }
                            sb.AppendLine(sb2.ToString());
                            sb.AppendLine($"... (showing relevant excerpts, full file is {content.Length} chars)");
                        }
                        else
                        {
                            sb.AppendLine(content[..6000] + $"\n... (truncated, full file is {content.Length} chars)");
                        }
                    }
                    else
                    {
                        sb.AppendLine(content);
                    }
                    sb.AppendLine("```");


                    var addClassMatch = Regex.Match(step.Change ?? @"", @"(?:add|insert|create)\s+(?:a\s+)?(?:new\s+)?class\s+(\w+)", RegexOptions.IgnoreCase);
                    if (addClassMatch.Success)
                    {
                        var className = addClassMatch.Groups[1].Value;
                        if (Regex.IsMatch(content, $@"\bclass\s+{Regex.Escape(className)}\b"))
                        {
                            sb.AppendLine($"⚠ PRE-CHECK: Class '{className}' already exists in the file. Step {i + 1} may be alreadyDone.");
                        }
                    }
                    var addMethodMatch2 = Regex.Match(step.Change ?? @"", @"(?:add|insert|create)\s+(?:a\s+)?(?:new\s+)?method\s+(\w+)", RegexOptions.IgnoreCase);
                    if (addMethodMatch2.Success)
                    {
                        var methodName = addMethodMatch2.Groups[1].Value;
                        if (Regex.IsMatch(content, $@"\b{Regex.Escape(methodName)}\s*\("))
                        {
                            sb.AppendLine($"⚠ PRE-CHECK: Method '{methodName}' already exists in the file. Step {i + 1} may be alreadyDone.");
                        }
                    }
                }
                else
                {
                    sb.AppendLine("(file does not exist yet — will be created)");
                }
            }
            else
            {
                sb.AppendLine("(special marker step — no file to check)");
            }
            sb.AppendLine();
        }

        var (raw, _, error) = await CallLlmRaw(
            "You are a plan auditor. Output ONLY the JSON object described below. No markdown, no extra text.",
            sb.ToString(), ct, requestTimeout: _infiniteTimeout, maxTokens: 2048);

        if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(raw))
        {
            await EmitLog(emitSse, "warn", $"Plan audit LLM call failed: {error ?? "empty response"}", ct: ct);
            return null;
        }

        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```"))
        {
            var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (m.Success) cleaned = m.Groups[1].Value.Trim();
        }
        var fb = cleaned.IndexOf('{');
        var lb = cleaned.LastIndexOf('}');
        if (fb >= 0 && lb > fb) cleaned = cleaned[fb..(lb + 1)];

        try
        {
            using var jDoc = JsonDocument.Parse(cleaned, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = jDoc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("steps", out var stepsArr) ||
                stepsArr.ValueKind != JsonValueKind.Array)
            {
                await EmitLog(emitSse, "warn", "Plan audit response missing 'steps' array", ct: ct);
                return null;
            }


            var preCheckedIndices = new HashSet<int>();
            for (var i = 0; i < plan.Plan.Count; i++)
            {
                var step = plan.Plan[i];
                var changeLower = (step.Change ?? "").ToLowerInvariant();

                if (!changeLower.StartsWith("remove ") && !changeLower.StartsWith("delete "))
                    continue;


                var htmlMatch = Regex.Match(step.Change ?? "", @"<(\w+)\b[^>]*>.*?</\1>", RegexOptions.Singleline);
                string? codeToRemove = htmlMatch.Success ? htmlMatch.Value : null;

                if (string.IsNullOrWhiteSpace(codeToRemove) || codeToRemove.Length < 20)
                    continue;

                var relPath = step.File.Replace('\\', '/');
                var fullPath = Path.GetFullPath(
                    Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));

                if (!System.IO.File.Exists(fullPath)) continue;
                var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);


                if (content.Contains(codeToRemove, StringComparison.Ordinal))
                {
                    await EmitLog(emitSse, "info",
                        $"Audit: step {i + 1} — code to remove IS present in file, NOT already done (deterministic override)", ct: ct);

                    auditSteps.Add(new AuditPlanStepResult
                    {
                        Index = i,
                        AlreadyDone = false,
                        NeedsDecoupling = false,
                        Reason = "Code to be removed is present in file — step is needed",
                        DecoupledSteps = null
                    });
                    preCheckedIndices.Add(i);
                }
            }

            foreach (var stepEl in stepsArr.EnumerateArray())
            {
                if (stepEl.ValueKind != JsonValueKind.Object) continue;

                var idx = stepEl.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number
                    ? idxEl.GetInt32() : -1;
                if (idx < 0 || idx >= plan.Plan.Count) continue;


                if (preCheckedIndices.Contains(idx))
                {
                    await EmitLog(emitSse, "info",
                        $"Audit: step {idx + 1} — using deterministic check (LLM verdict ignored)", ct: ct);
                    continue;
                }

                var alreadyDone = stepEl.TryGetProperty("alreadyDone", out var adEl) &&
                                  adEl.ValueKind == JsonValueKind.True && adEl.GetBoolean();

                var needsDecoupling = stepEl.TryGetProperty("needsDecoupling", out var ndEl) &&
                                      ndEl.ValueKind == JsonValueKind.True && ndEl.GetBoolean();

                string? reason = stepEl.TryGetProperty("reason", out var rEl) && rEl.ValueKind == JsonValueKind.String
                    ? rEl.GetString() : null;


                if (alreadyDone)
                {
                    var step = plan.Plan[idx];
                    var changeLower = (step.Change ?? "").ToLowerInvariant();
                    if (changeLower.StartsWith("remove ") || changeLower.StartsWith("delete "))
                    {
                        var htmlMatch = Regex.Match(step.Change ?? "", @"<(\w+)\b[^>]*>.*?</\1>", RegexOptions.Singleline);
                        if (htmlMatch.Success)
                        {
                            var relPath = step.File.Replace('\\', '/');
                            var fullPath = Path.GetFullPath(
                                Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
                            if (System.IO.File.Exists(fullPath))
                            {
                                var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                                if (content.Contains(htmlMatch.Value, StringComparison.Ordinal))
                                {
                                    await EmitLog(emitSse, "warn",
                                        $"Audit sanity check: step {idx + 1} was marked 'already done' but code is present — overriding", ct: ct);
                                    alreadyDone = false;
                                    reason = "Override: code to be removed is still present in file";
                                }
                            }
                        }
                    }
                }

                List<PlanStep>? decoupled = null;
                if (needsDecoupling && stepEl.TryGetProperty("decoupledSteps", out var dcArr) && dcArr.ValueKind == JsonValueKind.Array)
                {
                    decoupled = new List<PlanStep>();
                    foreach (var dc in dcArr.EnumerateArray())
                    {
                        if (dc.ValueKind != JsonValueKind.Object) continue;

                        var dcFile = dc.TryGetProperty("file", out var fEl) && fEl.ValueKind == JsonValueKind.String
                            ? fEl.GetString() ?? plan.Plan[idx].File : plan.Plan[idx].File;

                        var dcChange = dc.TryGetProperty("change", out var cEl) && cEl.ValueKind == JsonValueKind.String
                            ? cEl.GetString() ?? plan.Plan[idx].Change : plan.Plan[idx].Change;

                        if (!string.IsNullOrWhiteSpace(dcChange) && dcChange != plan.Plan[idx].Change)
                        {
                            var dcChangeLower = dcChange.ToLowerInvariant();
                            var researchVerbs = new[] { "locate", "find", "examine", "understand", "read", "explore", "look at", "inspect", "review", "check", "see", "search" };
                            if (researchVerbs.Any(v => dcChangeLower.StartsWith(v)))
                            {
                                await EmitLog(emitSse, "warn",
                                    $"Audit rejected research sub-step: \"{dcChange}\" — every step must make actual code changes.", ct: ct);
                            }
                            else
                            {
                                decoupled.Add(new PlanStep
                                {
                                    File = dcFile,
                                    Change = dcChange,
                                    Priority = plan.Plan[idx].Priority,
                                    ReferenceFiles = plan.Plan[idx].ReferenceFiles,
                                    LineNumber = plan.Plan[idx].LineNumber
                                });
                            }
                        }
                    }
                    if (decoupled.Count > 1)
                    {
                        var deduped = new List<PlanStep>();
                        foreach (var step in decoupled)
                        {
                            var isDuplicate = false;
                            var stepWords = step.Change?.ToLowerInvariant().Split(' ',
                                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
                            foreach (var existing in deduped)
                            {
                                if (!string.Equals(step.File, existing.File, StringComparison.OrdinalIgnoreCase))
                                    continue;
                                var existingWords = existing.Change?.ToLowerInvariant().Split(' ',
                                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
                                var commonWords = stepWords.Intersect(existingWords, StringComparer.OrdinalIgnoreCase).Count();
                                var maxLen = Math.Max(stepWords.Length, existingWords.Length);
                                if (maxLen > 0 && (double)commonWords / maxLen >= 0.70)
                                {
                                    isDuplicate = true;
                                    await EmitLog(emitSse, "warn",
                                        $"Audit dedup: removed duplicate decoupled step [{step.Change}] " +
                                        $"(~{commonWords * 100 / maxLen}% overlap with [{existing.Change}])", ct: ct);
                                    break;
                                }
                            }
                            if (!isDuplicate)
                                deduped.Add(step);
                        }
                        decoupled = deduped;
                    }
                    if (decoupled.Count == 0)
                    {
                        needsDecoupling = false;
                    }
                }

                auditSteps.Add(new AuditPlanStepResult
                {
                    Index = idx,
                    AlreadyDone = alreadyDone,
                    NeedsDecoupling = needsDecoupling,
                    Reason = reason,
                    DecoupledSteps = decoupled
                });

                if (alreadyDone)
                    await EmitLog(emitSse, "info",
                        $"Audit: step {idx + 1} already done — {reason}", ct: ct);
                if (needsDecoupling)
                    await EmitLog(emitSse, "info",
                        $"Audit: step {idx + 1} needs decoupling ({decoupled?.Count ?? 0} sub-steps) — {reason}", ct: ct);
            }

            return new PlanAuditResult { Steps = auditSteps };
        }
        catch (JsonException ex)
        {
            await EmitLog(emitSse, "warn", $"Plan audit JSON parse failed: {ex.Message}", ct: ct);
            return null;
        }
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
        "HttpClient", "HttpContent", "HttpMethod", "HttpStatusCode",
        "IHttpResponseBodyFeature", "IHttpRequestLifetimeFeature", "IHttpConnectionFeature",
        "IHttpWebSocketFeature", "Stream", "PipeWriter", "PipeReader",
        "HttpProtocol", "HttpVersion", "HttpContext", "HttpRequest", "HttpResponse",
        "Model", "ViewDataDictionary", "ViewData", "TempData", "ViewBag", "RouteData"
    };

    private static List<string> ScanMissingTypes(string fullFileContent, string newCode)
    {
        var declaredTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(fullFileContent,
            @"\b(class|record|struct|enum|interface)\s+([A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Multiline))
            declaredTypes.Add(m.Groups[2].Value);

        var usingNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(fullFileContent, @"using\s+([A-Za-z_.][A-Za-z0-9_.]*)\s*;"))
            usingNamespaces.Add(m.Groups[1].Value);

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(newCode, @"\b[A-Z][a-zA-Z0-9_]+\b"))
        {
            var name = m.Value;
            if (name.Length < 3) continue;
            if (_builtInTypes.Contains(name)) continue;
            if (declaredTypes.Contains(name)) continue;
            if (usingNamespaces.Any(ns => name.StartsWith(ns.Split('.').Last(), StringComparison.OrdinalIgnoreCase)))
                continue;
            candidates.Add(name);
        }

        var result = new List<string>();
        foreach (var c in candidates)
        {

            var isMethodCall = Regex.IsMatch(newCode, @"\b" + Regex.Escape(c) + @"\s*\(");
            var isConstructor = Regex.IsMatch(newCode, @"new\s+" + Regex.Escape(c) + @"\s*\(");
            if (isMethodCall && !isConstructor)
                continue;


            if (Regex.IsMatch(newCode, @"\." + Regex.Escape(c) + @"\b"))
                continue;

            var fbPattern = @"\[FromBody\]\s+\b" + Regex.Escape(c) + @"\b";
            if (Regex.IsMatch(newCode, fbPattern))
            { result.Add(c); continue; }

            if (c.EndsWith("Request", StringComparison.OrdinalIgnoreCase) ||
                c.EndsWith("Response", StringComparison.OrdinalIgnoreCase) ||
                c.EndsWith("Result", StringComparison.OrdinalIgnoreCase))
            { result.Add(c); continue; }

            var genericPattern = @"<" + Regex.Escape(c) + @"\s*>";
            if (Regex.IsMatch(newCode, genericPattern))
            { result.Add(c); continue; }
        }

        return result.Distinct().ToList();
    }

    private async Task<StepExplorationResult> RunStepExplorationLoop(
        PlanStep step,
        string projectRoot,
        string originalPrompt,
        AgentPlan? fullPlan,
        int planItemIndex,
        bool emitSse,
        CancellationToken ct,
        string? cardId = null,
        List<string>? attachedFiles = null)
    {
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
        const int ConfidenceThreshold = 80;
        var cfg4 = await LoadConfigAsync();
        var MaxContextChars = cfg4.maxContextChars;

        var relPath = step.File.Replace('\\', '/');
        var fullPath = Path.GetFullPath(
            Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));

        var ctx = new StringBuilder();
        var filesRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string AbsNormalize(string p)
        {
            try
            {
                var full = Path.IsPathRooted(p)
                    ? Path.GetFullPath(p)
                    : Path.GetFullPath(Path.Combine(projectRoot, p));
                return full.Replace('\\', '/').TrimEnd('/');
            }
            catch { return p.Replace('\\', '/').TrimEnd('/'); }
        }

        string RelNormalize(string p)
        {
            try
            {
                var full = Path.IsPathRooted(p)
                    ? Path.GetFullPath(p)
                    : Path.GetFullPath(Path.Combine(projectRoot, p));
                if (full.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                    return Path.GetRelativePath(projectRoot, full).Replace('\\', '/');
                return full.Replace('\\', '/').TrimEnd('/');
            }
            catch { return p.Replace('\\', '/').TrimEnd('/'); }
        }

        bool AddFileRead(string path)
        {
            var rel = RelNormalize(path);
            var abs = AbsNormalize(path);
            var addedToFiles = filesRead.Add(rel);
            normalizedPaths.Add(abs);
            return addedToFiles;
        }

        List<string> FindLikelyProjectFiles(string requested, int max = 5)
        {
            var normalized = (requested ?? "").Replace('\\', '/').Trim().Trim('"', '\'', '`');
            if (string.IsNullOrWhiteSpace(normalized)) return new List<string>();

            var requestedName = Path.GetFileName(normalized);
            var requestedStem = Path.GetFileNameWithoutExtension(requestedName);
            var rawTokens = Regex.Matches(normalized + " " + requestedStem, @"[A-Za-z_][A-Za-z0-9_]{2,}")
                .Select(m => m.Value)
                .Where(t => !new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "read", "file", "path", "source", "component", "service", "class", "method",
                    "function", "interface", "model", "controller", "style", "template"
                }.Contains(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();

            var skipSegments = new[] { "/bin/", "/obj/", "/node_modules/", "/dist/", "/packages/", "/.git/", "/.vs/" };
            var textExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".cs", ".ts", ".tsx", ".js", ".jsx", ".html", ".css", ".scss", ".less",
                ".json", ".xml", ".yaml", ".yml", ".md", ".razor", ".cshtml", ".sql"
            };

            var scored = new List<(string rel, int score)>();
            foreach (var file in Directory.EnumerateFiles(projectRoot, "*.*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
                var relLow = "/" + rel.ToLowerInvariant();
                if (skipSegments.Any(relLow.Contains)) continue;
                var ext = Path.GetExtension(rel);
                if (!textExts.Contains(ext)) continue;

                var name = Path.GetFileName(rel);
                var stem = Path.GetFileNameWithoutExtension(rel);
                var score = 0;

                if (string.Equals(rel, normalized, StringComparison.OrdinalIgnoreCase)) score += 1000;
                if (rel.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase)) score += 800;
                if (!string.IsNullOrWhiteSpace(requestedName) &&
                    string.Equals(name, requestedName, StringComparison.OrdinalIgnoreCase)) score += 650;
                if (!string.IsNullOrWhiteSpace(requestedStem) &&
                    string.Equals(stem, requestedStem, StringComparison.OrdinalIgnoreCase)) score += 500;

                foreach (var token in rawTokens)
                {
                    if (stem.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 120;
                    if (rel.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 45;
                }

                if (score > 0)
                    scored.Add((rel, score));
            }

            if (scored.Count < max && rawTokens.Count > 0)
            {
                var already = scored.Select(s => s.rel).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.EnumerateFiles(projectRoot, "*.*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
                    if (already.Contains(rel)) continue;
                    var relLow = "/" + rel.ToLowerInvariant();
                    if (skipSegments.Any(relLow.Contains)) continue;
                    if (!textExts.Contains(Path.GetExtension(rel))) continue;

                    try
                    {
                        var content = System.IO.File.ReadAllText(file);
                        var score = rawTokens.Sum(token =>
                            Regex.IsMatch(content, $@"\b{Regex.Escape(token)}\b") ? 35 : 0);
                        if (score > 0) scored.Add((rel, score));
                    }
                    catch { }
                }
            }

            return scored
                .OrderByDescending(s => s.score)
                .ThenBy(s => s.rel.Length)
                .Select(s => s.rel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .ToList();
        }


        if (attachedFiles != null && attachedFiles.Count > 0)
        {
            await EmitLog(emitSse, "info", $"  ⊕ Seeding {attachedFiles.Count} attached file(s) into exploration context", ct: ct);
            foreach (var af in attachedFiles)
            {
                try
                {
                    var afPath = Path.GetFullPath(Path.Combine(projectRoot, af.TrimStart('/', '\\')));
                    if (System.IO.File.Exists(afPath) && AddFileRead(afPath))
                    {
                        var afContent = await System.IO.File.ReadAllTextAsync(afPath, ct);
                        var afRel = Path.GetRelativePath(projectRoot, afPath).Replace('\\', '/');
                        ctx.AppendLine($"--- {afRel} (attached) ---");
                        ctx.AppendLine(afContent);
                        ctx.AppendLine($"--- end {afRel} ---");
                        ctx.AppendLine();
                    }
                }
                catch (Exception ex)
                {
                    await EmitLog(emitSse, "warn", $"  ⚠ Could not read attached file '{af}': {ex.Message}", ct: ct);
                }
            }
        }

        var serviceCallMatch = Regex.Match(step.Change ?? "", @"(?:this\.)?([A-Za-z]\w*Service)\b", RegexOptions.IgnoreCase);

        if (!serviceCallMatch.Success && fullPlan?.Summary != null)
        {
            serviceCallMatch = Regex.Match(fullPlan.Summary, @"([A-Za-z]\w*Service)\b", RegexOptions.IgnoreCase);
        }

        if (!serviceCallMatch.Success)
        {
            serviceCallMatch = Regex.Match(ctx.ToString(), @"this\.(\w+Service)\b", RegexOptions.IgnoreCase);
        }

        if (serviceCallMatch.Success)
        {
            var serviceName = serviceCallMatch.Groups[1].Value;
            var serviceFiles = Directory.GetFiles(projectRoot, $"{serviceName}.ts", SearchOption.AllDirectories)
                .Where(f => !f.Contains("node_modules") && !f.Contains("dist"))
                .Take(1)
                .ToList();

            foreach (var sf in serviceFiles)
            {
                var rel = Path.GetRelativePath(projectRoot, sf).Replace('\\', '/');
                if (AddFileRead(rel))
                {
                    var content = await System.IO.File.ReadAllTextAsync(sf, Encoding.UTF8, ct);
                    ctx.AppendLine($"### {rel} (deterministic service injection)");
                    ctx.AppendLine("```typescript");
                    ctx.AppendLine(content);
                    ctx.AppendLine("```");
                    ctx.AppendLine();
                    await EmitLog(emitSse, "info", $"  🎯 Auto-injected service: {rel}", ct: ct);
                }
            }
        }

        var refinedChange = step.Change;
        string? targetSymbol = null;
        string? lineRange = null;
        var confidence = 0;
        var roundsCompleted = 0;

        await EmitLog(emitSse, "info", $"🔍 Exploring: {relPath}", ct: ct);

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

        if (System.IO.File.Exists(fullPath) &&
            AgentUtilities.IsPathUnderRoot(fullPath, projectRoot))
        {
            var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
            var excerpt = content.Length > 5_000
                ? AgentUtilities.ExtractRelevantExcerpt(content, step.Change ?? "", step.OldString, cfg4.fileBodyTruncationChars)
                : content;

            ctx.AppendLine($"### TARGET FILE: {relPath}  ({content.Length:N0} chars total)");
            ctx.AppendLine("```");
            ctx.AppendLine(excerpt);
            ctx.AppendLine("```");
            ctx.AppendLine();
            AddFileRead(relPath);
            await EmitLog(emitSse, "info", $"  📄 {relPath}", ct: ct);
        }

        var targetWasAttached = attachedFiles != null &&
            attachedFiles.Any(af =>
            {
                var normAf = af.TrimStart('/', '\\').Replace('\\', '/');
                return string.Equals(normAf, relPath, StringComparison.OrdinalIgnoreCase);
            });

        if (targetWasAttached)
        {
            var fallbackSymbol = AgentUtilities.ExtractTargetSymbolFromChange(step.Change ?? "");
            await EmitLog(emitSse, "info",
                $"  ✓ Target file was attached by user — skipping LLM exploration, returning content directly" +
                (fallbackSymbol != null ? $" (target symbol: '{fallbackSymbol}')" : ""), ct: ct);
            return new StepExplorationResult
            {
                EnrichedStep = step,
                ExplorationContext = ctx.ToString(),
                FilesRead = filesRead.ToList(),
                RefinedChange = step?.Change ?? "",
                TargetSymbol = fallbackSymbol,
                EstimatedLineRange = null,
                Confidence = 100,
                LowConfidenceWarning = null
            };
        }

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

            var parsed = AgentUtilities.ParseStepExplorationResponse(raw);

            if (!string.IsNullOrWhiteSpace(parsed.RefinedChange))
            {
                refinedChange = parsed.RefinedChange;
                targetSymbol = parsed.TargetSymbol;
                lineRange = parsed.LineRange;
                confidence = parsed.Confidence;
            }

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

            var newlyRead = 0;
            foreach (var requested in parsed.FilesToRead.Take(3))
            {
                if (normalizedPaths.Contains(AbsNormalize(requested)) ||
                    filesRead.Contains(RelNormalize(requested))) continue;

                var fp = Path.GetFullPath(
                    Path.Combine(projectRoot, requested.Replace('/', Path.DirectorySeparatorChar)));

                if (!System.IO.File.Exists(fp) ||
                    !AgentUtilities.IsPathUnderRoot(fp, projectRoot))
                {
                    var matches = FindLikelyProjectFiles(requested, max: 5);

                    if (matches.Count > 0)
                    {
                        var readMatches = 0;
                        foreach (var correctPath in matches.Take(2))
                        {
                            if (normalizedPaths.Contains(AbsNormalize(correctPath)) ||
                                filesRead.Contains(RelNormalize(correctPath))) continue;

                            var matchFull = Path.GetFullPath(Path.Combine(projectRoot, correctPath.Replace('/', Path.DirectorySeparatorChar)));
                            var matchContent = await System.IO.File.ReadAllTextAsync(matchFull, Encoding.UTF8, ct);
                            var matchExcerpt = matchContent.Length > 3_500
                                ? AgentUtilities.ExtractRelevantExcerpt(matchContent, step.Change ?? "", step.OldString, cfg4.fileBodyTruncationChars)
                                : matchContent;
                            if (ctx.Length + matchExcerpt.Length <= MaxContextChars)
                            {
                                ctx.AppendLine($"### {correctPath}  (resolved from `{requested}`)");
                                ctx.AppendLine("```");
                                ctx.AppendLine(matchExcerpt);
                                ctx.AppendLine("```");
                                ctx.AppendLine();
                                readMatches++;
                            }
                            else
                            {
                                ctx.AppendLine($"⚠ `{requested}` resolved to `{correctPath}` (skipped — context budget exhausted)");
                                ctx.AppendLine();
                            }
                            AddFileRead(correctPath);
                        }

                        if (matches.Count > 2)
                        {
                            var suggestions = string.Join(", ", matches.Skip(2).Select(m => $"`{m}`"));
                            ctx.AppendLine($"Other likely matches for `{requested}`: {suggestions}.");
                            ctx.AppendLine();
                        }

                        await EmitLog(emitSse, "info",
                            $"  🔍 {requested} → {string.Join(", ", matches.Take(2))}" +
                            (readMatches == 0 ? " (not read: context budget or duplicate)" : ""), ct: ct);
                    }
                    else
                    {
                        await EmitLog(emitSse, "warn",
                            $"  ⚠ Not found: {requested}", ct: ct);
                        ctx.AppendLine($"⚠ The path `{requested}` does not exist. Use an exact relative path from the project root.");
                        ctx.AppendLine();
                    }
                    continue;
                }

                var fc = await System.IO.File.ReadAllTextAsync(fp, Encoding.UTF8, ct);
                var excerpt = fc.Length > 3_500
                    ? AgentUtilities.ExtractRelevantExcerpt(fc, step.Change ?? "", step.OldString, cfg4.fileBodyTruncationChars)
                    : fc;

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
                AddFileRead(requested);
                newlyRead++;
                await EmitLog(emitSse, "info", $"  📄 {requested}", ct: ct);
            }

            if (newlyRead == 0 && parsed.FilesToRead.Count == 0) break;
        }

    ExplorationComplete:

        if (ctx.Length > 0 && !string.IsNullOrWhiteSpace(step.Change))
        {
            var contextFiles = Regex.Matches(ctx.ToString(), @"^###\s+([^\n]+)", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p) &&
                    !p.StartsWith("TARGET FILE:", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(p.Split(' ')[0], relPath, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Split(' ')[0].TrimEnd(')', '('))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (contextFiles.Count > 2)
            {
                var filterPrompt = new StringBuilder();
                filterPrompt.AppendLine("You are a file relevance filter. Given a task and a list of files, identify which files are UNNECESSARY for completing the task. Return ONLY the file paths that are NOT useful, one per line. If all files are useful, return empty.");
                filterPrompt.AppendLine();
                filterPrompt.AppendLine($"TASK: {step.Change}");
                filterPrompt.AppendLine();
                filterPrompt.AppendLine("FILES IN CONTEXT:");
                foreach (var f in contextFiles)
                    filterPrompt.AppendLine($"  {f}");
                filterPrompt.AppendLine();
                filterPrompt.AppendLine("Return the paths of files that are NOT useful, one per line. If all are useful, return nothing.");

                var (filterRaw, _, _) = await CallLlmRaw(
                    "You are a file relevance filter. Be concise and accurate.",
                    filterPrompt.ToString(),
                    ct, TimeSpan.FromSeconds(15), maxTokens: 512);

                if (!string.IsNullOrWhiteSpace(filterRaw))
                {
                    var toRemove = filterRaw.Split('\n')
                        .Select(l => l.Trim().Trim('"', '*', '`', '-', ' '))
                        .Where(l => !string.IsNullOrWhiteSpace(l) &&
                            contextFiles.Any(cf => l.Contains(cf, StringComparison.OrdinalIgnoreCase)))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    foreach (var removePath in toRemove)
                    {
                        var matchFile = contextFiles.FirstOrDefault(cf =>
                            removePath.Contains(cf, StringComparison.OrdinalIgnoreCase));
                        if (matchFile == null) continue;

                        var sectionMatch = Regex.Match(ctx.ToString(),
                            $@"^### {Regex.Escape(matchFile)}[^\n]*\n.*?(?=^### |\Z)",
                            RegexOptions.Multiline | RegexOptions.Singleline);
                        if (sectionMatch.Success)
                        {
                            ctx.Remove(sectionMatch.Index, sectionMatch.Length);
                            await EmitLog(emitSse, "info",
                                $"  🗑️ LLM filter dropped: {matchFile} (not useful for task)", ct: ct);
                        }
                    }
                }
            }
        }

        string? astOldStringHint = null;
        if (!string.IsNullOrWhiteSpace(targetSymbol) &&
            System.IO.File.Exists(fullPath))
        {
            var ext = Path.GetExtension(relPath).ToLowerInvariant();
            var supportedExt = ext is ".cs" or ".ts" or ".js" or ".tsx" or ".jsx";
            if (supportedExt)
            {
                string? astOld = null;
                string? astErr = null;
                var resolvedType = "";
                foreach (var tryType in new[] { "method", "class", "interface", "property" })
                {
                    (astOld, astErr) = AstResolveEdit(fullPath, tryType, targetSymbol);
                    if (astOld != null) { resolvedType = tryType; break; }
                }
                if (astOld != null)
                {
                    var lineCount = astOld.Split('\n').Length;
                    var changeLower = (refinedChange ?? step.Change ?? "").ToLowerInvariant();
                    var isPropertyAdd = (changeLower.Contains("add") || changeLower.Contains("fill") || changeLower.Contains("populate")) &&
                        (changeLower.Contains("propert") || changeLower.Contains("field") ||
                         changeLower.Contains("column") || changeLower.Contains("setting") ||
                         changeLower.Contains("option") || changeLower.Contains("bool") ||
                         changeLower.Contains("string") || changeLower.Contains("int") ||
                         changeLower.Contains("{ get;") || changeLower.Contains("{get;"));
                    if (resolvedType == "class" && (lineCount > 20 || isPropertyAdd))
                    {
                        await EmitLog(emitSse, "info",
                            $"  🎯 AST resolved '{targetSymbol}' as class " +
                            $"({lineCount} lines) — {(isPropertyAdd ? "change targets a property add — skipping class AST hint to avoid full-class rewrite" : "too large for hint")}, " +
                            $"skipping to keep excerpt focused", ct: ct);
                    }
                    else
                    {
                        astOldStringHint = astOld;
                        await EmitLog(emitSse, "info",
                            $"  🎯 AST resolved '{targetSymbol}' " +
                            $"({lineCount} lines)", ct: ct);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(astErr))
                {
                    await EmitLog(emitSse, "info",
                        $"  AST hint failed ({astErr}) — will use text matching", ct: ct);
                }
            }
        }

        string? lowConfidenceWarning = null;
        if (roundsCompleted >= MaxRounds && confidence > 0 && confidence < 30)
        {
            lowConfidenceWarning =
                $"Exploration exhausted {MaxRounds} rounds at only {confidence}% confidence — " +
                $"step description may be too vague for a reliable edit";

            var reDerived = await ReDeriveStepDescription(
                step, originalPrompt, ctx.ToString(), refinedChange ?? step.Change ?? "", ct);

            if (!string.IsNullOrWhiteSpace(reDerived) &&
                reDerived.Length > (refinedChange?.Length ?? 0) / 3)
            {
                await EmitLog(emitSse, "info",
                    $"  🔄 Re-derived from original prompt " +
                    $"(was {confidence}% after {roundsCompleted} rounds)", ct: ct);
                refinedChange = reDerived;
            }
        }
        var enrichedStep = new PlanStep
        {
            File = step.File,
            Change = (string.IsNullOrWhiteSpace(refinedChange) ? step.Change : refinedChange) ?? "",
            Priority = step.Priority,
            LineNumber = step.LineNumber,

            OldString = astOldStringHint ?? step.OldString ?? "",
            NewString = step.NewString ?? ""
        };
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
            astResolved = astOldStringHint != null,
            lowConfidenceWarning
        }, emitSse, ct);

        await EmitLog(emitSse, "info",
            $"  ✅ Exploration done — {filesRead.Count} file(s), confidence {confidence}%", filesRead.ToList(),
            ct: ct);

        return new StepExplorationResult
        {
            EnrichedStep = enrichedStep,
            ExplorationContext = ctx.ToString(),
            FilesRead = filesRead.ToList(),
            RefinedChange = refinedChange ?? "",
            TargetSymbol = targetSymbol,
            EstimatedLineRange = lineRange,
            Confidence = confidence,
            RoundsCompleted = roundsCompleted,
            LowConfidenceWarning = lowConfidenceWarning
        };
    }

    private async Task<string?> ReDeriveStepDescription(
        PlanStep step,
        string originalPrompt,
        string explorationContext,
        string vagueDescription,
        CancellationToken ct)
    {
        var sysPrompt = "You are an expert code reviewer. Given the original user request, "
            + "the exploration context (files read and their content), and the current step "
            + "description (which may be vague), produce a crisp, specific, one-sentence "
            + "re-description of exactly what code change this step requires. Be concrete — "
            + "include the file path, the symbol or code region, and the exact nature of the "
            + "change (add, modify, delete, rename). Output ONLY the re-derived description, "
            + "no JSON, no explanation.";

        var userPrompt =
            $"## Original User Request\n{originalPrompt}\n\n"
            + $"## Exploration Context (files read)\n{explorationContext}\n\n"
            + $"## Current Step Description (may be vague)\n{vagueDescription}\n\n"
            + "Produce a crisp, specific, one-sentence re-description of this step's code change:";

        var (raw, _, _) = await CallLlmRaw(
            sysPrompt, userPrompt, ct,
            TimeSpan.FromSeconds(20), maxTokens: 256);

        if (string.IsNullOrWhiteSpace(raw)) return null;

        var cleaned = raw.Trim().Trim('"').Trim();
        if (cleaned.Length > 250) cleaned = cleaned[..250] + "…";
        return cleaned;
    }

    private async Task<string> EnrichContextWithProjectTypesAndSql(
        string projectRoot, string relPath, string stepChange, string explorationContext,
        HashSet<string> alreadyRead, bool emitSse, CancellationToken ct)
    {
        var buf = new StringBuilder();
        const int MaxEnrichChars = 6000;

        var targetFullPath = Path.GetFullPath(
            Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!System.IO.File.Exists(targetFullPath)) return explorationContext;
        var targetContent = await System.IO.File.ReadAllTextAsync(targetFullPath, Encoding.UTF8, ct);

        var methodNameMatch = Regex.Match(stepChange,
            @"(?:Modify|Update|Change|Edit|Replace|Add|Remove|Delete)\s+(?:the|this|that|a|an)?\s*(\w+)",
            RegexOptions.IgnoreCase);

        var methodName = methodNameMatch.Success ? methodNameMatch.Groups[1].Value : null;

        string? methodBody = null;
        if (methodName != null && methodName != "?")
        {
            var methodStartMatch = Regex.Match(targetContent,
                $@"({Regex.Escape(methodName)}\s*\()", RegexOptions.IgnoreCase);
            if (methodStartMatch.Success)
            {
                var startIdx = methodStartMatch.Index;
                var searchFrom = startIdx + methodStartMatch.Length;
                var parenDepth = 1;
                var braceIdx = -1;
                for (var i = searchFrom; i < targetContent.Length; i++)
                {
                    if (targetContent[i] == '(') parenDepth++;
                    else if (targetContent[i] == ')') parenDepth--;
                    else if (targetContent[i] == '{' && parenDepth == 0)
                    { braceIdx = i; break; }
                }
                if (braceIdx > 0)
                {
                    var depth = 0;
                    var endIdx = -1;
                    for (var i = braceIdx; i < targetContent.Length; i++)
                    {
                        if (targetContent[i] == '{') depth++;
                        else if (targetContent[i] == '}') { depth--; if (depth == 0) { endIdx = i; break; } }
                    }
                    if (endIdx > 0)
                        methodBody = targetContent.Substring(braceIdx, endIdx - braceIdx + 1);
                }
            }
        }

        var searchScope = methodBody ?? targetContent;
        var sqlStrings = new List<string>();
        foreach (Match sm in Regex.Matches(searchScope,
            @"@?""(?:[^""\\]*(?:\\.[^""\\]*)*)""", RegexOptions.Singleline))
        {
            var raw = sm.Value;
            if (Regex.IsMatch(raw, @"\b(SELECT|INSERT|UPDATE|DELETE|CREATE\s+TABLE|ALTER\s+TABLE)\b",
                RegexOptions.IgnoreCase))
                sqlStrings.Add(raw);
        }

        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var notTableWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "the", "and", "or", "not", "in", "on", "at", "to", "for", "of", "by",
            "as", "is", "it", "an", "be", "has", "have", "are", "was", "were",
            "from", "into", "with", "without", "using", "where", "when", "while",
            "then", "than", "this", "that", "these", "those", "each", "all",
            "both", "between", "after", "before", "above", "below", "under",
            "over", "through", "during", "until", "since", "within", "about",
            "join", "inner", "outer", "left", "right", "full", "cross", "natural",
            "order", "group", "having", "limit", "offset", "set", "values",
            "select", "insert", "update", "delete", "create", "alter", "drop",
            "true", "false", "null", "default", "unique", "index", "key",
            "primary", "foreign", "check", "cascade", "restrict", "action",
            "count", "sum", "avg", "min", "max", "distinct", "exists", "case",
            "when", "else", "then", "end", "cast", "convert", "coalesce",
            "nullif", "date", "time", "timestamp", "year", "month", "day",
            "hour", "minute", "second", "now", "utc_timestamp",
            "tinyint", "smallint", "mediumint", "int", "integer", "bigint",
            "decimal", "numeric", "float", "double", "real", "bit", "boolean",
            "char", "varchar", "nvarchar", "text", "blob", "binary", "varbinary",
            "enum", "set", "json", "geometry", "point", "linestring", "polygon",
            "return", "returns", "declare", "begin", "end", "if", "else",
            "iterate", "leave", "loop", "repeat", "while", "signal", "resignal",
            "cursor", "handler", "continue", "exit", "undo", "condition",
            "open", "close", "fetch", "into", "call", "rename", "truncate",
            "start", "stop", "commit", "rollback", "savepoint", "release",
            "lock", "unlock", "grant", "revoke", "analyze", "optimize",
            "reorganize", "repair", "check", "checksum", "backup", "restore",
            "utf8", "utf8mb4", "ascii", "latin1", "unicode", "?",
            "auto_increment", "unsigned", "signed", "zerofill",
            "current_timestamp", "current_date", "current_time", "localtime",
            "localtimestamp"
        };

        foreach (Match m in Regex.Matches(string.Join("\n", sqlStrings),
            @"(?:FROM|JOIN|INTO|UPDATE|TABLE(?:\s+IF\s+NOT\s+EXISTS)?)\s+`?(\w+(?:\.\w+)?)`?",
            RegexOptions.IgnoreCase))
        {
            var rawTbl = m.Groups[1].Value;
            var tbl = rawTbl.Contains('.') ? rawTbl.Split('.')[^1] : rawTbl;
            if (tbl.Length > 2 && !notTableWords.Contains(tbl) &&
                tbl[0] != '@' && !char.IsDigit(tbl[0]))
                tableNames.Add(tbl);
        }

        var skipTypes = new HashSet<string>(StringComparer.Ordinal) {
            "string", "int", "bool", "long", "double", "float", "decimal", "char",
            "byte", "short", "uint", "ulong", "ushort", "sbyte", "object", "void",
            "Task", "ValueTask", "IEnumerable", "ICollection", "IList", "List",
            "Dictionary", "HashSet", "Queue", "Stack", "Tuple", "Nullable",
            "StringBuilder", "StringReader", "StringWriter",
            "HttpResponseMessage", "HttpRequestMessage",
            "ActionResult", "IActionResult", "OkResult", "OkObjectResult",
            "BadRequestResult", "NotFoundResult", "StatusCodeResult",
            "JsonResult", "FileResult", "ContentResult", "RedirectResult",
            "ViewResult", "PartialViewResult", "IQueryable",
            "Thread", "TaskCompletionSource", "CancellationToken",
            "HttpClient", "HttpContext", "HttpRequest", "HttpResponse",
            "Stream", "StreamReader", "StreamWriter", "MemoryStream",
            "FileStream", "BinaryReader", "BinaryWriter", "TextReader", "TextWriter",
            "DateTime", "DateTimeOffset", "TimeSpan", "Guid", "Uri", "Version",
            "Regex", "Match", "Group", "Capture", "StringComparison",
            "Encoding", "UTF8", "Unicode", "ASCII", "Declare", "TryParse",
             "Parse", "Convert", "Math", "Random",
            "Exception", "InvalidOperationException", "ArgumentNullException",
            "ArgumentException", "IOException", "FormatException",
            "Response", "Request", "Delegate", "Func", "Action", "Predicate",
            "NameValueCollection", "IOrderedEnumerable",
            "IServiceProvider", "IDisposable", "IAsyncDisposable",
            "Startup", "Program", "MySqlConnection", "MySqlCommand", "MySqlDataReader",
            "MySqlParameter", "MySqlTransaction", "MySqlException",
            "SqlConnection", "SqlCommand", "SqlDataReader",
            "NpgsqlConnection", "NpgsqlCommand", "NpgsqlDataReader", "?",
            "IConfiguration", "Log", "JsonDocument", "JsonNode", "JsonObject",
            "JsonArray", "JsonValue", "JsonSerializer", "JsonSerializerOptions"
        };
        var serviceSuffixes = new[] { "Service", "Controller", "Handler", "Manager",
            "Provider", "Factory", "Repository", "Helper", "Util", "Extension",
            "Middleware", "Filter", "Attribute", "Converter", "Mapper", "Builder",
            "Adapter", "Proxy", "Facade", "Strategy", "Observer", "Configuration",
            "Options", "Settings" };

        var typeRefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(searchScope,
            @"(?:public|private|protected|readonly|static)?\s*(?:\w+)\s*:\s*([A-Z][A-Za-z0-9_]+)",
            RegexOptions.Compiled))
        {
            var name = m.Groups[1].Value;
            if (!skipTypes.Contains(name) && name.Length > 2 &&
                !serviceSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal)))
                typeRefs.Add(name);
        }

        foreach (Match m in Regex.Matches(searchScope,
            @"<\s*([A-Z][A-Za-z0-9_]+)\s*>",
            RegexOptions.Compiled))
        {
            var name = m.Groups[1].Value;
            if (!skipTypes.Contains(name) && name.Length > 2)
                typeRefs.Add(name);
        }

        await EmitLog(emitSse, "info",
            $"  🔎 Enrichment: {tableNames.Count} table(s) [{string.Join(", ", tableNames.Take(5))}], " +
            $"{typeRefs.Count} model type(s) from method '{(methodName ?? "?")}'", new { typeRefs, tableNames }, ct: ct);

        if (typeRefs.Count == 0 && tableNames.Count == 0)
            return explorationContext;

        var typeFileExtensions = new[] { "*.cs", "*.ts", "*.tsx", "*.js", "*.jsx" };
        var projectFiles = typeFileExtensions
            .SelectMany(ext => Directory.EnumerateFiles(projectRoot, ext, SearchOption.AllDirectories))
            .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\")
                     && !f.Contains("\\node_modules\\") && !f.Contains("\\.git\\")
                     && !f.Contains("\\dist\\"))
            .ToList();

        foreach (var tblName in tableNames)
        {
            if (buf.Length > MaxEnrichChars) break;
            foreach (var pf in projectFiles)
            {
                if (buf.Length > MaxEnrichChars) break;
                try
                {
                    var content = await System.IO.File.ReadAllTextAsync(pf, Encoding.UTF8, ct);
                    var rel = Path.GetRelativePath(projectRoot, pf).Replace('\\', '/');
                    if (rel == relPath || alreadyRead.Contains(rel) || alreadyRead.Contains(pf))
                        continue;

                    var sqlFound = new List<string>();
                    foreach (Match sm in Regex.Matches(content,
                        @"@?""(?:[^""\\]*(?:\\.[^""\\]*)*)""", RegexOptions.Singleline))
                    {
                        var val = sm.Value;
                        if (!Regex.IsMatch(val, @"\b(SELECT|INSERT|UPDATE|DELETE)\b",
                            RegexOptions.IgnoreCase)) continue;
                        if (Regex.IsMatch(val, @"\b" + Regex.Escape(tblName) + @"\b",
                            RegexOptions.IgnoreCase))
                        {
                            var clean = val.Length > 300 ? val[..297] + "..." : val;
                            sqlFound.Add(clean);
                        }
                    }
                    if (sqlFound.Count == 0) continue;

                    alreadyRead.Add(rel);
                    buf.AppendLine($"### {rel}  (table: {tblName})");
                    buf.AppendLine("```sql");
                    foreach (var s in sqlFound.Take(5))
                        buf.AppendLine(s);
                    buf.AppendLine("```");
                    buf.AppendLine();
                }
                catch { continue; }
            }
        }

        foreach (var typeName in typeRefs.OrderByDescending(t => t.Length))
        {
            if (buf.Length > MaxEnrichChars) break;
            foreach (var pf in projectFiles)
            {
                if (buf.Length > MaxEnrichChars) break;
                try
                {
                    var content = await System.IO.File.ReadAllTextAsync(pf, Encoding.UTF8, ct);
                    if (Regex.IsMatch(content,
                        $@"(?:class|record|struct|interface|type)\s+{Regex.Escape(typeName)}\b",
                        RegexOptions.IgnoreCase))
                    {
                        var rel = Path.GetRelativePath(projectRoot, pf).Replace('\\', '/');


                        if (alreadyRead.Contains("_type:" + rel) || alreadyRead.Contains("_type:" + pf))
                            continue;
                        alreadyRead.Add("_type:" + rel);

                        var excerpt = AgentUtilities.ExtractRelevantExcerpt(content, typeName, null, 800);
                        buf.AppendLine($"### {rel}  (model: {typeName})");
                        buf.AppendLine("```csharp");
                        buf.AppendLine(excerpt);
                        buf.AppendLine("```");
                        buf.AppendLine();
                        break;
                    }
                }
                catch { continue; }
            }
        }

        if (buf.Length == 0) return explorationContext;

        var enrichment = buf.ToString();
        await EmitLog(emitSse, "info",
            $"  📄 Auto-enriched context ({enrichment.Length:N0} chars)", new { enrichment }, ct: ct);

        var propertyWarning = "\n⚠ CRITICAL: The type definitions below show the EXACT property names. " +
            "Every `.PropertyName` you write in your edit MUST match these definitions exactly. " +
            "For example, if CalendarEntry shows `Note` property, use `.Note` not `.Description`. " +
            "If it shows `Type`, use `.Type` not `.Title`. Cross-reference EVERY property access.\n";
        return explorationContext + "\n### AUTO-ENRICHED CONTEXT\n" + propertyWarning + enrichment;
    }

    private static string BuildStepExplorationSystemPrompt() =>
        "You are a senior codebase navigation agent. Before a code change is applied, " +
        "your job is to understand exactly what needs to change, which existing code owns it, " +
        "and the smallest context needed to edit it safely.\n\n" +
        "You are given the original task, the full plan (so you understand what came before " +
        "and after), the specific step, and the files already read.\n\n" +
        "Work like a careful coding agent: inspect concrete files before inferring, follow names " +
        "from imports/call sites/types, and stop reading as soon as the edit is grounded.\n\n" +
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
        "3. Max 3 files per request; prefer exact project-relative paths. If you only know a symbol, " +
        "request the most likely file path from imports, filenames, or existing context; do not ask for broad directories.\n" +
        "4. Search strategy: target file first, then imported definitions, adjacent component/template/style files, " +
        "interface/model definitions, and tests only if they reveal expected behavior. Avoid generated, minified, bin/obj, and package files.\n" +
        "5. refinedChange MUST: name the exact method/function/component, describe the " +
        "exact code block being replaced, describe the replacement code — zero ambiguity\n" +
        "6. targetSymbol: the identifier of the specific method/function/class being changed\n" +
        "7. confidence 0-100: if < 70, request more files rather than guessing\n" +
        "8. If the target file already has enough context (small file, obvious location), " +
        "go ready=true on round 1 with a precise refinedChange\n" +
        "9. If the change involves a component, import, alias, or UI element, " +
        "request the import source files to verify the import path and alias are correct before proceeding\n" +
        "10. Memory discipline: do not ask for files just to be safe. Each requested file must answer a specific question " +
        "needed for THIS edit. If the question is already answered by the context, set ready=true.\n" +
        "11. SPACING in refinedChange: where you describe code snippets inline, verify every token is properly " +
        "separated by a space. 'INTERVAL15 MINUTE' is WRONG — it should be 'INTERVAL 15 MINUTE'. " +
        "Read through your output character-by-character before finalizing." +
        "12. TYPE CHAIN TRACING (CRITICAL): When the target file references a type (e.g., `FileEntry`), " +
        "you MUST read that type's definition file. If that type has properties referencing OTHER " +
        "custom types (e.g., `romMetadata?: RomMetadata`), you MUST read those type definitions too. " +
        "Do NOT assume you know the data structure — VERIFY it by reading the actual class/interface. " +
        "This is especially important when the change involves data that lives in nested type properties. " +
        "Example: if the task is about 'image previews' and the component uses FileEntry, you must read " +
        "FileEntry.ts, discover it has romMetadata?: RomMetadata, then read RomMetadata.ts to see " +
        "screenshotsJson, artworksJson, coverUrl — those are where image URLs actually live.\n" +
        "13. DATA SOURCE VERIFICATION: Before declaring ready=true, state explicitly in refinedChange " +
        "WHERE the data being modified comes from. Example: 'Images come from FileEntry.romMetadata." +
        "screenshotsJson (parsed from JSON string) and romMetadata.coverUrl, NOT from filtering " +
        "FileEntry objects by file type.' If you cannot state the data source, you are NOT ready.\n" +
        "14. SERVICE METHOD SIGNATURES (CRITICAL): If the change involves calling a service method (e.g., `this.myService.doSomething(data)`), you MUST read the service file to verify the exact method name and parameters. If the method accepts an interface/model (e.g., `UserEvent`), you MUST read that interface definition to know the exact properties required. Do NOT guess the method signature or model properties.\n" +
        "15. DEPENDENCY INJECTION SCOPE: If a service is injected into the constructor (e.g., `private userEventService: UserEventService`), you MUST call it using `this.userEventService.methodName()`. Do NOT access it via `this.parentRef?.userEventService` or other component references unless explicitly instructed.\n";

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
            await EmitLog(true, "error", "Failed to persist step status - halting to prevent data loss", new { cardId, planItemIndex, error = ex.Message });
            throw;
        }
    }

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
        catch { }
    }

    private async Task AutoAttachFileToCardAsync(string cardId, string filePath, bool emitSse, CancellationToken ct)
    {
        try
        {
            var raw = await _boardData.LoadRawAsync();
            if (raw == null) return;
            var root = JsonNode.Parse(raw)?.AsObject();
            if (root == null) return;
            var columns = root["columns"]?.AsArray();
            if (columns == null) return;
            foreach (var col in columns)
            {
                var cards = col?["cards"]?.AsArray();
                if (cards == null) continue;
                foreach (var c in cards)
                {
                    var id = c?["id"]?.GetValue<string>();
                    if (id != cardId) continue;
                    var attached = c!["attached"];
                    if (attached == null)
                        c["attached"] = new JsonArray { filePath };
                    else
                    {
                        var arr = attached.AsArray();
                        var exists = arr.Any(e => e?.GetValue<string>() == filePath);
                        if (!exists)
                            arr.Add(filePath);
                    }
                    await _boardData.SaveRawAsync(
                        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    if (emitSse)
                        await SendSse(Response, "refresh", new
                        {
                            target = "boarddata",
                            reason = "auto-attach",
                            cardId,
                            filePath
                        }, ct);
                    return;
                }
            }
        }
        catch { }
    }

    private async Task<int> ResolveAndApplyEdit(
        PlanStep step,
        string projectRoot,
        bool emitSse,
        CancellationToken ct,
        List<object> allResults,
        int stepIndex,
        string? prompt = null,
        AgentPlan? plan = null,
        int planItemIndex = -1,
        string? cardId = null,
        List<string>? attachedFiles = null,
        int replanDepth = 0)
    {
        var relPath = step.File.Replace('\\', '/');
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));

        if (!System.IO.File.Exists(fullPath) && !string.IsNullOrWhiteSpace(step.NewString) && string.IsNullOrWhiteSpace(step.OldString))
        {
            await EmitLog(emitSse, "info",
                $"⚠ Step targeted '{relPath}' which doesn't exist, but has NewString content. Treating as _create_file.", ct: ct);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

                var fileContent = step.NewString;
                var createExt = Path.GetExtension(relPath).ToLowerInvariant();

                if (createExt == ".css" || createExt == ".scss" || createExt == ".less")
                {
                    fileContent = AgentUtilities.AutoFixCssWhitespace(fileContent);
                }
                else if (createExt == ".html" || createExt == ".htm" || createExt == ".cshtml" || createExt == ".razor" || createExt == ".vue" || createExt == ".svelte")
                {
                    fileContent = AgentUtilities.AutoFixHtmlIndentation(fileContent);
                }

                await System.IO.File.WriteAllTextAsync(fullPath, fileContent, Encoding.UTF8, ct);

                var r = new Dictionary<string, object?>
                {
                    ["index"] = stepIndex,
                    ["type"] = "create",
                    ["status"] = "done",
                    ["path"] = relPath,
                    ["description"] = step.Change,
                    ["planItemIndex"] = planItemIndex
                };
                if (emitSse) await SendSse(Response, "step", r, ct);
                allResults.Add(r);
                await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct);
                return stepIndex + 1;
            }
            catch (Exception ex)
            {
                await EmitLog(emitSse, "error", $"Failed to create file {relPath}: {ex.Message}", ct: ct);
            }
        }

        var cfg8 = await LoadConfigAsync();
        var attemptScores = new List<(int attempt, int score, string reason, string failedNew)>();
        var bestScore = 0;
        var bestAttempt = -1;

        var fileExt = Path.GetExtension(relPath).ToLowerInvariant();
        var editKnowledge = await _editKnowledge.LoadAsync(projectRoot, ct);
        var filteredEditKnowledge = EditKnowledgeService.FormatForContext(
            editKnowledge, fileExt, step.Change ?? prompt ?? "");

        await EmitLog(emitSse, "info",
            $"▶ Resolving: {relPath} — {step.Change}", new { prompt, plan, stepIndex, allResults }, ct: ct);

        if (emitSse)
            await SendSse(Response, "step", new
            {
                index = stepIndex,
                type = "edit",
                status = "running",
                path = relPath,
                description = step.Change,
                planItemIndex,
                line = step.LineNumber
            }, ct);

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
                await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct);
                return stepIndex + 1;
            }
        }

        var exploration = await RunStepExplorationLoop(
            step, projectRoot,
            prompt ?? step.Change ?? "",
            plan, planItemIndex, emitSse, ct, cardId, attachedFiles);

        step = exploration.EnrichedStep;
        var explorationContext = exploration.ExplorationContext;
        if (!string.IsNullOrWhiteSpace(explorationContext))
        {
            explorationContext = await EnrichContextWithProjectTypesAndSql(
                projectRoot, relPath, step.Change, explorationContext,
                new HashSet<string>(exploration.FilesRead, StringComparer.OrdinalIgnoreCase),
                emitSse, ct);
            var typeChainContext = await EnrichWithTypeChain(
projectRoot, relPath, step.Change,
new HashSet<string>(exploration.FilesRead, StringComparer.OrdinalIgnoreCase),
emitSse, ct);
            if (!string.IsNullOrWhiteSpace(typeChainContext))
                explorationContext += typeChainContext;
        }

        if (exploration.LowConfidenceWarning != null)
        {
            await EmitLog(emitSse, "warn", $"  ⚠ {exploration.LowConfidenceWarning}", ct: ct);
            await SendSse(Response, "step", new
            {
                index = planItemIndex,
                type = "edit",
                status = "low-confidence",
                path = relPath,
                warning = exploration.LowConfidenceWarning,
                planItemIndex
            }, ct);
        }
        string? preservationDirective = null;
        if (!string.IsNullOrWhiteSpace(exploration.TargetSymbol))
        {
            preservationDirective = await AnalyzePreservationAndDependenciesAsync(
                step, projectRoot, relPath, exploration.TargetSymbol, explorationContext, emitSse, ct);
        }

        await PersistStepStatusAsync(cardId, planItemIndex, "applying", emitSse, ct);

        var history = new List<(string old, string @new, string error)>();
        var planOldStr = step.OldString;
        var planNewStr = step.NewString;
        var planOldTried = false;
        var stuckCount = 0;
        var resolveStuckCount = 0;
        var lastResolveError = "";
        var lastOld = "";
        const int MaxAttempts = 8;
        var forcedInsert = await TryForcedMethodInsertAsync(step, fullPath, relPath, explorationContext, emitSse, ct);
        if (forcedInsert.HasValue && !string.IsNullOrWhiteSpace(forcedInsert.Value.newStr))
        {
            planOldStr = forcedInsert.Value.oldStr;
            planNewStr = forcedInsert.Value.newStr;

        }

        if (System.IO.File.Exists(fullPath))
        {
            var preExtractContent = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
            var resolvedLine = AgentUtilities.ResolveTargetLineNumber(
                preExtractContent, step.Change ?? "", exploration.TargetSymbol);
            if (resolvedLine > 0)
            {
                step.LineNumber = resolvedLine;
                await EmitLog(emitSse, "info",
                    $"Corrected step.LineNumber from planner guess to resolved line {resolvedLine} via text matching", ct: ct);
            }

            if (resolvedLine > 0 && string.IsNullOrWhiteSpace(planOldStr))
            {
                var preExtracted = AgentUtilities.ExtractVerbatimTargetSection(
                    preExtractContent, step.Change ?? "", contextLines: 3, centerLine: resolvedLine);
                if (!string.IsNullOrWhiteSpace(preExtracted))
                {
                    var preLines = preExtracted.Split('\n').ToList();
                    while (preLines.Count > 0 && Regex.IsMatch(preLines[0].TrimEnd(), @"^}$"))
                        preLines.RemoveAt(0);
                    if (preLines.Count > 0 && preLines.Count <= 7)
                    {
                        var joined = string.Join("\n", preLines);
                        step.OldString = joined;
                        planOldStr = joined;
                        await EmitLog(emitSse, "info",
                            $"Pre-extracted {preLines.Count} lines from resolved line {resolvedLine} as oldString anchor (stripped leading '}}')", ct: ct);
                    }
                }
            }
        }

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            string? oldStr = null, newStr = null, resolveError = null;
            bool fullFile = false, alreadyDone = false;
            string? fullContent = null;
            bool fromFormatC = false;

            if (attempt == 0 && !string.IsNullOrWhiteSpace(planOldStr) && !planOldTried)
            {
                planOldTried = true;
                if (string.IsNullOrWhiteSpace(planNewStr))
                {
                    await EmitLog(emitSse, "info",
                        $"Plan-provided oldString is set but newString is empty — falling through to LLM resolve", ct: ct);
                    continue;
                }
                else
                {
                    oldStr = AgentUtilities.NormalizeLineEndings(planOldStr);
                    newStr = AgentUtilities.NormalizeLineEndings(planNewStr!);
                    await EmitLog(emitSse, "info",
                        $"Using plan-provided edit for {relPath}", step, ct: ct);
                }
            }
            else
            {
                if (attempt > 0)
                    await EmitLog(emitSse, "warn",
                        $"Resolve retry {attempt + 1} for {relPath}",
                        new { step, projectRoot }, ct: ct);

                (oldStr, newStr, fullFile, fullContent, alreadyDone, resolveError, fromFormatC) =
  await ResolveEditForStep(
      step, projectRoot, emitSse, ct, history,
      explorationContext: explorationContext,
      targetSymbol: exploration.TargetSymbol,
      originalPrompt: prompt,
      preservationDirective: preservationDirective,
      fullPlan: plan,
      planItemIndex: planItemIndex,
      filteredEditKnowledge: filteredEditKnowledge);

                if (resolveError == null)
                {
                    var fmt = fullFile ? "fullFile" : alreadyDone ? "alreadyDone" : fromFormatC ? "FORMAT C" : "oldString/newString";
                    var oldLen = oldStr?.Length ?? 0;
                    var newLen = newStr?.Length ?? 0;
                    await EmitLog(emitSse, "info",
                        $"  LLM produced: format={fmt}, old={oldLen}ch, new={newLen}ch", ct: ct);
                }
            }

            if (resolveError != null)
            {
                await EmitLog(emitSse, "warn",
                    $"Resolve attempt {attempt + 1}/{MaxAttempts}: {resolveError}",
                    new { resolveError, fullContent, step }, ct: ct);
                history.Add((step.OldString ?? "", step.NewString ?? "", resolveError));


                var normalizedError = Regex.Replace(resolveError ?? "", @"\(\d+ chars\)", "(N chars)")
                    .Trim();
                var normalizedLast = Regex.Replace(lastResolveError ?? "", @"\(\d+ chars\)", "(N chars)")
                    .Trim();
                if (normalizedError == normalizedLast) resolveStuckCount++;
                else { resolveStuckCount = 0; lastResolveError = resolveError; }
                if (resolveStuckCount >= 2)
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
                await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct);
                return stepIndex + 1;
            }


            if (fullFile && fullContent != null)
            {
                var fileAlreadyExists = System.IO.File.Exists(fullPath);
                var fullFileExt = Path.GetExtension(relPath).ToLowerInvariant();

                var isCssDeletion = fullFileExt is ".css" or ".scss" or ".less" &&
                    (step.Change ?? "").Contains("Remove", StringComparison.OrdinalIgnoreCase);

                var codeFileExtRestricted = fullFileExt is ".cs" or ".ts" or ".tsx" or ".js" or ".jsx";
                var allowFullFileEscalation = fileAlreadyExists && !codeFileExtRestricted &&
                    fullFileExt is not (".html" or ".htm" or ".cshtml" or ".razor" or ".vue" or ".svelte") &&
                    (history.Count >= 3 || isCssDeletion);

                if (fileAlreadyExists && !allowFullFileEscalation)
                {
                    var e = fullFileExt is ".html" or ".htm" or ".cshtml" or ".razor" or ".vue" or ".svelte"
                        ? "fullFile is BLOCKED for HTML/Angular template files — the LLM generates wrong component structure. " +
                          "Use a single unique line from the TARGET SECTION as your ENTIRE oldString (must appear only once in the file, ≥20 chars). " +
                          "Look at the ⚡ ACTUAL TARGET SECTION shown in the history above."
                        : fullFileExt == ".cs"
                            ? "fullFile is BLOCKED for existing C# files — the LLM hallucinates or truncates large C# files. " +
                              "Use a targeted oldString/newString edit. Pick a single unique line INSIDE the target class/method as your anchor. " +
                              "Do NOT include the preceding closing brace } in your oldString."
                        : "LLM incorrectly used fullFile for existing file — use oldString/newString targeted edits only";
                    await EmitLog(emitSse, "error", e, ct: ct);
                    history.Add((step.OldString ?? "", step.NewString ?? "", e));
                    continue;
                }
                if (allowFullFileEscalation)
                {
                    await EmitLog(emitSse, "warn",
                        $"⚠ Strategy escalation: accepting fullFile replacement for existing file {relPath} " +
                        $"(after {history.Count} failed oldString attempts)", ct: ct);
                }
                if (fullContent.Length > cfg8.maxFullFileTokens * 4)
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

            if (!fullFile && !string.IsNullOrWhiteSpace(newStr) &&
                (relPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                 relPath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase) ||
                 relPath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)))
            {
                var tsRelPath = Path.ChangeExtension(relPath, ".ts");
                var tsFullPath = Path.GetFullPath(Path.Combine(projectRoot, tsRelPath.Replace('/', Path.DirectorySeparatorChar)));
                if (System.IO.File.Exists(tsFullPath))
                {
                    var tsContent = await System.IO.File.ReadAllTextAsync(tsFullPath, Encoding.UTF8, ct);
                    var bindingRegex = new Regex(@"\((?:click|input|change|ngModelChange|submit|focus|blur)\)=""(\w+)\(");
                    var matches = bindingRegex.Matches(newStr);
                    var missingMethods = new List<string>();
                    foreach (Match m in matches)
                    {
                        var methodName = m.Groups[1].Value;
                        if (!string.IsNullOrWhiteSpace(methodName) &&
                            !tsContent.Contains($"{methodName}(") &&
                            !tsContent.Contains($"{methodName} =") &&
                            !tsContent.Contains($"{methodName}:"))
                        {
                            missingMethods.Add(methodName);
                        }
                    }
                    if (missingMethods.Count > 0)
                    {
                        await EmitLog(emitSse, "warn",
                            $"⚠ HTML edit references methods [{string.Join(", ", missingMethods.Distinct())}] that do not exist in {tsRelPath}. " +
                            "Auto-injecting empty stubs to prevent build breakage. These should be implemented in a follow-up step.", ct: ct);

                        var stubs = new StringBuilder();
                        foreach (var methodName in missingMethods.Distinct())
                        {
                            stubs.AppendLine($"  {methodName}() {{ }}");
                        }

                        var lastBrace = tsContent.LastIndexOf('}');
                        if (lastBrace > 0)
                        {
                            var newTsContent = tsContent.Substring(0, lastBrace) + stubs.ToString() + "\n" + tsContent.Substring(lastBrace);
                            await System.IO.File.WriteAllTextAsync(tsFullPath, newTsContent, Encoding.UTF8, ct);
                        }
                    }
                }
            }

            var fileContent = System.IO.File.Exists(fullPath)
                ? await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct)
                : string.Empty;
            string? preEditContent = null;

            if (!string.IsNullOrWhiteSpace(oldStr) && step.LineNumber > 0 && !fullFile && !fromFormatC)
            {
                var targetSection = AgentUtilities.ExtractVerbatimTargetSection(
                    fileContent, step.Change ?? "", 10, centerLine: step.LineNumber);
                if (!string.IsNullOrWhiteSpace(targetSection))
                {
                    var targetLines = new HashSet<string>(targetSection.Split('\n')
                        .Select(l => l.Trim()).Where(l => l.Length >= 8));
                    var oldContentLines = oldStr.Split('\n')
                        .Select(l => l.Trim()).Where(l => l.Length >= 8 && l != "}")
                        .ToList();
                    if (oldContentLines.Count > 0 && !oldContentLines.Any(l => targetLines.Contains(l)))
                    {
                        var err = "WRONG SECTION: None of the oldString lines appear inside the ⚡ ACTUAL TARGET SECTION. " +
                                  "Your oldString lines must be a subset of the target section shown in the prompt.";
                        await EmitLog(emitSse, "warn", $"Edit attempt {attempt + 1}/{MaxAttempts} failed for {relPath}: {err}", ct: ct);
                        history.Add((oldStr!, newStr ?? "", err));
                        continue;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(newStr) && !string.IsNullOrWhiteSpace(oldStr))
            {
                var oldLinesCount = oldStr!.Split('\n').Length;
                var oldTrimmed = oldStr.TrimStart();


                if (oldLinesCount > 3)
                {
                    var err = $"DELETION SIZE LIMIT: oldString is {oldLinesCount} lines long. When newString is empty (deletion), oldString MUST be 1-3 lines maximum. Output ONLY the exact element being deleted.";
                    await EmitLog(emitSse, "warn", $"Edit attempt {attempt + 1}/{MaxAttempts} failed for {relPath}: {err}", ct: ct);
                    history.Add((oldStr!, newStr ?? "", err));
                    if (string.Equals(AgentUtilities.NormalizeLineEndings(oldStr ?? ""), AgentUtilities.NormalizeLineEndings(lastOld), StringComparison.Ordinal)) stuckCount++;
                    else { stuckCount = 0; lastOld = AgentUtilities.NormalizeLineEndings(oldStr ?? ""); }
                    if (stuckCount >= 2) goto RecordFailure;
                    continue;
                }


                if (oldTrimmed.StartsWith("</div") || oldTrimmed.StartsWith("</label") || oldTrimmed.StartsWith("</span") ||
                    oldTrimmed.StartsWith("<div class=\"card-tags\"") || oldTrimmed.StartsWith("<div class=\"attachments\"") ||
                    oldTrimmed.StartsWith("<div class=\"card-actions\"") || oldTrimmed.StartsWith("<!--"))
                {
                    var err = "STRUCTURAL DELETION GUARD: Refusing to delete structural HTML elements (like </div>, container openings, or comments). Output ONLY the specific <span> or <label> being removed.";
                    await EmitLog(emitSse, "warn", $"Edit attempt {attempt + 1}/{MaxAttempts} failed for {relPath}: {err}", ct: ct);
                    history.Add((oldStr!, newStr ?? "", err));
                    if (string.Equals(AgentUtilities.NormalizeLineEndings(oldStr ?? ""), AgentUtilities.NormalizeLineEndings(lastOld), StringComparison.Ordinal)) stuckCount++;
                    else { stuckCount = 0; lastOld = AgentUtilities.NormalizeLineEndings(oldStr ?? ""); }
                    if (stuckCount >= 2) goto RecordFailure;
                    continue;
                }
            }

            var oldLines = oldStr?.Split('\n').Length ?? 0;
            var newLines = newStr?.Split('\n').Length ?? 0;
            var oldPreview = oldStr is { Length: > 0 }
                ? string.Join("\\n", oldStr.Split('\n').Take(2).Select(l => l.Length > 80 ? l[..80] + "…" : l))
                : "(empty)";
            var newPreview = newStr is { Length: > 0 }
                ? string.Join("\\n", newStr.Split('\n').Take(2).Select(l => l.Length > 80 ? l[..80] + "…" : l))
                : "(empty)";
            await EmitLog(emitSse, "info",
                $"Applying edit: old={oldLines}L, new={newLines}L | oldStart: {oldPreview} | newStart: {newPreview}",
                ct: ct);

            bool replaced;
            string newContent;
            string? matchError = null;
            string? snippet = null;

            if (string.IsNullOrEmpty(oldStr) && string.IsNullOrWhiteSpace(fileContent) && !string.IsNullOrWhiteSpace(newStr))
            {
                newContent = newStr;
                replaced = true;
                matchError = null;
                snippet = null;
            }
            else if (fromFormatC && !string.IsNullOrEmpty(oldStr))
            {
                var normFile = AgentUtilities.NormalizeLineEndings(fileContent);
                var normOld = AgentUtilities.NormalizeLineEndings(oldStr);
                var idx = normFile.IndexOf(normOld, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var normNew = AgentUtilities.NormalizeLineEndings(newStr ?? "");
                    newContent = normFile[..idx] + normNew + normFile[(idx + normOld.Length)..];
                    replaced = true;
                }
                else
                {
                    replaced = false;
                    newContent = fileContent;
                    matchError = "FORMAT C oldString not found in file (direct match failed)";
                }
            }
            else if (!string.IsNullOrWhiteSpace(oldStr) && !fromFormatC)
            {
                var normFile = AgentUtilities.NormalizeLineEndings(fileContent);
                var normOld = AgentUtilities.NormalizeLineEndings(oldStr).Trim('\n', '\r');
                var normNew = AgentUtilities.NormalizeLineEndings(newStr ?? "").Trim('\n', '\r');

                var fileLinesArr = normFile.Split('\n').ToList();
                var oldLinesArr = normOld.Split('\n').ToList();
                var newLinesArr = string.IsNullOrWhiteSpace(normNew)
                    ? new List<string>()
                    : normNew.Split('\n').ToList();

                int matchIdx = -1;
                var targetLineIdx = step.LineNumber > 0 ? step.LineNumber - 1 : -1;


                var allMatches = new List<int>();
                for (int i = 0; i <= fileLinesArr.Count - oldLinesArr.Count; i++)
                {
                    bool match = true;
                    for (int j = 0; j < oldLinesArr.Count; j++)
                    {
                        var fileLine = fileLinesArr[i + j].Trim();
                        var oldLine = oldLinesArr[j].Trim();
                        if (fileLine == oldLine) continue;


                        if (Regex.Replace(fileLine, @"\s+", "") == Regex.Replace(oldLine, @"\s+", ""))
                            continue;
                        match = false;
                        break;
                    }
                    if (match) allMatches.Add(i);
                }

                if (allMatches.Count == 1)
                {
                    matchIdx = allMatches[0];
                }
                else if (allMatches.Count > 1)
                {

                    var keywords = AgentUtilities.ExtractDisambiguationKeywords(step.Change);
                    if (keywords.Count > 0)
                    {
                        var bestKwScore = -1;
                        foreach (var mi in allMatches)
                        {
                            var ctxStart = Math.Max(0, mi - 3);
                            var ctxLines = fileLinesArr
                                .Skip(ctxStart)
                                .Take(mi - ctxStart + oldLinesArr.Count)
                                .ToList();
                            var ctx = string.Join("\n", ctxLines).ToLowerInvariant();
                            var score = keywords.Count(k => ctx.Contains(k));
                            if (score > bestKwScore) { bestKwScore = score; matchIdx = mi; }
                        }
                    }


                    if (matchIdx < 0 && targetLineIdx >= 0)
                    {
                        var bestDist = int.MaxValue;
                        foreach (var mi in allMatches)
                        {
                            var dist = Math.Abs(mi - targetLineIdx);
                            if (dist < bestDist) { bestDist = dist; matchIdx = mi; }
                        }
                    }


                    if (matchIdx < 0) matchIdx = allMatches[0];
                }

                if (matchIdx >= 0)
                {
                    var finalNewLines = new List<string>();
                    if (newLinesArr.Count > 0)
                    {
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
                        if (IsHtmlLikeContent(rawNew) && finalNewLines.Count > 5)
                        {
                            var stripped = finalNewLines
                                .Select(l => string.IsNullOrWhiteSpace(l) ? "" : l.TrimStart())
                                .ToList();
                            var fixedHtml = AgentUtilities.AutoIndentHtml(
                                string.Join("\n", stripped), baseIndent);
                            finalNewLines = fixedHtml.Split('\n').ToList();
                        }

                        var rawAfter = string.Join("\n", finalNewLines);
                        if ((rawAfter.Contains('{') || rawAfter.Contains('}')) &&
                            !IsHtmlLikeContent(rawAfter) && finalNewLines.Count > 2)
                        {
                            var fixedBraces = AgentUtilities.AutoIndentFromFile(
                                string.Join("\n", finalNewLines), baseIndent,
                                fileLinesArr.ToArray(), matchIdx);
                            finalNewLines = fixedBraces.Split('\n').ToList();
                        }
                    }

                    fileLinesArr.RemoveRange(matchIdx, oldLinesArr.Count);
                    fileLinesArr.InsertRange(matchIdx, finalNewLines);

                    newContent = string.Join("\n", fileLinesArr);
                    replaced = true;
                    matchError = null;
                    snippet = null;
                }
                else
                {
                    var (r, nc, me, sn) = TryReplaceSafe(fileContent, oldStr!, newStr ?? string.Empty, step.LineNumber, step.Change);
                    replaced = r; newContent = nc; matchError = me; snippet = sn;
                }
            }
            else
            {
                var (r, nc, me, sn) = TryReplaceSafe(fileContent, oldStr!, newStr ?? string.Empty, step.LineNumber, step.Change);
                replaced = r; newContent = nc; matchError = me; snippet = sn;
            }

            if (replaced && string.IsNullOrWhiteSpace(newStr) && !string.IsNullOrWhiteSpace(oldStr) &&
                            !(step.Change ?? "").Contains("remove", StringComparison.OrdinalIgnoreCase) &&
                            !(step.Change ?? "").Contains("delete", StringComparison.OrdinalIgnoreCase))
            {
                var err = "newString is empty but the step does not ask to remove/delete code. " +
                          "This would delete the matched block. Provide the replacement code in newString.";
                await EmitLog(emitSse, "warn", $"Edit attempt {attempt + 1}/{MaxAttempts} failed for {relPath}: {err}", ct: ct);
                history.Add((oldStr!, newStr ?? "", err));

                if (string.Equals(AgentUtilities.NormalizeLineEndings(oldStr ?? ""), AgentUtilities.NormalizeLineEndings(lastOld), StringComparison.Ordinal)) stuckCount++;
                else { stuckCount = 0; lastOld = AgentUtilities.NormalizeLineEndings(oldStr ?? ""); }
                if (stuckCount >= 2) goto RecordFailure;
                continue;
            }

            if (fileExt == ".ts" && !string.IsNullOrWhiteSpace(newStr) && !string.IsNullOrWhiteSpace(oldStr))
            {
                var changeLower = (step.Change ?? "").ToLowerInvariant();
                if (changeLower.Contains("method") || changeLower.Contains("handler") || changeLower.Contains("function"))
                {
                    var hasMethodDecl = Regex.IsMatch(newStr, @"\b\w+\s*\([^)]*\)\s*(\{|=>)");
                    if (!hasMethodDecl)
                    {
                        var err = "The step description asks to add methods/handlers, but the newString does not contain any method declarations (e.g., `methodName() { ... }`). " +
                                  "You MUST include the full method implementation in newString.";
                        await EmitLog(emitSse, "warn", $"Edit attempt {attempt + 1}/{MaxAttempts} failed for {relPath}: {err}", ct: ct);
                        history.Add((oldStr!, newStr ?? "", err));

                        if (string.Equals(AgentUtilities.NormalizeLineEndings(oldStr ?? ""), AgentUtilities.NormalizeLineEndings(lastOld), StringComparison.Ordinal)) stuckCount++;
                        else { stuckCount = 0; lastOld = AgentUtilities.NormalizeLineEndings(oldStr ?? ""); }
                        if (stuckCount >= 2) goto RecordFailure;
                        continue;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(oldStr) &&
                AgentUtilities.NormalizeLineEndings(oldStr) == AgentUtilities.NormalizeLineEndings(newStr ?? ""))
            {
                var checkOldStr = AgentUtilities.NormalizeLineEndings(oldStr);
                var checkFileContent = AgentUtilities.NormalizeLineEndings(fileContent);
                if (checkFileContent.Contains(checkOldStr, StringComparison.Ordinal))
                {
                    await EmitLog(emitSse, "info", $"✓ Already done (no-op): {relPath} — code already present", ct: ct);
                    var r2 = new Dictionary<string, object?>
                    {
                        ["index"] = stepIndex,
                        ["type"] = "edit",
                        ["status"] = "skipped",
                        ["path"] = relPath,
                        ["reason"] = "already done",
                        ["planItemIndex"] = planItemIndex
                    };
                    if (emitSse) await SendSse(Response, "step", r2, ct);
                    allResults.Add(r2);
                    await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct);
                    return stepIndex + 1;
                }

                await EmitLog(emitSse, "warn", $"No-op edit for {relPath}: LLM produced no change. Retrying.", ct: ct);
                history.Add((oldStr!, newStr ?? "", "LLM produced a no-op edit — oldString and newString are identical. If the step asks to REMOVE code, set newString to an empty string or empty array."));

                if (string.Equals(AgentUtilities.NormalizeLineEndings(oldStr ?? ""), AgentUtilities.NormalizeLineEndings(lastOld), StringComparison.Ordinal)) stuckCount++;
                else { stuckCount = 0; lastOld = AgentUtilities.NormalizeLineEndings(oldStr ?? ""); }
                if (stuckCount >= 2) goto RecordFailure;
                continue;
            }
            if (!fromFormatC &&
                !string.IsNullOrWhiteSpace(oldStr) && !string.IsNullOrWhiteSpace(newStr) &&
                oldStr.Length > newStr.Length * 4 &&
                !oldStr.TrimStart().StartsWith('}') &&
                oldStr.Length > 200)
            {
                var err = $"oldString ({oldStr.Length}ch, {oldLines}L) is >4x newString ({newStr.Length}ch, {newLines}L) — " +
                    "LLM likely replaced a method instead of inserting alongside it. " +
                    "Use insertion pattern: oldString = the 1-2 anchor lines right BEFORE where the new code goes, " +
                    "newString = anchor lines (unchanged) + your new code after them.";
                await EmitLog(emitSse, "warn", err, new { step }, ct: ct);
                history.Add((oldStr, newStr, err));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(oldStr) && !string.IsNullOrWhiteSpace(newStr))
            {
                var wipeReason = DetectFunctionalityWipe(
                    oldStr!, newStr!, fileContent, relPath, step.Change);

                if (wipeReason == null)
                {
                    wipeReason = AgentUtilities.DetectDuplicatePropertyAddition(oldStr!, newStr!);
                }

                if (wipeReason == null)
                {
                    wipeReason = AgentUtilities.DetectHallucinatedProperties(oldStr!, newStr!, fileContent, relPath);
                }

                if (wipeReason == null)
                {
                    wipeReason = AgentUtilities.DetectWrongSectionEdit(oldStr!, fileContent, step.Change ?? "", relPath);
                }

                if (wipeReason == null)
                {
                    wipeReason = await DetectMissingCreateTableAsync(oldStr!, newStr!, fileContent, relPath, emitSse, ct);
                }

                var changeLower = (step.Change ?? "").ToLowerInvariant();
                if (wipeReason == null && (changeLower.StartsWith("remove ") || changeLower.StartsWith("delete ")))
                {
                    var elementMatch = Regex.Match(step.Change ?? "", @"(?:remove|delete)\s+(?:the\s+)?([\w-]+)\s+(?:div|element|span|button|table|code|block|method)", RegexOptions.IgnoreCase);
                    if (elementMatch.Success)
                    {
                        var elementKeyword = elementMatch.Groups[1].Value;
                        if (!string.IsNullOrWhiteSpace(elementKeyword) &&
                            newStr!.Contains(elementKeyword, StringComparison.OrdinalIgnoreCase) &&
                            newStr.Length > oldStr!.Length)
                        {
                            wipeReason = $"ATOMIC STEP VIOLATION — Step asks to REMOVE '{elementKeyword}', but newString contains it and is longer than oldString. " +
                                         "For a REMOVE step, newString MUST be the anchor lines ONLY (the element is deleted). Do NOT add the element anywhere else in newString.";
                        }
                    }
                }

                if (wipeReason != null)
                {
                    if (wipeReason.StartsWith("SIGNATURE CHANGE", StringComparison.Ordinal))
                    {
                        var existingFn = CheckMethodExistsInFile(fileContent, newStr!);
                        if (existingFn != null)
                        {
                            await EmitLog(emitSse, "info",
                                $"✓ Already done: {relPath} — Function '{existingFn}' already exists in the file (guard detected attempted re-insertion)", ct: ct);
                            var r = new Dictionary<string, object?>
                            {
                                ["index"] = stepIndex,
                                ["type"] = "edit",
                                ["status"] = "skipped",
                                ["path"] = relPath,
                                ["reason"] = $"Function '{existingFn}' already exists in the file",
                                ["planItemIndex"] = planItemIndex
                            };
                            if (emitSse) await SendSse(Response, "step", r, ct);
                            allResults.Add(r);
                            await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct);
                            return stepIndex + 1;
                        }
                    }

                    await EmitLog(emitSse, "warn",
                        $"Guard triggered for {relPath}: {wipeReason}",
                        new
                        {
                            oldPreview = oldStr!.Length > 200 ? oldStr!.Substring(0, 200) + "..." : oldStr,
                            newPreview = newStr!.Length > 200 ? newStr!.Substring(0, 200) + "..." : newStr
                        },
                        ct: ct);
                    history.Add((oldStr!, newStr, wipeReason));

                    _ = Task.Run(async () =>
                    {
                        await _editKnowledge.RecordOutcomeAsync(projectRoot, relPath, step.Change ?? "", prompt ?? step.Change ?? "", oldStr, newStr, outcome: "abandoned", reason: wipeReason, ct);
                    }, CancellationToken.None);

                    if (string.Equals(AgentUtilities.NormalizeLineEndings(oldStr ?? ""), AgentUtilities.NormalizeLineEndings(lastOld), StringComparison.Ordinal)) stuckCount++;
                    else { stuckCount = 0; lastOld = AgentUtilities.NormalizeLineEndings(oldStr ?? ""); }
                    if (stuckCount >= 2) goto RecordFailure;
                    continue;
                }
            }

            if (!replaced)
            {
                var err = matchError ?? "oldString not found verbatim";
                if (!string.IsNullOrEmpty(snippet)) err += $". Nearby: {snippet}";

                if (step.LineNumber > 0)
                {
                    var fileLinesArr = fileContent.Split('\n');
                    var lineIdx = Math.Max(0, step.LineNumber - 1);
                    var start = Math.Max(0, lineIdx - 10);
                    var end = Math.Min(fileLinesArr.Length - 1, lineIdx + 10);
                    var actualCode = string.Join("\n", fileLinesArr.Skip(start).Take(end - start + 1));

                    err += $"\n⚠ TARGET LINE MISMATCH: The step targets line {step.LineNumber}, but your oldString was not found there. Here is the ACTUAL code around line {step.LineNumber}:\n```\n{actualCode}\n```\nCopy your oldString VERBATIM from this block.";
                }

                await EmitLog(emitSse, "warn",
                    $"Edit attempt {attempt + 1}/{MaxAttempts} failed for {relPath}: {err}",
                    new { step }, ct: ct);

                var correctedBlock = BuildExactMatchBlock(fileContent, oldStr!, step.LineNumber, step.Change);
                if (correctedBlock != null && correctedBlock != oldStr)
                {
                    var relevanceKeywords = AgentUtilities.ExtractDisambiguationKeywords(step.Change);
                    var isRelevant = relevanceKeywords.Count == 0 ||
                        relevanceKeywords.Any(k => correctedBlock.Contains(k, StringComparison.OrdinalIgnoreCase));

                    if (!isRelevant)
                    {
                        await EmitLog(emitSse, "warn",
                            $"Self-heal candidate for {relPath} shares no keywords with the change description " +
                            $"[{string.Join(", ", relevanceKeywords)}] — refusing to apply. Treating as already done.", ct: ct);
                        history.Add((oldStr!, newStr ?? "", "Self-heal candidate rejected: no relevant match found in file — target already absent."));
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(newStr) &&
                        correctedBlock.Split('\n').Length > newStr.Split('\n').Length + 4)
                    {
                        await EmitLog(emitSse, "warn",
                            $"Self-heal aborted: verbatim block ({correctedBlock.Split('\n').Length}L) is much larger than newString ({newStr.Split('\n').Length}L). LLM likely deleted code.", ct: ct);
                    }
                    else
                    {
                        await EmitLog(emitSse, "info",
                            $"Self-healing: found exact block in file (scoped to line {step.LineNumber}):\n{correctedBlock}",
                            ct: ct);

                        var corrIdx2 = fileContent.IndexOf(correctedBlock, StringComparison.Ordinal);
                        var indentNewStr = newStr ?? string.Empty;
                        if (corrIdx2 >= 0)
                        {
                            var allFileLines = fileContent.Split('\n');
                            var lineIdx2 = fileContent[..corrIdx2].Count(c => c == '\n');
                            indentNewStr = IndentReplacement(allFileLines, lineIdx2, indentNewStr);
                            indentNewStr = AgentUtilities.ReconstructFromVerbatimDiff(correctedBlock, indentNewStr);
                        }

                        var (replaced2, newContent2, _, _) =
                            TryReplaceSafe(fileContent, correctedBlock, indentNewStr, step.LineNumber, step.Change);

                        if (replaced2)
                        {
                            var (approved2, _, _) =
                                VerifyEdit(correctedBlock, newStr ?? "", fileContent, newContent2, fromFormatC);
                            if (approved2)
                            {
                                await System.IO.File.WriteAllTextAsync(
                                    fullPath, newContent2, Encoding.UTF8, ct);
                                await EmitLog(emitSse, "success",
                                    $"✓ Edited {relPath} (self-healed)", step, ct: ct);
                                newStr = correctedBlock;
                                newContent = newContent2;
                                goto AfterSelfHeal;
                            }
                        }
                    }
                }

                history.Add((oldStr!, newStr ?? "", err));

                if (string.Equals(
                    AgentUtilities.NormalizeLineEndings(oldStr ?? ""),
                    AgentUtilities.NormalizeLineEndings(lastOld),
                    StringComparison.Ordinal)) stuckCount++;
                else { stuckCount = 0; lastOld = AgentUtilities.NormalizeLineEndings(oldStr ?? ""); }
                if (stuckCount >= 2)
                {
                    await EmitLog(emitSse, "error",
                        $"LLM keeps producing the same oldString — aborting {relPath}",
                        ct: ct);
                    goto RecordFailure;
                }
                continue;
            }
            var shrinkThreshold = fromFormatC ? 0.02 : 0.1;
            if (!string.IsNullOrEmpty(newStr) && oldStr!.Length > 0 && (double)newStr.Length / oldStr!.Length < shrinkThreshold)
            {
                var err = $"newString too short ({(double)newStr.Length / oldStr.Length:P1} of oldString length) — possible content deletion";
                await EmitLog(emitSse, "warn",
                    $"Edit attempt {attempt + 1}/{MaxAttempts} failed for {relPath}: {err}", ct: ct);
                history.Add((oldStr!, newStr ?? "", err));
                if (string.Equals(
                    AgentUtilities.NormalizeLineEndings(oldStr ?? ""),
                    AgentUtilities.NormalizeLineEndings(lastOld),
                    StringComparison.Ordinal)) stuckCount++;
                else { stuckCount = 0; lastOld = AgentUtilities.NormalizeLineEndings(oldStr ?? ""); }
                if (stuckCount >= 2) goto RecordFailure;
                continue;
            }

            var newStrLines = newStr?.Split('\n') ?? Array.Empty<string>();
            for (var i = 0; i < newStrLines.Length - 1; i++)
            {
                var line = newStrLines[i];
                var trimmed = line.TrimStart();

                if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("/*"))
                    continue;

                var singleQuoteCount = 0;
                var doubleQuoteCount = 0;
                for (var j = 0; j < line.Length; j++)
                {
                    if (line[j] == '\\' && j + 1 < line.Length) { j++; continue; }
                    if (line[j] == '\'') singleQuoteCount++;
                    if (line[j] == '"') doubleQuoteCount++;
                }
                if ((singleQuoteCount % 2 != 0 || doubleQuoteCount % 2 != 0) &&
                    (line.Contains("'\\n") || line.Contains("'\\t") || line.Contains("'\\r") ||
                     line.Contains("\"\\n") || line.Contains("\"\\t") || line.Contains("\"\\r")))
                {
                    var err = "Syntax error: Unclosed string literal. You split a string containing '\\n' across multiple lines. " +
                              "If a line contains a newline character inside a string literal (e.g. `parts.join('\\n')`), " +
                              "you MUST output the `\\n` escaped inside that single array element. NEVER split a line of code across multiple array elements.";
                    await EmitLog(emitSse, "warn",
                        $"Edit attempt {attempt + 1}/{MaxAttempts} failed for {relPath}: {err}", ct: ct);
                    history.Add((oldStr!, newStr ?? "", err));
                    if (string.Equals(
                        AgentUtilities.NormalizeLineEndings(oldStr ?? ""),
                        AgentUtilities.NormalizeLineEndings(lastOld),
                        StringComparison.Ordinal)) stuckCount++;
                    else { stuckCount = 0; lastOld = AgentUtilities.NormalizeLineEndings(oldStr ?? ""); }
                    if (stuckCount >= 2) goto RecordFailure;
                    goto continueResolveLoop;
                }
            }

            var firstOldLine = oldStr?.TrimStart().Split('\n', '\r')
                .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (firstOldLine?.TrimStart().StartsWith('}') == true)
            {
                var err = $"oldString starts with '}}' — it includes the previous method's closing brace. " +
                    "Set oldString to start AT the target method declaration, not before it.";
                await EmitLog(emitSse, "warn",
                    $"Edit attempt {attempt + 1}/{MaxAttempts} failed for {relPath}: {err}", ct: ct);
                history.Add((oldStr!, newStr ?? "", err));
                if (string.Equals(
                    AgentUtilities.NormalizeLineEndings(oldStr ?? ""),
                    AgentUtilities.NormalizeLineEndings(lastOld),
                    StringComparison.Ordinal)) stuckCount++;
                else { stuckCount = 0; lastOld = AgentUtilities.NormalizeLineEndings(oldStr ?? ""); }
                if (stuckCount >= 2) goto RecordFailure;
                continue;
            }

        continueResolveLoop:;
            if (replaced && !string.IsNullOrWhiteSpace(newStr))
            {
                var fixedSqlContent = AgentUtilities.AutoFixSqlWhitespace(newContent);
                if (fixedSqlContent != newContent)
                {
                    await EmitLog(emitSse, "info", $"Pre-verify SQL fix: corrected spacing in {relPath}", ct: ct);
                    newContent = fixedSqlContent;
                    newStr = AgentUtilities.AutoFixSqlWhitespace(newStr);
                }
                if (Path.GetExtension(relPath).Equals(".py", StringComparison.OrdinalIgnoreCase))
                {
                    var fixedPyContent = AgentUtilities.AutoFixPythonStatements(newContent, relPath);
                    if (fixedPyContent != newContent)
                    {
                        await EmitLog(emitSse, "info", $"Pre-verify Python fix: split single-line statements in {relPath}", ct: ct);
                        newContent = fixedPyContent;
                        newStr = AgentUtilities.AutoFixPythonStatements(newStr, relPath);
                    }
                }

                var fixedPipeContent = Regex.Replace(newContent, @"\|\|(\d|[a-zA-Z])", "|| $1");
                if (fixedPipeContent != newContent)
                {
                    newContent = fixedPipeContent;
                    newStr = Regex.Replace(newStr ?? "", @"\|\|(\d|[a-zA-Z])", "|| $1");
                }

                var formatted = AgentUtilities.IsWhitespaceSignificant(relPath)
                    ? newContent
                    : AutoFormatEditedRegion(newContent, newStr);

                if (formatted != newContent)
                {
                    await EmitLog(emitSse, "info",
                        $"Pre-verify format: fixed spacing in {relPath}", ct: ct);
                    newContent = formatted;
                    newStr = AutoFormatEditedRegion(newStr, newStr);
                }
            }

            if ((relPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                 relPath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(newStr))
            {
                var tsRelPath2 = Path.ChangeExtension(relPath, ".ts");
                var tsFullPath2 = Path.GetFullPath(
                    Path.Combine(projectRoot, tsRelPath2.Replace('/', Path.DirectorySeparatorChar)));
                if (System.IO.File.Exists(tsFullPath2))
                {
                    var tsContent2 = await System.IO.File.ReadAllTextAsync(tsFullPath2, Encoding.UTF8, ct);
                    var definedProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (System.Text.RegularExpressions.Match m in Regex.Matches(
                        tsContent2, @"^\s*(\w+)\s*:\s*\w+(?:<[^>]*>)?\[\]?\s*=\s*(?!null|undefined)([^;]+);",
                        RegexOptions.Multiline))
                    {
                        definedProps.Add(m.Groups[1].Value);
                    }
                    foreach (System.Text.RegularExpressions.Match m in Regex.Matches(
                        tsContent2, @"^\s*(\w+)\s*:\s*\w+\s*=\s*(?:''|""""|0|false|true|new\s+\w+|\[|\{);",
                        RegexOptions.Multiline))
                    {
                        definedProps.Add(m.Groups[1].Value);
                    }

                    if (definedProps.Count > 0)
                    {
                        var fixed2 = newStr;
                        foreach (var p in definedProps)
                        {
                            fixed2 = Regex.Replace(fixed2,
                                $@"\b{p}\?\s*\.\s*",
                                $"{p}.",
                                RegexOptions.IgnoreCase);
                        }
                        if (fixed2 != newStr)
                        {
                            await EmitLog(emitSse, "info",
                                $"Stripped unnecessary ?. from defined properties in {relPath}", ct: ct);
                            newStr = fixed2;
                            var (replaced3, newContent3, _, _) =
                                TryReplaceSafe(fileContent, oldStr!, fixed2, step.LineNumber, step.Change);
                            if (replaced3)
                            {
                                newContent = newContent3;
                                await System.IO.File.WriteAllTextAsync(
                                    fullPath, newContent3, Encoding.UTF8, ct);
                            }
                        }
                    }
                }
            }

            var (approved, verifyReason, _) =
                (string.IsNullOrEmpty(oldStr) && string.IsNullOrWhiteSpace(fileContent))
                ? (true, "Bypassed verify for empty file insertion", 100)
                : VerifyEdit(oldStr!, newStr ?? "", fileContent, newContent, fromFormatC);

            if (!approved && verifyReason.Contains("SQL whitespace collapsed", StringComparison.OrdinalIgnoreCase))
            {
                var correctedContent = AgentUtilities.AutoFixSqlWhitespace(newContent);
                if (correctedContent != newContent)
                {
                    var correctedNewStr = AgentUtilities.AutoFixSqlWhitespace(newStr ?? "");
                    (approved, verifyReason, _) =
                        VerifyEdit(oldStr!, correctedNewStr, fileContent, correctedContent, fromFormatC);
                    if (approved)
                    {
                        newContent = correctedContent;
                        newStr = correctedNewStr;
                        await EmitLog(emitSse, "info",
                            $"SQL whitespace auto-corrected in {relPath}", ct: ct);
                    }
                    else if (verifyReason.Contains("identical", StringComparison.OrdinalIgnoreCase))
                    {
                        verifyReason =
                            "SQL whitespace auto-fix made your newCode IDENTICAL to the existing code — " +
                            "you reproduced the original method body without implementing the new functionality. " +
                            "Write a DIFFERENT method body that adds the logic described in CHANGE REQUIRED.";
                        newStr = correctedNewStr;
                    }
                }
            }

            if (!approved)
            {
                await EmitLog(emitSse, "warn", $"Verify failed for {relPath}: {verifyReason}", ct: ct);
                history.Add((oldStr!, newStr ?? "", verifyReason));

                var isIdenticalError =
                    verifyReason.Contains("IDENTICAL to the existing code", StringComparison.OrdinalIgnoreCase) ||
                    verifyReason.Contains("identical after normalization", StringComparison.OrdinalIgnoreCase);

                var trackBy = isIdenticalError
                    ? AgentUtilities.NormalizeLineEndings(newStr ?? "")
                    : AgentUtilities.NormalizeLineEndings(oldStr ?? "");

                if (string.Equals(trackBy, AgentUtilities.NormalizeLineEndings(lastOld), StringComparison.Ordinal))
                {
                    stuckCount++;
                }
                else
                {
                    stuckCount = 0;
                    lastOld = trackBy;
                }

                if (stuckCount >= 2) { goto RecordFailure; }
                continue;
            }

            if (!string.IsNullOrWhiteSpace(newStr)
                && !newContent.Contains(AgentUtilities.NormalizeLineEndings(newStr), StringComparison.Ordinal))
            {
                var trimmedNew = string.Join("\n",
                    AgentUtilities.StripLineLeadingWhitespace(AgentUtilities.NormalizeLineEndings(newStr))
                        .Split('\n').Select(l => l.TrimEnd()));
                var trimmedContent = string.Join("\n",
                    AgentUtilities.StripLineLeadingWhitespace(newContent)
                        .Split('\n').Select(l => l.TrimEnd()));
                if (!trimmedContent.Contains(trimmedNew, StringComparison.Ordinal))
                {
                    var verr = "Replacement produced mismatched content — " +
                               "oldString matched wrong location";
                    await EmitLog(emitSse, "warn",
                        $"Verify failed for {relPath}: {verr}", step, ct: ct);
                    history.Add((oldStr!, newStr, verr));

                    if (string.Equals(
                        AgentUtilities.NormalizeLineEndings(oldStr ?? ""),
                        AgentUtilities.NormalizeLineEndings(lastOld),
                        StringComparison.Ordinal)) stuckCount++;
                    else { stuckCount = 0; lastOld = AgentUtilities.NormalizeLineEndings(oldStr ?? ""); }
                    if (stuckCount >= 2) goto RecordFailure;
                    continue;
                }
            }

            if (Path.GetExtension(relPath).Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(relPath).Equals(".tsx", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(relPath).Equals(".js", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(relPath).Equals(".jsx", StringComparison.OrdinalIgnoreCase))
            {
                newContent = NormalizeTypeScriptObjectLiterals(newContent);
            }

            if (Path.GetExtension(relPath).Equals(".py", StringComparison.OrdinalIgnoreCase))
            {
                var pyKeywords = "print|return|if|for|while|def|class|import|from|with|try|except|finally|raise|yield|assert|del|global|nonlocal|pass|break|continue";
                newContent = Regex.Replace(newContent, $@"\)\s+({pyKeywords})\b", ")\n$1");
                if (!string.IsNullOrWhiteSpace(newStr))
                {
                    newStr = Regex.Replace(newStr, $@"\)\s+({pyKeywords})\b", ")\n$1");
                }
            }

            var ext = Path.GetExtension(relPath).ToLowerInvariant();
            if (ext == ".css" || ext == ".scss" || ext == ".less")
            {
                newContent = AgentUtilities.AutoFixCssWhitespace(newContent);
            }

            preEditContent = fileContent;
            await SaveEditWithUndoAsync(fullPath, newContent, relPath, projectRoot, preEditContent, ct);

            if (fileExt == ".cs" && !string.IsNullOrWhiteSpace(newStr))
            {
                var missing = ScanMissingTypes(newContent, newStr);
                var stubsToAdd = new List<string>();
                foreach (var t in missing)
                {
                    var definition = FindTypeDefinitionInContext(t, explorationContext ?? "");
                    if (definition != null)
                    {
                        stubsToAdd.Add(definition);
                        continue;
                    }
                    if (t.EndsWith("Dto", StringComparison.OrdinalIgnoreCase) ||
                        t.EndsWith("DTO", StringComparison.Ordinal))
                        continue;
                    stubsToAdd.Add($"public class {t}\n{{\n}}");
                }
                if (stubsToAdd.Count > 0)
                {
                    newContent += "\n" + string.Join("\n\n", stubsToAdd);
                    await SaveEditWithUndoAsync(
                        fullPath, newContent, relPath, projectRoot, preEditContent, ct);
                    await EmitLog(emitSse, "info",
                        $"Appended missing type(s): {string.Join(", ", stubsToAdd.Select(s => ExtractTypeNameForLog(s)))}", ct: ct);
                }
            }

            if (fileExt == ".cs")
            {
                var writtenContent = System.IO.File.ReadAllText(fullPath, Encoding.UTF8);

                var beforeErrors = CountRoslynErrors(writtenContent);

                var fixedContent = AgentUtilities.PostEditCSharpFixup(writtenContent);
                if (fixedContent != writtenContent)
                {
                    var afterErrors = CountRoslynErrors(fixedContent);
                    if (afterErrors > beforeErrors)
                    {
                        await EmitLog(emitSse, "warn",
                            $"Post-edit fixup would introduce {afterErrors - beforeErrors} new error(s) in {relPath} — skipping fixup", ct: ct);
                        await EmitLog(emitSse, "warn",
                            $"  Before: {beforeErrors} errors, After: {afterErrors} errors", ct: ct);
                    }
                    else
                    {
                        await SaveEditWithUndoAsync(fullPath, fixedContent, relPath, projectRoot, preEditContent, ct);
                        newContent = fixedContent;
                        await EmitLog(emitSse, "info",
                            $"Post-edit fixup applied to {relPath} (verbatim escapes / DTO wrappers / doubled braces)", ct: ct);
                    }
                }
            }

            if (fileExt == ".cs" && !string.IsNullOrWhiteSpace(newContent))
            {
                try
                {
                    var syntaxTree = CSharpSyntaxTree.ParseText(newContent);
                    var diagnostics = syntaxTree.GetDiagnostics()
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Take(10)
                        .ToList();
                    if (diagnostics.Count > 0)
                    {
                        var errorLines = diagnostics
                            .Select(d => $"  L{d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}")
                            .ToList();
                        await EmitLog(emitSse, "warn",
                            $"Roslyn syntax errors in {relPath} after edit ({diagnostics.Count} found):" +
                            Environment.NewLine + string.Join(Environment.NewLine, errorLines), ct: ct);
                    }
                }
                catch (Exception ex)
                {
                    await EmitLog(emitSse, "warn",
                        $"Roslyn parse failed for {relPath}: {ex.Message}", ct: ct);
                }
            }

            if (!string.IsNullOrWhiteSpace(newStr) &&
                (fileExt is ".css" or ".scss" or ".less"))
            {
                var (mergedCss, mergeWarnings) = MergeDuplicateCssRules(newContent);
                if (mergedCss != newContent)
                {
                    newContent = mergedCss;
                    foreach (var w in mergeWarnings)
                        await EmitLog(emitSse, "warn", w, ct: ct);
                    await EmitLog(emitSse, "info",
                        $"Merged duplicate CSS selectors in {relPath}", ct: ct);
                }
            }

            if (!string.IsNullOrWhiteSpace(newStr) &&
                (fileExt is ".css" or ".scss" or ".less"))
            {
                var beforeCssFmt = newContent;
                newContent = FormatCssEditedRegion(newContent, newStr);
                if (newContent != beforeCssFmt)
                {
                    await System.IO.File.WriteAllTextAsync(fullPath, newContent, Encoding.UTF8, ct);
                    await EmitLog(emitSse, "info",
                        $"CSS region formatted: fixed property spacing/indentation in {relPath}", ct: ct);
                }
            }

            if (!string.IsNullOrWhiteSpace(newStr) && !fromFormatC &&
                (fileExt is ".ts" or ".tsx" or ".js" or ".jsx" or ".cs"
                  or ".css" or ".scss" or ".less" or ".json"))
            {
                var beforeAutoFmt = newContent;
                newContent = AutoFormatEditedRegion(newContent, newStr);
                if (newContent != beforeAutoFmt)
                {
                    await System.IO.File.WriteAllTextAsync(fullPath, newContent, Encoding.UTF8, ct);
                    await EmitLog(emitSse, "info",
                        $"Auto-formatted edited region in {relPath} (commas/colons/semicolons/equals + closing-dedent)", ct: ct);
                }
            }

            if (!string.IsNullOrWhiteSpace(newStr) && !fromFormatC &&
                (fileExt is ".ts" or ".tsx" or ".js" or ".jsx" or ".html" or ".css" or ".scss" or ".less"))
            {
                newContent = await PostEditStyleFixAsync(fullPath, relPath, newContent, newStr, emitSse, ct);
            }

            if (!string.IsNullOrWhiteSpace(newStr) && !fromFormatC)
            {
                var fileLines = newContent.Split('\n');
                var changed = false;
                foreach (var nLine in newStr.Split('\n'))
                {
                    var trimmed = nLine.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed)) continue;
                    var trimmedLower = trimmed.ToLowerInvariant();
                    var isCommentOrString = trimmedLower.StartsWith("//") || trimmedLower.StartsWith("/*") ||
                        trimmedLower.StartsWith("*") || trimmedLower.StartsWith("'") || trimmedLower.StartsWith("\"");
                    if (isCommentOrString) continue;
                    for (var i = 0; i < fileLines.Length; i++)
                    {
                        if (fileLines[i].Contains(trimmed, StringComparison.Ordinal))
                        {
                            var before = fileLines[i];
                            var fixedLine = Regex.Replace(fileLines[i], @"\b(\w+)\s+\(", "$1(");
                            var stillComment = fixedLine.TrimStart().StartsWith("//") || fixedLine.TrimStart().StartsWith("/*") || fixedLine.TrimStart().StartsWith("*");
                            if (stillComment) break;
                            if (fixedLine != before)
                            {
                                fileLines[i] = fixedLine;
                                changed = true;
                            }
                            break;
                        }
                    }
                }
                if (changed)
                {
                    newContent = string.Join("\n", fileLines);
                    await System.IO.File.WriteAllTextAsync(fullPath, newContent, Encoding.UTF8, ct);
                }
            }

        AfterSelfHeal:
            if (!string.IsNullOrWhiteSpace(newStr) && !string.IsNullOrWhiteSpace(preEditContent) && !fromFormatC)
            {
                const int VerificationRounds = 3;
                var decisions = new List<string>();
                var reasons = new List<string>();
                var scores = new List<int>();

                for (int r = 0; r < VerificationRounds; r++)
                {
                    var (d, reason, score) = await LlmVerifyEditStepAsync(
                        relPath, prompt ?? step.Change ?? "", step.Change ?? "",
                        oldStr!, newStr!, preEditContent, newContent, emitSse, ct,
                        priorAttempts: attemptScores.Count > 0
                            ? attemptScores.Select(a => (a.score, a.reason, a.failedNew)).ToList()
                            : null,
                        explorationContext: explorationContext,
                        fullPlan: plan,
                        currentStepIndex: planItemIndex);
                    decisions.Add(d);
                    reasons.Add(reason);
                    scores.Add(score);
                }

                var keepCount = 0;
                var scoreSum = 0;
                for (int r = 0; r < VerificationRounds; r++)
                {
                    if (decisions[r] == "keep") keepCount++;
                    scoreSum += scores[r];
                }
                var avgScore = scoreSum / VerificationRounds;
                var llmGateDecision = keepCount >= 2 ? "keep" : "abandon";
                var truncatedReasons = new List<string>(reasons.Count);
                for (int r = 0; r < reasons.Count; r++)
                    truncatedReasons.Add(reasons[r].Length > 80 ? reasons[r][..80] + "…" : reasons[r]);
                var llmGateReason = $"Rounds: [{string.Join(", ", decisions)}] " +
                    $"scores [{string.Join(", ", scores)}] — final: {llmGateDecision} (avg {avgScore}/100). " +
                    $"Reasons: [{string.Join(" | ", truncatedReasons)}]";
                var llmGateScore = avgScore;

                attemptScores.Add((attempt + 1, llmGateScore, llmGateReason, newStr));

                if (llmGateScore > bestScore)
                {
                    bestScore = llmGateScore;
                    bestAttempt = attempt;
                }

                await EmitLog(emitSse, "info",
                    $"  📊 Attempt {attempt + 1} multi-round: [{string.Join(", ", scores)}] avg: {avgScore}/100 (best so far: {bestScore}/100) — {llmGateDecision}",
                    new { attempt = attempt + 1, scores, averageScore = avgScore, decision = llmGateDecision, reason = llmGateReason },
                    ct: ct);

                if (llmGateDecision == "abandon" && plan?.Plan?.Count > 0 && planItemIndex >= 0)
                {
                    var methodMatches = Regex.Matches(llmGateReason,
                        @"(?:'([a-zA-Z]\w+)'|`([a-zA-Z]\w+)`|method\s+'?([a-zA-Z]\w+)'?)",
                        RegexOptions.IgnoreCase);
                    var mentionedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (System.Text.RegularExpressions.Match m in methodMatches)
                    {
                        for (int g = 1; g < m.Groups.Count; g++)
                        {
                            if (m.Groups[g].Success)
                                mentionedNames.Add(m.Groups[g].Value);
                        }
                    }

                    if (mentionedNames.Count > 0)
                    {
                        for (int i = planItemIndex + 1; i < plan.Plan.Count; i++)
                        {
                            var futureStep = plan.Plan[i];
                            if (mentionedNames.Any(n =>
                                futureStep.Change?.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                await EmitLog(emitSse, "info",
                                    $"  🔄 LLM abandon reason mentions methods [{string.Join(", ", mentionedNames)}] " +
                                    $"that appear in future step {i + 1}: {futureStep.Change} — " +
                                    $"overriding to KEEP",
                                    ct: ct);
                                llmGateDecision = "keep";
                                llmGateScore = Math.Max(llmGateScore, 70);
                                break;
                            }
                        }
                    }
                }

                if (llmGateDecision == "abandon")
                {
                    await SaveEditWithUndoAsync(fullPath, preEditContent, relPath, projectRoot, preEditContent, ct);

                    await EmitLog(emitSse, "warn",
                        $"⟲ LLM verify: ABANDON edit on {relPath} (score {llmGateScore}/100) — {llmGateReason}. " +
                        $"Reverted to pre-edit state; retrying. " +
                        $"Prior attempts: {attemptScores.Count}, best score: {bestScore}/100",
                        new { step, reason = llmGateReason, score = llmGateScore, bestScore, attemptScores }, ct: ct);

                    if (emitSse)
                    {
                        await SendSse(Response, "step", new
                        {
                            index = stepIndex,
                            type = "edit",
                            status = "verify-abandoned",
                            path = relPath,
                            reason = llmGateReason,
                            score = llmGateScore,
                            bestScore,
                            attempt = attempt + 1,
                            planItemIndex
                        }, ct);
                    }

                    var abandonError =
                        $"LLM verify ABANDONED (score {llmGateScore}/100): {llmGateReason}\n" +
                        $"═══ FAILED CODE THAT WAS REVERTED (score {llmGateScore}/100) ═══\n" +
                        $"{TruncateForLlm(newStr, 600)}\n" +
                        $"═══ END FAILED CODE ═══\n" +
                        $"DO NOT reproduce this code. It scored {llmGateScore}/100 because: {llmGateReason}.\n" +
                        $"Try a DIFFERENT approach. ";

                    if (llmGateReason.Contains("signature", StringComparison.OrdinalIgnoreCase))
                        abandonError += "PRESERVE the original method signature (return type, name, parameters). Only change the BODY.";
                    else if (llmGateReason.Contains("cache", StringComparison.OrdinalIgnoreCase) ||
                             llmGateReason.Contains("guard", StringComparison.OrdinalIgnoreCase))
                        abandonError += "PRESERVE all cache/guard lines (if/return/map.has/map.get/map.set). Only add NEW logic alongside them.";
                    else if (llmGateReason.Contains("invent", StringComparison.OrdinalIgnoreCase) ||
                             llmGateReason.Contains("undefined", StringComparison.OrdinalIgnoreCase) ||
                             llmGateReason.Contains("not exist", StringComparison.OrdinalIgnoreCase))
                        abandonError += "Use ONLY methods/properties that already exist in the file. Do NOT invent new identifiers.";
                    else
                        abandonError += $"Address this specific issue: {llmGateReason}";

                    if (attemptScores.Count > 0)
                    {
                        var trend = attemptScores.Count >= 2 && llmGateScore > attemptScores[^2].score
                            ? "↑ improving"
                            : attemptScores.Count >= 2 && llmGateScore < attemptScores[^2].score
                                ? "↓ getting worse — change strategy significantly"
                                : "→ stagnant — try a fundamentally different approach";
                        abandonError += $"\nScore trend: {trend}. Best so far: {bestScore}/100 on attempt {bestAttempt + 1}.";
                    }

                    history.Add((oldStr!, newStr, abandonError));

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _editKnowledge.RecordOutcomeAsync(projectRoot, relPath, step.Change ?? "", prompt ?? step.Change ?? "",
                             oldStr, newStr, outcome: "abandoned", reason: $"LLM verify (score {llmGateScore}): {llmGateReason}", ct);
                        }
                        catch { }
                    }, CancellationToken.None);

                    if (string.Equals(
                        AgentUtilities.NormalizeLineEndings(oldStr ?? ""),
                        AgentUtilities.NormalizeLineEndings(lastOld),
                        StringComparison.Ordinal)) stuckCount++;
                    else { stuckCount = 0; lastOld = AgentUtilities.NormalizeLineEndings(oldStr ?? ""); }

                    if (attemptScores.Count >= 3)
                    {
                        var last3 = attemptScores.TakeLast(3).Select(a => a.score).ToList();
                        var allLow = last3.All(s => s < 40);
                        var noImprovement = last3.Distinct().Count() <= 2;
                        if (allLow && noImprovement)
                        {
                            await EmitLog(emitSse, "warn",
                                $"Score stagnation detected: last 3 attempts scored [{string.Join(", ", last3)}] — " +
                                $"entering replanning cycle with failure context",
                                ct: ct);
                            goto RecordFailure;
                        }
                    }

                    if (stuckCount >= 3)
                    {
                        await EmitLog(emitSse, "error",
                            $"LLM verify abandoned {stuckCount}x in a row for {relPath} — treating as failure",
                            ct: ct);
                        goto RecordFailure;
                    }
                    continue;
                }
                else if (llmGateDecision == "keep")
                {
                    await EmitLog(emitSse, "success",
                        $"✓ LLM verify: KEEP edit on {relPath} — score {llmGateScore}/100 — {llmGateReason}",
                        ct: ct);
                    if (emitSse)
                    {
                        await SendSse(Response, "step", new
                        {
                            index = stepIndex,
                            type = "edit",
                            status = "verify-kept",
                            path = relPath,
                            reason = llmGateReason,
                            score = llmGateScore,
                            planItemIndex
                        }, ct);
                    }
                }
                else
                {
                    await EmitLog(emitSse, "warn",
                        $"⚠ LLM verify returned error (defaulting to keep): {llmGateReason}", ct: ct);
                }
            }

            var successReason = "";
            if (attempt > 0 && history.Count > 0)
            {
                var lastFailure = history[history.Count - 1];
                var failSummary = lastFailure.error;
                if (failSummary.Length > 200) failSummary = failSummary[..200] + "…";
                successReason = $"Succeeded on attempt {attempt + 1} after {history.Count} failure(s). " +
                                $"Last failure: {failSummary}. " +
                                $"Strategy that worked: {(attempt == 1 ? "VERBATIM_COPY" : attempt == 2 ? "SINGLE_LINE_ANCHOR" : "LINE_RANGE_REPLACEMENT")}.";
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await _editKnowledge.RecordOutcomeAsync(
                        projectRoot, relPath, step.Change ?? "", prompt ?? step.Change ?? "",
                        oldStr, newStr, outcome: "success", reason: successReason, ct);
                }
                catch { }
                try
                {
                    await _editKnowledge.UpdateArchitectureAsync(
                        projectRoot, relPath, newStr ?? "", ct);
                }
                catch { }
            }, CancellationToken.None);

            await EmitLog(emitSse, "success", $"✓ Edited {relPath}", ct: ct);
            var result = new Dictionary<string, object?>();
            PopulateEditResult(result, "modified", relPath, oldStr, newStr ?? "", "");
            result["index"] = stepIndex; result["planItemIndex"] = planItemIndex;
            if (emitSse) await SendSse(Response, "step", result, ct);
            allResults.Add(result);
            await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct);

            if (fileExt == ".cs" && !string.IsNullOrWhiteSpace(oldStr) && !string.IsNullOrWhiteSpace(newStr))
            {
                stepIndex = await HandleMethodSignatureChange(
                    fullPath, relPath, oldStr, newStr, projectRoot,
                    emitSse, ct, stepIndex, allResults, cardId);
            }

            return stepIndex + 1;
        }

    RecordFailure:
        var lastErr = history.Count > 0 ? history[^1].error : "resolve failed";

        var failureSummary = new StringBuilder();
        failureSummary.AppendLine($"Step failed after {history.Count} attempts on {relPath}");
        failureSummary.AppendLine($"Step description: {step.Change}");
        failureSummary.AppendLine($"Final error: {lastErr}");

        if (attemptScores.Count > 0)
        {
            failureSummary.AppendLine($"\nAttempt score history:");
            foreach (var a in attemptScores)
            {
                failureSummary.AppendLine($"  Attempt {a.attempt}: score={a.score}/100 — {a.reason}");
            }
            failureSummary.AppendLine($"Best score achieved: {bestScore}/100 on attempt {bestAttempt + 1}");
        }

        failureSummary.AppendLine($"\nFailed code snippets (reverted — do NOT reproduce):");
        foreach (var a in attemptScores.TakeLast(3))
        {
            failureSummary.AppendLine($"--- Attempt {a.attempt} (score {a.score}/100): {a.reason} ---");
            failureSummary.AppendLine("```");
            failureSummary.AppendLine(TruncateForLlm(a.failedNew, 500));
            failureSummary.AppendLine("```");
        }

        var failureContext = failureSummary.ToString();

        await EmitLog(emitSse, "warn",
            $"Step failure summary for replanning:\n{failureContext}", ct: ct);

        _ = Task.Run(async () =>
        {
            try
            {
                await _editKnowledge.RecordOutcomeAsync(
                    projectRoot, step.File, step.Change ?? "", prompt ?? step.Change ?? "",
                    step.OldString, step.NewString,
                    outcome: "failure", reason: $"{lastErr}\n\n{failureContext}", ct);
            }
            catch { }
        }, CancellationToken.None);

        if (replanDepth > 0)
        {
            await EmitLog(emitSse, "error",
                $"✗ FATAL: Replan step failed (depth {replanDepth}) — aborting {relPath}: {lastErr}",
                new { failureContext, attemptScores }, ct: ct);

            var failDepth = new Dictionary<string, object?>
            {
                ["index"] = stepIndex,
                ["type"] = "edit",
                ["status"] = "error",
                ["path"] = relPath,
                ["error"] = lastErr,
                ["planItemIndex"] = planItemIndex,
                ["failureContext"] = failureContext,
                ["attemptScores"] = attemptScores.Select(a => new { a.attempt, a.score, a.reason }).ToList(),
                ["bestScore"] = bestScore,
                ["replanAttempts"] = 0
            };
            if (emitSse) await SendSse(Response, "step", failDepth, ct);
            allResults.Add(failDepth);
            await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct);

            throw new StepFatalException(
                $"Replan step failed after {history.Count} attempts: {relPath} — {lastErr}",
                relPath,
                step.Change ?? "",
                failureContext);
        }

        var replanAttempts = 0;
        const int MaxReplanAttempts = 2;

        while (replanAttempts < MaxReplanAttempts)
        {
            replanAttempts++;

            await EmitLog(emitSse, "info",
                $"🔄 Replanning cycle {replanAttempts}/{MaxReplanAttempts} for {relPath} — " +
                $"feeding failure context back to planner…", ct: ct);

            var replanSteering =
                $"PREVIOUS APPROACH FAILED after {attemptScores.Count} attempts. " +
                $"Best score: {bestScore}/100.\n\n" +
                $"FAILURE CONTEXT:\n{failureContext}\n\n" +
                $"You MUST take a FUNDAMENTALLY DIFFERENT approach. " +
                $"The code snippets above were tried and rejected — do NOT reproduce them. " +
                $"Consider:\n" +
                $"  - Using a smaller, more targeted edit (1-3 lines instead of a full method rewrite)\n" +
                $"  - Using oldString/newString instead of FORMAT C (or vice versa)\n" +
                $"  - Editing a different part of the file that achieves the same goal\n" +
                $"  - Breaking the change into a simpler, smaller edit\n" +
                $"Score your new plan 85+ only if it addresses the specific failure reasons above.";

            var replanSteps = await GenerateReplanStepsAsync(
                prompt ?? step.Change ?? "", allResults, plan,
                replanSteering, projectRoot, emitSse, ct,
                attachedFiles: attachedFiles,
                qualityCheckReason: failureContext);

            if (replanSteps == null || replanSteps.Count == 0)
            {
                await EmitLog(emitSse, "warn", $"Replan cycle {replanAttempts} returned no steps", ct: ct);
                continue;
            }

            await EmitLog(emitSse, "info", $"Replan cycle {replanAttempts} generated {replanSteps.Count} new step(s): " +
                string.Join(" | ", replanSteps.Select(s => s.Change)), ct: ct);

            replanSteps = await PruneIrrelevantPlanStepsAsync(replanSteps, projectRoot, ct);

            var replanResults = new List<object>();
            foreach (var replanStep in replanSteps)
            {
                var replanStepIndex = stepIndex;
                try
                {
                    replanStepIndex = await ResolveAndApplyEdit(
                        replanStep, projectRoot, emitSse, ct,
                        replanResults, replanStepIndex,
                        prompt, plan, planItemIndex, cardId, attachedFiles,
                        replanDepth + 1);
                }
                catch (StepFatalException)
                { }
            }

            var hasSuccess = replanResults.OfType<Dictionary<string, object?>>()
                .Any(r => r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created");

            if (hasSuccess)
            {
                await EmitLog(emitSse, "success",
                    $"✓ Replan cycle {replanAttempts} succeeded for {relPath}", ct: ct);
                allResults.AddRange(replanResults);
                await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct);
                return stepIndex + replanResults.Count;
            }

            failureContext = $"Replan attempt {replanAttempts} also failed.\n" + failureContext;
            allResults.AddRange(replanResults);
        }

        await EmitLog(emitSse, "error",
            $"✗ FATAL: All resolve attempts AND {MaxReplanAttempts} replan cycles failed for {relPath}: {lastErr}",
            new { failureContext, attemptScores }, ct: ct);

        var fail = new Dictionary<string, object?>
        {
            ["index"] = stepIndex,
            ["type"] = "edit",
            ["status"] = "error",
            ["path"] = relPath,
            ["error"] = lastErr,
            ["planItemIndex"] = planItemIndex,
            ["failureContext"] = failureContext,
            ["attemptScores"] = attemptScores.Select(a => new { a.attempt, a.score, a.reason }).ToList(),
            ["bestScore"] = bestScore,
            ["replanAttempts"] = MaxReplanAttempts
        };
        if (emitSse) await SendSse(Response, "step", fail, ct);
        allResults.Add(fail);

        await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct);

        throw new StepFatalException(
            $"Step failed after {history.Count} attempts and {MaxReplanAttempts} replan cycles: {relPath} — {lastErr}",
            relPath,
            step.Change ?? "",
            failureContext);
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
            await EmitLog(true, "error", "Failed to persist full plan to boarddata - halting to prevent data loss", new { cardId, error = ex.Message });
            throw;
        }
    }

    private async Task<string> PostEditStyleFixAsync(
        string fullPath, string relPath, string content, string appliedNewStr,
        bool emitSse, CancellationToken ct)
    {
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        if (ext == ".html" || ext == ".htm")
            return content;

        var hasSpacingIssue = false;
        var needleLines = appliedNewStr.Split('\n');
        var fileLines = content.Split('\n');
        var excerptStart = -1;
        var excerptEnd = -1;
        foreach (var nLine in needleLines)
        {
            var trimmed = nLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            for (var i = 0; i < fileLines.Length; i++)
            {
                if (fileLines[i].Contains(trimmed, StringComparison.Ordinal))
                {
                    if (excerptStart < 0 || i < excerptStart) excerptStart = i;
                    if (i > excerptEnd) excerptEnd = i;
                    if (Regex.IsMatch(fileLines[i], @"\(\w+\s*[+\-*/%]\d") ||
                        Regex.IsMatch(fileLines[i], @"\d\s*[+\-*/%]\s*\d") ||
                        Regex.IsMatch(fileLines[i], @"(?<![=!<>])=(?!=)\d") ||
                        Regex.IsMatch(fileLines[i], @"[\w\)\]]\s*[+*/%<>]\s*\d") ||
                        Regex.IsMatch(fileLines[i], @"\d\s*[+\-*/%<>]\s*[\w\(]") ||
                        Regex.IsMatch(fileLines[i], @"[\w\)]\s*[<>]\s*[\w\(]") ||
                        Regex.IsMatch(fileLines[i], @"[\w\)]\s*-\s*\w") && !fileLines[i].Contains("-="))
                    {
                        hasSpacingIssue = true;
                    }
                    break;
                }
            }
        }
        if (!hasSpacingIssue || excerptStart < 0)
            return content;

        var contextWindowStart = Math.Max(0, excerptStart - 3);
        var contextWindowEnd = Math.Min(fileLines.Length, excerptEnd + 4);
        var excerpt = string.Join("\n", fileLines[contextWindowStart..contextWindowEnd]);

        var sysPrompt = "You are a meticulous code formatter. Fix spacing issues in the code excerpt below: " +
                        "ensure proper spacing around operators (+, -, *, /, %, =, etc.) and colons in " +
                        "TypeScript/JavaScript/CSS. Output ONLY a JSON object with an array of fixes: " +
                        "{\"fixes\":[{\"oldString\":\"...\",\"newString\":\"...\"}]}. " +
                        "Each fix must be an exact substring from the excerpt. Do NOT change logic or add/remove code. " +
                        "CRITICAL RULE: NEVER add a space between a function/method name and its opening parenthesis. " +
                        "`delete(optionsFile)` is CORRECT; `delete (optionsFile)` is WRONG. " +
                        "`myFunc()` is CORRECT; `myFunc ()` is WRONG. " +
                        "DO NOT add spaces after keywords if they are immediately followed by '(' for a function call. " +
                        "DO NOT modify text inside HTML attribute values.";

        var userMsg = $"### FILE ###\n{relPath}\n\n### EXCERPT WITH SPACING ISSUES ###\n```\n{excerpt}\n```\n\n" +
                      "Fix spacing issues. Return JSON with oldString/newString pairs.";

        var (raw, _, error) = await CallLlmRawStreaming(sysPrompt, userMsg, emitSse, ct,
            requestTimeout: TimeSpan.FromMinutes(2), maxTokens: 1024);

        if (string.IsNullOrWhiteSpace(raw))
            return content;

        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```")) cleaned = cleaned.TrimStart('`').Trim();
            var fb = cleaned.IndexOf('{');
            var lb = cleaned.LastIndexOf('}');
            if (fb >= 0 && lb > fb) cleaned = cleaned[fb..(lb + 1)];

            using var doc = JsonDocument.Parse(cleaned);
            if (!doc.RootElement.TryGetProperty("fixes", out var fixesArr) || fixesArr.ValueKind != JsonValueKind.Array)
                return content;

            var fixedContent = content;
            var fixCount = 0;
            foreach (var fix in fixesArr.EnumerateArray())
            {
                var oldStr = fix.TryGetProperty("oldString", out var oEl) ? oEl.GetString() : null;
                var newStr = fix.TryGetProperty("newString", out var nEl) ? nEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(oldStr) || string.IsNullOrWhiteSpace(newStr) || oldStr == newStr)
                { continue; }
                if (Regex.IsMatch(newStr, @"\w\s+\(")) { continue; }
                var idx = fixedContent.IndexOf(oldStr, StringComparison.Ordinal);
                if (idx < 0) { continue; }
                var secIdx = fixedContent.IndexOf(oldStr, idx + oldStr.Length, StringComparison.Ordinal);
                if (secIdx >= 0) { continue; }
                fixedContent = fixedContent[..idx] + newStr + fixedContent[(idx + oldStr.Length)..];
                fixCount++;
            }

            if (fixCount > 0)
            {
                fixedContent = Regex.Replace(fixedContent, @"\b(\w+)\s+\(", "$1(");
                await System.IO.File.WriteAllTextAsync(fullPath, fixedContent, Encoding.UTF8, ct);
                await EmitLog(emitSse, "info",
                    $"Style fix: applied {fixCount} spacing fix(es) in {relPath}", ct: ct);
            }
            return fixedContent;
        }
        catch
        {
            return content;
        }
    }
    private static string NormalizeChangeForDedup(string? change)
    {
        if (string.IsNullOrWhiteSpace(change)) return "";
        var norm = change.Trim().ToLowerInvariant();
        norm = Regex.Replace(norm, @"[^\w\s]", "");
        norm = string.Join(" ", norm.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
        return norm;
    }

    private static double CalculateChangeSimilarity(string s1, string s2)
    {
        if (string.IsNullOrWhiteSpace(s1) || string.IsNullOrWhiteSpace(s2)) return 0.0;
        var words1 = new HashSet<string>(s1.Split(' '));
        var words2 = new HashSet<string>(s2.Split(' '));
        var intersection = words1.Intersect(words2).Count();
        var union = words1.Union(words2).Count();
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    private static string StripEditKnowledgeHeader(string discoveryContext)
    {
        if (string.IsNullOrWhiteSpace(discoveryContext)) return discoveryContext;

        var priorKnowledgeIdx = discoveryContext.IndexOf("### PRIOR EDIT KNOWLEDGE FOR THIS PROJECT ###", StringComparison.Ordinal);
        var relevantKnowledgeIdx = discoveryContext.IndexOf("### EDIT KNOWLEDGE (relevant to this file/task) ###", StringComparison.Ordinal);

        var headerIdx = priorKnowledgeIdx >= 0 ? priorKnowledgeIdx :
                        relevantKnowledgeIdx >= 0 ? relevantKnowledgeIdx : -1;

        if (headerIdx < 0) return discoveryContext;

        var afterHeader = headerIdx + 50;
        var nextMainSectionIdx = discoveryContext.IndexOf("\n### ", afterHeader, StringComparison.Ordinal);

        if (nextMainSectionIdx < 0)
            return discoveryContext;

        return discoveryContext.Substring(nextMainSectionIdx + 1).TrimStart();
    }

    private string AutoFormatEditedRegion(string content, string appliedNewStr)
    {
        if (string.IsNullOrWhiteSpace(appliedNewStr) || string.IsNullOrWhiteSpace(content))
            return content;

        var fileLines = content.Split('\n');
        var needleSet = appliedNewStr.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l) && l.Length >= 3)
            .ToHashSet(StringComparer.Ordinal);
        var longNeedles = needleSet.Where(n => n.Length >= 12).ToList();

        var editedLineIndices = new HashSet<int>();
        var firstExact = -1;
        var lastExact = -1;
        for (var i = 0; i < fileLines.Length; i++)
        {
            var trimmed = fileLines[i].Trim();
            if (trimmed.Length < 3) continue;
            if (needleSet.Contains(trimmed))
            {
                editedLineIndices.Add(i);
                if (firstExact < 0) firstExact = i;
                lastExact = i;
                continue;
            }
        }
        if (longNeedles.Count > 0 && firstExact >= 0)
        {
            var windowStart = Math.Max(0, firstExact - 30);
            var windowEnd = Math.Min(fileLines.Length, lastExact + 30);
            for (var i = windowStart; i < windowEnd; i++)
            {
                var trimmed = fileLines[i].Trim();
                if (trimmed.Length < 3) continue;
                foreach (var needle in longNeedles)
                {
                    if (trimmed.Contains(needle, StringComparison.Ordinal)) { editedLineIndices.Add(i); break; }
                }
            }
        }

        if (editedLineIndices.Count == 0) return content;

        var sb = new StringBuilder(content.Length + 16);
        var inStringDouble = false;
        var inStringSingle = false;
        var inTemplate = false;
        var inVerbatimString = false;
        var inLineComment = false;
        var inBlockComment = false;
        var changed = false;

        for (var i = 0; i < fileLines.Length; i++)
        {
            var line = fileLines[i];
            var formattedLine = FormatLineWithState(line, ref inStringDouble, ref inStringSingle, ref inTemplate, ref inVerbatimString, ref inLineComment, ref inBlockComment);

            if (editedLineIndices.Contains(i))
            {
                if (formattedLine != line) changed = true;
                sb.Append(formattedLine);
            }
            else
            {
                sb.Append(line);
            }

            if (i < fileLines.Length - 1) sb.Append('\n');
        }

        if (!changed) return content;

        var result = sb.ToString();

        var resultLines = result.Split('\n');
        var parensChanged = false;
        for (var i = 0; i < resultLines.Length; i++)
        {
            if (!editedLineIndices.Contains(i)) continue;
            var fixedLine = FixStrayClosingParens(resultLines, i);
            if (fixedLine != resultLines[i])
            {
                resultLines[i] = fixedLine;
                parensChanged = true;
            }
        }

        return parensChanged ? string.Join("\n", resultLines) : result;
    }
    private string FormatLineWithState(string line,
    ref bool inStringDouble, ref bool inStringSingle, ref bool inTemplate,
    ref bool inVerbatimString, ref bool inLineComment, ref bool inBlockComment)
    {
        var sb = new StringBuilder(line.Length + 4);
        var i = 0;

        while (i < line.Length)
        {
            var c = line[i];
            var next = (i + 1 < line.Length) ? line[i + 1] : '\0';
            var prev = (i > 0) ? line[i - 1] : '\0';

            if (inBlockComment)
            {
                sb.Append(c);
                if (c == '*' && next == '/')
                {
                    sb.Append(next);
                    i += 2;
                    inBlockComment = false;
                    continue;
                }
                i++;
                continue;
            }

            if (inLineComment)
            {
                sb.Append(c);
                i++;
                continue;
            }

            if (inVerbatimString)
            {
                sb.Append(c);
                if (c == '"')
                {
                    if (next == '"')
                    {
                        sb.Append(next);
                        i += 2;
                        continue;
                    }
                    else
                    {
                        inVerbatimString = false;
                        i++;
                        continue;
                    }
                }

                if (char.IsLetter(c))
                {
                    var rest = line.Substring(i);
                    var match = Regex.Match(rest, @"^(INTERVAL|MINUTE|HOUR|DAY|MONTH|YEAR|SECOND|MICROSECOND|WEEK|QUARTER|LIMIT|OFFSET|TOP|SELECT|DELETE|UPDATE|INSERT|FROM|WHERE|JOIN|AND|OR|NOT|IN|ON|AS|BY|ORDER|GROUP|HAVING|UNION|INTO|VALUES|SET|CREATE|TABLE|ALTER|DROP|CASE|WHEN|THEN|ELSE|END|EXISTS|DISTINCT|WITH|ALL)\d", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        sb.Append(match.Value.Substring(0, match.Value.Length - 1));
                        sb.Append(' ');
                        sb.Append(match.Value[match.Value.Length - 1]);
                        i += match.Value.Length;
                        continue;
                    }

                    match = Regex.Match(rest, @"^(SELECT|DELETE|DISTINCT|ALL)\*", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        sb.Append(match.Value.Substring(0, match.Value.Length - 1));
                        sb.Append(" *");
                        i += match.Value.Length;
                        continue;
                    }

                    match = Regex.Match(rest, @"^(SELECT|FROM|WHERE|JOIN|INNER|LEFT|RIGHT|OUTER|AND|OR|NOT|IN|BETWEEN|LIKE|IS|ON|AS|BY|ORDER|GROUP|HAVING|LIMIT|OFFSET|UNION|INSERT|INTO|VALUES|UPDATE|SET|DELETE|CREATE|TABLE|ALTER|DROP|CASE|WHEN|THEN|ELSE|END|EXISTS|DISTINCT|WITH)\(", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        sb.Append(match.Value.Substring(0, match.Value.Length - 1));
                        sb.Append(" (");
                        i += match.Value.Length;
                        continue;
                    }
                }

                i++;
                continue;
            }

            if (inStringDouble || inStringSingle || inTemplate)
            {
                if (char.IsLetter(c))
                {
                    var rest = line.Substring(i);
                    var match = Regex.Match(rest, @"^(INTERVAL|MINUTE|HOUR|DAY|MONTH|YEAR|SECOND|MICROSECOND|WEEK|QUARTER|LIMIT|OFFSET|TOP|SELECT|DELETE|UPDATE|INSERT|FROM|WHERE|JOIN|AND|OR|NOT|IN|ON|AS|BY|ORDER|GROUP|HAVING|UNION|INTO|VALUES|SET|CREATE|TABLE|ALTER|DROP|CASE|WHEN|THEN|ELSE|END|EXISTS|DISTINCT|WITH|ALL)\d", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        sb.Append(match.Value.Substring(0, match.Value.Length - 1));
                        sb.Append(' ');
                        sb.Append(match.Value[match.Value.Length - 1]);
                        i += match.Value.Length;
                        continue;
                    }

                    match = Regex.Match(rest, @"^(SELECT|DELETE|DISTINCT|ALL)\*", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        sb.Append(match.Value.Substring(0, match.Value.Length - 1));
                        sb.Append(" *");
                        i += match.Value.Length;
                        continue;
                    }

                    match = Regex.Match(rest, @"^(SELECT|FROM|WHERE|JOIN|INNER|LEFT|RIGHT|OUTER|AND|OR|NOT|IN|BETWEEN|LIKE|IS|ON|AS|BY|ORDER|GROUP|HAVING|LIMIT|OFFSET|UNION|INSERT|INTO|VALUES|UPDATE|SET|DELETE|CREATE|TABLE|ALTER|DROP|CASE|WHEN|THEN|ELSE|END|EXISTS|DISTINCT|WITH)\(", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        sb.Append(match.Value.Substring(0, match.Value.Length - 1));
                        sb.Append(" (");
                        i += match.Value.Length;
                        continue;
                    }
                }

                sb.Append(c);
                if (c == '\\' && next != '\0')
                {
                    sb.Append(next);
                    i += 2;
                    continue;
                }
                if (inStringDouble && c == '"') inStringDouble = false;
                else if (inStringSingle && c == '\'') inStringSingle = false;
                else if (inTemplate && c == '`') inTemplate = false;
                i++;
                continue;
            }

            if (c == '/' && next == '/')
            {
                inLineComment = true;
                sb.Append(c);
                i++;
                continue;
            }
            if (c == '/' && next == '*')
            {
                inBlockComment = true;
                sb.Append(c);
                i++;
                continue;
            }
            if (c == '@' && next == '"')
            {
                inVerbatimString = true;
                sb.Append(c);
                sb.Append(next);
                i += 2;
                continue;
            }
            if (c == '"') { inStringDouble = true; sb.Append(c); i++; continue; }
            if (c == '\'') { inStringSingle = true; sb.Append(c); i++; continue; }
            if (c == '`') { inTemplate = true; sb.Append(c); i++; continue; }


            if (c == ',')
            {
                sb.Append(c);
                i++;
                if (i < line.Length)
                {
                    var after = line[i];
                    if (after != ' ' && after != '\t' && after != '\r' && after != '\n'
                        && after != ')' && after != ']' && after != '}')
                    {
                        sb.Append(' ');
                    }
                }
                continue;
            }

            if (c == ':')
            {
                sb.Append(c);
                i++;
                if (i < line.Length)
                {
                    var after = line[i];
                    if (prev != ':' && after != ':'
                        && after != ' ' && after != '\t' && after != '\r' && after != '\n')
                    {
                        sb.Append(' ');
                    }
                }
                continue;
            }

            if (c == ';')
            {
                sb.Append(c);
                i++;
                if (i < line.Length)
                {
                    var after = line[i];
                    if (after != ';' && after != ')'
                        && after != ' ' && after != '\t' && after != '\r' && after != '\n')
                    {
                        sb.Append(' ');
                    }
                }
                continue;
            }

            if (c == '=')
            {
                const string operatorPrevChars = "!<>=+-*/%&|^~?:";
                var isOperatorContext = prev != '\0' && operatorPrevChars.IndexOf(prev) >= 0;
                var nextChar = (i + 1 < line.Length) ? line[i + 1] : '\0';
                var isHtmlAttributeLike = nextChar == '"' || nextChar == '\'' || nextChar == '`';

                if (isHtmlAttributeLike)
                {
                    sb.Append(c);
                    i++;
                    continue;
                }

                if (!isOperatorContext && sb.Length > 0)
                {
                    var lastChar = sb[sb.Length - 1];
                    if (lastChar != ' ' && lastChar != '\t'
                        && (char.IsLetterOrDigit(lastChar) || lastChar == ')' || lastChar == ']'
                            || lastChar == '_' || lastChar == '$'))
                    {
                        sb.Append(' ');
                    }
                }

                sb.Append(c);
                i++;

                if (i < line.Length)
                {
                    var after = line[i];
                    if (after != '=' && after != '>'
                        && after != '"' && after != '\'' && after != '`'
                        && after != ' ' && after != '\t' && after != '\r' && after != '\n')
                    {
                        sb.Append(' ');
                    }
                }
                continue;
            }

            if (c == '?')
            {
                sb.Append(c);
                i++;
                if (i < line.Length)
                {
                    var after = line[i];
                    if (char.IsDigit(after))
                    {
                        sb.Append(' ');
                    }
                }
                continue;
            }

            sb.Append(c);
            i++;
        }

        inLineComment = false;
        return sb.ToString();
    }

    private static string FixStrayClosingParens(string[] fileLines, int idx)
    {
        var line = fileLines[idx];
        if (string.IsNullOrEmpty(line)) return line;

        var trimmed = line.TrimStart();
        if (trimmed.Length == 0) return line;

        char closeCh;
        if (trimmed[0] == ')') closeCh = ')';
        else if (trimmed[0] == ']') closeCh = ']';
        else if (trimmed[0] == '}') closeCh = '}';
        else return line;

        char openCh = closeCh == ')' ? '(' : (closeCh == ']' ? '[' : '{');

        var suffix = trimmed.Substring(1);
        if (!IsSafeCloseSuffix(suffix)) return line;

        var depth = 1;
        var inStrDq = false; var inStrSq = false; var inTmpl = false;
        var inLineCmt = false; var inBlockCmt = false;
        var openerLineIdx = -1;

        for (var li = idx - 1; li >= 0; li--)
        {
            var upLine = fileLines[li];
            if (string.IsNullOrEmpty(upLine))
            {
                inLineCmt = false;
                continue;
            }
            var localInLineCmt = inLineCmt;
            var localInBlockCmt = inBlockCmt;
            var localInDq = inStrDq; var localInSq = inStrSq; var localInTmpl = inTmpl;
            var lastOpenerCharIdx = -1;
            var foundOpenerOnThisLine = false;

            for (var ci = upLine.Length - 1; ci >= 0; ci--)
            {
                var c = upLine[ci];
                var next = (ci + 1 < upLine.Length) ? upLine[ci + 1] : '\0';
                var prev = (ci > 0) ? upLine[ci - 1] : '\0';
                if (localInDq || localInSq || localInTmpl)
                {
                    if (c == '\\' && prev != '\\')
                    {
                        ci--;
                        continue;
                    }
                    if (localInDq && c == '"' && prev != '\\') localInDq = false;
                    else if (localInSq && c == '\'' && prev != '\\') localInSq = false;
                    else if (localInTmpl && c == '`' && prev != '\\') localInTmpl = false;
                    continue;
                }

                if (localInBlockCmt)
                {
                    if (c == '*' && prev == '/')
                    {
                        localInBlockCmt = false;
                        ci--;
                    }
                    continue;
                }

                if (c == '/' && prev == '*')
                {
                    localInBlockCmt = true;
                    ci--;
                    continue;
                }

                if (c == '/' && prev == '/')
                {
                    break;
                }

                if (c == '"') { localInDq = true; continue; }
                if (c == '\'') { localInSq = true; continue; }
                if (c == '`') { localInTmpl = true; continue; }

                if (c == openCh)
                {
                    depth--;
                    if (depth == 0)
                    {
                        lastOpenerCharIdx = ci;
                        foundOpenerOnThisLine = true;
                    }
                }
                else if (c == closeCh)
                {
                    depth++;
                }
            }

            inLineCmt = false;
            inBlockCmt = localInBlockCmt;
            inStrDq = localInDq; inStrSq = localInSq; inTmpl = localInTmpl;

            if (foundOpenerOnThisLine && depth == 0)
            {
                openerLineIdx = li;
                break;
            }

            if (depth < 0) return line;
        }

        if (openerLineIdx < 0) return line;

        var openerLine = fileLines[openerLineIdx];
        var openerIndent = AgentUtilities.GetLeadingWhitespace(openerLine);
        var currentIndent = AgentUtilities.GetLeadingWhitespace(line);

        if (currentIndent.Length <= openerIndent.Length) return line;

        return openerIndent + line[currentIndent.Length..];
    }

    private static bool IsSafeCloseSuffix(string suffix)
    {
        if (string.IsNullOrEmpty(suffix)) return true;
        if (suffix.Trim().Length == 0) return true;
        var s = suffix.Trim();
        return s is ";" or "," or ")" or "]" or "}"
            or "{" or "; {" or ", {" or ") {" or "] {" or "} {" or ";{" or ",{" or "){" or "]{" or "}{";
    }


    private async Task<(string decision, string reason, int score)> LlmVerifyEditStepAsync(
    string relPath,
    string originalPrompt,
    string stepChange,
    string oldStr,
    string newStr,
    string preEditContent,
    string postEditContent,
    bool emitSse,
    CancellationToken ct,
    List<(int score, string reason, string failedNew)>? priorAttempts = null,
    string? explorationContext = null,
    AgentPlan? fullPlan = null,
    int currentStepIndex = -1)
    {
        var anchor = newStr.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
        var postLines = postEditContent.Split('\n');
        var anchorIdx = -1;
        if (!string.IsNullOrEmpty(anchor))
        {
            for (var i = 0; i < postLines.Length; i++)
            {
                if (postLines[i].Contains(anchor, StringComparison.Ordinal))
                {
                    anchorIdx = i;
                    break;
                }
            }
        }
        var ctxStart = Math.Max(0, anchorIdx - 5);
        var ctxEnd = Math.Min(postLines.Length, anchorIdx + 5);
        var contextWindow = anchorIdx >= 0
            ? string.Join("\n", postLines[ctxStart..ctxEnd])
            : "(anchor not found in post-edit file)";

        var priorBlock = new StringBuilder();
        if (priorAttempts != null && priorAttempts.Count > 0)
        {
            priorBlock.AppendLine("\n### PRIOR FAILED ATTEMPTS — learn from these ###");
            for (var i = 0; i < priorAttempts.Count; i++)
            {
                var pa = priorAttempts[i];
                priorBlock.AppendLine($"Attempt {i + 1}: score={pa.score}/100, reason={pa.reason}");
                priorBlock.AppendLine("Failed code (DO NOT reproduce this):");
                priorBlock.AppendLine("```");
                priorBlock.AppendLine(TruncateForLlm(pa.failedNew, 800));
                priorBlock.AppendLine("```");
            }
            priorBlock.AppendLine();
        }

        var futureStepsBlock = new StringBuilder();
        if (fullPlan?.Plan?.Count > 0 && currentStepIndex >= 0)
        {
            futureStepsBlock.AppendLine("\n### PLANNED FUTURE STEPS (Context for verification) ###");
            futureStepsBlock.AppendLine("The current edit is step " + (currentStepIndex + 1) + ".");
            futureStepsBlock.AppendLine("If this edit references methods/properties that don't exist yet, check if they are added in a FUTURE step below.");
            futureStepsBlock.AppendLine("If they are added in a future step, DO NOT abandon the current edit for missing references.\n");

            for (int i = currentStepIndex + 1; i < fullPlan.Plan.Count; i++)
            {
                var p = fullPlan.Plan[i];
                futureStepsBlock.AppendLine($"Step {i + 1}: [{p.File}] {p.Change}");
            }
        }

        var sysPrompt =
            "You are a meticulous code reviewer verifying a single edit step in a larger plan. " +
            "Your job is to decide whether to KEEP the edit (it correctly implements the step " +
            "without breaking existing functionality) or ABANDON it (it broke something, changed " +
            "the wrong thing, introduced undefined references, deleted guards/caches, or otherwise " +
            "missed the intent of the step).\n\n" +
            "STRICT OUTPUT FORMAT — output ONLY a JSON object, no prose, no markdown fences:\n" +
            "{\"decision\":\"keep\"|\"abandon\", \"reason\":\"one short sentence\", \"score\": 0-100}\n\n" +
            "SCORE GUIDELINES:\n" +
            "  90-100: Perfect — correctly implements the step, no issues\n" +
            "  70-89:  Good — mostly correct, minor issues that could be fixed in a follow-up\n" +
            "  40-69:  Poor — wrong approach or missing key functionality\n" +
            "  0-39:   Broken — signature change, deleted functionality, invented symbols\n\n" +
            "DECISION RULES:\n" +
            " * Return \"keep\" if the edit correctly implements the step. Score 85+.\n" +
            " * Return \"abandon\" if ANY of these are true:\n" +
            "    - The edit deleted cache/state guard lines (e.g. `if (this.X) return ...`, " +
            "      `map.has(...)`, `map.get(...)`, `map.set(...)`).\n" +
            "    - The edit changed an existing method's signature (return type, name, or parameter list).\n" +
            "    - The edit is functionally a no-op (old and new do the same thing).\n" +
            "    - The edit breaks the build (syntax errors, missing braces, undefined vars).\n" +
            "    - SECTION MISMATCH: For HTML/Angular templates with multiple *ngIf sections " +
            "      (e.g., *ngIf=\"activeDataTab === 'users'\" vs *ngIf=\"activeDataTab === 'general'\"), " +
            "      if the step says 'add X to the general tab' but the oldString comes from the 'users' " +
            "      tab (or any other section), ABANDON with reason 'edited wrong section'. " +
            "      This is critical — do NOT be fooled by sections that have similar structure. " +
            "      Check WHICH *ngIf section the oldString belongs to, not just whether the edit 'looks right'.\n" +
            " * IMPORTANT: Do NOT abandon an edit just because it 'radically changed the method' or " +
            "  'replaced existing logic'. If the step asked for a new feature or significant modification, " +
            "  a rewrite of the method body is EXPECTED and CORRECT. Only abandon if it breaks existing " +
            "  functionality that is UNRELATED to the requested change.\n" +
            " * SEQUENTIAL DEPENDENCIES (CRITICAL): Do NOT abandon an edit just because it references a method or property " +
            "  that doesn't exist in the current file yet. If the PLANNED FUTURE STEPS section indicates that a future " +
            "  step will add the missing method/property, OR if the system has auto-injected stubs, you MUST KEEP the current edit. " +
            "  For HTML files, assume that methods referenced in (click) or (menuClicked) handlers WILL BE or HAVE BEEN added to the .ts file. " +
            "  Do NOT abandon an HTML edit solely because you think the method might not exist in the .ts file.\n" +
            " * INSERTIONS: If the step asks to ADD a new method, property, or block of code, and the newString " +
            "  CONTAINS the entire oldString unchanged (usually at the beginning) followed by the new code, this is an INSERTION. " +
            "  This is the CORRECT behavior. Do NOT abandon it claiming it 'replaced' or 'failed to add' the new method. " +
            "  If the new code is present and the old code is preserved, keep the edit.\n" +
            " * If the step asks to modify specific values inside a method (e.g., change coordinates, update a config), " +
            "  it is acceptable to replace the entire method as long as the requested values are updated correctly " +
            "  and the rest of the method is preserved. Do NOT abandon just because the LLM rewrote the method.\n" +
            " * Be conservative: if you're unsure, return \"keep\" and let the build check catch any issues.\n" +
            " * Do NOT consider style/whitespace issues — those are handled by other passes.";

        var userMsg =
            $"### TASK PROMPT ###\n{originalPrompt}\n\n" +
            $"### STEP DESCRIPTION ###\n{stepChange}\n\n" +
            $"### FILE ###\n{relPath}\n\n" +
            (string.IsNullOrWhiteSpace(explorationContext) ? "" : $"### RELATED SERVICE/MODEL CONTEXT ###\n{explorationContext}\n\n") +
            futureStepsBlock.ToString() +
            $"### OLD CODE (what was there before) ###\n```\n{TruncateForLlm(oldStr, 1500)}\n```\n\n" +
            $"### NEW CODE (what the edit replaced it with) ###\n```\n{TruncateForLlm(newStr, 1500)}\n```\n\n" +
            $"### POST-EDIT CONTEXT WINDOW (10 lines around the edit) ###\n```\n{contextWindow}\n```\n" +
            (priorAttempts != null && priorAttempts.Count > 0 ? priorBlock.ToString() : "") +
            "\nDecide: keep or abandon? Provide a quality score 0-100. Output JSON only.";

        try
        {
            var (raw, _, error) = await CallLlmRawStreaming(
                sysPrompt, userMsg, emitSse, ct,
                requestTimeout: TimeSpan.FromMinutes(2),
                maxTokens: 256);

            if (string.IsNullOrWhiteSpace(raw))
                return ("error", $"LLM returned empty response. {error}", 0);

            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                cleaned = cleaned.TrimStart('`');
                var firstNewline = cleaned.IndexOf('\n');
                if (firstNewline >= 0) cleaned = cleaned[(firstNewline + 1)..];
                if (cleaned.EndsWith("```")) cleaned = cleaned[..^3];
            }

            cleaned = ExtractFirstJsonObject(cleaned);

            using var doc = JsonDocument.Parse(cleaned);

            var decision = doc.RootElement.TryGetProperty("decision", out var dEl)
                ? dEl.GetString()?.ToLowerInvariant().Trim() ?? ""
                : "";
            var reason = doc.RootElement.TryGetProperty("reason", out var rEl)
                ? rEl.GetString()?.Trim() ?? ""
                : "";
            var score = doc.RootElement.TryGetProperty("score", out var sEl) && sEl.ValueKind == JsonValueKind.Number
                ? sEl.GetInt32()
                : (decision == "keep" ? 85 : 30);

            if (decision != "keep" && decision != "abandon")
                return ("error", $"LLM returned unknown decision '{decision}'", score);

            return (decision, reason, score);
        }
        catch (Exception ex)
        {
            return ("error", $"Exception during LLM verify: {ex.Message}", 0);
        }
    }

    private static string TruncateForLlm(string s, int maxChars)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxChars) return s ?? "";
        var headLen = (int)(maxChars * 0.6);
        var tailLen = maxChars - headLen - 20;
        if (tailLen < 0) tailLen = 0;
        return s.Substring(0, headLen) +
               $"\n... [truncated {s.Length - headLen - tailLen} chars] ...\n" +
               (tailLen > 0 ? s.Substring(s.Length - tailLen, tailLen) : "");
    }

    private async Task<string?> DetectMissingCreateTableAsync(
        string oldStr, string newStr, string fileContent, string relPath, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(newStr) || string.IsNullOrWhiteSpace(fileContent))
            return null;

        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        var sqlCapableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".rs", ".java", ".kt", ".php", ".rb", ".sql" };

        if (!sqlCapableExtensions.Contains(ext)) return null;

        var insertUpdateRegex = new Regex(
            @"\b(?:INSERT\s+INTO|UPDATE)\s+`?(\w+)`?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var referencedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchExcerpts = new List<string>();

        foreach (Match m in insertUpdateRegex.Matches(newStr))
        {
            var tbl = m.Groups[1].Value;
            if (tbl.Length <= 2 || char.IsDigit(tbl[0])) continue;
            referencedTables.Add(tbl);

            var start = Math.Max(0, m.Index - 60);
            var end = Math.Min(newStr.Length, m.Index + m.Length + 60);
            matchExcerpts.Add(newStr.Substring(start, end - start).Replace("\n", " ").Trim());
        }

        if (referencedTables.Count == 0) return null;

        var createTableRegex = new Regex(
            @"\bCREATE\s+TABLE(?:\s+IF\s+NOT\s+EXISTS)?\s+`?(\w+)`?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in createTableRegex.Matches(newStr))
            existingTables.Add(m.Groups[1].Value);
        foreach (Match m in createTableRegex.Matches(fileContent))
            existingTables.Add(m.Groups[1].Value);

        var tableMentionRegex = new Regex(
            @"\b(?:FROM|JOIN|INTO|UPDATE|TABLE)\s+`?(\w+)`?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        foreach (Match m in tableMentionRegex.Matches(fileContent))
            existingTables.Add(m.Groups[1].Value);

        var missingTables = referencedTables
            .Where(t => !existingTables.Contains(t))
            .ToList();

        if (missingTables.Count == 0) return null;

        var sysPrompt = "You are a code analysis AI. You examine code snippets to determine if they contain actual SQL statements (INSERT INTO, UPDATE) that are meant to modify a database table, or if they are just regular text/prose/comments that happen to contain those words. Output ONLY a JSON object: {\"isSql\": true|false}";
        var userPrompt = $"File: {relPath}\n\nSnippets found:\n{string.Join("\n---\n", matchExcerpts)}\n\nDo these snippets contain actual SQL statements executing against a database table?";

        try
        {
            var (raw, _, err) = await CallLlmRaw(sysPrompt, userPrompt, ct, TimeSpan.FromSeconds(15), maxTokens: 64);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var cleaned = raw.Trim();
                if (cleaned.StartsWith("```"))
                {
                    var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                    if (m.Success) cleaned = m.Groups[1].Value.Trim();
                }
                var fb = cleaned.IndexOf('{');
                var lb = cleaned.LastIndexOf('}');
                if (fb >= 0 && lb > fb) cleaned = cleaned.Substring(fb, lb - fb + 1);

                using var doc = JsonDocument.Parse(cleaned);
                if (doc.RootElement.TryGetProperty("isSql", out var isSqlEl) && isSqlEl.ValueKind == JsonValueKind.False)
                {
                    await EmitLog(emitSse, "info", $"SQL Guard: LLM verified that matched 'INSERT/UPDATE' keywords in {relPath} are prose, not SQL.", ct: ct);
                    return null;
                }
            }
        }
        catch { }

        var preview = string.Join(", ", missingTables.Take(5));
        return $"MISSING CREATE TABLE — newString contains INSERT/UPDATE statements referencing table(s) [{preview}] " +
               "that do NOT appear to exist in the current file (no CREATE TABLE, FROM, JOIN, or other reference found). " +
               "You MUST add a CREATE TABLE IF NOT EXISTS statement for each missing table, placed BEFORE the INSERT/UPDATE " +
               "statements that reference it. Define all columns referenced by the INSERT/UPDATE with appropriate MySQL data types " +
               "(INT, VARCHAR, TEXT, TIMESTAMP, etc.). Place the CREATE TABLE strategically at the beginning of the new code block, " +
               "before any INSERT/UPDATE that depends on it. Do NOT emit INSERT/UPDATE for a table that has not been created yet.";
    }

    private static string? DetectFunctionalityWipe(
        string oldStr, string newStr, string fileContent, string relPath, string? stepChange = null)
    {
        if (string.IsNullOrWhiteSpace(oldStr) || string.IsNullOrWhiteSpace(newStr))
            return null;

        var oldLines = oldStr.Split('\n');

        string NormalizeForComparison(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return "";
            return Regex.Replace(line.Trim(), @"\s+", " ").Trim();
        }

        var newLinesSet = new HashSet<string>(
            newStr.Split('\n').Select(l => NormalizeForComparison(l)),
            StringComparer.Ordinal);

        var cacheLinePatterns = new[]
        {
        new Regex(@"\.has\s*\(", RegexOptions.Compiled),
        new Regex(@"\.get\s*\(", RegexOptions.Compiled),
        new Regex(@"\.set\s*\(", RegexOptions.Compiled),
        new Regex(@"\.delete\s*\(", RegexOptions.Compiled),
        new Regex(@"return\s+this\.\w+\s*;", RegexOptions.Compiled),
        new Regex(@"if\s*\(\s*this\.\w+", RegexOptions.Compiled),
        new Regex(@"if\s*\(\s*!\s*this\.\w+", RegexOptions.Compiled),
        new Regex(@"this\.\w+\s*=\s*null\s*;", RegexOptions.Compiled),
        new Regex(@"this\.\w+\s*=\s*undefined\s*;", RegexOptions.Compiled),
        new Regex(@"this\.\w+\s*=\s*default\s*;", RegexOptions.Compiled),
        new Regex(@"_\w+\s*=\s*null\s*;", RegexOptions.Compiled),
    };

        var guardLinePatterns = new[]
        {
        cacheLinePatterns,
        new[]
        {
            new Regex(@"if\s*\([^)]*\.length\s*[<>=!]", RegexOptions.Compiled),
            new Regex(@"if\s*\([^)]*displayRadio", RegexOptions.Compiled),
            new Regex(@"if\s*\(\s*!\s*\w+\s*&&", RegexOptions.Compiled),
        }
    }.SelectMany(x => x).ToArray();

        var lostCacheLines = new List<string>();
        foreach (var line in oldLines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed == "{" || trimmed == "}" || trimmed == "});") continue;

            var isCacheLine = false;
            foreach (var pat in guardLinePatterns)
            {
                if (pat.IsMatch(trimmed)) { isCacheLine = true; break; }
            }
            if (!isCacheLine) continue;

            if (newStr.Contains("// Render explosion mesh here") ||
                newStr.Contains("// TODO: implement") ||
                newStr.Contains("// ... existing code ..."))
            {
                return "PLACEHOLDER DETECTED — newString replaced actual implementation logic with a placeholder comment. " +
                       "You MUST copy the exact implementation from oldString and modify it, not replace it with a stub.";
            }

            var normalizedOld = NormalizeForComparison(trimmed);
            if (!newLinesSet.Contains(normalizedOld))
                lostCacheLines.Add(trimmed);
        }

        if (lostCacheLines.Count > 0)
        {
            var preview = string.Join("; ", lostCacheLines.Take(3));
            if (lostCacheLines.Count > 3) preview += $"; (+{lostCacheLines.Count - 3} more)";
            return $"CACHE-STATE LOSS — oldString contained cache/guard line(s) that are MISSING from newString: [{preview}]. " +
                   "These lines protect against redundant work or null derefs. PRESERVE them in newString verbatim " +
                   "(only the property values you actually need to change should be edited, not the guard logic).";
        }

        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        if (ext is ".ts" or ".tsx" or ".js" or ".jsx" or ".cs" or ".vb")
        {
            var oldSigLine = AgentUtilities.CollectCompleteSignatureLine(oldLines);
            var newSigLine = AgentUtilities.CollectCompleteSignatureLine(newStr.Split('\n'));
            if (LooksLikeMethodDeclaration(oldSigLine) && LooksLikeMethodDeclaration(newSigLine))
            {
                var oldTokens = AgentUtilities.ExtractSignatureTokens(oldSigLine);
                var newTokens = AgentUtilities.ExtractSignatureTokens(newSigLine);
                if (!oldTokens.SequenceEqual(newTokens))
                {
                    bool isConstructorInjection = (stepChange ?? "").Contains("constructor", StringComparison.OrdinalIgnoreCase) &&
                                                  oldSigLine.Contains("constructor(", StringComparison.OrdinalIgnoreCase);

                    if (!isConstructorInjection)
                    {
                        var oldSig = string.Join(" ", oldTokens);
                        var newSig = string.Join(" ", newTokens);
                        return $"SIGNATURE CHANGE — method declaration changed from [{oldSig}] to [{newSig}]. " +
                               "The task is to tweak the body, not change the contract (return type / name / params). " +
                               "RESTORE the original signature line and only modify the body lines below it.";
                    }
                }
            }
        }

        return null;
    }

    private static string? CheckMethodExistsInFile(string fileContent, string newStr)
    {
        var fnMatch = Regex.Match(newStr, @"(?:vm\.)?(\w+)\s*(?:[:=])\s*function\s*\(", RegexOptions.IgnoreCase);
        if (!fnMatch.Success)
            fnMatch = Regex.Match(newStr, @"function\s+(\w+)\s*\(", RegexOptions.IgnoreCase);
        if (!fnMatch.Success)
            return null;

        var fnName = fnMatch.Groups[1].Value;
        if (fnName.Length <= 2) return null;

        var existingPattern = $@"(?:vm\.)?{Regex.Escape(fnName)}\s*(?:[:=])\s*function\s*\(";
        if (Regex.IsMatch(fileContent, existingPattern, RegexOptions.IgnoreCase))
            return fnName;

        var existingPattern2 = $@"function\s+{Regex.Escape(fnName)}\s*\(";
        if (Regex.IsMatch(fileContent, existingPattern2, RegexOptions.IgnoreCase))
            return fnName;

        return null;
    }

    private static bool LooksLikeMethodDeclaration(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        if (!line.Contains('(')) return false;
        var lower = line.ToLowerInvariant();
        if (lower.StartsWith("if ") || lower.StartsWith("if(") ||
            lower.StartsWith("for ") || lower.StartsWith("for(") ||
            lower.StartsWith("while ") || lower.StartsWith("while(") ||
            lower.StartsWith("switch ") || lower.StartsWith("switch(") ||
            lower.StartsWith("return ") || lower.StartsWith("return(") ||
            lower.StartsWith("using ") || lower.StartsWith("using(") ||
            lower.StartsWith("lock ") || lower.StartsWith("lock(") ||
            lower.StartsWith("await ") ||
            lower.StartsWith("//") || lower.StartsWith("/*"))
            return false;
        return Regex.IsMatch(line,
            @"^\s*(?:(?:public|private|protected|internal|static|async|export|function|override|sealed|virtual|abstract|readonly|partial)\s+)*"
            + @"(?:<[^>]+>\s*)?"
            + @"~?\w+\s*\(",
            RegexOptions.Compiled);
    }

    private string FormatCssEditedRegion(string content, string appliedNewStr)
    {
        if (string.IsNullOrWhiteSpace(appliedNewStr) || string.IsNullOrWhiteSpace(content))
            return content;

        var fileLines = content.Split('\n');

        var stepCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 1; i < fileLines.Length; i++)
        {
            var line = fileLines[i];
            var trimmed = line.TrimStart();
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (trimmed.Contains('{')) continue;
            if (trimmed.StartsWith("//") || trimmed.StartsWith("/*") ||
                trimmed.StartsWith("*") || trimmed.StartsWith("&") ||
                trimmed.StartsWith("@")) continue;
            if (!trimmed.Contains(':')) continue;
            if (trimmed.Contains("://")) continue;

            for (var j = i - 1; j >= 0; j--)
            {
                if (!fileLines[j].Contains('{')) continue;
                var ruleLeading = LeadingWhitespaceCss(fileLines[j]);
                var propLeading = LeadingWhitespaceCss(line);
                if (propLeading.Length > ruleLeading.Length)
                {
                    var step = propLeading.Substring(ruleLeading.Length);
                    if (!stepCounts.ContainsKey(step)) stepCounts[step] = 0;
                    stepCounts[step]++;
                }
                break;
            }
        }
        var dominantStep = stepCounts.Count > 0
            ? stepCounts.OrderByDescending(k => k.Value).First().Key
            : "  ";

        var anchor = appliedNewStr.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (string.IsNullOrEmpty(anchor))
            return content;

        var editLine = -1;
        for (var i = 0; i < fileLines.Length; i++)
        {
            if (fileLines[i].Contains(anchor, StringComparison.Ordinal))
            {
                editLine = i;
                break;
            }
        }
        if (editLine < 0) return content;

        var rulesToFormat = new HashSet<(int start, int end)>();
        var visited = new HashSet<int>();
        for (var i = editLine; i < fileLines.Length; i++)
        {
            if (!fileLines[i].Contains(anchor, StringComparison.Ordinal) && i != editLine)
            {
                var anyNeedleHere = false;
                foreach (var needle in appliedNewStr.Split('\n').Select(l => l.Trim())
                            .Where(l => !string.IsNullOrWhiteSpace(l)).Take(3))
                {
                    if (fileLines[i].Contains(needle, StringComparison.Ordinal))
                    {
                        anyNeedleHere = true;
                        break;
                    }
                }
                if (!anyNeedleHere && i - editLine > 30) break;
            }

            if (visited.Contains(i)) continue;
            var (ruleStart, ruleEnd) = FindEnclosingRuleCss(fileLines, i);
            if (ruleStart < 0 || ruleEnd <= ruleStart) continue;
            rulesToFormat.Add((ruleStart, ruleEnd));
            for (var k = ruleStart; k <= ruleEnd; k++) visited.Add(k);
        }

        if (rulesToFormat.Count == 0)
        {
            var (rs, re) = FindEnclosingRuleCss(fileLines, editLine);
            if (rs >= 0 && re > rs) rulesToFormat.Add((rs, re));
        }

        if (rulesToFormat.Count == 0) return content;

        var newLines = (string[])fileLines.Clone();
        foreach (var (start, end) in rulesToFormat)
        {
            var ruleIndent = LeadingWhitespaceCss(fileLines[start]);
            var propertyIndent = ruleIndent + dominantStep;

            for (var i = start + 1; i < end; i++)
            {
                var line = fileLines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    newLines[i] = line;
                    continue;
                }

                var trimmed = line.TrimStart();

                if (trimmed.StartsWith("//") || trimmed.StartsWith("/*") ||
                    trimmed.StartsWith("*") || trimmed.StartsWith("@"))
                    continue;
                if (trimmed.StartsWith("&")) continue;
                if (trimmed.Contains('{')) continue;
                if (trimmed.Contains("://")) continue;
                if (!trimmed.Contains(':')) continue;
                if (trimmed.StartsWith(":") || trimmed.StartsWith(">") ||
                    trimmed.StartsWith("+") || trimmed.StartsWith("~") ||
                    trimmed.StartsWith("*"))
                    continue;

                var colonIdx = IndexOfFirstColonOutsideParensCss(trimmed);
                if (colonIdx < 0) continue;

                var prop = trimmed.Substring(0, colonIdx).TrimEnd();
                var rest = trimmed.Substring(colonIdx + 1);

                string trailingComment = "";
                var commentIdx = rest.IndexOf("//");
                if (commentIdx >= 0)
                {
                    trailingComment = " " + rest.Substring(commentIdx).TrimEnd();
                    rest = rest.Substring(0, commentIdx);
                }

                var value = rest.Trim();
                if (value.Length == 0) continue;

                newLines[i] = propertyIndent + prop + ": " + value +
                              (trailingComment.Length > 0 ? trailingComment : "");
            }
        }

        return string.Join("\n", newLines);
    }

    private static (int start, int end) FindEnclosingRuleCss(string[] lines, int fromLine)
    {
        if (lines == null || lines.Length == 0 || fromLine < 0 || fromLine >= lines.Length)
            return (-1, -1);

        var ruleStart = -1;
        var depth = 0;
        for (var i = fromLine; i >= 0; i--)
        {
            foreach (var ch in lines[i])
            {
                if (ch == '}') depth++;
                else if (ch == '{')
                {
                    if (depth > 0) depth--;
                    else { ruleStart = i; goto FoundOpen; }
                }
            }
        }
    FoundOpen:
        if (ruleStart < 0) return (-1, -1);

        depth = 0;
        var foundOpen = false;
        for (var i = ruleStart; i < lines.Length; i++)
        {
            foreach (var ch in lines[i])
            {
                if (ch == '{') { depth++; foundOpen = true; }
                else if (ch == '}') depth--;
            }
            if (foundOpen && depth == 0)
                return (ruleStart, i);
        }
        return (-1, -1);
    }

    private static int IndexOfFirstColonOutsideParensCss(string s)
    {
        if (string.IsNullOrEmpty(s)) return -1;
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            else if (c == ':' && depth == 0) return i;
        }
        return -1;
    }

    private static string LeadingWhitespaceCss(string line)
    {
        if (string.IsNullOrEmpty(line)) return "";
        var sb = new StringBuilder();
        foreach (var ch in line)
        {
            if (ch == ' ' || ch == '\t') sb.Append(ch);
            else break;
        }
        return sb.ToString();
    }


    private async Task<AgentPlan> RunPlanCoherenceCheckAsync(
        AgentPlan plan,
        string projectRoot,
        string originalPrompt,
        bool emitSse,
        CancellationToken ct)
    {
        if (plan?.Plan == null || plan.Plan.Count < 2) return plan!;

        var sb = new StringBuilder();
        sb.AppendLine(
            "You are checking whether a code-change plan is coherent AS A WHOLE — not step by step, " +
            "but as a chain. A plan is coherent when every symbol a step REFERENCES (methods, properties, " +
            "arrays, variables) is either:\n" +
            "  (a) already present in the file's CURRENT content, OR\n" +
            "  (b) explicitly INTRODUCED by a PRIOR step in the same plan.\n" +
            "Name mismatches count as gaps: if the HTML references `selectedImageIndex` but a TS step " +
            "adds `imagePreviewIndex`, that is a gap — they are different names.");
        sb.AppendLine();
        sb.AppendLine("## ORIGINAL TASK");
        sb.AppendLine(originalPrompt);
        sb.AppendLine();

        sb.AppendLine("## CURRENT FILE CONTENTS");
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in plan.Plan)
        {
            if (!AgentUtilities.IsRelativePath(step.File) || AgentUtilities.IsSpecialMarker(step.File)) continue;
            if (!loaded.Add(step.File)) continue;
            var fp = Path.GetFullPath(Path.Combine(projectRoot, step.File.Replace('/', Path.DirectorySeparatorChar)));
            if (!System.IO.File.Exists(fp)) continue;
            var content = await System.IO.File.ReadAllTextAsync(fp, Encoding.UTF8, ct);
            sb.AppendLine($"### {step.File} (current — before this plan runs)");
            sb.AppendLine("```");
            sb.AppendLine(content.Length > 4000 ? content[..4000] + "\n// ... truncated" : content);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("## PLAN TO CHECK");
        for (var i = 0; i < plan.Plan.Count; i++)
            sb.AppendLine($"Step {i + 1}: [{plan.Plan[i].File}] {plan.Plan[i].Change}");
        sb.AppendLine();

        sb.AppendLine("## INSTRUCTIONS");
        sb.AppendLine("For each step, identify:");
        sb.AppendLine("  introduces: the specific symbol NAMES this step will ADD (e.g. `imagePreviews: FileEntry[]`, `nextImage()`)");
        sb.AppendLine("  requires:   the specific symbol NAMES this step REFERENCES that must already exist");
        sb.AppendLine();
        sb.AppendLine("Then check every 'requires' entry against (a) current file content and (b) prior steps' 'introduces'.");
        sb.AppendLine("A mismatch in NAME is a gap — `selectedImageIndex` ≠ `imagePreviewIndex`.");
        sb.AppendLine();
        sb.AppendLine("ALSO check for REDUNDANT or CONFLICTING steps:");
        sb.AppendLine("- If Step A modifies a method to include a new feature, and Step B updates the UI to call a *different* new method for the same feature, the steps are redundant/conflicting.");
        sb.AppendLine("- If a step is unnecessary because a prior step already achieves the same result, REMOVE it from the correctedPlan.");
        sb.AppendLine("- Do NOT keep steps that conflict with each other. Choose ONE clear approach and discard the other.");
        sb.AppendLine();
        sb.AppendLine("CRITICAL — SELF-INCONSISTENT STEP DESCRIPTIONS:");
        sb.AppendLine("Flag any step whose own description is internally contradictory or assumes something");
        sb.AppendLine("that doesn't exist yet. Common patterns to catch:");
        sb.AppendLine("  a) \"Modify the newly added X method\" — if X is NOT in the current file AND no prior step");
        sb.AppendLine("     in this plan adds it, then the step description is self-inconsistent. The step should");
        sb.AppendLine("     say \"Add X method with ...\" instead of assuming X already exists.");
        sb.AppendLine("  b) \"Update the existing Y method to also ...\" but Y does NOT exist in the current file.");
        sb.AppendLine("     This step needs to ADD Y, not modify it.");
        sb.AppendLine("  c) A step that references a symbol (method, property, variable) that is NOT in the");
        sb.AppendLine("     current file content AND is NOT introduced by any prior step in this plan.");
        sb.AppendLine("  d) Steps that say \"Add X and wire it up\" but the 'wiring' references symbols that");
        sb.AppendLine("     cannot be found in current file content or prior steps.");
        sb.AppendLine("For EACH self-inconsistency found, output it as a gap with afterStep = the step's index,");
        sb.AppendLine("missing = \"SELF-INCONSISTENT: [the issue]\", and include a corrected version");
        sb.AppendLine("of that step in correctedPlan (fixing its description to ADD rather than modify, etc.).");
        sb.AppendLine();
        sb.AppendLine("If coherent: {\"coherent\": true, \"gaps\": []}");
        sb.AppendLine("If not coherent (has gaps, redundant steps, or conflicting logic):");
        sb.AppendLine("{");
        sb.AppendLine("  \"coherent\": false,");
        sb.AppendLine("  \"gaps\": [");
        sb.AppendLine("    {\"afterStep\": 1, \"missing\": \"imagePreviews array\", \"usedBy\": \"Step 3 nextImage() and HTML template\"},");
        sb.AppendLine("    {\"afterStep\": 1, \"missing\": \"selectedImageIndex\", \"usedBy\": \"HTML *ngIf and Step 3\"}");
        sb.AppendLine("  ],");
        sb.AppendLine("  \"correctedPlan\": [");
        sb.AppendLine("    {\"file\": \"...\", \"change\": \"...\"}");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("correctedPlan must include ALL necessary steps (removing redundant/conflicting ones) PLUS new insertion steps in the correct order.");
        sb.AppendLine("Use the SAME property/method names consistently across all steps.");
        sb.AppendLine("Output ONLY JSON — no markdown, no explanation.");

        var (raw, _, err) = await CallLlmRaw(
            "You check code-change plan coherence across steps. Output ONLY valid JSON.",
            sb.ToString(), ct, TimeSpan.FromSeconds(45), maxTokens: 2048);

        if (string.IsNullOrWhiteSpace(raw))
        {
            await EmitLog(emitSse, "warn", $"Plan coherence check skipped: {err ?? "empty response"}", ct: ct);
            return plan;
        }

        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (m.Success) cleaned = m.Groups[1].Value.Trim();
            }
            var fb = cleaned.IndexOf('{'); var lb = cleaned.LastIndexOf('}');
            if (fb >= 0 && lb > fb) cleaned = cleaned[fb..(lb + 1)];

            using var doc = JsonDocument.Parse(cleaned, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;

            var coherent = root.TryGetProperty("coherent", out var cEl) && cEl.GetBoolean();
            if (coherent)
            {
                await EmitLog(emitSse, "info", "Plan coherence: ✓ steps form a coherent chain", ct: ct);
                return plan;
            }

            var gapSummaries = new List<string>();
            if (root.TryGetProperty("gaps", out var gapsEl) && gapsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var gap in gapsEl.EnumerateArray())
                {
                    var afterStep = gap.TryGetProperty("afterStep", out var asEl) ? asEl.GetInt32() : -1;
                    var missing = gap.TryGetProperty("missing", out var miEl) ? miEl.GetString() : "?";
                    var usedBy = gap.TryGetProperty("usedBy", out var ubEl) ? ubEl.GetString() : "";
                    var msg = $"gap after step {afterStep}: '{missing}'" +
                              (string.IsNullOrWhiteSpace(usedBy) ? "" : $" — needed by: {usedBy}");
                    gapSummaries.Add(msg);
                    await EmitLog(emitSse, "warn", $"Plan coherence {msg}", ct: ct);
                }
            }

            if (root.TryGetProperty("correctedPlan", out var cpArr) && cpArr.ValueKind == JsonValueKind.Array)
            {
                var corrected = new List<PlanStep>();
                foreach (var el in cpArr.EnumerateArray())
                {
                    var file = el.TryGetProperty("file", out var f) ? f.GetString() : null;
                    var change = el.TryGetProperty("change", out var c) ? c.GetString() : null;
                    if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(change)) continue;
                    var orig = plan.Plan.FirstOrDefault(p =>
                        string.Equals(p.File, file, StringComparison.OrdinalIgnoreCase));
                    corrected.Add(new PlanStep
                    {
                        File = file,
                        Change = change,
                        Priority = orig?.Priority ?? 1,
                        LineNumber = orig?.LineNumber ?? 0
                    });
                }

                if (corrected.Count >= plan.Plan.Count)
                {
                    var added = corrected.Count - plan.Plan.Count;
                    await EmitLog(emitSse, "info",
                        $"Plan coherence: inserted {added} missing step(s) to close {gapSummaries.Count} gap(s)", ct: ct);
                    plan.Plan = corrected;

                    if (emitSse)
                        await SendSse(Response, "plan", new
                        {
                            thinking = $"Coherence check found {gapSummaries.Count} gap(s) — inserted {added} step(s)",
                            summary = plan.Summary,
                            items = plan.Plan
                        }, ct);
                }
                else
                {
                    await EmitLog(emitSse, "warn",
                        $"Plan coherence: corrected plan ({corrected.Count} steps) is smaller than original " +
                        $"({plan.Plan.Count}) — keeping original to avoid data loss", ct: ct);
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(emitSse, "warn", $"Plan coherence check parse error: {ex.Message}", ct: ct);
        }

        return plan;
    }

    private static List<string> ExtractTopLevelCssSelectors(string css)
    {
        var selectors = new List<string>();
        if (string.IsNullOrWhiteSpace(css)) return selectors;

        var i = 0;
        var depth = 0;
        var selectorStart = 0;

        while (i < css.Length)
        {
            var c = css[i];

            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var endPos = end >= 0 ? end + 2 : css.Length;
                if (depth == 0) selectorStart = endPos;
                i = endPos;
                continue;
            }

            if (c == '"' || c == '\'')
            {
                i++;
                while (i < css.Length && css[i] != c)
                {
                    if (css[i] == '\\') i += 2;
                    else i++;
                }
                i++;
                continue;
            }

            if (c == '{' && depth == 0)
            {
                var selector = css[selectorStart..i].Trim();
                if (!string.IsNullOrWhiteSpace(selector))
                    selectors.Add(selector);
                var bodyDepth = 1;
                var j = i + 1;
                while (j < css.Length && bodyDepth > 0)
                {
                    if (css[j] == '{') bodyDepth++;
                    else if (css[j] == '}') bodyDepth--;
                    j++;
                }
                i = j;
                selectorStart = i;
                continue;
            }

            if (c == '@' && depth == 0)
            {
                var j = i;
                while (j < css.Length && css[j] != '{' && css[j] != ';') j++;
                if (j < css.Length && css[j] == ';')
                {
                    i = j + 1;
                    selectorStart = i;
                    continue;
                }
                var blockDepth = 1;
                var k = j + 1;
                while (k < css.Length && blockDepth > 0)
                {
                    if (css[k] == '{') blockDepth++;
                    else if (css[k] == '}') blockDepth--;
                    k++;
                }
                i = k;
                selectorStart = i;
                continue;
            }

            i++;
        }

        return selectors;
    }

    private static (string content, List<string> warnings) MergeDuplicateCssRules(string css)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(css)) return (css, warnings);

        var rules = new List<CssRule>();
        var i = 0;
        var depth = 0;
        var selectorStart = 0;

        while (i < css.Length)
        {
            var c = css[i];
            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var endPos = end >= 0 ? end + 2 : css.Length;
                if (depth == 0) selectorStart = endPos;
                i = endPos;
                continue;
            }

            if (c == '"' || c == '\'')
            {
                i++;
                while (i < css.Length && css[i] != c)
                {
                    if (css[i] == '\\') i += 2;
                    else i++;
                }
                i++;
                continue;
            }

            if (c == '{' && depth == 0)
            {
                var selector = css[selectorStart..i].Trim();
                var bodyStart = i + 1;
                var bodyDepth = 1;
                var j = bodyStart;
                while (j < css.Length && bodyDepth > 0)
                {
                    if (css[j] == '{') bodyDepth++;
                    else if (css[j] == '}') bodyDepth--;
                    if (bodyDepth > 0) j++;
                }
                var body = css[bodyStart..j];
                rules.Add(new CssRule
                {
                    Selector = selector,
                    Body = body,
                    Start = selectorStart,
                    End = j + 1
                });
                i = j + 1;
                selectorStart = i;
                continue;
            }

            if (c == '@' && depth == 0)
            {
                var j = i;
                while (j < css.Length && css[j] != '{' && css[j] != ';') j++;
                if (j < css.Length && css[j] == ';')
                {
                    i = j + 1;
                    selectorStart = i;
                    continue;
                }
                var blockDepth = 1;
                var k = j + 1;
                while (k < css.Length && blockDepth > 0)
                {
                    if (css[k] == '{') blockDepth++;
                    else if (css[k] == '}') blockDepth--;
                    if (blockDepth > 0) k++;
                }
                rules.Add(new CssRule
                {
                    Selector = css[selectorStart..(k + 1)],
                    Body = "",
                    Start = selectorStart,
                    End = k + 1,
                    IsAtRuleBlock = true
                });
                i = k + 1;
                selectorStart = i;
                continue;
            }

            i++;
        }

        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<(int firstIdx, int dupIdx)>();

        for (var idx = 0; idx < rules.Count; idx++)
        {
            var rule = rules[idx];
            if (rule.IsAtRuleBlock) continue;
            var norm = Regex.Replace(
                Regex.Replace(rule.Selector.ToLowerInvariant(), @"\s+", " ").Trim(),
                @"\s*,\s*", ",").Trim();
            if (seen.TryGetValue(norm, out var firstIdx))
            {
                duplicates.Add((firstIdx, idx));
                var lineApprox = css[..rule.Start].Count(ch => ch == '\n') + 1;
                warnings.Add($"Duplicate CSS selector '{rule.Selector}' — merging into first occurrence (line ~{lineApprox})");
            }
            else
            {
                seen[norm] = idx;
            }
        }

        if (duplicates.Count == 0) return (css, warnings);

        var merges = new Dictionary<int, List<int>>();
        foreach (var (firstIdx, dupIdx) in duplicates)
        {
            if (!merges.ContainsKey(firstIdx)) merges[firstIdx] = new List<int>();
            merges[firstIdx].Add(dupIdx);
        }

        var skipIndices = new HashSet<int>();
        foreach (var kvp in merges)
            foreach (var di in kvp.Value)
                skipIndices.Add(di);

        var result = new StringBuilder(css.Length);
        var lastEnd = 0;

        for (var idx = 0; idx < rules.Count; idx++)
        {
            var rule = rules[idx];
            if (skipIndices.Contains(idx))
            {
                lastEnd = rule.End;
                continue;
            }

            result.Append(css[lastEnd..rule.Start]);

            if (merges.TryGetValue(idx, out var dupIndices))
            {
                var propMap = new Dictionary<string, (string value, string indent)>(StringComparer.OrdinalIgnoreCase);
                var propOrder = new List<string>();

                foreach (var (prop, value, indent) in ParseCssProperties(rule.Body))
                {
                    if (!propMap.ContainsKey(prop)) propOrder.Add(prop);
                    propMap[prop] = (value, indent);
                }

                foreach (var dupIdx in dupIndices)
                {
                    foreach (var (prop, value, indent) in ParseCssProperties(rules[dupIdx].Body))
                    {
                        if (!propMap.ContainsKey(prop)) propOrder.Add(prop);
                        propMap[prop] = (value, indent.Length > 0 ? indent : "  ");
                    }
                }

                var bodySb = new StringBuilder();
                foreach (var prop in propOrder)
                {
                    var (value, indent) = propMap[prop];
                    bodySb.Append(indent);
                    bodySb.Append(prop);
                    bodySb.Append(": ");
                    bodySb.Append(value);
                    bodySb.Append(";\n");
                }
                if (bodySb.Length > 0 && bodySb[bodySb.Length - 1] == '\n')
                    bodySb.Length--;

                result.Append(rule.Selector);
                result.Append(" {\n");
                result.Append(bodySb);
                result.Append("\n}");
            }
            else
            {
                result.Append(css[rule.Start..rule.End]);
            }

            lastEnd = rule.End;
        }
        result.Append(css[lastEnd..]);

        return (result.ToString(), warnings);
    }

    private static List<(string prop, string value, string indent)> ParseCssProperties(string body)
    {
        var props = new List<(string, string, string)>();
        if (string.IsNullOrWhiteSpace(body)) return props;

        foreach (var line in body.Split('\n'))
        {
            var stripped = line.Trim();
            if (string.IsNullOrWhiteSpace(stripped)) continue;
            if (stripped.StartsWith("/*") || stripped.StartsWith("//")) continue;
            if (!stripped.EndsWith(';')) continue;

            var colonIdx = IndexOfFirstColonOutsideParensCss(stripped);
            if (colonIdx <= 0) continue;

            var prop = stripped[..colonIdx].Trim();
            var value = stripped[(colonIdx + 1)..].TrimEnd(';').Trim();
            var indent = LeadingWhitespaceCss(line);
            props.Add((prop, value, indent));
        }

        return props;
    }

    private sealed class CssRule
    {
        public string Selector { get; set; } = "";
        public string Body { get; set; } = "";
        public int Start { get; set; }
        public int End { get; set; }
        public bool IsAtRuleBlock { get; set; }
    }

    private async Task PersistBoardDataPlanAsync(string? cardId, List<PlanStep> planSteps, bool emitSse, CancellationToken ct,
        string summary = "", int score = 0, bool append = false)
    {
        if (string.IsNullOrWhiteSpace(cardId) || planSteps == null || planSteps.Count == 0)
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

                    var planItems = new JsonArray();
                    var existingItems = cardObj["_plan"]?.AsObject()?["items"] as JsonArray;
                    if (append && existingItems != null)
                    {
                        foreach (var existing in existingItems)
                        {
                            if (existing is JsonObject eo)
                            {
                                eo["done"] = true;
                                planItems.Add(eo.DeepClone());
                            }
                        }
                    }
                    for (var i = 0; i < planSteps.Count; i++)
                    {
                        var s = planSteps[i];
                        planItems.Add(new JsonObject
                        {
                            ["index"] = planItems.Count,
                            ["file"] = s.File,
                            ["change"] = s.Change,
                            ["priority"] = s.Priority,
                            ["line"] = s.LineNumber,
                            ["metaGroup"] = s.MetaGroup,
                            ["done"] = false
                        });
                    }

                    cardObj["_plan"] = new JsonObject
                    {
                        ["items"] = planItems,
                        ["summary"] = summary,
                        ["score"] = score
                    };

                    var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await _boardData.SaveRawAsync(saved);
                    if (emitSse)
                    {
                        await SendSse(Response, "refresh", new
                        {
                            target = "boarddata",
                            reason = "plan-updated",
                            cardId
                        }, ct);
                    }
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to persist full plan to boarddata", new { cardId, error = ex.Message });
        }
    }

    private async Task PersistCohesionToCardAsync(string? cardId, string relPath, List<string> issues, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardId) || issues == null)
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

                    cardObj["_cohesion"] = new JsonObject
                    {
                        ["file"] = relPath,
                        ["issues"] = new JsonArray(issues.Select(i => JsonValue.Create(i)).ToArray())
                    };

                    var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await _boardData.SaveRawAsync(saved);
                    if (emitSse)
                    {
                        await SendSse(Response, "cohesion", new
                        {
                            file = relPath,
                            issues
                        }, ct);
                    }
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to persist cohesion check to boarddata", new { cardId, error = ex.Message });
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

    private static readonly Regex MethodDeclRegex = new(
        @"(?:(?:public|private|protected|internal)\s+)?(?:(?:static|virtual|override|abstract|sealed|new|partial|async|unsafe)\s+)*(?:\w+(?:\[\])?(?:<[^>]*>)?)\s+(\w+)\s*\(([^)]*)\)",
        RegexOptions.Compiled);

    private async Task<int> HandleMethodSignatureChange(
        string fullPath, string relPath,
        string oldStr, string newStr,
        string projectRoot, bool emitSse, CancellationToken ct,
        int stepIndex, List<object> allResults, string? cardId)
    {
        var oldMatch = MethodDeclRegex.Match(oldStr);
        var newMatch = MethodDeclRegex.Match(newStr);
        if (!oldMatch.Success || !newMatch.Success)
            return stepIndex;

        var oldMethodName = oldMatch.Groups[1].Value;
        var newMethodName = newMatch.Groups[1].Value;
        if (!string.Equals(oldMethodName, newMethodName, StringComparison.Ordinal))
            return stepIndex;

        var oldParams = oldMatch.Groups[2].Value;
        var newParams = newMatch.Groups[2].Value;
        if (string.Equals(oldParams, newParams, StringComparison.Ordinal))
            return stepIndex;

        await EmitLog(emitSse, "info",
            $"Method signature change detected: {oldMethodName}({oldParams}) → {newMethodName}({newParams}). Searching for call sites...", ct: ct);

        var csFiles = new List<string>();
        try
        {
            if (Directory.Exists(projectRoot))
            {
                csFiles = Directory.GetFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
                    .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\") && !f.Contains("\\node_modules\\")
                             && !f.Contains("\\dist\\") && !f.Contains("\\.git\\"))
                    .OrderBy(f => f.Length)
                    .ToList();
            }
        }
        catch { return stepIndex; }

        if (csFiles.Count == 0)
        {
            await EmitLog(emitSse, "info", "No .cs files found in project to search for call sites.", ct: ct);
            return stepIndex;
        }

        var methodNameLower = oldMethodName.ToLowerInvariant();
        var candidateFiles = new List<string>();
        foreach (var f in csFiles)
        {
            if (string.Equals(f, fullPath, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                using var sr = new StreamReader(f, Encoding.UTF8);
                var firstFewKb = new char[4096];
                var read = await sr.ReadAsync(firstFewKb, 0, firstFewKb.Length);
                var head = new string(firstFewKb, 0, read);
                if (head.Contains(methodNameLower, StringComparison.OrdinalIgnoreCase))
                    candidateFiles.Add(f);
            }
            catch { }
        }

        if (candidateFiles.Count == 0)
        {
            await EmitLog(emitSse, "info", "No call site files found.", ct: ct);
            return stepIndex;
        }

        await EmitLog(emitSse, "info",
            $"Found {candidateFiles.Count} file(s) containing '{oldMethodName}' — checking for call sites...", ct: ct);

        foreach (var candidateFile in candidateFiles)
        {
            ct.ThrowIfCancellationRequested();

            var fileContent = await System.IO.File.ReadAllTextAsync(candidateFile, Encoding.UTF8, ct);
            var candidateRelPath = Path.GetRelativePath(projectRoot, candidateFile).Replace('\\', '/');

            var callSitePrompt = $@"File: {candidateRelPath}

METHOD SIGNATURE CHANGED:
Old: `{oldMethodName}({oldParams})`
New: `{newMethodName}({newParams})`

The file above contains one or more calls to `{oldMethodName}` that may need updating because the method's signature changed.

Search through the ENTIRE file content below and find EVERY occurrence of `{oldMethodName}(`. For each call site found:
1. Determine the correct new call based on the new signature
2. Output the edits needed

FILE CONTENT:
```csharp
{fileContent}
```

For each call site that needs updating, output a JSON array:
[
  {{""oldString"": ""exact text of the old call"", ""newString"": ""exact text of the updated call""}}
]

If no call sites need updating, output an empty array [].
Reply ONLY with the JSON array — no explanation, no markdown.";

            var (callSitesJson, _, _) = await CallLlmRaw(
                "You are a code refactoring assistant. Update method call sites to match a changed signature. Output only JSON.",
                callSitePrompt, ct, TimeSpan.FromSeconds(30), maxTokens: 4096);

            if (string.IsNullOrWhiteSpace(callSitesJson))
                continue;

            var cleanJson = callSitesJson.Trim();
            if (cleanJson.StartsWith("```"))
            {
                var m = Regex.Match(cleanJson, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (m.Success) cleanJson = m.Groups[1].Value.Trim();
            }

            List<Dictionary<string, string>>? callSiteEdits = null;
            try { callSiteEdits = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(cleanJson); }
            catch { }

            if (callSiteEdits == null || callSiteEdits.Count == 0)
                continue;

            await EmitLog(emitSse, "info",
                $"  {candidateRelPath}: {callSiteEdits.Count} call site edit(s) suggested", ct: ct);

            var fileContentMut = fileContent;
            var appliedCount = 0;
            foreach (var edit in callSiteEdits)
            {
                if (!edit.TryGetValue("oldString", out var callOld) || string.IsNullOrWhiteSpace(callOld))
                    continue;
                if (!edit.TryGetValue("newString", out var callNew))
                    callNew = "";

                var (replaced, newContent, _, _) = TryReplaceSafe(fileContentMut, callOld, callNew);
                if (replaced)
                {
                    fileContentMut = newContent;
                    appliedCount++;

                    stepIndex++;
                    var stepResult = new Dictionary<string, object?>
                    {
                        ["index"] = stepIndex,
                        ["type"] = "edit",
                        ["status"] = "modified",
                        ["path"] = candidateRelPath,
                        ["description"] = $"Updated call site: {oldMethodName} → {newMethodName}",
                        ["planItemIndex"] = -1,
                        ["parentStep"] = relPath,
                        ["methodSignature"] = $"{oldMethodName}({oldParams}) → {newMethodName}({newParams})"
                    };
                    allResults.Add(stepResult);
                    if (emitSse)
                        await SendSse(Response, "step", stepResult, ct);
                }
            }

            if (appliedCount > 0)
            {
                await System.IO.File.WriteAllTextAsync(candidateFile, fileContentMut, Encoding.UTF8, ct);
                await EmitLog(emitSse, "success",
                    $"  ✓ Updated {appliedCount} call site(s) in {candidateRelPath}", ct: ct);
            }
        }

        return stepIndex;
    }
    private static string BuildIncrementalStepSystemPrompt() =>
    "You are a senior autonomous coding agent building a code-change plan ONE STEP AT A TIME.\n" +
    "You will be shown the task, the discovered file contents, and the PLAN SO FAR (steps already committed).\n" +
    "Your job on EACH turn is to propose exactly ONE new step — the next atomic action required — " +
    "or declare the plan complete if no further step is needed.\n\n" +
    "Output ONLY valid JSON — no markdown fences, no prose outside the JSON.\n\n" +
    "### DECISION ###\n" +
    "If the plan-so-far, together with the discovery context, already fully satisfies the task:\n" +
    "{\"planComplete\": true, \"completionReason\": \"one sentence: why nothing more is needed\"}\n\n" +
    "If you need to read a file not yet in discovery context before you can safely propose the next step:\n" +
    "{\"planComplete\": false, \"exploreFile\": \"{path/to/FILE_TO_EXPLORE.ext}\", \"thinking\": \"why you need this file\"}\n\n" +
    "Otherwise, propose exactly ONE next step:\n" +
    "{\n" +
    "  \"planComplete\": false,\n" +
    "  \"thinking\": \"1-2 sentences: why this is the correct NEXT step given what's already planned\",\n" +
    "  \"step\": {\n" +
    "    \"file\": \"{path/to/TARGET_FILE}.ext, or a marker: _create_file/_command/_web_search/_web_fetch/_git/_rename_file/_delete_file/_show/_checkpoint\",\n" +
    "    \"change\": \"precise, atomic description of ONLY this step's change\",\n" +
    "    \"line\": 42,\n" +
    "    \"referenceFiles\": [\"{path/to/REFERENCE_FILE}.ext\"]\n" +
    "  },\n" +
    "  \"justification\": \"why this step must happen NOW relative to steps already committed " +
    "(e.g. 'this DTO property must exist before step 2's endpoint can reference it')\"\n" +
    "}\n\n" +
    "### RULES ###\n" +
    "1. ONE step per turn. Never propose multiple steps or a 'plan' array.\n" +
    "2. The step MUST be atomic: one coherent edit at one location in one file. If the natural next " +
    "   action touches two locations (e.g. add a field AND initialize it in a constructor), propose ONLY " +
    "   the first location now — the second location gets its own turn later.\n" +
    "3. NEVER repeat or restate a step already present in PLAN SO FAR.\n" +
    "4. NEVER propose a step that assumes a method/property/symbol exists unless it is already visible in " +
    "   the discovery context OR was introduced by an earlier committed step. NEVER explore files outside the " +
    "   attached files listed in the task or steering. Only explore to find a symbol definition when the symbol " +
    "   is referenced in an already-attached file.\n" +
    "5. Respect dependency order: DTOs/models before endpoints that use them, backend before frontend, " +
    "   services before UI code that calls them.\n" +
    "6. CREATE TABLE IF NOT EXISTS belongs INSIDE the method body that needs it — never its own step.\n" +
    "7. Prefer FEWER, more complete steps. If the whole task is one coherent edit, propose that one step, " +
    "   then declare planComplete=true on the next turn.\n" +
    "8. If your last proposal was REJECTED (see REJECTED ATTEMPTS), do not repeat the same mistake. " +
    "   Read the discovery context more carefully and fix the symbol references instead of trying to explore " +
    "   unrelated files.\n" +
    "9. Stop as soon as the task is fully satisfied — never propose steps the user did not ask for.\n" +
    "10. NEVER propose a 'locate', 'find', 'examine', 'understand', 'read', 'explore', 'look at', 'inspect', 'review', 'check', 'see', 'search' step. " +
    "You already have the full file content in the discovery context. Every step MUST make an actual code change " +
    "(add, modify, delete, replace, rename, etc.). If you need to understand code before editing, do it in your thinking, not in a separate step.\n";

    private static string BuildIncrementalStepUserPrompt(
        string originalPrompt, string discoveryContext, List<PlanStep> planSoFar,
        string? steeringContext, List<string> rejectionFeedback)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### TASK ###");
        sb.AppendLine(originalPrompt);
        if (!string.IsNullOrWhiteSpace(steeringContext))
        {
            sb.AppendLine();
            sb.AppendLine("### STEERING ###");
            sb.AppendLine(steeringContext);
        }
        sb.AppendLine();
        sb.AppendLine("### DISCOVERY CONTEXT (only reference paths/content shown here) ###");
        sb.AppendLine(BuildPlannerDiscoveryContext(discoveryContext));
        sb.AppendLine();
        sb.AppendLine("### PLAN SO FAR (already committed — do NOT repeat these) ###");
        if (planSoFar.Count == 0)
        {
            sb.AppendLine("(empty — this will be the first step)");
        }
        else
        {
            for (var i = 0; i < planSoFar.Count; i++)
                sb.AppendLine($"  Step {i + 1}: [{planSoFar[i].File}] {planSoFar[i].Change}");
        }
        if (rejectionFeedback.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### REJECTED ATTEMPTS FOR THE NEXT STEP (fix these issues) ###");
            foreach (var r in rejectionFeedback) sb.AppendLine($"  - {r}");
        }
        sb.AppendLine();
        sb.AppendLine("Propose the NEXT step now, or declare the plan complete. Output ONLY JSON.");
        return sb.ToString();
    }

    private async Task<IncrementalStepProposal?> ProposeNextIncrementalStepAsync(
        string originalPrompt, string discoveryContext, List<PlanStep> planSoFar,
        string? steeringContext, List<string> rejectionFeedback, bool emitSse, CancellationToken ct)
    {
        var sys = BuildIncrementalStepSystemPrompt();
        var user = BuildIncrementalStepUserPrompt(originalPrompt, discoveryContext, planSoFar, steeringContext, rejectionFeedback);

        var (raw, _, err) = await CallLlmRawStreaming(sys, user, emitSse, ct,
            requestTimeout: TimeSpan.FromMinutes(2), maxTokens: 700);

        if (string.IsNullOrWhiteSpace(raw))
        {
            await EmitLog(emitSse, "warn", $"Incremental step proposal returned empty: {err}", ct: ct);
            return null;
        }

        try
        {
            var cleaned = ExtractFirstJsonObject(raw);
            using var doc = JsonDocument.Parse(cleaned, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;

            var complete = root.TryGetProperty("planComplete", out var pc) && pc.ValueKind == JsonValueKind.True;
            var completionReason = root.TryGetProperty("completionReason", out var cr) ? cr.GetString() : null;
            var thinking = root.TryGetProperty("thinking", out var th) ? th.GetString() : null;
            var exploreFile = root.TryGetProperty("exploreFile", out var ef) && ef.ValueKind == JsonValueKind.String
                ? ef.GetString() : null;

            if (complete)
                return new IncrementalStepProposal { PlanComplete = true, CompletionReason = completionReason, Thinking = thinking };

            if (!string.IsNullOrWhiteSpace(exploreFile))
                return new IncrementalStepProposal { PlanComplete = false, ExploreFile = exploreFile, Thinking = thinking };

            if (root.TryGetProperty("step", out var maybeExploreStep) && maybeExploreStep.ValueKind == JsonValueKind.Object)
            {
                var maybeFile = maybeExploreStep.TryGetProperty("file", out var mfEl) ? mfEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(maybeFile) &&
                    (maybeFile.StartsWith("_explore", StringComparison.OrdinalIgnoreCase)))
                {
                    var maybeChange = maybeExploreStep.TryGetProperty("change", out var mcEl) ? mcEl.GetString() : "";
                    var pathMatch = Regex.Match(maybeChange ?? "", @"[\w./\\-]+\.\w{1,5}\b");
                    var target = pathMatch.Success ? pathMatch.Value : maybeChange;
                    if (!string.IsNullOrWhiteSpace(target))
                        return new IncrementalStepProposal { PlanComplete = false, ExploreFile = target, Thinking = thinking };
                }
            }

            if (!root.TryGetProperty("step", out var stepEl) || stepEl.ValueKind != JsonValueKind.Object)
                return new IncrementalStepProposal { PlanComplete = false, Thinking = thinking };

            var file = stepEl.TryGetProperty("file", out var fEl) ? fEl.GetString() : null;
            var change = stepEl.TryGetProperty("change", out var cEl) ? cEl.GetString() : null;
            var line = stepEl.TryGetProperty("line", out var lEl) && lEl.ValueKind == JsonValueKind.Number ? lEl.GetInt32() : 0;

            var refFiles = new List<string>();
            if (stepEl.TryGetProperty("referenceFiles", out var rfArr) && rfArr.ValueKind == JsonValueKind.Array)
                foreach (var rf in rfArr.EnumerateArray())
                    if (rf.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(rf.GetString()))
                        refFiles.Add(rf.GetString()!);

            if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(change))
                return new IncrementalStepProposal { PlanComplete = false, Thinking = thinking };

            var justification = root.TryGetProperty("justification", out var jEl) ? jEl.GetString() : null;

            return new IncrementalStepProposal
            {
                PlanComplete = false,
                Thinking = thinking,
                CompletionReason = justification,
                Step = new PlanStep
                {
                    File = file!.Replace('\\', '/'),
                    Change = change!,
                    Priority = 1,
                    LineNumber = line,
                    ReferenceFiles = refFiles
                }
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<(bool valid, string? reason)> ValidateIncrementalStepAsync(
        PlanStep step, string originalPrompt, string discoveryContext, List<PlanStep> planSoFar,
        string projectRoot, bool emitSse, CancellationToken ct,
        bool skipLlm = false)
    {
        if (string.IsNullOrWhiteSpace(step.File) || string.IsNullOrWhiteSpace(step.Change))
            return (false, "Step is missing file or change description.");

        var normNew = NormalizeChangeForDedup(step.Change);
        foreach (var existing in planSoFar)
        {
            if (!string.Equals(existing.File, step.File, StringComparison.OrdinalIgnoreCase)) continue;
            var normExisting = NormalizeChangeForDedup(existing.Change);
            if (normNew == normExisting || CalculateChangeSimilarity(normNew, normExisting) >= 0.82)
                return (false, $"Duplicates an already-committed step targeting {existing.File}: \"{existing.Change}\".");
        }

        var changeLower = step.Change.ToLowerInvariant();

        var researchVerbs = new[] { "locate", "find", "examine", "understand", "read", "explore", "look at", "inspect", "review", "check", "see", "search" };
        if (researchVerbs.Any(v => changeLower.StartsWith(v)))
        {
            return (false, $"Research step rejected — '{changeLower.Split(' ')[0]}' is not an actionable edit. " +
                            "All steps must make actual code changes (add/modify/delete/replace). " +
                            "The file content is already available in the discovery context.");
        }

        if (changeLower.StartsWith("remove") || changeLower.StartsWith("delete"))
        {
            var targetMatch = Regex.Match(step.Change, @"remove\s+(?:the\s+)?(\w+)", RegexOptions.IgnoreCase);
            if (targetMatch.Success)
            {
                var target = targetMatch.Groups[1].Value;
                var contradicts = planSoFar.Any(p =>
                    string.Equals(p.File, step.File, StringComparison.OrdinalIgnoreCase) &&
                    Regex.IsMatch(p.Change ?? "", $@"\b(add|create|insert)\b.*\b{Regex.Escape(target)}\b", RegexOptions.IgnoreCase));
                if (contradicts)
                    return (false, $"Removes '{target}' but an earlier committed step just added it — contradicts the plan so far.");
            }
        }

        var isSpecial = AgentUtilities.IsSpecialMarker(step.File);

        if (!isSpecial && AgentUtilities.IsRelativePath(step.File))
        {
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, step.File.Replace('/', Path.DirectorySeparatorChar)));
            var isModifyVerb = Regex.IsMatch(changeLower, @"^\s*(modify|update|change|replace|fix)\b");
            var fileExists = System.IO.File.Exists(fullPath);
            var willBeCreatedEarlier = planSoFar.Any(p =>
                (p.File.Equals("_create_file", StringComparison.OrdinalIgnoreCase) ||
                 p.File.Equals("_command", StringComparison.OrdinalIgnoreCase)) &&
                (p.Change ?? "").Contains(Path.GetFileName(step.File), StringComparison.OrdinalIgnoreCase));

            if (isModifyVerb && !fileExists && !willBeCreatedEarlier)
                return (false, $"Says '{changeLower.Split(' ')[0]}' but {step.File} does not exist yet and no earlier " +
                                "step creates it. Add a creation step first, or rephrase as a creation ('Add ...').");

            if (fileExists)
            {
                var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                var (verdict, reason) = PreEditValidation(content, step);
                if (verdict == PreEditVerdict.AlreadyDone)
                    return (false, $"Already satisfied in the current file — {reason}. Move on to the next requirement.");
            }
        }

        if (isSpecial) return (true, null);

        if (skipLlm)
        {
            await EmitLog(emitSse, "info", $"LLM validator skipped (retry mode) — accepting step: [{step.File}] {step.Change}", ct: ct);
            return (true, null);
        }

        var sb = new StringBuilder();
        sb.AppendLine("### ORIGINAL TASK ###");
        sb.AppendLine(originalPrompt);
        sb.AppendLine();
        sb.AppendLine("### PLAN SO FAR (already committed, in order) ###");
        if (planSoFar.Count == 0) sb.AppendLine("(none yet — this would be the first step)");
        else for (var i = 0; i < planSoFar.Count; i++) sb.AppendLine($"  Step {i + 1}: [{planSoFar[i].File}] {planSoFar[i].Change}");
        sb.AppendLine();
        sb.AppendLine("### PROPOSED NEXT STEP ###");
        sb.AppendLine($"[{step.File}] {step.Change}");
        sb.AppendLine();
        sb.AppendLine("### RELEVANT DISCOVERY CONTEXT (target file only) ###");
        var validatorDiscovery = StripEditKnowledgeHeader(discoveryContext);
        var fileSection = AgentUtilities.ExtractFileSectionFromContext(validatorDiscovery, step.File);
        sb.AppendLine(string.IsNullOrWhiteSpace(fileSection) ? validatorDiscovery : fileSection);
        sb.AppendLine();
        sb.AppendLine("Judge the PROPOSED NEXT STEP ONLY. Answer:");
        sb.AppendLine("1. Does it reference any method/property/symbol that does NOT exist in the discovery context " +
                      "AND is NOT introduced by an earlier committed step? (if so: invalid)");
        sb.AppendLine("2. Does it contradict or redo anything already committed? (if so: invalid)");
        sb.AppendLine("3. Does it require a prerequisite step not yet committed (e.g. an endpoint before its DTO)? (if so: invalid)");
        sb.AppendLine("4. Is it a genuinely necessary, atomic step toward the ORIGINAL TASK (not scope creep)? (if not: invalid)");
        sb.AppendLine();
        sb.AppendLine("Output ONLY JSON: {\"valid\": true|false, \"reason\": \"short reason, only if invalid\"}");

        var (raw, _, err) = await CallLlmRaw(
            "You are a strict plan-coherence validator. Output ONLY the requested JSON.",
            sb.ToString(), ct, TimeSpan.FromSeconds(25), maxTokens: 200);

        if (string.IsNullOrWhiteSpace(raw))
        {
            await EmitLog(emitSse, "warn", $"Coherence validator call failed ({err}) — accepting step by default.", ct: ct);
            return (true, null);
        }

        try
        {
            var cleaned = ExtractFirstJsonObject(raw);
            using var doc = JsonDocument.Parse(cleaned);
            var valid = !doc.RootElement.TryGetProperty("valid", out var v) || v.ValueKind != JsonValueKind.False;
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
            return (valid, valid ? null : (reason ?? "Rejected by coherence validator."));
        }
        catch
        {
            return (true, null);
        }
    }

    private async Task<(AgentPlan plan, string discoveryContext)> RunIncrementalPlanningLoop(
        string prompt, string discoveryContext, string projectRoot, bool emitSse,
        CancellationToken ct, string? steeringContext, string? cardId = null)
    {
        var planSoFar = new List<PlanStep>();
        var rejectionFeedback = new List<string>();
        var exploredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var thinkingLog = new StringBuilder();
        var regenAttempts = 0;
        var consecutiveSlotFailures = 0;
        var stepEventIndex = 0;

        await EmitLog(emitSse, "info", "Incremental planning: proposing steps one at a time…", ct: ct);

        if (emitSse)
            await SendSse(Response, "plan", new
            {
                thinking = "",
                summary = "Building plan incrementally — 0 steps so far",
                items = Array.Empty<PlanStep>(),
                incremental = true
            }, ct);

        for (var turn = 0; turn < MAX_INCREMENTAL_STEPS; turn++)
        {
            ct.ThrowIfCancellationRequested();

            if (emitSse)
                await SendSse(Response, "phase", new { message = $"Planning — step {planSoFar.Count + 1}/{MAX_INCREMENTAL_STEPS}" }, ct);

            var proposal = await ProposeNextIncrementalStepAsync(
                prompt, discoveryContext, planSoFar, steeringContext, rejectionFeedback, emitSse, ct);

            if (proposal == null)
            {
                rejectionFeedback.Add("Your previous response could not be parsed as valid JSON. " +
                    "Output ONLY the JSON object described in the system prompt.");
                if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                continue;
            }

            if (proposal.PlanComplete)
            {
                if (emitSse)
                    await SendSse(Response, "thinking", new { text = $"Plan complete: {proposal.CompletionReason}" }, ct);
                await EmitLog(emitSse, "success",
                    $"Incremental planning: plan complete after {planSoFar.Count} step(s) — {proposal.CompletionReason}", ct: ct);
                break;
            }

            if (!string.IsNullOrWhiteSpace(proposal.ExploreFile))
            {
                if (emitSse)
                    await SendSse(Response, "step", new
                    {
                        index = ++stepEventIndex,
                        type = "explore",
                        status = "exploring",
                        path = proposal.ExploreFile,
                        description = $"Exploring: {proposal.ExploreFile}",
                        planItemIndex = planSoFar.Count,
                        message = proposal.Thinking ?? ""
                    }, ct);

                if (exploredFiles.Add(proposal.ExploreFile))
                {
                    var isMarker = proposal.ExploreFile.StartsWith("_");
                    var alreadyInContext = false;
                    if (!isMarker)
                    {
                        var normPath = proposal.ExploreFile.Replace('\\', '/').TrimStart('/');
                        alreadyInContext = discoveryContext.Contains($"### read {normPath}") ||
                                           discoveryContext.Contains($"### {normPath}") ||
                                           Regex.IsMatch(discoveryContext, $@"### (?:read )?\S*{Regex.Escape(Path.GetFileName(normPath))}\b");
                    }

                    if (alreadyInContext)
                    {
                        var contextMsg = $"STOP — '{proposal.ExploreFile}' is ALREADY in the DISCOVERY CONTEXT above (its full content is already shown). " +
                            "Do NOT request it again. Read the file content from the DISCOVERY CONTEXT and propose the actual edit step now.";
                        await EmitLog(emitSse, "warn",
                            $"Incremental planning: skipped explore — {proposal.ExploreFile} already in discovery context", ct: ct);
                        rejectionFeedback.Add(contextMsg);
                        if (emitSse)
                            await SendSse(Response, "step", new
                            {
                                index = ++stepEventIndex,
                                type = "explore",
                                status = "error",
                                path = proposal.ExploreFile,
                                description = $"Already in context: {proposal.ExploreFile}",
                                error = contextMsg
                            }, ct);
                        if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                        continue;
                    }

                    await EmitLog(emitSse, "info", $"Incremental planning: exploring {proposal.ExploreFile}", ct: ct);
                    discoveryContext = await ExplorationPipeline(
                        new List<PlanStep> { new() { File = "_explore", Change = proposal.ExploreFile } },
                        discoveryContext, projectRoot, emitSse, ct);
                    if (emitSse)
                        await SendSse(Response, "step", new
                        {
                            index = ++stepEventIndex,
                            type = "explore",
                            status = "done",
                            path = proposal.ExploreFile,
                            description = $"Explored: {proposal.ExploreFile}"
                        }, ct);

                    if (!string.IsNullOrWhiteSpace(cardId))
                    {
                        await AutoAttachFileToCardAsync(cardId, proposal.ExploreFile, emitSse, ct);
                    }
                    regenAttempts = 0;
                    continue;
                }
                else
                {
                    rejectionFeedback.Add(
                        $"You asked to explore '{proposal.ExploreFile}' again — it is ALREADY shown in full in the " +
                        "DISCOVERY CONTEXT above. Do not re-request it. Read it carefully and propose the actual next " +
                        "step now, using the exact symbol/method names visible there.");
                    if (emitSse)
                    {
                        await SendSse(Response, "step", new
                        {
                            index = ++stepEventIndex,
                            type = "explore",
                            status = "error",
                            path = proposal.ExploreFile,
                            description = $"Already explored: {proposal.ExploreFile}"
                        }, ct);
                    }
                    if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) { break; }
                    continue;
                }
            }

            if (proposal.Step == null)
            {
                rejectionFeedback.Add("You returned neither planComplete=true, exploreFile, nor a step — return exactly one.");
                if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                continue;
            }

            if (emitSse && !string.IsNullOrWhiteSpace(proposal.Thinking))
            {
                await SendSse(Response, "thinking", new { text = proposal.Thinking }, ct);
            }
            var stepIndex = ++stepEventIndex;
            if (emitSse)
            {
                await SendSse(Response, "step", new
                {
                    index = stepIndex,
                    type = "plan",
                    status = "proposing",
                    path = proposal.Step.File,
                    description = proposal.Step.Change,
                    line = proposal.Step.LineNumber,
                    planItemIndex = planSoFar.Count,
                    thinking = proposal.Thinking,
                    justification = proposal.CompletionReason
                }, ct);
            }
            var skipLlm = regenAttempts > 0;
            if (!skipLlm && proposal.Step != null && !AgentUtilities.IsSpecialMarker(proposal.Step.File))
            {
                var fullPath = Path.GetFullPath(Path.Combine(projectRoot,
                    proposal.Step.File.Replace('/', Path.DirectorySeparatorChar)));
                if (System.IO.File.Exists(fullPath))
                    skipLlm = true;
            }

            if (proposal.Step != null)
            {
                var (valid, reason) = await ValidateIncrementalStepAsync(
                    proposal.Step, prompt, discoveryContext, planSoFar, projectRoot, emitSse, ct,
                    skipLlm: skipLlm);
                if (!valid)
                {
                    await EmitLog(emitSse, "warn",
                        $"Incremental planning: rejected [{proposal.Step.File}] {proposal.Step.Change} — {reason}", ct: ct);
                    rejectionFeedback.Add($"REJECTED — [{proposal.Step.File}] {proposal.Step.Change} → {reason}");
                    if (emitSse)
                        await SendSse(Response, "step", new
                        {
                            index = stepIndex,
                            type = "plan",
                            status = "rejected",
                            path = proposal.Step.File,
                            description = proposal.Step.Change,
                            error = reason,
                            line = proposal.Step.LineNumber,
                            planItemIndex = planSoFar.Count
                        }, ct);
                    if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS)
                    {
                        consecutiveSlotFailures++;
                        await EmitLog(emitSse, "warn",
                            $"Incremental planning: giving up on this slot after {MAX_STEP_REGEN_ATTEMPTS} rejections — moving on. " +
                            $"({consecutiveSlotFailures} consecutive slot failures)", ct: ct);
                        if (emitSse)
                            await SendSse(Response, "thinking", new
                            {
                                text = $"Giving up on this slot after {MAX_STEP_REGEN_ATTEMPTS} rejections — moving on. ({consecutiveSlotFailures} consecutive slot failures)"
                            }, ct);
                        rejectionFeedback.Clear();
                        regenAttempts = 0;

                        if (consecutiveSlotFailures >= 3)
                            throw new InvalidOperationException(
                                "Incremental planner failed 3 slots in a row — the discovery context likely doesn't contain " +
                                "what the task needs (wrong file attached, or the target method/property doesn't exist as described). " +
                                "Attach the correct file(s) and retry.");
                        continue;
                    }
                    continue;
                }

                consecutiveSlotFailures = 0;
                planSoFar.Add(proposal.Step);
                rejectionFeedback.Clear();
                regenAttempts = 0;
                if (!string.IsNullOrWhiteSpace(proposal.Thinking))
                {
                    thinkingLog.AppendLine($"Step {planSoFar.Count}: {proposal.Thinking}");
                }
                await EmitLog(emitSse, "info",
                    $"Incremental planning: committed step {planSoFar.Count} — [{proposal.Step.File}] {proposal.Step.Change}", ct: ct);

                if (emitSse)
                {
                    await SendSse(Response, "step", new
                    {
                        index = stepIndex,
                        type = "plan",
                        status = "pending",
                        path = proposal.Step.File,
                        description = proposal.Step.Change,
                        line = proposal.Step.LineNumber,
                        planItemIndex = planSoFar.Count,
                        message = proposal.CompletionReason
                    }, ct);

                    await SendSse(Response, "plan", new
                    {
                        thinking = thinkingLog.ToString(),
                        summary = $"Building plan incrementally — {planSoFar.Count} step(s) so far",
                        items = planSoFar,
                        incremental = true
                    }, ct);
                }
            }
            break;
        }

        if (planSoFar.Count == 0)
            throw new InvalidOperationException("Incremental planner did not produce any actionable steps.");

        var plan = new AgentPlan
        {
            Thinking = thinkingLog.ToString(),
            Summary = $"Built incrementally across {planSoFar.Count} validated step(s)",
            Score = 90,
            Plan = planSoFar
        };

        return (plan, discoveryContext);
    }
    private static string BuildIncrementalSubPlanSystemPrompt() =>
    "You are a senior software architect building a MULTI-STAGE execution plan ONE STAGE AT A TIME.\n" +
    "Each stage ('sub-plan') is a self-contained deliverable — a concrete file (or small related set) with a " +
    "precise, atomic modification. Stages execute strictly in the order you produce them, and each later stage " +
    "can rely on symbols introduced by earlier ones.\n\n" +
    "Output ONLY valid JSON — no markdown fences, no prose outside the JSON.\n\n" +
    "If the STAGES SO FAR already fully cover everything the task requires:\n" +
    "{\"metaPlanComplete\": true, \"completionReason\": \"why nothing more is needed\"}\n\n" +
    "Otherwise, propose exactly ONE next stage:\n" +
    "{\n" +
    "  \"metaPlanComplete\": false,\n" +
    "  \"thinking\": \"1-2 sentences: why this is the correct NEXT deliverable given what's already staged\",\n" +
    "  \"subPlan\": {\n" +
    "    \"title\": \"Concrete deliverable with exact file path(s)\",\n" +
    "    \"description\": \"Exact files and exact modifications for THIS stage only\",\n" +
    "    \"files\": [\"relative/path.ext\"],\n" +
    "    \"contextNote\": \"Concrete symbol names/schemas THIS stage introduces, for later stages to reference\"\n" +
    "  }\n" +
    "}\n\n" +
    "### RULES ###\n" +
    "1. ONE sub-plan per turn.\n" +
    "2. Order dependencies correctly: data models/DTOs before endpoints, backend before frontend, services before UI.\n" +
    "3. ATOMICITY: table creation lives INSIDE the endpoint method that needs it, never its own stage. " +
    "   A method's signature and body are one stage, not two.\n" +
    "4. NEVER repeat a stage already listed in STAGES SO FAR.\n" +
    "5. If the whole task fits in ONE stage, propose that single stage, then declare metaPlanComplete=true next turn.\n" +
    "6. Only split into multiple stages when the task genuinely spans multiple files/layers — never manufacture " +
    "   stages for a single-file change.\n";

    private static string BuildIncrementalSubPlanUserPrompt(
        string originalPrompt, string discoveryContext, List<MetaPlanSubPlan> subPlansSoFar, List<string> rejectionFeedback)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### TASK ###");
        sb.AppendLine(originalPrompt);
        sb.AppendLine();
        sb.AppendLine("### DISCOVERY CONTEXT ###");
        var ctx = discoveryContext.Length > 8000 ? discoveryContext[..8000] + "\n...(truncated)" : discoveryContext;
        sb.AppendLine(ctx);
        sb.AppendLine();
        sb.AppendLine("### STAGES SO FAR (already committed, in execution order) ###");
        if (subPlansSoFar.Count == 0) sb.AppendLine("(none yet — this will be the first stage)");
        else for (var i = 0; i < subPlansSoFar.Count; i++)
            sb.AppendLine($"  Stage {i + 1}: {subPlansSoFar[i].Title} — {subPlansSoFar[i].Description}");
        if (rejectionFeedback.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### REJECTED ATTEMPTS (fix these) ###");
            foreach (var r in rejectionFeedback) sb.AppendLine($"  - {r}");
        }
        sb.AppendLine();
        sb.AppendLine("Propose the NEXT stage now, or declare the meta-plan complete. Output ONLY JSON.");
        return sb.ToString();
    }

    private async Task<IncrementalSubPlanProposal?> ProposeNextSubPlanAsync(
        string originalPrompt, string discoveryContext, List<MetaPlanSubPlan> subPlansSoFar,
        List<string> rejectionFeedback, bool emitSse, CancellationToken ct)
    {
        var sys = BuildIncrementalSubPlanSystemPrompt();
        var user = BuildIncrementalSubPlanUserPrompt(originalPrompt, discoveryContext, subPlansSoFar, rejectionFeedback);
        var (raw, _, err) = await CallLlmRawStreaming(sys, user, emitSse, ct, TimeSpan.FromMinutes(2), maxTokens: 500);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            var cleaned = ExtractFirstJsonObject(raw);
            using var doc = JsonDocument.Parse(cleaned, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;
            var complete = root.TryGetProperty("metaPlanComplete", out var mc) && mc.ValueKind == JsonValueKind.True;
            var reason = root.TryGetProperty("completionReason", out var cr) ? cr.GetString() : null;
            var thinking = root.TryGetProperty("thinking", out var th) ? th.GetString() : null;

            if (complete)
                return new IncrementalSubPlanProposal { MetaPlanComplete = true, CompletionReason = reason, Thinking = thinking };

            if (!root.TryGetProperty("subPlan", out var spEl) || spEl.ValueKind != JsonValueKind.Object)
                return new IncrementalSubPlanProposal { MetaPlanComplete = false, Thinking = thinking };

            var title = spEl.TryGetProperty("title", out var tEl) ? tEl.GetString() : null;
            var desc = spEl.TryGetProperty("description", out var dEl) ? dEl.GetString() : null;
            var note = spEl.TryGetProperty("contextNote", out var nEl) ? nEl.GetString() : "";
            var files = new List<string>();
            if (spEl.TryGetProperty("files", out var fArr) && fArr.ValueKind == JsonValueKind.Array)
                foreach (var f in fArr.EnumerateArray())
                    if (f.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(f.GetString()))
                        files.Add(f.GetString()!);

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(desc))
                return new IncrementalSubPlanProposal { MetaPlanComplete = false, Thinking = thinking };

            return new IncrementalSubPlanProposal
            {
                MetaPlanComplete = false,
                Thinking = thinking,
                SubPlan = new MetaPlanSubPlan
                {
                    Id = $"sp-{subPlansSoFar.Count + 1}",
                    Title = title!,
                    Description = desc!,
                    ContextNote = note ?? "",
                    Files = files
                }
            };
        }
        catch { return null; }
    }

    private async Task<(bool valid, string? reason)> ValidateSubPlanAsync(
        MetaPlanSubPlan subPlan, string originalPrompt, List<MetaPlanSubPlan> subPlansSoFar, CancellationToken ct)
    {
        foreach (var existing in subPlansSoFar)
        {
            var sim = CalculateChangeSimilarity(NormalizeChangeForDedup(subPlan.Description), NormalizeChangeForDedup(existing.Description));
            if (sim >= 0.82)
                return (false, $"Duplicates already-committed stage '{existing.Title}'.");
        }

        var sb = new StringBuilder();
        sb.AppendLine("### ORIGINAL TASK ###"); sb.AppendLine(originalPrompt);
        sb.AppendLine();
        sb.AppendLine("### STAGES SO FAR ###");
        if (subPlansSoFar.Count == 0) sb.AppendLine("(none)");
        else foreach (var s in subPlansSoFar) sb.AppendLine($"  - {s.Title}: {s.Description}");
        sb.AppendLine();
        sb.AppendLine("### PROPOSED NEXT STAGE ###");
        sb.AppendLine($"{subPlan.Title}: {subPlan.Description}");
        sb.AppendLine();
        sb.AppendLine("Judge ONLY the proposed next stage. Is it atomic (not two concerns split apart, e.g. table " +
                      "creation split from its endpoint)? Does it depend on something not yet staged (e.g. an endpoint " +
                      "before its DTO)? Is it genuinely required by the task (not scope creep)?");
        sb.AppendLine("Output ONLY JSON: {\"valid\": true|false, \"reason\": \"short reason if invalid\"}");

        var (raw, _, _) = await CallLlmRaw(
            "You are a strict meta-plan coherence validator. Output ONLY the requested JSON.",
            sb.ToString(), ct, TimeSpan.FromSeconds(25), maxTokens: 200);

        if (string.IsNullOrWhiteSpace(raw)) return (true, null);
        try
        {
            var cleaned = ExtractFirstJsonObject(raw);
            using var doc = JsonDocument.Parse(cleaned);
            var valid = !doc.RootElement.TryGetProperty("valid", out var v) || v.ValueKind != JsonValueKind.False;
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
            return (valid, valid ? null : reason);
        }
        catch { return (true, null); }
    }

    private async Task<MetaPlanResult?> RunIncrementalMetaPlanLoop(
        string prompt, string discoveryContext, string projectRoot, bool emitSse, CancellationToken ct,
        string? cardId = null)
    {
        var (skipMetaPlan, gateScore) = DeterministicMetaPlanGate(prompt);
        if (skipMetaPlan)
        {
            await EmitLog(emitSse, "info", $"Meta-plan skipped deterministically (score {gateScore} < 6) — atomic task.", ct: ct);
            return null;
        }

        var subPlansSoFar = new List<MetaPlanSubPlan>();
        var rejectionFeedback = new List<string>();
        var attempts = 0;

        for (var turn = 0; turn < MAX_INCREMENTAL_SUBPLANS; turn++)
        {
            ct.ThrowIfCancellationRequested();
            var proposal = await ProposeNextSubPlanAsync(prompt, discoveryContext, subPlansSoFar, rejectionFeedback, emitSse, ct);

            if (proposal == null)
            {
                if (++attempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                continue;
            }

            if (proposal.MetaPlanComplete)
            {
                await EmitLog(emitSse, "success", $"Meta-plan: complete after {subPlansSoFar.Count} stage(s) — {proposal.CompletionReason}", ct: ct);
                break;
            }

            if (proposal.SubPlan == null) { if (++attempts >= MAX_STEP_REGEN_ATTEMPTS) break; continue; }

            var (valid, reason) = await ValidateSubPlanAsync(proposal.SubPlan, prompt, subPlansSoFar, ct);
            if (!valid)
            {
                await EmitLog(emitSse, "warn", $"Meta-plan: rejected stage '{proposal.SubPlan.Title}' — {reason}", ct: ct);
                rejectionFeedback.Add($"REJECTED — '{proposal.SubPlan.Title}' → {reason}");
                if (++attempts >= MAX_STEP_REGEN_ATTEMPTS) { rejectionFeedback.Clear(); attempts = 0; }
                continue;
            }

            subPlansSoFar.Add(proposal.SubPlan);
            rejectionFeedback.Clear();
            attempts = 0;
            await EmitLog(emitSse, "info", $"Meta-plan: committed stage {subPlansSoFar.Count} — {proposal.SubPlan.Title}", ct: ct);
        }

        if (subPlansSoFar.Count <= 1)
            return null;

        var result = new MetaPlanResult
        {
            MetaThinking = "Built incrementally, one validated stage at a time.",
            MetaSummary = $"{subPlansSoFar.Count}-stage plan built incrementally",
            Complexity = Math.Min(10, 6 + subPlansSoFar.Count),
            SubPlans = subPlansSoFar
        };

        if (emitSse)
            await SendSse(Response, "meta-plan", new
            {
                summary = result.MetaSummary,
                complexity = result.Complexity,
                subPlans = result.SubPlans.Select(sp => new { id = sp.Id, title = sp.Title, description = sp.Description, files = sp.Files, contextNote = sp.ContextNote, done = false })
            }, ct);

        if (!string.IsNullOrWhiteSpace(cardId))
            await PersistMetaPlanToCardAsync(cardId, result, emitSse, ct);

        return result;
    }

    private async Task<(AgentPlan? plan, HashSet<int>? completedIndices, bool isBenchmark)> LoadPlanFromBoardDataAsync(string? cardId)
    {
        if (string.IsNullOrWhiteSpace(cardId))
            return (null, null, false);

        try
        {
            var raw = await _boardData.LoadRawAsync();
            if (string.IsNullOrWhiteSpace(raw)) return (null, null, false);

            using var jsonDoc = JsonDocument.Parse(raw);
            var root = JsonNode.Parse(jsonDoc.RootElement.GetRawText())?.AsObject();
            if (root == null) return (null, null, false);

            var columns = new[] { "todo", "doing", "done", "selfImproving" };
            foreach (var column in columns)
            {
                if (!root.TryGetPropertyValue(column, out var columnNode) || columnNode is not JsonArray columnItems)
                    continue;

                foreach (var item in columnItems)
                {
                    if (item is not JsonObject cardObj || cardObj["id"]?.GetValue<string>() != cardId)
                        continue;

                    var isBenchmark = cardObj["_benchmark"]?.GetValue<bool>() ?? false;

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
                            NewString = si["newString"]?.GetValue<string>() ?? "",
                            LineNumber = si["line"]?.GetValue<int>() ?? 0
                        };

                        var idx = si["index"]?.GetValue<int>() ?? i;
                        steps.Add(step);
                        var done = si["done"]?.GetValue<bool>() ?? false;
                        if (done) completed.Add(idx);
                    }

                    if (steps.Count == 0) return (null, null, isBenchmark);

                    var plan = new AgentPlan
                    {
                        Summary = planObj["summary"]?.GetValue<string>() ?? "",
                        Plan = steps
                    };

                    return (plan, completed.Count > 0 ? completed : null, isBenchmark);
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to load plan from board data", new { cardId, error = ex.Message });
        }

        return (null, null, false);
    }
    private sealed class IncrementalStepProposal
    {
        public bool PlanComplete { get; set; }
        public string? CompletionReason { get; set; }
        public string? Thinking { get; set; }
        public string? ExploreFile { get; set; }
        public PlanStep? Step { get; set; }
    }

    private sealed class IncrementalSubPlanProposal
    {
        public bool MetaPlanComplete { get; set; }
        public string? CompletionReason { get; set; }
        public string? Thinking { get; set; }
        public MetaPlanSubPlan? SubPlan { get; set; }
    }

    private static string BuildSubPlanPrompt(
  string originalPrompt,
  MetaPlanSubPlan subPlan,
  int index, int total,
  string? accumulatedResults = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### ORIGINAL TASK (FOR CONTEXT ONLY - DO NOT PLAN THIS) ###");
        sb.AppendLine(originalPrompt);
        sb.AppendLine();
        sb.AppendLine($"### SUB-PLAN {index}/{total}: {subPlan.Title} ###");
        sb.AppendLine("⚠ CRITICAL: You MUST plan ONLY the changes described in this specific sub-plan. Do NOT plan the entire original task. Do NOT plan steps for other sub-plans.");
        sb.AppendLine(subPlan.Description);

        if (!string.IsNullOrWhiteSpace(subPlan.ContextNote))
        {
            sb.AppendLine();
            sb.AppendLine("### CONTEXT FROM PRIOR SUB-PLANS (planned) ###");
            sb.AppendLine(subPlan.ContextNote);
        }

        if (!string.IsNullOrWhiteSpace(accumulatedResults))
        {
            sb.AppendLine();
            sb.AppendLine("### ACTUAL RESULTS FROM PRIOR SUB-PLANS (executed) ###");
            sb.AppendLine("The following changes were ACTUALLY applied to files in prior sub-plans.");
            sb.AppendLine("You MUST use these exact symbol names and file paths in your plan:");
            sb.AppendLine(accumulatedResults);
        }

        sb.AppendLine();
        sb.AppendLine("### CONSTRAINTS ###");
        sb.AppendLine("1. Plan ONLY the changes described in this sub-plan — do NOT add steps for other sub-plans' work");
        sb.AppendLine("2. Use the EXACT symbol names, property names, and file paths from the context above");
        sb.AppendLine("3. If context says a prior sub-plan added 'OS, CPU, RAM, GPU properties to BenchmarkDataDTO',");
        sb.AppendLine("   your INSERT statement MUST reference those exact property names (benchmark.OS, benchmark.CPU, etc.)");
        sb.AppendLine("4. CREATE TABLE IF NOT EXISTS goes INSIDE the method body, before the INSERT — NOT as a separate step");
        sb.AppendLine("5. Do NOT re-describe or re-implement work from prior sub-plans");
        sb.AppendLine("6. Each step must be atomic: one coherent edit at one location in one file");

        return sb.ToString();
    }


    private static string BuildPlanningPrompt() =>
        "You are a senior autonomous coding agent. Plan the complete minimum set of steps needed to satisfy the user's request.\n" +
        "Think in this loop before writing JSON: understand the exact task, identify the owning files, decide what context is missing, then plan only the actionable delta.\n" +
        "Output ONLY valid JSON — no markdown fences, no extra text.\n\n" +
               "### STEP TYPES (the \"file\" field) ###\n" +
        "  \"relative/path.ext\"  — Edit an existing file (must be in discovery context). Do NOT include oldString/newString — they will be resolved at execution time. " +
            "For every edit step, include a \"line\" field with the 1-based line number of the target location. " +
            "Example: {\"file\": \"src/app.ts\", \"change\": \"description\", \"priority\": 1, \"line\": 42}\n" +
        "  \"_explore\"            — Read a file NOT YET in the discovery context for REFERENCE only (no edits). Put the file path in \"change\". Do NOT use _explore for files whose content is already shown in the DISCOVERY CONTEXT section — they have already been read.\n" +
        "  \"_command\"            — Run a terminal command; put the full command in \"change\". SAFETY: only use _command if the task requires terminal operations. NEVER use mkdir/rmdir/del for project files — use _create_file instead.\n" +
        "  \"_create_file\"        — Create a new file: put full file content in \"newString\", leave \"oldString\" empty. If the directory does not exist, the system will create it automatically. Do NOT use mkdir.\n" +
        "  \"_web_search\"         — Search the web; put the query in \"change\"\n" +
        "  \"_web_fetch\"          — Fetch a URL; put the full URL in \"change\"\n" +
        "  \"_git\"                — Git operation (commit/pull/push/branch/revert)\n" +
        "  \"_rename_file\"        — Rename: put \"oldpath → newpath\" in \"change\"\n" +
        "  \"_delete_file\"        — Delete a file path in \"change\"\n" +
        "  \"_show\"               — Display text to the user (use last)\n" +
        "  \"_done\"               — Task is already complete; put reason in \"change\"\n" +
        "  \"_checkpoint\"         — Split large refactor into phases\n\n" +
        "### RULES ###\n" +
        "0. OUTPUT DISCIPLINE (CRITICAL): Do NOT write step-by-step reasoning, analysis, or exploratory prose before the JSON. " +
            "Any internal reasoning belongs ONLY inside the \"thinking\" field (max 1-2 sentences). " +
            "Your response MUST begin with '{' as the very first character — no preamble, no \"Looking at...\", no walkthrough.\n" +
        "1. Only reference files that exist in the discovery context. Files whose content is shown in the DISCOVERY CONTEXT have already been read — do NOT add _explore steps for them.\n" +
        "   If the right file is not in discovery context but its path is listed or strongly implied, use _explore with the exact project-relative path.\n" +
        "   If you are unsure which file owns a symbol, choose the most likely path from filenames/imports and use _explore before planning an edit.\n" +
        "2. Plan the COMPLETE set of steps needed to finish this task in ONE shot — usually 1-4 steps for simple tasks, but up to 10-12 steps for complex features. " +
        "Do NOT artificially limit yourself to 1-2 steps so you can be re-invoked later — under-planning " +
        "causes repeated re-invocations that tend to invent redundant or conflicting follow-up edits. " +
        "If the task is a single coherent code change (e.g. two related assignments in the same block, " +
        "or one method body), output exactly ONE step for it.\n" +
        "3. Tool choice: use _explore for repository source, _web_search/_web_fetch only for external current information, and _command only for terminal work that cannot be represented as an edit step.\n" +
        "4. WEB FIRST: add a _web_search step if you need current API docs or recent data.\n" +
        "5. COMMANDS BEFORE EDITS: if a generated/downloaded file must exist first, add _command BEFORE the edit step and write outputs inside the project.\n" +
        "6. SELF-STOP: emit a single _done step if the code already satisfies the requirement.\n" +
        "   The DISCOVERY CONTEXT section above shows the ACTUAL content of files that were read.\n" +
        "   Check that content BEFORE planning an edit step. If the property, method, or config\n" +
        "   already exists in the file shown in discovery context, do NOT create an edit step.\n" +
        "   Use _done instead.\n" +
        "7. Score precisely:\n" +
        "   90-100: Exact file + precise change description, no uncertainty\n" +
        "   70-89:  Correct file, good description, minor refinement possible\n" +
        "   40-69:  File identified but change is vague or approach is uncertain\n" +
        "   0-39:  Unsure which file or what to change.\n" +
        "   Be decisive. If you have the right file and a clear change, score 85+. Do NOT stay low when the plan is solid.\n" +
        "8. Step sizing: one step may cover one coherent edit in one file or one tightly coupled block. Split when changes touch different files, have different owners, need independent verification, OR target DIFFERENT LOCATIONS within the same file.\n" +
        "   CRITICAL — SAME-FILE MULTI-LOCATION: Even within a single file, if a step requires editing 2+ separate locations (e.g., add a field at the top of a class, initialize it in the constructor, AND add a new method at the bottom), that is MULTIPLE steps. Each location requires a different edit strategy and anchor, so combining them causes the editor to attempt full-class rewrites instead of targeted edits.\n" +
        "   BAD (over-combined, SAME FILE): \"Add a _timer field and initialize it in the constructor, then add a RunTimerTasks method\"\n" +
        "   GOOD (3 steps, SAME FILE): Step 1: \"Add _timer field declaration after the last existing timer field\", Step 2: \"Initialize _timer in the constructor after existing timer initializations\", Step 3: \"Add RunTimerTasks method after the last existing RunXxxTasks method\"\n" +
        "   BAD (over-combined, SAME FILE): \"Add a isMenuPanelOpen property and add showMenuPanel/closeMenuPanel methods\"\n" +
        "   GOOD (3 steps, SAME FILE): Step 1: \"Add isMenuPanelOpen property declaration after the last existing property\", Step 2: \"Add showMenuPanel() method after the last existing method\", Step 3: \"Add closeMenuPanel() method after showMenuPanel()\"\n" +
        "   BAD (too vague): \"Fix the dashboard\"\n" +
        "   GOOD: \"In Dashboard.renderCards(): include archived cards in the existing filteredCards calculation when showArchived is true\"\n" +
        "   RULE OF THUMB: If the change description contains 'and ... then ...' or mentions 2+ of {field, property, constructor, method, handler} in a single step, SPLIT IT.\n" +
        "9. Each step's change field must be extremely precise: name the method/component/selector, describe the old behavior, and describe the new behavior.\n" +
        "10. UI layout rule: if the request is about visual position/spacing/screen location (top right, under, overlay, mobile-only, etc.), plan a stylesheet/CSS step. Do NOT satisfy visual placement by reordering existing HTML nodes. Use HTML only to create a missing control or fix missing wiring, and use the component script when changing event handlers.\n" +
        "11. If the user stated any constraints (e.g. 'do not use x'), include them verbatim in the 'change' field.\n" +
        "12. If the file path contains \"\\\\\" escape it for JSON: use \"path/to/file.ext\"\n" +
        "13. For each edit step (relative path in \"file\"), also set \"referenceFiles\" to a list of file paths the edit pipeline should load as context. Include files that define types, methods, or patterns the edit needs to reference. This keeps the edit context small and focused.\n" +
        "14. When editing a component/UI file or making changes involving imports/aliases, first read the target file's imports. Include the import source files in \"referenceFiles\" so the edit pipeline can verify aliases are correct before making changes.\n" +
        "15. NEVER use _web_search to find, read, or understand code that exists inside this project's repository. " +
        "For reading project source files use _explore with the relative file path. " +
        "_web_search is ONLY for external resources (public docs, npm packages, Stack Overflow, API references). " +
        "If you don't know which file contains the code, add an _explore step first.\n" +
        "16. Context and memory discipline: do not ask to read everything. Prefer the smallest file set that proves names, signatures, imports, and local patterns. Use referenceFiles for narrow supporting context rather than extra edit steps.\n" +
        "17. Describe plan steps as the MINIMAL delta needed. The DISCOVERY CONTEXT section shows actual file content. " +
        "DO NOT re-describe existing functionality as something that needs to be built. " +
        "BAD: \"Modify GetUsersWithCalendarNotificationsEnabled to collect all events per user and send Firebase notifications\" " +
        "(the method already collects events — \"collect\" is wrong). " +
        "GOOD: \"After the existing usersWithEvents loop, send Firebase notification for each user with the events list\" " +
        "(describes only the missing logic). " +
        "Read the file body in DISCOVERY CONTEXT to understand what already exists, then describe ONLY what is missing.\n" +
         "18. CREATE TABLE MUST BE INLINE (CRITICAL): If the task involves creating a new database table and inserting/updating data into it, " +
         "the CREATE TABLE IF NOT EXISTS statement MUST be placed INSIDE the method body, BEFORE the INSERT/UPDATE statement. " +
         "Do NOT create a separate 'table creation' method or step — the table creation is an inline guard clause, not a separate concern. " +
         "BAD: Step 1: 'Add CreateBenchmarksTable method', Step 2: 'Add PostBenchmarks endpoint with INSERT' — WRONG, these should be ONE step. " +
         "GOOD: 'Add PostBenchmarks endpoint with inline CREATE TABLE IF NOT EXISTS and INSERT statement inside the method body'.\n\n" +
        "### OUTPUT FORMAT ###\n" +
        "{\n" +
        "  \"thinking\": \"1-2 lines: which file needs changing and why\",\n" +
        "  \"summary\": \"one sentence: what this step accomplishes\",\n" +
        "  \"score\": <0-100>,\n" +
        "  \"plan\": [\n" +
        "    {\n" +
        "      \"file\": \"wwwroot/app.js\",\n" +
        "      \"change\": \"Modify confirmFilePicker to append files to existing list\",\n" +
        "      \"line\": 42,\n" +
        "      \"referenceFiles\": [\"wwwroot/utils.js\", \"wwwroot/types.js\"]\n" +
        "    }\n" +
        "  ]\n" +
        "}" +
        "22. LINE NUMBERS (CRITICAL): Every line of file content above is PREFIXED with its line number " +
        "(e.g. \"104: <div class=\\\"card-tags\\\">\"). Use those EXACT numbers in the \"line\" field — do NOT guess or " +
        "estimate. The line prefix IS the correct line number in the file. If the change targets the section " +
        "at line 96, set \"line\": 96. These numbers are used during execution to find the right code location " +
        "in files with repeated sections.\n" +
        "18. DATA FLOW TRACING: Before planning an edit that modifies how data is displayed or accessed, " +
        "trace WHERE the data comes from. Read type definitions to understand the full data structure. " +
        "Example: if the task is about 'image preview navigation', don't assume images come from filtering " +
        "a file list — check the actual type definitions to see if there's a metadata field with " +
        "screenshot/artwork/cover URLs. Plan your edit based on the ACTUAL data structure, not assumptions.\n" +
        "19. When the DISCOVERY CONTEXT shows a type reference like `romMetadata?: RomMetadata`, and the " +
        "task involves data that might live in that nested type, add a _explore step to read the RomMetadata " +
        "type definition BEFORE planning the edit. You cannot plan correctly without understanding the " +
        "full data structure.\n" +
        "20. CROSS-FILE ENDPOINT WIRING: When the task involves creating a new backend endpoint (e.g., in a .cs controller), " +
        "and the frontend needs to call it, you MUST add a step to create the corresponding method in the frontend service file " +
        "(e.g., grandtheft.service.ts) BEFORE adding the UI code that calls it. " +
        "Do NOT reuse methods from unrelated services (e.g., enderService) just because they have similar names. " +
        "If the service method does not exist, plan a step to create it.\n" +
       "21. SCAFFOLDING (CRITICAL & MANDATORY): When the task asks to CREATE a new component... " +
        "your plan MUST contain the following steps in order:\n" +
        "   1. A `_command` step to run the framework's CLI generator. Use `;` to separate commands (e.g., `cd maxhanna.client; npx ng g c components/recipe --skip-tests`). NEVER use `&&` as it fails in PowerShell.\n" +
        "   2. An edit step for `app.module.ts` to register the new component in the declarations array. This is MANDATORY.\n" +
        "   3. Edit steps to modify the newly generated `.ts`, `.html`, and `.css` files.\n" +
        "   Do NOT manually create the files with `_create_file`. Do NOT bypass scaffolding by using `edit` with `fullFile` on a non-existent file. The system will inject the scaffolding command automatically if you forget.\n" +
        "22. COMPONENT TEMPLATE WIRING (CRITICAL): When the task involves adding UI elements (buttons, inputs) that trigger new " +
        "actions (e.g., (click)=\"doSomething()\"), you MUST plan the step to implement the method in the TypeScript " +
        "component file (e.g., .ts) BEFORE the step to edit the HTML template (e.g., .html) to reference it. " +
        "Do NOT plan HTML edits that depend on .ts methods if the .ts step comes later. The .ts step MUST come first.\n" +
        "actions (e.g., (click)=\"doSomething()\"), you MUST add a step to implement the method in the TypeScript " +
        "component file (e.g., .ts) BEFORE editing the HTML template (e.g., .html) to reference it. " +
        "Do NOT reference methods in the HTML template that do not exist in the component class yet. " +
        "If the component class does not have the method, plan a step to add it first.\n" +
        "23. SERVICE DEPENDENCIES (CRITICAL): When planning to call a method on a service (e.g., `this.userEventService.insertUserEvent(...)`) that is NOT already imported and injected into the constructor of the target file, you MUST add a separate step FIRST to import the service and add it to the constructor parameters. Do NOT assume the service is already available in the component. If the service method requires a specific model/interface (e.g., `UserEvent`), you MUST read that model's definition to know the exact properties required before writing the call. When describing the call in the step description, you MUST use the exact syntax `this.serviceName.methodName()` so the system can automatically detect the dependency.\n" +
        "24. MODEL CONSTRUCTION: When passing an object to a service method, you MUST match the exact properties of the target interface. Do NOT invent properties. If the interface requires `{ userId, eventType, eventText }`, do not pass `('wordler', guess)`. Read the interface definition first.\n" +
        "25. FEATURE IMPLEMENTATION STACK (CRITICAL): When the user asks to build a new feature (e.g., 'Create a paint component where users can paint, save, and view paintings'), you MUST plan the steps in the following strict architectural order. Do NOT combine these phases into fewer steps.\n" +
        "   a. Backend Data Model (if needed): Create the C# data contract (e.g., `Painting.cs`) with the required properties.\n" +
        "   b. Backend Controller/Service (if needed): Create the endpoint (e.g., `PaintController.cs`) to save and retrieve the data. \n" +
        "   c. Frontend Data Contract: Create the TypeScript interface (e.g., `painting.ts`) matching the backend model.\n" +
        "   d. Frontend Service: Create the Angular service (e.g., `paint.service.ts`) with methods to call the backend API (e.g., `savePainting`, `getPaintings`).\n" +
        "   e. Frontend Component Scaffolding: Run the `_command` step to generate the component.\n" +
        "   f. Component Logic (.ts): Inject the new service in the constructor and implement the methods to save/load paintings.\n" +
        "   g. Component Template (.html): Build the UI (canvas, buttons, list).\n" +
        "   h. Routing/Module: Register the new component in `app.module.ts` and routing if necessary.\n" +
        "   Do NOT skip steps. Do NOT combine the service creation and the component logic into one step. Each letter (a through h) should be its own step in the plan if required by the feature.\n" +
        "26. NUMBERED LIST ADHERENCE: If the user's request contains a numbered list of tasks (e.g., '1. Create folder, 2. Create file, 3. Add heading'), your plan MUST contain AT LEAST that many steps. Do NOT combine multiple numbered items into a single step. Follow the user's structure exactly.\n" +
        "27. FILE CREATION MARKER: When creating a new file that does not exist yet, you MUST use \"_create_file\" as the step type. Put the full file path in the \"file\" field and the complete file content in the \"newString\" field. NEVER use a relative path as the file marker for a new file — the executor will treat it as an edit to an existing file and fail.\n" +
        "28. PATTERN MIRRORING (CRITICAL): When the user asks to 'mirror', 'copy', or 'wire up exactly like' a pattern from another file, your plan steps MUST explicitly name the exact methods, properties, and HTML attributes to copy. " +
        "For each file type involved:\n" +
        "   • .ts steps: Explicitly list the exact property declarations and method signatures to add (e.g., 'Add X property, Y() method, and Z() method').\n" +
        "   • .html steps: Explicitly describe BOTH the new HTML structure to insert AND any attribute/event bindings to add to existing elements.\n" +
        "Do NOT invent new method names. Use the EXACT names from the source. " +
        "Do NOT invent custom wrapper tags if the source uses standard elements with classes. " +
        "Copy the EXACT DOM structure, CSS classes, and text content from the source. " +
         "Do NOT invent method calls inside the HTML that do not exist in the source pattern.\n" +
        "29. METHOD CREATION IS ONE STEP (CRITICAL): Creating a new method includes its signature, parameters, and body — " +
        "all in ONE step. Do NOT split into \"Create method signature\" and \"Implement method body\". " +
        "A method is ONE coherent block at ONE location. " +
        "If the method body needs inline SQL (CREATE TABLE IF NOT EXISTS + INSERT/UPDATE/SELECT), " +
        "that SQL belongs INSIDE the method body in the same step. " +
        "BAD: Step 1: \"Create Benchmarks table\", Step 2: \"Add PostBenchmarks method with INSERT\"\n" +
        "GOOD: \"Add PostBenchmarks method with CREATE TABLE IF NOT EXISTS and INSERT logic\"\n" +
        "BAD: Step 1: \"Add Benchmarks schema\", Step 2: \"Add INSERT endpoint\"\n" +
        "GOOD: \"Add PostBenchmarks endpoint method with inline CREATE TABLE IF NOT EXISTS and parameterized INSERT\"\n" +
        "RATIONALE: The CREATE TABLE IF NOT EXISTS runs at the START of the method body, " +
        "before any INSERT/UPDATE/SELECT. It is NOT a separate schema definition — " +
        "it is an inline guard clause that ensures the table exists. " +
        "Treating it as a separate step forces the editor to insert the CREATE TABLE into " +
        "a random unrelated location instead of inside the method where it belongs.\n" +
        "30. DO NOT CREATE SEPARATE SETUP ENDPOINTS: When a single new endpoint needs both " +
        "infrastructure setup (CREATE TABLE, connection check) and business logic (INSERT/UPDATE/SELECT), " +
        "put the setup INSIDE the endpoint method as inline code at the top. " +
        "Do NOT create a separate \"PostXxxTable\" or \"InitializeXxx\" endpoint as a separate step. " +
        "BAD: Step 1: \"Add PostBenchmarksTable endpoint (creates table)\", Step 2: \"Add AddBenchmark endpoint (inserts data)\"\n" +
        "GOOD: \"Add AddBenchmark endpoint with CREATE TABLE IF NOT EXISTS at the top and parameterized INSERT below\"\n" +
        "RATIONALE: A separate setup endpoint creates unnecessary public API surface. " +
        "Table creation is an implementation detail that belongs inside the method that needs it.\n";

    private static bool IsVisualLayoutTask(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) { return false; }
        var p = prompt.ToLowerInvariant();

        if (Regex.IsMatch(p, @"\bmove\b.{0,60}\b(inside|into|to|under|below|above|after|before)\b"))
        { return false; }

        return Regex.IsMatch(p, @"\b(position|layout|align(?:ed|ment)?|margin|padding|spacing)\b") ||
               Regex.IsMatch(p, @"\b(overlap|z[- ]?index|float|sticky|fixed|absolute|relative)\b") ||
               Regex.IsMatch(p, @"\b(grid|flex|width|height|overflow)\b") ||
               p.Contains("move ");
    }

    private static bool IsStylesheetPath(string file)
    {
        var ext = Path.GetExtension(file ?? "").ToLowerInvariant();
        return ext is ".css" or ".scss" or ".sass" or ".less" or ".styl";
    }

    private async Task<string?> ValidatePlanAsync(string userPrompt, AgentPlan plan, CancellationToken ct)
    {
        if (plan?.Plan != null)
        {
            string? lastImpliedDir = null;

            for (var i = 0; i < plan.Plan.Count; i++)
            {
                var step = plan.Plan[i];
                if (string.Equals(step.File, "_command", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(step.Change))
                {
                    var cmd = step.Change.ToLowerInvariant();
                    if (cmd.Contains("mkdir") || cmd.Contains("rmdir") || cmd.Contains("rm -rf") ||
                        cmd.Contains("del /") || cmd.Contains("rd /"))
                    {
                        var mkdirMatch = Regex.Match(step.Change, @"(?:mkdir|md)\s+([^\s;|&]+)", RegexOptions.IgnoreCase);
                        if (mkdirMatch.Success)
                        {
                            lastImpliedDir = mkdirMatch.Groups[1].Value.Trim('/', '\\', '"', '\'');
                            step.File = "_create_directory";
                            step.Change = lastImpliedDir;
                            await EmitLog(true, "warn",
                                $"Converted directory manipulation command '{step.Change}' to a _create_directory step.", ct: ct);
                        }
                        else
                        {
                            plan.Plan.RemoveAt(i);
                            i--;
                            await EmitLog(true, "warn", "Removed unparseable directory manipulation command.", ct: ct);
                        }
                    }
                }

                if (string.Equals(step.File, "_create_file", StringComparison.OrdinalIgnoreCase) && lastImpliedDir != null)
                {
                    if (!step.Change.Contains("/") && !step.Change.Contains("\\"))
                    {
                        step.Change = $"Create file in directory {lastImpliedDir}: {step.Change}";
                    }
                }
            }
        }

        if (plan?.Plan != null && IsVisualLayoutTask(userPrompt))
        {
            var allChanges = string.Join(" ", plan.Plan.Select(s => s.Change ?? ""));
            if (Regex.IsMatch(allChanges, @"\b(remove|delete|hide)\b", RegexOptions.IgnoreCase))
                goto SkipLayoutCheck;

            var editFiles = plan.Plan
                .Select(s => (s.File ?? "").Replace('\\', '/'))
                .Where(AgentUtilities.IsRelativePath)
                .Where(f => !AgentUtilities.IsSpecialMarker(f))
                .ToList();

            var hasMarkup = editFiles.Any(f => Path.GetExtension(f).Equals(".html", StringComparison.OrdinalIgnoreCase) ||
                                               Path.GetExtension(f).Equals(".cshtml", StringComparison.OrdinalIgnoreCase) ||
                                               Path.GetExtension(f).Equals(".razor", StringComparison.OrdinalIgnoreCase));
            var hasStylesheet = editFiles.Any(IsStylesheetPath);
            var hasScript = editFiles.Any(f => Path.GetExtension(f) is ".ts" or ".tsx" or ".js" or ".jsx");

            if (hasMarkup && !hasStylesheet)
            {
                return "Visual layout/positioning request is planned only against markup. Replan with a stylesheet/CSS step for positioning instead of moving DOM order. Keep markup edits only for missing elements or missing event wiring.";
            }

            var changes = string.Join(" ", plan.Plan.Select(s => s.Change ?? ""));
            if (Regex.IsMatch(changes, @"\b(click|touchstart|touchend|handler|method|function|wire|wiring|event)\b", RegexOptions.IgnoreCase) &&
                hasMarkup && !hasScript)
            {
                return "Template event wiring is planned without the component script/context. Replan to inspect or edit the .ts/.js component before changing handlers, so method names are verified instead of invented.";
            }
        }
    SkipLayoutCheck:;

        if (plan?.Plan != null && plan.Plan.Count >= 2)
        {
            var splitStopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "method", "endpoint", "after", "before", "using", "into", "with",
                "this", "that", "from", "which", "their", "there", "about",
                "would", "could", "should", "inline", "also", "will", "been",
                "have", "does", "just", "more", "than", "then", "when", "what"
            };
            for (var i = 0; i < plan.Plan.Count - 1; i++)
            {
                var s1 = plan.Plan[i];
                var s2 = plan.Plan[i + 1];
                if (string.IsNullOrWhiteSpace(s1.File) || string.IsNullOrWhiteSpace(s2.File)) continue;
                if (!string.Equals(s1.File, s2.File, StringComparison.OrdinalIgnoreCase)) continue;
                var ext = Path.GetExtension(s1.File.Replace('\\', '/'));
                if (!string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase)) continue;

                var c1 = s1.Change ?? "";
                var c2 = s2.Change ?? "";

                if (!Regex.IsMatch(c1, @"\b(add|create|insert)\b.*\b(method|endpoint|handler)\b", RegexOptions.IgnoreCase)) continue;
                if (!Regex.IsMatch(c2, @"\b(add|create|insert)\b.*\b(method|endpoint|handler)\b", RegexOptions.IgnoreCase)) continue;

                var words1 = Regex.Matches(c1, @"\b[a-zA-Z]{4,}\b")
                    .Select(m => m.Value.ToLowerInvariant())
                    .Where(w => !splitStopWords.Contains(w) && !Regex.IsMatch(w, @"^(add|create|insert|post|get|put|delete)$"))
                    .ToHashSet();
                var words2 = Regex.Matches(c2, @"\b[a-zA-Z]{4,}\b")
                    .Select(m => m.Value.ToLowerInvariant())
                    .Where(w => !splitStopWords.Contains(w) && !Regex.IsMatch(w, @"^(add|create|insert|post|get|put|delete)$"))
                    .ToHashSet();

                var overlap = words1.Intersect(words2, StringComparer.OrdinalIgnoreCase).Count();
                if (overlap >= 3)
                {
                    return $"Steps {i + 1} and {i + 2} both target {s1.File} and share {overlap} overlapping keywords " +
                           $"({string.Join(", ", words1.Intersect(words2, StringComparer.OrdinalIgnoreCase).Take(5))}). " +
                           "They describe the same endpoint/feature and should be one step. " +
                           "If one step is a setup/prerequisite (e.g. CREATE TABLE), inline it inside the other step " +
                           "instead of making it a separate endpoint.";
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
        sb.AppendLine(JsonSerializer.Serialize(plan!.Plan, new JsonSerializerOptions { WriteIndented = true }));

        var (raw, _, err) = await CallLlmRaw(
            "You validate code-change plans. Output ONLY a JSON object with a \"valid\" boolean and optional \"reason\". No extra text, no markdown fences.",
            sb.ToString(), ct, TimeSpan.FromSeconds(30), maxTokens: 256);

        if (!string.IsNullOrWhiteSpace(err) || string.IsNullOrWhiteSpace(raw))
            return null;

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
        catch { }

        return null;
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

        const int MaxRetries = 3;
        string? raw = null;
        string? llmError = null;

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            if (attempt > 1)
                await EmitLog(emitSse, "warn", $"Retrying plan generation (attempt {attempt}/{MaxRetries})...", ct: ct);
            else
                await EmitLog(emitSse, "info", "Generating plan...", ct: ct);

            Console.WriteLine($"### CALLING LLM WITH PROMPT >>> {planningPrompt} >>> {userPrompt}");

            (raw, _, llmError) = await CallLlmRawStreaming(
                planningPrompt, userPrompt.ToString(), emitSse, ct,
                requestTimeout: _infiniteTimeout, maxTokens: 2048);

            if (string.IsNullOrWhiteSpace(raw))
            {
                await EmitLog(emitSse, "error",
                    $"LLM returned empty plan response: {llmError ?? "no content"}", ct: ct);
                continue;
            }

            AgentPlan? plan = AgentUtilities.ParsePlan(raw);
            if (plan == null && (raw.Contains("<<<STEP", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains("### STEP", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains("STEP", StringComparison.OrdinalIgnoreCase)))
            {
                plan = AgentUtilities.ParseDelimitedPlan(raw);
            }

            if (plan == null)
            {
                plan = await RecoverPlanFromRamblingAsync(emitSse, ct, raw);
            }

            if (plan == null)
            {
                bool containsLLMError = false;
                bool containsLLMLoading = false;
                if (!string.IsNullOrEmpty(raw))
                {
                    if (raw.ToLower().Contains("error"))
                    {
                        containsLLMError = true;
                    }
                    if (raw.ToLower().Contains("loading model"))
                    {
                        containsLLMLoading = true;
                    }
                }
                string errorMessage = containsLLMLoading ? " Model Loading. Please retry after a short period of time."
                                        : containsLLMError ? " LLM Returned Error state. Check LLM."
                                        : "";
                await EmitLog(emitSse, "error", "Failed to parse plan." + errorMessage, raw, ct: ct);
                continue;
            }

            var webViolation = DetectMissingWebSearch(prompt, plan);
            if (webViolation != null)
                await EmitLog(emitSse, "warn", $"Plan may need web search: {webViolation}", ct: ct);


            if (plan.Plan != null && plan.Plan.Count > 1)
            {
                var uniqueSteps = new List<PlanStep>();
                for (var i = 0; i < plan.Plan.Count; i++)
                {
                    var step = plan.Plan[i];
                    var normChange = NormalizeChangeForDedup(step.Change);
                    var isDuplicate = false;

                    foreach (var existing in uniqueSteps)
                    {
                        if (!string.Equals(step.File, existing.File, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var existingNorm = NormalizeChangeForDedup(existing.Change);
                        var similarity = CalculateChangeSimilarity(normChange, existingNorm);

                        if (similarity >= 0.8)
                        {
                            isDuplicate = true;
                            await EmitLog(emitSse, "warn", $"Removed duplicate plan step (similarity {similarity:P0}): [{step.File}] {step.Change}", ct: ct);
                            break;
                        }
                    }

                    if (!isDuplicate)
                    {
                        uniqueSteps.Add(step);
                    }
                }
                plan.Plan = uniqueSteps;
            }

            await EmitLog(emitSse, "info",
                $"Plan: {plan?.Plan?.Count ?? 0} step(s) — score {plan?.Score ?? 0}/100", new { plan }, ct: ct);

            return plan;
        }

        await EmitLog(emitSse, "error",
            $"LLM failed to produce a valid plan after {MaxRetries} attempts.", ct: ct);
        return null;
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
            parsed = AgentUtilities.ParsePlan(cleaned);

        if (parsed == null)
        {
            await EmitLog(emitSse, "error", "Failed to parse plan.", cleaned, ct: ct);
            return (null, "Response was unparseable.");
        }

        var violations = GetPlanSizeViolations(parsed);
        if (violations.Count > 0)
        {
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

    private async Task ValidatePlanLineNumbersAsync(AgentPlan plan, string projectRoot, CancellationToken ct = default)
    {
        if (plan?.Plan == null) return;


        var fileGroups = plan.Plan
            .Select((step, idx) => (step, idx))
            .Where(x => !string.IsNullOrWhiteSpace(x.step.File) &&
                        AgentUtilities.IsRelativePath(x.step.File))
            .GroupBy(x => x.step.File, StringComparer.OrdinalIgnoreCase);

        foreach (var group in fileGroups)
        {
            var filePath = group.Key.Replace('\\', '/');
            var fullPath = Path.GetFullPath(
                Path.Combine(projectRoot, filePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!System.IO.File.Exists(fullPath)) continue;

            var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8);
            var lines = content.Split('\n');

            var sb = new StringBuilder();
            sb.AppendLine("Find the exact 1-based line number where each edit should be applied in this file.");
            sb.AppendLine();
            sb.AppendLine("File content (line numbers shown):");
            sb.AppendLine("```");

            var displayLines = lines.Take(500).ToList();
            for (var i = 0; i < displayLines.Count; i++)
                sb.AppendLine($"{i + 1}: {displayLines[i]}");
            if (lines.Length > 500)
                sb.AppendLine($"... ({lines.Length - 500} more lines)");
            sb.AppendLine("```");
            sb.AppendLine();

            var stepList = group.ToList();
            for (var si = 0; si < stepList.Count; si++)
            {
                var (step, _) = stepList[si];
                sb.AppendLine($"Edit {si}: \"{step.Change}\"");
                sb.AppendLine($"  Current line: {step.LineNumber}");
                sb.AppendLine();
            }

            sb.AppendLine("For each Edit, output the correct 1-based line number where the change should be applied.");
            sb.AppendLine("Consider: property declarations go with properties, methods go with methods, etc.");
            sb.AppendLine("If the current line is already correct, keep it unchanged.");
            sb.AppendLine();
            sb.AppendLine("Respond with ONLY a valid JSON object (no markdown, no extra text):");
            sb.AppendLine("{\"lines\":[{\"index\":0,\"line\":42},{\"index\":1,\"line\":78}]}");

            var prompt = sb.ToString();
            const int MaxRetries = 2;
            for (var attempt = 1; attempt <= MaxRetries; attempt++)
            {
                var llmResult = await CallLlmRaw(
                    "You are a code analyst. Output ONLY the JSON object described below.",
                    prompt, ct, requestTimeout: TimeSpan.FromMinutes(2), maxTokens: 1024);
                var raw = llmResult.raw;
                var error = llmResult.error;

                if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(raw))
                {
                    if (attempt < MaxRetries)
                        await Task.Delay(500, ct);
                    continue;
                }

                var cleaned = raw.Trim();
                if (cleaned.StartsWith("```"))
                {
                    var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                    if (m.Success) cleaned = m.Groups[1].Value.Trim();
                }
                var fb = cleaned.IndexOf('{');
                var lb = cleaned.LastIndexOf('}');
                if (fb >= 0 && lb > fb) cleaned = cleaned[fb..(lb + 1)];

                try
                {
                    using var jDoc = JsonDocument.Parse(cleaned, new JsonDocumentOptions { AllowTrailingCommas = true });
                    var root = jDoc.RootElement;
                    if (!root.TryGetProperty("lines", out var linesEl) || linesEl.ValueKind != JsonValueKind.Array)
                    {
                        if (attempt < MaxRetries) continue;
                        break;
                    }

                    foreach (var lineEl in linesEl.EnumerateArray())
                    {
                        if (!lineEl.TryGetProperty("index", out var idxEl) || idxEl.ValueKind != JsonValueKind.Number)
                            continue;
                        if (!lineEl.TryGetProperty("line", out var lnEl) || lnEl.ValueKind != JsonValueKind.Number)
                            continue;

                        var idx = idxEl.GetInt32();
                        var ln = lnEl.GetInt32();
                        if (idx >= 0 && idx < stepList.Count && ln >= 1 && ln <= lines.Length)
                        {
                            var (step, _) = stepList[idx];
                            step.LineNumber = ln;
                        }
                    }
                    break;
                }
                catch (JsonException)
                {
                    if (attempt < MaxRetries) continue;
                }
            }
        }
    }

    private static string BuildPlannerDiscoveryContext(string fullDiscovery)
    {
        if (string.IsNullOrWhiteSpace(fullDiscovery)) return fullDiscovery;
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
            if (fileCount >= MAX_DISCOVERY_FILES)
            {
                result.AppendLine("...(additional files omitted from planner context)");
                break;
            }
            var lines = section.Split('\n');
            result.AppendLine(lines[0]);
            var body = lines.Skip(1).ToArray();
            var numbered = body.Select((line, idx) => $"{idx + 1}: {line}").ToArray();
            if (numbered.Length <= MAX_LINES_PER_DISCOVERY_FILE)
            {
                result.AppendLine(string.Join('\n', numbered));
            }
            else
            {
                var head = numbered.Take(MAX_LINES_PER_DISCOVERY_FILE / 2).ToArray();
                var tail = numbered.Skip(numbered.Length - MAX_LINES_PER_DISCOVERY_FILE / 2).ToArray();
                result.AppendLine(string.Join('\n', head));
                result.AppendLine($"...({numbered.Length - MAX_LINES_PER_DISCOVERY_FILE} lines omitted — head and tail shown)...");
                result.AppendLine(string.Join('\n', tail));
                result.AppendLine($"...(truncated — full content used during execution)");
            }
            result.AppendLine();
            fileCount++;
        }
        return result.ToString();
    }

    private async Task<(string discoveryText, List<object> steps)> RunLightBootstrap(
        List<string> attachedFiles, string projectRoot, bool emitSse, CancellationToken ct = default)
    {
        await EmitLog(emitSse, "info", "Fast-path bootstrap: reading attached files only");

        var files = (attachedFiles ?? new List<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .ToList();

        if (files.Count == 0) return ("", new List<object>());

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

        var heuristicCandidates = AgentUtilities.ApplyTaskTypeHeuristics(prompt, allFiles);
        var candidatePool = hintedFiles
            .Concat(heuristicCandidates)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(60).ToList();

        List<string> toRead;
        if (candidatePool.Count <= 6)
        {
            toRead = candidatePool;
            await EmitLog(emitSse, "info", $"Phase 1 — {candidatePool.Count} candidate(s), reading all directly", ct: ct);
        }
        else
        {
            var candidatesText = string.Join(", ", candidatePool);
            if (candidatesText.Length > 75) { candidatesText = candidatesText[..75] + "..."; }

            await EmitLog(emitSse, "info", $"Phase 1 — selecting from {candidatePool.Count} candidates…", new { Candidates = candidatesText }, ct: ct);
            var selected = await SelectRelevantFilesWithLlm(prompt, candidatePool, emitSse, ct);
            toRead = hintedFiles.Concat(selected).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        }

        toRead = toRead.Where(f =>
        {
            var full = Path.GetFullPath(Path.Combine(projectRoot, f.Replace('/', Path.DirectorySeparatorChar)));
            return System.IO.File.Exists(full) && AgentUtilities.IsPathUnderRoot(full, projectRoot);
        }).ToList();

        await EmitLog(emitSse, "info", $"Phase 1 — reading {toRead.Count} file(s): {string.Join(", ", toRead)}", ct: ct);

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

    private async Task<List<string>> SelectRelevantFilesWithLlm(
        string prompt, List<string> candidates, bool emitSse, CancellationToken ct)
    {
        if (candidates.Count == 0) return new List<string>();
        var promptTokens = AgentUtilities.ExtractMeaningfulKeywords(prompt.ToLowerInvariant());
        var deterministic = candidates
            .Select(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                var score = 0;
                foreach (var token in promptTokens)
                {
                    if (name.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 6;
                    if (f.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 2;
                }
                return (file: f, score);
            })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.file.Length)
            .Take(3)
            .Select(x => x.file)
            .ToList();

        const string system =
            "You are a file relevance selector for a coding agent. Given a task and candidate files, pick the 3-7 files most likely to own the requested change or define types/imports needed for it. " +
            "Prefer exact filename/path/symbol matches, neighboring component/template/style files, and files named in the task. Avoid generated, minified, dependency, build, or broad entry-point files unless the task clearly targets them. " +
            "Output ONLY valid JSON, no markdown: {\"files\": [\"path1\", \"path2\"]}";
        var user = $"Task: {prompt}\n\nCandidate files:\n{string.Join("\n", candidates)}\n\nDeterministic high-signal matches to include unless clearly irrelevant:\n{string.Join("\n", deterministic)}\n\nSelect 3-7 max.";
        var (raw, _, err) = await CallLlmRaw(system, user, ct, TimeSpan.FromSeconds(25));
        if (string.IsNullOrWhiteSpace(raw))
            return deterministic.Concat(candidates).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
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
                    .ToList();
                selected = deterministic.Concat(selected)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(7)
                    .ToList();
                if (selected.Count > 0) return selected;
            }
        }
        catch { }
        return deterministic.Concat(candidates).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
    }

    private async Task<(List<object> allSteps, AgentPlan? plan, bool complete)> Orchestrate(
        string prompt, string projectRoot, bool emitSse, CancellationToken ct = default,
        List<string>? attachedFiles = null, bool skipContextReview = false,
        string? steeringContext = null, bool skipQualityCheck = false,
        AgentPlan? existingPlan = null, HashSet<int>? completedStepIndices = null,
        string? cardId = null, bool createTests = false, string? buildCommands = null)
    {
        _gracefulStop = false;

        if (!await CheckLlmConnectivity(projectRoot, emitSse, ct))
            throw new InvalidOperationException("LLM connectivity check failed.");

        var fastPlan = AgentUtilities.TryDetectSimpleIntent(prompt);
        if (fastPlan != null)
        {
            var steps = await QuickPipeline(prompt, projectRoot, emitSse, fastPlan, ct, cardId: cardId);
            return (steps, fastPlan, true);
        }


        var lower = prompt.ToLowerInvariant();
        var mightBeBuildRepair = lower.Contains("build") || lower.Contains("compile") ||
                                 lower.Contains("error") || lower.Contains("warning");

        if (mightBeBuildRepair)
        {
            if (buildCommands != null && buildCommands.Trim().Length > 0)
            {
                var isBuildRepair = await ClassifyIsBuildRepairPromptAsync(prompt, ct);
                if (isBuildRepair)
                {
                    return await RepairBuildPipeline(prompt, projectRoot, emitSse, buildCommands, ct);
                }
            }
            else
            {
                await EmitLog(emitSse, "warn", "Possible build repair prompt detected but no build commands provided — skipping repair.", new { prompt, buildCommands }, ct: ct);
            }
        }

        if (existingPlan != null && existingPlan.Plan.Count > 0)
        {
            var resumeSteps = new List<object>();
            await ExecutePlan(prompt, projectRoot, emitSse, "", existingPlan, ct, resumeSteps,
                steeringContext: steeringContext, attachedFiles: attachedFiles,
                completedStepIndices: completedStepIndices, cardId: cardId);

            var resumeHasErrors = resumeSteps.OfType<Dictionary<string, object?>>()
                .Any(s => s.TryGetValue("status", out var st) && st?.ToString() == "error");

            var allStepsAlreadyDone = completedStepIndices != null && completedStepIndices.Count >= existingPlan.Plan.Count;
            bool resumeComplete = !resumeHasErrors && ((resumeSteps.Count > 0) || allStepsAlreadyDone);

            if (resumeHasErrors)
            {
                await EmitLog(emitSse, "error", "Resumed plan has step errors — task NOT complete", ct: ct);
            }

            return (resumeSteps, existingPlan, resumeComplete);
        }

        var (pipelineType, cmdScore, editScore) = AgentUtilities.ClassifyTask(prompt);
        await EmitLog(emitSse, "info",
            $"Router → {pipelineType}",
            new { CommandScore = cmdScore, EditScore = editScore, BuildCommands = buildCommands }, ct: ct);

        bool hasCodeInPrompt = prompt.Contains("```") || prompt.Contains("<div") || prompt.Contains("function ") || prompt.Contains("public class") || prompt.Contains("export class") || prompt.Contains("import ");

        bool mentionsCodeFiles = Regex.IsMatch(prompt, @"\.(cs|ts|tsx|js|jsx|html|css|scss|java|go|py|rb|php|md|json|yaml|yml)\b", RegexOptions.IgnoreCase) ||
                                 prompt.Contains("component", StringComparison.OrdinalIgnoreCase) ||
                                 prompt.Contains("service", StringComparison.OrdinalIgnoreCase) ||
                                 prompt.Contains("controller", StringComparison.OrdinalIgnoreCase) ||
                                 prompt.Contains("directive", StringComparison.OrdinalIgnoreCase) ||
                                 prompt.Contains("module", StringComparison.OrdinalIgnoreCase);


        bool mentionsCodeLogic = prompt.Contains("upload list", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("list changes", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("pre-mark", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("button", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("click", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("event", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("function", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("method", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("variable", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("array", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("callback", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("search box", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("input field", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("map", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("globe", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("rotate", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("select", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("dropdown", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("modal", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("popup", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("ui", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("frontend", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("style", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("layout", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("render", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("display", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("faq", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("expand on", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("readme", StringComparison.OrdinalIgnoreCase) ||
                                prompt.Contains("section", StringComparison.OrdinalIgnoreCase);


        bool hasAttachedFiles = attachedFiles != null && attachedFiles.Count > 0;

        if ((hasCodeInPrompt || mentionsCodeFiles || mentionsCodeLogic || hasAttachedFiles) && pipelineType != PipelineType.CodeEdit)
        {
            await EmitLog(emitSse, "info", $"Code files, components, UI logic, or attached files detected — forcing CodeEdit pipeline", ct: ct);
            pipelineType = PipelineType.CodeEdit;
        }

        PipelineType? chainedNext = null;
        List<(PipelineType Pipeline, string Summary)>? stages = null;

        if (!hasCodeInPrompt && !mentionsCodeFiles && !mentionsCodeLogic && !hasAttachedFiles)
        {
            var verifyPrompt = $"Verify this routing decision.\n\nTask: \"{prompt}\"\nRouter selected: {pipelineType} (commandScore={cmdScore}, editScore={editScore})\n\nPipeline types:\n- CommandExecution: running shell/terminal commands, downloading files via URL, file system operations OUTSIDE the codebase logic.\n- UnifiedPipeline (CodeEdit): modifying, adding, or refactoring source code in the project (e.g., .cs, .ts, .html, .css files). This includes implementing upload logic, modifying components, or changing API calls.\n\nIs this routing correct? \n- If the task mentions modifying or creating code in specific files (like 'upload.component.ts' or 'file.service.ts'), it MUST be UnifiedPipeline.\n- If the task asks to 'create a method', 'add a variable', or 'change logic', it MUST be UnifiedPipeline.\n- DO NOT route to CommandExecution just because the task mentions 'uploading files', 'fetch data', or 'files' — if the upload/fetch logic is being implemented in code, it's UnifiedPipeline.\n- Only suggest chaining if the task EXPLICITLY requires running terminal scripts, downloading files from URLs, or querying a database BEFORE code can be edited.\n\nReply ONLY with JSON:\n{{\"decision\": \"confirm\"}}\n{{\"decision\": \"override\", \"pipeline\": \"CommandExecution|UnifiedPipeline\"}}\n{{\"decision\": \"chain\", \"stages\": [{{\"pipeline\": \"CommandExecution\", \"summary\": \"...\"}}, {{\"pipeline\": \"UnifiedPipeline\", \"summary\": \"...\"}}]}}";
            var (vRaw, _, vErr) = await CallLlmRaw(
                "You verify task routing. Output only JSON.",
                verifyPrompt, ct, TimeSpan.FromSeconds(15), maxTokens: 256);

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
                        pipelineType = overridePipeline?.ToLowerInvariant() switch
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
        }


        List<object> allSteps = new();
        AgentPlan? plan = null;

        if (pipelineType == PipelineType.CommandExecution)
        {
            var result = await CommandExecutionPipeline(prompt, projectRoot, emitSse, ct,
                steeringContext: steeringContext, cardId: cardId);
            allSteps = result.steps;
            plan = result.plan;


            if (chainedNext == PipelineType.CodeEdit)
            {

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

                    await AttachFilesToCardAsync(cardId, createdFiles, emitSse, ct);

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


        bool complete = true;
        var hasFatalStepErrors = allSteps.OfType<Dictionary<string, object?>>()
            .Any(s => s.TryGetValue("status", out var st) &&
                      st?.ToString() == "error");

        if (hasFatalStepErrors)
        {
            complete = false;
            await EmitLog(emitSse, "warn",
                "Task marked INCOMPLETE — one or more steps failed with errors. " +
                "Skipping LLM quality check since step failures are deterministic.", ct: ct);
        }

        if (complete && !skipQualityCheck && allSteps.Count > 0)
        {
            var hasDone = allSteps.OfType<Dictionary<string, object?>>()
                .Any(s => s.TryGetValue("type", out var t) && t?.ToString() == "done_signal");
            var verified = allSteps.OfType<Dictionary<string, object?>>()
                .Any(s => s.TryGetValue("type", out var t) && t?.ToString() == "verified_complete");

            if (verified) hasDone = true;

            if (!hasDone)
            {
                var (ok, reason) = await AssessCompletion(prompt, allSteps, projectRoot, ct, plan, attachedFiles: attachedFiles);

                if (ok && hasFatalStepErrors)
                {
                    ok = false;
                    reason = "Step errors present — overriding LLM completion assessment";
                }

                complete = ok;
                if (!ok)
                {
                    await EmitLog(emitSse, "warn", $"Quality check: {reason}", ct: ct);

                    var doneIndices = new HashSet<int>();
                    for (var i = 0; i < (plan?.Plan?.Count ?? 0); i++)
                    {
                        var result = allSteps.OfType<Dictionary<string, object?>>()
                            .LastOrDefault(s =>
                                s.TryGetValue("planItemIndex", out var pIdxObj) &&
                                pIdxObj is int pIdx &&
                                pIdx == i &&
                                s.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created" or "skipped" &&
                                s.GetValueOrDefault("type")?.ToString() is "edit" or "create" or "rename");
                        if (result != null) doneIndices.Add(i);
                    }

                    var hasIncomplete = plan != null && doneIndices.Count < plan.Plan.Count;

                    if (hasIncomplete)
                    {
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

                    if (!complete && plan?.Plan?.Count > 0)
                    {
                        for (var i = 0; i < plan.Plan.Count; i++)
                        {
                            var step = plan.Plan[i];
                            var result = allSteps.OfType<Dictionary<string, object?>>()
                                .LastOrDefault(s =>
                                    s.TryGetValue("planItemIndex", out var pIdxObj) &&
                                    pIdxObj is int pIdx &&
                                    pIdx == i &&
                                    s.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created" or "skipped" &&
                                    s.GetValueOrDefault("type")?.ToString() is "edit" or "create" or "rename");
                            if (result != null) doneIndices.Add(i);
                        }
                    }

                    if (!complete && (plan?.Plan?.Count == 0 || doneIndices.Count == (plan?.Plan?.Count ?? 0)))
                    {
                        await EmitLog(emitSse, "info", "All steps done — checking for genuinely missing work…", ct: ct);

                        var scopedSteering = "The original plan's steps all succeeded. Only add steps for work the " +
                            "user EXPLICITLY requested that is still genuinely missing. Do NOT invent extra files, " +
                            "features, refactors, or improvements the user did not ask for. If nothing explicit is " +
                            "missing, return an empty plan." +
                            (string.IsNullOrWhiteSpace(steeringContext) ? "" : $"\n\n{steeringContext}");
                        var newSteps = await GenerateReplanStepsAsync(prompt, allSteps, plan,
                            scopedSteering, projectRoot, emitSse, ct,
                            attachedFiles: attachedFiles, qualityCheckReason: reason);

                        if (newSteps?.Count > 0)
                        {
                            var revertKeywords = new[] { "revert", "undo", "restore", "roll back", "rollback", "replace current content with" };
                            var safeSteps = new List<PlanStep>();

                            foreach (var s in newSteps)
                            {
                                var changeLower = (s.Change ?? "").ToLowerInvariant();
                                if (revertKeywords.Any(k => changeLower.Contains(k)))
                                {
                                    await EmitLog(emitSse, "warn", $"🚫 Replan generated a revert/undo step: '{s.Change}'. Blocking to prevent infinite loop.", ct: ct);
                                }
                                else
                                {
                                    safeSteps.Add(s);
                                }
                            }

                            if (safeSteps.Count == 0)
                            {
                                await EmitLog(emitSse, "warn", "Replan only generated revert/undo steps — ignoring.", ct: ct);
                                newSteps = null;
                            }
                            else
                            {
                                newSteps = safeSteps;
                            }
                        }

                        if (newSteps?.Count > 0)
                        {
                            plan = MergePlans(plan ?? new AgentPlan(), new AgentPlan { Plan = newSteps });
                            var existingChanges = plan!.Plan
                                .Select(p => NormalizeChangeForDedup(p.Change))
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);

                            var filteredNewSteps = newSteps
                                .Where(s => !existingChanges.Contains(NormalizeChangeForDedup(s.Change)))
                                .ToList();

                            if (filteredNewSteps.Count == 0)
                            {
                                await EmitLog(emitSse, "warn", "Replan generated duplicate steps of completed work — ignoring.", ct: ct);
                                newSteps = null;
                            }
                            else
                            {
                                newSteps = filteredNewSteps;
                            }
                        }

                        if (newSteps?.Count > 0)
                        {
                            plan = MergePlans(plan ?? new AgentPlan(), new AgentPlan { Plan = newSteps });
                            if (emitSse)
                                await SendSse(Response, "plan",
                                    new { thinking = plan.Thinking, summary = "Replan: added steps", items = plan.Plan }, ct);
                            await PersistBoardDataPlanAsync(cardId, plan.Plan, emitSse, ct,
                                summary: plan.Summary ?? "Replan: added steps", score: plan.Score);


                            var mergedDone = new HashSet<int>();
                            for (var i = 0; i < plan.Plan.Count; i++)
                            {
                                var step = plan.Plan[i];
                                var result = allSteps.OfType<Dictionary<string, object?>>()
                                    .LastOrDefault(s =>
                                        s.TryGetValue("planItemIndex", out var pIdxObj) &&
                                        pIdxObj is int pIdx &&
                                        pIdx == i &&
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
                            complete = plan?.Plan?.Select((p, i) =>
                            {
                                var result = allSteps.OfType<Dictionary<string, object?>>()
                                    .LastOrDefault(s =>
                                        s.TryGetValue("planItemIndex", out var pIdxObj) &&
                                        pIdxObj is int pIdx &&
                                        pIdx == i &&
                                        s.GetValueOrDefault("type")?.ToString() is "edit" or "create" or "rename");
                                return result != null && result.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created" or "skipped";
                            }).All(x => x) ?? true;
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


        if (createTests && isEdited)
        {
            await RunTestCreationPipeline(projectRoot, allSteps, emitSse, ct);
        }


        bool buildOk = true;
        if (allSteps.Count > 0 && isEdited && buildCommands != null && !string.IsNullOrWhiteSpace(buildCommands))
        {
            var cmds = ParseBuildCommands(buildCommands);
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
                await RepairPipeline(projectRoot, emitSse, ct, prompt, steeringContext, buildCommands);
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
        sb.AppendLine("Only address concrete failures or work the user EXPLICITLY requested that is genuinely missing.");
        sb.AppendLine("Do NOT add new files, features, refactors, or improvements the user did not ask for.");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(steeringContext)) { sb.AppendLine("## Steering"); sb.AppendLine(steeringContext); sb.AppendLine(); }

        if (existingPlan?.Plan?.Count > 0)
        {
            sb.AppendLine("## Existing plan with results");
            for (var i = 0; i < existingPlan.Plan.Count; i++)
            {
                var step = existingPlan.Plan[i];

                string? status = null;
                string? output = null;
                if (executedSteps != null)
                {
                    var result = executedSteps.OfType<Dictionary<string, object?>>()
                        .LastOrDefault(s =>
                            s.TryGetValue("planItemIndex", out var pIdxObj) &&
                            pIdxObj is int pIdx &&
                            pIdx == i);
                    if (result != null)
                    {
                        status = result.GetValueOrDefault("status")?.ToString();
                        var raw = result.GetValueOrDefault("output")?.ToString();
                        if (!string.IsNullOrWhiteSpace(raw))
                        {
                            var trimmed = raw.Trim();
                            if (trimmed.Length > 2000) trimmed = trimmed[..2000] + "… (truncated)";
                            output = trimmed;
                        }
                    }
                }
                var tag = status switch
                {
                    "done" or "modified" or "created" => "✓ DONE",
                    "skipped" => "○ SKIPPED",
                    "error" => "✗ FAILED",
                    _ => "… PENDING"
                };
                sb.AppendLine($"  {tag} {step.File}: {step.Change}");
                if (output != null)
                    sb.AppendLine($"    stdout: {output}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Original task"); sb.AppendLine(originalPrompt); sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(qualityCheckReason))
        {
            sb.AppendLine("## Quality check assessment");
            sb.AppendLine(qualityCheckReason);
            sb.AppendLine();
            sb.AppendLine("CRITICAL: The quality check above identifies specific missing implementations. You MUST create steps to implement exactly what it asks for. Do not return an empty plan if the quality check identifies missing methods or properties that need to be added.");
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
        sb.AppendLine("⚠ CRITICAL: The plan may involve SEQUENTIAL steps (e.g. 'Create file with X, then modify it to Y').");
        sb.AppendLine("If the file content matches Y (the final state), do NOT report X as missing. X was intentionally replaced by Y.");
        sb.AppendLine("Do NOT generate steps to revert the file to an earlier state. Only generate steps if the FINAL requested state is missing.");
        sb.AppendLine();
        sb.AppendLine("STOP AND THINK: does the CURRENT FILE CONTENT shown above ALREADY satisfy the ORIGINAL TASK, even imperfectly?");
        sb.AppendLine("If the explicit request is met, return an EMPTY plan — do NOT propose restructuring, renaming, moving code");
        sb.AppendLine("between methods, or 'cleanup' the user did not ask for.");
        sb.AppendLine("Only add a step if you can name a SPECIFIC piece of the ORIGINAL TASK that is VERIFIABLY absent from the");
        sb.AppendLine("current file content above.");
        sb.AppendLine("⚠ CRITICAL: The Quality Check assessment below may claim something is missing. DO NOT blindly trust it.");
        sb.AppendLine("Read the file content yourself. If the code IS already there, return an EMPTY plan. The quality check LLM often hallucinates failures.");

        if (string.IsNullOrWhiteSpace(qualityCheckReason))
        {
            sb.AppendLine("NEVER introduce a property/variable name that does not already appear in the current file content above —");
            sb.AppendLine("reuse existing names exactly, character for character.");
        }
        else
        {
            sb.AppendLine("If the quality check requires a new method or property, you MAY introduce it, but it must be exactly named as requested by the task or quality check.");
        }

        sb.AppendLine();
        sb.AppendLine("SCOPE DISCIPLINE — the #1 replan failure mode is scope drift:");
        sb.AppendLine("  * Do NOT reinterpret the original task. Read '## Original task' literally and stay on that topic.");
        sb.AppendLine("  * Do NOT pivot to a different approach. If the EXISTING PLAN (✓ DONE steps) chose approach X,");
        sb.AppendLine("    your new step must EXTEND X, not replace it with approach Y. If you think X is wrong, return");
        sb.AppendLine("    an EMPTY plan — the user will steer, not the replanner.");
        sb.AppendLine("  * Do NOT add new files, features, refactors, or improvements the user did not ask for.");
        sb.AppendLine("  * Do NOT invent new helper methods in service files. If you need to call a service, use the EXISTING methods. For example, if UserEventService has `insertUserEvent`, call it directly. Do NOT create `insertUserEventWithPlantName`.");
        sb.AppendLine("  * If a step in the EXISTING PLAN added a property/variable/CSS-rule/method, that name NOW EXISTS");
        sb.AppendLine("    in the file. Reuse it. Do NOT introduce a parallel mechanism.");
        sb.AppendLine("  * If the verification gaps can be closed by EDITING the code that step 1 already added, do that —");
        sb.AppendLine("    do not add a second step that lives in a different file.");
        sb.AppendLine("  * CROSS-FILE DEPENDENCIES: If a step failed because it referenced methods or properties that don't exist in the target file (e.g., an HTML template referencing a method not yet implemented in the .ts component), you MUST add a step to implement the missing method/property in the correct file BEFORE retrying the failed step.");
        sb.AppendLine();
        sb.AppendLine("Add at most 1 new step. If everything is done or you are unsure, return an EMPTY plan with no steps.");
        return sb.ToString();
    }
    private async Task<List<PlanStep>?> GenerateReplanStepsAsync(
        string originalPrompt, List<object> executedSteps, AgentPlan? existingPlan,
        string? steeringContext, string projectRoot, bool emitSse, CancellationToken ct,
        List<string>? attachedFiles = null, string qualityCheckReason = "")
    {
        var failHist = BuildFailedEditHistory(executedSteps);


        var failedCodeSnippets = new StringBuilder();
        foreach (var step in executedSteps.OfType<Dictionary<string, object?>>())
        {
            var status = step.GetValueOrDefault("status")?.ToString();
            if (status != "error" && status != "verify-abandoned") continue;

            var path = step.GetValueOrDefault("path")?.ToString() ?? "?";
            var error = step.GetValueOrDefault("error")?.ToString() ??
                        step.GetValueOrDefault("reason")?.ToString() ?? "";
            var failureCtx = step.GetValueOrDefault("failureContext")?.ToString();
            var attemptScores = step.GetValueOrDefault("attemptScores");
            var bestScore = step.GetValueOrDefault("bestScore");

            failedCodeSnippets.AppendLine($"### FAILED STEP: {path} ###");
            failedCodeSnippets.AppendLine($"Error: {error}");
            if (bestScore != null)
                failedCodeSnippets.AppendLine($"Best quality score achieved: {bestScore}/100");
            if (failureCtx != null)
                failedCodeSnippets.AppendLine($"Detailed failure context:\n{failureCtx}");
            failedCodeSnippets.AppendLine();
        }

        (StringBuilder fileContents, string warn) = await AgentUtilities.GetReplanFileContents(executedSteps, projectRoot, attachedFiles, ct);
        if (!string.IsNullOrEmpty(warn) && emitSse)
        {
            await EmitLog(emitSse, "warn", warn, ct: ct);
        }

        var replanPrompt = BuildReplanPrompt(originalPrompt, new List<string> { failHist },
            steeringContext, existingPlan, executedSteps, qualityCheckReason,
            fileContents.ToString() + "\n\n## FAILED CODE SNIPPETS (do NOT reproduce)\n" + failedCodeSnippets.ToString());

        var (raw, _, llmError) = await CallLlmRaw(
                "You are a plan-fixer. Output ONLY valid JSON with a 'plan' array. Example: {\"plan\": [{\"file\": \"path/to/file.js\", \"change\": \"describe the change\", \"priority\": 1, \"line\": 42}]}. For every edit step include the 1-based line number. Max 1-2 steps. Empty array if all done. CRITICAL: Do NOT generate steps that revert or redo completed work. If the CURRENT FILE CONTENT matches the final requested state, return an EMPTY plan.",
                replanPrompt, ct, requestTimeout: _infiniteTimeout);

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
                var line = item.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
                if (!string.IsNullOrWhiteSpace(file) && !string.IsNullOrWhiteSpace(change))
                    steps.Add(new PlanStep { File = file, Change = change, Priority = priority, LineNumber = line });
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



    private async Task<(AgentPlan plan, string discoveryContext)> RunPlanningConvergenceLoop(
        string prompt, string discoveryContext, string projectRoot, bool emitSse,
        CancellationToken ct, string? steeringContext)
    {
        AgentPlan? best = null;
        var steering = steeringContext;
        var exploredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var iter = 1; iter <= MAX_PLANNING_ITERATIONS; iter++)
        {
            var plan = await AnalyzePromptAndPlanCodeChanges(
                prompt, discoveryContext, projectRoot, emitSse, ct, steering);

            if (plan == null || plan.Plan.Count == 0)
            {
                if (best != null) break;
                throw new InvalidOperationException("LLM returned an empty or unparseable plan.");
            }

            plan.Plan = DeduplicateSteps(plan.Plan);
            plan.Plan = AgentUtilities.DeduplicateSimilarSteps(plan.Plan);




            var exploreSteps = plan.Plan
                .Where(p => p.File.Equals("_explore", StringComparison.OrdinalIgnoreCase)).ToList();



            var readOnlyPrefixes = new[] { "read", "look at", "examine", "inspect", "review",
                "understand", "study", "browse", "view", "check how", "see how",
                "get familiar", "explore" };
            foreach (var p in plan.Plan)
            {
                if (AgentUtilities.IsRelativePath(p.File) &&
                    readOnlyPrefixes.Any(prefix =>
                        (p.Change ?? "").Trim().ToLowerInvariant().StartsWith(prefix)))
                {
                    exploreSteps.Add(new PlanStep { File = "_explore", Change = p.File });
                }
            }


            var newExploreSteps = exploreSteps
                .Where(s => !string.IsNullOrWhiteSpace(s.Change) && exploredFiles.Add(s.Change))
                .ToList();

            if (newExploreSteps.Count > 0)
            {
                await EmitLog(emitSse, "info",
                    $"Planning {iter}/{MAX_PLANNING_ITERATIONS}: planner requested {newExploreSteps.Count} new exploration target(s) — gathering context…", ct: ct);
                discoveryContext = await ExplorationPipeline(newExploreSteps, discoveryContext, projectRoot, emitSse, ct);
                if (iter == MAX_PLANNING_ITERATIONS)
                    steering = AppendExploreSteering(steeringContext);
                continue;
            }

            if (best == null || plan.Score > best.Score) best = plan;

            await EmitLog(emitSse, "info",
                $"Planning {iter}/{MAX_PLANNING_ITERATIONS} — score {plan.Score}/100 ({plan.Plan.Count} step(s))",
                new { plan.Score }, ct: ct);

            if (plan.Score >= PLAN_SCORE_THRESHOLD)
            {
                await EmitLog(emitSse, "success",
                    $"Plan converged: score {plan.Score} ≥ {PLAN_SCORE_THRESHOLD}.", ct: ct);
                best = plan;
                break;
            }

            if (iter < MAX_PLANNING_ITERATIONS)
            {
                await EmitLog(emitSse, "info",
                    $"Plan score {plan.Score} below {PLAN_SCORE_THRESHOLD} — refining…", ct: ct);
                steering = BuildLowScoreSteering(plan, steeringContext);
            }
            else
            {
                await EmitLog(emitSse, "warn",
                    $"Planning budget exhausted at score {best!.Score} — proceeding with best plan.", ct: ct);
            }
        }


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


    private static string BuildLowScoreSteering(AgentPlan plan, string? prior)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Your previous plan scored {plan.Score}/100, below the confidence threshold of {PLAN_SCORE_THRESHOLD}.");
        sb.AppendLine("Do NOT explore more files. The discovery context already has everything you need.");
        sb.AppendLine("Raise your score by making each step's change description more precise:");
        sb.AppendLine("  • Name the exact method, property, or line range (e.g. \"In getUser() around line 42:…\")");
        sb.AppendLine("  • Describe the exact old → new behavior clearly");
        sb.AppendLine("  • If the plan is already correct, simply increase your score to 85+ and re-output it");
        sb.AppendLine("Do NOT change the file paths or add steps. Do NOT add _explore steps.");
        if (!string.IsNullOrWhiteSpace(prior)) { sb.AppendLine(); sb.AppendLine(prior); }
        return sb.ToString();
    }


    private static string AppendExploreSteering(string? prior)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You have exhausted exploration rounds. Produce the final edit plan NOW — no more _explore steps.");
        sb.AppendLine("Be decisive: keep the same file paths and give each step a precise change description. Score the plan 85+ if it's correct.");
        if (!string.IsNullOrWhiteSpace(prior)) { sb.AppendLine(); sb.AppendLine(prior); }
        return sb.ToString();
    }


    private static List<PlanStep> DeduplicateSteps(List<PlanStep> steps)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<PlanStep>();
        foreach (var step in steps)
        {
            var normChange = (step.Change ?? "").Trim().ToLowerInvariant();

            normChange = string.Join(" ", normChange.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
            var key = $"{step.File}|{normChange}";
            if (seen.Add(key))
                deduped.Add(step);
        }
        return deduped;
    }


    private async Task<(List<object> steps, AgentPlan plan)> UnifiedPipeline(
        string prompt, string projectRoot, bool emitSse, CancellationToken ct,
        List<string>? attachedFiles = null,
        bool skipContextReview = false,
        string? steeringContext = null,
        string? cardId = null)
    {
        var editKnowledge = await _editKnowledge.LoadAsync(projectRoot, ct);

        if (editKnowledge == null)
        {
            await _editKnowledge.EnsureExistsAsync(projectRoot, ct);
            editKnowledge = await _editKnowledge.LoadAsync(projectRoot, ct);
        }

        var editKnowledgeHeader = EditKnowledgeService.FormatForContext(editKnowledge);
        if (!string.IsNullOrWhiteSpace(editKnowledgeHeader))
        {
            await EmitLog(emitSse, "info",
                $"Loaded edit knowledge for project: {editKnowledge!.ProjectName} " +
                $"({editKnowledge.Do.Count} do, {editKnowledge.Dont.Count} dont, " +
                $"{editKnowledge.Patterns.Count} pattern categories, " +
                $"{editKnowledge.RecentFailures.Count} recent failures)", ct: ct);
        }

        var allSteps = new List<object>();

        await EmitLog(emitSse, "info", "Phase 1 — DISCOVER", new { prompt, attachedFiles, steeringContext, cardId }, ct: ct);
        var (discoveryContext, ds) = await RunBootstrapDiscovery(prompt, projectRoot, emitSse, attachedFiles, ct);
        allSteps.AddRange(ds);

        if (attachedFiles != null && attachedFiles.Count > 0)
        {
            var attachedSteering = "The user has explicitly attached one or more files for editing " +
                                   "(visible in DISCOVERY CONTEXT below).\n" +
                                   "You MUST plan your edits to target only the attached file(s). " +
                                   "Do NOT add _explore steps. Do NOT search for or reference any other files. " +
                                   "Do NOT try to understand how the code is called from elsewhere. " +
                                   "Read the attached files in the DISCOVERY CONTEXT and plan the required edits directly. " +
                                   "If the files are empty, plan steps to populate them with the necessary code based on the user's task.";

            steeringContext = string.IsNullOrWhiteSpace(steeringContext)
                ? attachedSteering
                : $"{steeringContext}\n\n{attachedSteering}";
        }

        if (!string.IsNullOrWhiteSpace(editKnowledgeHeader))
        {
            discoveryContext = editKnowledgeHeader + "\n\n" + discoveryContext;
        }



        if (emitSse && !skipContextReview)
        {
            await EmitLog(emitSse, "info", $"Reviewing context from {ds.Count} discovery steps ...", ct: ct);
            discoveryContext = await RunContextReview(ds, discoveryContext, allSteps, ct);
        }

        await EmitLog(emitSse, "info", "Phase 1.5 — META-PLAN (incremental construction)", ct: ct);
        var metaPlan = await RunIncrementalMetaPlanLoop(prompt, discoveryContext, projectRoot, emitSse, ct, cardId);


        AgentPlan plan;
        if (metaPlan?.SubPlans?.Count > 0)
        {
            await EmitLog(emitSse, "info", $"Phase 2 — META-PLAN ({metaPlan.SubPlans.Count} sub-plans)", ct: ct);
            if (emitSse)
                await SendSse(Response, "phase", new { phase = "metaplan", message = $"Meta-plan: {metaPlan.SubPlans.Count} sub-plans", subPlans = metaPlan.SubPlans }, ct);

            var combinedSteps = new List<PlanStep>();
            var accumulatedContext = new StringBuilder();
            await PersistMetaPlanToCardAsync(cardId, metaPlan, emitSse, ct);

            for (var i = 0; i < metaPlan.SubPlans.Count; i++)
            {
                var subPlan = metaPlan.SubPlans[i];

                var subPrompt = BuildSubPlanPrompt(
                      prompt, subPlan, i + 1, metaPlan.SubPlans.Count,
                      accumulatedContext.Length > 0 ? accumulatedContext.ToString() : null);

                AgentPlan? subPlanResult;
                try
                {
                    var (incSubPlan, updatedCtx) = await RunIncrementalPlanningLoop(
                        subPrompt, discoveryContext, projectRoot, emitSse, ct, subPlan.ContextNote, cardId);
                    subPlanResult = incSubPlan;
                    discoveryContext = updatedCtx;
                }
                catch (InvalidOperationException)
                {
                    await EmitLog(emitSse, "info",
                        $"Sub-plan '{subPlan.Title}' produced no actionable steps — treating as already satisfied.", ct: ct);
                    subPlanResult = new AgentPlan { Plan = new List<PlanStep>() };
                }

                if (subPlanResult?.Plan != null && subPlanResult.Plan.Count > 0)
                {
                    subPlanResult.Plan = await PruneIrrelevantPlanStepsAsync(subPlanResult.Plan, projectRoot, ct);

                    if (subPlanResult.Plan.Count > 0)
                    {
                        var subAudit = await PlanPreAuditAsync(subPlanResult, projectRoot, emitSse, ct, prompt);
                        if (subAudit != null && subAudit.Steps.Count > 0)
                        {
                            var alreadyDoneIdx = subAudit.Steps.Where(s => s.AlreadyDone).Select(s => s.Index).ToHashSet();
                            var decoupled = subAudit.Steps.Where(s => s.NeedsDecoupling && s.DecoupledSteps?.Count > 0).ToList();

                            if (alreadyDoneIdx.Count > 0 || decoupled.Count > 0)
                            {
                                var newSubItems = new List<PlanStep>();
                                for (var si = 0; si < subPlanResult.Plan.Count; si++)
                                {
                                    if (alreadyDoneIdx.Contains(si))
                                    {
                                        await EmitLog(emitSse, "info",
                                            $"Sub-plan {subPlan.Title}: step {si + 1} already done — skipping. " +
                                            $"Reason: {subAudit.Steps.First(s => s.Index == si).Reason}", ct: ct);
                                        continue;
                                    }
                                    var dec = decoupled.FirstOrDefault(d => d.Index == si);
                                    if (dec != null)
                                    {
                                        foreach (var sub in dec.DecoupledSteps!)
                                        {
                                            sub.Priority = subPlanResult.Plan[si].Priority;
                                            newSubItems.Add(sub);
                                        }
                                        continue;
                                    }
                                    newSubItems.Add(subPlanResult.Plan[si]);
                                }
                                subPlanResult.Plan = AgentUtilities.DeduplicateSimilarSteps(newSubItems);
                            }
                        }
                    }

                    if (subPlanResult.Plan.Count == 0)
                    {
                        await EmitLog(emitSse, "info",
                            $"Sub-plan '{subPlan.Title}' fully collapsed — all target changes already exist. Marking done.", ct: ct);
                        await UpdateMetaPlanSubPlanStatusAsync(cardId, subPlan.Id, true, emitSse, ct);
                        accumulatedContext.AppendLine($"## Sub-plan {i + 1} ({subPlan.Title}) — ALREADY COMPLETE (no changes needed) ##");
                        accumulatedContext.AppendLine();
                        continue;
                    }

                    if (emitSse)
                    {
                        await SendSse(Response, "plan", new
                        {
                            thinking = subPlanResult.Thinking,
                            summary = $"Sub-plan {i + 1}/{metaPlan.SubPlans.Count}: {subPlan.Title}",
                            items = subPlanResult.Plan
                        }, ct);
                    }

                    await PersistBoardDataPlanAsync(cardId, subPlanResult.Plan, emitSse, ct,
                        summary: $"Sub-plan {i + 1}/{metaPlan.SubPlans.Count}: {subPlan.Title}",
                        score: subPlanResult.Score);

                    var subResults = new List<object>();
                    await ExecutePlan(prompt, projectRoot, emitSse, "", subPlanResult, ct, subResults,
                        steeringContext: subPlan.ContextNote, attachedFiles: attachedFiles, cardId: cardId);

                    await UpdateMetaPlanSubPlanStatusAsync(cardId, subPlan.Id, true, emitSse, ct);


                    accumulatedContext.AppendLine($"## Sub-plan {i + 1} ({subPlan.Title}) — COMPLETED ##");
                    foreach (var r in subResults.OfType<Dictionary<string, object?>>())
                    {
                        var status = r.GetValueOrDefault("status")?.ToString();
                        var path = r.GetValueOrDefault("path")?.ToString();
                        var desc = r.GetValueOrDefault("description")?.ToString();
                        if (!string.IsNullOrWhiteSpace(path) && status is "done" or "modified" or "created")
                        {
                            accumulatedContext.AppendLine($"  ✓ [{path}] {desc}");
                        }
                    }
                    accumulatedContext.AppendLine();
                }
            }

            plan = new AgentPlan
            {
                Thinking = metaPlan.MetaThinking,
                Summary = metaPlan.MetaSummary,
                Score = 85,
                Plan = combinedSteps
            };
            discoveryContext = accumulatedContext.ToString();
        }
        else
        {
            await EmitLog(emitSse, "info", "Phase 2 — PLAN", ct: ct);
            if (emitSse)
            {
                await SendSse(Response, "phase", new { phase = "plan", message = "Planning...", contextSize = discoveryContext.Length, prompt }, ct);
            }

            var (p, convergedContext) = await RunIncrementalPlanningLoop(
                   prompt, discoveryContext, projectRoot, emitSse, ct, steeringContext, cardId);
            plan = p;
            discoveryContext = convergedContext;
        }

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
                if (plan?.Plan?.Count > 0)
                    plan.Plan = await PruneIrrelevantPlanStepsAsync(plan.Plan, projectRoot, ct);
                if (emitSse && plan != null)
                    await SendSse(Response, "plan",
                        new { thinking = plan.Thinking, summary = plan.Summary, items = plan.Plan }, ct);
            }
        }
        else
        {
            await EmitLog(emitSse, "success", $"Plan validation passed.", ct: ct);
        }

        if (plan != null && !string.IsNullOrEmpty(projectRoot))
        {
            plan = AgentUtilities.EnforceAngularScaffolding(plan, projectRoot) ?? plan;
            plan = AgentUtilities.EnforceProxyConfigForControllers(plan, projectRoot) ?? plan;
        }

        if (emitSse && plan?.Plan?.Count > 0)
        {
            await SendSse(Response, "plan",
                new { thinking = plan.Thinking, summary = plan.Summary, items = plan.Plan, audited = true }, ct);
        }

        if (!string.IsNullOrWhiteSpace(cardId) && plan?.Plan?.Count > 0)
        {
            await PersistBoardDataPlanAsync(cardId, plan.Plan, emitSse, ct, summary: plan.Summary ?? "", score: plan.Score);
        }

        if (plan?.Plan?.Count > 0)
        {
            var auditResult = await PlanPreAuditAsync(plan, projectRoot, emitSse, ct, prompt);
            if (auditResult != null && auditResult.Steps.Count > 0)
            {
                var alreadyDoneIndices = auditResult.Steps
                    .Where(s => s.AlreadyDone)
                    .Select(s => s.Index)
                    .ToHashSet();
                var decoupledSteps = new List<(int originalIndex, List<PlanStep> newSteps)>();
                foreach (var step in auditResult.Steps)
                {
                    if (step.NeedsDecoupling && step.DecoupledSteps?.Count > 0)
                    {
                        decoupledSteps.Add((step.Index, step.DecoupledSteps));
                    }
                }

                if (alreadyDoneIndices.Count > 0 || decoupledSteps.Count > 0)
                {
                    var newPlanItems = new List<PlanStep>();
                    for (var i = 0; i < plan.Plan.Count; i++)
                    {
                        if (alreadyDoneIndices.Contains(i))
                        {
                            await EmitLog(emitSse, "info",
                                $"Plan audit: step {i + 1} already done — skipping. Reason: {auditResult.Steps.First(s => s.Index == i).Reason}", ct: ct);
                            continue;
                        }
                        var decoupled = decoupledSteps.FirstOrDefault(d => d.originalIndex == i);
                        if (decoupled != default)
                        {
                            await EmitLog(emitSse, "info",
                                $"Plan audit: step {i + 1} decoupled into {decoupled.newSteps.Count} sub-steps", ct: ct);
                            newPlanItems.Add(plan.Plan[i]);
                            foreach (var sub in decoupled.newSteps)
                            {
                                sub.Priority = plan.Plan[i].Priority;
                                newPlanItems.Add(sub);
                            }
                            continue;
                        }
                        newPlanItems.Add(plan.Plan[i]);
                    }
                    plan.Plan = newPlanItems;
                    plan.Plan = AgentUtilities.RemergeTableCreationSplits(plan.Plan);
                    plan.Plan = AgentUtilities.DeduplicateSimilarSteps(plan.Plan);
                    plan.Plan = await PruneIrrelevantPlanStepsAsync(plan.Plan, projectRoot, ct);

                    if (emitSse)
                    {
                        await SendSse(Response, "plan", new { thinking = plan.Thinking, summary = plan.Summary, items = plan.Plan, audited = true }, ct);
                    }
                }
            }
        }


        if (plan != null)
            await ValidatePlanLineNumbersAsync(plan, projectRoot, ct);



        if (plan?.Plan?.Count > 1)
        {
            plan = await RunPlanCoherenceCheckAsync(
                plan, projectRoot, prompt, emitSse, ct);
            if (plan?.Plan?.Count > 0)
                plan.Plan = await PruneIrrelevantPlanStepsAsync(plan.Plan, projectRoot, ct);
            if (!string.IsNullOrWhiteSpace(cardId) && plan?.Plan?.Count > 0)
                await PersistBoardDataPlanAsync(cardId, plan.Plan, emitSse, ct,
                    summary: plan.Summary ?? "", score: plan.Score);
        }


        await EmitLog(emitSse, "info", "Phase 3 — EXECUTE", ct: ct);
        if (emitSse)
        {
            await SendSse(Response, "phase", new { phase = "execute", message = "Executing plan…" }, ct);
        }

        var fileBackups = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in plan?.Plan ?? [])
        {
            if (!AgentUtilities.IsRelativePath(step.File)) continue;
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, step.File.Replace('/', Path.DirectorySeparatorChar)));
            if (!fileBackups.ContainsKey(fullPath))
            {
                fileBackups[fullPath] = System.IO.File.Exists(fullPath)
                    ? System.IO.File.ReadAllText(fullPath, Encoding.UTF8)
                    : null;
            }
        }

        try
        {
            await ExecutePlan(prompt, projectRoot, emitSse, discoveryContext, plan ?? new AgentPlan(), ct, allSteps,
                steeringContext: steeringContext, attachedFiles: attachedFiles,
                cardId: cardId);
        }
        catch (StepFatalException ex)
        {
            await EmitLog(emitSse, "error",
                $"⛔ Plan execution halted due to fatal step failure: {ex.Message}", ct: ct);

            if (emitSse)
            {
                await SendSse(Response, "fatal", new
                {
                    reason = "A plan step failed irrecoverably — execution halted",
                    failedStep = ex.FailedFilePath,
                    error = ex.Message
                }, ct);
            }

            return (allSteps, plan ?? new AgentPlan());
        }

        var (taskComplete, verificationDetails) = await PostExecuteVerify(prompt, projectRoot, emitSse, allSteps, ct);

        if (!taskComplete)
        {
            var stepTruthCompleted = await VerifyCompletedFromStepTruthAsync(allSteps, projectRoot, ct);
            if (stepTruthCompleted)
            {
                await EmitLog(emitSse, "warn",
                    $"Post-execution verification says task is incomplete despite all steps having status 'done'. " +
                    $"Verifier details: {verificationDetails}. Re-running verifier to check for race/staleness...", ct: ct);

                var (reverifyComplete, reverifyDetails) = await PostExecuteVerify(
                    prompt, projectRoot, emitSse, allSteps, ct);
                if (reverifyComplete)
                {
                    await EmitLog(emitSse, "info",
                        "Re-verification passed — trusting verifier on retry.", ct: ct);
                    taskComplete = true;
                    verificationDetails = reverifyDetails;
                }
                else
                {
                    await EmitLog(emitSse, "warn",
                        $"Re-verification still says incomplete: {reverifyDetails}. " +
                        $"All steps are done — trusting step truth over verifier.", ct: ct);
                    taskComplete = true;
                    verificationDetails = reverifyDetails + " (overridden: all steps completed successfully despite verifier concerns)";
                }
            }
        }

        if (taskComplete)
        {
            allSteps.Add(new Dictionary<string, object?>
            {
                ["type"] = "verified_complete",
                ["status"] = "done",
                ["reason"] = verificationDetails
            });
        }
        else
        {
            await EmitLog(emitSse, "warn", $"Post-execution verification: {verificationDetails}. Re-planning...", ct: ct);

            var allFailures = allSteps.OfType<Dictionary<string, object?>>()
                .Where(s => s.GetValueOrDefault("status")?.ToString() is "error" or "verify-abandoned")
                .ToList();


            var failureContextForReplan = new StringBuilder();
            foreach (var f in allFailures)
            {
                var path = f.GetValueOrDefault("path")?.ToString() ?? "?";
                var reason = f.GetValueOrDefault("reason")?.ToString() ?? f.GetValueOrDefault("error")?.ToString() ?? "";
                var bestScore = f.GetValueOrDefault("bestScore");
                var failureCtx = f.GetValueOrDefault("failureContext")?.ToString();

                failureContextForReplan.AppendLine($"FAILED: {path} — {reason}");
                if (bestScore != null)
                    failureContextForReplan.AppendLine($"  Best score: {bestScore}/100");
                if (failureCtx != null)
                    failureContextForReplan.AppendLine($"  Context: {TruncateForLlm(failureCtx, 1000)}");
                failureContextForReplan.AppendLine();
            }

            var enhancedSteering = (steeringContext ?? "") +
                "\n\n## PRIOR FAILURES — avoid repeating these approaches ##\n" +
                failureContextForReplan.ToString();

            var replanSteps = await GenerateReplanStepsAsync(prompt, allSteps, plan,
                enhancedSteering, projectRoot, emitSse, ct,
                attachedFiles: attachedFiles,
                qualityCheckReason: verificationDetails + "\n\nPrior failures:\n" + failureContextForReplan.ToString());

            if (replanSteps != null && replanSteps.Count > 0 && plan != null)
            {
                var originalStepCount = plan?.Plan?.Count ?? 0;
                plan = MergePlans(plan ?? new AgentPlan(),
                    new AgentPlan { Plan = replanSteps, Summary = "Re-plan: added steps", Score = 0 });

                if (plan?.Plan?.Count > 0)
                    plan.Plan = await PruneIrrelevantPlanStepsAsync(plan.Plan, projectRoot, ct);

                if (emitSse && plan != null)
                {
                    await SendSse(Response, "plan",
                        new { thinking = plan.Thinking, summary = "Re-plan: added steps", items = plan.Plan }, ct);
                }

                if (plan != null)
                {
                    await PersistBoardDataPlanAsync(cardId, plan.Plan, emitSse, ct,
                        summary: plan.Summary ?? "Re-plan: added steps", score: plan.Score, append: false);
                }

                var mergedDone = new HashSet<int>();
                for (var i = 0; i < originalStepCount && i < plan?.Plan?.Count; i++)
                { mergedDone.Add(i); }
                if (plan != null)
                {
                    await ExecutePlan(prompt, projectRoot, emitSse, "", plan, ct, allSteps,
                        steeringContext: enhancedSteering, attachedFiles: attachedFiles,
                        completedStepIndices: mergedDone, cardId: cardId);
                }
            }
            else
            {

                await EmitLog(emitSse, "warn",
                    "Re-plan returned no steps. Reverting changes and generating a fresh plan with verification feedback...", ct: ct);

                foreach (var kvp in fileBackups)
                {
                    try
                    {
                        if (kvp.Value != null)
                            await System.IO.File.WriteAllTextAsync(kvp.Key, kvp.Value, Encoding.UTF8, ct);
                        else if (System.IO.File.Exists(kvp.Key))
                            System.IO.File.Delete(kvp.Key);
                    }
                    catch { }
                }


                var freshPlanSteering = "A previous attempt to complete this task failed verification with the following issues:\n" +
                                         $"{verificationDetails}\n\n";
                if (allFailures.Count > 0)
                {
                    freshPlanSteering += "Prior execution failures:\n" + failureContextForReplan.ToString() + "\n";
                }
                freshPlanSteering += "The previous changes have been REVERTED. You MUST generate a completely new plan that addresses these issues and correctly completes the task. Do NOT repeat the mistakes of the previous attempt.";

                var (freshPlan, _) = await RunPlanningConvergenceLoop(
                    prompt, discoveryContext, projectRoot, emitSse, ct, freshPlanSteering);

                if (freshPlan != null && freshPlan.Plan.Count > 0)
                {
                    plan = freshPlan;

                    await ExecutePlan(prompt, projectRoot, emitSse, discoveryContext, plan, ct, allSteps,
                        steeringContext: freshPlanSteering, attachedFiles: attachedFiles, cardId: cardId);
                }
                else
                {
                    await EmitLog(emitSse, "warn",
                        "Fresh plan generation failed — stopping with verification gaps unresolved.", ct: ct);
                }
            }
        }

        return (allSteps, plan ?? new AgentPlan());
    }

    private async Task<Dictionary<string, string>> AskUserAsync(string question, List<QuestionField>? fields = null, CancellationToken ct = default, Object? additionalData = null)
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
            fields = pending.Fields.Select(f => new { f.Key, f.Label, f.Type, f.DefaultValue }).ToList(),
            additionalData
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

    private async Task<(bool complete, string details)> PostExecuteVerify(
        string originalPrompt, string projectRoot, bool emitSse,
        List<object> allResults, CancellationToken ct)
    {

        var modifiedPaths = allResults
            .OfType<Dictionary<string, object?>>()
            .Where(r => r.TryGetValue("type", out var t) && t?.ToString() == "edit")
            .Select(r => r.GetValueOrDefault("path")?.ToString())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (modifiedPaths.Count == 0)
        {
            var exploredPaths = allResults
                .OfType<Dictionary<string, object?>>()
                .Where(r => r.TryGetValue("type", out var t) && t?.ToString() == "read")
                .Select(r => r.GetValueOrDefault("path")?.ToString())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (exploredPaths.Count == 0)
            {
                return (true, "");
            }

            modifiedPaths = exploredPaths;
        }

        var sb = new StringBuilder();
        sb.AppendLine("### ORIGINAL TASK ###");
        sb.AppendLine(originalPrompt);
        sb.AppendLine();
        sb.AppendLine("### CURRENT STATE OF MODIFIED FILES ###");


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

                            var baseDir = Path.GetDirectoryName(fullPath) ?? "";
                            var resolved = Path.GetFullPath(Path.Combine(baseDir, importPath));

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
        sb.AppendLine("1. Is the original task fully implemented? Evaluate ONLY against what the original task asks for.");
        sb.AppendLine("   Ignore existing code that predates this task — the task may be meant to REPLACE it.");
        sb.AppendLine("   CRITICAL: If the task asks to MOVE code, SEARCH THE ENTIRE FILE for the code in its new location. Do NOT report it as 'missing' or 'not moved' just because it is no longer in its original spot. Verify the actual file content provided above.");
        sb.AppendLine("   CRITICAL: If the requested content is physically present in the file, even if the formatting or nesting is slightly incorrect, the task is COMPLETE. Do NOT report a failure for minor formatting issues.");
        sb.AppendLine("   Example: if the task says 'wrap in details/summary' but the file already has per-column");
        sb.AppendLine("   collapse buttons, report that details/summary is missing — do NOT report that");
        sb.AppendLine("   toggleColumnCollapse is unimplemented, because the task has nothing to do with that.");
        sb.AppendLine("2. Do ALL property accesses in the code exist on their respective types/interfaces?");
        sb.AppendLine("3. Are ALL referenced methods, functions, and classes defined or imported?");
        sb.AppendLine("4. Are ALL imports present for every type used?");
        sb.AppendLine("5. Would the code compile without errors?");
        sb.AppendLine();
        sb.AppendLine("Answer with a single JSON object:");
        sb.AppendLine("{ \"complete\": true|false, \"reason\": \"short explanation\", \"issues\": [\"issue1\", \"issue2\"] }");
        sb.AppendLine("Set complete=true only if the task is fully implemented AND the code would compile.");
        sb.AppendLine("Set complete=false if anything is missing, broken, or would cause compilation errors.");
        sb.AppendLine("Include a brief list of specific issues in the 'issues' array when complete=false.");

        var verifySystemPrompt = "You are a meticulous code reviewer verifying if a task is fully complete based ONLY on the original task prompt. " +
       "Do NOT invent new requirements or check for things not explicitly mentioned in the task. " +
       "If the original task asked to modify a specific method, and that method was modified, the task is complete. " +
       "Check if the code would compile (no syntax errors, missing brackets, or undefined variables). " +
       "Output ONLY a JSON object: {\"complete\": true/false, \"reason\": \"...\", \"issues\": [\"...\"]}.";

        var (raw, _, error) = await CallLlmRawStreaming(
            verifySystemPrompt, sb.ToString(), emitSse, ct,
            requestTimeout: _infiniteTimeout, maxTokens: 512);

        if (string.IsNullOrWhiteSpace(raw))
        {
            await EmitLog(emitSse, "warn", $"Verification LLM returned empty: {error}", ct: ct);
            return (true, "");
        }

        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (m.Success) cleaned = m.Groups[1].Value.Trim();
            }
            cleaned = ExtractFirstJsonObject(cleaned);

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

        return (true, "");
    }


    private async Task<List<PlanStep>> TryReplanAfterStep(
    string prompt, List<object> allResults, AgentPlan plan,
    string? steeringContext, string projectRoot, bool emitSse,
    CancellationToken ct, List<PlanStep> planItems, int itemIdx,
    bool stepSkipped, bool stepSucceeded, List<string>? attachedFiles,
    int[] replanBudget, string? cardId = null)
    {
        if (!stepSkipped && !stepSucceeded) return planItems;
        var remainingSteps = planItems.Skip(itemIdx + 1)
            .Where(p => !string.IsNullOrWhiteSpace(p.File)).ToList();
        if (remainingSteps.Count > 0) return planItems;

        if (replanBudget[0] <= 0)
        {
            await EmitLog(emitSse, "info",
                "Replan budget exhausted — any remaining gaps will be handled by post-execution verification.", ct: ct);
            return planItems;
        }

        var moreSteps = await GenerateReplanStepsAsync(prompt, allResults, plan,
            steeringContext, projectRoot, emitSse, ct, attachedFiles: attachedFiles);
        if (moreSteps != null && moreSteps.Count > 0)
        {
            replanBudget[0]--;
            planItems = MergePlanSteps(planItems, moreSteps);
            if (emitSse)
                await SendSse(Response, "plan",
                    new { summary = $"Added {moreSteps.Count} step(s)", items = planItems }, ct);
            await PersistBoardDataPlanAsync(cardId, planItems, emitSse, ct,
                summary: $"Added {moreSteps.Count} step(s)", score: 0);
        }
        return planItems;
    }

    private async Task<(string? oldStr, string? newStr, bool fromFormatC)?> TryForcedMethodInsertAsync(
        PlanStep step, string fullPath, string relPath, string explorationContext,
        bool emitSse, CancellationToken ct)
    {
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        var (_, supportsFormatC, _) = AgentUtilities.GetLanguageProfile(ext);
        if (!supportsFormatC) return null;
        if (!System.IO.File.Exists(fullPath)) return null;

        var change = step.Change ?? "";
        if (!Regex.IsMatch(change, @"\b(add|create|insert)\b.{0,40}\b(method|endpoint|action|route|function)\b",
                RegexOptions.IgnoreCase))
            return null;



        var anchorMatch = Regex.Match(change,
            @"\bafter\s+(?:the\s+)?([A-Za-z_]\w*)\s*(?:method|endpoint|\()?", RegexOptions.IgnoreCase);
        string? anchorName = anchorMatch.Success ? anchorMatch.Groups[1].Value : null;

        string? anchorBody = null;
        string? astErr = null;
        var targetTypeForLang = "method";

        if (!string.IsNullOrWhiteSpace(anchorName))
            (anchorBody, astErr) = AstResolveEdit(fullPath, targetTypeForLang, anchorName, returnTail: false);


        if (anchorBody == null)
        {
            var sourceText = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
            var lastMethodName = ext == ".cs"
                ? Regex.Matches(sourceText,
                    @"(?:(?:public|private|protected|internal)\s+)?(?:(?:static|virtual|override|abstract|async)\s+)*\w+(?:<[^>]*>)?\s+(\w+)\s*\(")
                    .Cast<Match>().LastOrDefault()?.Groups[1].Value
                : Regex.Matches(sourceText, @"\b(?:function\s+)?([A-Za-z_]\w*)\s*\([^)]*\)\s*\{")
                    .Cast<Match>().LastOrDefault()?.Groups[1].Value;

            if (string.IsNullOrWhiteSpace(lastMethodName)) return null;
            (anchorBody, astErr) = AstResolveEdit(fullPath, targetTypeForLang, lastMethodName, returnTail: false);
            anchorName = lastMethodName;
        }

        if (anchorBody == null)
        {
            await EmitLog(emitSse, "info",
                $"  Forced-insert fast-path: could not resolve anchor ({astErr}) — falling back to normal resolver", ct: ct);
            return null;
        }


        var sysPrompt =
            "Output ONLY the raw source code for ONE new method/function — nothing else. " +
            "No JSON, no markdown fences, no explanation, no surrounding class/namespace. " +
            "Include the full signature (attributes, access modifier, return type, parameters) and complete body. " +
            "If the method needs SQL table creation, put a CREATE TABLE IF NOT EXISTS statement as the FIRST " +
            "statement in the body, before any INSERT/UPDATE/SELECT — never as a separate method.";

        var userPrompt = new StringBuilder();
        userPrompt.AppendLine($"FILE: {relPath}");
        userPrompt.AppendLine($"CHANGE REQUIRED: {change}");
        userPrompt.AppendLine();
        userPrompt.AppendLine("EXISTING METHOD THIS WILL BE INSERTED AFTER (for style/pattern reference):");
        userPrompt.AppendLine("```");
        userPrompt.AppendLine(anchorBody.Length > 1500 ? anchorBody[..1500] : anchorBody);
        userPrompt.AppendLine("```");
        if (!string.IsNullOrWhiteSpace(explorationContext))
        {
            userPrompt.AppendLine();
            userPrompt.AppendLine("RELATED CONTEXT:");
            userPrompt.AppendLine(explorationContext.Length > 3000 ? explorationContext[..3000] : explorationContext);
        }

        var (raw, _, err) = await CallLlmRawStreaming(sysPrompt, userPrompt.ToString(), emitSse, ct,
            requestTimeout: TimeSpan.FromMinutes(2), maxTokens: 1024);

        if (string.IsNullOrWhiteSpace(raw)) return null;

        var newCode = raw.Trim();
        if (newCode.StartsWith("```"))
        {
            var m = Regex.Match(newCode, @"```(?:\w+)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (m.Success) newCode = m.Groups[1].Value.Trim();
        }
        if (string.IsNullOrWhiteSpace(newCode)) return null;

        await EmitLog(emitSse, "info",
            $"  🎯 Forced FORMAT C insert after '{anchorName}' — model only supplied the method body ({newCode.Length} chars)", ct: ct);

        var indented = AutoIndentCode(anchorBody, newCode, relPath);
        var newStr = anchorBody + "\n\n" + indented;
        return (anchorBody, newStr, true);
    }
    private async Task<bool> ClassifyIsBuildRepairPromptAsync(string prompt, CancellationToken ct)
    {
        const string sys =
            "You classify a single user request. Answer ONLY with JSON: {\"isBuildRepair\": true|false}.\n" +
            "isBuildRepair = true ONLY if the user is asking to fix compilation/build errors or warnings " +
            "in the existing project — i.e. the build is currently broken and they want it fixed, with " +
            "no new feature or code-change request attached.\n" +
            "isBuildRepair = false if the user is asking for a new feature, a UI change, a refactor, or " +
            "any request that happens to mention words like 'build', 'error', 'warning' in an unrelated " +
            "sense (e.g. 'build out this feature', 'wire it up like X', 'add a popup panel').";

        var (raw, _, _) = await CallLlmRaw(sys, prompt, ct, TimeSpan.FromSeconds(10), maxTokens: 32);
        if (string.IsNullOrWhiteSpace(raw)) return false;

        try
        {
            var cleaned = ExtractFirstJsonObject(raw);
            using var doc = JsonDocument.Parse(cleaned);
            return doc.RootElement.TryGetProperty("isBuildRepair", out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }
    private async Task<AgentPlan?> RecoverPlanFromRamblingAsync(
        bool emitSse, CancellationToken ct, string ramblingRaw)
    {


        if (ramblingRaw.Contains('{')) return null;

        await EmitLog(emitSse, "warn",
            "Planner produced pure prose with no JSON — attempting recovery from its own reasoning", ct: ct);

        var tail = ramblingRaw.Length > 3000 ? ramblingRaw[^3000..] : ramblingRaw;
        var recoveryPrompt =
            "You were asked to plan code changes and output ONLY a JSON object, but instead you wrote " +
            "free-form reasoning and never produced the JSON. Here is the reasoning you already wrote:\n\n" +
            $"```\n{tail}\n```\n\n" +
            "STOP reasoning further. Based on the analysis above, output ONLY the JSON plan now. " +
            "Start your response with '{' as the very first character. No prose, no markdown fences.";

        var (raw, _, _) = await CallLlmRawStreaming(
            BuildPlanningPrompt(), recoveryPrompt, emitSse, ct,
            requestTimeout: TimeSpan.FromMinutes(3), maxTokens: 2048);

        if (string.IsNullOrWhiteSpace(raw)) return null;

        var plan = AgentUtilities.ParsePlan(raw);
        if (plan == null && raw.Contains("<<<STEP", StringComparison.OrdinalIgnoreCase))
            plan = AgentUtilities.ParseDelimitedPlan(raw);

        if (plan != null)
            await EmitLog(emitSse, "success", "Recovery pass produced a valid plan from prior reasoning", ct: ct);

        return plan;
    }


    private static string? GetStepSignature(string file, string change)
    {
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(change)) return null;
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var methodMatches = Regex.Matches(change,
            @"\b(Post|Add|Get|Put|Delete|Create|Insert|Update)[A-Z][A-Za-z0-9]*\b");
        foreach (Match m in methodMatches)
        {
            parts.Add(m.Value);

            var entity = Regex.Replace(m.Value, @"^(Post|Add|Get|Put|Delete|Create|Insert|Update)", "");
            entity = entity.TrimEnd('s');
            parts.Add("e:" + entity);
        }
        if (parts.Count == 0) return null;
        var normalized = parts.OrderBy(x => x);
        return file.Replace('\\', '/') + "::" + string.Join("|", normalized);
    }

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
        var replanBudget = new[] { 1 };
        var alreadyDecoupled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var completedStepSignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var itemIdx = 0; itemIdx < planItems.Count; itemIdx++)
        {
            ct.ThrowIfCancellationRequested();

            var item = planItems[itemIdx];


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
                    await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                }
                else await EmitLog(emitSse, "warn", $"Delete target not found: {target}", ct: ct);
                continue;
            }

            if (planFile.Equals("_git", StringComparison.OrdinalIgnoreCase))
            {
                stepIndex = await ExecuteGitStep(changeDesc, projectRoot, emitSse, ct, allResults, stepIndex);
                await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
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

            if (planFile.Equals("_create_directory", StringComparison.OrdinalIgnoreCase))
            {
                var dirRelPath = changeDesc.Replace('\\', '/');
                var dirFullPath = Path.GetFullPath(Path.Combine(projectRoot, dirRelPath.Replace('/', Path.DirectorySeparatorChar)));

                await EmitLog(emitSse, "info", $"Creating directory: {dirRelPath}", ct: ct);
                if (emitSse)
                    await SendSse(Response, "step", new
                    {
                        index = stepIndex,
                        type = "create",
                        status = "running",
                        path = dirRelPath,
                        description = item.Change,
                        planItemIndex = itemIdx
                    }, ct);

                try
                {
                    Directory.CreateDirectory(dirFullPath);
                    await EmitLog(emitSse, "success", $"Created directory {dirRelPath}", ct: ct);

                    var createResult = new Dictionary<string, object?>
                    {
                        ["index"] = stepIndex,
                        ["type"] = "create",
                        ["status"] = "done",
                        ["path"] = dirRelPath,
                        ["description"] = item.Change,
                        ["planItemIndex"] = itemIdx
                    };

                    if (emitSse) await SendSse(Response, "step", createResult, ct);
                    allResults.Add(createResult);
                    await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                }
                catch (Exception ex)
                {
                    await EmitLog(emitSse, "error", $"Failed to create directory {dirRelPath}: {ex.Message}", ct: ct);
                    var errResult = new Dictionary<string, object?>
                    {
                        ["index"] = stepIndex,
                        ["type"] = "create",
                        ["status"] = "error",
                        ["path"] = dirRelPath,
                        ["error"] = ex.Message,
                        ["planItemIndex"] = itemIdx
                    };
                    if (emitSse) await SendSse(Response, "step", errResult, ct);
                    allResults.Add(errResult);
                    await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                }

                stepIndex++;
                continue;
            }

            if (planFile.Equals("_create_file", StringComparison.OrdinalIgnoreCase))
            {
                await EmitLog(emitSse, "info", $"Creating file: {changeDesc}", ct: ct);
                if (emitSse)
                    await SendSse(Response, "step", new
                    {
                        index = stepIndex,
                        type = "create",
                        status = "running",
                        path = changeDesc,
                        description = item.Change,
                        planItemIndex = itemIdx
                    }, ct);


                var extractSysPrompt = "You extract file paths from instructions. Output ONLY the relative file path (e.g., 'folder/file.ext'). No quotes, no markdown, no explanation.";
                var (extractedPath, _, _) = await CallLlmRaw(extractSysPrompt, changeDesc, ct, TimeSpan.FromSeconds(15), maxTokens: 64);

                var newFileRelPath = extractedPath.Trim().Trim('"', '\'', '`', ' ');


                if (string.IsNullOrWhiteSpace(newFileRelPath) || newFileRelPath.Contains(' ') || !newFileRelPath.Contains('.'))
                {
                    var match = Regex.Match(changeDesc, @"([\w\-/\\]+\.\w{1,5})");
                    if (match.Success)
                    {
                        newFileRelPath = match.Groups[1].Value;
                    }
                    else
                    {
                        await EmitLog(emitSse, "error", $"Could not extract valid file path from _create_file description: {changeDesc}", ct: ct);
                        var errResult = new Dictionary<string, object?>
                        {
                            ["index"] = stepIndex,
                            ["type"] = "create",
                            ["status"] = "error",
                            ["path"] = changeDesc,
                            ["error"] = "Missing filename in _create_file description",
                            ["planItemIndex"] = itemIdx
                        };
                        if (emitSse) await SendSse(Response, "step", errResult, ct);
                        allResults.Add(errResult);
                        await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                        stepIndex++;
                        continue;
                    }
                }

                var newFileFullPath = Path.GetFullPath(Path.Combine(projectRoot, newFileRelPath.Replace('/', Path.DirectorySeparatorChar)));
                var contentToWrite = item.NewString ?? "";

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(newFileFullPath)!);
                    await System.IO.File.WriteAllTextAsync(newFileFullPath, contentToWrite, Encoding.UTF8, ct);
                    await EmitLog(emitSse, "success", $"Created {newFileRelPath} ({contentToWrite.Length} chars)", ct: ct);

                    var createResult = new Dictionary<string, object?>
                    {
                        ["index"] = stepIndex,
                        ["type"] = "create",
                        ["status"] = "done",
                        ["path"] = newFileRelPath,
                        ["description"] = item.Change,
                        ["planItemIndex"] = itemIdx
                    };

                    if (emitSse) await SendSse(Response, "step", createResult, ct);
                    allResults.Add(createResult);
                    await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                }
                catch (Exception ex)
                {
                    await EmitLog(emitSse, "error", $"Failed to create {newFileRelPath}: {ex.Message}", ct: ct);
                    var errResult = new Dictionary<string, object?>
                    {
                        ["index"] = stepIndex,
                        ["type"] = "create",
                        ["status"] = "error",
                        ["path"] = newFileRelPath,
                        ["error"] = ex.Message,
                        ["planItemIndex"] = itemIdx
                    };
                    if (emitSse) await SendSse(Response, "step", errResult, ct);
                    allResults.Add(errResult);
                    await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                }

                stepIndex++;
                continue;
            }

            if (planFile.Equals("_ping", StringComparison.OrdinalIgnoreCase))
            {
                stepIndex = await ExecutePingStep(changeDesc, projectRoot, emitSse, ct, allResults, stepIndex);
                await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                continue;
            }

            if (planFile.Equals("_package_install", StringComparison.OrdinalIgnoreCase))
            {
                stepIndex = await ExecutePackageInstallStep(changeDesc, projectRoot, emitSse, ct, allResults, stepIndex);
                await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                continue;
            }

            if (planFile.Equals("_command", StringComparison.OrdinalIgnoreCase))
            {
                var stepSkipped = false;
                var cmd = changeDesc.Trim().Trim('`', '"', '\'');
                if (!string.IsNullOrWhiteSpace(cmd))
                {
                    await EmitLog(emitSse, "info", $"Command: {cmd}", ct: ct);
                    _terminal.Start();
                    var cs = new AgentStep { Index = 0, Type = "command", Command = cmd, Description = cmd };
                    var prevCount = allResults.Count;
                    var cr = await ExecuteSteps(new List<AgentStep> { cs }, projectRoot, stepIndex, emitSse, ct);
                    stepIndex += cr.Count; allResults.AddRange(cr);
                    await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                    planItems = await TryReplanAfterStep(prompt, allResults, plan,
                        steeringContext, projectRoot, emitSse, ct, planItems, itemIdx,
                        stepSkipped, allResults.Count > prevCount, attachedFiles, replanBudget, cardId: cardId);
                }
                continue;
            }

            if (planFile.Equals("_web_search", StringComparison.OrdinalIgnoreCase) ||
                planFile.Equals("_web_fetch", StringComparison.OrdinalIgnoreCase))
            {
                (stepIndex, discoveryContext) = await ExecuteWebPlanStep(planFile, changeDesc, prompt, projectRoot, emitSse, ct,
                    allResults, planItems, itemIdx, stepIndex, discoveryContext, webCtx);
                await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
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
                await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                continue;
            }

            if (AgentUtilities.IsRelativePath(planFile))
            {
                var readOnlyPrefixes = new[] { "read", "look at", "examine", "inspect", "review", "understand",
                    "study", "browse", "view", "check how", "see how", "get familiar", "explore" };
                var changeLower = (item.Change ?? "").Trim().ToLowerInvariant();
                if (readOnlyPrefixes.Any(p => changeLower.StartsWith(p)))
                {
                    await EmitLog(emitSse, "info",
                        $"⏭ Read-only step (change starts with '{changeLower.Split(' ')[0]}') — exploring instead of editing", ct: ct);
                    var fp = Path.GetFullPath(Path.Combine(projectRoot, planFile.Replace('/', Path.DirectorySeparatorChar)));
                    var relPath = planFile.Replace('\\', '/');
                    if (System.IO.File.Exists(fp) && AgentUtilities.IsPathUnderRoot(fp, projectRoot))
                    {
                        if (emitSse)
                            await SendSse(Response, "step", new
                            {
                                index = stepIndex,
                                type = "read",
                                status = "done",
                                path = relPath,
                                description = item.Change,
                                planItemIndex = itemIdx
                            }, ct);
                        allResults.Add(new Dictionary<string, object?>
                        {
                            ["index"] = stepIndex,
                            ["type"] = "read",
                            ["status"] = "done",
                            ["path"] = relPath,
                            ["description"] = item.Change,
                            ["planItemIndex"] = itemIdx
                        });
                    }
                    else
                    {
                        if (emitSse)
                            await SendSse(Response, "step", new
                            {
                                index = stepIndex,
                                type = "read",
                                status = "error",
                                path = relPath,
                                error = "File not found",
                                planItemIndex = itemIdx
                            }, ct);
                        allResults.Add(new Dictionary<string, object?>
                        {
                            ["index"] = stepIndex,
                            ["type"] = "read",
                            ["status"] = "error",
                            ["path"] = relPath,
                            ["error"] = "File not found",
                            ["planItemIndex"] = itemIdx
                        });
                    }
                    await PersistStepStatusAsync(cardId, itemIdx, "done", emitSse, ct);
                    stepIndex++;
                    continue;
                }

                if (!alreadyDecoupled.Contains(item.Change ?? ""))
                {
                    alreadyDecoupled.Add(item.Change ?? "");
                }


                var stepSig = GetStepSignature(item.File, item.Change ?? "");
                if (stepSig != null && completedStepSignatures.Contains(stepSig))
                {
                    await EmitLog(emitSse, "info",
                        $"⏭ Step skipped — already accomplished by a prior step (signature: {stepSig})", ct: ct);
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
                            message = "Already accomplished by a prior step"
                        }, ct);
                    stepIndex++;
                    continue;
                }

                var prevCount = allResults.Count;
                try
                {
                    var prevSigCount = completedStepSignatures.Count;
                    stepIndex = await ResolveAndApplyEdit(
                        item, projectRoot, emitSse, ct, allResults, stepIndex,
                        prompt: prompt, plan: plan, planItemIndex: itemIdx,
                        cardId: cardId, attachedFiles: attachedFiles);

                    if (stepSig != null &&
                        (allResults.Count > prevCount &&
                         allResults[^1] is Dictionary<string, object?> lastResult &&
                         lastResult.TryGetValue("status", out var st) &&
                         st?.ToString() == "done"))
                    {
                        completedStepSignatures.Add(stepSig);
                    }
                }
                catch (StepFatalException ex)
                {
                    await EmitLog(emitSse, "error",
                        $"⛔ FATAL STEP FAILURE — halting plan execution. " +
                        $"Failed step: {ex.FailedFilePath} — {ex.FailedChangeDescription}",
                        new
                        {
                            error = ex.Message,
                            failedFile = ex.FailedFilePath,
                            failureContext = ex.FailureContext
                        }, ct: ct);

                    if (emitSse)
                    {
                        await SendSse(Response, "plan-halted", new
                        {
                            reason = "A plan step failed irrecoverably",
                            failedStep = ex.FailedFilePath,
                            failedChange = ex.FailedChangeDescription,
                            error = ex.Message,
                            remainingSteps = planItems.Count - itemIdx - 1
                        }, ct);
                    }

                    allResults.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "plan_halted",
                        ["status"] = "error",
                        ["reason"] = $"Fatal step failure: {ex.Message}",
                        ["failedFile"] = ex.FailedFilePath,
                        ["remainingSteps"] = planItems.Count - itemIdx - 1
                    });

                    return;
                }

                var stepSkipped = false;
                string? status = null;
                if (allResults.Count > prevCount &&
                    allResults[^1] is Dictionary<string, object?> lastDict2 &&
                    lastDict2.TryGetValue("status", out var st2))
                {
                    status = st2?.ToString();
                    if (status == "error")
                    {
                        await EmitLog(emitSse, "error",
                            $"✗ Step permanently failed for {planFile} — {lastDict2.GetValueOrDefault("error")}", ct: ct);
                    }
                    else if (status == "skipped" || status == "done")
                    {
                        stepSkipped = true;
                    }
                }

                if (status == "done" && planFile.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                {
                    var editNewStr = allResults.Count > prevCount &&
                        allResults[^1] is Dictionary<string, object?> lastEditDict
                        ? lastEditDict.GetValueOrDefault("newStringPreview")?.ToString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(editNewStr))
                    {
                        var funcMatches = Regex.Matches(editNewStr,
                            @"\(\w+\)=\""[^\""]*?(\w+)\s*\(");
                        if (funcMatches.Count > 0)
                        {
                            var htmlFullPath2 = Path.GetFullPath(
                                Path.Combine(projectRoot, planFile.Replace('/', Path.DirectorySeparatorChar)));
                            var baseDir = Path.GetDirectoryName(htmlFullPath2) ?? "";
                            var nameNoExt = Path.GetFileNameWithoutExtension(planFile);
                            var tsPath = Path.Combine(baseDir, nameNoExt + ".ts");
                            var tsContent = System.IO.File.Exists(tsPath)
                                ? await System.IO.File.ReadAllTextAsync(tsPath, Encoding.UTF8, ct)
                                : "";

                            var missingSteps = new List<PlanStep>();
                            var seenMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (System.Text.RegularExpressions.Match m in funcMatches)
                            {
                                var funcName = m.Groups[1].Value;
                                if (funcName is "ngOnInit" or "ngOnDestroy" or "ngAfterViewInit" or "ngOnChanges" or "ngDoCheck" or "ngAfterContentInit" or "ngAfterContentChecked" or "ngAfterViewChecked" or "toggle" or "open" or "close" or "preventDefault" or "stopPropagation" or "console")
                                { continue; }
                                if (!seenMethods.Add(funcName)) { continue; }
                                var foundInProject = false;
                                if (!string.IsNullOrWhiteSpace(tsContent))
                                {
                                    var methodRx = new Regex($@"\b{Regex.Escape(funcName)}\s*[=:(<]|\b(get|set)\s+{Regex.Escape(funcName)}\b");
                                    if (methodRx.IsMatch(tsContent))
                                        foundInProject = true;
                                }
                                if (!foundInProject)
                                {
                                    var (grepPath, _) = await GrepProjectForDefinitionAsync(
                                        projectRoot, funcName, planFile, ct);
                                    if (grepPath != null)
                                        foundInProject = true;
                                }
                                if (foundInProject)
                                {
                                    var argsText = "";
                                    try
                                    {
                                        var callStart = m.Index + m.Length;
                                        if (callStart > 0 && callStart < (editNewStr?.Length ?? 0))
                                        {
                                            var remaining = editNewStr!.Substring(callStart);
                                            var closeParen = remaining.IndexOf(')');
                                            if (closeParen >= 0)
                                                argsText = remaining.Substring(0, closeParen).Trim();
                                        }
                                    }
                                    catch { }
                                    if (!string.IsNullOrWhiteSpace(argsText) && !string.IsNullOrWhiteSpace(tsContent))
                                    {
                                        var argProps = Regex.Matches(argsText, @"\b[a-z]+[a-zA-Z0-9]*\b")
                                            .Cast<Match>().Select(x => x.Value)
                                            .Where(x => !Regex.IsMatch(x, @"^\d+$"))
                                            .Distinct().ToList();
                                        var methodBodyProps = Regex.Matches(tsContent, @"\bthis\.([a-zA-Z_]\w*)")
                                            .Cast<Match>().Select(x => x.Value)
                                            .Distinct().ToHashSet(StringComparer.OrdinalIgnoreCase);
                                        var mismatchedArgs = argProps
                                            .Where(a => !a.Contains("this.") &&
                                                !methodBodyProps.Any(b =>
                                                    b.EndsWith("." + a, StringComparison.OrdinalIgnoreCase)))
                                            .ToList();
                                        if (mismatchedArgs.Count > 0 && mismatchedArgs.Count >= argProps.Count / 2)
                                        {
                                            var newFuncName = funcName;
                                            var youtubeArg = mismatchedArgs
                                                .FirstOrDefault(a => a.IndexOf("youtube", StringComparison.OrdinalIgnoreCase) >= 0
                                                    || a.IndexOf("yt", StringComparison.OrdinalIgnoreCase) >= 0);
                                            if (youtubeArg != null)
                                            {
                                                var prefix = funcName.StartsWith("on", StringComparison.OrdinalIgnoreCase) ? "onYoutube" : "youtube";
                                                var suffix = funcName.Length > 2 && char.IsUpper(funcName[2]) ? funcName.Substring(2) : funcName;
                                                newFuncName = prefix + char.ToUpper(suffix[0]) + suffix.Substring(1);
                                                foundInProject = false;
                                                funcName = newFuncName;
                                            }
                                        }
                                    }
                                    if (foundInProject)
                                        continue;
                                }
                                if (planItems.Any(p => p.File != null && p.File.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) &&
                                    p.Change != null && p.Change.Contains(funcName, StringComparison.OrdinalIgnoreCase)))
                                    continue;

                                var relDir2 = Path.GetDirectoryName(planFile)?.Replace('\\', '/') ?? "";
                                var relTsPath2 = string.IsNullOrWhiteSpace(relDir2)
                                    ? nameNoExt + ".ts"
                                    : relDir2 + "/" + nameNoExt + ".ts";
                                missingSteps.Add(new PlanStep
                                {
                                    File = relTsPath2,
                                    Change = $"Implement the missing {funcName}() method referenced in {Path.GetFileName(planFile)}",
                                    Priority = item.Priority
                                });
                            }

                            if (missingSteps.Count > 0)
                            {
                                planItems.InsertRange(itemIdx + 1, missingSteps);
                                await EmitLog(emitSse, "info",
                                    $"Added {missingSteps.Count} step(s) for missing method(s): " +
                                    string.Join(", ", missingSteps.Select(s => s.Change)), ct: ct);
                                var planItemsJson2 = new JsonArray();
                                for (var pi = 0; pi < planItems.Count; pi++)
                                {
                                    planItemsJson2.Add(new JsonObject
                                    {
                                        ["index"] = pi,
                                        ["file"] = planItems[pi].File ?? "",
                                        ["change"] = planItems[pi].Change ?? "",
                                        ["priority"] = planItems[pi].Priority,
                                        ["line"] = planItems[pi].LineNumber,
                                        ["done"] = allResults.Any(r => r is Dictionary<string, object?> dict && dict.TryGetValue("planItemIndex", out var pii) && pii is int piiVal2 && piiVal2 == pi && dict.TryGetValue("status", out var st2b) && st2b is string stStr2b && stStr2b == "done")
                                    });
                                }
                                if (emitSse)
                                    await SendSse(Response, "plan", new
                                    {
                                        thinking = $"Added {missingSteps.Count} step(s) for missing method(s) referenced in HTML",
                                        summary = $"Added {missingSteps.Count} step(s)",
                                        items = planItemsJson2
                                    }, ct);
                                if (!string.IsNullOrWhiteSpace(cardId))
                                    await PersistBoardDataPlanAsync(cardId, planItems, emitSse, ct);
                            }
                        }
                    }
                }
                if (status is "done" or "modified")
                {
                    var lastEditResult = allResults.Count > prevCount
                        ? allResults[^1] as Dictionary<string, object?> : null;
                    var appliedNewStr = lastEditResult?.GetValueOrDefault("newStringPreview")?.ToString();

                    if (!string.IsNullOrWhiteSpace(appliedNewStr))
                    {
                        var currentFullPath = Path.GetFullPath(
                            Path.Combine(projectRoot, planFile.Replace('/', Path.DirectorySeparatorChar)));
                        string currentContent = "";
                        try
                        {
                            currentContent = await System.IO.File.ReadAllTextAsync(
                            currentFullPath, Encoding.UTF8, ct);
                        }
                        catch { }

                        var reflectedSteps = await ReflectOnAppliedEditAsync(
                            planFile, appliedNewStr, currentContent,
                            projectRoot, planItems, emitSse, ct);

                        if (reflectedSteps.Count > 0)
                        {

                            var completedSig = GetStepSignature(planFile, item.Change ?? "");
                            if (completedSig != null)
                            {
                                var before = reflectedSteps.Count;
                                reflectedSteps = reflectedSteps
                                    .Where(rs => GetStepSignature(rs.File ?? "", rs.Change ?? "") != completedSig)
                                    .ToList();
                                if (reflectedSteps.Count < before)
                                    await EmitLog(emitSse, "info",
                                        $"  ↪ Skipped {before - reflectedSteps.Count} reflected step(s) with same signature as completed step", ct: ct);
                            }

                            if (reflectedSteps.Count > 0)
                            {
                                await EmitLog(emitSse, "info",
                                    $"  ➕ Reflection added {reflectedSteps.Count} step(s): " +
                                    string.Join(" | ", reflectedSteps.Select(s => s.Change)), ct: ct);

                                planItems.InsertRange(itemIdx + 1, reflectedSteps);
                                await PersistBoardDataPlanAsync(cardId, planItems, emitSse, ct,
                                    summary: $"Reflection: +{reflectedSteps.Count} step(s)", score: 0,
                                    append: false);

                                if (emitSse)
                                    await SendSse(Response, "plan", new
                                    {
                                        thinking = $"Reflection after editing {planFile}",
                                        summary = $"Added {reflectedSteps.Count} follow-up step(s)",
                                        items = planItems.Select((p, i) => new
                                        {
                                            index = i,
                                            file = p.File,
                                            change = p.Change,
                                            priority = p.Priority,
                                            line = p.LineNumber,
                                            done = false
                                        }).ToList()
                                    }, ct);
                            }
                        }


                        if (AgentUtilities.IsRelativePath(planFile))
                        {
                            var cohesionIssues = await RunCohesionCheckAsync(
                                planFile, currentContent, projectRoot, emitSse, ct);
                            await PersistCohesionToCardAsync(
                                cardId, planFile, cohesionIssues, emitSse, ct);
                        }
                    }
                }


                if (status != "skipped")
                {
                    planItems = await TryReplanAfterStep(prompt, allResults, plan,
                        steeringContext, projectRoot, emitSse, ct, planItems, itemIdx,
                        stepSkipped, allResults.Count > prevCount, attachedFiles, replanBudget, cardId: cardId);
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

    private static (bool approved, string reason, int score) VerifyEdit(
        string oldString, string newString, string oldContent, string newContent, bool fromFormatC = false)
    {
        if (oldContent == newContent) return (false, "Edit produced no change", 3);


        if (!string.IsNullOrWhiteSpace(oldContent) && string.IsNullOrWhiteSpace(newContent))
            return (false, "Edit would produce empty file — rejected to prevent data loss", 1);

        if (oldContent.Length > 200 && newContent.Length > 0 &&
            newContent.Length < oldContent.Length * 0.10)
            return (false, $"Edit would reduce file by {100 - (int)(newContent.Length * 100.0 / oldContent.Length)}% — suspicious content loss", 1);

        var normOld = AgentUtilities.NormalizeLineEndings(oldString);
        var normNew = AgentUtilities.NormalizeLineEndings(newString);
        var normOldContent = AgentUtilities.NormalizeLineEndings(oldContent);
        var normNewContent = AgentUtilities.NormalizeLineEndings(newContent);

        if (!string.IsNullOrEmpty(normNew) &&
            !normNewContent.Contains(normNew, StringComparison.Ordinal))
        {
            var strippedNew = AgentUtilities.StripLineLeadingWhitespace(normNew);
            var strippedContent = AgentUtilities.StripLineLeadingWhitespace(normNewContent);
            var trimmedNew = string.Join("\n", strippedNew.Split('\n').Select(l => l.TrimEnd()));
            var trimmedContent = string.Join("\n", strippedContent.Split('\n').Select(l => l.TrimEnd()));
            if (!trimmedContent.Contains(trimmedNew, StringComparison.Ordinal))
                return (false, "newString not found after replacement", 4);
        }

        var hallucinatedPropertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "EventTitle", "EventDescription",
            "UserName", "UserEmail",
            "Attendees", "Organizer",
        };
        var newlyIntroducedProps = hallucinatedPropertyNames
            .Where(p => !Regex.IsMatch(normOldContent, $@"\b{Regex.Escape(p)}\b", RegexOptions.IgnoreCase))
            .Where(p => Regex.IsMatch(normNewContent, $@"\.{Regex.Escape(p)}\b", RegexOptions.IgnoreCase))
            .ToList();
        if (newlyIntroducedProps.Count > 0)
        {
            return (false,
                $"Newly introduced property(s) [{string.Join(", ", newlyIntroducedProps)}] not found in any " +
                $"model, SQL column, or comment in the original file. These are common LLM hallucination names. " +
                $"Cross-reference the type definition in AUTO-ENRICHED CONTEXT and use the EXACT property names " +
                $"shown there (e.g. CalendarEntry uses 'Type' and 'Note', not 'Title' and 'Description').", 2);
        }

        if (!string.IsNullOrEmpty(normOld) && normOld.Length >= 10 && !normNew.Contains(normOld))
        {

            var strippedOld = AgentUtilities.StripLineLeadingWhitespace(normOld);
            var strippedOldContent = AgentUtilities.StripLineLeadingWhitespace(normOldContent);
            var strippedNewContent = AgentUtilities.StripLineLeadingWhitespace(normNewContent);

            var oldCount = 0; var newCount = 0; var pos = 0;
            while ((pos = strippedOldContent.IndexOf(strippedOld, pos, StringComparison.Ordinal)) >= 0)
            { oldCount++; pos += strippedOld.Length; }
            pos = 0;
            while ((pos = strippedNewContent.IndexOf(strippedOld, pos, StringComparison.Ordinal)) >= 0)
            { newCount++; pos += strippedOld.Length; }

            if (oldCount > 0 && newCount >= oldCount)
                return (false, "oldString still fully present after replacement — edit hit wrong location", 4);
        }

        if (string.Equals(normOld.Trim(), normNew.Trim(), StringComparison.Ordinal))
            return (false, "oldString and newString are identical after normalization", 3);


        if (!string.IsNullOrWhiteSpace(normNew))
        {
            var garbageTokens = new[] { "</s>", "<|endoftext|>", "<|im_end|>", "|im_end|", "<|endofprompt|>" };
            foreach (var tok in garbageTokens)
            {
                if (normNew.Contains(tok, StringComparison.OrdinalIgnoreCase))
                    return (false, $"newString contains leaked LLM token '{tok}' — edit is corrupted", 1);
            }
        }

        if (!fromFormatC)
        {
            var newRoutes = Regex.Matches(newContent,
                @"\[Http(?:Get|Post|Put|Delete|Patch)\(""([^""]+)""")
                .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            var oldRoutes = Regex.Matches(oldContent,
                @"\[Http(?:Get|Post|Put|Delete|Patch)\(""([^""]+)""")
                .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            var introducedDups = newRoutes
                .GroupBy(r => r, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .Where(r => !oldRoutes.Contains(r, StringComparer.OrdinalIgnoreCase) ||
                             oldRoutes.Count(o => o.Equals(r, StringComparison.OrdinalIgnoreCase)) < 2)
                .ToList();
            if (introducedDups.Count > 0)
                return (false,
                    $"Edit introduces duplicate route(s): {string.Join(", ", introducedDups)}. " +
                    "LLM likely copied an entire existing method instead of inserting new code. " +
                    "Use a precise insertion anchor or insertAfter instead.", 1);
        }

        var emptyDeclPattern =
             @"(?:public|private|internal|protected)?\s*(?:class|struct|interface|record)\s+\w+\s*\{\s*(?:\/\*[\s\S]*?\*\/)?\s*\}|" +
             @"(?:public|private|internal|protected)?\s*(?:class|struct|interface|record)\s+\w+\s*\n\s*\{\s*\n\s*\}";
        var emptyDeclsNew = Regex.Matches(newContent, emptyDeclPattern).Cast<Match>().Select(m => m.Value.Trim()).ToHashSet(StringComparer.Ordinal);
        var emptyDeclsOld = Regex.Matches(oldContent, emptyDeclPattern).Cast<Match>().Select(m => m.Value.Trim()).ToHashSet(StringComparer.Ordinal);
        var introducedEmpty = emptyDeclsNew.Except(emptyDeclsOld).ToList();
        if (introducedEmpty.Count > 0)
        {
            return (false,
                $"Edit introduces NEW empty type(s): {string.Join(", ", introducedEmpty)}. These types already exist in the project — " +
                "find their definition and use the existing type instead of creating a stub.", 1);
        }

        var specificSqlPatterns = new[]
        {
            @"\bINTERVAL\d",
            @"\bDAY\d", @"\bHOUR\d", @"\bMINUTE\d", @"\bSECOND\d",
            @"\bLIMIT\d",
            @"\bOFFSET\d",
        };
        foreach (var line in newContent.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 10) continue;

            if (!Regex.IsMatch(trimmed, @"\b(SELECT|FROM|WHERE|AND|INSERT|UPDATE|DELETE|JOIN|INTERVAL|DATE_ADD|LIMIT)\b", RegexOptions.IgnoreCase))
                continue;


            foreach (var pattern in specificSqlPatterns)
            {
                var match = Regex.Match(trimmed, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var ctx = match.Value;
                    return (false,
                        $"SQL whitespace collapsed: '{ctx}' — likely missing a space. " +
                        "Copy the exact whitespace from the original SQL. 'INTERVAL 15' is correct, 'INTERVAL15' is not.", 2);
                }
            }
        }

        var fixedOld = AgentUtilities.AutoFixSqlWhitespace(normOldContent);
        var fixedNew = AgentUtilities.AutoFixSqlWhitespace(normNewContent);
        var oldTables = AgentUtilities.ExtractSqlTableNames(fixedOld);
        var newTables = AgentUtilities.ExtractSqlTableNames(fixedNew);
        if (oldTables.Count > 0 && newTables.Count > 0)
        {
            var missingTables = oldTables.Where(t => !newTables.Contains(t)).ToList();
            if (missingTables.Count > 0)
            {
                var returnAnchor = AgentUtilities.FindLastReturnLine(normOld);
                var anchorHint = returnAnchor != null
                    ? $" Anchor on the return statement: oldString=\"{returnAnchor.Trim()}\""
                    : "";
                return (false,
                    $"Edit replaces existing SQL table(s) [{string.Join(", ", missingTables.Take(3))}] with different tables. " +
                    "Preserve the original query structure; only add the required logic." + anchorHint, 1);
            }
        }

        if (AgentUtilities.IsAngularTemplate(newContent))
        {
            var bannedInAngular = new[] { "Math.min(", "Math.max(", "Math.floor(", "Math.ceil(",
                "Math.round(", "Math.random(", "parseInt(", "parseFloat(", "JSON.parse", "JSON.stringify" };
            foreach (var banned in bannedInAngular)
            {
                if (newContent.Contains(banned, StringComparison.OrdinalIgnoreCase))
                {
                    var match = Regex.Match(newContent, $@"\b{Regex.Escape(banned[..^1])}\s*\(", RegexOptions.IgnoreCase);
                    if (match.Success)
                        return (false,
                            $"Angular template uses `{match.Value}()` which is not available in Angular templates. " +
                            "Only component properties and methods are accessible in template expressions. " +
                            $"Move this logic to the component's .ts file.", 2);
                }
            }
        }

        return (true, "Programmatic check passed", 10);
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
        var parsed = AgentUtilities.ParsePlan(cleaned);
        return parsed?.Plan?.Count > 0 ? parsed.Plan : null;
    }

    private async Task SaveEditWithUndoAsync(
        string fullPath, string newContent, string relPath,
        string projectRoot, string preEditContent, CancellationToken ct)
    {
        try
        {
            var undoDir = Path.Combine(projectRoot, "data", "undo");
            Directory.CreateDirectory(undoDir);
            var safeName = relPath.Replace('/', '_').Replace('\\', '_');
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-ffff");
            var diffPath = Path.Combine(undoDir, $"{safeName}.{timestamp}.diff");


            var gitDir = Path.Combine(projectRoot, ".git");
            if (Directory.Exists(gitDir))
            {
                var proc = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = $"diff --no-color \"{relPath.Replace('/', Path.DirectorySeparatorChar)}\"",
                        WorkingDirectory = projectRoot,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                var diffOutput = await proc.StandardOutput.ReadToEndAsync();
                var diffError = await proc.StandardError.ReadToEndAsync();
                proc.WaitForExit(5000);

                if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(diffOutput))
                {

                    var undoHeader = $"; Undo for {relPath} @ {DateTime.UtcNow:O}\n" +
                                     $"; Restore with: git apply --reverse \"{diffPath}\"\n" +
                                     $"; Or use: git checkout -- \"{relPath.Replace('/', Path.DirectorySeparatorChar)}\"\n";
                    await System.IO.File.WriteAllTextAsync(
                        diffPath, undoHeader + diffOutput, Encoding.UTF8, ct);
                }
            }
        }
        catch
        {

        }

        await System.IO.File.WriteAllTextAsync(fullPath, newContent, Encoding.UTF8, ct);
    }

    private async Task<List<PlanStep>> PruneIrrelevantPlanStepsAsync(List<PlanStep> steps, string projectRoot, CancellationToken ct)
    {
        if (steps == null || steps.Count == 0) return steps ?? [];

        var pruned = new List<PlanStep>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.File) || string.IsNullOrWhiteSpace(step.Change))
                continue;

            var changeLower = step.Change.ToLowerInvariant().Trim();


            if (changeLower.StartsWith("already done") || changeLower.StartsWith("no change") ||
                changeLower.StartsWith("skip") || changeLower.StartsWith("none") ||
                changeLower == "done" || changeLower == "n/a")
                continue;


            var removeMatch = Regex.Match(changeLower, @"remove\s+(?:the\s+)?(?:existing\s+)?(\w+)", RegexOptions.IgnoreCase);
            if (removeMatch.Success)
            {
                var target = $"{step.File}|{removeMatch.Groups[1].Value.ToLowerInvariant()}";
                removedTargets.Add(target);
            }

            var addMatch = Regex.Match(changeLower, @"(?:add|insert|create)\s+(?:a\s+)?(?:new\s+)?(\w+)", RegexOptions.IgnoreCase);
            if (addMatch.Success)
            {
                var target = $"{step.File}|{addMatch.Groups[1].Value.ToLowerInvariant()}";
                if (removedTargets.Contains(target))
                {
                    await EmitLog(false, "warn", $"Prune: removing add-after-remove loop for '{target}'", ct: ct);
                    continue;
                }
            }


            var normChange = NormalizeChangeForDedup(step.Change);
            var key = $"{step.File}|{normChange}";
            if (!seenKeys.Add(key))
            {
                await EmitLog(false, "warn", $"Prune: duplicate step '{step.Change}'", ct: ct);
                continue;
            }


            var isCreation = changeLower.Contains("create file") || changeLower.Contains("new file") ||
                            step.File.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                            changeLower.StartsWith("add ") || changeLower.StartsWith("create ");
            var fullPath = Path.GetFullPath(
                Path.Combine(projectRoot, step.File.Replace('/', Path.DirectorySeparatorChar)));
            var fileExists = System.IO.File.Exists(fullPath);




            var isModify = changeLower.StartsWith("modify ") || changeLower.StartsWith("change ") ||
                           changeLower.StartsWith("update ") || changeLower.StartsWith("replace ");

            if (isModify && !fileExists)
            {
                await EmitLog(false, "warn", $"Prune: modify step targets non-existent file '{step.File}'", ct: ct);
                continue;
            }


            if (fileExists)
            {
                var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                var contentLower = content.ToLowerInvariant();


                var endpointMatch = Regex.Match(step.Change ?? "",
                    @"add\s+.*(?:httppost|httpget|httpput|httpdelete)\(""(.*?)""\)",
                    RegexOptions.IgnoreCase);
                if (endpointMatch.Success)
                {
                    var route = endpointMatch.Groups[1].Value.ToLowerInvariant();
                    if (contentLower.Contains($"httppost(\"{route}\"") ||
                        contentLower.Contains($"httpget(\"{route}\"") ||
                        contentLower.Contains($"httpput(\"{route}\"") ||
                        contentLower.Contains($"httpdelete(\"{route}\""))
                    {
                        await EmitLog(false, "warn", $"Prune: endpoint '{route}' already exists in '{step.File}'", ct: ct);
                        continue;
                    }
                }


                var methodMatch = Regex.Match(step.Change ?? @"", @"add\s+(?:method\s+)?(\w+)(?:\s*method)?\s*(?:endpoint|method|function)?", RegexOptions.IgnoreCase);
                if (methodMatch.Success)
                {
                    var methodName = methodMatch.Groups[1].Value;
                    if (Regex.IsMatch(content, $@"\b{Regex.Escape(methodName)}\s*\("))
                    {
                        await EmitLog(false, "warn", $"Prune: method '{methodName}' already exists in '{step.File}'", ct: ct);
                        continue;
                    }
                }


                var propMatch = Regex.Match(step.Change ?? @"", @"add\s+(?:property\s+)?(\w+)(?:\s*property)?", RegexOptions.IgnoreCase);
                if (propMatch.Success)
                {
                    var propName = propMatch.Groups[1].Value;
                    if (Regex.IsMatch(content, $@"\b{Regex.Escape(propName)}\b\s*(?::\s*\w+|;\s*$|\s*{{)"))
                    {
                        await EmitLog(false, "warn", $"Prune: property '{propName}' already exists in '{step.File}'", ct: ct);
                        continue;
                    }
                }


                if (step.LineNumber > 0)
                {
                    var locKey = $"{step.File}|L{step.LineNumber}";
                    if (!seenLocations.Add(locKey))
                    {
                        await EmitLog(false, "warn", $"Prune: another step already targets line {step.LineNumber} in '{step.File}'", ct: ct);
                        continue;
                    }
                }
            }

            pruned.Add(step);
        }

        return pruned;
    }




    private async Task<bool> VerifyCompletedFromStepTruthAsync(
        List<object> allSteps, string projectRoot, CancellationToken ct)
    {
        var doneEdits = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.TryGetValue("type", out var t) && t?.ToString() == "edit" &&
                        r.TryGetValue("status", out var st) && st?.ToString() == "done" &&
                        r.TryGetValue("editAction", out var a) && a?.ToString() == "modified")
            .ToList();

        if (doneEdits.Count == 0) return false;

        var fileCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var edit in doneEdits)
        {
            var relPath = edit.GetValueOrDefault("path")?.ToString();
            var oldPreview = edit.GetValueOrDefault("oldStringPreview")?.ToString();
            var newPreview = edit.GetValueOrDefault("newStringPreview")?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(relPath) || string.IsNullOrWhiteSpace(oldPreview)) continue;
            if (!string.IsNullOrWhiteSpace(newPreview)) continue;

            if (!fileCache.TryGetValue(relPath, out var content))
            {
                var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!System.IO.File.Exists(fullPath)) continue;
                content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                fileCache[relPath] = content;
            }

            var normContent = AgentUtilities.NormalizeLineEndings(content);
            var normOld = AgentUtilities.NormalizeLineEndings(oldPreview);
            if (normContent.Contains(normOld, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

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
        baseInstructions.AppendLine("You are a senior terminal automation agent. You have full terminal access and must complete the user's task end-to-end.");
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
        baseInstructions.AppendLine("Inspect before acting: for repository questions use fast file commands first. Prefer `rg --files` to enumerate files, `rg -n \"pattern\" <path>` to search text, and `Get-Content -TotalCount/-Tail` for bounded reads. If `rg` is unavailable, use PowerShell equivalents.");
        baseInstructions.AppendLine("Keep outputs small and useful. Limit broad searches, exclude bin/obj/node_modules/.git/dist, and save large raw outputs to a project temp file instead of dumping them into the conversation.");
        baseInstructions.AppendLine("After every command, decide what new fact was learned and what exact next step follows. Do not repeat failed commands without changing the hypothesis or command.");
        baseInstructions.AppendLine("For well-known REST APIs (pokeapi.co, jsonplaceholder, github api, etc.) use Invoke-RestMethod/curl via cmd — NOT web_search. web_search is only for finding URLs or info you don't already know.");
        baseInstructions.AppendLine("BEFORE planning the first step, assess the full task end-to-end. What data do you need? What files will be created? What merge/transform/verification steps are needed? Plan the smallest complete chain, usually 1-4 steps.");
        baseInstructions.AppendLine("KEEP THE ORIGINAL TASK AS YOUR NORTH STAR. After each step, check: does this complete the user's request yet? If the planned steps do not add up to finishing the task, add the remaining steps. If your plan covers the full task, execute the steps — do NOT keep planning new steps.");
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

        var stepIndex = 0; string? summary = null;
        var usedSearchQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var planSteps = new List<PlanStep>();
        var completedPlanSteps = new HashSet<int>();
        var totalPlanSteps = 0;
        var consecutiveErrors = 0;

        var conversation = new StringBuilder();
        conversation.Append(baseInstructions);
        conversation.AppendLine("\nPlan the smallest complete chain of remaining steps. Do NOT repeat steps already in the plan. Output:");
        conversation.AppendLine("  {\"plan\": [{\"file\": \"<output path or tool>\", \"change\": \"what to do and how you will verify it\"}]}  # add needed new steps");
        conversation.AppendLine("  {\"cmd\": \"...\"} / {\"web_fetch\": \"...\"} / {\"web_search\": \"...\"}  # execute directly");
        conversation.AppendLine("  {\"step\": N}  # explicitly mark step N done (if current approach failed but you want a different one)");
        conversation.AppendLine("  {\"done\": true, \"summary\": \"...\"}  # finish");
        conversation.AppendLine("After each action, verify if the step\'s objective was met using concrete output, file existence, or a bounded read. If a step errors, change approach or mark it done before trying a different route.");
        conversation.AppendLine("IMPORTANT: Check the PLAN section above before adding new steps. If a step is already in the plan, DO NOT add it again.");

        for (var i = 0; i < MAX_COMMAND_ITERATIONS; i++)
        {
            ct.ThrowIfCancellationRequested();

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

            if (jsonToParse == null) { conversation.AppendLine("Could not parse JSON."); continue; }

            using var doc = JsonDocument.Parse(jsonToParse, jsonOpts);
            var root = doc.RootElement;

            if (root.TryGetProperty("plan", out var pArr) && pArr.ValueKind == JsonValueKind.Array && pArr.GetArrayLength() > 0)
            {
                var newSteps = new List<PlanStep>();
                foreach (var item in pArr.EnumerateArray())
                    newSteps.Add(new PlanStep
                    {
                        File = item.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "",
                        Change = item.TryGetProperty("change", out var c) ? c.GetString() ?? "" : "",
                        LineNumber = item.TryGetProperty("line", out var ln) ? ln.GetInt32() : 0
                    });
                var deduped = newSteps.Where(ns =>
                    !planSteps.Any(ps =>
                        string.Equals(ps.File, ns.File, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals((ps.Change ?? "").Trim(), (ns.Change ?? "").Trim(), StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (deduped.Count == 0)
                {
                    if (totalPlanSteps > 0 && completedPlanSteps.Count >= totalPlanSteps)
                        conversation.AppendLine("All plan steps are already completed. If the task is done, respond with: {\"done\": true, \"summary\": \"...\"}");
                    else
                        conversation.AppendLine("Step(s) already in plan — not added. Execute existing steps or plan something new.");
                    continue;
                }
                planSteps.AddRange(deduped);
                totalPlanSteps = planSteps.Count;
                await EmitLog(emitSse, "info", $"Plan: +{deduped.Count} step(s) -- {string.Join(", ", deduped.Select(p => p.File))}", ct: ct);
                if (emitSse)
                    await SendSse(Response, "plan", new
                    {
                        thinking = "Planned steps",
                        summary = string.Join(" -> ", planSteps.Select(p => p.Change)),
                        items = planSteps.Select(p => new { file = p.File, change = p.Change, priority = 1, line = p.LineNumber }).ToList()
                    }, ct);
                conversation.AppendLine($"\n### PLAN UPDATED ({totalPlanSteps} total steps) ###");
                for (var pi = 0; pi < planSteps.Count; pi++)
                    conversation.AppendLine($"  Step {pi + 1}: [{planSteps[pi].File}] {planSteps[pi].Change}");
                conversation.AppendLine("### END PLAN ###");


                for (var pi = 0; pi < planSteps.Count; pi++)
                {
                    if (completedPlanSteps.Contains(pi)) continue;
                    var step = planSteps[pi];
                    var changeLower = (step.Change ?? "").Trim().ToLowerInvariant();


                    bool isVerification = changeLower.StartsWith("verify") ||
                        changeLower.StartsWith("check") ||
                        changeLower.StartsWith("test that") ||
                        changeLower.StartsWith("validate") ||
                        changeLower.StartsWith("confirm") ||
                        changeLower.StartsWith("ensure");

                    var translatePrompt = $"You are running on {shellName} ({Environment.OSVersion}).\nThe working directory (project root) is: {projectRoot}\nALL files and folders must be created INSIDE this working directory — translate desktop paths to this directory.\n\nTranslate this task step into a SINGLE terminal command. Output ONLY the command, no explanations, no markdown:\n\nStep {pi + 1}: [{step.File}] {step.Change}";
                    var (cmdRaw, _, _) = await CallLlmRaw(
                        "You are a terminal command translator. Output only the command, no markdown, no explanation.",
                        translatePrompt, ct, TimeSpan.FromSeconds(20));
                    if (string.IsNullOrWhiteSpace(cmdRaw)) continue;
                    var cmdClean = cmdRaw.Trim();
                    if (cmdClean.StartsWith("```")) cmdClean = cmdClean.Split('\n').LastOrDefault()?.Replace("```", "").Trim() ?? cmdClean;

                    var beforeLen = _terminal.ReadAll().Length;
                    await _terminal.SendCommandAsync(cmdClean, projectRoot);
                    var marker = "__DONE_" + Guid.NewGuid().ToString("N") + "__";
                    await _terminal.WriteStdinAsync("echo '" + marker + "'");
                    var timeout2 = DateTime.UtcNow.AddMinutes(5);
                    while (!ct.IsCancellationRequested && DateTime.UtcNow < timeout2)
                    { await Task.Delay(500); if (_terminal.ReadAll().Contains(marker)) break; }
                    var fullOut = _terminal.ReadAll();
                    var freshOut = beforeLen < fullOut.Length ? fullOut[beforeLen..] : "";
                    freshOut = string.Join("\n", (freshOut ?? "").Split('\n').Where(l => !l.Contains("__DONE_")));
                    if (string.IsNullOrWhiteSpace(freshOut)) freshOut = "(ok)";

                    var isError = !string.IsNullOrWhiteSpace(freshOut) &&
                        Regex.IsMatch(freshOut.ToLowerInvariant(),
                            @"not recognized|not found|cannot find|terminate|error|exception|failed|access denied|permission denied");

                    if (isVerification)
                    {

                        conversation.AppendLine($"→ Verified step {pi + 1}: {cmdClean}");
                        conversation.AppendLine($"  Result: {AgentUtilities.Truncate(freshOut, 300)}");

                        if (!isError) completedPlanSteps.Add(pi);
                        else
                        {
                            conversation.AppendLine($"  Verification found issues — step {pi + 1} may need attention");
                            completedPlanSteps.Add(pi);
                        }
                        var vResult = new Dictionary<string, object?>
                        {
                            ["index"] = stepIndex++,
                            ["type"] = "command",
                            ["command"] = cmdClean,
                            ["status"] = isError ? "warning" : "done",
                            ["output"] = freshOut
                        };
                        steps.Add(vResult);
                        if (emitSse) await SendSse(Response, "step", vResult, ct);
                        continue;
                    }

                    if (isError)
                    {

                        var errorText = freshOut.ToLowerInvariant();

                        bool isBenign = errorText.Contains("already exists")
                            || (errorText.Contains("access denied") && errorText.Contains("already exists"));

                        if (isBenign)
                        {

                            completedPlanSteps.Add(pi);
                            var benignResult = new Dictionary<string, object?>
                            {
                                ["index"] = stepIndex++,
                                ["type"] = "plan_step",
                                ["planItemIndex"] = pi,
                                ["command"] = cmdClean,
                                ["status"] = "done",
                                ["output"] = freshOut
                            };
                            steps.Add(benignResult);
                            if (emitSse) await SendSse(Response, "step", benignResult, ct);
                            await PersistBoardDataPlanStepAsync(cardId, pi, emitSse, ct);
                            conversation.AppendLine($"→ Step {pi + 1} OK (already existed): {cmdClean}");
                            conversation.AppendLine($"  Output: {AgentUtilities.Truncate(freshOut, 300)}");
                        }
                        else
                        {

                            conversation.AppendLine($"→ Step {pi + 1} FAILED: {cmdClean}");
                            conversation.AppendLine($"  Error: {AgentUtilities.Truncate(freshOut, 500)}");
                            conversation.AppendLine("  The step above failed. If you know a different command or approach, output a new plan step to recover. Otherwise mark it done with {\"step\": " + (pi + 1) + "} and move on.");
                            var errResult = new Dictionary<string, object?>
                            {
                                ["index"] = stepIndex++,
                                ["type"] = "plan_step",
                                ["planItemIndex"] = pi,
                                ["command"] = cmdClean,
                                ["status"] = "error",
                                ["output"] = freshOut
                            };
                            steps.Add(errResult);
                            if (emitSse) await SendSse(Response, "step", errResult, ct);

                        }
                        continue;
                    }

                    completedPlanSteps.Add(pi);
                    var result = new Dictionary<string, object?>
                    {
                        ["index"] = stepIndex++,
                        ["type"] = "plan_step",
                        ["planItemIndex"] = pi,
                        ["command"] = cmdClean,
                        ["status"] = "done",
                        ["output"] = freshOut
                    };
                    steps.Add(result);
                    if (emitSse) await SendSse(Response, "step", result, ct);
                    await PersistBoardDataPlanStepAsync(cardId, pi, emitSse, ct);
                    conversation.AppendLine($"→ Auto-executed step {pi + 1}: {cmdClean}");
                    conversation.AppendLine($"  Output: {AgentUtilities.Truncate(freshOut, 500)}");
                }


                if (completedPlanSteps.Count >= totalPlanSteps)
                {
                    summary = "All plan steps completed";
                    break;
                }
                continue;
            }

            if (root.TryGetProperty("step", out var stepEl) && stepEl.ValueKind == JsonValueKind.Number)
            {
                var stepNum = stepEl.GetInt32();
                if (stepNum >= 1 && stepNum <= totalPlanSteps && completedPlanSteps.Add(stepNum - 1))
                {
                    conversation.AppendLine("-> Step " + stepNum + " marked done.");
                    if (emitSse)
                        await SendSse(Response, "step", new { index = stepIndex, type = "plan_step", planItemIndex = stepNum - 1, status = "done" }, ct);
                    await PersistBoardDataPlanStepAsync(cardId, stepNum - 1, emitSse, ct);
                }
                continue;
            }

            if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
            {
                summary = root.TryGetProperty("summary", out var s) ? s.GetString() : "Task complete";
                break;
            }

            if (root.TryGetProperty("cmd", out var cmdEl) || root.TryGetProperty("command", out cmdEl))
            {
                var cmd = cmdEl.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(cmd)) { conversation.AppendLine("Empty command - try again."); continue; }
                if ((cmd.Contains('\n') || cmd.Contains('\r')) && !cmd.Contains("@\""))
                {
                    var san = cmd.Replace("\r\n", "; ").Replace("\r", "; ").Replace("\n", "; ");
                    await EmitLog(emitSse, "info", "newlines in cmd - joined", ct: ct);
                    cmd = san;
                }
                var cmdLower = cmd.TrimStart().ToLowerInvariant();
                if (cmdLower.StartsWith("mkdir") && Regex.IsMatch(cmd, @"\.\w{2,4}[""'\s]|\.\w{2,4}$"))
                { conversation.AppendLine("REJECTED: mkdir creates DIRECTORIES. Use: New-Item -ItemType File -Path \"<path>\" -Force"); continue; }
                if (cmdLower == "cd" || cmdLower.StartsWith("cd ") || cmdLower.Contains("set-location"))
                { conversation.AppendLine("REJECTED: cd/Set-Location not supported. Use absolute paths."); continue; }

                var beforeLen = _terminal.ReadAll().Length;
                await _terminal.SendCommandAsync(cmd, projectRoot);
                var marker = "__DONE_" + Guid.NewGuid().ToString("N") + "__";
                await _terminal.WriteStdinAsync("echo '" + marker + "'");
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
                conversation.AppendLine("Command [" + (i + 1) + "]: " + cmd);
                conversation.AppendLine(isError ? "Error:" : "Output:");
                conversation.AppendLine(freshOut);
                if (isError && freshOut.Contains("ConvertFrom-Json"))
                    conversation.AppendLine("Hint: Invoke-RestMethod already parses JSON - remove ConvertFrom-Json from the pipeline.");
                if (isError && freshOut.Contains("already exists"))
                    conversation.AppendLine("Hint: The file already exists. Use -Force flag or a different path.");
                if (isError) consecutiveErrors++;
                else
                {
                    if (totalPlanSteps > 0 && completedPlanSteps.Count < totalPlanSteps)
                    {
                        var advStep = completedPlanSteps.Count;
                        if (completedPlanSteps.Add(advStep))
                        {
                            if (emitSse)
                                await SendSse(Response, "step", new { index = stepIndex, type = "plan_step", planItemIndex = advStep, status = "done" }, ct);
                            await PersistBoardDataPlanStepAsync(cardId, advStep, emitSse, ct);
                        }
                    }
                    consecutiveErrors = 0;
                }
                continue;
            }

            if (root.TryGetProperty("web_search", out var searchEl))
            {
                var query = searchEl.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(query)) { conversation.AppendLine("Empty query."); continue; }
                if (!usedSearchQueries.Add(query)) { conversation.AppendLine("Already searched for \"" + query + "\". Use the results above."); continue; }
                var (searchOut, _) = await WebSearchAsync(query, ct);
                var wr = new Dictionary<string, object?> { ["index"] = stepIndex++, ["type"] = "web_search", ["query"] = query, ["status"] = "done", ["output"] = searchOut };
                steps.Add(wr); if (emitSse) await SendSse(Response, "step", wr, ct);
                conversation.AppendLine("Web search [" + (i + 1) + "]: " + query + "\nResults:\n" + searchOut);
                continue;
            }

            if (root.TryGetProperty("web_fetch", out var fetchEl))
            {
                var url = fetchEl.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(url)) { conversation.AppendLine("Empty URL."); continue; }
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != "http" && uri.Scheme != "https"))
                {
                    conversation.AppendLine("Invalid URL: \"" + url + "\" - must be http/https. Provide a real URL.");
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
                    conversation.AppendLine("Fetch error [" + (i + 1) + "]: " + url + "\n" + fetchOut);
                    consecutiveErrors++;
                }
                else
                {
                    conversation.AppendLine("Fetch [" + (i + 1) + "]: " + url + "\n" + fetchOut);
                }
                continue;
            }

            if (root.TryGetProperty("message", out var msgEl) || root.TryGetProperty("result", out msgEl))
            {
                var msgText = msgEl.GetString() ?? "";
                var mr = new Dictionary<string, object?> { ["index"] = stepIndex++, ["type"] = "message", ["output"] = msgText };
                steps.Add(mr); if (emitSse) await SendSse(Response, "step", mr, ct);
                conversation.AppendLine("Message: " + msgText);
                continue;
            }

            conversation.AppendLine("Unrecognized JSON - use cmd, web_search, web_fetch, message, done, or plan.");
        }

        summary ??= "Command execution completed (" + steps.Count + " steps)";
        await EmitLog(emitSse, "info", summary, steps, ct: ct);

        steps.Add(new Dictionary<string, object?> { ["type"] = "done_signal", ["status"] = "done" });

        var agentPlan = planSteps != null && planSteps.Count > 0
            ? new AgentPlan { Plan = planSteps, Summary = summary, Thinking = "Command execution plan" }
            : null;
        return (steps, agentPlan);
    }

    [HttpPost("execute")]
    public async Task<IActionResult> Execute([FromBody] AgentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt)) return BadRequest("Prompt is required");
        var projectRoot = AgentUtilities.GetProjectRoot(req.Project, _config, _env);
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
        var projectRoot = AgentUtilities.GetProjectRoot(req.Project, _config, _env);
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
            var projectRoot = AgentUtilities.GetProjectRoot(req.Project, _config, _env);
            await SendSse(Response, "phase", new { phase = "start", projectRoot });
            await EmitLog(true, "info", "Agent started", new { projectRoot, task = req.Prompt });


            AgentPlan? existingPlan = null;
            HashSet<int>? completedIndices = null;
            bool isBenchmark = req.IsBenchmark;
            if (!string.IsNullOrWhiteSpace(req.CardId))
            {
                var (loadedPlan, loadedCompleted, loadedBenchmark) = await LoadPlanFromBoardDataAsync(req.CardId);
                existingPlan = loadedPlan;
                completedIndices = loadedCompleted;

                if (loadedBenchmark) isBenchmark = true;
            }


            if (isBenchmark)
            {
                projectRoot = !string.IsNullOrWhiteSpace(req.BenchmarkProjectRoot)
                    ? Path.GetFullPath(req.BenchmarkProjectRoot)
                    : AgentUtilities.GetBenchmarkSandboxPath();
                await EmitLog(true, "info", "Benchmark sandbox active", new { sandbox = projectRoot });
                await SendSse(Response, "phase", new { phase = "sandbox", sandbox = projectRoot }, ct: Response.HttpContext.RequestAborted);
            }

            var (allSteps, plan, complete) = await Orchestrate(
                req.Prompt, projectRoot, emitSse: true,
                ct: Response.HttpContext.RequestAborted,
                attachedFiles: req.Files?.Count > 0 ? req.Files : null,
                steeringContext: req.SteeringContext,
                existingPlan: existingPlan,
                completedStepIndices: completedIndices,
                cardId: req.CardId,
                createTests: req.CreateTests,
                buildCommands: req.BuildCommands);

            var filesEdited = ExtractFilesEdited(allSteps);
            var editsApplied = AgentUtilities.HasSuccessfulEdits(allSteps);


            if (isBenchmark)
            {
                var anyStepsAttempted = allSteps.OfType<Dictionary<string, object?>>()
                    .Any(s => s.TryGetValue("type", out var t) &&
                              t?.ToString() is "plan_step" or "command" or "edit" or "create");
                var planAlreadyDone = existingPlan != null && completedIndices != null && completedIndices.Count >= existingPlan.Plan.Count;
                complete = anyStepsAttempted || planAlreadyDone;
                editsApplied = true;
            }

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
            Console.WriteLine($"[AGENT CRASH] {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            await SendSse(Response, "error", new { message = $"{ex.GetType().Name}: {ex.Message}" });
        }
        finally
        {
            keepaliveCts.Cancel(); try { await keepaliveTask; } catch { }

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
        var step = new AgentStep { Index = 0, Type = "command", Command = tcpCmd, Description = "TCP Check" };
        var results = await ExecuteSteps(new List<AgentStep> { step }, projectRoot, 0, emitSse, ct);
        var first = results.FirstOrDefault() as Dictionary<string, object?>;
        var output = first?.TryGetValue("output", out var o) == true ? o?.ToString() ?? "" : "";
        var succeeded = output.Contains("TcpTestSucceeded : True", StringComparison.OrdinalIgnoreCase) ||
                        output.Contains("succeeded", StringComparison.OrdinalIgnoreCase) ||
                        output.Contains("HTTP 200", StringComparison.Ordinal);
        if (succeeded) { await EmitLog(emitSse, "info", $"LLM reachable", ct: ct); return true; }

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
        var cfg6 = await LoadConfigAsync();
        var mt = maxTokens ?? cfg6.defaultMaxTokens;
        var reqBody = new
        {
            model,
            messages,
            stream = false,
            temperature = 0.05,
            max_tokens = mt,
            repeat_penalty = 1.3,
            repeat_last_n = 256
        };
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
        var cfg7 = await LoadConfigAsync();
        var mt = maxTokens ?? cfg7.defaultMaxTokens;
        var reqBody = new
        {
            model,
            messages,
            stream = true,
            temperature = 0.05,
            max_tokens = mt,
            repeat_penalty = 1.3,
            repeat_last_n = 256
        };
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

            const int WindowChars = 400;
            const int ChunkLen = 40;
            const int RepeatThreshold = 4;
            using var repeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            while (true)
            {
                repeatCts.Token.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync().WaitAsync(repeatCts.Token);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
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

                                if (sb.Length >= ChunkLen * (RepeatThreshold + 1) &&
                                    IsRepeatingLoop(sb, WindowChars, ChunkLen, RepeatThreshold))
                                {
                                    try { resp.Dispose(); } catch { }
                                    var truncated = sb.ToString();
                                    return (truncated, null,
                                        $"Repetition loop detected after {truncated.Length} chars — aborted early. " +
                                        "The model got stuck re-emitting the same block. Retry with a smaller, more targeted anchor.");
                                }
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

    private static bool IsRepeatingLoop(StringBuilder sb, int windowChars, int chunkLen, int repeatThreshold)
    {
        var len = sb.Length;
        var start = Math.Max(0, len - windowChars);
        var window = sb.ToString(start, len - start);

        if (window.Length < chunkLen * repeatThreshold) return false;


        var tail = window[^chunkLen..];
        if (string.IsNullOrWhiteSpace(tail.Trim())) return false;

        var pos = window.Length - chunkLen;
        var consecutive = 1;
        pos -= chunkLen;
        while (pos >= 0)
        {
            var candidate = window.Substring(pos, chunkLen);
            if (candidate == tail)
            {
                consecutive++;
                if (consecutive >= repeatThreshold) return true;
                pos -= chunkLen;
            }
            else break;
        }
        return false;
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
            var cfg3 = await LoadConfigAsync();
            var reqBody = new { model, messages, stream = false, temperature = 0.0, max_tokens = maxTokens ?? cfg3.maxFileContextChars / 2 };
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
                await EmitLog(emitSse, "step", $"▶ {step.Type}: {label}", new { result }, ct: ct);
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
                await EmitLog(true, "log", $"Raw {step.Type?.ToLowerInvariant()} Result", result, ct);
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


    private async Task<List<PlanStep>> ReflectOnAppliedEditAsync(
        string relPath,
        string newStr,
        string fullFileContent,
        string projectRoot,
        List<PlanStep> existingPlanSteps,
        bool emitSse,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(newStr)) return new List<PlanStep>();

        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        var codeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".ts", ".js", ".tsx", ".jsx", ".html" };
        if (!codeExts.Contains(ext)) return new List<PlanStep>();


        var candidates = ExtractReferencedSymbolsFromCode(newStr, ext);
        if (candidates.Count == 0) return new List<PlanStep>();


        var toCheck = candidates
            .Where(sym => !fullFileContent.Contains(sym, StringComparison.Ordinal))
            .Where(sym => !existingPlanSteps.Any(s =>
                s.Change?.Contains(sym, StringComparison.OrdinalIgnoreCase) == true))
            .Distinct()
            .Take(12)
            .ToList();

        if (toCheck.Count == 0) return new List<PlanStep>();


        var grepCtx = new StringBuilder();
        var missing = new List<string>();

        foreach (var sym in toCheck)
        {
            ct.ThrowIfCancellationRequested();
            var (foundIn, snippet) = await GrepProjectForDefinitionAsync(
                projectRoot, sym, relPath, ct);

            if (foundIn != null)
                grepCtx.AppendLine($"  '{sym}' → found in {foundIn}: {snippet}");
            else
                missing.Add(sym);
        }

        if (missing.Count == 0)
        {
            await EmitLog(emitSse, "info",
                $"  ✓ Reflection: all {toCheck.Count} referenced symbol(s) already defined", ct: ct);
            return new List<PlanStep>();
        }

        await EmitLog(emitSse, "info",
            $"  🔍 Reflection: {missing.Count} potentially missing symbol(s): {string.Join(", ", missing)}", ct: ct);


        var sb = new StringBuilder();
        sb.AppendLine($"FILE JUST EDITED: {relPath}");
        sb.AppendLine();
        sb.AppendLine("NEW CODE ADDED:");
        sb.AppendLine("```");
        sb.AppendLine(newStr.Length > 2500 ? newStr[..2500] + "\n// ... (truncated)" : newStr);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("SYMBOLS REFERENCED BUT NOT FOUND IN THE PROJECT (via grep):");
        foreach (var sym in missing)
            sb.AppendLine($"  - {sym}");
        sb.AppendLine();
        if (grepCtx.Length > 0)
        {
            sb.AppendLine("SYMBOLS THAT WERE FOUND (for context):");
            sb.AppendLine(grepCtx.ToString());
        }
        sb.AppendLine("CURRENT PLAN STEPS (do NOT duplicate any of these):");
        foreach (var step in existingPlanSteps.Take(8))
            sb.AppendLine($"  - {step.File}: {step.Change}");
        sb.AppendLine();
        sb.AppendLine("TASK: For each missing symbol that genuinely needs to be implemented:");
        sb.AppendLine("  1. Decide which file it belongs in (same file, or a companion .ts/.cs file)");
        sb.AppendLine("  2. Write one specific plan step to implement it");
        sb.AppendLine("Do NOT create steps for standard library items, Angular lifecycle hooks,");
        sb.AppendLine("or anything where the absence is intentional (e.g. a placeholder).");
        sb.AppendLine("If nothing is actually missing, return {\"steps\": []}.");
        sb.AppendLine();
        sb.AppendLine("Output ONLY JSON (no markdown):");
        sb.AppendLine("{\"steps\": [{\"file\": \"rel/path.ext\", \"change\": \"precise description\"}]}");

        var (raw, _, _) = await CallLlmRaw(
            "You detect missing code implementations after an edit. Output ONLY JSON.",
            sb.ToString(), ct, TimeSpan.FromSeconds(25), maxTokens: 512);

        if (string.IsNullOrWhiteSpace(raw)) return new List<PlanStep>();

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
            if (!doc.RootElement.TryGetProperty("steps", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return new List<PlanStep>();

            var result = new List<PlanStep>();
            foreach (var el in arr.EnumerateArray())
            {
                var file = el.TryGetProperty("file", out var f) ? f.GetString() : null;
                var change = el.TryGetProperty("change", out var c) ? c.GetString() : null;
                if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(change)) continue;


                var changePrefix = change[..Math.Min(40, change.Length)];
                if (existingPlanSteps.Any(s =>
                    string.Equals(s.File, file, StringComparison.OrdinalIgnoreCase) &&
                    (s.Change ?? "").Contains(changePrefix, StringComparison.OrdinalIgnoreCase)))
                    continue;

                result.Add(new PlanStep { File = file, Change = change, Priority = 1 });
            }
            return result;
        }
        catch { return new List<PlanStep>(); }
    }

    private static List<string> ExtractReferencedSymbolsFromCode(string code, string ext)
    {
        var symbols = new HashSet<string>(StringComparer.Ordinal);

        if (ext is ".html" or ".htm")
        {

            foreach (Match m in Regex.Matches(code,
                @"\(\w+\)=""([A-Za-z_]\w*)\s*\("))
                symbols.Add(m.Groups[1].Value);


            foreach (Match m in Regex.Matches(code,
                @"\*ngFor=""let \w+ of ([A-Za-z_]\w*)"))
                symbols.Add(m.Groups[1].Value);


            foreach (Match m in Regex.Matches(code,
                @"\[[\w-]+\]=""([A-Za-z_]\w*)"))
                symbols.Add(m.Groups[1].Value);


            foreach (Match m in Regex.Matches(code,
                @"\[\(ngModel\)\]=""([A-Za-z_]\w*)"))
                symbols.Add(m.Groups[1].Value);


            foreach (Match m in Regex.Matches(code,
                @"\{\{\s*([A-Za-z_]\w*)\s*(?:\||\}\})"))
                symbols.Add(m.Groups[1].Value);
        }
        else if (ext is ".ts" or ".js" or ".tsx" or ".jsx")
        {

            foreach (Match m in Regex.Matches(code, @"this\.([A-Za-z_]\w*)\b"))
                symbols.Add(m.Groups[1].Value);
        }
        else if (ext == ".cs")
        {

            foreach (Match m in Regex.Matches(code, @"\bthis\.([A-Za-z_]\w*)\s*[(\[]"))
                symbols.Add(m.Groups[1].Value);
        }


        var builtins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {

        "ngOnInit","ngOnDestroy","ngAfterViewInit","ngOnChanges","ngDoCheck",
        "ngAfterContentInit","ngAfterContentChecked","ngAfterViewChecked","constructor",

        "length","name","value","type","id","url","href","src","target","key","index",
        "push","pop","shift","unshift","splice","slice","map","filter","reduce","find",
        "some","every","includes","indexOf","join","split","trim","toLowerCase","toUpperCase",
        "toString","parseInt","parseFloat","JSON","Math","Object","Array","String","Number",
        "Boolean","Promise","console","log","error","warn","Date","Error","typeof","instanceof",

        "subscribe","next","error","complete","pipe","tap","catchError","takeUntil",

        "ngModel","ngClass","ngStyle","ngIf","ngFor","ngSwitch","trackBy","async",
        "markForCheck","detectChanges","emit","getValue","patchValue","reset","get","set",

        "ToString","GetType","Equals","GetHashCode","Dispose","Task","List","Dictionary",
        "Console","String","Int32","Boolean","DateTime","Guid","Path","File","Directory",
    };

        return symbols
            .Where(s => s.Length >= 3 && !builtins.Contains(s) && !char.IsUpper(s[0]))
            .Distinct()
            .ToList();
    }

    private async Task<List<string>> RunCohesionCheckAsync(
        string relPath, string fileContent, string projectRoot, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileContent) || string.IsNullOrWhiteSpace(relPath))
            return new List<string>();

        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        var codeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".ts", ".js", ".tsx", ".jsx", ".html", ".css", ".scss", ".json" };
        if (!codeExts.Contains(ext)) return new List<string>();


        var staticIssues = new List<string>();
        if (ext is ".ts" or ".tsx" or ".js" or ".jsx")
        {
            var lines = fileContent.Split('\n');
            var topLevelFns = new List<(int line, string name, int indent)>();
            var indentWidth = AgentUtilities.DetectIndentWidth(fileContent);
            if (indentWidth <= 0) indentWidth = 2;


            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();

                if (Regex.IsMatch(trimmed, @"^(?:vm\.)?\w+\s*(?:[:=])\s*function\s*\(") ||
                    Regex.IsMatch(trimmed, @"^function\s+\w+\s*\("))
                {
                    var indent = line.Length - trimmed.Length;
                    topLevelFns.Add((i, trimmed, indent));
                }
            }


            var indentGroups = topLevelFns.GroupBy(f => f.indent).OrderBy(g => g.Key).ToList();
            if (indentGroups.Count > 1)
            {
                var topLevelIndent = indentGroups[0].Key;

                foreach (var group in indentGroups.Skip(1))
                {
                    if (group.Key > topLevelIndent + indentWidth)
                    {
                        foreach (var fn in group)
                        {

                            var namePart = fn.name.Split('=').Last().Split(':').Last().Trim();
                            namePart = Regex.Replace(namePart, @"\s*function\s*\(.*", "").Trim();
                            var fullName = fn.name.Contains('=') || fn.name.Contains(':')
                                ? (Regex.Match(fn.name, @"^(?:vm\.)?(\w+)").Groups[1].Value)
                                : Regex.Match(fn.name, @"function\s+(\w+)").Groups[1].Value;
                            var lineNum = fn.line + 1;
                            staticIssues.Add($"Function '{fullName}' at line {lineNum} appears to be nested inside another function body (indent level {fn.indent}, expected ~{topLevelIndent}). Move it to the top-level scope.");
                        }
                    }
                }
            }


            var fnNames = topLevelFns.Select(f => Regex.Match(f.name, @"(?:vm\.)?(\w+)\s*(?:[:=])\s*function|function\s+(\w+)").Groups.Values
                .Select(g => g.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && v != "function")).ToList();
            var dupes = fnNames.GroupBy(n => n).Where(g => g.Count() > 1);
            foreach (var dupe in dupes)
            {
                staticIssues.Add($"Duplicate definition of '{dupe.Key}' found at multiple locations. Remove the duplicate.");
            }
        }

        var contentPreview = fileContent.Length > 6000
            ? fileContent[..6000] + "\n// ... (truncated)"
            : fileContent;

        var sb = new StringBuilder();
        sb.AppendLine($"FILE: {relPath}");
        sb.AppendLine();
        sb.AppendLine("Scan the file and list any cohesion issues with the recently added code.");
        sb.AppendLine("Cohesion issues include:");
        sb.AppendLine("  - New method placed at wrong location (not grouped with similar methods)");
        sb.AppendLine("  - Naming or style inconsistent with the rest of the file");
        sb.AppendLine("  - Missing blank lines between methods");
        sb.AppendLine("  - Inconsistent error handling, logging, or return patterns");
        sb.AppendLine("  - Inconsistent attribute/annotation usage");
        sb.AppendLine("  - Inconsistent SQL or query patterns compared to similar existing methods");
        sb.AppendLine("  - Code that looks out of place or doesn't follow the file's conventions");
        sb.AppendLine();
        sb.AppendLine("Do NOT fix anything. Do NOT rewrite anything.");
        sb.AppendLine("If no issues found, output: {\"issues\": []}");
        sb.AppendLine("Otherwise output: {\"issues\": [\"Issue 1\", \"Issue 2\", ...]}");
        sb.AppendLine();
        sb.AppendLine("FILE CONTENT:");
        sb.AppendLine("```");
        sb.AppendLine(contentPreview);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Output ONLY valid JSON. No markdown. No explanations.");

        var (raw, _, _) = await CallLlmRaw(
            "You detect code cohesion issues after an edit. Output ONLY JSON.",
            sb.ToString(), ct, TimeSpan.FromSeconds(20), maxTokens: 512);

        var issues = new List<string>();

        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                var cleaned = raw.Trim();
                if (cleaned.StartsWith("```"))
                {
                    var start = cleaned.IndexOf('\n');
                    if (start > 0) cleaned = cleaned.Substring(start).Trim();
                    if (cleaned.EndsWith("```")) cleaned = cleaned[..^3].Trim();
                }
                var result = JsonSerializer.Deserialize<CohesionCheckResult>(cleaned);
                if (result?.Issues != null)
                    issues = result.Issues;
            }
            catch { }
        }

        if (issues.Count > 0)
        {
            await EmitLog(emitSse, "info",
                $"  🔍 Cohesion check: {issues.Count} issue(s) found in {relPath}", ct: ct);
            foreach (var issue in issues)
                await EmitLog(emitSse, "info", $"    - {issue}", ct: ct);
        }
        else
        {
            await EmitLog(emitSse, "info", $"  🔍 Cohesion check: no issues in {relPath}", ct: ct);
        }

        return issues;
    }


    private async Task<(string? foundInPath, string? snippet)> GrepProjectForDefinitionAsync(
        string projectRoot, string symbol, string excludeRelPath, CancellationToken ct)
    {
        var defPatterns = new[]
        {

        new Regex($@"^\s*(?:(?:public|private|protected|readonly|static|async|override|get|set)\s+)*{Regex.Escape(symbol)}\s*[=(:(<]", RegexOptions.Multiline),

        new Regex($@"\b(?:public|private|protected|internal)\b[^{{}}]*\b{Regex.Escape(symbol)}\s*[({{;]", RegexOptions.Multiline),

        new Regex($@"@(?:Input|Output)\(\)[^;]*\b{Regex.Escape(symbol)}\b", RegexOptions.Multiline),
    };

        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "node_modules", ".git", "bin", "obj", "dist", ".angular", "packages" };
        var codeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".ts", ".js", ".tsx", ".jsx" };

        try
        {
            foreach (var file in Directory.EnumerateFiles(
                projectRoot, "*.*", SearchOption.AllDirectories))
            {
                if (ct.IsCancellationRequested) break;

                var rel = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
                if (string.Equals(rel, excludeRelPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (skipDirs.Any(d => rel.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase) ||
                    rel.Contains("/" + d + "/", StringComparison.OrdinalIgnoreCase))) continue;
                if (!codeExts.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;

                FileInfo fi;
                try { fi = new FileInfo(file); } catch { continue; }
                if (fi.Length > 300_000) continue;

                string content;
                try { content = await System.IO.File.ReadAllTextAsync(file, Encoding.UTF8, ct); }
                catch { continue; }

                foreach (var rx in defPatterns)
                {
                    var m = rx.Match(content);
                    if (!m.Success) continue;

                    var lineNo = content[..m.Index].Count(c => c == '\n') + 1;
                    var line = m.Value.Trim();
                    if (line.Length > 80) line = line[..80] + "…";
                    return (rel, $"line {lineNo}: {line}");
                }
            }
        }
        catch { }

        return (null, null);
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


            try { _fileHints.LearnFromAppliedEdit(projectRoot, targetPath, newString); }
            catch { }
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


        try { _fileHints.LearnFromAppliedEdit(projectRoot, targetPath, newString); }
        catch { }
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
            .Where(s => s.TryGetValue("type", out var t) && t?.ToString() == "edit")
            .GroupBy(s => s.GetValueOrDefault("path")?.ToString() ?? Guid.NewGuid().ToString())
            .Select(g => g.Last())
            .ToList();
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


        var modifiedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in editSteps)
        {
            var p = s.GetValueOrDefault("path")?.ToString();
            if (!string.IsNullOrWhiteSpace(p))
                modifiedSet.Add(p.Replace('\\', '/'));
        }



        if (attachedFiles != null && attachedFiles.Count > 0)
        {
            sb.AppendLine("## Unmodified attached files (check each one — does it still need changes to complete the task?)");
            foreach (var relPath in attachedFiles)
            {
                var normalized = relPath.Replace('\\', '/');
                if (modifiedSet.Contains(normalized)) continue;
                var fullPath = Path.GetFullPath(Path.Combine(projectRoot, normalized));
                if (!System.IO.File.Exists(fullPath)) continue;
                var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                sb.AppendLine($"### {normalized}\n```\n{content}\n```\n");
            }
        }


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

        sb.AppendLine(@"Evaluate the code changes against the ORIGINAL TASK ONLY. Judge strictly against what the user
EXPLICITLY requested — do NOT invent additional requirements, features, files, or 'best practice' improvements the
user did not ask for. Check for:
1. Does the code address everything the user EXPLICITLY requested?
2. Are there bugs, syntax errors, or logic issues in the modified files that would break the requested change?
3. Did any planned step fail or get left unfinished?
4. Check files in ""Unmodified attached files"" ONLY against the explicit request — mark incomplete only if the user's request clearly required changing them.
A task is complete when the explicit request is satisfied, even if you can imagine further improvements. When in doubt, mark complete=true.

Respond with JSON only:
```json
{
  ""complete"": true|false,
  ""reason"": ""one sentence summary"",
  ""issues"": [""description of each bug or remaining work""]
}
```");

        const string sys = @"You are a thorough code reviewer and task completion verifier. Examine the original task, the changes made, and the current state of all files. Check for bugs, logic errors, and syntax mistakes that would break the requested change. Judge completion ONLY against what the user explicitly requested — never invent new requirements, features, or scope the user did not ask for. When the explicit request is met, mark complete=true even if further improvements are imaginable. Output ONLY valid JSON in the format specified.";

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

    private AgentPlan MergePlans(AgentPlan existing, AgentPlan replan)
    {
        if (existing == null) return replan;
        if (existing.Plan == null) existing.Plan = new List<PlanStep>();

        var existingKeys = new HashSet<string>(
            existing.Plan.Select(p => $"{p.File}|{NormalizeChangeForDedup(p.Change)}"),
            StringComparer.OrdinalIgnoreCase);

        foreach (var step in replan.Plan)
        {
            var key = $"{step.File}|{NormalizeChangeForDedup(step.Change)}";
            if (existingKeys.Add(key))
            {
                existing.Plan.Add(step);
            }
        }

        return existing;
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



    private static string RepairJsonNewlines(string json)
    {



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

                if (!inString) { inString = true; sb.Append(c); continue; }



                var lookahead = json.Length > i + 1 ? json[i + 1] : '\0';
                if (lookahead == ',' || lookahead == ']' || lookahead == '}' ||
                    lookahead == ':' || lookahead == '\t' ||
                    lookahead == '\n' || lookahead == '\r' || lookahead == ' ')
                {
                    inString = false; sb.Append(c);
                }
                else
                {
                    sb.Append("\\\"");
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

    private static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder();
        var inQuote = false;
        var quoteChar = '\0';
        var prevWasSpace = false;

        foreach (var c in s)
        {
            if ((c == '"' || c == '\'' || c == '`') && (sb.Length == 0 || sb[sb.Length - 1] != '\\'))
            {
                if (!inQuote) { inQuote = true; quoteChar = c; }
                else if (c == quoteChar) { inQuote = false; }
            }

            if (inQuote)
            {
                sb.Append(c);
                prevWasSpace = false;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (!prevWasSpace && sb.Length > 0) { sb.Append(' '); prevWasSpace = true; }
            }
            else
            {
                sb.Append(c);
                prevWasSpace = false;
            }
        }

        return sb.ToString().Trim();
    }


    private static int FindLineBlock(string[] fileLines, string[] patternLines, StringComparison cmp, int preferredLine = 0)
    {
        if (patternLines.Length == 0 || fileLines.Length < patternLines.Length) return -1;
        var bestIdx = -1;
        var bestDist = int.MaxValue;
        for (var fi = 0; fi <= fileLines.Length - patternLines.Length; fi++)
        {
            var match = true;
            for (var li = 0; li < patternLines.Length; li++)
            {
                if (!string.Equals(fileLines[fi + li], patternLines[li], cmp))
                { match = false; break; }
            }
            if (!match) continue;
            if (preferredLine <= 0) return fi;
            var dist = Math.Abs((fi + 1) - preferredLine);
            if (dist < bestDist) { bestDist = dist; bestIdx = fi; }
        }
        return preferredLine > 0 ? bestIdx : -1;
    }

    private static double ComputeLineSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 1.0;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
        var aNorm = a.Trim().ToLowerInvariant();
        var bNorm = b.Trim().ToLowerInvariant();
        var maxLen = Math.Max(aNorm.Length, bNorm.Length);
        if (maxLen == 0) return 1.0;

        if (maxLen <= 80)
            return 1.0 - (double)AgentUtilities.ComputeLevenshteinDistance(aNorm, bNorm) / maxLen;

        var common = 0; var minLen = Math.Min(aNorm.Length, bNorm.Length);
        for (var i = 0; i < minLen; i++) { if (aNorm[i] == bNorm[i]) common++; else break; }
        return (double)common / maxLen;
    }

    private static (int lineIdx, double score, bool hasExactLine) FindBestFuzzyBlock(string[] fileLines, string[] oldLines, int preferredLine = 0)
    {
        if (oldLines.Length == 0 || fileLines.Length < oldLines.Length) return (-1, 0, false);
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

            if (preferredLine > 0)
            {
                var dist = Math.Abs((fi + 1) - preferredLine);
                avg += Math.Max(0, 0.10 - dist * 0.005);
            }
            if (avg > bestScore) { bestScore = avg; bestIdx = fi; bestHasExact = anyExact; }
        }
        return (bestIdx, bestScore, bestHasExact);
    }
    private static (int lineIdx, double score, int exactCount) FindBestAnchorLineBlock(string[] fileLines, string[] oldLines, int preferredLine = 0)
    {
        if (oldLines.Length == 0) return (-1, 0, 0);


        var candidates = oldLines
            .Select((l, i) => (line: l.Trim(), idx: i))
            .Where(x => x.line.Length >= 8)
            .OrderByDescending(x => x.line.Length)
            .ToList();

        var bestScore = 0.0; var bestIdx = -1; var bestExact = 0;

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

            if (preferredLine > 0)
            {
                var dist = Math.Abs((filePos + 1) - preferredLine);
                avg += Math.Max(0, 0.05 - dist * 0.005);
            }
            if (avg >= 0.80 && exactCount >= 2 && avg > bestScore)
            {
                bestScore = avg;
                bestIdx = filePos;
                bestExact = exactCount;
            }
        }

        return bestIdx >= 0 ? (bestIdx, bestScore, bestExact) : (-1, 0, 0);
    }

    private static (bool replaced, string newContent, string? matchError, string? snippet) TryReplaceSafe(
     string fileContent, string oldStr, string newStr, int targetLine = 0, string? changeDesc = null)
    {
        if (string.IsNullOrEmpty(oldStr) && string.IsNullOrEmpty(fileContent) && !string.IsNullOrEmpty(newStr))
            return (true, newStr, null, null);


        if (string.IsNullOrEmpty(oldStr) && !string.IsNullOrEmpty(fileContent))
        {
            return (false, fileContent,
                "oldString is empty but the file is non-empty — refusing to perform an unbounded replacement. " +
                "Provide a non-empty, specific anchor.", null);
        }

        var normFile = AgentUtilities.NormalizeLineEndings(fileContent);
        var normOld = AgentUtilities.NormalizeLineEndings(oldStr);


        var matches = new List<int>();
        var searchPos = 0;
        var maxIterations = normFile.Length + 2;
        var iterations = 0;
        while (iterations++ < maxIterations)
        {
            var idx = normFile.IndexOf(normOld, searchPos, StringComparison.Ordinal);
            if (idx < 0) break;
            matches.Add(idx);
            searchPos = idx + Math.Max(1, normOld.Length);
        }


        if (matches.Count == 1)
        {
            var normNew = AgentUtilities.NormalizeLineEndings(newStr);
            return (true, normFile[..matches[0]] + normNew + normFile[(matches[0] + normOld.Length)..], null, null);
        }

        if (matches.Count > 1)
        {
            int chosenIdx = -1;


            var keywords = AgentUtilities.ExtractDisambiguationKeywords(changeDesc);
            if (keywords.Count > 0)
            {
                int bestContextScore = -1;
                for (int i = 0; i < matches.Count; i++)
                {
                    var lookbackStart = Math.Max(0, matches[i] - 2000);
                    var context = normFile.Substring(lookbackStart, matches[i] - lookbackStart).ToLowerInvariant();
                    var score = keywords.Count(k => context.Contains(k));

                    if (score > bestContextScore)
                    {
                        bestContextScore = score;
                        chosenIdx = i;
                    }
                }
            }


            if (chosenIdx == -1 && targetLine > 0)
            {
                var bestDist = int.MaxValue;
                for (int i = 0; i < matches.Count; i++)
                {
                    var matchLine = normFile[..matches[i]].Count(c => c == '\n') + 1;
                    var dist = Math.Abs(matchLine - targetLine);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        chosenIdx = i;
                    }
                }


                if (bestDist > 50) chosenIdx = -1;
            }

            if (chosenIdx >= 0)
            {
                var normNew = AgentUtilities.NormalizeLineEndings(newStr);
                return (true, normFile[..matches[chosenIdx]] + normNew + normFile[(matches[chosenIdx] + normOld.Length)..], null, null);
            }

            var firstLine = normOld.Split('\n')[0].Trim();
            var uniqueLine = AgentUtilities.ExtractMostUniqueLine(normOld, normFile);

            var err = $"oldString found {matches.Count} times in file — include more surrounding lines as anchor context.";
            if (uniqueLine != null)
                err += $" OR use ONLY this unique line as your entire oldString: `{uniqueLine.Trim()}`";

            return (false, fileContent, err, firstLine);
        }


        var firstRealLine = normOld.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (firstRealLine != null)
        {
            var fuzzyIdx = normFile.IndexOf(firstRealLine, StringComparison.Ordinal);
            if (fuzzyIdx >= 0)
            {
                var lineStart = normFile.LastIndexOf('\n', fuzzyIdx) + 1;
                var fileSegment = normFile[lineStart..];
                if (fileSegment.StartsWith(normOld.TrimStart()))
                {
                    var normNew = AgentUtilities.NormalizeLineEndings(newStr);
                    return (true, normFile[..lineStart] + normNew + normFile[(lineStart + normOld.Length)..], null, null);
                }
            }
        }

        return (false, fileContent, "oldString not found verbatim in file", null);
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
            .Where(l => l.Length >= 8)
            .ToList();
        if (oldLines.Count == 0 || fileLines.Length == 0) return null;

        bool IsTrivialLine(string line)
        {
            var t = line.Trim();
            if (t.Length < 12) return true;

            var meaningful = new string(t.Where(char.IsLetterOrDigit).ToArray());
            if (meaningful.Length < 12) return true;

            if (Regex.IsMatch(t, @"^\s*[\w-]+\s*:\s*[\w\d#.()-]+\s*;?\s*$"))
            {

                return true;
            }
            return false;
        }

        var results = new List<(int fileIdx, double score, string line)>();
        for (var fi = 0; fi < fileLines.Length; fi++)
        {
            var fLine = fileLines[fi];
            if (IsTrivialLine(fLine)) continue;
            var bestSim = oldLines.Max(o => ComputeLineSimilarity(fLine, o));
            if (bestSim >= 0.50)
                results.Add((fi, bestSim, fLine));
        }

        var best = results
            .OrderByDescending(r => r.score)
            .ThenByDescending(r => r.line.Trim().Length)
            .Take(3)
            .ToList();
        if (best.Count == 0) return null;

        var sb = new StringBuilder();
        foreach (var b in best)
        {
            sb.AppendLine($"  ({(b.score * 100):F0}% match) line {b.fileIdx + 1}: {b.line}");

            var llmLine = oldLines
                .OrderByDescending(o => ComputeLineSimilarity(b.line, o))
                .FirstOrDefault();
            if (llmLine != null && llmLine != b.line.Trim())
            {
                var fileTrimmed = b.line.Trim();
                var diff = DescribeLineDiff(llmLine, fileTrimmed);
                if (diff != null)
                    sb.AppendLine($"    └ DIFF: {diff}");
            }
        }
        return sb.ToString();
    }

    private static string? DescribeLineDiff(string llm, string file)
    {
        if (string.Equals(llm, file, StringComparison.Ordinal)) return null;

        var diffs = new List<string>();


        var llmNoCommaSpace = Regex.Replace(llm, @",\s*", ",");
        var fileNoCommaSpace = Regex.Replace(file, @",\s*", ",");
        if (llmNoCommaSpace == fileNoCommaSpace && llm != file)
            diffs.Add("the file has spaces after commas that you omitted — e.g. 'rgba(255,255,255)' should be 'rgba(255, 255, 255)'");


        var llmNoColonSpace = Regex.Replace(llm, @":\s*", ":");
        var fileNoColonSpace = Regex.Replace(file, @":\s*", ":");
        if (llmNoColonSpace == fileNoColonSpace && llmNoCommaSpace != fileNoCommaSpace)
            diffs.Add("the file has spaces after colons that you omitted — e.g. 'padding:16px' should be 'padding: 16px'");


        var llmNoEqSpace = Regex.Replace(llm, @"\s*=\s*", "=");
        var fileNoEqSpace = Regex.Replace(file, @"\s*=\s*", "=");
        if (llmNoEqSpace == fileNoEqSpace && llmNoCommaSpace != fileNoCommaSpace && llmNoColonSpace != fileNoColonSpace)
            diffs.Add("the file has spaces around '=' that you omitted — e.g. 'x=0' should be 'x = 0'");


        var llmNoParenSpace = Regex.Replace(llm, @"\(\s+", "(").Replace(")", " )").Replace(") )", "))");
        var fileNoParenSpace = Regex.Replace(file, @"\(\s+", "(").Replace(")", " )").Replace(") )", "))");
        if (llmNoParenSpace == fileNoParenSpace && llm != file
            && llmNoCommaSpace == fileNoCommaSpace && llmNoColonSpace == fileNoColonSpace)
            diffs.Add("the file has different whitespace inside parens");


        if (diffs.Count == 0)
        {
            var minLen = Math.Min(llm.Length, file.Length);
            var firstDiff = -1;
            for (var i = 0; i < minLen; i++)
            {
                if (llm[i] != file[i]) { firstDiff = i; break; }
            }
            if (firstDiff >= 0)
            {
                var ctx = Math.Max(0, firstDiff - 8);
                var llmCtx = llm.Substring(ctx, Math.Min(20, llm.Length - ctx));
                var fileCtx = file.Substring(ctx, Math.Min(20, file.Length - ctx));
                diffs.Add($"first difference at position {firstDiff}: you wrote '{llmCtx}' but file has '{fileCtx}'");
            }
            else
            {
                diffs.Add($"length differs: you wrote {llm.Length} chars, file has {file.Length} chars");
            }
        }

        return string.Join("; ", diffs);
    }

    private static string? BuildExactMatchBlock(string fileContent, string oldStr, int targetLine = 0, string? changeDesc = null)
    {
        if (string.IsNullOrWhiteSpace(oldStr)) return null;
        var normFile = AgentUtilities.NormalizeLineEndings(fileContent);



        var changeLower = (changeDesc ?? "").ToLowerInvariant();
        bool isRemoval = changeLower.Contains("remove") ||
            (changeLower.Contains("delete") && !Regex.IsMatch(changeLower, @"\b(add|create|insert|implement)\b"));

        if (!isRemoval)
        {
            var htmlBlock = AgentUtilities.ExtractFullHtmlBlock(normFile, oldStr);
            if (htmlBlock != null) return htmlBlock;
        }

        var normOld = AgentUtilities.NormalizeLineEndings(oldStr);
        var oldLines = normOld.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (oldLines.Count == 0) return null;

        var fileLines = normFile.Split('\n');
        var candidates = new List<(int startIdx, int score)>();

        for (var i = 0; i < fileLines.Length; i++)
        {
            var score = 0;
            var fIdx = i;
            var oIdx = 0;

            while (fIdx < fileLines.Length && oIdx < oldLines.Count)
            {
                var fileTrim = fileLines[fIdx].Trim();
                var oldTrim = oldLines[oIdx].Trim();

                if (fileTrim == oldTrim || fileTrim.StartsWith(oldTrim) || oldTrim.StartsWith(fileTrim))
                {
                    score++;
                    fIdx++;
                    oIdx++;
                }
                else if (string.IsNullOrEmpty(fileTrim) || string.IsNullOrEmpty(oldTrim))
                {
                    if (string.IsNullOrEmpty(fileTrim)) fIdx++;
                    if (string.IsNullOrEmpty(oldTrim)) oIdx++;
                }
                else
                {
                    break;
                }
            }

            if (score >= Math.Max(1, oldLines.Count / 2))
            {
                candidates.Add((i, score));
            }
        }

        if (candidates.Count == 0) return null;

        int chosenCandidate = -1;

        if (candidates.Count == 1)
        {
            chosenCandidate = 0;
        }
        else
        {

            var keywords = AgentUtilities.ExtractDisambiguationKeywords(changeDesc);
            if (keywords.Count > 0)
            {
                int bestContextScore = -1;
                for (int i = 0; i < candidates.Count; i++)
                {
                    var startIdx = candidates[i].startIdx;
                    var lookbackStart = Math.Max(0, startIdx - 50);
                    var context = string.Join("\n", fileLines.Skip(lookbackStart).Take(startIdx - lookbackStart)).ToLowerInvariant();

                    var score = keywords.Count(k => context.Contains(k));
                    if (score > bestContextScore)
                    {
                        bestContextScore = score;
                        chosenCandidate = i;
                    }
                }
            }


            if (chosenCandidate == -1 && targetLine > 0)
            {
                int bestDist = int.MaxValue;
                for (int i = 0; i < candidates.Count; i++)
                {
                    var dist = Math.Abs(candidates[i].startIdx + 1 - targetLine);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        chosenCandidate = i;
                    }
                }
                if (bestDist > 50) chosenCandidate = -1;
            }
        }

        if (chosenCandidate >= 0)
        {
            var bestStart = candidates[chosenCandidate].startIdx;
            var endIdx = Math.Min(fileLines.Length, bestStart + oldLines.Count);
            var joined = string.Join("\n", fileLines.Skip(bestStart).Take(endIdx - bestStart));
            return string.IsNullOrWhiteSpace(joined) ? null : joined;
        }

        return null;
    }


    private static string? GetUnsafeEditPayloadReason(string oldString, string newString)
    {
        foreach (var marker in UnsafeEditMarkers)
            if (oldString.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                newString.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return $"Edit contains placeholder marker '{marker}'.";
        return null;
    }

    private static bool IsHtmlLikeContent(string content) =>
     content.Contains('<') && Regex.IsMatch(content, @"</?\w+[\s/>]");

    private static string IndentReplacement(string[] fileLines, int start, string replacement)
    {
        if (string.IsNullOrEmpty(replacement) || start >= fileLines.Length)
            return replacement;

        var fileIndent = AgentUtilities.GetLeadingWhitespace(fileLines[start]);
        if (fileIndent.Length == 0)
            return replacement;

        var replLines = replacement.Split('\n');
        var replBaseIndent = replLines.Where(l => l.Length > 0)
                                      .Select(AgentUtilities.GetLeadingWhitespace)
                                      .FirstOrDefault();

        if (replBaseIndent != null && replBaseIndent != fileIndent)
        {
            for (var i = 0; i < replLines.Length; i++)
            {
                if (replLines[i].Length == 0) continue;
                var lineIndent = AgentUtilities.GetLeadingWhitespace(replLines[i]);
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

        if (IsHtmlLikeContent(replacement) && replLines.Length > 5)
        {
            return AgentUtilities.AutoIndentHtml(string.Join("\n", replLines), fileIndent);
        }

        var joined = string.Join("\n", replLines);
        var distinctIndentDepths = replLines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => AgentUtilities.GetLeadingWhitespace(l).Length)
            .Distinct()
            .Count();
        return distinctIndentDepths <= 1 && replLines.Length > 2
            ? AgentUtilities.AutoIndentFromFile(joined, fileIndent, fileLines, start)
            : joined;
    }

    private async Task<string> EnrichWithTypeChain(
        string projectRoot,
        string relPath,
        string stepChange,
        HashSet<string> alreadyRead,
        bool emitSse,
        CancellationToken ct,
        int maxDepth = 3)
    {
        var buf = new StringBuilder();
        const int MaxEnrichChars = 6000;
        var discoveredTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var typesToFollow = new Queue<(string typeName, int depth)>();

        var targetFullPath = Path.GetFullPath(
            Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!System.IO.File.Exists(targetFullPath)) return "";

        var targetContent = await System.IO.File.ReadAllTextAsync(targetFullPath, Encoding.UTF8, ct);

        var typeRefPattern = new Regex(
            @"(?::\s*)([A-Z][A-Za-z0-9_]+)(?:\[\])?(?:\s*[;=})|])",
            RegexOptions.Compiled);

        foreach (Match m in typeRefPattern.Matches(targetContent))
        {
            var typeName = m.Groups[1].Value;
            if (!_builtInTypes.Contains(typeName) && typeName.Length > 2)
            {
                typesToFollow.Enqueue((typeName, 0));
            }
        }

        foreach (Match m in Regex.Matches(stepChange, @"\b([A-Z][A-Za-z0-9_]+)\b"))
        {
            var typeName = m.Groups[1].Value;
            if (!_builtInTypes.Contains(typeName) && typeName.Length > 2)
            {
                typesToFollow.Enqueue((typeName, 0));
            }
        }

        var typeFileExtensions = new[] { ".cs", ".ts", ".tsx", ".js", ".jsx" };
        var allProjectFiles = typeFileExtensions
            .SelectMany(ext => Directory.EnumerateFiles(projectRoot, ext, SearchOption.AllDirectories))
            .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\")
                     && !f.Contains("\\node_modules\\") && !f.Contains("\\.git\\")
                     && !f.Contains("\\dist\\"))
            .ToList();


        while (typesToFollow.Count > 0 && buf.Length < MaxEnrichChars)
        {
            var (typeName, depth) = typesToFollow.Dequeue();
            if (depth > maxDepth) continue;
            if (discoveredTypes.Contains(typeName)) continue;
            if (_builtInTypes.Contains(typeName)) continue;
            discoveredTypes.Add(typeName);


            string? definingFile = null;
            string? definingContent = null;
            foreach (var pf in allProjectFiles)
            {
                try
                {
                    var content = await System.IO.File.ReadAllTextAsync(pf, Encoding.UTF8, ct);
                    if (Regex.IsMatch(content,
                        $@"(?:export\s+)?(?:abstract\s+)?(?:class|interface|type|record|struct)\s+{Regex.Escape(typeName)}\b",
                        RegexOptions.IgnoreCase))
                    {
                        definingFile = pf;
                        definingContent = content;
                        break;
                    }
                }
                catch { continue; }
            }

            if (definingFile == null || definingContent == null) continue;

            var rel = Path.GetRelativePath(projectRoot, definingFile).Replace('\\', '/');
            if (alreadyRead.Contains(rel)) continue;
            alreadyRead.Add(rel);


            var excerpt = AgentUtilities.ExtractRelevantExcerpt(definingContent, typeName, null, 1500);
            buf.AppendLine($"### {rel}  (type: {typeName}, depth: {depth})");
            buf.AppendLine("```");
            buf.AppendLine(excerpt);
            buf.AppendLine("```");
            buf.AppendLine();



            if (depth < maxDepth && !string.IsNullOrEmpty(excerpt))
            {
                foreach (Match m in typeRefPattern.Matches(excerpt))
                {
                    var nestedType = m.Groups[1].Value;
                    if (!discoveredTypes.Contains(nestedType) &&
                        !_builtInTypes.Contains(nestedType) &&
                        nestedType.Length > 2)
                    {
                        typesToFollow.Enqueue((nestedType, depth + 1));
                    }
                }
            }
        }

        if (buf.Length == 0) return "";

        await EmitLog(emitSse, "info",
            $"  🔗 Type-chain enrichment: discovered {discoveredTypes.Count} type(s) " +
            $"[{string.Join(", ", discoveredTypes.Take(8))}]", ct: ct);

        return "\n### AUTO-ENRICHED TYPE CONTEXT (followed type references recursively)\n" +
               "⚠ These type definitions show EXACT property names. Use ONLY these property names in your edit.\n" +
               buf.ToString();
    }


    private static string NormalizeTypeScriptObjectLiterals(string content)
    {

        return Regex.Replace(content, @"(?<=[\{,]\s*)(\w[\w']*)\s*:\s*(?=\S)", "$1: ");
    }

    private async Task<string> EnsureCompleteFullFile(string partialContent, PlanStep step,
        string fullPath, string projectRoot, bool emitSse, CancellationToken ct,
        List<(string old, string @new, string error)>? history = null)
    {
        if (!AgentUtilities.IsFullFileTruncated(partialContent))
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

            var continuationStart = AgentUtilities.FindLastBalancedPrefix(accumulated);
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

            raw = StripFullFileFence(raw);

            accumulated += "\n" + raw;

            if (!AgentUtilities.IsFullFileTruncated(accumulated))
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
            fullContent = AgentUtilities.AutoIndentFullFile(fullContent, existingLines);


        var fileExt = Path.GetExtension(relPath).ToLowerInvariant();

        if (fileExt is ".css" or ".scss" or ".less")
        {
            var (merged, mergeWarnings) = MergeDuplicateCssRules(fullContent);
            if (merged != fullContent)
            {
                fullContent = merged;
                foreach (var w in mergeWarnings)
                    await EmitLog(emitSse, "warn", w, ct: ct);
                await EmitLog(emitSse, "info",
                    $"Merged duplicate CSS selectors in {relPath} (fullFile path)", ct: ct);
            }
        }


        if (fileExt is ".css" or ".scss" or ".less")
        {
            var before = fullContent;
            fullContent = FormatCssEditedRegion(fullContent, fullContent);
            if (fullContent != before)
                await EmitLog(emitSse, "info",
                    $"CSS region formatted in {relPath} (fullFile path)", ct: ct);
        }


        if (fileExt is ".ts" or ".tsx" or ".js" or ".jsx" or ".cs"
            or ".css" or ".scss" or ".less" or ".html" or ".json"
            or ".vue" or ".svelte")
        {
            var before = fullContent;
            fullContent = AutoFormatEditedRegion(fullContent, fullContent);
            if (fullContent != before)
                await EmitLog(emitSse, "info",
                    $"Auto-formatted full file in {relPath} (commas/colons/semicolons/equals + closing-dedent)", ct: ct);
        }

        await System.IO.File.WriteAllTextAsync(fullPath, fullContent, Encoding.UTF8, ct);
        await EmitLog(emitSse, "success", $"✓ Written {relPath} ({fullContent.Length} chars)", ct: ct);
        var r = new Dictionary<string, object?>();
        PopulateEditResult(r, "modified", relPath, null, fullContent, "");
        r["index"] = stepIndex;
        r["planItemIndex"] = planItemIndex;
        if (emitSse) await SendSse(Response, "step", r, ct);
        allResults.Add(r);
        await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct);

        try { _fileHints.LearnFromAppliedEdit(projectRoot, fullPath, fullContent); }
        catch { }

        _ = Task.Run(async () =>
        {
            try { await _editKnowledge.UpdateArchitectureAsync(projectRoot, relPath, fullContent); }
            catch { }
        }, CancellationToken.None);

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
        if (!AgentUtilities.IsPathUnderRoot(targetPath, projectRoot))
        {
            result["status"] = "error";
            result["error"] = "Path outside root";
            return;
        }
        if (!System.IO.File.Exists(targetPath))
        {
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

    private async Task<(List<object> allSteps, AgentPlan? plan, bool complete)> RepairBuildPipeline(string prompt, string projectRoot, bool emitSse, string buildCommands, CancellationToken ct)
    {
        await EmitLog(emitSse, "info", "Build repair prompt detected — running repair pipeline.", ct: ct);
        var cmds = ParseBuildCommands(buildCommands);
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
            if (string.IsNullOrWhiteSpace(raw)) { await EmitLog(emitSse, "warn", $"Build check LLM failed: {err}", new { raw }, ct: ct); break; }

            var decision = ParseBuildCheckResponse(raw);
            if (decision == null) { await EmitLog(emitSse, "warn", "Could not parse build check response", new { raw, decision }, ct: ct); break; }

            switch (decision.Decision)
            {
                case "done": await EmitLog(emitSse, "success", $"Build OK: {decision.Summary}", new { raw, decision }, ct: ct); return true;
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
                    var userQuestion = !string.IsNullOrWhiteSpace(decision.UserQuestion)
                        ? decision.UserQuestion
                        : $"Build needs input: {decision.Summary}\n\nProvide the required input or type 'skip' to skip this build check:";
                    var answer = await AskUserAsync(userQuestion, new List<QuestionField>
                    {
                        new() { Key = "buildResponse", Label = decision.Summary ?? "", Type = "text", DefaultValue = "" }
                    }, ct, new { raw, decision });
                    var userResponse = answer.GetValueOrDefault("buildResponse", "").Trim();
                    if (!string.IsNullOrWhiteSpace(userResponse))
                    {
                        if (userResponse.Equals("skip", StringComparison.OrdinalIgnoreCase))
                        {
                            await EmitLog(emitSse, "warn", "User skipped build check.", ct: ct);
                            return true;
                        }

                        await _terminal.WriteStdinAsync(userResponse);
                        await Task.Delay(1000);

                        continue;
                    }
                    return false;
                default: return false;
            }
        }
        await EmitLog(emitSse, "warn", $"Build check inconclusive after {maxIter} iterations", ct: ct);
        return false;
    }

    private static string ExtractFirstJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "{}";
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```"))
        {
            var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (m.Success) cleaned = m.Groups[1].Value.Trim();
            else
            {
                cleaned = cleaned.TrimStart('`');
                var firstNl = cleaned.IndexOf('\n');
                if (firstNl >= 0) cleaned = cleaned[(firstNl + 1)..];
                if (cleaned.EndsWith("```")) cleaned = cleaned[..^3];
            }
        }

        var fb = cleaned.IndexOf('{');
        if (fb < 0) return "{}";

        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = fb; i < cleaned.Length; i++)
        {
            var c = cleaned[i];
            if (escape) { escape = false; continue; }
            if (c == '\\') { escape = true; continue; }
            if (c == '"') inString = !inString;
            if (inString) continue;

            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return cleaned.Substring(fb, i - fb + 1);
            }
        }
        return cleaned.Substring(fb);
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

    private static (bool skipMetaPlan, int score) DeterministicMetaPlanGate(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return (true, 0);
        var lower = prompt.ToLowerInvariant();

        if (Regex.IsMatch(lower,
                @"\b(add|create|implement)\b.{0,50}\b(method|endpoint)\b.{0,80}\b(table|insert|create\s+table)\b") &&
            !Regex.IsMatch(lower, @"\b(component|frontend|angular|service\s+layer|multiple\s+files)\b"))
        {
            return (true, 0);
        }

        var distinctFileHints = Regex.Matches(prompt, @"[\w\-/\\]+\.(cs|ts|tsx|js|jsx|html|css|scss)")
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var newSymbolVerbs = Regex.Matches(lower, @"\b(add|create|implement|build)\b").Count;

        var crossCuttingWords = Regex.Matches(lower,
            @"\b(component|service|controller|endpoint|frontend|backend|scaffold|module|full\s+crud|end.to.end)\b").Count;

        var score = distinctFileHints * 2 + newSymbolVerbs + crossCuttingWords;

        const int MetaPlanFloor = 6;
        return (score < MetaPlanFloor, score);
    }
    private async Task RepairPipeline(
        string projectRoot, bool emitSse, CancellationToken ct,
        string originalPrompt, string? steeringContext, string? buildCommands)
    {
        var buildOutput = _terminal.ReadAll();
        var resultSteps = new List<object>();
        await RunRepairPlan(projectRoot, emitSse, ct, originalPrompt, buildOutput, resultSteps, steeringContext);

        var cmds = !string.IsNullOrWhiteSpace(buildCommands) ? ParseBuildCommands(buildCommands) : new List<string>();
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

    private async Task RunTestCreationPipeline(
        string projectRoot, List<object> allSteps, bool emitSse, CancellationToken ct)
    {
        var editedFiles = allSteps
            .OfType<Dictionary<string, object?>>()
            .Where(s => s.GetValueOrDefault("type")?.ToString() is "edit" or "create")
            .Select(s => s.GetValueOrDefault("path")?.ToString())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (editedFiles.Count == 0) return;

        await EmitLog(emitSse, "info", $"TestCreation: preparing tests for {editedFiles.Count} file(s)", ct: ct);

        var existingTestFiles = AgentUtilities.FindExistingTestFiles(projectRoot);
        var hasExistingTests = existingTestFiles.Count > 0;
        var testFramework = await AgentUtilities.DetectTestFramework(projectRoot, ct);

        if (!hasExistingTests && testFramework == null)
        {
            if (emitSse)
                await SendSse(Response, "phase", new { phase = "test-creation", message = "No test framework detected" }, ct);

            var answer = await AskUserAsync(
                "No test files found. Enter framework name to set up (xunit, nunit, mstest) or leave empty to skip:",
                new List<QuestionField>
                {
                    new() { Key = "framework", Label = "Test framework", Type = "text", DefaultValue = "xunit" }
                }, ct);

            var framework = answer.GetValueOrDefault("framework")?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(framework) || framework is "none" or "skip")
            {
                await EmitLog(emitSse, "info", "Test creation skipped by user.", ct: ct);
                return;
            }
            testFramework = framework;
        }

        testFramework ??= "xunit";

        if (existingTestFiles.Count > 0)
        {
            if (existingTestFiles.Any(f => AgentUtilities.FileContains(f, "xunit", "Fact"))) testFramework = "xunit";
            else if (existingTestFiles.Any(f => AgentUtilities.FileContains(f, "nunit", "TestFixture"))) testFramework = "nunit";
            else if (existingTestFiles.Any(f => AgentUtilities.FileContains(f, "mstest", "TestClass", "TestMethod"))) testFramework = "mstest";
        }

        await EmitLog(emitSse, "info", $"TestCreation: using '{testFramework}'", ct: ct);

        if (emitSse)
            await SendSse(Response, "phase", new { phase = "test-creation", message = $"Generating tests ({testFramework})" }, ct);

        var existingContext = new StringBuilder();
        foreach (var tf in existingTestFiles.Take(3))
        {
            try
            {
                var rel = Path.GetRelativePath(projectRoot, tf);
                var content = await System.IO.File.ReadAllTextAsync(tf, Encoding.UTF8, ct);
                existingContext.AppendLine($"// File: {rel}");
                existingContext.AppendLine(content);
                existingContext.AppendLine();
            }
            catch { }
        }

        var testDir = AgentUtilities.FindOrDetermineTestDir(projectRoot, existingTestFiles);

        foreach (var filePath in editedFiles)
        {
            var fullPath = Path.Combine(projectRoot, filePath);
            if (!System.IO.File.Exists(fullPath))
            {
                await EmitLog(emitSse, "warn", $"TestCreation: file not found: {filePath}", ct: ct);
                continue;
            }

            var fileContent = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
            var testFilePath = AgentUtilities.GetTestFilePath(projectRoot, filePath, testDir);

            var sysMsg = "You are a test-generation assistant. Generate unit tests for the given source code. Return ONLY the code, no explanations or markdown formatting.";
            var userMsg = new StringBuilder();
            userMsg.AppendLine($"Test framework: {testFramework}");
            userMsg.AppendLine($"Source file: {filePath}");
            if (existingContext.Length > 0)
            {
                userMsg.AppendLine();
                userMsg.AppendLine("Existing test files in the project (match style):");
                userMsg.Append(existingContext);
            }
            userMsg.AppendLine();
            userMsg.AppendLine("Source code to test:");
            userMsg.AppendLine(fileContent);
            userMsg.AppendLine();
            userMsg.AppendLine($"Generate a complete {testFramework} test file. Return ONLY the code.");

            var (raw, error) = await CallLlmRawText(sysMsg, userMsg.ToString(), ct,
                requestTimeout: TimeSpan.FromMinutes(5), maxTokens: 4096);

            if (error != null || string.IsNullOrWhiteSpace(raw))
            {
                await EmitLog(emitSse, "warn", $"TestCreation: LLM failed for {filePath}: {error}", ct: ct);
                continue;
            }

            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                var m = Regex.Match(cleaned, @"```(?:\w+)?\s*([\s\S]*?)```");
                if (m.Success) cleaned = m.Groups[1].Value.Trim();
            }

            var testFullDir = Path.GetDirectoryName(testFilePath);
            if (!string.IsNullOrWhiteSpace(testFullDir))
                Directory.CreateDirectory(testFullDir);

            await System.IO.File.WriteAllTextAsync(testFilePath, cleaned, Encoding.UTF8, ct);

            var relPath = Path.GetRelativePath(projectRoot, testFilePath);
            await EmitLog(emitSse, "success", $"Test file created: {relPath}", ct: ct);

            if (emitSse)
                await SendSse(Response, "step", new { type = "create", path = relPath, status = "created" }, ct);
        }
    }

    private async Task RunRepairPlan(
        string projectRoot, bool emitSse, CancellationToken ct,
        string prompt, string buildOutput, List<object> resultSteps,
        string? steeringContext = null)
    {
        var cfg9 = await LoadConfigAsync();
        await EmitLog(emitSse, "info", "RunRepairPlan: analyzing build errors…", ct: ct);
        if (emitSse)
            await SendSse(Response, "phase", new { phase = "repair", message = "Analyzing build errors and planning fixes…" }, ct);

        var tail = buildOutput.Length > cfg9.buildOutputTailChars ? buildOutput[^cfg9.buildOutputTailChars..] : buildOutput;
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

    private async Task<string?> AnalyzePreservationAndDependenciesAsync(
        PlanStep step, string projectRoot, string relPath, string? targetSymbol,
        string explorationContext, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetSymbol)) return null;


        var callSites = new List<string>();
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        var codeFiles = ext is ".cs" or ".ts" or ".tsx" or ".js" or ".jsx"
            ? Directory.EnumerateFiles(projectRoot, "*" + ext, SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\") && !f.Contains("\\node_modules\\"))
                .ToList()
            : new List<string>();

        foreach (var file in codeFiles)
        {
            try
            {
                var content = await System.IO.File.ReadAllTextAsync(file, ct);
                if (content.Contains(targetSymbol + "(") || content.Contains(targetSymbol + " ("))
                {
                    var rel = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
                    if (rel != relPath) callSites.Add(rel);
                }
            }
            catch { }
        }


        var fullPath = Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
        string? existingMethodBody = null;
        if (System.IO.File.Exists(fullPath))
        {
            var (oldStr, _) = AstResolveEdit(fullPath, "method", targetSymbol);
            if (!string.IsNullOrWhiteSpace(oldStr))
            {
                existingMethodBody = oldStr;
            }
        }

        if (existingMethodBody == null && callSites.Count == 0) return null;


        var sysPrompt =
            "You are a Code Preservation and Dependency Analysis Agent. " +
            "Your job is to analyze an existing method and a proposed change, then output a strict 'PRESERVATION DIRECTIVE'. " +
            "This directive will be fed to an Editor Agent to ensure it reshapes existing logic rather than inventing new logic or breaking dependencies.\n\n" +
            "Output ONLY valid JSON: " +
            "{\"preservationDirective\": \"...\", \"performanceNotes\": \"...\"}\n\n" +
            "In the directive, explicitly state:\n" +
            "1. Whether the method signature MUST be preserved (if there are call sites).\n" +
            "2. What existing logic must be retained (e.g., 'must still return a valid User object').\n" +
            "3. How the new logic should integrate with the old logic (e.g., 'add the new filter BEFORE the existing loop').";

        var sb = new StringBuilder();
        sb.AppendLine("## TASK CONTEXT");
        sb.AppendLine($"File: {relPath}");
        sb.AppendLine($"Proposed Change: {step.Change}");
        sb.AppendLine();

        if (existingMethodBody != null)
        {
            sb.AppendLine("## EXISTING METHOD IMPLEMENTATION (Target Symbol: " + targetSymbol + ")");
            sb.AppendLine("```");
            sb.AppendLine(existingMethodBody.Length > 2000 ? existingMethodBody[..2000] + "..." : existingMethodBody);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (callSites.Count > 0)
        {
            sb.AppendLine("## DEPENDENCIES / CALL SITES");
            sb.AppendLine($"This method is called in {callSites.Count} other file(s): {string.Join(", ", callSites.Take(5))}");
            sb.AppendLine("The method signature and return type MUST be preserved to avoid breaking these files.");
            sb.AppendLine();
        }

        var (raw, _, err) = await CallLlmRawStreaming(sysPrompt, sb.ToString(), emitSse, ct,
            requestTimeout: TimeSpan.FromSeconds(45), maxTokens: 512);

        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```")) { var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) cleaned = m.Groups[1].Value.Trim(); }
            using var doc = JsonDocument.Parse(cleaned);
            if (doc.RootElement.TryGetProperty("preservationDirective", out var pdEl))
            {
                var directive = pdEl.GetString();
                if (!string.IsNullOrWhiteSpace(directive))
                {
                    await EmitLog(emitSse, "info", $"  🛡️ Preservation Directive generated: {directive}", ct: ct);
                    return directive;
                }
            }
        }
        catch { }

        return null;
    }
    private async Task RunSelfImprovingPipeline(
        string prompt, string projectRoot, List<object> allSteps,
        AgentPlan? plan, bool complete, bool editsApplied)
    {
        var filePath = Path.Combine(projectRoot, "data/improvementdata.json");
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
    private async Task PersistMetaPlanToCardAsync(string? cardId, MetaPlanResult metaPlan, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardId) || metaPlan == null) return;
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

                    cardObj["_metaPlan"] = new JsonObject
                    {
                        ["summary"] = metaPlan.MetaSummary,
                        ["complexity"] = metaPlan.Complexity,
                        ["thinking"] = metaPlan.MetaThinking,
                        ["subPlans"] = new JsonArray(
                            metaPlan.SubPlans.Select(sp => new JsonObject
                            {
                                ["id"] = sp.Id,
                                ["title"] = sp.Title,
                                ["description"] = sp.Description,
                                ["files"] = JsonNode.Parse(JsonSerializer.Serialize(sp.Files ?? new List<string>())),
                                ["contextNote"] = sp.ContextNote,
                                ["done"] = false
                            }).ToArray()
                        )
                    };

                    var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await _boardData.SaveRawAsync(saved);
                    if (emitSse)
                    {
                        await SendSse(Response, "refresh", new { target = "boarddata", reason = "meta-plan-updated", cardId }, ct);
                    }
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to persist meta-plan to boarddata", new { cardId, error = ex.Message });
        }
    }

    private async Task UpdateMetaPlanSubPlanStatusAsync(string? cardId, string subPlanId, bool isDone, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardId) || string.IsNullOrWhiteSpace(subPlanId)) return;
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

                    if (cardObj["_metaPlan"] is JsonObject metaPlanObj &&
                        metaPlanObj["subPlans"] is JsonArray subPlansArr)
                    {
                        foreach (var sp in subPlansArr)
                        {
                            if (sp is JsonObject spObj && spObj["id"]?.GetValue<string>() == subPlanId)
                            {
                                spObj["done"] = isDone;
                                break;
                            }
                        }
                    }

                    var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await _boardData.SaveRawAsync(saved);
                    if (emitSse)
                    {
                        await SendSse(Response, "meta-plan-step-updated", new { subPlanId, done = isDone, cardId }, ct);
                        await SendSse(Response, "refresh", new { target = "boarddata", reason = "meta-plan-step-updated", cardId }, ct);
                    }
                    return;
                }
            }
        }
        catch { }
    }

    private static string? FindTypeDefinitionInContext(string typeName, string context)
    {
        if (string.IsNullOrWhiteSpace(context) || string.IsNullOrWhiteSpace(typeName))
            return null;
        var declPattern = @"(class|record|struct)\s+" + Regex.Escape(typeName) + @"\b";
        var decl = Regex.Match(context, declPattern);
        if (!decl.Success) return null;

        var startIdx = decl.Index;
        var braceStart = context.IndexOf('{', startIdx);
        if (braceStart < 0) return null;

        var depth = 0;
        var endIdx = -1;
        for (var i = braceStart; i < context.Length; i++)
        {
            if (context[i] == '{') depth++;
            else if (context[i] == '}') { depth--; if (depth == 0) { endIdx = i; break; } }
        }
        if (endIdx < 0) return null;

        return context[startIdx..(endIdx + 1)].Trim();
    }

    private static string ExtractTypeNameForLog(string classDef)
    {
        var m = Regex.Match(classDef, @"\b(class|record|struct)\s+([A-Za-z_][A-Za-z0-9_]*)");
        return m.Success ? m.Groups[2].Value : "?";
    }

    private static int CountRoslynErrors(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return 0;
        try
        {
            var tree = CSharpSyntaxTree.ParseText(content);
            return tree.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);
        }
        catch
        {
            return 0;
        }
    }
}