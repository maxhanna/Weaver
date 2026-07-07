using System.Diagnostics;

namespace Weaver;

/// <summary>
/// The formattingClean gate's oracle: runs a per-extension check-mode formatter
/// command over the files a benchmark run edited. A command that exits non-zero
/// (or a file with no configured command) fails/skips the check for that file.
/// </summary>
public static class FormattingGate
{
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
            var command = commandTemplate.Replace("{file}", fullPath);
            if (!await RunCheckCommandAsync(command, projectRoot))
                return false;
        }

        return checkedAny ? true : (bool?)null;
    }

    static async Task<bool> RunCheckCommandAsync(string command, string workingDirectory)
    {
        try
        {
            var isWindows = OperatingSystem.IsWindows();
            var psi = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/sh",
                Arguments = isWindows ? $"/c {command}" : $"-c \"{command}\"",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
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
}
