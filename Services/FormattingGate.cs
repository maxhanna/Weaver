using System.Diagnostics;
using System.Text;

namespace Weaver;

/// <summary>
/// The formattingClean gate's oracle: runs a per-extension check-mode formatter
/// command over the files a benchmark run edited. A command that exits non-zero
/// (or a file with no configured command) fails/skips the check for that file.
///
/// The command template is tokenized and executed directly (no cmd.exe/sh), so the
/// substituted file path is passed as a single argv element rather than interpolated
/// into a shell string — file names can legally contain shell metacharacters
/// (&amp;, |, ^, $, `), and cards may originate from a shared BugHosted leaderboard,
/// so the file path must never be shell-parsed.
/// </summary>
public static class FormattingGate
{
    const string FilePlaceholder = "{file}";

    /// <summary>
    /// Returns null when unmeasured (no formatting config, "none" mode, or none of the
    /// edited files have a configured command) — an unmeasured gate counts as not-perfect,
    /// per the benchmark contract, so this is distinct from a passing check.
    /// </summary>
    public static async Task<bool?> CheckAsync(string projectRoot, IReadOnlyList<string> editedPaths, BenchmarkFormatting? config)
    {
        if (config == null || string.Equals(config.Mode, "none", StringComparison.OrdinalIgnoreCase))
            return null;
        if (config.Commands.Count == 0 || editedPaths.Count == 0)
            return null;

        var checkedAny = false;
        foreach (var relPath in editedPaths)
        {
            var ext = Path.GetExtension(relPath).TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(ext) || !config.Commands.TryGetValue(ext, out var commandTemplate) ||
                string.IsNullOrWhiteSpace(commandTemplate))
                continue;

            var fullPath = Path.IsPathRooted(relPath) ? relPath : Path.Combine(projectRoot, relPath);
            if (!File.Exists(fullPath)) continue;

            checkedAny = true;
            if (!await RunCheckCommandAsync(commandTemplate, fullPath, projectRoot))
                return false;
        }

        return checkedAny ? true : (bool?)null;
    }

    static async Task<bool> RunCheckCommandAsync(string commandTemplate, string fullPath, string workingDirectory)
    {
        try
        {
            var tokens = Tokenize(commandTemplate);
            if (tokens.Count == 0) return false;

            var psi = new ProcessStartInfo
            {
                FileName = tokens[0],
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            for (var i = 1; i < tokens.Count; i++)
                psi.ArgumentList.Add(tokens[i] == FilePlaceholder ? fullPath : tokens[i]);

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Whitespace tokenizer respecting "..." / '...' quoted segments. No shell semantics — quotes only group tokens.</summary>
    internal static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char? quote = null;

        foreach (var c in command)
        {
            if (quote != null)
            {
                if (c == quote) quote = null;
                else current.Append(c);
            }
            else if (c == '"' || c == '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
