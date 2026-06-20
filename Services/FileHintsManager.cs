using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Weaver.Services;

/// <summary>
/// Per-project file-hints manager. Records associations between prompts,
/// symbols (class/method names), and the files where those symbols live.
/// Surfaces those associations during bootstrap discovery so the agent
/// auto-reads the right files without needing an LLM-based file-selection pass.
///
/// ─────────────────────────────────────────────────────────────────────────
///  TWO LEARNING PATHS
/// ─────────────────────────────────────────────────────────────────────────
///  1. PROMPT → FILE  (legacy, score-based promotion)
///     <see cref="LearnFromGrepOutput"/> parses grep output for file paths,
///     then <see cref="RecordAssociation"/> increments a per-keyword score.
///     Once a (keyword, file) pair hits Score >= 3, it is promoted to a
///     <see cref="KeywordHint"/> and surfaced via <see cref="GetFilesForPrompt"/>.
///
///  2. SYMBOL → FILE  (new, immediate, high-value)
///     <see cref="LearnFromAppliedEdit"/> parses the just-applied edit text
///     for new public class and public method declarations, filters
///     aggressively for pertinence, and records them immediately as
///     <see cref="SymbolHint"/>s. These are surfaced via
///     <see cref="GetFilesForPrompt"/> with HIGHER priority than keyword
///     hints because:
///       - The agent just WROTE the symbol there → it's the canonical home.
///       - Symbol names are usually unique enough to identify a file
///         ("DrawMesh" → grandtheft-renderer.ts).
///       - Prompts frequently mention the symbol they want changed.
///
/// ─────────────────────────────────────────────────────────────────────────
///  PERTINENCE GUARDRAILS (the whole point of the symbol path — keep signal high)
/// ─────────────────────────────────────────────────────────────────────────
///  LearnFromAppliedEdit skips:
///    • Non-source extensions (only .cs/.ts/.tsx/.js/.jsx/.py are extracted;
///      .go/.rs/.java/.kt/.swift/.rb/.php are recognized but not yet parsed)
///    • Test files (*.test.ts, *Tests.cs, _test.go, etc.)
///    • Generated files (.g.cs, .designer.cs, .pb.go, .generated.*, etc.)
///    • Build artifacts (/bin/, /obj/, /node_modules/, /dist/, /build/,
///      /target/, /vendor/, /.git/)
///    • Minified files (.min.js, .min.css)
///    • Private and protected members (implementation details)
///    • C# `override` methods (canonical home is the base class)
///    • Constructors (redundant with the class hint)
///    • Generic method names (Run, Do, Init, Execute, Add, Get, Set, Render,
///      Draw, Find, Search, Validate, … — 90+ in <see cref="GenericMethodNames"/>)
///      — too ambiguous to identify a unique file
///    • Method names shorter than 4 characters
///    • Duplicates where the symbol already canonically lives in a DIFFERENT
///      file (first-write-wins — prevents test files or partial classes in
///      non-canonical locations from hijacking the canonical-home mapping)
///
///  Symbol table is capped at 5000 entries per project (LRU eviction by
///  LastSeen). Keyword-hint promotion threshold is 3 (legacy behavior).
/// </summary>
public class FileHintsManager
{
    private readonly string _basePath;
    private readonly object _lock = new();

    public FileHintsManager(string basePath)
    {
        _basePath = basePath;
    }

    private string HintsFilePath => Path.Combine(_basePath, "data", "filehints.json");

    // ════════════════════════════════════════════════════════════════════════
    //  PERSISTENCE  (unchanged from original)
    // ════════════════════════════════════════════════════════════════════════

    private GlobalHintsStore LoadAll()
    {
        try
        {
            if (!System.IO.File.Exists(HintsFilePath))
            {
                var dir = Path.GetDirectoryName(HintsFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                var defaultContent = "{\"Projects\": {}}";
                System.IO.File.WriteAllText(HintsFilePath, defaultContent);
                return new GlobalHintsStore();
            }
            var json = System.IO.File.ReadAllText(HintsFilePath);
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                // Tolerant deserialization: if AutoLearned or SymbolHints are
                // missing on an old store written by pre-2.0 code, default them
                // to empty lists instead of crashing.
            };
            var store = JsonSerializer.Deserialize<GlobalHintsStore>(json, opts) ?? new GlobalHintsStore();
            // Backfill null collections — older stores may not have these fields.
            foreach (var proj in store.Projects.Values)
            {
                proj.AutoLearned ??= new List<LearnedAssociation>();
                proj.Hints ??= new List<KeywordHint>();
                proj.SymbolHints ??= new List<SymbolHint>();
            }
            return store;
        }
        catch { }
        return new GlobalHintsStore();
    }

    private void SaveAll(GlobalHintsStore store)
    {
        var json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
        System.IO.File.WriteAllText(HintsFilePath, json);
    }

    private ProjectHints EnsureProject(string projectRoot, GlobalHintsStore store)
    {
        if (!store.Projects.TryGetValue(projectRoot, out var proj))
        {
            proj = new ProjectHints
            {
                Hints = new List<KeywordHint>(),
                AutoLearned = new List<LearnedAssociation>(),
                SymbolHints = new List<SymbolHint>()
            };
            store.Projects[projectRoot] = proj;
        }
        // Backfill in case the project was created by old code and only now
        // being touched by new code.
        proj.Hints ??= new List<KeywordHint>();
        proj.AutoLearned ??= new List<LearnedAssociation>();
        proj.SymbolHints ??= new List<SymbolHint>();
        return proj;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  READ PATH
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns up to 8 file paths the hints system believes are relevant to
    /// the given prompt. Combines:
    ///   1. SYMBOL HINTS (highest priority): prompt mentions a class or method
    ///      name that has a canonical home recorded via LearnFromAppliedEdit.
    ///      Method hits score higher than class hits because they're more
    ///      specific — "DrawMesh" usually identifies one file, while
    ///      "AgentController" might appear in several.
    ///   2. KEYWORD HINTS (legacy, score-based): prompt contains a keyword
    ///      that has been promoted via the score-based LearnFromGrepOutput
    ///      path. Lower priority than symbol hits.
    /// </summary>
    public List<string> GetFilesForPrompt(string prompt, string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return new List<string>();

        var lower = prompt.ToLowerInvariant();
        var scored = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            var store = LoadAll();
            var proj = EnsureProject(projectRoot, store);

            // ── 1. Symbol hints (highest priority) ──
            // Word-boundary, case-SENSITIVE matching against the ORIGINAL prompt.
            // Case-sensitive because PascalCase / snake_case names are stable
            // across casings, and case-insensitive matching causes false
            // positives like matching "Add" inside "address".
            foreach (var sym in proj.SymbolHints)
            {
                if (PromptMentionsSymbol(prompt, sym.SymbolName))
                {
                    // Method hits score 100; class hits score 50.
                    var score = sym.Kind == SymbolKind.Method ? 100 : 50;
                    if (!scored.TryGetValue(sym.File, out var prev) || score > prev)
                        scored[sym.File] = score;
                }
            }

            // ── 2. Keyword hints (legacy path) ──
            // Lowercase substring match — preserved exactly from original
            // implementation for backward compatibility.
            foreach (var hint in proj.Hints)
            {
                if (hint.Keywords.Any(k => lower.Contains(k, StringComparison.Ordinal)))
                {
                    foreach (var f in hint.Files)
                    {
                        // Keyword hints score 25 — below symbol hits so they
                        // only fill remaining slots after symbol-driven files.
                        if (!scored.TryGetValue(f, out var prev) || 25 > prev)
                            scored[f] = 25;
                    }
                }
            }
        }

        return scored
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .Take(8)
            .ToList();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  LEARN PATH 1 — KEYWORD/GREP  (unchanged from original)
    // ════════════════════════════════════════════════════════════════════════

    public void RecordAssociation(string keyword, string file, string projectRoot)
    {
        lock (_lock)
        {
            var store = LoadAll();
            var proj = EnsureProject(projectRoot, store);

            var normalizedKeyword = keyword.ToLowerInvariant();
            var normalizedFile = file.Replace('\\', '/').TrimStart('/');

            var existing = proj.AutoLearned.FirstOrDefault(a =>
                string.Equals(a.Keyword, normalizedKeyword, StringComparison.Ordinal) &&
                string.Equals(a.File, normalizedFile, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.Score++;
                existing.LastSeen = DateTime.UtcNow.ToString("o");
            }
            else
            {
                proj.AutoLearned.Add(new LearnedAssociation
                {
                    Keyword = normalizedKeyword,
                    File = normalizedFile,
                    Score = 1,
                    LastSeen = DateTime.UtcNow.ToString("o")
                });
            }

            // Promote to hint when score >= 3
            var readyGroups = proj.AutoLearned
                .Where(a => a.Score >= 3)
                .GroupBy(a => a.Keyword)
                .ToList();

            foreach (var group in readyGroups)
            {
                var kw = group.Key;
                var files = group.Select(a => a.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                var existingHint = proj.Hints.FirstOrDefault(h =>
                    h.Keywords.Contains(kw, StringComparer.OrdinalIgnoreCase));

                if (existingHint != null)
                {
                    foreach (var f in files)
                        if (!existingHint.Files.Contains(f, StringComparer.OrdinalIgnoreCase))
                            existingHint.Files.Add(f);
                }
                else
                {
                    proj.Hints.Add(new KeywordHint
                    {
                        Keywords = new() { kw },
                        Files = files
                    });
                }

                proj.AutoLearned.RemoveAll(a =>
                    string.Equals(a.Keyword, kw, StringComparison.Ordinal) &&
                    files.Any(f => string.Equals(f, a.File, StringComparison.OrdinalIgnoreCase)));
            }

            SaveAll(store);
        }
    }

    public void LearnFromGrepOutput(string keyword, string grepOutput, string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(grepOutput) || grepOutput == "(no matches)") return;

        var lines = grepOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var colonIdx = line.IndexOf(':');
            if (colonIdx > 0)
            {
                var path = line.Substring(0, colonIdx).Trim();
                if (!string.IsNullOrEmpty(path) && !path.Contains(' ') && path.Contains('/'))
                    filePaths.Add(path);
            }
        }

        foreach (var file in filePaths)
            RecordAssociation(keyword, file, projectRoot);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  LEARN PATH 2 — SYMBOL/APPLIED-EDIT  (NEW)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// NEW: Learn canonical (class/method → file) hints from a successfully-
    /// applied edit. Parses <paramref name="appliedNewStr"/> for new public
    /// class and public method declarations, filters aggressively for
    /// pertinence, and records them immediately as <see cref="SymbolHint"/>s.
    ///
    /// Called from AgentController.ExecuteEditStep after each successful edit
    /// write (both the file-create path and the normal replace path).
    /// Signature: (projectRoot, filePath, appliedNewStr) — matches the call
    /// site at AgentController.cs.
    ///
    /// SAFE BY CONSTRUCTION:
    ///   • Null/empty inputs → no-op.
    ///   • Non-source files → no-op.
    ///   • Noise files (test, generated, build artifacts) → no-op.
    ///   • If extraction yields zero pertinent symbols → no-op (no disk write).
    ///   • All writes serialized through `_lock`; concurrent edit steps are safe.
    ///   • Any exception is swallowed by the caller (AgentController wraps the
    ///     call in try/catch) — hint-learning never crashes the edit pipeline.
    /// </summary>
    public void LearnFromAppliedEdit(string projectRoot, string filePath, string appliedNewStr)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)
            || string.IsNullOrWhiteSpace(filePath)
            || string.IsNullOrWhiteSpace(appliedNewStr))
            return;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (!IsSourceFile(ext)) return;
        if (IsNoiseFile(filePath)) return;

        var symbols = ExtractSymbols(appliedNewStr, ext);
        if (symbols.Count == 0) return;

        var normalizedFile = NormalizeRelativePath(filePath, projectRoot);
        var nowUtc = DateTime.UtcNow.ToString("o");
        var changed = false;

        lock (_lock)
        {
            var store = LoadAll();
            var proj = EnsureProject(projectRoot, store);

            foreach (var s in symbols)
            {
                // First-write-wins: if this symbol already canonically lives
                // in a DIFFERENT file, skip — don't let test files / partial
                // classes hijack the canonical-home mapping.
                var existing = proj.SymbolHints.FirstOrDefault(x =>
                    x.SymbolName == s.SymbolName
                    && x.Kind == s.Kind
                    && string.Equals(x.ContainingClass ?? "", s.ContainingClass ?? "",
                                     StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    if (!string.Equals(existing.File, normalizedFile, StringComparison.OrdinalIgnoreCase))
                        continue; // already canonically lives elsewhere — skip
                    existing.LastSeen = nowUtc;
                    continue;
                }

                proj.SymbolHints.Add(new SymbolHint
                {
                    SymbolName = s.SymbolName,
                    Kind = s.Kind,
                    ContainingClass = s.ContainingClass,
                    File = normalizedFile,
                    FirstSeen = nowUtc,
                    LastSeen = nowUtc
                });
                changed = true;
            }

            // Cap symbol table at 5000 entries per project (LRU eviction by LastSeen).
            if (proj.SymbolHints.Count > 5000)
            {
                proj.SymbolHints = proj.SymbolHints
                    .OrderByDescending(s => s.LastSeen)
                    .Take(5000)
                    .ToList();
                changed = true;
            }

            if (changed) SaveAll(store);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  PERTINENCE FILTERS
    // ════════════════════════════════════════════════════════════════════════

    private static bool IsSourceFile(string ext) =>
        ext is ".cs" or ".ts" or ".tsx" or ".js" or ".jsx"
            or ".py" or ".go" or ".rs" or ".java" or ".kt"
            or ".swift" or ".rb" or ".php";

    /// <summary>
    /// Returns true if the file path points at a test file, generated file,
    /// build artifact, or other noise that should never contribute hints.
    /// </summary>
    private static bool IsNoiseFile(string filePath)
    {
        var name = Path.GetFileName(filePath);
        var nameLow = name.ToLowerInvariant();
        // Normalize to forward slashes and prepend a leading slash so we can
        // check path segments uniformly for both absolute and relative paths.
        var norm = "/" + filePath.Replace('\\', '/').Trim('/').ToLowerInvariant();

        // Test files
        if (nameLow.EndsWith(".test.ts") || nameLow.EndsWith(".test.tsx")
            || nameLow.EndsWith(".test.js") || nameLow.EndsWith(".test.jsx")
            || nameLow.EndsWith(".spec.ts") || nameLow.EndsWith(".spec.tsx")
            || nameLow.EndsWith(".spec.js") || nameLow.EndsWith(".spec.jsx")
            || nameLow.EndsWith("tests.cs") || nameLow.EndsWith("test.cs")
            || nameLow.EndsWith(".test.py") || nameLow.EndsWith("_test.go")
            || nameLow.EndsWith("_test.rs") || nameLow.EndsWith("test.java")
            || nameLow.EndsWith("tests.java"))
            return true;

        // Generated files
        if (nameLow.EndsWith(".generated.cs") || nameLow.EndsWith(".designer.cs")
            || nameLow.EndsWith(".g.cs") || nameLow.EndsWith(".g.ts")
            || nameLow.EndsWith(".gen.ts") || nameLow.EndsWith(".gen.go")
            || nameLow.EndsWith(".pb.go") || nameLow.EndsWith(".generated.js"))
            return true;

        // Build artifacts / common noise — checked as path segments so we
        // don't false-positive on a folder literally named "bin" in src.
        if (norm.Contains("/bin/") || norm.Contains("/obj/")
            || norm.Contains("/node_modules/") || norm.Contains("/.git/")
            || norm.Contains("/dist/") || norm.Contains("/build/")
            || norm.Contains("/target/") || norm.Contains("/vendor/"))
            return true;

        // Minified
        if (nameLow.EndsWith(".min.js") || nameLow.EndsWith(".min.css"))
            return true;

        return false;
    }

    /// <summary>
    /// Stop-list of method names too common across codebases to be useful as
    /// hints. If the user says "fix the Run method", we don't know WHICH Run
    /// — there are probably 20 in any nontrivial codebase. Skipping these
    /// keeps the symbol table signal-high.
    /// </summary>
    private static readonly HashSet<string> GenericMethodNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Generic verbs
        "run", "do", "init", "execute", "add", "get", "set", "remove",
        "start", "stop", "begin", "end", "open", "close", "read", "write",
        "load", "save", "update", "delete", "create", "build", "make",
        "process", "handle", "apply", "call", "invoke", "send", "receive",
        "show", "hide", "draw", "render", "print", "log", "test",
        "main", "compute", "calculate", "fetch", "push", "pop", "peek",
        "find", "search", "filter", "map", "reduce", "foreach", "iterate",
        "validate", "check", "verify", "parse", "format", "convert",
        "transform", "normalize", "resolve", "reset", "clear", "dispose",
        "configure", "setup", "teardown", "connect", "disconnect", "subscribe",
        "unsubscribe", "emit", "dispatch", "trigger", "fire", "watch",
        "listen", "notify", "alert", "warn", "error", "fail", "succeed",
        "complete", "finish", "abort", "cancel", "pause", "resume", "wait",
        "sleep", "yield", "return", "exit", "quit", "lock", "unlock", "try",
        "use", "with", "from", "into", "to", "as", "is", "has", "can",
        "should", "would", "will", "may", "might", "must", "shall",
        // C# conventionals
        "tostring", "gethashcode", "equals", "compareto",
        // TS/JS conventionals
        "tojson", "valueof",
    };

    private static bool IsGenericMethodName(string name) =>
        GenericMethodNames.Contains(name);

    // ════════════════════════════════════════════════════════════════════════
    //  SYMBOL EXTRACTION (per language)
    // ════════════════════════════════════════════════════════════════════════

    private static List<ExtractedSymbol> ExtractSymbols(string code, string ext)
    {
        return ext switch
        {
            ".cs" => ExtractCsSymbols(code),
            ".ts" or ".tsx" or ".js" or ".jsx" => ExtractTsSymbols(code),
            ".py" => ExtractPySymbols(code),
            _ => new List<ExtractedSymbol>() // .go/.rs/.java/.kt/.swift/.rb/.php — future work
        };
    }

    // ── C# ───────────────────────────────────────────────────────────────────

    private static readonly Regex CsClassRegex = new(
        @"^\s*(?<mods>(?:(?:public|internal|private|protected|static|sealed|abstract|partial|readonly)\s+)*)"
        + @"(?<kind>class|record|struct|interface)\s+(?<name>[A-Z_][A-Za-z0-9_]*)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // Method: [mods] ReturnType Name(
    // The mods group REQUIRES trailing whitespace on each modifier so the
    // regex doesn't greedily match `async` as the method name when the line is
    // `public async Task Foo(`.
    private static readonly Regex CsMethodRegex = new(
        @"^\s*(?<mods>(?:(?:public|internal|private|protected|static|async|virtual|override|abstract|sealed|new|readonly)\s+)*)"
        + @"(?<returnType>[A-Za-z_][A-Za-z0-9_<>\[\],\s\?\|]*?)\s+"
        + @"(?<name>[A-Z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static List<ExtractedSymbol> ExtractCsSymbols(string code)
    {
        var symbols = new List<ExtractedSymbol>();
        string? currentClass = null;

        foreach (var line in code.Split('\n'))
        {
            var classMatch = CsClassRegex.Match(line);
            if (classMatch.Success)
            {
                var mods = classMatch.Groups["mods"].Value;
                var name = classMatch.Groups["name"].Value;
                if (mods.Contains("private")) continue;

                symbols.Add(new ExtractedSymbol
                {
                    SymbolName = name,
                    Kind = SymbolKind.Class,
                    ContainingClass = null
                });
                currentClass = name;
                continue;
            }

            var methodMatch = CsMethodRegex.Match(line);
            if (methodMatch.Success)
            {
                var mods = methodMatch.Groups["mods"].Value;
                var name = methodMatch.Groups["name"].Value;

                if (mods.Contains("private")) continue;
                if (mods.Contains("override")) continue; // canonical home is the base class
                if (currentClass != null && name == currentClass) continue; // constructor
                if (name.Length < 4) continue;
                if (IsGenericMethodName(name)) continue;

                symbols.Add(new ExtractedSymbol
                {
                    SymbolName = name,
                    Kind = SymbolKind.Method,
                    ContainingClass = currentClass
                });
            }
        }

        return symbols;
    }

    // ── TypeScript / JavaScript ───────────────────────────────────────────────

    private static readonly Regex TsClassRegex = new(
        @"^\s*(?:export\s+)?(?:default\s+)?(?:abstract\s+)?class\s+(?<name>[A-Z_][A-Za-z0-9_]*)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex TsMethodRegex = new(
        @"^\s*(?<mods>(?:(?:public|private|protected|static|async|abstract|readonly)\s+)*)"
        + @"(?<name>[a-zA-Z_][A-Za-z0-9_]*)\s*(?:<[^>]+>)?\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex TsFunctionRegex = new(
        @"^\s*(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s+(?<name>[a-zA-Z_][A-Za-z0-9_]*)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static List<ExtractedSymbol> ExtractTsSymbols(string code)
    {
        var symbols = new List<ExtractedSymbol>();
        string? currentClass = null;

        foreach (var line in code.Split('\n'))
        {
            var classMatch = TsClassRegex.Match(line);
            if (classMatch.Success)
            {
                var name = classMatch.Groups["name"].Value;
                symbols.Add(new ExtractedSymbol
                {
                    SymbolName = name,
                    Kind = SymbolKind.Class,
                    ContainingClass = null
                });
                currentClass = name;
                continue;
            }

            // Top-level function declaration
            if (currentClass == null)
            {
                var fnMatch = TsFunctionRegex.Match(line);
                if (fnMatch.Success)
                {
                    var name = fnMatch.Groups["name"].Value;
                    if (name.Length >= 4 && !IsGenericMethodName(name))
                    {
                        symbols.Add(new ExtractedSymbol
                        {
                            SymbolName = name,
                            Kind = SymbolKind.Method,
                            ContainingClass = null
                        });
                    }
                }
                continue;
            }

            // Method inside a class
            var methodMatch = TsMethodRegex.Match(line);
            if (methodMatch.Success)
            {
                var mods = methodMatch.Groups["mods"].Value;
                var name = methodMatch.Groups["name"].Value;

                if (mods.Contains("private") || mods.Contains("protected")) continue;
                if (name == currentClass || name == "constructor") continue;
                if (name.Length < 4) continue;
                if (IsGenericMethodName(name)) continue;

                // Skip method CALLS — a method definition signature usually
                // ends with `) {`, `): <type> {`, `): <type> =>` etc. A method
                // call ends with `);` or `),` after the closing paren.
                var afterMatch = line.Substring(methodMatch.Index + methodMatch.Length);
                var stripped = afterMatch.Trim();
                if (stripped.EndsWith(";") || stripped.EndsWith(","))
                    continue;

                symbols.Add(new ExtractedSymbol
                {
                    SymbolName = name,
                    Kind = SymbolKind.Method,
                    ContainingClass = currentClass
                });
            }
        }

        return symbols;
    }

    // ── Python ─────────────────────────────────────────────────────────────────

    private static readonly Regex PyClassRegex = new(
        @"^(?<indent>\s*)class\s+(?<name>[A-Z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    private static readonly Regex PyMethodRegex = new(
        @"^(?<indent>\s*)def\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    private static List<ExtractedSymbol> ExtractPySymbols(string code)
    {
        var symbols = new List<ExtractedSymbol>();
        string? currentClass = null;
        var currentClassIndent = -1;

        foreach (var line in code.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Compute indentation (spaces only — Python convention)
            var indent = line.Length - line.TrimStart(' ').Length;

            // If this line is at the same or lower indent than the class
            // declaration, we've left the class body.
            if (currentClass != null && indent <= currentClassIndent)
            {
                currentClass = null;
                currentClassIndent = -1;
            }

            var classMatch = PyClassRegex.Match(line);
            if (classMatch.Success)
            {
                var name = classMatch.Groups["name"].Value;
                symbols.Add(new ExtractedSymbol
                {
                    SymbolName = name,
                    Kind = SymbolKind.Class,
                    ContainingClass = null
                });
                currentClass = name;
                currentClassIndent = indent;
                continue;
            }

            var methodMatch = PyMethodRegex.Match(line);
            if (methodMatch.Success)
            {
                var name = methodMatch.Groups["name"].Value;
                if (name.StartsWith("_")) continue; // private or dunder
                if (name.Length < 4) continue;
                if (IsGenericMethodName(name)) continue;

                symbols.Add(new ExtractedSymbol
                {
                    SymbolName = name,
                    Kind = SymbolKind.Method,
                    ContainingClass = currentClass
                });
            }
        }

        return symbols;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  PROMPT MATCHING
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true if the prompt mentions the symbol name as a word
    /// (word-boundary match, case-SENSITIVE). Case-sensitive because
    /// PascalCase and snake_case names are stable across casings, and
    /// case-insensitive matching causes false positives like matching
    /// "Add" inside "address".
    /// </summary>
    private static bool PromptMentionsSymbol(string prompt, string symbolName)
    {
        if (string.IsNullOrEmpty(symbolName) || symbolName.Length < 4)
            return false;

        var pattern = @"\b" + Regex.Escape(symbolName) + @"\b";
        return Regex.IsMatch(prompt, pattern);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  PATH NORMALIZATION
    // ════════════════════════════════════════════════════════════════════════

    private static string NormalizeRelativePath(string filePath, string projectRoot)
    {
        try
        {
            var full = Path.GetFullPath(filePath);
            var root = Path.GetFullPath(projectRoot);
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                var rel = full[root.Length..]
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return rel.Replace('\\', '/');
            }
        }
        catch { /* fall through */ }
        return filePath.Replace('\\', '/').TrimStart('/');
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  DATA MODELS
// ════════════════════════════════════════════════════════════════════════════

public class GlobalHintsStore
{
    public Dictionary<string, ProjectHints> Projects { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class ProjectHints
{
    public List<KeywordHint> Hints { get; set; } = new();
    public List<LearnedAssociation> AutoLearned { get; set; } = new();
    /// <summary>NEW: canonical (symbol → file) hints learned from applied edits.</summary>
    public List<SymbolHint> SymbolHints { get; set; } = new();
}

public class KeywordHint
{
    public List<string> Keywords { get; set; } = new();
    public List<string> Files { get; set; } = new();
}

public class LearnedAssociation
{
    public string Keyword { get; set; } = "";
    public string File { get; set; } = "";
    public int Score { get; set; }
    public string LastSeen { get; set; } = "";
}

/// <summary>NEW: a canonical (symbol → file) hint learned from an applied edit.</summary>
public class SymbolHint
{
    public string SymbolName { get; set; } = "";
    public SymbolKind Kind { get; set; }
    /// <summary>
    /// The class this method belongs to, or null for class-level symbols and
    /// top-level functions. Used together with SymbolName + Kind to uniquely
    /// identify a symbol (a Foo method on class Bar is different from a Foo
    /// method on class Baz).
    /// </summary>
    public string? ContainingClass { get; set; }
    public string File { get; set; } = "";
    public string FirstSeen { get; set; } = "";
    public string LastSeen { get; set; } = "";
}

public enum SymbolKind
{
    Class = 0,
    Method = 1
}

/// <summary>Internal-only: extracted from applied edit text, not persisted.</summary>
internal sealed class ExtractedSymbol
{
    public string SymbolName { get; set; } = "";
    public SymbolKind Kind { get; set; }
    public string? ContainingClass { get; set; }
}
