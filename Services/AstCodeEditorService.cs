using TreeSitter;

namespace Weaver.Services;

public static class AstCodeEditorService
{
    private static readonly Dictionary<string, string> LanguageMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".ts", "TypeScript" },
        { ".tsx", "TSX" },
        { ".js", "JavaScript" },
        { ".jsx", "JavaScript" },
        { ".mjs", "JavaScript" },
        { ".cjs", "JavaScript" },
        { ".cs", "c_sharp" },
    };

    public static string[] GetSupportedExtensions() => [.. LanguageMap.Keys];

    public static bool IsSupportedExtension(string fileExt) =>
        LanguageMap.ContainsKey(fileExt.ToLowerInvariant());

    public static bool CanEdit(string filePath)
    {
        var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
        return ext != null && LanguageMap.ContainsKey(ext);
    }

    public static List<(string name, string source, int startLine)> FindAllFunctions(
        string fileContent, string fileExtension)
    {
        var results = new List<(string name, string source, int startLine)>();
        if (!LanguageMap.TryGetValue(fileExtension.ToLowerInvariant(), out var langName))
            return results;

        try
        {
            using var language = new Language(langName);
            using var parser = new Parser(language);
            using var tree = parser.Parse(fileContent);
            if (tree == null) return results;

            var patterns = langName switch
            {
                "TypeScript" or "TSX" => new[]
                {
                    "(method_definition name: (property_identifier) @name) @target",
                    "(function_declaration name: (identifier) @name) @target",
                    "(method_signature name: (property_identifier) @name) @target",
                    "(function_signature name: (identifier) @name) @target",
                    "(generator_method name: (property_identifier) @name) @target",
                    "(generator_declaration name: (identifier) @name) @target",
                },
                "JavaScript" => new[]
                {
                    "(method_definition name: (property_identifier) @name) @target",
                    "(function_declaration name: (identifier) @name) @target",
                    "(generator_method name: (property_identifier) @name) @target",
                    "(generator_declaration name: (identifier) @name) @target",
                },
                "c_sharp" => new[]
                {
                    "(method_declaration name: (identifier) @name) @target",
                    "(local_function_statement name: (identifier) @name) @target",
                    "(constructor_declaration name: (identifier) @name) @target",
                },
                _ => []
            };

            foreach (var pattern in patterns)
            {
                using var query = new Query(language, pattern);
                string? lastName = null;

                foreach (var capture in query.Execute(tree.RootNode).Captures)
                {
                    if (capture.Name == "name")
                    {
                        lastName = capture.Node.Text;
                    }
                    else if (capture.Name == "method" || capture.Name == "target" || capture.Name == "func")
                    {
                        if (!string.IsNullOrWhiteSpace(lastName))
                        {
                            var startIndex = capture.Node.StartIndex;
                            var endIndex = capture.Node.EndIndex;

                            var lineStart = fileContent.LastIndexOf('\n', startIndex) + 1;
                            if (lineStart < 0) lineStart = 0;

                            var fullOldStr = fileContent[lineStart..endIndex]
                                .Replace("\r\n", "\n").Replace("\r", "\n");

                            var startLine = capture.Node.StartPosition.Row + 1;

                            results.Add((lastName, fullOldStr, startLine));
                        }
                        lastName = null;
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

        try
        {
            using var language = new Language(langName);
            using var parser = new Parser(language);
            using var tree = parser.Parse(fileContent);
            if (tree == null)
                return (null, 0, "Failed to parse file");

            var patterns = langName switch
            {
                "TypeScript" or "TSX" => new[]
                {
                    "(method_definition name: (property_identifier) @name) @target",
                    "(function_declaration name: (identifier) @name) @target",
                    "(method_signature name: (property_identifier) @name) @target",
                    "(function_signature name: (identifier) @name) @target",
                    "(generator_method name: (property_identifier) @name) @target",
                    "(generator_declaration name: (identifier) @name) @target",
                },
                "JavaScript" => new[]
                {
                    "(method_definition name: (property_identifier) @name) @target",
                    "(function_declaration name: (identifier) @name) @target",
                    "(generator_method name: (property_identifier) @name) @target",
                    "(generator_declaration name: (identifier) @name) @target",
                },
                "c_sharp" => new[]
                {
                    "(method_declaration name: (identifier) @name) @target",
                    "(local_function_statement name: (identifier) @name) @target",
                    "(constructor_declaration name: (identifier) @name) @target",
                },
                _ => []
            };

            foreach (var pattern in patterns)
            {
                using var query = new Query(language, pattern);
                string? lastName = null;

                foreach (var capture in query.Execute(tree.RootNode).Captures)
                {
                    if (capture.Name == "name")
                    {
                        lastName = capture.Node.Text;
                    }
                    else if ((capture.Name == "method" || capture.Name == "target" || capture.Name == "func")
                             && lastName == targetSymbol)
                    {
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

            return (null, 0, $"'{targetSymbol}' not found in {fileExtension} file");
        }
        catch (Exception ex)
        {
            return (null, 0, $"Tree-sitter error: {ex.Message}");
        }
    }
}
