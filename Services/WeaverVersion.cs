namespace Weaver;

/// <summary>
/// Resolves the running Weaver version for benchmark reporting.
///
/// History: the repo ships a <c>.weaver-version.txt</c> at its root, while the
/// self-update flow in <see cref="BughostedController"/> tracks a copy under
/// <c>%LOCALAPPDATA%\Weaver\.weaver-version</c>. This resolver prefers the repo
/// file (the source of truth for a build) and falls back to the LocalAppData copy
/// so a deployed exe still reports a sensible value.
/// </summary>
public static class WeaverVersion
{
    static readonly string[] FileNames = { ".weaver-version.txt", ".weaver-version" };

    /// <summary>
    /// Reads the version string. <paramref name="searchDirs"/> are checked in order
    /// (each for both known file names) before the LocalAppData fallback. Returns
    /// "0" when nothing is found.
    /// </summary>
    public static string Read(params string?[] searchDirs)
    {
        foreach (var dir in searchDirs)
        {
            var v = TryReadFrom(dir);
            if (v != null) return v;
        }

        // Process base directory (where the exe runs from).
        var baseDir = TryReadFrom(AppContext.BaseDirectory);
        if (baseDir != null) return baseDir;

        // LocalAppData self-update copy.
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var localCopy = TryReadFrom(Path.Combine(localAppData, "Weaver"));
            if (localCopy != null) return localCopy;
        }
        catch { /* best-effort */ }

        return "0";
    }

    static string? TryReadFrom(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        foreach (var name in FileNames)
        {
            try
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path))
                {
                    var text = File.ReadAllText(path).Trim();
                    if (text.Length > 0) return text;
                }
            }
            catch { /* keep looking */ }
        }
        return null;
    }
}
