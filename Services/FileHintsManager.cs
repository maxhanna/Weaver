using System.Text.Json;

namespace MaestroBackend.Services;


public class FileHintsManager
{
    private readonly string _basePath;
    private readonly object _lock = new();

    public FileHintsManager(string basePath)
    {
        _basePath = basePath;
    }

    private string HintsFilePath => Path.Combine(_basePath, "filehints.json");

    private GlobalHintsStore LoadAll()
    {
        try
        {
            if (System.IO.File.Exists(HintsFilePath))
            {
                var json = System.IO.File.ReadAllText(HintsFilePath);
                return JsonSerializer.Deserialize<GlobalHintsStore>(json) ?? new GlobalHintsStore();
            }
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
            proj = new ProjectHints {  Hints = new List<KeywordHint> { } };
            store.Projects[projectRoot] = proj;
        }
        return proj;
    } 
    public List<string> GetFilesForPrompt(string prompt, string projectRoot)
    {
        var lower = prompt.ToLowerInvariant();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            var store = LoadAll();
            var proj = EnsureProject(projectRoot, store);

            foreach (var hint in proj.Hints)
            {
                if (hint.Keywords.Any(k => lower.Contains(k, StringComparison.Ordinal)))
                    foreach (var f in hint.Files)
                        files.Add(f);
            }
        }

        return files.ToList();
    }

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
                    files.Contains(a.File, StringComparer.OrdinalIgnoreCase));
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
}
