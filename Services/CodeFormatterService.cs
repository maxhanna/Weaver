using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;

namespace Weaver.Services;

public static class CodeFormatterService
{
    private static readonly string? _formatterDir;
    private static readonly string? _prettierCli;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Prettier built-in
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs",
        ".html", ".htm",
        ".css", ".scss", ".less",
        ".json", ".jsonc",
        ".md", ".markdown",
        ".yaml", ".yml",
        ".graphql", ".gql",
        ".vue", ".svelte",
        ".cshtml", ".razor",
        // Prettier plugins
        ".sql",
        ".java",
        ".sh", ".bash", ".zsh",
        ".php",
        ".toml",
        ".xml", ".svg",
        // Other formatters
        ".cs",
        ".py",
    };

    private static readonly Dictionary<string, string> PrettierParsers = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".ts", "typescript" },
        { ".tsx", "typescript" },
        { ".js", "babel" },
        { ".jsx", "babel" },
        { ".mjs", "babel" },
        { ".cjs", "babel" },
        { ".html", "html" },
        { ".htm", "html" },
        { ".css", "css" },
        { ".scss", "scss" },
        { ".less", "less" },
        { ".json", "json" },
        { ".jsonc", "json" },
        { ".md", "markdown" },
        { ".markdown", "markdown" },
        { ".yaml", "yaml" },
        { ".yml", "yaml" },
        { ".graphql", "graphql" },
        { ".gql", "graphql" },
        { ".vue", "vue" },
        { ".svelte", "svelte" },
        { ".cshtml", "html" },
        { ".razor", "html" },
        { ".sql", "sql" },
        { ".java", "java" },
        { ".sh", "sh" },
        { ".bash", "sh" },
        { ".zsh", "sh" },
        { ".php", "php" },
        { ".toml", "toml" },
        { ".xml", "xml" },
        { ".svg", "xml" },
    };

    static CodeFormatterService()
    {
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDir))
        {
            var dir = new DirectoryInfo(baseDir);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, ".formatter");
                if (Directory.Exists(candidate))
                {
                    _formatterDir = candidate;
                    _prettierCli = Path.Combine(candidate, "node_modules", ".bin", "prettier.cmd");
                    break;
                }
                dir = dir.Parent;
            }
        }
    }

    public static bool CanFormat(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return SupportedExtensions.Contains(ext);
    }

    public static async Task<string> FormatAsync(string filePath, string content, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrWhiteSpace(content) || !SupportedExtensions.Contains(ext))
            return content;

        if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
            return FormatWithRoslyn(content);

        if (ext.Equals(".py", StringComparison.OrdinalIgnoreCase))
            return await FormatWithBlackAsync(content, ct);

        var formatted = await FormatWithPrettierAsync(ext, content, ct);

        if (ext is ".html" or ".htm" or ".css" or ".cshtml" or ".razor" or ".vue" or ".svelte")
            formatted = FixCssSpacing(formatted);

        return formatted;
    }

    private static string FixCssSpacing(string content)
    {
        return System.Text.RegularExpressions.Regex.Replace(content,
            @"(\d+(?:\.\d+)?)(px|em|rem|%|vh|vw|vmin|vmax|pt|pc|mm|cm|ch|ex)(\d)",
            "$1$2 $3");
    }

    private static async Task<string> FormatWithPrettierAsync(string ext, string content, CancellationToken ct)
    {
        if (_prettierCli == null || !File.Exists(_prettierCli))
        {
            Debug.WriteLine("[CodeFormatter] Local Prettier not available at " + _prettierCli);
            return content;
        }

        var parser = PrettierParsers.GetValueOrDefault(ext, "babel");
        var dummyName = $"dummy{ext}";

        var prettierArgs = $"--stdin-filepath \"{dummyName}\"";
        if (parser == "html") prettierArgs += " --bracket-same-line";

        var psi = new ProcessStartInfo(_prettierCli, prettierArgs)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = _formatterDir,
        };

        try
        {
            using var proc = new Process { StartInfo = psi };
            proc.Start();
            await proc.StandardInput.WriteAsync(content);
            proc.StandardInput.Close();
            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                Debug.WriteLine($"[CodeFormatter] Prettier failed for {ext}: {error}");
                return content;
            }
            return output;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CodeFormatter] Prettier error for {ext}: {ex.Message}");
            return content;
        }
    }

    private static async Task<string> FormatWithBlackAsync(string content, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("python", "-m black --quiet --stdin-filename dummy.py -")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        try
        {
            using var proc = new Process { StartInfo = psi };
            proc.Start();
            await proc.StandardInput.WriteAsync(content);
            proc.StandardInput.Close();
            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                Debug.WriteLine($"[CodeFormatter] Black failed: {error}");
                return content;
            }
            return output;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CodeFormatter] Black error: {ex.Message}");
            return content;
        }
    }

    private static string FormatWithRoslyn(string content)
    {
        try
        {
            var tree = CSharpSyntaxTree.ParseText(content, new CSharpParseOptions(LanguageVersion.Latest));
            var root = tree.GetRoot();
            using var workspace = new AdhocWorkspace();
            var formattedRoot = Formatter.Format(root, workspace);
            return formattedRoot.ToFullString();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CodeFormatter] Roslyn error: {ex.Message}");
            return content;
        }
    }
}
