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

    /// <summary>Test seam: when set, external formatter binaries (prettier, black,
    /// clang-format, …) are skipped and content passes through unchanged — exactly as on a
    /// machine without them installed. Pipeline tests script verbatim edits and assert the
    /// exact on-disk result, so their outcome must never depend on npx/node availability
    /// (prettier's default singleQuote=false silently rewrites single-quoted TS lines, which
    /// both broke verbatim assertions and broke subsequent edits' anchors in scripted plans).
    /// AsyncLocal flows down the caller's async pipeline only and never leaks sideways into
    /// concurrently running tests. In-process Roslyn (.cs) formatting stays on — it is
    /// deterministic and spawns nothing.</summary>
    internal static readonly System.Threading.AsyncLocal<bool> SuppressExternalFormattersForTests = new();

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
        ".rb",
        // clang-format
        ".c", ".h",
        ".cpp", ".hpp", ".cc", ".cxx", ".hh", ".hxx",
        // gofmt
        ".go",
        // rustfmt
        ".rs",
        // ktlint
        ".kt", ".kts",
        // dart format
        ".dart",
        // swift-format
        ".swift",
        // Roslyn
        ".cs",
        // Black
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
        { ".rb", "ruby" },
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
                    // npm's .bin holds prettier.cmd on Windows and a bare `prettier` on Unix.
                    _prettierCli = Path.Combine(candidate, "node_modules", ".bin",
                        OperatingSystem.IsWindows() ? "prettier.cmd" : "prettier");
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

    private static readonly HashSet<string> PrettierExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs",
        ".html", ".htm", ".css", ".scss", ".less",
        ".json", ".jsonc",
        ".md", ".markdown",
        ".yaml", ".yml",
        ".graphql", ".gql",
        ".vue", ".svelte",
        ".cshtml", ".razor",
        ".sql", ".java",
        ".sh", ".bash", ".zsh",
        ".php", ".toml",
        ".xml", ".svg",
        ".rb",
    };

    private static readonly HashSet<string> ClangFormatExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".h", ".cpp", ".hpp", ".cc", ".cxx", ".hh", ".hxx",
    };

    public static async Task<string> FormatAsync(string filePath, string content, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrWhiteSpace(content) || !SupportedExtensions.Contains(ext))
            return content;

        if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
            return FormatWithRoslyn(content);

        // External formatters are processes with environment-dependent availability and
        // opinionated defaults — tests that need byte-exact edits opt out here (see the
        // AsyncLocal's doc above).
        if (SuppressExternalFormattersForTests.Value)
            return content;

        if (ext.Equals(".py", StringComparison.OrdinalIgnoreCase))
            return await FormatWithBlackAsync(content, ct);

        if (PrettierExts.Contains(ext))
        {
            var formatted = await FormatWithPrettierAsync(ext, content, ct);
            if (ext is ".html" or ".htm" or ".css" or ".cshtml" or ".razor" or ".vue" or ".svelte")
                formatted = FixCssSpacing(formatted);
            return formatted;
        }

        if (ClangFormatExts.Contains(ext))
            return await FormatWithClangFormatAsync(ext, content, ct);

        if (ext.Equals(".go", StringComparison.OrdinalIgnoreCase))
            return await FormatWithGofmtAsync(content, ct);

        if (ext.Equals(".rs", StringComparison.OrdinalIgnoreCase))
            return await FormatWithRustfmtAsync(content, ct);

        if (ext is ".kt" or ".kts")
            return await FormatWithKtlintAsync(content, ct);

        if (ext.Equals(".dart", StringComparison.OrdinalIgnoreCase))
            return await FormatWithDartFormatAsync(content, ct);

        if (ext.Equals(".swift", StringComparison.OrdinalIgnoreCase))
            return await FormatWithSwiftFormatAsync(content, ct);

        return content;
    }

    private static string FixCssSpacing(string content)
    {
        return System.Text.RegularExpressions.Regex.Replace(content,
            @"(\d+(?:\.\d+)?)(px|em|rem|%|vh|vw|vmin|vmax|pt|pc|mm|cm|ch|ex)(\d)",
            "$1$2 $3");
    }

    private static async Task<string> FormatWithPrettierAsync(string ext, string content, CancellationToken ct)
    {
        string prettierCmd;
        string prettierArgsBase;
        string workingDir;

        if (_prettierCli != null && File.Exists(_prettierCli))
        {
            prettierCmd = _prettierCli;
            prettierArgsBase = "";
            workingDir = _formatterDir!;
        }
        else
        {
            if (!await IsNpxAvailableAsync())
            {
                Debug.WriteLine("[CodeFormatter] npx not found on PATH");
                return content;
            }
            prettierCmd = OperatingSystem.IsWindows() ? "npx.cmd" : "npx";
            prettierArgsBase = "--yes prettier";
            workingDir = Path.GetTempPath();
        }

        var parser = PrettierParsers.GetValueOrDefault(ext, "babel");
        var dummyName = $"dummy{ext}";

        var prettierArgs = $"{prettierArgsBase} --stdin-filepath \"{dummyName}\" --print-width 200";
        if (parser == "html") prettierArgs += " --bracket-same-line";

        return await RunToolAsync(prettierCmd, prettierArgs, content, workingDir, ct);
    }

    private static async Task<string> FormatWithClangFormatAsync(string ext, string content, CancellationToken ct)
    {
        var style = ext is ".c" or ".h" ? "c" : "cpp";
        var args = $"--style=file --assume-filename=dummy{ext} -i";
        // clang-format reads from stdin when -i is not used with a file argument
        // Use --Werror to error on invalid
        return await RunToolAsync("clang-format", $"--style=file --assume-filename=dummy{ext}", content, Path.GetTempPath(), ct);
    }

    private static async Task<string> FormatWithGofmtAsync(string content, CancellationToken ct)
    {
        return await RunToolAsync("gofmt", "", content, Path.GetTempPath(), ct);
    }

    private static async Task<string> FormatWithRustfmtAsync(string content, CancellationToken ct)
    {
        return await RunToolAsync("rustfmt", "--emit=stdout", content, Path.GetTempPath(), ct);
    }

    private static async Task<string> FormatWithKtlintAsync(string content, CancellationToken ct)
    {
        return await RunToolAsync("ktlint", "--format --stdin", content, Path.GetTempPath(), ct);
    }

    private static async Task<string> FormatWithDartFormatAsync(string content, CancellationToken ct)
    {
        return await RunToolAsync("dart", "format", content, Path.GetTempPath(), ct);
    }

    private static async Task<string> FormatWithSwiftFormatAsync(string content, CancellationToken ct)
    {
        return await RunToolAsync("swift-format", "", content, Path.GetTempPath(), ct);
    }

    private static async Task<string> RunToolAsync(string command, string args, string content, string workingDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(command, args)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDir,
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
                Debug.WriteLine($"[CodeFormatter] {command} failed for: {error}");
                return content;
            }
            return output;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CodeFormatter] {command} error: {ex.Message}");
            return content;
        }
    }

    private static async Task<bool> IsNpxAvailableAsync()
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(OperatingSystem.IsWindows() ? "npx.cmd" : "npx", "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync();
            proc.WaitForExit(5000);
            return proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
        }
        catch { return false; }
    }

    private static async Task<string> FormatWithBlackAsync(string content, CancellationToken ct)
    {
        return await RunToolAsync("python", "-m black --quiet --stdin-filename dummy.py -", content, Path.GetTempPath(), ct);
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
