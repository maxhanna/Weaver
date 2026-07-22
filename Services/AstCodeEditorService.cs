using TreeSitter;
namespace Weaver.Services;
public static class AstCodeEditorService
{
    // Maps file extension -> TreeSitter grammar name (for new Language(name))
    private static readonly Dictionary<string, string> LanguageMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".ts", "TypeScript" },
        { ".tsx", "TSX" },
        { ".js", "JavaScript" },
        { ".jsx", "JavaScript" },
        { ".mjs", "JavaScript" },
        { ".cjs", "JavaScript" },
        { ".cs", "c_sharp" },
        { ".py", "python" },
        { ".rb", "ruby" },
        { ".go", "go" },
        { ".rs", "rust" },
        { ".java", "java" },
        { ".php", "php" },
        { ".c", "c" },
        { ".h", "c" },
        { ".cpp", "cpp" },
        { ".cc", "cpp" },
        { ".cxx", "cpp" },
        { ".hpp", "cpp" },
        { ".css", "css" },
        { ".swift", "swift" },
        { ".scala", "scala" },
        { ".hs", "haskell" },
        { ".jl", "julia" },
        { ".sh", "bash" },
        { ".bash", "bash" },
        { ".zsh", "bash" },
        { ".toml", "toml" },
        { ".ql", "ql" },
        { ".razor", "razor" },
    };
    // TreeSitter query patterns to find named declarations, grouped by grammar name.
    // Each pattern captures @name (the declaration name) and @target (the full node).
    private static readonly Dictionary<string, string[]> QueryPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TypeScript"] =
        [
            "(property_definition name: (property_identifier) @name) @target",
            "(method_definition name: (property_identifier) @name) @target",
            "(function_declaration name: (identifier) @name) @target",
            "(method_signature name: (property_identifier) @name) @target",
            "(function_signature name: (identifier) @name) @target",
            "(generator_method name: (property_identifier) @name) @target",
            "(generator_declaration name: (identifier) @name) @target",
            "(class_declaration name: (type_identifier) @name) @target",
            "(interface_declaration name: (type_identifier) @name) @target",
            "(enum_declaration name: (identifier) @name) @target",
        ],
        ["TSX"] =
        [
            "(property_definition name: (property_identifier) @name) @target",
            "(method_definition name: (property_identifier) @name) @target",
            "(function_declaration name: (identifier) @name) @target",
            "(method_signature name: (property_identifier) @name) @target",
            "(function_signature name: (identifier) @name) @target",
            "(generator_method name: (property_identifier) @name) @target",
            "(generator_declaration name: (identifier) @name) @target",
            "(class_declaration name: (type_identifier) @name) @target",
            "(interface_declaration name: (type_identifier) @name) @target",
        ],
        ["JavaScript"] =
        [
            "(method_definition name: (property_identifier) @name) @target",
            "(function_declaration name: (identifier) @name) @target",
            "(generator_method name: (property_identifier) @name) @target",
            "(generator_declaration name: (identifier) @name) @target",
            "(class_declaration name: (identifier) @name) @target",
            "(export_statement declaration: (function_declaration name: (identifier) @name)) @target",
        ],
        ["c_sharp"] =
        [
            "(method_declaration name: (identifier) @name) @target",
            "(local_function_statement name: (identifier) @name) @target",
            "(constructor_declaration name: (identifier) @name) @target",
            "(class_declaration name: (identifier) @name) @target",
            "(struct_declaration name: (identifier) @name) @target",
            "(interface_declaration name: (identifier) @name) @target",
            "(record_declaration name: (identifier) @name) @target",
            "(enum_declaration name: (identifier) @name) @target",
            "(property_declaration name: (identifier) @name) @target",
        ],
        ["python"] =
        [
            "(function_definition name: (identifier) @name) @target",
            "(class_definition name: (identifier) @name) @target",
            "(decorated_definition definition: (function_definition name: (identifier) @name)) @target",
            "(decorated_definition definition: (class_definition name: (identifier) @name)) @target",
        ],
        ["ruby"] =
        [
            "(method name: (identifier) @name) @target",
            "(singleton_method name: (identifier) @name) @target",
            "(class name: (constant) @name) @target",
            "(module name: (constant) @name) @target",
        ],
        ["go"] =
        [
            "(function_declaration name: (identifier) @name) @target",
            "(method_declaration name: (field_identifier) @name) @target",
            "(type_declaration (type_spec name: (type_identifier) @name)) @target",
        ],
        ["rust"] =
        [
            "(function_item name: (identifier) @name) @target",
            "(struct_item name: (type_identifier) @name) @target",
            "(enum_item name: (type_identifier) @name) @target",
            "(trait_item name: (type_identifier) @name) @target",
            "(impl_item trait: (type_identifier) @name) @target",
            "(impl_item type: (type_identifier) @name) @target",
            "(type_item name: (type_identifier) @name) @target",
            "(constant_item name: (identifier) @name) @target",
            "(static_item name: (identifier) @name) @target",
        ],
        ["java"] =
        [
            "(method_declaration name: (identifier) @name) @target",
            "(class_declaration name: (identifier) @name) @target",
            "(interface_declaration name: (identifier) @name) @target",
            "(enum_declaration name: (identifier) @name) @target",
            "(constructor_declaration name: (identifier) @name) @target",
            "(record_declaration name: (identifier) @name) @target",
            "(annotation_type_declaration name: (identifier) @name) @target",
        ],
        ["php"] =
        [
            "(method_declaration name: (name) @name) @target",
            "(function_definition name: (name) @name) @target",
            "(class_declaration name: (name) @name) @target",
            "(interface_declaration name: (name) @name) @target",
            "(trait_declaration name: (name) @name) @target",
            "(enum_declaration name: (name) @name) @target",
        ],
        ["c"] =
        [
            "(function_definition declarator: (function_declarator declarator: (identifier) @name)) @target",
        ],
        ["cpp"] =
        [
            "(function_definition declarator: (function_declarator declarator: (identifier) @name)) @target",
            "(class_specifier name: (type_identifier) @name) @target",
            "(struct_specifier name: (type_identifier) @name) @target",
            "(enum_specifier name: (type_identifier) @name) @target",
            "(template_declaration declaration: (function_definition declarator: (function_declarator declarator: (identifier) @name))) @target",
            "(template_declaration declaration: (class_specifier name: (type_identifier) @name)) @target",
        ],
        ["css"] =
        [
            "(rule_set (selectors) @name) @target",
        ],
        ["swift"] =
        [
            "(function_declaration name: (identifier) @name) @target",
            "(method_declaration name: (identifier) @name) @target",
            "(class_declaration name: (identifier) @name) @target",
            "(struct_declaration name: (identifier) @name) @target",
            "(enum_declaration name: (identifier) @name) @target",
            "(protocol_declaration name: (identifier) @name) @target",
            "(extension_declaration name: (identifier) @name) @target",
            "(constructor_declaration name: (identifier) @name) @target",
            "(destructor_declaration name: (identifier) @name) @target",
        ],
        ["scala"] =
        [
            "(function_definition name: (identifier) @name) @target",
            "(class_definition name: (identifier) @name) @target",
            "(trait_definition name: (identifier) @name) @target",
            "(object_definition name: (identifier) @name) @target",
            "(enum_definition name: (identifier) @name) @target",
        ],
        ["haskell"] =
        [
            "(function name: (variable) @name) @target",
            "(class name: (type) @name) @target",
            "(instance name: (type) @name) @target",
            "(data name: (type) @name) @target",
            "(type name: (type) @name) @target",
        ],
        ["julia"] =
        [
            "(function_definition name: (identifier) @name) @target",
            "(macro_definition name: (identifier) @name) @target",
            "(struct_definition name: (identifier) @name) @target",
            "(abstract_definition name: (identifier) @name) @target",
            "(primitive_definition name: (identifier) @name) @target",
            "(module_definition name: (identifier) @name) @target",
        ],
        ["bash"] =
        [
            "(function_definition name: (word) @name) @target",
        ],
    }; 
    public static bool IsSupportedExtension(string fileExt) => LanguageMap.ContainsKey(fileExt.ToLowerInvariant());
    public static List<(string name, string source, int startLine)> FindAllFunctions(
        string fileContent, string fileExtension)
    {
        var results = new List<(string name, string source, int startLine)>();
        if (!LanguageMap.TryGetValue(fileExtension.ToLowerInvariant(), out var langName))
            return results;
        var patterns = QueryPatterns.GetValueOrDefault(langName);
        if (patterns == null || patterns.Length == 0)
            return results;
        try
        {
            using var language = new Language(langName);
            using var parser = new Parser(language);
            using var tree = parser.Parse(fileContent);
            if (tree == null) return results;
            foreach (var pattern in patterns)
            {
                Query query;
                try { query = new Query(language, pattern); }
                catch { continue; }
                using (query)
                {
                    var allCaptures = query.Execute(tree.RootNode).Captures.ToList();
                    var nameByStart = new Dictionary<int, string>();
                    foreach (var c in allCaptures)
                    {
                        if (c.Name == "name")
                            nameByStart[c.Node.StartIndex] = c.Node.Text;
                    }
                    foreach (var capture in allCaptures)
                    {
                        if (capture.Name != "method" && capture.Name != "target" && capture.Name != "func")
                            continue;
                        var targetStart = capture.Node.StartIndex;
                        var targetEnd = capture.Node.EndIndex;
                        var resolvedName = nameByStart
                            .Where(kvp => kvp.Key >= targetStart && kvp.Key < targetEnd)
                            .OrderBy(kvp => kvp.Key)
                            .Select(kvp => kvp.Value)
                            .FirstOrDefault();
                        if (string.IsNullOrWhiteSpace(resolvedName))
                            continue;
                        var startIndex = capture.Node.StartIndex;
                        var endIndex = capture.Node.EndIndex;
                        var lineStart = fileContent.LastIndexOf('\n', startIndex) + 1;
                        if (lineStart < 0) lineStart = 0;
                        var fullOldStr = fileContent[lineStart..endIndex]
                            .Replace("\r\n", "\n").Replace("\r", "\n");
                        var startLine = capture.Node.StartPosition.Row + 1;
                        results.Add((resolvedName, fullOldStr, startLine));
                    }
                }
            }
            return results;
        }
        catch
        {
            return results;
        }
    }
    public static (string? oldString, int startLine, string? error) FindFunctionSource(
        string fileContent, string targetSymbol, string fileExtension)
    {
        if (!LanguageMap.TryGetValue(fileExtension.ToLowerInvariant(), out var langName))
            return (null, 0, $"Unsupported extension: {fileExtension}");
        var patterns = QueryPatterns.GetValueOrDefault(langName);
        if (patterns == null || patterns.Length == 0)
            return (null, 0, $"No query patterns for {langName}");
        try
        {
            using var language = new Language(langName);
            using var parser = new Parser(language);
            using var tree = parser.Parse(fileContent);
            if (tree == null)
                return (null, 0, "Failed to parse file");
            foreach (var pattern in patterns)
            {
                Query q2;
                try { q2 = new Query(language, pattern); }
                catch { continue; }
                using (q2)
                {
                    var allCaptures = q2.Execute(tree.RootNode).Captures.ToList();
                    // Build a map: name-node start-index → name text for all @name captures
                    var nameByStart = new Dictionary<int, string>();
                    foreach (var c in allCaptures)
                    {
                        if (c.Name == "name")
                            nameByStart[c.Node.StartIndex] = c.Node.Text;
                    }
                    foreach (var capture in allCaptures)
                    {
                        if (capture.Name != "method" && capture.Name != "target" && capture.Name != "func")
                            continue;
                        // Find the @name capture that falls WITHIN this target node's range
                        // (the name is always a child of the target node in the syntax tree)
                        var targetStart = capture.Node.StartIndex;
                        var targetEnd = capture.Node.EndIndex;
                        var resolvedName = nameByStart
                            .Where(kvp => kvp.Key >= targetStart && kvp.Key < targetEnd)
                            .OrderBy(kvp => kvp.Key)
                            .Select(kvp => kvp.Value)
                            .FirstOrDefault();
                        if (resolvedName == null || resolvedName != targetSymbol)
                            continue;
                        var startIndex = capture.Node.StartIndex;
                        var endIndex = capture.Node.EndIndex;
                        var lineStart = fileContent.LastIndexOf('\n', startIndex) + 1;
                        if (lineStart < 0) lineStart = 0;
                        var fullOldStr = fileContent[lineStart..endIndex];
                        fullOldStr = fullOldStr.Replace("\r\n", "\n").Replace("\r", "\n");
                        var startLine = capture.Node.StartPosition.Row + 1;
                        return (fullOldStr, startLine, null);
                    }
                }
            }
            return (null, 0, $"'{targetSymbol}' not found in {langName} file");
        }
        catch (Exception ex)
        {
            return (null, 0, $"Tree-sitter error: {ex.Message}");
        }
    }
}
