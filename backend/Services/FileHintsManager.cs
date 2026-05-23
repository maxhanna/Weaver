using System.Text.Json;

namespace MaestroBackend.Services;

public class FileHintsStore
{
    public List<KeywordHint> Hints { get; set; } = new();
    public List<LearnedAssociation> AutoLearned { get; set; } = new();
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

public class FileHintsManager
{
    private readonly object _lock = new();

    private string HintsPath(string projectRoot) =>
        Path.Combine(projectRoot, "filehints.json");

    private FileHintsStore Load(string projectRoot)
    {
        var path = HintsPath(projectRoot);
        try
        {
            if (System.IO.File.Exists(path))
            {
                var json = System.IO.File.ReadAllText(path);
                return JsonSerializer.Deserialize<FileHintsStore>(json) ?? new FileHintsStore();
            }
        }
        catch { }
        return new FileHintsStore();
    }

    private void Save(string projectRoot, FileHintsStore store)
    {
        var path = HintsPath(projectRoot);
        var json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
        System.IO.File.WriteAllText(path, json);
    }

    private static FileHintsStore SeedDefaults()
    {
        return new FileHintsStore
        {
            Hints = new List<KeywordHint>
            {
                new() { Keywords = new() { "terminal" }, Files = new() { "backend/wwwroot/app.js", "backend/wwwroot/index.html" } },
                new() { Keywords = new() { "settings", "popup", "panel", "toggle" }, Files = new() { "backend/wwwroot/app.js", "backend/wwwroot/index.html", "backend/wwwroot/styles.css" } },
                new() { Keywords = new() { "blur", "overlay", "background" }, Files = new() { "backend/wwwroot/styles.css" } },
                new() { Keywords = new() { "delete", "remove" }, Files = new() { "backend/wwwroot/app.js", "backend/wwwroot/index.html" } },
                new() { Keywords = new() { "card", "column" }, Files = new() { "backend/wwwroot/app.js", "backend/wwwroot/index.html", "backend/wwwroot/styles.css" } }
            }
        };
    }

    private FileHintsStore LoadOrSeed(string projectRoot)
    {
        lock (_lock)
        {
            var store = Load(projectRoot);
            if (store.Hints.Count == 0 && store.AutoLearned.Count == 0)
            {
                store = SeedDefaults();
                Save(projectRoot, store);
            }
            return store;
        }
    }

    public List<string> GetFilesForPrompt(string prompt, string projectRoot)
    {
        var lower = prompt.ToLowerInvariant();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var store = LoadOrSeed(projectRoot);
        foreach (var hint in store.Hints)
        {
            if (hint.Keywords.Any(k => lower.Contains(k, StringComparison.Ordinal)))
                foreach (var f in hint.Files)
                    files.Add(f);
        }

        return files.ToList();
    }

    public void RecordAssociation(string keyword, string file, string projectRoot)
    {
        lock (_lock)
        {
            var store = LoadOrSeed(projectRoot);
            var normalizedKeyword = keyword.ToLowerInvariant();
            var normalizedFile = file.Replace('\\', '/').TrimStart('/');

            var existing = store.AutoLearned.FirstOrDefault(a =>
                string.Equals(a.Keyword, normalizedKeyword, StringComparison.Ordinal) &&
                string.Equals(a.File, normalizedFile, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.Score++;
                existing.LastSeen = DateTime.UtcNow.ToString("o");
            }
            else
            {
                store.AutoLearned.Add(new LearnedAssociation
                {
                    Keyword = normalizedKeyword,
                    File = normalizedFile,
                    Score = 1,
                    LastSeen = DateTime.UtcNow.ToString("o")
                });
            }

            // Promote to hint when score >= 3
            var readyGroups = store.AutoLearned
                .Where(a => a.Score >= 3)
                .GroupBy(a => a.Keyword)
                .ToList();

            foreach (var group in readyGroups)
            {
                var kw = group.Key;
                var files = group.Select(a => a.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                var existingHint = store.Hints.FirstOrDefault(h =>
                    h.Keywords.Contains(kw, StringComparer.OrdinalIgnoreCase));

                if (existingHint != null)
                {
                    foreach (var f in files)
                        if (!existingHint.Files.Contains(f, StringComparer.OrdinalIgnoreCase))
                            existingHint.Files.Add(f);
                }
                else
                {
                    store.Hints.Add(new KeywordHint
                    {
                        Keywords = new() { kw },
                        Files = files
                    });
                }

                store.AutoLearned.RemoveAll(a =>
                    string.Equals(a.Keyword, kw, StringComparison.Ordinal) &&
                    files.Contains(a.File, StringComparer.OrdinalIgnoreCase));
            }

            Save(projectRoot, store);
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
}
