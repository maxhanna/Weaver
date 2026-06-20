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
            "8. NEVER use targetType='class' to add PROPERTIES or FIELDS. targetType='class' is for REPLACING an entire class or for adding entire METHODS via insertAfter. For adding a single property/field, use oldString/newString with a small anchor.\n" +
            "8b. oldString must be ≥ 20 characters total — short strings cause false matches\n" +
            "9. Use FORMAT A (array) whenever the content has multiple lines — it is more reliable and needs no escaping\n" +
            "10. Output ONLY the JSON — no markdown, no code fences, no introductory text\n" +
            "11. INDENTATION: newString MUST use the EXACT SAME leading whitespace as oldString for every line. Open-brace ({) increases indent for following lines. Close-brace (}) decreases indent. Copy the leading whitespace character-for-character from oldString into newString.\n" +
            "12. FORMAT C supported extensions:\n" +
                "REQUIRED (.cs): Roslyn AST — oldString/newString WILL fail for C#\n" +
                "SUPPORTED (regex): .ts .tsx .js .jsx .java .kt .scala .go .rs .swift .php .rb .dart .groovy\n" +
                "→ targetType: 'method'/'function'/'class'/'interface'/'property'\n" +
                "NOT SUPPORTED (use oldString/newString): .html .css .json .yaml .toml .xml .svg .md .sql .sh .py .lua .ex\n" +
            "12b. targetType='class': ONLY to REPLACE the ENTIRE class body. " +
                "To add a single property/field, use oldString/newString instead.\n" +
            "13. oldString STRICT LIMIT: MAXIMUM 10 lines — APPLIES ONLY to oldString/newString format." +
                "FORMAT C (targetType/targetName/newCode) is EXEMPT from this limit: the system extracts " +
                "the entire method body from the AST automatically. You SHOULD use FORMAT C for any " +
                "method longer than 10 lines. Do NOT truncate newCode to fit 10 lines — output the " +
                "complete replacement method body." +
            "14. To APPEND to the end of any file: oldString = last 2-3 closing braces only. Repeat them at the start of newString before your new code.\n" +
            "15. fullFile is ONLY for NEW files (files that don't exist yet). NEVER use fullFile for existing files.\n" +
            "16. REPLACE vs ADD: When the CHANGE REQUIRED description says \"instead of X, use Y\" or \"change X to use Y\" or \"display X in a popupPanel instead of inline\", you must REPLACE the existing X with Y — do NOT keep X and also add Y alongside it. You MUST modify the EXISTING section, not duplicate it.\n" +
            "17. BEFORE adding a new block/section, ALWAYS check whether an EXISTING section in the file already does what the change needs. If it does, MODIFY that section — don't add a new one.\n" +
            "18. If the change asks you to move something \"into a popupPanel\" or \"into a dialog\", find the EXISTING code that displays that thing inline, and make oldString span from its opening tag to its closing tag. Replace the ENTIRE block with the new popup/dialog version — do NOT keep the old block and also add a new one.\n" +
            "19. MODIFY the existing, don't ADD new alongside the existing. If you see duplicate functionality in newString (both old inline code AND new popup/dialog code), REMOVE the old inline part from newString.\n" +
            "20. NEVER INVENT type names. Every type (class/record/struct/interface) you reference in newString MUST already exist in the project. " +
                "The RELATED FILE CONTEXT / AUTO-ENRICHED CONTEXT sections show type definitions found across the project. " +
                "Use those existing types — do NOT rename them or create similar ones. " +
                "If you need a type not present in the context, define it fully (not as a stub) in the same edit. " +
            "21. SPACING — tokens concatenated without spaces are the #1 cause of bad edits. BEFORE outputting oldString/newString, read through EVERY line character-by-character and verify that every token boundary has the correct whitespace. Common errors: 'INTERVAL15 MINUTE' (should be 'INTERVAL 15 MINUTE'), 'font-size:12px' → 'font-size:12px' is OK but '12pximportant' → '12px important'. If you see two tokens running together without a space, fix it. After writing your output, re-read it and mentally say each space. " +
                "Before you write newString, first check the exploration context/file content for the actual property names, method names, and patterns used in THAT file. " +
                "For example, if you are converting an inline detail section to a popupPanel in an Angular component, look at EXISTING popupPanel instances in that same .html file — " +
                "use their exact class names (like `popupPanelTitle`, not `popupPanel-header`), and reference only existing properties/methods " +
                "(like `selectedCommand.command`, not `selectedCommand.title`; `cancelCommand()`, not `executeCommand()`). " +
                "Do NOT add new @Input/@Output bindings, new component properties, or new method calls unless they are EXPLICITLY required by the change description and you also add their definitions in the same edit." +
            "22. FORMAT C + INLINE SQL: When using FORMAT C to replace a C# method that contains" +
                "inline SQL (verbatim @\"...\" strings), you MUST copy the SQL verbatim from the file" +
                "into newCode.Do NOT reformat, re-indent, or \"clean up\" SQL inside @\"...\" strings —" +
                "every space inside the string literal is significant.The system will NOT normalize" +
                "whitespace inside verbatim strings.";

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
                    catch { /* swallow */ }
                });
            });
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
    /// <summary>Use Roslyn to find a C# AST node and return its exact source text as oldString.</summary>
    private (string? oldStr, string? error) AstResolveEdit(string fullPath, string targetType, string targetName, bool returnTail = false)
    {
        if (!System.IO.File.Exists(fullPath))
            return (null, "File not found for AST edit");

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        var sourceText = System.IO.File.ReadAllText(fullPath, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(sourceText))
            return (null, "File is empty");

        // ── Non-C#: regex-based resolution ─────────────────────────────────────────
        if (ext != ".cs")
        {
            // Build a prioritised list of patterns for the target language.
            // The first pattern that matches wins.
            var patterns = new List<(string label, Regex regex)>();

            if (string.Equals(targetType, "method", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(targetType, "function", StringComparison.OrdinalIgnoreCase))
            {
                // ── General: JS/TS/Java/PHP/C-family access-modifier prefix ───── 
                patterns.Add(("Method/function",
                    new Regex(
                        $@"^\s*(?:(?:async|export|default|public|private|protected|static|override|abstract|get|set|readonly)\s+)*(?:\w+(?:\[\])?(?:<[^>]*>)?\s+)?\b{Regex.Escape(targetName)}\s*(?:<[^>]*>)?\s*\([^)]*\)\s*(?::\s*[^{{;]+?)?\s*(?:{{|=>)",
                        RegexOptions.Multiline)));
                // ── Go: func [( receiver )] Name( ───────────────────────────────
                if (ext == ".go")
                    patterns.Add(("Go function",
                        new Regex(
                            $@"^\s*func\s+(?:\(\s*\w+\s+\*?\w+\s*\)\s+)?{Regex.Escape(targetName)}\s*\(",
                            RegexOptions.Multiline)));

                // ── Rust: [pub] [async|unsafe] fn Name ──────────────────────────
                if (ext == ".rs")
                    patterns.Add(("Rust fn",
                        new Regex(
                            $@"^\s*(?:pub(?:\([^)]+\))?\s+)?(?:async\s+)?(?:unsafe\s+)?fn\s+{Regex.Escape(targetName)}\s*[<(]",
                            RegexOptions.Multiline)));

                // ── Swift: func Name ─────────────────────────────────────────────
                if (ext == ".swift")
                    patterns.Add(("Swift func",
                        new Regex(
                            $@"^\s*(?:(?:public|private|internal|open|fileprivate|override|static|class|mutating|nonmutating|dynamic|final|lazy)\s+)*func\s+{Regex.Escape(targetName)}\s*[<(]",
                            RegexOptions.Multiline)));

                // ── Kotlin: fun Name ─────────────────────────────────────────────
                if (ext is ".kt" or ".kts")
                    patterns.Add(("Kotlin fun",
                        new Regex(
                            $@"^\s*(?:(?:public|private|protected|internal|override|abstract|open|inline|suspend|tailrec|operator|infix)\s+)*fun\s+{Regex.Escape(targetName)}\s*[<(]",
                            RegexOptions.Multiline)));

                // ── PHP: [modifiers] function Name ───────────────────────────────
                if (ext == ".php")
                    patterns.Add(("PHP function",
                        new Regex(
                            $@"^\s*(?:(?:public|private|protected|static|abstract|final)\s+)*function\s+{Regex.Escape(targetName)}\s*\(",
                            RegexOptions.Multiline)));

                // ── Ruby: def name ───────────────────────────────────────────────
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

            // ── Try patterns in order ────────────────────────────────────────────
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

            // ── Ruby: find matching `end` by indentation ─────────────────────────
            // Ruby uses def/end rather than braces, so we track indent level.
            if (ext == ".rb")
            {
                var defLine = sourceText[..startIdx].Split('\n')[^1];
                var defIndent = GetLeadingWhitespace(defLine);
                // Look for the first `end` at the same (or lesser) indentation after the def line
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

            // ── All other non-C# languages: use brace-depth matching ────────────
            // (Properties without bodies ending in ; need no brace matching)
            if (string.Equals(targetType, "property", StringComparison.OrdinalIgnoreCase) &&
                match.Value.TrimEnd().EndsWith(";"))
                return (match.Value, null);

            var openBraceIdx = sourceText.IndexOf('{', startIdx);
            if (openBraceIdx < 0)
                return (null, $"{label} '{targetName}' has no opening brace");

            // (The existing brace-matching loop continues unchanged from here...)
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
            // Check if this is a top-level statements file (C# 9+ Program.cs style)
            // — Roslyn synthesises the Main entry point; no MethodDeclarationSyntax named "Main" exists.
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

    /// <summary>
    /// Detects the base indentation of the original AST node (method/class) and
    /// normalizes the LLM's newCode to match. If the LLM outputs flat code
    /// (1-3 spaces) but the file uses 8+ spaces, this shifts the entire block
    /// to the correct base while preserving relative internal nesting.
    /// </summary>
    /// <summary>
    /// File extensions whose indentation IS the syntax (no braces to recover structure from).
    /// For these we must never re-indent by brace depth — doing so flattens the code.
    /// </summary>
    private static readonly HashSet<string> _whitespaceSignificantExts = new(StringComparer.OrdinalIgnoreCase)
{
    ".py", ".pyi", ".pyw",
    ".yaml", ".yml",
    ".coffee",
    ".haml", ".slim", ".pug", ".jade",
    ".fs", ".fsx", ".fsi",
    ".nim",
    ".sass",
    ".hs", ".lhs",          // Haskell
    ".elm",                 // Elm
    ".ml", ".mli",          // OCaml (off-side rule)
};
    // Languages that use keyword terminators (def/end, if/fi, etc.) instead of braces.
    // Brace-depth re-indentation would corrupt these just like whitespace-significant ones.
    private static readonly HashSet<string> _endKeywordLanguages = new(StringComparer.OrdinalIgnoreCase)
{
    ".rb",                              // Ruby:   def/end
    ".lua",                             // Lua:    function/end
    ".ex", ".exs",                      // Elixir: do/end
    ".sh", ".bash", ".zsh", ".fish",    // Shell:  if/fi, for/done, case/esac
};

    private static bool IsWhitespaceSignificant(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var ext = Path.GetExtension(filePath);
        // Both true indent-based languages AND keyword-terminated ones are unsafe for
        // brace-depth re-indentation — neither group uses braces to express structure.
        return _whitespaceSignificantExts.Contains(ext) || _endKeywordLanguages.Contains(ext);
    }
    /// <summary>
    /// Returns (family, supportsFormatC, llmHint) for a file extension.
    /// family: "brace" | "indent" | "end-keyword" | "tag" | "config" | "plain"
    /// supportsFormatC: whether the AstResolveEdit regex can target named symbols
    /// llmHint: the ⚠ line injected into the ResolveEditForStep prompt
    /// </summary>
    private static (string family, bool supportsFormatC, string llmHint) GetLanguageProfile(string ext)
    {
        return ext.ToLowerInvariant() switch
        {
            // ── C# (Roslyn AST) ────────────────────────────────────────────────────
            ".cs" => ("brace", true,
                "⚠ C# FILE: USE FORMAT C (targetType/targetName/newCode). " +
                "This is the ONLY supported format for C# files — oldString/newString WILL fail. " +
                "INDENTATION: method signature at class-member level, body indented 4 spaces more. " +
                "To ADD a method use insertAfter:true. To add a PROPERTY use oldString/newString."),

            // ── TypeScript / JavaScript ────────────────────────────────────────────
            ".ts" or ".tsx" => ("brace", true,
                "⚠ TS FILE: preserve ALL indentation exactly. Methods inside a class MUST be indented. " +
                "Preserve inline formatting: keep a space after colons in object literals ({key: value}) " +
                "and after commas in arrays/objects. " +
                "You can use FORMAT C (targetType='method', targetName='name') for full method replacements. " +
                "For small targeted edits (< 10 lines) prefer oldString/newString." +
                " Do NOT use targetType='class' — class REPLACE is blocked for .ts files. " +
                "Use insertAfter:true with targetType='method' to add methods, " +
                "or oldString/newString to add properties."),
            ".js" or ".jsx" => ("brace", true,
                "⚠ JS FILE: preserve ALL indentation exactly. " +
                "Preserve inline formatting: keep a space after colons in object literals ({key: value}) " +
                "and after commas in arrays/objects. " +
                "FORMAT C supported (targetType='function'/'method', targetName='name'). " +
                "For small edits prefer oldString/newString."),

            // ── JVM / CLR family (brace-based, regex-targeted) ────────────────────
            ".java" => ("brace", true,
                "⚠ JAVA FILE: brace-based, similar to C#. " +
                "FORMAT C supported: targetType='method'/'class'/'interface'. " +
                "Preserve ALL annotations (@Override, @Autowired, etc.) exactly. " +
                "NEVER alter generic type parameters or throws clauses."),
            ".kt" or ".kts" => ("brace", true,
                "⚠ KOTLIN FILE: brace-based. FORMAT C supported: targetType='function'/'class'. " +
                "Preserve data class properties, suspend/inline/override modifiers, and lambda syntax exactly."),
            ".scala" => ("brace", true,
                "⚠ SCALA FILE: brace-based. FORMAT C supported: targetType='method'/'class'/'object'. " +
                "Preserve implicits, case class syntax, and for-comprehension indentation exactly."),

            // ── Go ─────────────────────────────────────────────────────────────────
            ".go" => ("brace", true,
                "⚠ GO FILE: brace-based, uses TABS (not spaces) for indentation — never convert tabs to spaces. " +
                "FORMAT C supported: targetType='function', targetName='FunctionName'. " +
                "Preserve ALL error-handling idioms (if err != nil), defer statements, and goroutine patterns."),

            // ── Systems languages ──────────────────────────────────────────────────
            ".rs" => ("brace", true,
                "⚠ RUST FILE: brace-based. FORMAT C supported: targetType='function'/'impl'. " +
                "Preserve ALL lifetime annotations ('a), borrow markers (&, &mut), " +
                "ownership semantics, trait bounds, and match arm patterns EXACTLY."),
            ".c" or ".cpp" or ".cc" or ".cxx" or ".h" or ".hpp" => ("brace", false,
                "⚠ C/C++ FILE: brace-based, preprocessor directives must stay on their own line. " +
                "Use oldString/newString. Preserve #include order, extern \"C\", " +
                "template parameters, and pointer/reference syntax exactly."),
            ".swift" => ("brace", true,
                "⚠ SWIFT FILE: brace-based. FORMAT C supported: targetType='function'/'class'/'struct'. " +
                "Preserve access modifiers (open/public/internal/fileprivate/private), " +
                "property wrappers (@State, @Binding), and optional chaining exactly."),

            // ── Scripting / dynamic ────────────────────────────────────────────────
            ".php" => ("brace", true,
                "⚠ PHP FILE: brace-based. FORMAT C supported: targetType='function'/'method'/'class'. " +
                "Preserve $ sigils on all variables, type hints, and nullable ? modifiers exactly."),
            ".dart" => ("brace", true,
                "⚠ DART FILE: brace-based. FORMAT C supported: targetType='function'/'class'. " +
                "Preserve async/await, null-safety operators (?., ??, ??=, !), and Widget tree indentation."),
            ".groovy" => ("brace", true,
                "⚠ GROOVY FILE: brace-based (Gradle/Groovy DSL). FORMAT C supported: targetType='method'. " +
                "Preserve closure syntax { ... }, GString interpolation, and Gradle DSL patterns."),

            // ── End-keyword languages (def/end, if/fi, function/end) ──────────────
            ".rb" => ("end-keyword", true,
                "⚠ RUBY FILE: uses def/end, do/end, class/end block terminators — NOT braces. " +
                "FORMAT C supported: targetType='method', targetName='method_name' (snake_case). " +
                "Use oldString/newString for small edits. " +
                "Preserve Ruby idioms: ||=, &., symbol literals, and block/proc/lambda syntax."),
            ".lua" => ("end-keyword", false,
                "⚠ LUA FILE: uses function/end, if/end, for/end block terminators — NOT braces. " +
                "Use oldString/newString only. Preserve Lua table syntax, colon-method calls (:), " +
                "and global vs local variable scoping."),
            ".ex" or ".exs" => ("end-keyword", false,
                "⚠ ELIXIR FILE: uses do/end block terminators and pipe operators |>. " +
                "Use oldString/newString. Preserve pattern matching, atoms (:name), " +
                "and module attribute syntax (@doc, @spec)."),
            ".sh" or ".bash" or ".zsh" or ".fish" => ("end-keyword", false,
                "⚠ SHELL SCRIPT: uses if/fi, for/done, while/done, case/esac terminators. " +
                "Use oldString/newString. Preserve $() vs ``, quoting rules, " +
                "and test [ ] vs [[ ]] distinctions exactly."),
            ".ps1" or ".psm1" or ".psd1" => ("brace", false,
                "⚠ POWERSHELL FILE: brace-based, $Variables, -Flags syntax. " +
                "Use oldString/newString. Preserve $_ pipeline variable, " +
                "cmdlet verb-noun naming, and parameter attribute syntax."),

            // ── Whitespace-significant / indent-based ─────────────────────────────
            ".py" or ".pyi" => ("indent", false,
                "⚠ PYTHON FILE: indentation IS the syntax — do NOT alter indent levels. " +
                "Use oldString/newString only. FORMAT C is NOT supported. " +
                "Copy every leading space/tab from the file exactly into oldString and newString. " +
                "Preserve type hints, decorators (@), and docstring quotes."),
            ".yaml" or ".yml" => ("indent", false,
                "⚠ YAML FILE: whitespace-significant. Use oldString/newString only. " +
                "NEVER change indentation levels — copy exactly. " +
                "Preserve anchors (&), aliases (*), and multiline block styles (|, >)."),
            ".fs" or ".fsx" or ".fsi" => ("indent", false,
                "⚠ F# FILE: whitespace-significant (offside rule). Use oldString/newString only. " +
                "Preserve pipeline |> operators, computation expressions, and discriminated union syntax."),
            ".hs" or ".lhs" => ("indent", false,
                "⚠ HASKELL FILE: whitespace-significant. Use oldString/newString only. " +
                "Preserve do-notation alignment, type class instances, and where-clause indentation."),
            ".coffee" => ("indent", false,
                "⚠ COFFEESCRIPT FILE: whitespace-significant, no braces. Use oldString/newString only."),

            // ── Tag / markup ───────────────────────────────────────────────────────
            ".html" or ".htm" => ("tag", false,
                "⚠ HTML FILE: tag-based indentation — child elements MUST be indented more than parent. " +
                "Use oldString/newString. Preserve attribute quoting, void element self-closing, " +
                "and Angular/Vue directive syntax exactly."),
            ".xml" or ".xaml" or ".axaml" => ("tag", false,
                "⚠ XML FILE: tag-based. Use oldString/newString. " +
                "Preserve namespace prefixes (xmlns:), attribute order, and CDATA sections."),
            ".cshtml" or ".razor" => ("tag", false,
                "⚠ RAZOR FILE: HTML with @C# expressions. Use oldString/newString. " +
                "Preserve @model, @inject, @Html.* helpers, and @{ } code blocks exactly."),
            ".vue" => ("tag", true,
                "⚠ VUE FILE: <template>/<script>/<style> sections. " +
                "FORMAT C supported for methods inside <script>. " +
                "For template changes use oldString/newString. Preserve v-bind/:, v-on/@, v-model directives."),
            ".svelte" => ("tag", false,
                "⚠ SVELTE FILE: <script>/<style>/template sections. Use oldString/newString. " +
                "Preserve $: reactive declarations, {#if}, {#each} blocks, and slot syntax."),
            ".svg" => ("tag", false,
                "⚠ SVG FILE: XML tag-based. Use oldString/newString. " +
                "Preserve viewBox, transform attributes, and path d= values exactly."),

            // ── Stylesheets ────────────────────────────────────────────────────────
            ".css" or ".scss" or ".less" => ("brace", false,
                "⚠ CSS/SCSS/LESS FILE: brace-based selectors. Use oldString/newString. " +
                "CRITICAL: oldString MUST be at most 4 lines — never replace an entire CSS block. " +
                "To change a CSS property value, set oldString to the ONE line containing that property " +
                "(copied verbatim from the file), and newString to that line with the new value. " +
                "Example: if changing `flex-direction: row;` to `flex-direction: column;`, " +
                "oldString = \"  flex-direction: row;\" (exact whitespace), newString = \"  flex-direction: column;\". " +
                "Preserve ALL whitespace in property values (e.g. '0 1px 2px rgba(0,0,0,0.5)' — " +
                "every space and comma is significant). Preserve SCSS variables ($var), mixins, and nesting."),

            // ── Config / data ──────────────────────────────────────────────────────
            ".json" => ("config", false,
                "⚠ JSON FILE: strict syntax — use oldString/newString only. " +
                "NO trailing commas, NO comments. Preserve ALL nested object structure exactly. " +
                "When editing arrays, include the full surrounding element for uniqueness."),
            ".toml" => ("config", false,
                "⚠ TOML FILE: use oldString/newString. Preserve [section] headers, " +
                "[[array-of-tables]], and inline table {key=val} syntax exactly."),
            ".env" or ".ini" => ("config", false,
                "⚠ CONFIG FILE: key=value pairs. Use oldString/newString. " +
                "Preserve comment lines (#) and section headers ([section]) exactly."),
            ".proto" => ("brace", false,
                "⚠ PROTOBUF FILE: brace-based. Use oldString/newString. " +
                "Preserve field numbers, oneof blocks, and option statements exactly."),

            // ── Query / data ───────────────────────────────────────────────────────
            ".sql" => ("plain", false,
                "⚠ SQL FILE: use oldString/newString. Preserve ALL whitespace in multi-line queries. " +
                "Match exact keyword casing (uppercase SQL keywords are conventional). " +
                "Preserve semicolons and comment styles (-- vs /* */)."),
            ".graphql" or ".gql" => ("plain", false,
                "⚠ GRAPHQL FILE: use oldString/newString. Preserve type definitions, " +
                "field arguments, and directive (@deprecated, @skip) syntax exactly."),

            // ── Documentation ──────────────────────────────────────────────────────
            ".md" or ".mdx" => ("plain", false,
                "⚠ MARKDOWN FILE: use oldString/newString. " +
                "Preserve heading levels (# vs ##), list markers (-, *, 1.), " +
                "and fenced code block language tags exactly."),
            ".rst" => ("indent", false,
                "⚠ RST FILE: indentation-significant section underlines. Use oldString/newString. " +
                "Preserve directive syntax (.. directive::) and role syntax (:role:`text`)."),

            // ── Default ────────────────────────────────────────────────────────────
            _ => ("plain", false,
                "⚠ Preserve ALL indentation and whitespace exactly as shown in the file. " +
                "Use oldString/newString. Copy every leading space/tab character-for-character.")
        };
    }
    /// <summary>
    /// Infers the indentation unit (a tab, or N spaces) used by the surrounding source so
    /// re-indentation matches the file's own convention instead of a hardcoded width.
    /// </summary>
    private static string DetectIndentUnit(string source)
    {
        var lines = source.Split('\n');
        foreach (var line in lines)
            if (line.Length > 0 && line[0] == '\t') return "\t";
        var min = int.MaxValue;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var n = 0;
            while (n < line.Length && line[n] == ' ') n++;
            if (n > 0 && n < min) min = n;
        }
        return new string(' ', min is > 0 and < int.MaxValue ? min : 4);
    }

    /// <summary>Strips existing indent and re-indents code to the given indent level.</summary>
    private static string ReindentToLevel(string code, string indent)
    {
        if (string.IsNullOrEmpty(code)) return code;
        var lines = code.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
                lines[i] = indent + lines[i].TrimStart();
        }
        return string.Join("\n", lines);
    }

    /// <summary>Removes the class declaration wrapper (e.g. "export class Foo {") 
    /// and trailing "}" from partial class snippets so only body content remains.</summary>
    private static string StripClassWrapper(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return code;
        var lines = code.Split('\n').ToList();
        // Remove leading "export class X {" or "class X {" lines
        while (lines.Count > 0)
        {
            var trimmed = lines[0].Trim();
            if (trimmed.Length == 0 ||
                Regex.IsMatch(trimmed, @"^(export\s+)?(default\s+)?(abstract\s+)?class\s+\w+"))
            {
                lines.RemoveAt(0);
            }
            else break;
        }
        // Remove trailing closing braces and blank lines
        while (lines.Count > 0)
        {
            var trimmed = lines[^1].Trim();
            if (trimmed == "}" || trimmed.Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }
            else break;
        }
        return string.Join("\n", lines);
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
        // indent level), the LLM flattened the structure. Re-indent by brace depth — but
        // ONLY for brace-based languages. For whitespace-significant languages (Python,
        // YAML, F#, …) there are no braces to recover from, so brace-reindent would
        // collapse everything to the base indent. There we keep the rebased code as-is.
        var shiftedLines = shifted.Split('\n');
        var distinctIndents = shiftedLines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => Regex.Match(l, @"^(\s*)").Groups[1].Length)
            .Distinct()
            .ToList();
        if (distinctIndents.Count <= 1
            && !IsWhitespaceSignificant(filePath)
            && !shifted.Contains("@\"", StringComparison.Ordinal)
            && !shifted.Contains("\"\"\"", StringComparison.Ordinal))
            return ReindentByBraceDepth(shifted, baseIndent, DetectIndentUnit(oldSource));

        return shifted;
    }

    /// <summary>
    /// Re-indents code by tracking brace depth, using baseIndent as the starting
    /// indentation. Accounts for strings and comments to avoid false brace matches.
    /// </summary>
    private static string ReindentByBraceDepth(string code, string baseIndent, string indentUnit = "  ")
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

            var indent = baseIndent + string.Concat(Enumerable.Repeat(indentUnit, effectiveDepth));
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
      string? fullContent, bool alreadyDone, string? error, bool fromFormatC)>
      ResolveEditForStep(PlanStep step, string projectRoot, bool emitSse,
          CancellationToken ct,
          List<(string old, string @new, string error)>? history = null,
          string? explorationContext = null,
          string? targetSymbol = null)
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
        sb.AppendLine($"FILE: {relPath}");
        sb.AppendLine($"CHANGE REQUIRED: {step.Change}");
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
                        "`this.youtubeResults` locally without calling `searchUrl`.");


        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        var (langFamily, langSupportsFormatC, langHint) = GetLanguageProfile(ext);
        sb.AppendLine(langHint);

        // For top-level statement .cs files the language hint above is wrong —
        // FORMAT C needs named AST nodes that don't exist in these files.
        // Override it here before the LLM reads the file content.
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
            catch { /* non-critical — Roslyn parse errors fall through */ }
        }

        var lineCount = fileContent.Split('\n').Length;
        var isLarge = fileContent.Length > 3000 || lineCount > 80;
        // Large-file instruction depends on whether FORMAT C is available
        if (isLarge)
        {
            if (langSupportsFormatC && ext != ".cs")
                sb.AppendLine("⚠ Large file — prefer FORMAT C (targetType/targetName) to avoid fragile text matching.");
            else if (ext != ".cs")
                sb.AppendLine("⚠ Large file — use a tight oldString (3–6 lines max). " +
                              "The excerpt above is the ONLY portion shown; your oldString MUST appear in it.");
        }
        else if (ext is ".css" or ".scss" or ".sass")
            sb.AppendLine("⚠ CSS FILE: preserve ALL whitespace in property values exactly " +
                          "(e.g. '0px 1px' must stay as two tokens with a space).");
        else if (ext is ".ts" or ".tsx" or ".js" or ".jsx")
            sb.AppendLine("⚠ TS/JS FILE: preserve ALL indentation exactly — " +
                          "methods inside a class body MUST be indented, nested blocks " +
                          "must be indented relative to their parent. Copy the leading " +
                          "whitespace from oldString character-for-character into newString.");
        else if (ext == ".cs")
            sb.AppendLine("⚠ C# FILE: Choose your edit format based on change SIZE.");
        sb.AppendLine("  • For SMALL targeted changes (1-5 lines, e.g. add a column to SQL, add a property, change a return value):");
        sb.AppendLine("    USE oldString/newString. Copy 2-3 lines verbatim from the file as oldString.");
        sb.AppendLine("    Include the line above and below your change as anchor context, repeating them unchanged in newString.");
        sb.AppendLine("  • For FULL method/class replacements (entire method body rewrite):");
        sb.AppendLine("    USE FORMAT C (targetType/targetName/newCode). AST-based, bypasses text matching.");
        sb.AppendLine("    CRITICAL: Preserve the existing attribute(s), return type, method name, and parameter list VERBATIM from the file.");
        sb.AppendLine("    Only change the method BODY. Do NOT change [FromBody], route templates, or parameter names.");
        sb.AppendLine("  • To ADD a new method: use insertAfter:true with targetType=\"method\" and targetName of an existing method.");
        sb.AppendLine("  • Do NOT use targetType=\"class\" — that replaces the entire class.");
        sb.AppendLine("INDENTATION: newCode MUST include proper C# indentation.");
        sb.AppendLine("SQL STRINGS: PRESERVE exact whitespace inside SQL. 'INTERVAL 15 MINUTE' is CORRECT; " +
                      "'INTERVAL15 MINUTE' is WRONG. '= 1' is CORRECT; '=1' is WRONG. " +
                      "Copy SQL lines VERBATIM from the file and only change what the task requires.");
        sb.AppendLine();

        //  Include exploration context if available — this is the most important
        //  signal the editor gets; the LLM should read it before the file content
        if (!string.IsNullOrWhiteSpace(explorationContext))
        {
            var distilled = DistillExplorationContext(
                explorationContext, relPath, step.Change, targetSymbol);
            if (!string.IsNullOrWhiteSpace(distilled))
            {
                sb.AppendLine();
                sb.AppendLine("## RELATED FILE CONTEXT");
                sb.AppendLine("Types, interfaces, and relevant code from files read during exploration " +
                              "(target file is shown above; these are supporting files only):");
                sb.AppendLine(distilled);
                sb.AppendLine();
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
                sb.AppendLine(AgentUtilities.ExtractRelevantExcerpt(fileContent, step.Change, step.OldString, cfg5.fileBodyTruncationChars));
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

        // Always encourage small, focused oldStrings
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

                    // Check if a prior attempt wrote genuinely different code (but had the wrong signature)
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
                            var returnLine = FindLastReturnLine(priorSqlError.old);
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
                else if (!string.IsNullOrWhiteSpace(h.old))
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
                else if (h.error.Contains("FORMAT C failed", StringComparison.OrdinalIgnoreCase) || h.error.Contains("not found in file", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("  You used FORMAT C but the symbol was not found. " +
                                  "This file has no named methods/classes for FORMAT C to target. " +
                                  "Switch to oldString/newString: copy the EXACT lines from the file content, " +
                                  "verbatim including indentation, and set them as oldString.");
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
            return (null, null, false, null, false, "LLM returned empty response", false);

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
                return (null, null, false, null, true, null, false);
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
                            lines.Add(UnescapeString(item.GetString() ?? ""));
                    }
                    if (lines.Count > 0) body = string.Join("\n", lines);
                }
                if (!string.IsNullOrWhiteSpace(body))
                {
                    body = StripFullFileFence(body);
                    return (null, null, true, body, false, null, false);
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
                        ? string.Join("\n", ncEl.EnumerateArray().Select(e => UnescapeString(e.GetString() ?? "")))
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
                            return (null, null, false, null, false,
                                $"FORMAT C failed: targetType='{targetType}', targetName='{targetName}' — {astErr ?? "symbol not found in file"}", false);

                        if (string.Equals(targetType, "class", StringComparison.OrdinalIgnoreCase))
                        {
                            // For classes, insert new members inside the body (before closing })
                            var unit = DetectIndentUnit(fullStr);
                            var hasClassDecl = newCodeStr.Contains("class ", StringComparison.OrdinalIgnoreCase);
                            var body = hasClassDecl ? StripClassWrapper(newCodeStr) : newCodeStr;
                            var bodyIndented = ReindentToLevel(body, unit);
                            var lastBrace = fullStr.LastIndexOf('}');
                            if (lastBrace >= 0)
                            {
                                newStr = fullStr[..lastBrace].TrimEnd() + "\n" + bodyIndented + "\n" + fullStr[lastBrace..];
                                return (fullStr, newStr, false, null, false, null, true);
                            }
                        }

                        var indented = AutoIndentCode(fullStr, newCodeStr, relPath);
                        newStr = fullStr + "\n" + indented;
                        return (fullStr, newStr, false, null, false, null, true);
                    }
                    else
                    {
                        // REPLACE mode: find the full node and replace it entirely
                        var (astOldStr, astErr) = AstResolveEdit(fullPath, targetType, targetName, returnTail: false);
                        if (astOldStr != null)
                        {
                            // When targetType="class" is used WITHOUT a full class declaration
                            // in newCode, the LLM is incorrectly trying to insert properties
                            // via targetType. Reject this — the LLM must use oldString/newString
                            // for property additions, or include the FULL class in newCode
                            // for full-class replacements.
                            var isClassTarget = string.Equals(targetType, "class", StringComparison.OrdinalIgnoreCase);
                            var hasClassDecl = newCodeStr.Contains("class ", StringComparison.OrdinalIgnoreCase);
                            if (isClassTarget && !hasClassDecl)
                            {
                                var codeLineCount = newCodeStr.Split('\n').Length;
                                return (null, null, false, null, false,
                                    $"targetType 'class' used without a full class declaration in newCode ({codeLineCount} lines). " +
                                    "targetType='class' is ONLY for replacing the ENTIRE class — newCode must contain 'class ClassName {{'. " +
                                    "For adding properties/fields, use oldString/newString format instead: " +
                                    "set oldString to the last 1-2 existing lines before the class closing brace, " +
                                    "and newString to those lines plus the new property line.", false);
                            }
                            if (isClassTarget)
                            {
                                // For .ts/.js and other non-C# files: full-class REPLACE is unsafe.
                                // The old merge formula appended the new body to the existing one (doubling
                                // every member). LLM output is also frequently truncated mid-class.
                                // Force the LLM to use insertAfter or oldString/newString instead.
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

                                // C# only: strip the class declaration wrapper so newCode body
                                // REPLACES the existing body rather than being appended to it.
                                var body = hasClassDecl ? StripClassWrapper(newCodeStr) : newCodeStr;
                                if (!string.IsNullOrWhiteSpace(body))
                                {
                                    var unit = DetectIndentUnit(astOldStr);
                                    var bodyIndented = ReindentToLevel(body, unit);
                                    var lastBrace = astOldStr.LastIndexOf('}');
                                    var openBrace = astOldStr.IndexOf('{');
                                    if (lastBrace >= 0 && openBrace >= 0 && openBrace < lastBrace)
                                    {
                                        // FIXED: classHeader + new body + }, not old-body + new body + }
                                        var classHeader = astOldStr[..(openBrace + 1)];
                                        var mergedStr = classHeader.TrimEnd() + "\n" + bodyIndented.TrimEnd() + "\n" + astOldStr[lastBrace..];
                                        return (astOldStr, mergedStr, false, null, false, null, true);
                                    }
                                }
                            }

                            // Format newCode with Roslyn BEFORE AutoIndentCode so that
                            // the indentation adjustment shifts from a normalized base
                            // (0 indent for root, 4 spaces per level) to the file's level
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

            // HARD ENFORCEMENT: oldString must be ≤ 15 lines (array format adds a few blank lines)
            if (oldStr != null && oldStr.Split('\n').Length > 15)
            {
                var lineCounts = oldStr.Split('\n').Length;
                return (null, null, false, null, false,
                    $"oldString is {lineCounts} lines long — STRICT MAXIMUM is 10 lines. " +
                    "You MUST use FORMAT C (targetType='method', targetName='MethodName') for methods longer than 10 lines. " +
                    "If FORMAT C failed because the method name is wrong, look at the file content, find the exact method name, and use FORMAT C with the correct name. " +
                    "Do NOT output a massive oldString.", false);
            }

            if (!string.IsNullOrWhiteSpace(oldStr))
                return (oldStr, newStr ?? "", false, null, false, null, false);

            return (null, null, false, null, false, "JSON has no oldString, targetType, fullFile, or alreadyDone field", false);
        }
        catch
        {
            // Fallback: legacy delimiter format
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

                    // HARD ENFORCEMENT in fallback parser too
                    if (oldStr.Split('\n').Length > 15)
                    {
                        return (null, null, false, null, false,
                            $"oldString is {oldStr.Split('\n').Length} lines long — STRICT MAXIMUM is 10 lines. " +
                            "You MUST use FORMAT C (targetType='method', targetName='MethodName') for methods longer than 10 lines.", false);
                    }

                    return (oldStr, newStr ?? "", false, null, false, null, false);
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
                return (oldStr, newStr ?? "", false, null, false, null, false);
            }

            // FORMAT C fallback: extract targetType/targetName/newCode from malformed JSON
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

            if (string.IsNullOrWhiteSpace(oldStr))
                return (null, null, false, null, false, "OLD section is empty", false);

            return (oldStr, newStr, false, null, false, null, false);

        }
    }

    private enum PreEditVerdict { Proceed, AlreadyDone, Irrelevant }

    /// <summary>
    /// Quick pre-edit validation: checks if the edit is still relevant before
    /// calling the LLM. Verifies oldString still exists, newString isn't already
    /// present, and the edit makes sense given the current file content.
    /// </summary>
    private static readonly string[] _verifyPrefixes = {
        "ensure", "verify", "make sure", "confirm", "validate",
        "check", "guarantee", "see if", "determine if", "review"
    };

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

            var collapsedNew = CollapseWhitespace(newStr);
            if (collapsedNew.Length >= 15 &&
                CollapseWhitespace(content).Contains(collapsedNew, StringComparison.Ordinal))
                return (PreEditVerdict.AlreadyDone, "code already present in file (whitespace differences only)");
        }

        // Verification-only step: change description uses passive language,
        // oldString already exists, and no newString means no actual change.
        // The planner should not have created this step, but if it slipped
        // through, skip it instead of entering a no-op retry loop.
        if (string.IsNullOrWhiteSpace(step.NewString) && !string.IsNullOrWhiteSpace(step.OldString))
        {
            var changeLower = (step.Change ?? "").Trim().ToLowerInvariant();
            if (_verifyPrefixes.Any(p => changeLower.StartsWith(p)))
            {
                var oldStr = AgentUtilities.NormalizeLineEndings(step.OldString);
                if (content.Contains(oldStr, StringComparison.Ordinal))
                    return (PreEditVerdict.AlreadyDone, "step is verification-only — code already present");
            }
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

    /// <summary>
    /// Plan Pre-Audit: checks each plan step against current file content via LLM
    /// to detect (a) steps where the change is already present, and (b) steps that
    /// combine multiple distinct changes and should be decoupled into smaller edits.
    /// Runs AFTER plan validation but BEFORE execution.
    /// </summary>
    private async Task<PlanAuditResult?> PlanPreAuditAsync(
        AgentPlan plan, string projectRoot, bool emitSse,
        CancellationToken ct, string? originalPrompt = null)
    {
        if (plan?.Plan == null || plan.Plan.Count == 0) return null;

        var auditSteps = new List<AuditPlanStepResult>();
        var sb = new StringBuilder();

        sb.AppendLine("You are auditing a code-change plan BEFORE execution. Your job: detect problems that would waste time or cause bugs.");
        sb.AppendLine();
        sb.AppendLine("For EACH step in the plan, examine the target file's content and determine:");
        sb.AppendLine("1. alreadyDone = true/false — Is the proposed change ALREADY present in the file?");
        sb.AppendLine("   Example: if step says \"Add CalendarNotificationsEnabled property to UserSettings\"");
        sb.AppendLine("   but the file ALREADY has `public bool CalendarNotificationsEnabled { get; set; } = true;`,");
        sb.AppendLine("   then alreadyDone = true.");
        sb.AppendLine("2. needsDecoupling = true/false — Does this step combine TWO OR MORE distinct");
        sb.AppendLine("   changes that should be separate steps? Example: \"Add X property AND implement Y toggle\"");
        sb.AppendLine("   should be two steps: one for the property, one for the toggle.");
        sb.AppendLine("   CRITICAL: MOVING content is TWO changes: add at new location + remove from old location.");
        sb.AppendLine("   Example: \"Move the link button from the action column into the todo text cell\"");
        sb.AppendLine("   needsDecoupling = true. decoupledSteps = [");
        sb.AppendLine("     { file: \"...\", change: \"Append link button after the todo text content\" },");
        sb.AppendLine("     { file: \"...\", change: \"Remove link button from the action column\" }");
        sb.AppendLine("   ]");
        sb.AppendLine("3. If needsDecoupling, provide decoupledSteps as an array of {file, change} objects.");
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
                    sb.AppendLine("TARGET FILE CONTENT:");
                    sb.AppendLine("```");
                    if (content.Length > 6000)
                        sb.AppendLine(content[..6000] + $"\n... (truncated, full file is {content.Length} chars)");
                    else
                        sb.AppendLine(content);
                    sb.AppendLine("```");
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
            sb.ToString(), ct, TimeSpan.FromSeconds(60), maxTokens: 2048);

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
            if (!root.TryGetProperty("steps", out var stepsArr) || stepsArr.ValueKind != JsonValueKind.Array)
            {
                await EmitLog(emitSse, "warn", "Plan audit response missing 'steps' array", ct: ct);
                return null;
            }

            foreach (var stepEl in stepsArr.EnumerateArray())
            {
                var idx = stepEl.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number
                    ? idxEl.GetInt32() : -1;
                if (idx < 0 || idx >= plan.Plan.Count) continue;

                var alreadyDone = stepEl.TryGetProperty("alreadyDone", out var adEl) && adEl.GetBoolean();
                var needsDecoupling = stepEl.TryGetProperty("needsDecoupling", out var ndEl) && ndEl.GetBoolean();
                var reason = stepEl.TryGetProperty("reason", out var rEl) ? rEl.GetString() : null;

                List<PlanStep>? decoupled = null;
                if (needsDecoupling && stepEl.TryGetProperty("decoupledSteps", out var dcArr) && dcArr.ValueKind == JsonValueKind.Array)
                {
                    decoupled = new List<PlanStep>();
                    foreach (var dc in dcArr.EnumerateArray())
                    {
                        var dcFile = dc.TryGetProperty("file", out var fEl) ? fEl.GetString() ?? plan.Plan[idx].File : plan.Plan[idx].File;
                        var dcChange = dc.TryGetProperty("change", out var cEl) ? cEl.GetString() ?? plan.Plan[idx].Change : plan.Plan[idx].Change;
                        if (!string.IsNullOrWhiteSpace(dcChange) && dcChange != plan.Plan[idx].Change)
                        {
                            decoupled.Add(new PlanStep
                            {
                                File = dcFile,
                                Change = dcChange,
                                Priority = plan.Plan[idx].Priority,
                                ReferenceFiles = plan.Plan[idx].ReferenceFiles
                            });
                        }
                    }
                    if (decoupled.Count == 0)
                    {
                        // LLM didn't produce usable sub-steps — ignore decoupling request
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
        public string? LowConfidenceWarning { get; init; }
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
        string? cardId = null,
        List<string>? attachedFiles = null)
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
        const int ConfidenceThreshold = 80; // stop early once the LLM is this confident
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

        // ── Canonical relative-path helper (Patch 7) ──────────────────────
        // Returns the path as a project-relative string with forward slashes,
        // e.g. "src/app/grandtheft/grandtheft-renderer.ts". Falls back to the
        // input (with backslashes -> forward slashes) if path resolution fails.
        // Used by AddFileRead() so filesRead never accumulates duplicate
        // entries for the same file in different string forms.
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

        // Add a file to both filesRead (canonical relative form) and
        // normalizedPaths (absolute form). Returns true if added, false if
        // already present. ALWAYS use this instead of filesRead.Add() /
        // normalizedPaths.Add() directly so the two collections stay in sync
        // and never contain duplicate entries for the same file.
        bool AddFileRead(string path)
        {
            var rel = RelNormalize(path);
            var abs = AbsNormalize(path);
            var addedToFiles = filesRead.Add(rel);
            normalizedPaths.Add(abs);
            return addedToFiles;
        }


        // ── Seed exploration with attached file content ───────────────────
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
                ? AgentUtilities.ExtractRelevantExcerpt(content, step.Change, step.OldString, cfg4.fileBodyTruncationChars)
                : content;

            ctx.AppendLine($"### TARGET FILE: {relPath}  ({content.Length:N0} chars total)");
            ctx.AppendLine("```");
            ctx.AppendLine(excerpt);
            ctx.AppendLine("```");
            ctx.AppendLine();
            AddFileRead(relPath);
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
                // Dedup against BOTH the absolute form (normalizedPaths) and
                // the canonical relative form (filesRead, which now stores
                // RelNormalize output). Either match means we've already read
                // this file — skip it.
                if (normalizedPaths.Contains(AbsNormalize(requested)) ||
                    filesRead.Contains(RelNormalize(requested))) continue;

                var fp = Path.GetFullPath(
                    Path.Combine(projectRoot, requested.Replace('/', Path.DirectorySeparatorChar)));

                if (!System.IO.File.Exists(fp) ||
                    !AgentUtilities.IsPathUnderRoot(fp, projectRoot))
                {
                    // Search for the file by filename in the project
                    var fileName = Path.GetFileName(requested.Replace('/', '\\'));
                    var matches = new List<string>();
                    try
                    {
                        matches = Directory.EnumerateFiles(projectRoot, fileName, SearchOption.AllDirectories)
                            .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/'))
                            .Where(rel => AgentUtilities.IsPathUnderRoot(
                                Path.GetFullPath(Path.Combine(projectRoot, rel.Replace('/', Path.DirectorySeparatorChar))), projectRoot))
                            .Take(5)
                            .ToList();
                    }
                    catch { }

                    if (matches.Count == 1)
                    {
                        // Exactly one match — read it and tell the LLM the correct path
                        var correctPath = matches[0];
                        var matchFull = Path.GetFullPath(Path.Combine(projectRoot, correctPath.Replace('/', Path.DirectorySeparatorChar)));
                        var matchContent = await System.IO.File.ReadAllTextAsync(matchFull, Encoding.UTF8, ct);
                        var matchExcerpt = matchContent.Length > 3_500 ? AgentUtilities.ExtractRelevantExcerpt(matchContent, step.Change, step.OldString, cfg4.fileBodyTruncationChars) : matchContent;
                        if (ctx.Length + matchExcerpt.Length <= MaxContextChars)
                        {
                            ctx.AppendLine($"### {correctPath}  (resolved from `{requested}`)");
                            ctx.AppendLine("```");
                            ctx.AppendLine(matchExcerpt);
                            ctx.AppendLine("```");
                            ctx.AppendLine();
                        }
                        else
                        {
                            ctx.AppendLine($"⚠ `{requested}` resolved to `{correctPath}` (skipped — context budget exhausted)");
                            ctx.AppendLine();
                        }
                        AddFileRead(correctPath);
                        await EmitLog(emitSse, "info",
                            $"  🔍 {requested} → {correctPath}", ct: ct);
                    }
                    else if (matches.Count > 1)
                    {
                        var suggestions = string.Join(", ", matches.Select(m => $"`{m}`"));
                        ctx.AppendLine($"⚠ `{requested}` not found. Possible matches: {suggestions}. Use an exact relative path from the project root.");
                        ctx.AppendLine();
                        await EmitLog(emitSse, "warn",
                            $"  ⚠ Not found: {requested} — found {matches.Count} candidates: {string.Join(", ", matches)}", ct: ct);
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
                    ? AgentUtilities.ExtractRelevantExcerpt(fc, step.Change, step.OldString, cfg4.fileBodyTruncationChars)
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
                AddFileRead(requested);
                newlyRead++;
                await EmitLog(emitSse, "info", $"  📄 {requested}", ct: ct);
            }

            if (newlyRead == 0 && parsed.FilesToRead.Count == 0) break;
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
                // Try method first, then class (symbol could be a method or a class)
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
                    // Don't use class-level AST as hint for property/field additions
                    // or when the change description is about adding one member —
                    // it bloats the exploration excerpt (ExtractRelevantExcerpt
                    // anchors on planOldString and extracts the full class + 60 lines),
                    // causing the LLM to attempt full-class rewrites instead of
                    // targeted single-line inserts.
                    var changeLower = (refinedChange ?? step.Change ?? "").ToLowerInvariant();
                    var isPropertyAdd = changeLower.Contains("add") &&
                        (changeLower.Contains("property") || changeLower.Contains("field") ||
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

        // ── Step 3.5: If all rounds exhausted with very low confidence ────
        //    the replanned description is likely too vague to ground an edit.
        //    Re-derive a crisp description directly from the original prompt.
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

        // ── Step 4: Build the enriched step ──────────────────────────────
        var enrichedStep = new PlanStep
        {
            File = step.File,
            Change = (string.IsNullOrWhiteSpace(refinedChange) ? step.Change : refinedChange) ?? "",
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

    private static string NormalizePathForDedup(string path, string projectRoot)
    {
        try
        {
            var full = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(projectRoot, path));
            return full.Replace('\\', '/').TrimEnd('/');
        }
        catch { return path.Replace('\\', '/').TrimEnd('/'); }
    }

    /// <summary>
    /// When exploration exhausts all rounds at low confidence, the step
    /// description has been diluted by repeated replanning.  Re-derive a
    /// crisp, specific description directly from the original prompt so
    /// that ResolveEditForStep has a concrete target.
    /// </summary>
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

    /// <summary>
    /// After exploration, auto-discover referenced types and SQL table schemas
    /// from the project to enrich the edit context.  This catches gaps the
    /// LLM exploration agent misses (type definitions, same-table SQL).
    /// </summary>
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

        // ── 1) Find the target METHOD body ──────────────────────────────
        // Extract method name from step change (e.g. "Modify GetUsersWithCalendarNotificationsEnabled")
        var methodNameMatch = Regex.Match(stepChange,
            @"(?:Modify|Update|Change|Edit|Replace|Add|Remove|Delete)\s+(\w+)\s*[\(<]?",
            RegexOptions.IgnoreCase);
        var methodName = methodNameMatch.Success ? methodNameMatch.Groups[1].Value : null;

        string? methodBody = null;
        if (methodName != null)
        {
            // Find the method declaration and extract its body
            var methodStartMatch = Regex.Match(targetContent,
                $@"({Regex.Escape(methodName)}\s*\()", RegexOptions.IgnoreCase);
            if (methodStartMatch.Success)
            {
                var startIdx = methodStartMatch.Index;
                // Find the opening brace of this method (skip parameter list)
                var searchFrom = startIdx + methodStartMatch.Length;
                // Skip past the parameter list to find the opening {
                var parenDepth = 1; // already consumed the opening ( in the regex
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
                    // Match the method body with brace counting
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

        var searchScope = methodBody ?? targetContent; // fall back to full file

        // ── 2) Extract SQL only from C# string literals inside the method ──
        // Match @"..." (verbatim) and "..." (regular) string literals containing SQL keywords
        var sqlStrings = new List<string>();
        foreach (Match sm in Regex.Matches(searchScope,
            @"@?""(?:[^""\\]*(?:\\.[^""\\]*)*)""", RegexOptions.Singleline))
        {
            var raw = sm.Value;
            // Only keep strings that look like SQL queries
            if (Regex.IsMatch(raw, @"\b(SELECT|INSERT|UPDATE|DELETE|CREATE\s+TABLE|ALTER\s+TABLE)\b",
                RegexOptions.IgnoreCase))
                sqlStrings.Add(raw);
        }

        // Extract table names from the SQL strings
        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Common English words that are NOT table names
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
            "utf8", "utf8mb4", "ascii", "latin1", "unicode",
            "auto_increment", "unsigned", "signed", "zerofill",
            "current_timestamp", "current_date", "current_time", "localtime",
            "localtimestamp"
        };

        foreach (Match m in Regex.Matches(string.Join("\n", sqlStrings),
            @"(?:FROM|JOIN|INTO|UPDATE|TABLE(?:\s+IF\s+NOT\s+EXISTS)?)\s+`?(\w+(?:\.\w+)?)`?",
            RegexOptions.IgnoreCase))
        {
            var rawTbl = m.Groups[1].Value;
            // Handle schema.table → extract just the table name
            var tbl = rawTbl.Contains('.') ? rawTbl.Split('.')[^1] : rawTbl;
            if (tbl.Length > 2 && !notTableWords.Contains(tbl) &&
                tbl[0] != '@' && !char.IsDigit(tbl[0]))
                tableNames.Add(tbl);
        }

        // ── 3) Extract model type references only from the method body ───────
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
            "Encoding", "UTF8", "Unicode", "ASCII",
            "Exception", "InvalidOperationException", "ArgumentNullException",
            "ArgumentException", "IOException", "FormatException",
            "Response", "Request", "Delegate", "Func", "Action", "Predicate",
            "NameValueCollection", "IOrderedEnumerable",
            "IServiceProvider", "IDisposable", "IAsyncDisposable",
            "Startup", "Program", "MySqlConnection", "MySqlCommand", "MySqlDataReader",
            "MySqlParameter", "MySqlTransaction", "MySqlException",
            "SqlConnection", "SqlCommand", "SqlDataReader",
            "NpgsqlConnection", "NpgsqlCommand", "NpgsqlDataReader",
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
            @"(?:new\s+|:\s*|<\s*|,\s*|Task\s*<\s*|ValueTask\s*<\s*)" +
            @"([A-Z][a-zA-Z0-9]+)" +
            @"(?:\s*[>\[,;\)]|\s+\w|\s*\{|\s*\?)"))
        {
            var name = m.Groups[1].Value;
            if (!skipTypes.Contains(name) && name.Length > 2 &&
                !serviceSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal)))
                typeRefs.Add(name);
        }

        await EmitLog(emitSse, "info",
            $"  🔎 Enrichment: {tableNames.Count} table(s) [{string.Join(", ", tableNames.Take(5))}], " +
            $"{typeRefs.Count} model type(s) from method '{(methodName ?? "?")}'", new { typeRefs, tableNames }, ct: ct);

        if (typeRefs.Count == 0 && tableNames.Count == 0)
            return explorationContext;

        var projectFiles = Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\")
                     && !f.Contains("\\node_modules\\") && !f.Contains("\\.git\\"))
            .ToList();

        // ── 3) Same-table SQL from other files ──────────────────────────────
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

                    // Extract SQL strings containing this table name
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
                            // Format: trim the surrounding quotes, limit length
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

        // ── 4) Model type definitions ───────────────────────────────────────
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
                        $@"(?:class|record|struct)\s+{Regex.Escape(typeName)}\b"))
                    {
                        var rel = Path.GetRelativePath(projectRoot, pf).Replace('\\', '/');
                        if (alreadyRead.Contains(rel) || alreadyRead.Contains(pf)) continue;
                        alreadyRead.Add(rel);

                        var excerpt = AgentUtilities.ExtractRelevantExcerpt(content, typeName, null, 600);
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

        // Prepend a strong warning about property name accuracy directly before the type definitions
        var propertyWarning = "\n⚠ CRITICAL: The type definitions below show the EXACT property names. " +
            "Every `.PropertyName` you write in your edit MUST match these definitions exactly. " +
            "For example, if CalendarEntry shows `Note` property, use `.Note` not `.Description`. " +
            "If it shows `Type`, use `.Type` not `.Title`. Cross-reference EVERY property access.\n";
        return explorationContext + "\n### AUTO-ENRICHED CONTEXT\n" + propertyWarning + enrichment;
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
        "go ready=true on round 1 with a precise refinedChange\n" +
        "8. If the change involves a component, import, alias, or UI element, " +
        "request the import source files to verify the import path and alias are correct before proceeding\n" +
        "9. SPACING in refinedChange: where you describe code snippets inline, verify every token is properly " +
        "separated by a space. 'INTERVAL15 MINUTE' is WRONG — it should be 'INTERVAL 15 MINUTE'. " +
        "Read through your output character-by-character before finalizing.";

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


    /// <summary>
    /// Before executing a plan step, check if it combines multiple distinct changes
    /// and should be decoupled into smaller sub-steps. Returns sub-steps or null.
    /// Called inline during execution, not at plan-time.
    /// </summary>
    private async Task<List<PlanStep>?> CheckAndDecoupleStepAsync(
        PlanStep step, int itemIdx, string projectRoot, bool emitSse,
        CancellationToken ct, List<object> allResults, string? cardId, string originalPrompt)
    {
        if (step == null || string.IsNullOrWhiteSpace(step.Change))
            return null;
        if (!AgentUtilities.IsRelativePath(step.File) || AgentUtilities.IsSpecialMarker(step.File))
            return null;

        // Quick heuristic: only check steps whose description suggests multiple actions
        var ch = (step.Change ?? "").ToLowerInvariant();
        var hasMultipleConcerns =
            ch.Contains(" and ") || ch.Contains(" & ") ||
            ch.Contains(" + ") || ch.Contains(" wrap ") || ch.StartsWith("wrap ") ||
            ch.Contains(" move ") || ch.StartsWith("move ") ||
            // property/field combined with method/logic
            (ch.ContainsAny("propert", "field", "variable", "global") &&
             ch.ContainsAny("method", "function", "logic", "implement", "handler", "control")) ||
            // "add X, implement Y" style
            Regex.IsMatch(ch, @"\b(add|create|implement)\b.{5,60}\b(add|create|implement)\b") ||
            // pagination / navigation / sort with controls
            Regex.IsMatch(ch, @"\b(pagination|navigation|filter|sort)\b.{0,50}\b(control|button|logic|method)\b") ||
            // more than one comma-separated noun phrase
            (ch.Split(',').Length >= 3 && ch.Length > 40);
        if (!hasMultipleConcerns) return null;

        var relPath = step.File.Replace('\\', '/');
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));

        var sb = new StringBuilder();
        sb.AppendLine("You are auditing ONE step of a code-change plan before execution.");
        sb.AppendLine("Your job: detect if this step combines multiple distinct changes and should be split into separate steps.");
        sb.AppendLine();
        sb.AppendLine($"--- STEP ---");
        sb.AppendLine($"File:   {step.File}");
        sb.AppendLine($"Change: {step.Change}");
        sb.AppendLine();
        sb.AppendLine("Split if the change describes TWO OR MORE distinct edits:");
        sb.AppendLine("  - Adding a property AND wiring it up in the template (2 files or 2 concerns)");
        sb.AppendLine("  - Adding code AND removing old code (e.g. move = add + delete)");
        sb.AppendLine("  - Changing logic AND adding UI elements");
        sb.AppendLine("  - Adding a backend endpoint AND frontend code");
        sb.AppendLine("  - WRAPPING content in a new container/tag (needs open + close = 2 edits)");
        sb.AppendLine();
        sb.AppendLine("Also check the TARGET FILE CONTENT below: if it introduces new method calls,");
        sb.AppendLine("property references, or component bindings that would need implementation in a");
        sb.AppendLine("DIFFERENT file (e.g. .ts, .js, .cs), add a separate step for that file.");
        sb.AppendLine("Example: HTML adds (click)=\"toggleKanbanCollapse()\" → add step in .ts to implement it.");
        sb.AppendLine();
        sb.AppendLine("Do NOT split if the step is a single coherent edit (e.g. \"Modify the X method to Y\").");
        sb.AppendLine();
        sb.AppendLine("Output ONLY valid JSON:");
        sb.AppendLine("{");
        sb.AppendLine("  \"needsDecoupling\": false,");
        sb.AppendLine("  \"decoupledSteps\": []");
        sb.AppendLine("}");
        sb.AppendLine("Each decoupledStep: { \"file\": \"...\", \"change\": \"...\" }");

        if (System.IO.File.Exists(fullPath) && AgentUtilities.IsPathUnderRoot(fullPath, projectRoot))
        {
            var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
            sb.AppendLine();
            // Search for content relevant to this step's change description
            var changeKeywords = (step.Change ?? "")
                .Split(new[] { ' ', ',', '.', '(', ')', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim().ToLowerInvariant())
                .Where(w => w.Length > 3)
                .Distinct()
                .ToList();
            var relevantIdx = -1;
            if (changeKeywords.Count > 0)
            {
                relevantIdx = changeKeywords
                    .Select(kw => content.IndexOf(kw, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault(idx => idx >= 0);
            }
            if (relevantIdx > 3000)
            {
                var start = Math.Max(0, relevantIdx - 1500);
                var previewLen = Math.Min(4500, content.Length - start);
                sb.AppendLine("TARGET FILE CONTENT (relevant excerpt around step's target area):");
                sb.AppendLine("```");
                sb.AppendLine((start > 0 ? "...\n" : "") +
                    content.Substring(start, previewLen) +
                    (start + previewLen < content.Length ? "\n..." : ""));
                sb.AppendLine("```");
            }
            else
            {
                sb.AppendLine("TARGET FILE CONTENT (first 4500 chars):");
                sb.AppendLine("```");
                sb.AppendLine(content.Length > 4500 ? content[..4500] + "\n..." : content);
                sb.AppendLine("```");
            }
        }

        sb.AppendLine();
        sb.AppendLine("### ORIGINAL TASK ###");
        sb.AppendLine(originalPrompt.Length > 500 ? originalPrompt[..500] + "..." : originalPrompt);

        var (raw, _, _) = await CallLlmRawStreaming(
            "You output ONLY valid JSON. Never add explanation.",
            sb.ToString(), emitSse, ct, TimeSpan.FromSeconds(15), maxTokens: 1024);

        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```")) { var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) cleaned = m.Groups[1].Value.Trim(); }
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            if (!root.TryGetProperty("needsDecoupling", out var nd) || !nd.GetBoolean())
                return null;
            if (!root.TryGetProperty("decoupledSteps", out var dcArr) || dcArr.ValueKind != JsonValueKind.Array)
                return null;

            var result = new List<PlanStep>();
            foreach (var el in dcArr.EnumerateArray())
            {
                var f = el.TryGetProperty("file", out var fEl) ? fEl.GetString() ?? "" : "";
                var c = el.TryGetProperty("change", out var cEl) ? cEl.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(f) && !string.IsNullOrWhiteSpace(c))
                    result.Add(new PlanStep { File = f, Change = c, Priority = step.Priority });
            }
            return result.Count > 0 ? result : null;
        }
        catch { return null; }
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
        string? cardId = null,
        List<string>? attachedFiles = null)
    {
        var cfg8 = await LoadConfigAsync();
        var relPath = step.File.Replace('\\', '/');
        var fullPath = Path.GetFullPath(
            Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));

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
                await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct);
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
            plan, planItemIndex, emitSse, ct, cardId, attachedFiles);

        step = exploration.EnrichedStep;
        var explorationContext = exploration.ExplorationContext;
        // Auto-enrich with type definitions and same-table SQL from the project
        if (!string.IsNullOrWhiteSpace(explorationContext))
        {
            explorationContext = await EnrichContextWithProjectTypesAndSql(
                projectRoot, relPath, step.Change, explorationContext,
                new HashSet<string>(exploration.FilesRead, StringComparer.OrdinalIgnoreCase),
                emitSse, ct);
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
        const int MaxAttempts = 8;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            string? oldStr = null, newStr = null, resolveError = null;
            bool fullFile = false, alreadyDone = false;
            string? fullContent = null;
            bool fromFormatC = false;

            // Attempt 0: use plan-provided (or AST-resolved) oldString directly
            if (attempt == 0 && !string.IsNullOrWhiteSpace(planOldStr) && !planOldTried)
            {
                planOldTried = true;
                if (string.IsNullOrWhiteSpace(planNewStr))
                {
                    // No newString provided — skip plan-provided edit and let the LLM resolve it
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

                // Pass the rich exploration context so the edit-resolve LLM
                // has far better information than just the file excerpt
                (oldStr, newStr, fullFile, fullContent, alreadyDone, resolveError, fromFormatC) =
                    await ResolveEditForStep(
                        step, projectRoot, emitSse, ct, history,
                        explorationContext: explorationContext,
                        targetSymbol: exploration.TargetSymbol);

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
                await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct);
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

            // ── Targeted replacement ──────────────────────────────────
            var fileContent = System.IO.File.Exists(fullPath)
                ? await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct)
                : string.Empty;

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

            // Detect no-op edit: if old and new are functionally identical, treat as error.
            // A "skipped" status would make doneIndices count the step as completed, causing the
            // quality-check loop to enter "genuinely missing work" → return empty → "stopping"
            // even though the LLM produced zero actual changes. The LLM was asked to make a
            // change and failed — that's an error, triggering a retry via hasIncomplete.
            if (!string.IsNullOrWhiteSpace(oldStr) &&
                AgentUtilities.NormalizeLineEndings(oldStr) == AgentUtilities.NormalizeLineEndings(newStr ?? ""))
            {
                await EmitLog(emitSse, "error", $"No-op edit for {relPath}: LLM produced no change", ct: ct);
                var r = new Dictionary<string, object?>
                {
                    ["index"] = stepIndex,
                    ["type"] = "edit",
                    ["status"] = "error",
                    ["path"] = relPath,
                    ["error"] = "LLM produced a no-op edit — oldString and newString are identical. The LLM failed to produce the required change.",
                    ["planItemIndex"] = planItemIndex
                };
                if (emitSse) await SendSse(Response, "step", r, ct);
                allResults.Add(r);
                return stepIndex + 1;
            }
            // Detect replacement-when-insertion-was-intended: oldString >> newString
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

            // ── Functionality-wipe guard (deterministic, runs BEFORE write-to-disk) ──
            // Catches three classes of LLM mistakes that the no-op / shrink / prefix
            // checks above miss, and forces a retry WITHOUT touching the file:
            //   A. Cached-state loss  — cache/guard lines (.has( / .get( / .set( /
            //      return this.X;) present in oldString but missing from newString.
            //   B. Signature change   — method/function signature line in oldString
            //      has different tokens (return type, name, param count) in newString.
            //   C. New-symbol invention — newString calls this.X(...) / X.Y(...) /
            //      new X(...) where X does NOT appear anywhere in the file content.
            // Concrete trigger case: a "make the rocket projectile a bit bigger"
            // prompt caused the LLM to delete the meshCache guards, change the
            // return type from CityMesh | CityMesh[] to CityMesh[], and invent
            // this.createBoxMesh / this.createConeMesh calls. All three guards
            // fire now and force a re-plan instead of corrupting the file.
            if (!string.IsNullOrWhiteSpace(oldStr) && !string.IsNullOrWhiteSpace(newStr))
            {
                var wipeReason = DetectFunctionalityWipe(
                    oldStr!, newStr!, fileContent, relPath);
                if (wipeReason != null)
                {
                    await EmitLog(emitSse, "warn",
                        $"Functionality-wipe guard triggered for {relPath}: {wipeReason}",
                        new
                        {
                            oldPreview = oldStr!.Length > 200 ? oldStr!.Substring(0, 200) + "..." : oldStr,
                            newPreview = newStr!.Length > 200 ? newStr!.Substring(0, 200) + "..." : newStr
                        },
                        ct: ct);
                    history.Add((oldStr!, newStr, wipeReason));
                    // ── Record abandoned edit in project knowledge ──
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _editKnowledge.RecordOutcomeAsync(
                            projectRoot, relPath, step.Change, prompt ?? step.Change,
                            oldStr, newStr,
                            outcome: "abandoned", reason: wipeReason, ct);
                        }
                        catch { /* swallow */ }
                    }, CancellationToken.None);
                    if (string.Equals(
                        AgentUtilities.NormalizeLineEndings(oldStr ?? ""),
                        AgentUtilities.NormalizeLineEndings(lastOld),
                        StringComparison.Ordinal)) stuckCount++;
                    else { stuckCount = 0; lastOld = AgentUtilities.NormalizeLineEndings(oldStr ?? ""); }
                    if (stuckCount >= 3) goto RecordFailure;
                    continue;
                }
            }

            var fileExt = Path.GetExtension(relPath).ToLowerInvariant();

            var (replaced, newContent, matchError, snippet) =
                TryReplaceSafe(fileContent, oldStr!, newStr ?? string.Empty);

            if (!replaced)
            {
                var err = matchError ?? "oldString not found verbatim";
                if (!string.IsNullOrEmpty(snippet)) err += $". Nearby: {snippet}";
                await EmitLog(emitSse, "warn",
                    $"Edit attempt {attempt + 1}/{MaxAttempts} failed for {relPath}: {err}",
                    new { step }, ct: ct);

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
                        // Prefix leak check for self-healed block
                        var fixFirstLine = correctedBlock.TrimStart().Split('\n', '\r')
                            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                        if (fixFirstLine?.TrimStart().StartsWith('}') == true)
                        {
                            err = "oldString starts with '}' — includes previous method's closing brace. " +
                                "Set oldString to start AT the target method declaration.";
                        }
                        else if (!string.IsNullOrEmpty(newStr) &&
                            (double)newStr.Length / correctedBlock.Length < 0.1)
                        {
                            err = $"newString too short ({(double)newStr.Length / correctedBlock.Length:P1} of self-healed block)";
                        }
                        else
                        {
                            var (approved2, _, _) =
                                VerifyEdit(correctedBlock, newStr ?? "", fileContent, newContent2, fromFormatC);
                            if (approved2)
                            {
                                await System.IO.File.WriteAllTextAsync(
                                    fullPath, newContent2, Encoding.UTF8, ct);
                                await EmitLog(emitSse, "success",
                                    $"✓ Edited {relPath} (self-healed)", step, ct: ct);
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
                }

                // ── CSS/SCSS auto-shrink fallback ────────────────────
                // If oldString is huge (>=8 lines) and the edit failed, try to
                // extract just the lines that actually differ and apply those
                // as independent 1-line edits.
                if (!replaced && fileExt is ".css" or ".scss" or ".less" && oldStr != null &&
                    newStr != null && oldStr.Split('\n').Length >= 8)
                {
                    var slimOldLines = oldStr.Split('\n');
                    var slimNewLines = newStr.Split('\n');
                    var slimFileLines = fileContent.Split('\n');
                    var diffLines = new List<int>();
                    for (var i = 0; i < Math.Min(slimOldLines.Length, slimNewLines.Length); i++)
                    {
                        var o = slimOldLines[i].Trim();
                        var n = slimNewLines[i].Trim();
                        if (!string.IsNullOrWhiteSpace(o) && o != n)
                            diffLines.Add(i);
                    }
                    // Try each differing line as an independent 1-line replacement
                    foreach (var li in diffLines)
                    {
                        var oldTrimmed = slimOldLines[li].TrimEnd();
                        var newTrimmed = slimNewLines[li].TrimEnd();
                        if (string.IsNullOrWhiteSpace(oldTrimmed) || oldTrimmed == newTrimmed)
                            continue;
                        // Find this line in the actual file (exact match)
                        var found = false;
                        for (var fi = 0; fi < slimFileLines.Length; fi++)
                        {
                            if (slimFileLines[fi].TrimEnd() != oldTrimmed) continue;
                            if (found) { found = false; break; } // ambiguous
                            found = true;
                        }
                        if (!found) continue;
                        // Found a unique matching line — apply it
                        var slimOld = slimOldLines[li];   // preserve exact whitespace from LLM
                        var slimNew = slimNewLines[li];
                        // But use the file's actual content for the oldString
                        for (var fi = 0; fi < slimFileLines.Length; fi++)
                        {
                            if (slimFileLines[fi].TrimEnd() != oldTrimmed) continue;
                            slimOld = slimFileLines[fi];
                            break;
                        }
                        var (slimReplaced, slimContent, _, _) = TryReplaceSafe(fileContent, slimOld, slimNew);
                        if (slimReplaced)
                        {
                            await EmitLog(emitSse, "info",
                                $"CSS auto-shrink: replacing \"{slimOld.Trim()}\" with \"{slimNew.Trim()}\"",
                                ct: ct);
                            await System.IO.File.WriteAllTextAsync(fullPath, slimContent, Encoding.UTF8, ct);
                            await EmitLog(emitSse, "success",
                                $"✓ Edited {relPath} (CSS auto-shrink)", step, ct: ct);
                            var r2 = new Dictionary<string, object?>();
                            PopulateEditResult(r2, "modified", relPath, slimOld, slimNew, "css-auto-shrink");
                            r2["index"] = stepIndex; r2["planItemIndex"] = planItemIndex;
                            if (emitSse) await SendSse(Response, "step", r2, ct);
                            allResults.Add(r2);
                            await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct);
                            return stepIndex + 1;
                        }
                    }
                }

                history.Add((oldStr!, newStr ?? "", err));

                if (string.Equals(
                    AgentUtilities.NormalizeLineEndings(oldStr ?? ""),
                    AgentUtilities.NormalizeLineEndings(lastOld),
                    StringComparison.Ordinal)) stuckCount++;
                else { stuckCount = 0; lastOld = AgentUtilities.NormalizeLineEndings(oldStr ?? ""); }
                if (stuckCount >= 3)
                {
                    await EmitLog(emitSse, "error",
                        $"LLM keeps producing the same oldString — aborting {relPath}",
                        ct: ct);
                    goto RecordFailure;
                }
                // CSS/SCSS: abort after 4 attempts (plan + 3 retries) — LLM consistently
                // hallucinates oldStrings that don't match the actual file content.
                if (attempt >= 4 && fileExt is ".css" or ".scss" or ".less")
                {
                    await EmitLog(emitSse, "error",
                        $"CSS edit failed after {attempt + 1} attempts — aborting {relPath}",
                        ct: ct);
                    goto RecordFailure;
                }
                continue;
            }

            // Content-shrink check: if newString is drastically shorter than oldString,
            var shrinkThreshold = fromFormatC ? 0.02 : 0.1;
            if (!string.IsNullOrEmpty(newStr) && (double)newStr.Length / oldStr!.Length < shrinkThreshold)
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
                if (stuckCount >= 3) goto RecordFailure;
                continue;
            }

            // Prefix leak check: if oldString's first trimmed line starts with '}',
            // the LLM included the previous method's closing brace as context.
            // The newString typically doesn't include it, causing the previous method
            // to lose its closing brace and become corrupted.
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
                if (stuckCount >= 3) goto RecordFailure;
                continue;
            }

            var (approved, verifyReason, _) = VerifyEdit(oldStr!, newStr ?? "", fileContent, newContent, fromFormatC);

            if (!approved && verifyReason.Contains("SQL whitespace collapsed", StringComparison.OrdinalIgnoreCase))
            {
                var correctedContent = AutoFixSqlWhitespace(newContent);
                if (correctedContent != newContent)
                {
                    // Also fix newStr so the "newString not found after replacement" check
                    // inside VerifyEdit doesn't false-fire: correctedContent now has
                    // "INTERVAL 15" but the original newStr still has "INTERVAL15", so
                    // normNewContent.Contains(normNew) would always return false.
                    var correctedNewStr = AutoFixSqlWhitespace(newStr ?? "");
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
                        // SQL whitespace was the only difference — after fixing it, code matches original.
                        // The LLM reproduced the existing method without adding any new logic.
                        verifyReason =
                            "SQL whitespace auto-fix made your newCode IDENTICAL to the existing code — " +
                            "you reproduced the original method body without implementing the new functionality. " +
                            "Write a DIFFERENT method body that adds the logic described in CHANGE REQUIRED.";
                        newStr = correctedNewStr; // keep for feedback context
                    }
                }
            }

            if (!approved)
            {
                await EmitLog(emitSse, "warn",
                    $"Verify failed for {relPath}: {verifyReason}", ct: ct);
                history.Add((oldStr!, newStr ?? "", verifyReason));
                var isIdenticalError =
    verifyReason.Contains("IDENTICAL to the existing code", StringComparison.OrdinalIgnoreCase) ||
    verifyReason.Contains("identical after normalization", StringComparison.OrdinalIgnoreCase);
                var trackBy = isIdenticalError
                    ? AgentUtilities.NormalizeLineEndings(newStr ?? "")
                    : AgentUtilities.NormalizeLineEndings(oldStr ?? "");

                if (string.Equals(trackBy, AgentUtilities.NormalizeLineEndings(lastOld), StringComparison.Ordinal))
                    stuckCount++;
                else { stuckCount = 0; lastOld = trackBy; }
                if (stuckCount >= 3) goto RecordFailure;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(newStr) &&
    !newContent.Contains(
        AgentUtilities.NormalizeLineEndings(newStr), StringComparison.Ordinal))
            {
                // Strip leading whitespace AND trailing whitespace per line,
                // same as VerifyEdit does internally — handles IndentReplacement re-indentation
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

                    // ← ADD stuckCount tracking so this aborts after 3 identical failures
                    if (string.Equals(
                        AgentUtilities.NormalizeLineEndings(oldStr ?? ""),
                        AgentUtilities.NormalizeLineEndings(lastOld),
                        StringComparison.Ordinal)) stuckCount++;
                    else { stuckCount = 0; lastOld = AgentUtilities.NormalizeLineEndings(oldStr ?? ""); }
                    if (stuckCount >= 3) goto RecordFailure;
                    continue;
                }
            }

            // Normalize TypeScript/JS object literal spacing (C# was already
            // formatted before insertion above — no file-wide Roslyn pass).
            if (Path.GetExtension(relPath).Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(relPath).Equals(".tsx", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(relPath).Equals(".js", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(relPath).Equals(".jsx", StringComparison.OrdinalIgnoreCase))
            {
                newContent = NormalizeTypeScriptObjectLiterals(newContent);
            }

            // ── Snapshot the file BEFORE the edit is committed ───────────────
            // Captured here so the LLM verify-and-decide gate below can revert
            // deterministically if it decides to abandon the edit. `fileContent`
            // is the in-memory copy we read earlier; we re-write it from disk
            // on revert so any concurrent external modification is also undone.
            var preEditContent = fileContent;

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

            // ── Post-edit CSS region formatting (deterministic, runs BEFORE LLM pass) ──
            // Fixes two LLM-generated CSS defects in the rule block(s) touched by the edit:
            //   * Missing space after ':' in declarations   ("flex:1;" -> "flex: 1;")
            //   * Inconsistent property indentation          (" flex:1;" -> "  flex: 1;")
            // Only the enclosing rule block(s) of the edited region are rewritten, so
            // unrelated parts of the file stay byte-for-byte identical.
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

            // ── Deterministic auto-format of edited region (Patch 5) ──────────
            // Fixes three common LLM formatting regressions in the lines that
            // were just inserted/modified, using a character-by-character
            // scanner that tracks string and comment state:
            //   1. Missing space after ','  ("indices,0" -> "indices, 0")
            //   2. Missing space after ':'  ("{a:1}" -> "{a: 1}")
            //   3. Missing space after ';'  ("for(i=0;i<10;)" -> "for(i=0; i<10;)")
            // Scoped to the edited region only — unrelated lines are never
            // touched. Runs BEFORE PostEditStyleFixAsync so the LLM only
            // handles residual issues.
            if (!string.IsNullOrWhiteSpace(newStr) &&
                (fileExt is ".ts" or ".tsx" or ".js" or ".jsx" or ".cs"
                  or ".css" or ".scss" or ".less" or ".html" or ".json"
                  or ".vue" or ".svelte"))
            {
                var beforeAutoFmt = newContent;
                newContent = AutoFormatEditedRegion(newContent, newStr);
                if (newContent != beforeAutoFmt)
                {
                    await System.IO.File.WriteAllTextAsync(fullPath, newContent, Encoding.UTF8, ct);
                    await EmitLog(emitSse, "info",
                        $"Auto-formatted edited region in {relPath} (commas/colons/semicolons)", ct: ct);
                }
            }

            // Post-edit style fix: check for spacing issues in the new content and fix via LLM
            if (!string.IsNullOrWhiteSpace(newStr) &&
                (fileExt is ".ts" or ".tsx" or ".js" or ".jsx" or ".html" or ".css" or ".scss" or ".less"))
            {
                newContent = await PostEditStyleFixAsync(fullPath, relPath, newContent, newStr, emitSse, ct);
            }

            // ── LLM-driven per-step verify-and-decide gate ───────────────────
            // After all deterministic checks pass and the file is written, ask
            // the LLM to make a keep/abandon decision. The LLM sees:
            //   * the original task prompt
            //   * the step description
            //   * the diff (oldStr / newStr)
            //   * a small context window around the edit in the new file
            // If it returns "abandon", we RESTORE the pre-edit snapshot to disk
            // and continue the retry loop — the LLM gets another shot at a
            // better edit. If it returns "keep", we fall through to mark the
            // step complete. After 2 consecutive "abandon" decisions on the
            // same step, we treat the step as failed and let the existing
            // re-plan machinery take over.
            //
            // NOTE: variable names use the `llmGate` prefix to avoid colliding
            // with `verifyReason` declared in the enclosing scope at the
            // VerifyEdit(...) call above. C# disallows a local named
            // `verifyReason` when an enclosing block already has one in scope.
            if (!string.IsNullOrWhiteSpace(newStr) && !string.IsNullOrWhiteSpace(preEditContent))
            {
                var (llmGateDecision, llmGateReason) = await LlmVerifyEditStepAsync(
                    relPath, prompt ?? step.Change, step.Change,
                    oldStr!, newStr!, preEditContent, newContent,
                    emitSse, ct);

                if (llmGateDecision == "abandon")
                {
                    // Real revert: write the original content back to disk.
                    await System.IO.File.WriteAllTextAsync(fullPath, preEditContent, Encoding.UTF8, ct);
                    await EmitLog(emitSse, "warn",
                        $"⟲ LLM verify: ABANDON edit on {relPath} — {llmGateReason}. Reverted to pre-edit state; retrying.",
                        new { step, reason = llmGateReason }, ct: ct);
                    if (emitSse)
                    {
                        await SendSse(Response, "step", new
                        {
                            index = stepIndex,
                            type = "edit",
                            status = "verify-abandoned",
                            path = relPath,
                            reason = llmGateReason,
                            planItemIndex
                        }, ct);
                    }
                    history.Add((oldStr!, newStr, $"LLM verify abandoned: {llmGateReason}"));
                    // ── Record abandoned edit in project knowledge ──
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _editKnowledge.RecordOutcomeAsync(
                            projectRoot, relPath, step.Change, prompt ?? step.Change,
                            oldStr, newStr,
                            outcome: "abandoned", reason: $"LLM verify: {llmGateReason}", ct);
                        }
                        catch { /* swallow */ }
                    }, CancellationToken.None);
                    // Track consecutive abandons so we don't loop forever.
                    if (string.Equals(
                        AgentUtilities.NormalizeLineEndings(oldStr ?? ""),
                        AgentUtilities.NormalizeLineEndings(lastOld),
                        StringComparison.Ordinal)) stuckCount++;
                    else { stuckCount = 0; lastOld = AgentUtilities.NormalizeLineEndings(oldStr ?? ""); }
                    if (stuckCount >= 2)
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
                    await EmitLog(emitSse, "info",
                        $"✓ LLM verify: KEEP edit on {relPath} — {llmGateReason}", ct: ct);
                    if (emitSse)
                    {
                        await SendSse(Response, "step", new
                        {
                            index = stepIndex,
                            type = "edit",
                            status = "verify-kept",
                            path = relPath,
                            reason = llmGateReason,
                            planItemIndex
                        }, ct);
                    }
                }
                // llmGateDecision == "error" / null -> default to keep (don't block on infra)
            }

            // ── Record successful edit in project knowledge ───────────────
            _ = Task.Run(async () =>
            {
                try
                {
                    await _editKnowledge.RecordOutcomeAsync(
                    projectRoot, relPath, step.Change, prompt ?? step.Change,
                    oldStr, newStr, outcome: "success", reason: "", ct);
                }
                catch { /* swallow */ }
            }, CancellationToken.None);

            await EmitLog(emitSse, "success", $"✓ Edited {relPath}", ct: ct);
            var result = new Dictionary<string, object?>();
            PopulateEditResult(result, "modified", relPath, oldStr, newStr ?? "", "");
            result["index"] = stepIndex; result["planItemIndex"] = planItemIndex;
            if (emitSse) await SendSse(Response, "step", result, ct);
            allResults.Add(result);
            await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct);

            // ── Method signature change: update call sites ─────────────
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

        // ── Record failed edit in project knowledge ───────────────────
        _ = Task.Run(async () =>
        {
            try
            {
                await _editKnowledge.RecordOutcomeAsync(
                projectRoot, step.File, step.Change, prompt ?? step.Change,
                step.OldString, step.NewString,
                outcome: "failure", reason: lastErr, ct);
            }
            catch { /* swallow */ }
        }, CancellationToken.None);

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
        // Mark step done in boarddata so it won't be retried on restart
        await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct);
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

    /// <summary>
    /// Post-edit style pass: scans the applied newStr for common spacing issues
    /// (missing spaces around operators, colons, etc.) and calls the LLM to fix them.
    /// Returns the (possibly unchanged) content.
    /// </summary>
    private async Task<string> PostEditStyleFixAsync(
        string fullPath, string relPath, string content, string appliedNewStr,
        bool emitSse, CancellationToken ct)
    {
        // Check for common spacing issues in the applied edit: e.g. `-1)` or `*5` or `:5`
        // We look only in the changed region to keep the LLM call focused.
        var hasSpacingIssue = false;
        var needleLines = appliedNewStr.Split('\n');
        // Build a compact excerpt of lines in the file that contain the new string lines
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
                    // Check this line for spacing issues
                    if (Regex.IsMatch(fileLines[i], @"\(\w+\s*[+\-*/%]\d") ||  // `(page -1` no space before digit
                        Regex.IsMatch(fileLines[i], @"\d\s*[+\-*/%]\s*\d") ||  // `5*5` `5 *5` `5* 5` missing spaces
                        Regex.IsMatch(fileLines[i], @"(?<![=!<>])=(?!=)\d"))    // `=1` no space after = (but not ==, !=, <=, >=)
                    {
                        hasSpacingIssue = true;
                    }
                    break;
                }
            }
        }
        if (!hasSpacingIssue || excerptStart < 0)
            return content;

        // Send excerpt to LLM for fixing
        var contextWindowStart = Math.Max(0, excerptStart - 3);
        var contextWindowEnd = Math.Min(fileLines.Length, excerptEnd + 4);
        var excerpt = string.Join("\n", fileLines[contextWindowStart..contextWindowEnd]);

        var sysPrompt = "You are a meticulous code formatter. Fix spacing issues in the code excerpt below: " +
                        "ensure proper spacing around operators (+, -, *, /, %, =, etc.) and colons in " +
                        "TypeScript/JavaScript/HTML/CSS. Output ONLY a JSON object with an array of fixes: " +
                        "{\"fixes\":[{\"oldString\":\"...\",\"newString\":\"...\"}]}. " +
                        "Each fix must be an exact substring from the excerpt. Do NOT change logic or add/remove code.";

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
                    continue;
                if (!fixedContent.Contains(oldStr, StringComparison.Ordinal))
                    continue;
                fixedContent = fixedContent.Replace(oldStr, newStr);
                fixCount++;
            }

            if (fixCount > 0)
            {
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

    /// <summary>
    /// Deterministically auto-formats the edited region of a code file.
    /// Fixes three common LLM formatting regressions by scanning each
    /// edited line character-by-character, tracking string and comment
    /// state so that commas/colons/semicolons inside strings or comments
    /// are never touched:
    ///
    ///   1. Missing space after ','  ("indices,0" -> "indices, 0")
    ///      — skips when next char is ')', ']', '}' (trailing comma) or
    ///        whitespace or end-of-line.
    ///   2. Missing space after ':'  ("{a:1}" -> "{a: 1}", "let x:number"
    ///      -> "let x: number")
    ///      — skips "::" (C# qualified names, TS scope), end-of-line
    ///        colons (case labels, default:), and URLs (handled by string
    ///        state).
    ///   3. Missing space after ';'  ("for(i=0;i<10;)" -> "for(i=0; i<10;)")
    ///      — skips ";;", ";)", and end-of-line semicolons.
    ///
    /// Only lines that contain a needle from the applied newStr are
    /// processed — unrelated lines are left byte-for-byte unchanged.
    /// </summary>
    private string AutoFormatEditedRegion(string content, string appliedNewStr)
    {
        if (string.IsNullOrWhiteSpace(appliedNewStr) || string.IsNullOrWhiteSpace(content))
            return content;

        var fileLines = content.Split('\n');

        // Collect needles (trimmed, non-empty, min 5 chars to avoid
        // matching trivially short fragments like "{" or "0").
        var needles = appliedNewStr.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l) && l.Length >= 5)
            .Take(10)
            .ToList();

        if (needles.Count == 0) return content;

        // Find which file lines contain any needle — those are the
        // "edited region" lines that get formatting fixes.
        var editedLineIndices = new HashSet<int>();
        foreach (var needle in needles)
        {
            for (var i = 0; i < fileLines.Length; i++)
            {
                if (fileLines[i].Contains(needle, StringComparison.Ordinal))
                    editedLineIndices.Add(i);
            }
        }

        if (editedLineIndices.Count == 0) return content;

        var changed = false;
        for (var i = 0; i < fileLines.Length; i++)
        {
            if (!editedLineIndices.Contains(i)) continue;
            var fixedLine = FixLineSpacing(fileLines[i]);
            if (fixedLine != fileLines[i])
            {
                fileLines[i] = fixedLine;
                changed = true;
            }
        }

        return changed ? string.Join("\n", fileLines) : content;
    }

    /// <summary>
    /// Fix missing spaces after ',', ':', and ';' in a single line of code.
    /// Uses a character-by-character scanner that tracks:
    ///   - Double-quote strings  ("...")
    ///   - Single-quote strings  ('...')
    ///   - Template literals      (`...`)
    ///   - Line comments          (// ...)
    ///   - Block comments         (/* ... */ — single-line portion)
    /// Punctuation inside any of these is never touched.
    /// </summary>
    private static string FixLineSpacing(string line)
    {
        if (string.IsNullOrEmpty(line))
            return line;

        // Quick exit: if none of the target chars are present, skip.
        if (!line.Contains(',') && !line.Contains(':') && !line.Contains(';'))
            return line;

        var sb = new StringBuilder(line.Length + 4);
        var i = 0;
        var inStringDouble = false;
        var inStringSingle = false;
        var inTemplate = false;
        var inLineComment = false;
        var inBlockComment = false;

        while (i < line.Length)
        {
            var c = line[i];
            var next = (i + 1 < line.Length) ? line[i + 1] : '\0';
            var prev = (i > 0) ? line[i - 1] : '\0';

            // ── Block comment (single-line portion of /* ... */) ──
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

            // ── Line comment (// ... to end of line) ──
            if (inLineComment)
            {
                sb.Append(c);
                i++;
                continue;
            }

            // ── Inside a string literal ──
            if (inStringDouble || inStringSingle || inTemplate)
            {
                sb.Append(c);
                // Handle escape sequences: \X -> append both chars
                if (c == '\\' && next != '\0')
                {
                    sb.Append(next);
                    i += 2;
                    continue;
                }
                // Check for closing delimiter
                if (inStringDouble && c == '"') inStringDouble = false;
                else if (inStringSingle && c == '\'') inStringSingle = false;
                else if (inTemplate && c == '`') inTemplate = false;
                i++;
                continue;
            }

            // ── Not in string or comment — check for state transitions ──
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
            if (c == '"') { inStringDouble = true; sb.Append(c); i++; continue; }
            if (c == '\'') { inStringSingle = true; sb.Append(c); i++; continue; }
            if (c == '`') { inTemplate = true; sb.Append(c); i++; continue; }

            // ── Not in string or comment — apply spacing fixes ──

            // Rule 1: ',' followed by non-whitespace, non-')', ']', '}' -> insert space
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

            // Rule 2: ':' — insert space after if:
            //   - not part of '::' (prev != ':' and next != ':')
            //   - next is not whitespace, not end-of-line
            //   - (URLs like "http://" are handled by string state above)
            if (c == ':')
            {
                sb.Append(c);
                i++;
                if (i < line.Length)
                {
                    var after = line[i];
                    // Skip "::" — don't insert space inside double-colon
                    if (prev != ':' && after != ':'
                        && after != ' ' && after != '\t' && after != '\r' && after != '\n')
                    {
                        sb.Append(' ');
                    }
                }
                continue;
            }

            // Rule 3: ';' — insert space after if:
            //   - next is not ';', ')', whitespace, or end-of-line
            //   - (catches "for(i=0;i<10;)" -> "for(i=0; i<10;)")
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

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    // ── Edit knowledge (recorded in EditKnowledgeService) ──

    /// <summary>
    /// LLM-driven per-step verify-and-decide gate.
    ///
    /// After all deterministic checks (no-op, shrink, prefix-leak,
    /// functionality-wipe, VerifyEdit, replacement-mismatch) pass and the
    /// file has been written to disk, this method asks the LLM to make a
    /// keep/abandon decision on the just-applied edit.
    ///
    /// The LLM receives:
    ///   * the original task prompt (so it can judge intent, not just syntax)
    ///   * the step description
    ///   * the diff region (oldStr / newStr)
    ///   * a small context window (10 lines) around the edit in the new file
    ///
    /// Returns one of:
    ///   ("keep",    reason) — accept the edit; caller marks step complete.
    ///   ("abandon", reason) — revert the edit; caller restores pre-edit
    ///                          snapshot and retries.
    ///   ("error",   reason) — infra failure (LLM unreachable, malformed
    ///                          JSON). Caller defaults to "keep" so we don't
    ///                          block on network/JSON issues.
    /// </summary>
    private async Task<(string decision, string reason)> LlmVerifyEditStepAsync(
        string relPath,
        string originalPrompt,
        string stepChange,
        string oldStr,
        string newStr,
        string preEditContent,
        string postEditContent,
        bool emitSse,
        CancellationToken ct)
    {
        // Build a focused context window around the edit location in the
        // POST-edit file. We find the first non-empty line of newStr and
        // center a 10-line window on it.
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

        var sysPrompt =
            "You are a meticulous code reviewer verifying a single edit step in a larger plan. " +
            "Your job is to decide whether to KEEP the edit (it correctly implements the step " +
            "without breaking existing functionality) or ABANDON it (it broke something, changed " +
            "the wrong thing, introduced undefined references, deleted guards/caches, or otherwise " +
            "missed the intent of the step).\n\n" +
            "STRICT OUTPUT FORMAT — output ONLY a JSON object, no prose, no markdown fences:\n" +
            "{\"decision\":\"keep\"|\"abandon\", \"reason\":\"one short sentence\"}\n\n" +
            "DECISION RULES:\n" +
            " * Return \"keep\" if the edit correctly implements the step AND preserves all existing " +
            "  functionality (caches, guards, return types, call-site contracts).\n" +
            " * Return \"abandon\" if ANY of these are true:\n" +
            "    - The edit deleted cache/state guard lines (e.g. `if (this.X) return ...`, " +
            "      `map.has(...)`, `map.get(...)`, `map.set(...)`).\n" +
            "    - The edit changed the method signature (return type, name, or parameter list).\n" +
            "    - The edit introduced calls to methods/identifiers that don't exist in the file.\n" +
            "    - The edit replaced existing logic instead of extending/tweaking it, when the " +
            "      step only asked for a small change.\n" +
            "    - The edit is functionally a no-op (old and new do the same thing).\n" +
            "    - The edit breaks the build (syntax errors, missing braces, undefined vars).\n" +
            " * Be conservative: if you're unsure, return \"abandon\" with a clear reason. The " +
            "  agent will retry with your feedback.\n" +
            " * Do NOT consider style/whitespace issues — those are handled by other passes.";

        var userMsg =
            $"### TASK PROMPT ###\n{originalPrompt}\n\n" +
            $"### STEP DESCRIPTION ###\n{stepChange}\n\n" +
            $"### FILE ###\n{relPath}\n\n" +
            $"### OLD CODE (what was there before) ###\n```\n{TruncateForLlm(oldStr, 1500)}\n```\n\n" +
            $"### NEW CODE (what the edit replaced it with) ###\n```\n{TruncateForLlm(newStr, 1500)}\n```\n\n" +
            $"### POST-EDIT CONTEXT WINDOW (10 lines around the edit) ###\n```\n{contextWindow}\n```\n\n" +
            "Decide: keep or abandon? Output JSON only.";

        try
        {
            var (raw, _, error) = await CallLlmRawStreaming(
                sysPrompt, userMsg, emitSse, ct,
                requestTimeout: TimeSpan.FromMinutes(2),
                maxTokens: 256);

            if (!string.IsNullOrWhiteSpace(error))
                return ("error", $"LLM call failed: {error}");
            if (string.IsNullOrWhiteSpace(raw))
                return ("error", "LLM returned empty response");

            // Strip markdown fences if present.
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                cleaned = cleaned.TrimStart('`');
                // Remove optional language tag like "json"
                var firstNewline = cleaned.IndexOf('\n');
                if (firstNewline >= 0) cleaned = cleaned[(firstNewline + 1)..];
                if (cleaned.EndsWith("```")) cleaned = cleaned[..^3];
            }
            // Find the outermost JSON object.
            var fb = cleaned.IndexOf('{');
            var lb = cleaned.LastIndexOf('}');
            if (fb < 0 || lb <= fb)
                return ("error", $"No JSON object found in LLM response: {TruncateForLlm(cleaned, 200)}");
            cleaned = cleaned[fb..(lb + 1)];

            using var doc = JsonDocument.Parse(cleaned);
            var decision = doc.RootElement.TryGetProperty("decision", out var dEl)
                ? dEl.GetString()?.ToLowerInvariant().Trim() ?? ""
                : "";
            var reason = doc.RootElement.TryGetProperty("reason", out var rEl)
                ? rEl.GetString()?.Trim() ?? ""
                : "";

            if (decision != "keep" && decision != "abandon")
                return ("error", $"LLM returned unknown decision '{decision}'");

            return (decision, reason);
        }
        catch (Exception ex)
        {
            return ("error", $"Exception during LLM verify: {ex.Message}");
        }
    }

    /// <summary>
    /// Truncate a string for inclusion in an LLM prompt. If the string
    /// exceeds <paramref name="maxChars"/>, keeps the first half and the
    /// last quarter with an ellipsis marker in the middle.
    /// </summary>
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

    /// <summary>
    /// Deterministic pre-write guard. Inspects an oldString/newString pair
    /// and returns a non-null reason string if the edit would wipe existing
    /// functionality, change a method's signature, or invent symbols that do
    /// not exist in the file. Returns null if the edit looks safe.
    ///
    /// The three checks below catch the failure mode where the LLM, asked to
    /// make a small tweak ("make the rocket projectile a bit bigger"),
    /// instead rewrites the entire method — deleting cache guards, changing
    /// the return type, and calling methods that don't exist in the file.
    /// </summary>
    private static string? DetectFunctionalityWipe(
        string oldStr, string newStr, string fileContent, string relPath)
    {
        if (string.IsNullOrWhiteSpace(oldStr) || string.IsNullOrWhiteSpace(newStr))
            return null;

        var oldLines = oldStr.Split('\n');
        var newLinesSet = new HashSet<string>(
            newStr.Split('\n').Select(l => l.Trim()),
            StringComparer.Ordinal);

        // ── Guard A: cached-state loss ────────────────────────────────────
        // Lines in oldString that match cache/guard patterns but are missing
        // (by trimmed content) from newString. We match on the trimmed line
        // so trivial re-indentation by the LLM doesn't bypass the check.
        var cacheLinePatterns = new[]
        {
            new Regex(@"\.has\s*\(", RegexOptions.Compiled),                  // map.has('rocket')
            new Regex(@"\.get\s*\(", RegexOptions.Compiled),                  // map.get('rocket')
            new Regex(@"\.set\s*\(", RegexOptions.Compiled),                  // map.set('rocket', ...)
            new Regex(@"\.delete\s*\(", RegexOptions.Compiled),               // map.delete('rocket')
            new Regex(@"return\s+this\.\w+\s*;", RegexOptions.Compiled),      // return this.rocketMesh;
            new Regex(@"if\s*\(\s*this\.\w+", RegexOptions.Compiled),         // if (this.rocketMesh)
            new Regex(@"if\s*\(\s*!\s*this\.\w+", RegexOptions.Compiled),     // if (!this.rocketMesh)
            new Regex(@"this\.\w+\s*=\s*null\s*;", RegexOptions.Compiled),    // this.rocketMesh = null;
            new Regex(@"this\.\w+\s*=\s*undefined\s*;", RegexOptions.Compiled),
            new Regex(@"this\.\w+\s*=\s*default\s*;", RegexOptions.Compiled),
            new Regex(@"_\w+\s*=\s*null\s*;", RegexOptions.Compiled),         // _field = null; (C#)
        };
        var lostCacheLines = new List<string>();
        foreach (var line in oldLines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            // Skip pure-structural lines (braces, blank) — those are allowed to move.
            if (trimmed == "{" || trimmed == "}" || trimmed == "});") continue;
            var isCacheLine = false;
            foreach (var pat in cacheLinePatterns)
            {
                if (pat.IsMatch(trimmed)) { isCacheLine = true; break; }
            }
            if (!isCacheLine) continue;
            if (!newLinesSet.Contains(trimmed))
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

        // ── Guard B: signature change ────────────────────────────────────
        // For .ts/.tsx/.js/.jsx/.cs files: if the FIRST non-blank line of
        // oldString looks like a method/function declaration, the corresponding
        // line of newString must have the SAME identifier tokens (modulo
        // whitespace and the body). A change to the return type, method name,
        // or parameter list is treated as a signature change.
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        if (ext is ".ts" or ".tsx" or ".js" or ".jsx" or ".cs" or ".vb")
        {
            var oldSigLine = oldLines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";
            var newSigLine = newStr.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";
            if (LooksLikeMethodDeclaration(oldSigLine) && LooksLikeMethodDeclaration(newSigLine))
            {
                var oldTokens = ExtractSignatureTokens(oldSigLine);
                var newTokens = ExtractSignatureTokens(newSigLine);
                if (!oldTokens.SequenceEqual(newTokens))
                {
                    var oldSig = string.Join(" ", oldTokens);
                    var newSig = string.Join(" ", newTokens);
                    return $"SIGNATURE CHANGE — method declaration changed from [{oldSig}] to [{newSig}]. " +
                           "The task is to tweak the body, not change the contract (return type / name / params). " +
                           "RESTORE the original signature line and only modify the body lines below it.";
                }
            }
        }

        // ── Guard C: new-symbol invention ────────────────────────────────
        // Collect every `this.X(`, `X.Y(`, `new X(` call in newString that
        // was NOT in oldString, then check whether X appears anywhere in the
        // full file content. If it doesn't, the LLM invented it.
        var oldCalls = ExtractMethodCalls(oldStr);
        var newCalls = ExtractMethodCalls(newStr);
        var inventedCalls = newCalls.Except(oldCalls, StringComparer.Ordinal).ToList();
        if (inventedCalls.Count > 0)
        {
            var trulyInvented = new List<string>();
            foreach (var call in inventedCalls)
            {
                // The call is e.g. "this.createBoxMesh" or "createConeMesh" or "Foo.bar".
                // Check if the bare identifier (last segment) appears anywhere in the file.
                var bareIdent = call.Split('.').Last();
                if (string.IsNullOrEmpty(bareIdent)) continue;
                // Allow common built-ins / language constructs.
                if (IsBuiltinIdentifier(bareIdent)) continue;
                // Search the file content for the bare identifier followed by '(' or as a property.
                if (!fileContent.Contains(bareIdent, StringComparison.Ordinal))
                    trulyInvented.Add(call);
            }
            if (trulyInvented.Count > 0)
            {
                var preview = string.Join(", ", trulyInvented.Take(5));
                if (trulyInvented.Count > 5) preview += $", (+{trulyInvented.Count - 5} more)";
                return $"NEW-SYMBOL INVENTION — newString calls [{preview}] which do NOT appear anywhere in {relPath}. " +
                       "The LLM invented methods/identifiers that don't exist in this file. " +
                       "Use ONLY methods that already appear in the file. If you need a helper that doesn't exist, " +
                       "say so in the plan instead of fabricating calls.";
            }
        }

        return null;
    }

    /// <summary>
    /// Heuristic: does this trimmed line look like a method/function declaration?
    /// Matches TypeScript/JavaScript/C# patterns like:
    ///   private getRocketMesh(): CityMesh | CityMesh[] {
    ///   public async Task&lt;int&gt; ResolveAsync(string path, CancellationToken ct)
    ///   function foo(a, b) {
    /// </summary>
    private static bool LooksLikeMethodDeclaration(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        // Must contain '(' and either '{' at end or no body (declaration line).
        if (!line.Contains('(')) return false;
        // Reject lines that are clearly control flow / expressions.
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
        // Match: optional modifiers, then an identifier, then '('.
        return Regex.IsMatch(line,
            @"^\s*(public|private|protected|internal|static|async|export|function|override|sealed|virtual|abstract|readonly|partial|\s)+\s*"
            + @"(<[^>]+>\s*)?"                 // optional generic return type
            + @"~?\w+\s*\(",
            RegexOptions.Compiled);
    }

    /// <summary>
    /// Extract the "signature tokens" from a method declaration line so two
    /// signatures can be compared for equality modulo whitespace.
    ///
    /// Captures THREE regions of the declaration line:
    ///   1. Everything from the start through the opening '(' — modifiers,
    ///      generic return type (C# `Task&lt;int&gt; Foo`), and method name.
    ///   2. Everything between the matching '(' and ')' — the parameter list.
    ///   3. Everything after ')' up to (but not including) '{' or end-of-line —
    ///      the TypeScript/JS return type annotation (`: CityMesh | CityMesh[]`).
    ///
    /// This is necessary because TypeScript places the return type AFTER the
    /// parameter list, so cutting at '(' alone misses return-type changes
    /// like `CityMesh | CityMesh[]` -> `CityMesh[]`.
    /// </summary>
    private static List<string> ExtractSignatureTokens(string sigLine)
    {
        if (string.IsNullOrWhiteSpace(sigLine))
            return new List<string>();

        // Region 1: head (modifiers + name) up to '('.
        var parenIdx = sigLine.IndexOf('(');
        var head = parenIdx >= 0 ? sigLine.Substring(0, parenIdx) : sigLine;

        // Region 2: parameter list between '(' and matching ')'.
        // Region 3: return-type annotation between ')' and '{' (TS/JS only).
        string paramsRegion = "";
        string returnRegion = "";
        if (parenIdx >= 0)
        {
            var depth = 0;
            var closeIdx = -1;
            for (var i = parenIdx; i < sigLine.Length; i++)
            {
                if (sigLine[i] == '(') depth++;
                else if (sigLine[i] == ')')
                {
                    depth--;
                    if (depth == 0) { closeIdx = i; break; }
                }
            }
            if (closeIdx >= 0)
            {
                paramsRegion = sigLine.Substring(parenIdx, closeIdx - parenIdx + 1);
                var after = sigLine.Substring(closeIdx + 1);
                var braceIdx = after.IndexOf('{');
                returnRegion = braceIdx >= 0 ? after.Substring(0, braceIdx) : after;
            }
        }

        var combined = head + " " + paramsRegion + " " + returnRegion;
        var normalized = Regex.Replace(combined.Trim(), @"\s+", " ");
        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>
    /// Extract all `this.X(`, `obj.X(`, `new X(`, and bare `X(` call targets
    /// from a code string. Returns the call prefix (e.g. "this.createBoxMesh",
    /// "createConeMesh", "Foo.bar") without the parens.
    /// </summary>
    private static HashSet<string> ExtractMethodCalls(string code)
    {
        var calls = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(code)) return calls;

        // this.X(  or  obj.X(  — capture the dotted prefix
        foreach (Match m in Regex.Matches(code, @"((?:this|\b[A-Za-z_]\w*)\.[A-Za-z_]\w*)\s*\(", RegexOptions.Compiled))
        {
            var prefix = m.Groups[1].Value;
            // Filter out common false positives: console.log, Math.max, etc. —
            // those are fine to call without checking the file.
            var bare = prefix.Split('.').Last();
            if (IsBuiltinIdentifier(bare)) continue;
            calls.Add(prefix);
        }
        // new X(  — capture X
        foreach (Match m in Regex.Matches(code, @"\bnew\s+([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*\(", RegexOptions.Compiled))
        {
            var name = m.Groups[1].Value;
            var bare = name.Split('.').Last();
            if (IsBuiltinIdentifier(bare)) continue;
            calls.Add(name);
        }
        // Bare X(  — capture X, but only if it's capitalized or preceded by a
        // word boundary so we don't catch keywords like `if(`, `for(`.
        foreach (Match m in Regex.Matches(code, @"\b([A-Z][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled))
        {
            var name = m.Groups[1].Value;
            if (IsBuiltinIdentifier(name)) continue;
            calls.Add(name);
        }
        return calls;
    }

    /// <summary>
    /// Built-in identifiers that are always safe to call without checking the
    /// file (console, Math, JSON, Promise, Array, Object, etc.). Also C#/
    /// TS keywords that look like calls (`if`, `for`, `switch`...).
    /// </summary>
    private static bool IsBuiltinIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        // Lowercase keywords / control flow.
        var keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if","for","while","switch","return","using","lock","catch","throw",
            "function","typeof","instanceof","in","of","do","else","try","finally",
            "await","async","yield","new","delete","void","this","super","extends",
            "implements","interface","class","struct","enum","namespace","import",
            "export","from","as","is","out","ref","params","var","let","const",
        };
        if (keywords.Contains(name)) return true;
        // Capitalized globals / standard library.
        var builtins = new HashSet<string>(StringComparer.Ordinal)
        {
            "Math","JSON","Object","Array","String","Number","Boolean","Date",
            "Promise","Map","Set","WeakMap","WeakSet","Symbol","Reflect","Proxy",
            "Error","TypeError","RangeError","SyntaxError","RegExp","Function",
            "Console","console","window","document","globalThis","global",
            "Number","BigInt","Intl","WebAssembly","process","Buffer",
            "Task","List","Dictionary","HashSet","Enumerable","Action","Func",
            "Tuple","ValueTuple","KeyValuePair","Nullable","Convert","Console",
            "Exception","InvalidOperationException","ArgumentException","Guid",
            "DateTime","TimeSpan","StringBuilder","Regex","Encoding","JsonSerializer",
            "Path","File","Directory","Environment","Math","Random","CancellationToken",
        };
        if (builtins.Contains(name)) return true;
        // Common framework prefixes are fine if used with a known target.
        return false;
    }

    /// <summary>
    /// Deterministically reformats the CSS rule block(s) touched by the applied
    /// edit. Fixes two specific LLM-generated defects:
    ///   1. Missing space after ':' in declarations  (e.g. "flex:1;" -> "flex: 1;")
    ///   2. Inconsistent indentation of properties inside a rule block
    ///      (e.g. 1-space indent when the rest of the file uses 2-space indent).
    /// Only the enclosing rule block(s) of the edited region are rewritten, so
    /// unrelated parts of the file are left byte-for-byte unchanged.
    /// Handles .css / .scss / .less / .sass.  Preserves:
    ///   * Selectors, nested rules, @media / @include / @if / @each blocks
    ///   * Comments (// and /* */)
    ///   * SCSS variables ($var), mixins, parent selectors (&)
    ///   * URL literals (url(http://...)) — the ':' inside URLs is untouched
    /// </summary>
    private string FormatCssEditedRegion(string content, string appliedNewStr)
    {
        if (string.IsNullOrWhiteSpace(appliedNewStr) || string.IsNullOrWhiteSpace(content))
            return content;

        var fileLines = content.Split('\n');

        // ── Detect the file's dominant property-indent step ────────────────
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
                        // Preserve existing plan items, mark them as done
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

    /// <summary>
    /// After a successful edit, detect if the edit changed a method signature
    /// (e.g., added a parameter). If so, search all .cs files for call sites
    /// and update them with sub-step edits.
    /// </summary>
    private async Task<int> HandleMethodSignatureChange(
        string fullPath, string relPath,
        string oldStr, string newStr,
        string projectRoot, bool emitSse, CancellationToken ct,
        int stepIndex, List<object> allResults, string? cardId)
    {
        // ── Detect method signature change ──────────────────────────────
        var oldMatch = MethodDeclRegex.Match(oldStr);
        var newMatch = MethodDeclRegex.Match(newStr);
        if (!oldMatch.Success || !newMatch.Success)
            return stepIndex;

        var oldMethodName = oldMatch.Groups[1].Value;
        var newMethodName = newMatch.Groups[1].Value;
        if (!string.Equals(oldMethodName, newMethodName, StringComparison.Ordinal))
            return stepIndex; // different method — not a signature change

        var oldParams = oldMatch.Groups[2].Value;
        var newParams = newMatch.Groups[2].Value;
        if (string.Equals(oldParams, newParams, StringComparison.Ordinal))
            return stepIndex; // params identical — not a signature change

        await EmitLog(emitSse, "info",
            $"Method signature change detected: {oldMethodName}({oldParams}) → {newMethodName}({newParams}). Searching for call sites...", ct: ct);

        // ── Find all .cs files in the project ──────────────────────────
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

        // ── Find files containing the method name ──────────────────────
        var methodNameLower = oldMethodName.ToLowerInvariant();
        var candidateFiles = new List<string>();
        foreach (var f in csFiles)
        {
            if (string.Equals(f, fullPath, StringComparison.OrdinalIgnoreCase))
                continue; // skip the file that was just edited
            try
            {
                // Quick check: does the file contain the method name?
                using var sr = new StreamReader(f, Encoding.UTF8);
                var firstFewKb = new char[4096];
                var read = await sr.ReadAsync(firstFewKb, 0, firstFewKb.Length);
                var head = new string(firstFewKb, 0, read);
                if (head.Contains(methodNameLower, StringComparison.OrdinalIgnoreCase))
                    candidateFiles.Add(f);
            }
            catch { /* skip unreadable files */ }
        }

        if (candidateFiles.Count == 0)
        {
            await EmitLog(emitSse, "info", "No call site files found.", ct: ct);
            return stepIndex;
        }

        await EmitLog(emitSse, "info",
            $"Found {candidateFiles.Count} file(s) containing '{oldMethodName}' — checking for call sites...", ct: ct);

        // ── For each candidate file, use LLM to find and fix call sites ─
        foreach (var candidateFile in candidateFiles)
        {
            ct.ThrowIfCancellationRequested();

            var fileContent = await System.IO.File.ReadAllTextAsync(candidateFile, Encoding.UTF8, ct);
            var candidateRelPath = Path.GetRelativePath(projectRoot, candidateFile).Replace('\\', '/');

            // Build LLM prompt to update call sites in this file
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

            // Parse the JSON response
            var cleanJson = callSitesJson.Trim();
            if (cleanJson.StartsWith("```"))
            {
                var m = Regex.Match(cleanJson, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (m.Success) cleanJson = m.Groups[1].Value.Trim();
            }

            List<Dictionary<string, string>>? callSiteEdits = null;
            try { callSiteEdits = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(cleanJson); }
            catch { /* try repair */ }

            if (callSiteEdits == null || callSiteEdits.Count == 0)
                continue;

            await EmitLog(emitSse, "info",
                $"  {candidateRelPath}: {callSiteEdits.Count} call site edit(s) suggested", ct: ct);

            // ── Apply each suggested call site edit ─────────────────
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
        "  \"_explore\"            — Read a file NOT YET in the discovery context for REFERENCE only (no edits). Put the file path in \"change\". Do NOT use _explore for files whose content is already shown in the DISCOVERY CONTEXT section — they have already been read.\n" +
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
        "1. Only reference files that exist in the discovery context. Files whose content is shown in the DISCOVERY CONTEXT have already been read — do NOT add _explore steps for them.\n" +
        "2. Plan the COMPLETE set of steps needed to finish this task in ONE shot — usually 1-4 steps. " +
        "Do NOT artificially limit yourself to 1-2 steps so you can be re-invoked later — under-planning " +
        "causes repeated re-invocations that tend to invent redundant or conflicting follow-up edits. " +
        "If the task is a single coherent code change (e.g. two related assignments in the same block, " +
        "or one method body), output exactly ONE step for it.\n" +
        "3. WEB FIRST: add a _web_search step if you need current API docs or recent data.\n" +
        "4. COMMANDS BEFORE EDITS: if a file must exist first, add _command BEFORE the edit step.\n" +
        "5. SELF-STOP: emit a single _done step if the code already satisfies the requirement.\n" +
        "   The DISCOVERY CONTEXT section above shows the ACTUAL content of files that were read.\n" +
        "   Check that content BEFORE planning an edit step. If the property, method, or config\n" +
        "   already exists in the file shown in discovery context, do NOT create an edit step.\n" +
        "   Use _done instead.\n" +
        "6. Score precisely:\n" +
        "   90-100: Exact file + precise change description, no uncertainty\n" +
        "   70-89:  Correct file, good description, minor refinement possible\n" +
        "   40-69:  File identified but change is vague or approach is uncertain\n" +
        "   0-39:  Unsure which file or what to change.\n" +
        "   Be decisive. If you have the right file and a clear change, score 85+. Do NOT stay low when the plan is solid.\n" +
         "7. Each step must be ONE atomic change — do NOT combine two edits in one step.\n" +
         "   BAD (combined): \"Add CalendarNotificationsEnabled property to UserSettings and wire up the toggle checkbox\"\n" +
         "   GOOD (decoupled): Two steps — Step 1: \"Add CalendarNotificationsEnabled property to UserSettings\", Step 2: \"Wire up CalendarNotificationsEnabled toggle in HTML\"\n" +
         "   IMPORTANT: MOVING content from one location to another is NOT a single step. It is TWO steps:\n" +
         "     Step 1: Add/copy the content at the new location\n" +
         "     Step 2: Remove the content from the old location\n" +
         "   BAD (combined): \"Move the link button from the action column into the todo text column\"\n" +
         "   GOOD (decoupled): Step 1: \"Append link button at end of todo text cell, after the text content\", Step 2: \"Remove link button from the action column\"\n" +
         "   If your change description contains the word \"and\" joining two distinct actions, SPLIT it into separate steps.\n" +
         "   Each step's change field must be extremely precise. Ex: \"In UserSettings class: add CalendarNotificationsEnabled property with default true\"\n" +
        "8. If the user stated any constraints (e.g. 'do not use x'), include them verbatim in the 'change' field.\n" +
        "9. If the file path contains \"\\\\\" escape it for JSON: use \"path/to/file.ext\"\n" +
        "10. For each edit step (relative path in \"file\"), also set \"referenceFiles\" to a list of file paths the edit pipeline should load as context. Include files that define types, methods, or patterns the edit needs to reference. This keeps the edit context small and focused.\n" +
        "11. When editing a component/UI file or making changes involving imports/aliases, first read the target file's imports. Include the import source files in \"referenceFiles\" so the edit pipeline can verify aliases are correct before making changes.\n" +
         "12. NEVER use _web_search to find, read, or understand code that exists inside this project's repository. " +
         "For reading project source files use _explore with the relative file path. " +
         "_web_search is ONLY for external resources (public docs, npm packages, Stack Overflow, API references). " +
         "If you don't know which file contains the code, add an _explore step first.\n" +
         "13. Describe plan steps as the MINIMAL delta needed. The DISCOVERY CONTEXT section shows actual file content. " +
         "DO NOT re-describe existing functionality as something that needs to be built. " +
         "BAD: \"Modify GetUsersWithCalendarNotificationsEnabled to collect all events per user and send Firebase notifications\" " +
         "(the method already collects events — \"collect\" is wrong). " +
         "GOOD: \"After the existing usersWithEvents loop, send Firebase notification for each user with the events list\" " +
         "(describes only the missing logic). " +
         "Read the file body in DISCOVERY CONTEXT to understand what already exists, then describe ONLY what is missing.\n\n" +
         "### OUTPUT FORMAT ###\n" +
        "{\n" +
        "  \"thinking\": \"1-2 lines: which file needs changing and why\",\n" +
        "  \"summary\": \"one sentence: what this step accomplishes\",\n" +
        "  \"score\": <0-100>,\n" +
        "  \"plan\": [\n" +
        "    {\n" +
        "      \"file\": \"wwwroot/app.js\",\n" +
        "      \"change\": \"Modify confirmFilePicker to append files to existing list\",\n" +
        "      \"referenceFiles\": [\"wwwroot/utils.js\", \"wwwroot/types.js\"]\n" +
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
        sb.AppendLine(JsonSerializer.Serialize(plan!.Plan, new JsonSerializerOptions { WriteIndented = true }));

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

        var heuristicCandidates = AgentUtilities.ApplyTaskTypeHeuristics(prompt, allFiles);
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
    private AgentPlan DeduplicatePlan(AgentPlan? plan)
    {
        if (plan?.Plan == null || plan.Plan.Count == 0)
            return plan!;

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
                try
                {
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
        if (fb >= 0 && lb > fb)
        {
            var bc = cleaned[fb..(lb + 1)];
            if (LooksLikePlanJson(bc))
            {
                jsonBlocks.Add(bc);
            }
        }
        foreach (var candidate in jsonBlocks.Distinct())
        {
            foreach (var repaired in AgentUtilities.GeneratePlanJsonCandidates(candidate))
            {
                try
                {
                    var result = JsonSerializer.Deserialize<AgentPlan>(repaired, opts);
                    if (result?.Plan != null)
                    {
                        return DeduplicatePlan(result);
                    }
                }
                catch { }
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
        string? cardId = null, bool createTests = false)
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
                    new
                    {
                        thinking = existingPlan.Thinking,
                        summary = existingPlan.Summary,
                        items = existingPlan.Plan,
                        resumed = true
                    }, ct);

            var resumeSteps = new List<object>();
            await ExecutePlan(prompt, projectRoot, emitSse, "", existingPlan, ct, resumeSteps,
                steeringContext: steeringContext, attachedFiles: attachedFiles,
                completedStepIndices: completedStepIndices, cardId: cardId);

            var allStepsAlreadyDone = completedStepIndices != null && completedStepIndices.Count >= existingPlan.Plan.Count;
            return (resumeSteps, existingPlan, (resumeSteps.Count > 0) || allStepsAlreadyDone);
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
                        await EmitLog(emitSse, "info", "All steps done — checking for genuinely missing work…", ct: ct);
                        // Every planned step succeeded, so any extra steps must be anchored to the
                        // user's explicit request — not invented scope. GenerateReplanStepsAsync
                        // returns an empty plan when nothing is genuinely missing, which stops here.
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
                            plan = MergePlans(plan ?? new AgentPlan(), new AgentPlan { Plan = newSteps });
                            if (emitSse)
                                await SendSse(Response, "plan",
                                    new { thinking = plan.Thinking, summary = "Replan: added steps", items = plan.Plan }, ct);
                            await PersistBoardDataPlanAsync(cardId, plan.Plan, emitSse, ct,
                                summary: plan.Summary ?? "Replan: added steps", score: plan.Score);

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

        // ── Test creation pipeline ────────────────────────────────────────
        if (createTests && isEdited)
        {
            await RunTestCreationPipeline(projectRoot, allSteps, emitSse, ct);
        }

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
    /// <summary>
    /// Produces a compact version of the exploration context for the edit-resolve LLM:
    /// - Removes the target file entirely (it is already shown in the main prompt)
    /// - For each auxiliary file: keeps imports, type/class declarations, property signatures,
    ///   and a ±3-line window around any keyword/symbol match — strips method bodies
    /// - Caps output at maxChars so the edit LLM isn't overwhelmed with unrelated code
    /// </summary>
    private static string DistillExplorationContext(
        string explorationContext,
        string targetRelPath,
        string changeDesc,
        string? targetSymbol,
        int maxChars = 7_000)
    {
        if (string.IsNullOrWhiteSpace(explorationContext)) return "";

        var keywords = AgentUtilities.ExtractMeaningfulKeywords(changeDesc.ToLowerInvariant())
            .Where(k => k.Length >= 4)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(targetSymbol))
            keywords.Add(targetSymbol);

        var normalizedTarget = targetRelPath.Replace('\\', '/');

        // Split on section headers ("### ") — each file read in the loop produces one section
        var sections = Regex.Split(explorationContext.Trim(), @"(?=^### )", RegexOptions.Multiline);
        var result = new StringBuilder();

        foreach (var rawSection in sections)
        {
            if (string.IsNullOrWhiteSpace(rawSection)) continue;

            // Skip the target file — already shown verbatim in the main prompt
            var firstLine = rawSection.Split('\n')[0];
            if (firstLine.Contains("TARGET FILE:", StringComparison.OrdinalIgnoreCase)) continue;
            var sectionPath = Regex.Match(firstLine, @"###\s+([^\s(]+)").Groups[1].Value
                .Replace('\\', '/').Trim();
            if (string.Equals(sectionPath, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                continue;

            var distilled = DistillFileSection(rawSection, keywords);
            if (string.IsNullOrWhiteSpace(distilled)) continue;

            var budget = maxChars - result.Length;
            if (budget < 100) { result.AppendLine("... [context budget exhausted]"); break; }
            if (distilled.Length > budget)
                distilled = distilled[..budget] + "\n    // ... [truncated]";
            result.AppendLine(distilled);
        }

        return result.ToString();
    }

    /// <summary>
    /// From a single "### filepath\n```\n...\n```" section, extracts:
    /// - First 20 lines of the code block (imports + class/type declarations)
    /// - ±3 lines around any line that contains a keyword or target symbol
    /// Skips all other implementation lines, reducing an average file from
    /// 3 000+ chars to ~400–800 chars while preserving type information.
    /// </summary>
    private static string DistillFileSection(string section, HashSet<string> keywords, int maxCharsPerSection = 1_800)
    {
        var lines = section.Split('\n');
        var headerLines = new List<string>();
        var codeLines = new List<string>();
        var openingFence = "";
        var inFence = false;
        var pastFirstFence = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```"))
            {
                if (!pastFirstFence)
                {
                    pastFirstFence = true;
                    inFence = true;
                    openingFence = line;
                }
                else if (inFence)
                {
                    inFence = false;
                    break; // stop after the first code block
                }
                continue;
            }
            if (inFence)
                codeLines.Add(line);
            else
                headerLines.Add(line);
        }

        if (codeLines.Count == 0)
            return string.Join("\n", headerLines); // prose-only section, keep as-is

        // ── Determine which code lines to include ────────────────────────────
        var included = new SortedSet<int>();

        // Always include the first 20 lines — imports and type declarations live here
        for (var i = 0; i < Math.Min(20, codeLines.Count); i++)
            included.Add(i);

        // Include ±3 lines around any keyword match throughout the rest of the file
        for (var i = 20; i < codeLines.Count; i++)
        {
            if (keywords.Count == 0) break;
            if (keywords.Any(kw => codeLines[i].Contains(kw, StringComparison.OrdinalIgnoreCase)))
            {
                for (var w = Math.Max(0, i - 3); w <= Math.Min(codeLines.Count - 1, i + 3); w++)
                    included.Add(w);
            }
        }

        // ── Build output, inserting gap markers between non-contiguous ranges ─
        var result = new List<string>(headerLines) { openingFence };
        var prevIdx = -2;

        foreach (var idx in included)
        {
            if (prevIdx >= 0 && idx > prevIdx + 1)
                result.Add("    // ...");
            result.Add(codeLines[idx]);
            prevIdx = idx;
        }

        if (prevIdx < codeLines.Count - 1)
            result.Add("    // ...");

        result.Add("```");

        var output = string.Join("\n", result);
        return output.Length > maxCharsPerSection
            ? output[..maxCharsPerSection] + "\n    // ... [truncated]"
            : output;
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

        // Show what was already planned and their results
        if (existingPlan?.Plan?.Count > 0)
        {
            sb.AppendLine("## Existing plan with results");
            foreach (var step in existingPlan.Plan)
            {
                // Look up the result for this step
                string? status = null;
                string? output = null;
                if (executedSteps != null)
                {
                    var result = executedSteps.OfType<Dictionary<string, object?>>()
                        .LastOrDefault(s =>
                            string.Equals(s.GetValueOrDefault("path")?.ToString(), step.File, StringComparison.OrdinalIgnoreCase) ||
                            (string.Equals(s.GetValueOrDefault("type")?.ToString(), step.File, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(s.GetValueOrDefault("command")?.ToString(), step.Change, StringComparison.OrdinalIgnoreCase)));
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

        sb.AppendLine("STOP AND THINK: does the CURRENT FILE CONTENT shown above ALREADY satisfy the ORIGINAL TASK, even imperfectly?");
        sb.AppendLine("If the explicit request is met, return an EMPTY plan — do NOT propose restructuring, renaming, moving code");
        sb.AppendLine("between methods, or 'cleanup' the user did not ask for.");
        sb.AppendLine("Only add a step if you can name a SPECIFIC piece of the ORIGINAL TASK that is VERIFIABLY absent from the");
        sb.AppendLine("current file content above.");
        sb.AppendLine("NEVER introduce a property/variable name that does not already appear in the current file content above —");
        sb.AppendLine("reuse existing names exactly, character for character.");
        sb.AppendLine("Add at most 1 new step. If everything is done or you are unsure, return an EMPTY plan with no steps.");
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
    private const int PlanScoreThreshold = 65;
    /// <summary>Upper bound on planning iterations so a low-scoring/exploring model still terminates.</summary>
    private const int MaxPlanningIterations = 3;

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
        var exploredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var iter = 1; iter <= MaxPlanningIterations; iter++)
        {
            var plan = await AnalyzePromptAndPlanCodeChanges(
                prompt, discoveryContext, projectRoot, emitSse, ct, steering);

            if (plan == null || plan.Plan.Count == 0)
            {
                if (best != null) break; // reuse the last good plan rather than failing
                throw new InvalidOperationException("LLM returned an empty or unparseable plan.");
            }

            // Deduplicate plan steps: merge steps with the same file and similar
            // change description (normalized). The planner often hallucinates the
            // same step repeatedly despite "AT MOST 2 steps" in the prompt.
            plan.Plan = DeduplicateSteps(plan.Plan);

            // Cap total steps — the planner should produce ≤2 per iteration, but
            // we enforce a hard limit as a safety net against runaway hallucination.
            const int MaxPlanSteps = 5;
            if (plan.Plan.Count > MaxPlanSteps)
            {
                await EmitLog(emitSse, "warn",
                    $"Plan has {plan.Plan.Count} steps (max {MaxPlanSteps}) — truncating to first {MaxPlanSteps}", ct: ct);
                plan.Plan = plan.Plan.Take(MaxPlanSteps).ToList();
            }

            // If the planner asked to read more files, gather that context and replan.
            // Exploration rounds never count as a converged plan, so _explore steps can
            // never leak into the executable plan.
            var exploreSteps = plan.Plan
                .Where(p => p.File.Equals("_explore", StringComparison.OrdinalIgnoreCase)).ToList();

            // Also detect steps where the LLM used a regular file path with a read-only
            // change description (e.g. "Read the file…") instead of the _explore marker.
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

            // Deduplicate: skip files already explored in a prior iteration
            var newExploreSteps = exploreSteps
                .Where(s => !string.IsNullOrWhiteSpace(s.Change) && exploredFiles.Add(s.Change))
                .ToList();

            if (newExploreSteps.Count > 0)
            {
                await EmitLog(emitSse, "info",
                    $"Planning {iter}/{MaxPlanningIterations}: planner requested {newExploreSteps.Count} new exploration target(s) — gathering context…", ct: ct);
                discoveryContext = await ExplorationPipeline(newExploreSteps, discoveryContext, projectRoot, emitSse, ct);
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

    /// <summary>Steering that nudges a low-confidence planner to sharpen steps — never to invent extra work or explore.</summary>
    private static string BuildLowScoreSteering(AgentPlan plan, string? prior)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Your previous plan scored {plan.Score}/100, below the confidence threshold of {PlanScoreThreshold}.");
        sb.AppendLine("Do NOT explore more files. The discovery context already has everything you need.");
        sb.AppendLine("Raise your score by making each step's change description more precise:");
        sb.AppendLine("  • Name the exact method, property, or line range (e.g. \"In getUser() around line 42:…\")");
        sb.AppendLine("  • Describe the exact old → new behavior clearly");
        sb.AppendLine("  • If the plan is already correct, simply increase your score to 85+ and re-output it");
        sb.AppendLine("Do NOT change the file paths or add steps. Do NOT add _explore steps.");
        if (!string.IsNullOrWhiteSpace(prior)) { sb.AppendLine(); sb.AppendLine(prior); }
        return sb.ToString();
    }

    /// <summary>Steering that forces the planner to stop exploring and emit the final edit plan.</summary>
    private static string AppendExploreSteering(string? prior)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You have exhausted exploration rounds. Produce the final edit plan NOW — no more _explore steps.");
        sb.AppendLine("Be decisive: keep the same file paths and give each step a precise change description. Score the plan 85+ if it's correct.");
        if (!string.IsNullOrWhiteSpace(prior)) { sb.AppendLine(); sb.AppendLine(prior); }
        return sb.ToString();
    }

    /// <summary>Deduplicate plan steps that have the same file and similar change description.</summary>
    private static List<PlanStep> DeduplicateSteps(List<PlanStep> steps)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<PlanStep>();
        foreach (var step in steps)
        {
            var normChange = (step.Change ?? "").Trim().ToLowerInvariant();
            // Normalize whitespace so minor phrasing differences collapse
            normChange = string.Join(" ", normChange.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
            var key = $"{step.File}|{normChange}";
            if (seen.Add(key))
                deduped.Add(step);
        }
        return deduped;
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
        // ── Load per-project edit knowledge (EditKnowledgeService) ────────
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

        // Phase 1: Discover
        await EmitLog(emitSse, "info", "Phase 1 — DISCOVER", new { prompt, attachedFiles, steeringContext, cardId }, ct: ct);
        var (discoveryContext, ds) = await RunBootstrapDiscovery(prompt, projectRoot, emitSse, attachedFiles, ct);
        allSteps.AddRange(ds);

        // Prepend edit-knowledge header to the discovery context so the planner
        // sees it on every planning iteration. Putting it AFTER the discovery
        // context keeps file content primary; the knowledge header is a
        // focused, short summary that won't blow up the token budget.
        if (!string.IsNullOrWhiteSpace(editKnowledgeHeader))
        {
            discoveryContext = editKnowledgeHeader + "\n\n" + discoveryContext;
        }


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
        }
        else
        {
            await EmitLog(emitSse, "success", $"Plan validation passed.", ct: ct);
        }

        // Phase 2.9: Plan Pre-Audit — check for already-done steps and multi-concern steps
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

                    if (emitSse)
                        await SendSse(Response, "plan",
                            new { thinking = plan.Thinking, summary = plan.Summary, items = plan.Plan, audited = true }, ct);
                }
            }
        }

        // Phase 3: Execute
        await EmitLog(emitSse, "info", "Phase 3 — EXECUTE", ct: ct);
        if (emitSse)
        {
            await SendSse(Response, "phase", new { phase = "execute", message = "Executing plan…" }, ct);
        }

        await ExecutePlan(prompt, projectRoot, emitSse, discoveryContext, plan ?? new AgentPlan(), ct, allSteps,
            steeringContext: steeringContext, attachedFiles: attachedFiles,
            cardId: cardId);

        // ── Post-execution verification: re-check with LLM that task is 100% complete ──
        var (taskComplete, verificationDetails) = await PostExecuteVerify(prompt, projectRoot, emitSse, allSteps, ct);
        if (!taskComplete)
        {
            await EmitLog(emitSse, "warn", $"Post-execution verification: {verificationDetails}. Re-planning...", ct: ct);
            // Re-read all touched files from disk so the planner sees current code, not stale
            // step outputs. BuildDiscoveryTextFromSteps misses edit steps (no "output" field),
            // leaving context empty and causing the planner to fall back to _web_search.
            var touchedPaths = allSteps.OfType<Dictionary<string, object?>>()
                .Select(s => s.GetValueOrDefault("path")?.ToString())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var freshSb = new StringBuilder("ONLY use paths that appear below. Do NOT invent paths.\n\n");
            foreach (var rp in touchedPaths.Take(10))
            {
                var fp2 = Path.GetFullPath(
                    Path.Combine(projectRoot, rp!.Replace('/', Path.DirectorySeparatorChar)));
                if (!System.IO.File.Exists(fp2)) continue;
                try
                {
                    var fc2 = await System.IO.File.ReadAllTextAsync(fp2, Encoding.UTF8, ct);
                    freshSb.AppendLine($"### read {rp}");
                    freshSb.AppendLine($"```\n{fc2}\n```\n");
                }
                catch { /* skip unreadable files */ }
            }
            var freshContext = freshSb.Length > 100
                ? freshSb.ToString()
                : AgentUtilities.BuildDiscoveryTextFromSteps(allSteps);
            // Append verification gaps to freshContext so the replanner knows what's still missing.
            // We include only the gap descriptions (issues/reason), not any wrong-approach artifact
            // names from the original file that the task was meant to replace.
            if (!string.IsNullOrWhiteSpace(verificationDetails))
            {
                freshContext = freshContext + "\n### GAPS REMAINING FROM VERIFICATION ###\n" + verificationDetails;
            }
            var verifySteering = !string.IsNullOrWhiteSpace(steeringContext)
                ? steeringContext
                : null;
            var replan = await AnalyzePromptAndPlanCodeChanges(
                prompt, freshContext, projectRoot, emitSse, ct, verifySteering);
            if (replan != null && replan.Plan.Count > 0)
            {
                // Emit plan SSE so the frontend card shows the new step(s)
                if (emitSse)
                    await SendSse(Response, "plan",
                        new { thinking = replan.Thinking, summary = $"Re-plan: {replan.Summary}", items = replan.Plan }, ct);

                // Persist to boarddata so step progress is tracked in the card — append to existing steps, don't replace
                await PersistBoardDataPlanAsync(cardId, replan.Plan, emitSse, ct,
                    summary: $"Re-plan: {replan.Summary}", score: replan.Score, append: true);

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
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (modifiedPaths.Count == 0)
        {
            // No files were edited — check if files were explored (read-only)
            // and verify with the LLM whether the original task needs edits.
            var exploredPaths = allResults
                .OfType<Dictionary<string, object?>>()
                .Where(r => r.TryGetValue("type", out var t) && t?.ToString() == "read")
                .Select(r => r.GetValueOrDefault("path")?.ToString())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (exploredPaths.Count == 0)
                return (true, ""); // nothing to verify — no steps at all
            // Treat explored files as "current state" so the LLM
            // can determine if code changes are still needed.
            modifiedPaths = exploredPaths;
        }

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
        sb.AppendLine("1. Is the original task fully implemented? Evaluate ONLY against what the original task asks for.");
        sb.AppendLine("   Ignore existing code that predates this task — the task may be meant to REPLACE it.");
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

    /// <summary>After a step completes, check if more plan steps are needed and ask the LLM.</summary>
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
        // Per-execution budget: TryReplanAfterStep may add at most ONE extra round of
        // steps. Genuinely missing work beyond that is handled by PostExecuteVerify's
        // single, well-grounded replan (full file content + compile-check), not by
        // repeated cheap re-invocations that tend to hallucinate.
        var replanBudget = new[] { 1 };

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
                var stepSkipped = false;
                await EmitLog(emitSse, "info", $"Creating file: {changeDesc}", ct: ct);
                var prevCount = allResults.Count;
                var cr = await HandleCreateFile(changeDesc, projectRoot, prompt, discoveryContext, stepIndex, emitSse, ct, null, attachedFiles);
                stepIndex += cr.stepsCount; allResults.AddRange(cr.results);
                planItems = await TryReplanAfterStep(prompt, allResults, plan,
                    steeringContext, projectRoot, emitSse, ct, planItems, itemIdx,
                    stepSkipped, allResults.Count > prevCount, attachedFiles, replanBudget, cardId: cardId);
                continue;
            }

            if (planFile.Equals("_ping", StringComparison.OrdinalIgnoreCase))
            { stepIndex = await ExecutePingStep(changeDesc, projectRoot, emitSse, ct, allResults, stepIndex); continue; }

            if (planFile.Equals("_package_install", StringComparison.OrdinalIgnoreCase))
            { stepIndex = await ExecutePackageInstallStep(changeDesc, projectRoot, emitSse, ct, allResults, stepIndex); continue; }

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
                // Safeguard: detect read-only intent (e.g. "Look at", "Read", "Examine")
                // when the LLM used a regular file path instead of _explore.
                // Treat as exploration (read + add result) rather than an edit.
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

                // ── Per-step decoupling check: split into sub-steps if needed ──
                var decoupledSubSteps = await CheckAndDecoupleStepAsync(
                    item, itemIdx, projectRoot, emitSse, ct, allResults, cardId, prompt);
                if (decoupledSubSteps?.Count > 0)
                {
                    await EmitLog(emitSse, "info",
                        $"Step {itemIdx + 1} decoupled into {decoupledSubSteps.Count} sub-steps: " +
                        string.Join(" | ", decoupledSubSteps.Select(s => s.Change)), ct: ct);
                    planItems.RemoveAt(itemIdx);
                    planItems.InsertRange(itemIdx, decoupledSubSteps);
                    if (!string.IsNullOrWhiteSpace(cardId))
                        await PersistBoardDataPlanAsync(cardId, planItems, emitSse, ct);
                    // Send a "plan" SSE event so the frontend immediately shows the updated steps
                    var planItemsJson = new JsonArray();
                    for (var pi = 0; pi < planItems.Count; pi++)
                    {
                        planItemsJson.Add(new JsonObject
                        {
                            ["index"] = pi,
                            ["file"] = planItems[pi].File,
                            ["change"] = planItems[pi].Change,
                            ["priority"] = planItems[pi].Priority,
                            ["done"] = allResults.Any(r => r is Dictionary<string, object?> dict && dict.TryGetValue("planItemIndex", out var pii) && pii is int piiVal && piiVal == pi && dict.TryGetValue("status", out var st) && st is string stStr && stStr == "done")
                        });
                    }
                    await SendSse(Response, "plan", new
                    {
                        thinking = $"Step {itemIdx + 2} decoupled into {decoupledSubSteps.Count} sub-steps",
                        summary = "Plan updated after decoupling",
                        items = planItemsJson
                    }, ct);
                    itemIdx--;
                    continue;
                }

                // ResolveAndApplyEdit handles plan-provided oldString + unbounded LLM retries internally
                var prevCount = allResults.Count;
                stepIndex = await ResolveAndApplyEdit(item, projectRoot, emitSse,
                    ct, allResults, stepIndex,
                    prompt, plan,
                    itemIdx, cardId, attachedFiles);

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

                // After successful HTML edit, detect references to undefined methods and add steps
                if (status == "done" && planFile.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                {
                    // Only scan the newStr from the edit itself — never the whole file,
                    // so we only flag methods the edit INTRODUCED, not inherited ones.
                    var editNewStr = allResults.Count > prevCount &&
                        allResults[^1] is Dictionary<string, object?> lastEditDict
                        ? lastEditDict.GetValueOrDefault("newStringPreview")?.ToString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(editNewStr))
                    {
                        // Extract function names from Angular event bindings in the edit's newStr
                        var funcMatches = Regex.Matches(editNewStr,
                            @"\(\w+\)=\""[^\""]*?(\w+)\s*\(");
                        if (funcMatches.Count > 0)
                        {
                            // Find corresponding .ts file (same directory, same base name)
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
                                // Skip common Angular lifecycle hooks, built-in DOM events, etc.
                                if (funcName is "ngOnInit" or "ngOnDestroy" or "ngAfterViewInit" or "ngOnChanges" or "ngDoCheck" or "ngAfterContentInit" or "ngAfterContentChecked" or "ngAfterViewChecked" or "toggle" or "open" or "close" or "preventDefault" or "stopPropagation" or "console")
                                    continue;
                                // Skip if already seen in this batch (e.g. same method referenced in two buttons)
                                if (!seenMethods.Add(funcName))
                                    continue;
                                // Check if function exists in local .ts or anywhere in the project
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
                                    // Even if the function exists, check if the arguments passed in the HTML
                                    // reference properties that don't match the method body — this catches
                                    // the LLM reusing a generic method (e.g. onPageChange) for a different
                                    // data type (e.g., YouTube pagination) where a new method is needed.
                                    var argsText = "";
                                    try
                                    {
                                        // Extract argument text from the function call in editNewStr
                                        var callStart = m.Index + m.Length;
                                        if (callStart > 0 && callStart < (editNewStr?.Length ?? 0))
                                        {
                                            var remaining = editNewStr!.Substring(callStart);
                                            var closeParen = remaining.IndexOf(')');
                                            if (closeParen >= 0)
                                                argsText = remaining.Substring(0, closeParen).Trim();
                                        }
                                    }
                                    catch { /* best-effort */ }
                                    if (!string.IsNullOrWhiteSpace(argsText) && !string.IsNullOrWhiteSpace(tsContent))
                                    {
                                        // Extract property-like words from the arguments (camelCase words that aren't numbers/operators)
                                        var argProps = Regex.Matches(argsText, @"\b[a-z]+[a-zA-Z0-9]*\b")
                                            .Cast<Match>().Select(x => x.Value)
                                            .Where(x => !Regex.IsMatch(x, @"^\d+$"))
                                            .Distinct().ToList();
                                        // Extract this.xxx property references from the method body
                                        var methodBodyProps = Regex.Matches(tsContent, @"\bthis\.([a-zA-Z_]\w*)")
                                            .Cast<Match>().Select(x => x.Value)
                                            .Distinct().ToHashSet(StringComparer.OrdinalIgnoreCase);
                                        // If args reference properties the method body doesn't use,
                                        // the LLM probably reused the wrong method — flag it.
                                        var mismatchedArgs = argProps
                                            .Where(a => !a.Contains("this.") &&
                                                !methodBodyProps.Any(b =>
                                                    b.EndsWith("." + a, StringComparison.OrdinalIgnoreCase)))
                                            .ToList();
                                        if (mismatchedArgs.Count > 0 && mismatchedArgs.Count >= argProps.Count / 2)
                                        {
                                            // Generate a more specific method name by replacing the qualifier
                                            var newFuncName = funcName;
                                            // Try to extract a data type qualifier from mismatched args
                                            var youtubeArg = mismatchedArgs
                                                .FirstOrDefault(a => a.IndexOf("youtube", StringComparison.OrdinalIgnoreCase) >= 0
                                                    || a.IndexOf("yt", StringComparison.OrdinalIgnoreCase) >= 0);
                                            if (youtubeArg != null)
                                            {
                                                // The existing method is being called with YouTube-specific args —
                                                // a separate youtube method is needed
                                                var prefix = funcName.StartsWith("on", StringComparison.OrdinalIgnoreCase) ? "onYoutube" : "youtube";
                                                var suffix = funcName.Length > 2 && char.IsUpper(funcName[2]) ? funcName.Substring(2) : funcName;
                                                newFuncName = prefix + char.ToUpper(suffix[0]) + suffix.Substring(1);
                                                // Don't skip — create the missing method step below
                                                foundInProject = false;
                                                funcName = newFuncName;
                                            }
                                        }
                                    }
                                    if (foundInProject)
                                        continue;
                                }
                                // Avoid duplicating steps already in the plan
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
                // ── Post-edit reflection: find missing methods/classes and add steps ──
                if (status is "done" or "modified")
                {
                    var lastEditResult = allResults.Count > prevCount
                        ? allResults[^1] as Dictionary<string, object?> : null;
                    var appliedNewStr = lastEditResult?.GetValueOrDefault("newStringPreview")?.ToString();

                    if (!string.IsNullOrWhiteSpace(appliedNewStr))
                    {
                        // Re-read the file from disk so we have the current full content for context
                        var currentFullPath = Path.GetFullPath(
                            Path.Combine(projectRoot, planFile.Replace('/', Path.DirectorySeparatorChar)));
                        string currentContent = "";
                        try
                        {
                            currentContent = await System.IO.File.ReadAllTextAsync(
                            currentFullPath, Encoding.UTF8, ct);
                        }
                        catch { /* skip on read failure */ }

                        var reflectedSteps = await ReflectOnAppliedEditAsync(
                            planFile, appliedNewStr, currentContent,
                            projectRoot, planItems, emitSse, ct);

                        if (reflectedSteps.Count > 0)
                        {
                            await EmitLog(emitSse, "info",
                                $"  ➕ Reflection added {reflectedSteps.Count} step(s): " +
                                string.Join(" | ", reflectedSteps.Select(s => s.Change)), ct: ct);

                            // Insert immediately after the current step so they run next
                            planItems.InsertRange(itemIdx + 1, reflectedSteps);

                            // Persist the expanded plan to boarddata and emit to frontend
                            await PersistBoardDataPlanAsync(cardId, planItems, emitSse, ct,
                                summary: $"Reflection: +{reflectedSteps.Count} step(s)", score: 0,
                                append: true);

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
                                        done = false
                                    }).ToList()
                                }, ct);
                        }
                    }
                }

                // Step was skipped (already done / irrelevant) or succeeded —
                // check if more work remains by re-running the planner
                planItems = await TryReplanAfterStep(prompt, allResults, plan,
                    steeringContext, projectRoot, emitSse, ct, planItems, itemIdx,
                    stepSkipped, allResults.Count > prevCount, attachedFiles, replanBudget, cardId: cardId);
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
        if (!approved && verifyReason.Contains("SQL whitespace collapsed", StringComparison.OrdinalIgnoreCase))
        {
            var correctedContent = AutoFixSqlWhitespace(newContent);
            if (correctedContent != newContent)
            {
                (approved, verifyReason, score) =
                    VerifyEdit(item.OldString!, item.NewString ?? "", content, correctedContent);
                if (approved) newContent = correctedContent;
            }
        }
        if (!approved) return (false, verifyReason, score);

        await System.IO.File.WriteAllTextAsync(fullPath, newContent, Encoding.UTF8);
        await EmitLog(emitSse, "success", $"✔ Edited {relPath}", ct: ct);
        return (true, "", 10);
    }
    private static (bool approved, string reason, int score) VerifyEdit(
        string oldString, string newString, string oldContent, string newContent, bool fromFormatC = false)
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
            var strippedNew = AgentUtilities.StripLineLeadingWhitespace(normNew);
            var strippedContent = AgentUtilities.StripLineLeadingWhitespace(normNewContent);
            // AutoIndentHtml uses Trim() internally so trailing whitespace is also lost;
            // trim trailing from both to avoid false mismatch.
            var trimmedNew = string.Join("\n", strippedNew.Split('\n').Select(l => l.TrimEnd()));
            var trimmedContent = string.Join("\n", strippedContent.Split('\n').Select(l => l.TrimEnd()));
            if (!trimmedContent.Contains(trimmedNew, StringComparison.Ordinal))
                return (false, "newString not found after replacement", 4);
        }

        // Detect hallucinated property names: LLMs often use common names like Title,
        // Description, StartTime, EndTime that don't exist on the actual model types.
        // If a property name appears nowhere in the old file but the new code uses it,
        // it's likely hallucinated.
        var hallucinatedPropertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "EventTitle", "EventDescription",   // almost always should be Title/Description on an Event type
            "UserName", "UserEmail",            // usually just Email or Username
            "Attendees", "Organizer",           // rarely used; usually People/Owner
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

        // oldString should not appear as-frequently in the result
        // (it was supposed to be replaced). Only check this for non-trivial oldStrings.
        // Skip if oldString is contained inside newString — that means this is an
        // additive insert (insertAfter, append), not a replacement, so oldString
        // should still be present.
        if (!string.IsNullOrEmpty(normOld) && normOld.Length >= 10 && !normNew.Contains(normOld))
        {
            // Strip leading whitespace from each line for indentation-aware comparison
            var strippedOld = AgentUtilities.StripLineLeadingWhitespace(normOld);
            var strippedOldContent = AgentUtilities.StripLineLeadingWhitespace(normOldContent);
            var strippedNewContent = AgentUtilities.StripLineLeadingWhitespace(normNewContent);

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

        // Detect duplicate HTTP-method route attributes (the most common source
        // of LLM-produced method duplication — it copies a whole existing method
        // alongside the new one instead of doing a clean insertion).
        if (!fromFormatC)
        {
            var uniqueRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(newContent,
                @"\[Http(?:Get|Post|Put|Delete|Patch)\(""([^""]+)"""))
            {
                if (!uniqueRoutes.Add(m.Groups[1].Value))
                    return (false,
                        $"Duplicate route \"{m.Groups[1].Value}\" in result — LLM likely " +
                        "copied an entire existing method instead of inserting the new code. " +
                        "Use a precise insertion anchor or insertAfter instead.", 1);
            }
        }
        // Detect empty placeholder classes/structs — the LLM creates them when it
        // doesn't know a type exists. These exist already in the project; grep for them.
        var emptyDecls = Regex.Matches(newContent,
            @"(?:public|private|internal|protected)?\s*(?:class|struct|interface|record)\s+\w+\s*\{\s*(?:\/\*[\s\S]*?\*\/)?\s*\}|" +
            @"(?:public|private|internal|protected)?\s*(?:class|struct|interface|record)\s+\w+\s*\n\s*\{\s*\n\s*\}")
            .Cast<Match>().ToList();
        if (emptyDecls.Count > 0)
        {
            var names = emptyDecls.Select(m => m.Value.Trim()).Distinct().ToList();
            return (false,
                $"Edit introduces empty type(s): {string.Join(", ", names)}. These types already exist in the project — " +
                "find their definition and use the existing type instead of creating a stub.", 1);
        }

        // Detect collapsed SQL whitespace: SPECIFIC patterns like 'INTERVAL15' or 'MINUTE1' where a
        // space was removed between an SQL keyword/operator and a number/literal.
        // Check only for specific SQL keywords that commonly appear before numbers.
        var specificSqlPatterns = new[]
        {
            @"\bINTERVAL\d",          // INTERVAL followed by digit (should be "INTERVAL <number>")
            @"\bDAY\d", @"\bHOUR\d", @"\bMINUTE\d", @"\bSECOND\d",  // Time units
            @"\bLIMIT\d",             // LIMIT without space
            @"\bOFFSET\d",            // OFFSET without space
        };
        foreach (var line in newContent.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 10) continue;
            // Quick SQL check: must contain at least one known SQL keyword
            if (!Regex.IsMatch(trimmed, @"\b(SELECT|FROM|WHERE|AND|INSERT|UPDATE|DELETE|JOIN|INTERVAL|DATE_ADD|LIMIT)\b", RegexOptions.IgnoreCase))
                continue;

            // Check for specific collapsed-whitespace patterns
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

        // Detect SQL table-name replacement: LLM often rewrites SQL from scratch with
        // different tables. Run auto-fix first so whitespace differences don't false-fire.
        var fixedOld = AutoFixSqlWhitespace(normOldContent);
        var fixedNew = AutoFixSqlWhitespace(normNewContent);
        var oldTables = ExtractSqlTableNames(fixedOld);
        var newTables = ExtractSqlTableNames(fixedNew);
        if (oldTables.Count > 0 && newTables.Count > 0)
        {
            var missingTables = oldTables.Where(t => !newTables.Contains(t)).ToList();
            if (missingTables.Count > 0)
            {
                // Find a return statement in the original method body as anchor suggestion
                var returnAnchor = FindLastReturnLine(normOld);
                var anchorHint = returnAnchor != null
                    ? $" Anchor on the return statement: oldString=\"{returnAnchor.Trim()}\""
                    : "";
                return (false,
                    $"Edit replaces existing SQL table(s) [{string.Join(", ", missingTables.Take(3))}] with different tables. " +
                    "Preserve the original query structure; only add the required logic." + anchorHint, 1);
            }
        }

        // Detect Angular template global references that don't work
        // In Angular templates, Math, window, console, JSON, parseInt, etc. are NOT accessible.
        if (IsAngularTemplate(newContent))
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

    /// <summary>Find the last 'return ...;' line in a code fragment (e.g. a method body)
    /// for use as an oldString anchor suggestion.</summary>
    private static string? FindLastReturnLine(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        var lines = code.Split('\n', StringSplitOptions.None);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("return ") && trimmed.EndsWith(";"))
                return lines[i];
        }
        return null;
    }

    /// <summary>Check if content looks like an Angular HTML template (has Angular-specific syntax).</summary>
    private static bool IsAngularTemplate(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length < 20)
            return false;
        return Regex.IsMatch(content, @"\*ng(If|For|Switch)") ||
               Regex.IsMatch(content, @"\(click\)|\(change\)|\(keydown\)|\(submit\)|\(focus\)|\(blur\)") ||
               (content.Contains("{{") && content.Contains("}}"));
    }

    /// <summary>Extract table names from inline SQL strings in C# source.</summary>
    private static HashSet<string> ExtractSqlTableNames(string source)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "the", "and", "or", "not", "in", "on", "at", "to", "for", "of", "by",
            "as", "is", "it", "from", "join", "inner", "outer", "left", "right",
            "where", "set", "values", "select", "insert", "update", "delete",
            "order", "group", "having", "limit", "offset", "true", "false", "null",
            "count", "sum", "avg", "min", "max", "distinct",
            "date", "time", "year", "month", "day", "hour", "minute", "second",
            "now", "between", "like", "exists", "case", "when", "then", "else", "end",
            "return", "returns", "declare", "begin", "if", "else",
            "start", "stop", "commit", "rollback", "savepoint",
            "int", "integer", "bigint", "smallint", "tinyint",
            "decimal", "numeric", "float", "double", "real",
            "char", "varchar", "text", "blob", "binary",
            "enum", "set", "json", "boolean", "bit",
            "default", "unique", "index", "key", "primary", "foreign",
            "cascade", "restrict", "action", "check",
            "auto_increment", "unsigned", "signed", "zerofill",
            "character", "collate", "charset", "engine", "row_format"
        };
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match sm in Regex.Matches(source,
            @"@?""(?:[^""\\]*(?:\\.[^""\\]*)*)""", RegexOptions.Singleline))
        {
            var val = sm.Value;
            if (!Regex.IsMatch(val, @"\b(SELECT|INSERT|UPDATE|DELETE)\b", RegexOptions.IgnoreCase))
                continue;
            foreach (Match m in Regex.Matches(val,
                @"(?:FROM|JOIN|INTO|UPDATE|TABLE(?:\s+IF\s+NOT\s+EXISTS)?)\s+`?(\w+(?:\.\w+)?)`?",
                RegexOptions.IgnoreCase))
            {
                var tbl = m.Groups[1].Value;
                if (tbl.Contains('.')) tbl = tbl.Split('.')[^1];
                if (tbl.Length > 2 && !skip.Contains(tbl) && !char.IsDigit(tbl[0]))
                    tables.Add(tbl);
            }
        }
        return tables;
    }

    private static string AutoFixSqlWhitespace(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;
        var lines = content.Split('\n');
        var changed = false;
        for (var li = 0; li < lines.Length; li++)
        {
            var trimmed = lines[li].Trim();
            if (trimmed.Length < 10) continue;
            // Skip URL lines — they can have =digits in query params that should not be touched
            if (trimmed.Contains("://")) continue;
            // Only fix lines that contain SQL keywords
            if (!Regex.IsMatch(trimmed, @"\b(SELECT|FROM|WHERE|SET|INSERT|UPDATE|DELETE|JOIN|ORDER|GROUP|HAVING|INTERVAL|DATE_ADD|DATE_SUB|NOW)\b", RegexOptions.IgnoreCase))
                continue;

            var line = lines[li];
            var ws = Regex.Match(line, @"^(\s*)").Groups[1].Value;
            var body = line.Substring(ws.Length);

            body = Regex.Replace(body, @"\bINTERVAL(\d)", "INTERVAL $1", RegexOptions.IgnoreCase);
            body = Regex.Replace(body, @"\bINTERVAL(\w)", "INTERVAL $1", RegexOptions.IgnoreCase);
            body = Regex.Replace(body, @"(?<![=!<>])=(?=\d)", "= ");

            var newLine = ws + body;
            if (newLine != lines[li])
            {
                lines[li] = newLine;
                changed = true;
            }
        }
        return changed ? string.Join("\n", lines) : content;
    }

    /// <summary>Extract a C# method signature (attributes + return type + declaration) for comparison.</summary>
    private static string? ExtractMethodSignature(string code)
    {
        try
        {
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetRoot();
            var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (method == null) return null;
            var attrTexts = method.AttributeLists.Select(a => a.ToFullString().Trim());
            var returnType = method.ReturnType.ToFullString().Trim();
            var declText = returnType + " " + method.Identifier.Text +
                           method.TypeParameterList?.ToFullString() + "(" +
                           string.Join(", ", method.ParameterList.Parameters.Select(p =>
                               p.AttributeLists.Any()
                                   ? string.Join(" ", p.AttributeLists.Select(a => a.ToFullString().Trim())) + " " + p.ToFullString().Trim()
                                   : p.ToFullString().Trim())) +
                           ")";
            return string.Join(" ", attrTexts) + " " + declText;
        }
        catch { return null; }
    }

    /// <summary>Extract all method signatures from a C# file content for cross-edit comparison.</summary>
    private static List<string> ExtractAllMethodSignatures(string code)
    {
        try
        {
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetRoot();
            return root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Select(m =>
                {
                    var attrTexts = m.AttributeLists.Select(a => a.ToFullString().Trim());
                    var returnType = m.ReturnType.ToFullString().Trim();
                    var declText = returnType + " " + m.Identifier.Text +
                                   m.TypeParameterList?.ToFullString() + "(" +
                                   string.Join(", ", m.ParameterList.Parameters.Select(p =>
                                       p.AttributeLists.Any()
                                           ? string.Join(" ", p.AttributeLists.Select(a => a.ToFullString().Trim())) + " " + p.ToFullString().Trim()
                                           : p.ToFullString().Trim())) +
                                   ")";
                    return string.Join(" ", attrTexts) + " " + declText;
                })
                .ToList();
        }
        catch { return new List<string>(); }
    }

    private static string TruncateForLog(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxLen) return s;
        return s[..(maxLen - 3)] + "...";
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
        baseInstructions.AppendLine("BEFORE planning the first step, briefly assess the full task end-to-end. What data do you need? What files will be created? What merge/transform steps are needed? Then plan only the next 1-2 steps. Re-assess after each step.");
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

        // Execute: one step at a time, plan as you go
        var stepIndex = 0; string? summary = null;
        var usedSearchQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var planSteps = new List<PlanStep>();
        var completedPlanSteps = new HashSet<int>();
        var totalPlanSteps = 0;
        var consecutiveErrors = 0;

        var conversation = new StringBuilder();
        conversation.Append(baseInstructions);
        conversation.AppendLine("\nPlan 1-2 steps at a time. Do NOT repeat steps already in the plan. Output:");
        conversation.AppendLine("  {\"plan\": [{\"file\": \"<output path>\", \"change\": \"what to do\"}]}  # add 1-2 new steps");
        conversation.AppendLine("  {\"cmd\": \"...\"} / {\"web_fetch\": \"...\"} / {\"web_search\": \"...\"}  # execute directly");
        conversation.AppendLine("  {\"step\": N}  # explicitly mark step N done (if current approach failed but you want a different one)");
        conversation.AppendLine("  {\"done\": true, \"summary\": \"...\"}  # finish");
        conversation.AppendLine("After each action, verify if the step\'s objective was met. If a step errors, you can mark it done and try a different approach.");
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

            // Plan step definition
            if (root.TryGetProperty("plan", out var pArr) && pArr.ValueKind == JsonValueKind.Array && pArr.GetArrayLength() > 0)
            {
                var newSteps = new List<PlanStep>();
                foreach (var item in pArr.EnumerateArray())
                    newSteps.Add(new PlanStep
                    {
                        File = item.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "",
                        Change = item.TryGetProperty("change", out var c) ? c.GetString() ?? "" : ""
                    });
                // Deduplicate against already-planned steps so the LLM can't
                // add the same step multiple times across iterations
                var deduped = newSteps.Where(ns =>
                    !planSteps.Any(ps =>
                        string.Equals(ps.File, ns.File, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals((ps.Change ?? "").Trim(), (ns.Change ?? "").Trim(), StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (deduped.Count == 0)
                {
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
                        items = planSteps.Select(p => new { file = p.File, change = p.Change, priority = 1 }).ToList()
                    }, ct);
                conversation.AppendLine($"\n### PLAN UPDATED ({totalPlanSteps} total steps) ###");
                for (var pi = 0; pi < planSteps.Count; pi++)
                    conversation.AppendLine($"  Step {pi + 1}: [{planSteps[pi].File}] {planSteps[pi].Change}");
                conversation.AppendLine("### END PLAN ###");
                continue;
            }

            // Mark step done
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

            // Done signal
            if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
            {
                summary = root.TryGetProperty("summary", out var s) ? s.GetString() : "Task complete";
                break;
            }

            // Execute command
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

            // Web search
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

            // Web fetch
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

            // Message / result
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
                cardId: req.CardId,
                createTests: req.CreateTests);

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
            Console.WriteLine($"[AGENT CRASH] {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            await SendSse(Response, "error", new { message = $"{ex.GetType().Name}: {ex.Message}" });
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
        var step = new AgentStep { Index = 0, Type = "command", Command = tcpCmd, Description = "TCP Check" };
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
        var cfg6 = await LoadConfigAsync();
        var mt = maxTokens ?? cfg6.defaultMaxTokens;
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
        var cfg7 = await LoadConfigAsync();
        var mt = maxTokens ?? cfg7.defaultMaxTokens;
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

            while (true)
            {
                var line = await reader.ReadLineAsync();
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


    /// <summary>
    /// After a successful edit, scans the newly added code for method calls, property
    /// accesses, and class references. Greps the project to check if each symbol is
    /// defined elsewhere. If anything is genuinely missing, asks the LLM to generate
    /// the minimum follow-up steps needed to implement it.
    /// </summary>
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

        // Step 1: Extract symbols referenced in the new code
        var candidates = ExtractReferencedSymbolsFromCode(newStr, ext);
        if (candidates.Count == 0) return new List<PlanStep>();

        // Step 2: Filter out symbols already defined in the current file or in plan steps
        var toCheck = candidates
            .Where(sym => !fullFileContent.Contains(sym, StringComparison.Ordinal))
            .Where(sym => !existingPlanSteps.Any(s =>
                s.Change?.Contains(sym, StringComparison.OrdinalIgnoreCase) == true))
            .Distinct()
            .Take(12)
            .ToList();

        if (toCheck.Count == 0) return new List<PlanStep>();

        // Step 3: Grep project for each symbol definition
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

        // Step 4: Ask the LLM to generate steps for genuinely missing symbols
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

                // Deduplicate against existing steps
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

    /// <summary>
    /// Extracts method calls, property accesses, and class references from newly added
    /// code. Results are filtered to exclude well-known built-ins and framework symbols.
    /// </summary>
    private static List<string> ExtractReferencedSymbolsFromCode(string code, string ext)
    {
        var symbols = new HashSet<string>(StringComparer.Ordinal);

        if (ext is ".html" or ".htm")
        {
            // Angular event bindings: (click)="methodName()"
            foreach (Match m in Regex.Matches(code,
                @"\(\w+\)=""([A-Za-z_]\w*)\s*\("))
                symbols.Add(m.Groups[1].Value);

            // Angular structural directives referencing component properties
            foreach (Match m in Regex.Matches(code,
                @"\*ngFor=""let \w+ of ([A-Za-z_]\w*)"))
                symbols.Add(m.Groups[1].Value);

            // Property bindings: [disabled]="expression" — grab first identifier
            foreach (Match m in Regex.Matches(code,
                @"\[[\w-]+\]=""([A-Za-z_]\w*)"))
                symbols.Add(m.Groups[1].Value);

            // Two-way binding: [(ngModel)]="prop"
            foreach (Match m in Regex.Matches(code,
                @"\[\(ngModel\)\]=""([A-Za-z_]\w*)"))
                symbols.Add(m.Groups[1].Value);

            // Interpolation: {{ symbol }}
            foreach (Match m in Regex.Matches(code,
                @"\{\{\s*([A-Za-z_]\w*)\s*(?:\||\}\})"))
                symbols.Add(m.Groups[1].Value);
        }
        else if (ext is ".ts" or ".js" or ".tsx" or ".jsx")
        {
            // this.method() and this.property
            foreach (Match m in Regex.Matches(code, @"this\.([A-Za-z_]\w*)\b"))
                symbols.Add(m.Groups[1].Value);
        }
        else if (ext == ".cs")
        {
            // Method calls (non-this for public API calls within same class)
            foreach (Match m in Regex.Matches(code, @"\bthis\.([A-Za-z_]\w*)\s*[(\[]"))
                symbols.Add(m.Groups[1].Value);
        }

        // Strip well-known built-ins so we don't generate steps for them
        var builtins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Lifecycle
        "ngOnInit","ngOnDestroy","ngAfterViewInit","ngOnChanges","ngDoCheck",
        "ngAfterContentInit","ngAfterContentChecked","ngAfterViewChecked","constructor",
        // JS/TS primitives
        "length","name","value","type","id","url","href","src","target","key","index",
        "push","pop","shift","unshift","splice","slice","map","filter","reduce","find",
        "some","every","includes","indexOf","join","split","trim","toLowerCase","toUpperCase",
        "toString","parseInt","parseFloat","JSON","Math","Object","Array","String","Number",
        "Boolean","Promise","console","log","error","warn","Date","Error","typeof","instanceof",
        // RxJS
        "subscribe","next","error","complete","pipe","tap","catchError","takeUntil",
        // Angular common
        "ngModel","ngClass","ngStyle","ngIf","ngFor","ngSwitch","trackBy","async",
        "markForCheck","detectChanges","emit","getValue","patchValue","reset","get","set",
        // C# common
        "ToString","GetType","Equals","GetHashCode","Dispose","Task","List","Dictionary",
        "Console","String","Int32","Boolean","DateTime","Guid","Path","File","Directory",
    };

        return symbols
            .Where(s => s.Length >= 3 && !builtins.Contains(s) && !char.IsUpper(s[0]))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Greps the project for a definition of <paramref name="symbol"/> (method, property,
    /// or class declaration). Returns (relPath, snippet) if found, or (null, null) if missing.
    /// Skips build artifacts, the file that was just edited, and files > 300 KB.
    /// </summary>
    private async Task<(string? foundInPath, string? snippet)> GrepProjectForDefinitionAsync(
        string projectRoot, string symbol, string excludeRelPath, CancellationToken ct)
    {
        var defPatterns = new[]
        {
        // TS/JS method or property definition
        new Regex($@"^\s*(?:(?:public|private|protected|readonly|static|async|override|get|set)\s+)*{Regex.Escape(symbol)}\s*[=(:(<]", RegexOptions.Multiline),
        // C# member
        new Regex($@"\b(?:public|private|protected|internal)\b[^{{}}]*\b{Regex.Escape(symbol)}\s*[({{;]", RegexOptions.Multiline),
        // Angular @Input / @Output property
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
        catch { /* non-critical */ }

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

    private static string CollapseWhitespace(string s)
    {
        // Don't collapse whitespace inside quoted strings (SQL, etc.)
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

    private static string UnescapeString(string s)
    {
        // Handle escaped newlines: "line1\\nline2" → "line1\nline2"
        if (string.IsNullOrEmpty(s)) return s ?? "";
        return s.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
    }

    private static bool IsSqlLike(string content)
    {
        // Detect if content contains SQL patterns to skip aggressive whitespace normalization
        var lower = content.ToLower();
        return lower.Contains("select ") || lower.Contains("insert ") || lower.Contains("update ") ||
               lower.Contains("delete ") || lower.Contains("from ") || lower.Contains("where ") ||
               lower.Contains("interval ") || lower.Contains("date_add") || lower.Contains("where ");
    }

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
            // Direct splice: LLM already provided correct indentation; don't re-indent.
            return (true, content[..idx] + newString + content[(idx + oldString.Length)..], null, null);
        }

        // ── S2: Trailing-whitespace-trimmed exact match ────────────────────
        var trimmedOld = oldLines.Select(l => l.TrimEnd()).ToArray();
        var trimmedFile = fileLines.Select(l => l.TrimEnd()).ToArray();
        var matchLine = FindLineBlock(trimmedFile, trimmedOld, StringComparison.Ordinal);
        if (matchLine >= 0)
            return (true, ReplaceLineBlock(fileLines, matchLine, oldLines.Length, newString), null, null);

        // ── S3: Whitespace-collapsed match (SKIPPED for SQL to preserve spacing) ───────
        // Skip aggressive whitespace collapsing for SQL content where spacing is critical
        var isSql = IsSqlLike(oldString) || IsSqlLike(content);
        if (!isSql)
        {
            var wsOld = oldLines.Select(l => CollapseWhitespace(l)).ToArray();
            var wsFile = fileLines.Select(l => CollapseWhitespace(l)).ToArray();
            matchLine = FindLineBlock(wsFile, wsOld, StringComparison.Ordinal);
            if (matchLine >= 0)
                return (true, ReplaceLineBlock(fileLines, matchLine, oldLines.Length, newString), null, null);

            // ── S4: Whitespace-collapsed case-insensitive match ────────────────
            matchLine = FindLineBlock(wsFile, wsOld, StringComparison.OrdinalIgnoreCase);
            if (matchLine >= 0)
                return (true, ReplaceLineBlock(fileLines, matchLine, oldLines.Length, newString), null, null);
        }

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

        if (IsHtmlLikeContent(replacement))
            return AutoIndentHtml(string.Join("\n", replLines), fileIndent);

        var joined = string.Join("\n", replLines);
        // Only apply brace-depth re-indentation when the replacement is flat
        // (all non-empty lines at the same indent level = LLM collapsed structure).
        // If it already has multiple indent levels, the LLM structured it correctly — keep it.
        var distinctIndentDepths = replLines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => GetLeadingWhitespace(l).Length)
            .Distinct()
            .Count();
        return distinctIndentDepths <= 1 && replLines.Length > 2
            ? AutoIndentFromFile(joined, fileIndent, fileLines, start)
            : joined;
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
        // Brace-depth re-indentation is only meaningful when the code actually uses braces.
        // For whitespace-significant languages (Python, YAML, …) there are no braces, so this
        // would collapse every line to the base indent. IndentReplacement has already rebased
        // the relative indentation, so keep it as-is.
        if (!replacement.Contains('{') && !replacement.Contains('}'))
            return replacement;

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

    /// <summary>Normalize spacing after colons in .ts/.js object literals.
    /// Ensures property:value pairs inside {...} have a space after the colon,
    /// matching the codebase convention. Avoids modifying already-correct
    /// spacing or content inside string literals.</summary>
    private static string NormalizeTypeScriptObjectLiterals(string content)
    {
        // Match propertyName:value after { or , — add space after colon if missing
        return Regex.Replace(content, @"(?<=[\{,]\s*)(\w[\w']*)\s*:\s*(?=\S)", "$1: ");
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
                            return true; // treat as pass
                        }
                        // Send the user's response as terminal input
                        await _terminal.WriteStdinAsync(userResponse);
                        await Task.Delay(1000);
                        // Continue the loop to re-check build
                        continue;
                    }
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

        var existingTestFiles = FindExistingTestFiles(projectRoot);
        var hasExistingTests = existingTestFiles.Count > 0;
        var testFramework = await DetectTestFramework(projectRoot, ct);

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
            if (existingTestFiles.Any(f => FileContains(f, "xunit", "Fact"))) testFramework = "xunit";
            else if (existingTestFiles.Any(f => FileContains(f, "nunit", "TestFixture"))) testFramework = "nunit";
            else if (existingTestFiles.Any(f => FileContains(f, "mstest", "TestClass", "TestMethod"))) testFramework = "mstest";
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

        var testDir = FindOrDetermineTestDir(projectRoot, existingTestFiles);

        foreach (var filePath in editedFiles)
        {
            var fullPath = Path.Combine(projectRoot, filePath);
            if (!System.IO.File.Exists(fullPath))
            {
                await EmitLog(emitSse, "warn", $"TestCreation: file not found: {filePath}", ct: ct);
                continue;
            }

            var fileContent = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
            var testFilePath = GetTestFilePath(projectRoot, filePath, testDir);

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

    private static List<string> FindExistingTestFiles(string projectRoot)
    {
        var patterns = new[] { "*Test*.cs", "*Tests.cs", "*.Specs.cs", "*.specs.cs" };
        var dirs = new[] { "test", "tests", "Test", "Tests" };
        var result = new List<string>();

        foreach (var p in patterns)
        {
            try
            {
                result.AddRange(Directory.EnumerateFiles(projectRoot, p, SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\") && !f.Contains("\\node_modules\\") && !f.Contains("\\.git\\")));
            }
            catch { }
        }

        foreach (var d in dirs)
        {
            var dp = Path.Combine(projectRoot, d);
            if (Directory.Exists(dp))
            {
                try { result.AddRange(Directory.EnumerateFiles(dp, "*.cs", SearchOption.AllDirectories)); }
                catch { }
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool FileContains(string filePath, params string[] keywords)
    {
        try
        {
            using var sr = new StreamReader(filePath, Encoding.UTF8);
            var header = sr.ReadToEnd();
            return keywords.Any(k => header.Contains(k, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static string FindOrDetermineTestDir(string projectRoot, List<string> existingTestFiles)
    {
        if (existingTestFiles.Count > 0)
        {
            var dir = Path.GetDirectoryName(existingTestFiles[0]);
            if (dir != null) return dir;
        }
        return Path.Combine(projectRoot, "tests");
    }

    private static string GetTestFilePath(string projectRoot, string sourceFilePath, string testDir)
    {
        var fileName = Path.GetFileNameWithoutExtension(sourceFilePath);
        var ext = Path.GetExtension(sourceFilePath);
        return Path.Combine(testDir, $"{fileName}Tests{ext}");
    }

    private async Task<string?> DetectTestFramework(string projectRoot, CancellationToken ct)
    {
        try
        {
            foreach (var csproj in Directory.EnumerateFiles(projectRoot, "*.csproj", SearchOption.AllDirectories))
            {
                var content = await System.IO.File.ReadAllTextAsync(csproj, Encoding.UTF8, ct);
                if (content.Contains("xunit", StringComparison.OrdinalIgnoreCase)) return "xunit";
                if (content.Contains("nunit", StringComparison.OrdinalIgnoreCase)) return "nunit";
                if (content.Contains("MSTest", StringComparison.OrdinalIgnoreCase)) return "mstest";
            }
        }
        catch { }
        return null;
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
}