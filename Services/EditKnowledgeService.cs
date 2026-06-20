using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Weaver.Services;

public class EditKnowledgeService
{
    public delegate Task<(string? text, string? error)> LlmCallDelegate(string systemPrompt, string userMessage, CancellationToken ct);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly LlmCallDelegate? _llmCaller;
    private readonly Action<string, string>? _logger;
    private readonly string _weaverDataDir;

    public const int MaxDoEntries = 30;
    public const int MaxDontEntries = 30;
    public const int MaxPatternsPerExt = 30;
    public const int MaxRecentFailures = 10;

    public EditKnowledgeService(string weaverDataDir, LlmCallDelegate? llmCaller = null, Action<string, string>? logger = null)
    {
        _weaverDataDir = weaverDataDir;
        _llmCaller = llmCaller;
        _logger = logger;
    }

    public string GetEditKnowledgeFilePath(string projectRoot)
    {
        var projectName = Path.GetFileName(projectRoot.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var safeName = new StringBuilder();
        foreach (var ch in projectName)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
                safeName.Append(ch);
            else
                safeName.Append('_');
        }
        if (safeName.Length == 0) safeName.Append("default");
        return Path.Combine(_weaverDataDir, $".project_{safeName}_edit_knowledge.json");
    }

    public async Task<ProjectEditKnowledge?> LoadAsync(string projectRoot, CancellationToken ct = default)
    {
        try
        {
            var path = GetEditKnowledgeFilePath(projectRoot);
            if (!System.IO.File.Exists(path)) return null;
            var raw = await System.IO.File.ReadAllTextAsync(path, Encoding.UTF8, ct);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return JsonSerializer.Deserialize<ProjectEditKnowledge>(raw, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            _logger?.Invoke("warn", $"Failed to load edit knowledge: {ex.Message}");
            return null;
        }
    }

    public async Task EnsureExistsAsync(string projectRoot, CancellationToken ct = default)
    {
        try
        {
            var path = GetEditKnowledgeFilePath(projectRoot);
            if (System.IO.File.Exists(path)) return;

            var projectName = Path.GetFileName(projectRoot.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var empty = new ProjectEditKnowledge
            {
                ProjectName = projectName,
                LastUpdated = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Version = 1,
                Do = new List<string>(),
                Dont = new List<string>(),
                Patterns = new Dictionary<string, List<string>>(StringComparer.Ordinal),
                RecentFailures = new List<ProjectEditFailure>()
            };

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            var json = JsonSerializer.Serialize(empty, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = null
            });
            await System.IO.File.WriteAllTextAsync(tmp, json, Encoding.UTF8, ct);
            if (System.IO.File.Exists(path))
                System.IO.File.Replace(tmp, path, null);
            else
                System.IO.File.Move(tmp, path);

            _logger?.Invoke("info", $"Created edit knowledge file for project: {projectName}");
        }
        catch (Exception ex)
        {
            _logger?.Invoke("warn", $"Failed to create edit knowledge file: {ex.Message}");
        }
    }

    public static string FormatForContext(ProjectEditKnowledge? k)
    {
        if (k == null) return "";
        var sb = new StringBuilder();
        sb.AppendLine("### PRIOR EDIT KNOWLEDGE FOR THIS PROJECT ###");
        sb.AppendLine($"Project: {k.ProjectName}  |  Last updated: {k.LastUpdated}");
        sb.AppendLine("These are accumulated lessons from prior edits in this project.");
        sb.AppendLine("USE them — don't repeat mistakes that are already recorded here.");
        sb.AppendLine();

        if (k.Do != null && k.Do.Count > 0)
        {
            sb.AppendLine("DO (patterns that worked):");
            foreach (var b in k.Do) sb.AppendLine($"  + {b}");
            sb.AppendLine();
        }
        if (k.Dont != null && k.Dont.Count > 0)
        {
            sb.AppendLine("DON'T (patterns that failed or broke things):");
            foreach (var b in k.Dont) sb.AppendLine($"  - {b}");
            sb.AppendLine();
        }
        if (k.Patterns != null && k.Patterns.Count > 0)
        {
            sb.AppendLine("FILE-TYPE PATTERNS:");
            foreach (var (ext, bullets) in k.Patterns)
            {
                if (bullets == null || bullets.Count == 0) continue;
                sb.AppendLine($"  {ext}:");
                foreach (var b in bullets) sb.AppendLine($"    + {b}");
            }
            sb.AppendLine();
        }
        if (k.RecentFailures != null && k.RecentFailures.Count > 0)
        {
            sb.AppendLine("RECENT FAILURES (avoid repeating):");
            foreach (var f in k.RecentFailures)
            {
                sb.AppendLine($"  [{f.Ts}] {f.File} — {f.Outcome}: {f.Reason}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public async Task RecordOutcomeAsync(
        string projectRoot,
        string relPath,
        string stepChange,
        string originalPrompt,
        string? oldStr,
        string? newStr,
        string outcome,
        string reason,
        CancellationToken ct = default)
    {
        using var bgCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bgCts.CancelAfter(TimeSpan.FromMinutes(2));
        var bgCt = bgCts.Token;

        try
        {
            var path = GetEditKnowledgeFilePath(projectRoot);
            var projectName = Path.GetFileName(projectRoot.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            var sem = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync(bgCt);
            try
            {
                ProjectEditKnowledge k;
                if (System.IO.File.Exists(path))
                {
                    try
                    {
                        var raw = await System.IO.File.ReadAllTextAsync(path, Encoding.UTF8, bgCt);
                        k = (string.IsNullOrWhiteSpace(raw)
                                ? null
                                : JsonSerializer.Deserialize<ProjectEditKnowledge>(raw, new JsonSerializerOptions
                                {
                                    PropertyNameCaseInsensitive = true
                                })) ?? new ProjectEditKnowledge();
                    }
                    catch { k = new ProjectEditKnowledge(); }
                }
                else
                {
                    k = new ProjectEditKnowledge();
                }
                if (string.IsNullOrEmpty(k.ProjectName)) k.ProjectName = projectName;
                k.Do ??= new List<string>();
                k.Dont ??= new List<string>();
                k.Patterns ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
                k.RecentFailures ??= new List<ProjectEditFailure>();

                if (outcome != "success")
                {
                    k.RecentFailures.Add(new ProjectEditFailure
                    {
                        Ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        File = relPath ?? "",
                        Reason = TruncateForLlm(reason, 200),
                        Outcome = outcome
                    });
                    while (k.RecentFailures.Count > MaxRecentFailures)
                        k.RecentFailures.RemoveAt(0);
                }

                var fileExt = Path.GetExtension(relPath ?? "").ToLowerInvariant();
                var (newDoBullets, newDontBullets, newPatternBullets, summary)
                    = await SummarizeOutcomeAsync(
                        originalPrompt, stepChange, relPath ?? "", fileExt,
                        oldStr ?? "", newStr ?? "", outcome, reason,
                        k, bgCt);

                if (!string.IsNullOrWhiteSpace(summary))
                {
                    _logger?.Invoke("info", $"Edit knowledge [{outcome}]: {summary}");
                }

                foreach (var b in newDoBullets)
                {
                    if (string.IsNullOrWhiteSpace(b)) continue;
                    if (k.Do.Contains(b, StringComparer.OrdinalIgnoreCase)) continue;
                    k.Do.Add(b);
                    while (k.Do.Count > MaxDoEntries) k.Do.RemoveAt(0);
                }
                foreach (var b in newDontBullets)
                {
                    if (string.IsNullOrWhiteSpace(b)) continue;
                    if (k.Dont.Contains(b, StringComparer.OrdinalIgnoreCase)) continue;
                    k.Dont.Add(b);
                    while (k.Dont.Count > MaxDontEntries) k.Dont.RemoveAt(0);
                }
                if (!string.IsNullOrEmpty(fileExt) && newPatternBullets.Count > 0)
                {
                    if (!k.Patterns.TryGetValue(fileExt, out var list))
                    {
                        list = new List<string>();
                        k.Patterns[fileExt] = list;
                    }
                    foreach (var b in newPatternBullets)
                    {
                        if (string.IsNullOrWhiteSpace(b)) continue;
                        if (list.Contains(b, StringComparer.OrdinalIgnoreCase)) continue;
                        list.Add(b);
                        while (list.Count > MaxPatternsPerExt) list.RemoveAt(0);
                    }
                }

                k.LastUpdated = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var tmp = path + ".tmp";
                var json = JsonSerializer.Serialize(k, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = null
                });
                await System.IO.File.WriteAllTextAsync(tmp, json, Encoding.UTF8, bgCt);
                if (System.IO.File.Exists(path))
                    System.IO.File.Replace(tmp, path, null);
                else
                    System.IO.File.Move(tmp, path);
            }
            finally
            {
                sem.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.Invoke("warn", $"Failed to record edit knowledge: {ex.Message}");
        }
    }

    private async Task<(List<string> doBullets, List<string> dontBullets, List<string> patternBullets, string summary)>
        SummarizeOutcomeAsync(
            string originalPrompt, string stepChange, string relPath, string fileExt,
            string oldStr, string newStr, string outcome, string reason,
            ProjectEditKnowledge existing, CancellationToken ct)
    {
        var existingDo = existing.Do != null && existing.Do.Count > 0
            ? string.Join("\n", existing.Do.Take(15).Select(b => $"  + {b}"))
            : "  (none)";
        var existingDont = existing.Dont != null && existing.Dont.Count > 0
            ? string.Join("\n", existing.Dont.Take(15).Select(b => $"  - {b}"))
            : "  (none)";

        var sysPrompt =
            "You are maintaining a small, deduplicated knowledge file for a software project. " +
            "Your job: given the outcome of a single code edit, decide if it teaches a NEW lesson " +
            "worth recording. If yes, output 1-2 short point-form bullets for each relevant section. " +
            "If the lesson is already covered by the existing knowledge, return empty lists — " +
            "do NOT duplicate or rephrase existing bullets.\n\n" +
            "STRICT OUTPUT FORMAT — JSON only, no prose, no markdown fences:\n" +
            "{\n" +
            "  \"summary\": \"one short sentence describing the outcome\",\n" +
            "  \"do\": [\"short bullet\", ...],\n" +
            "  \"dont\": [\"short bullet\", ...],\n" +
            "  \"patterns\": [\"short bullet\", ...]\n" +
            "}\n\n" +
            "RULES:\n" +
            " * Each bullet MUST be <= 15 words, point-form, no preamble.\n" +
            " * Each bullet MUST be a GENERAL lesson, not a one-off fact.\n" +
            " * If outcome==success, prefer filling 'do' and 'patterns'. Leave 'dont' empty.\n" +
            " * If outcome!=success, prefer filling 'dont' and 'patterns'. Leave 'do' empty.\n" +
            " * If the lesson is already covered by EXISTING KNOWLEDGE, return EMPTY lists.\n" +
            " * Return at most 2 bullets per list.";

        var userMsg =
            $"### TASK PROMPT ###\n{TruncateForLlm(originalPrompt, 400)}\n\n" +
            $"### STEP ###\n{TruncateForLlm(stepChange, 300)}\n\n" +
            $"### FILE ###\n{relPath} (ext: {fileExt})\n\n" +
            $"### OUTCOME ###\n{outcome}\n" +
            (string.IsNullOrWhiteSpace(reason) ? "" : $"REASON: {TruncateForLlm(reason, 300)}\n") +
            $"\n### OLD CODE ###\n```\n{TruncateForLlm(oldStr, 800)}\n```\n\n" +
            $"### NEW CODE ###\n```\n{TruncateForLlm(newStr, 800)}\n```\n\n" +
            $"### EXISTING KNOWLEDGE (do NOT duplicate these) ###\n" +
            $"DO:\n{existingDo}\n\nDON'T:\n{existingDont}\n\n" +
            "Decide: what new bullets (if any) should be added? Output JSON only.";

        try
        {
            if (_llmCaller == null)
                return (new List<string>(), new List<string>(), new List<string>(),
                    "(LLM summarize skipped: no LLM caller configured)");

            var (raw, error) = await _llmCaller(sysPrompt, userMsg, ct);

            if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(raw))
                return (new List<string>(), new List<string>(), new List<string>(),
                    $"(LLM summarize skipped: {error ?? "empty"})");

            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                cleaned = cleaned.TrimStart('`');
                var nl = cleaned.IndexOf('\n');
                if (nl >= 0) cleaned = cleaned[(nl + 1)..];
                if (cleaned.EndsWith("```")) cleaned = cleaned[..^3];
            }
            var fb = cleaned.IndexOf('{');
            var lb = cleaned.LastIndexOf('}');
            if (fb < 0 || lb <= fb)
                return (new List<string>(), new List<string>(), new List<string>(),
                    "(LLM summarize: no JSON)");
            cleaned = cleaned[fb..(lb + 1)];

            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            var summary = root.TryGetProperty("summary", out var sEl)
                ? sEl.GetString()?.Trim() ?? ""
                : "";

            var doBullets = new List<string>();
            var dontBullets = new List<string>();
            var patternBullets = new List<string>();

            if (root.TryGetProperty("do", out var doEl) && doEl.ValueKind == JsonValueKind.Array)
                foreach (var b in doEl.EnumerateArray())
                {
                    var s = b.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(s) && s.Length <= 200) doBullets.Add(s);
                }
            if (root.TryGetProperty("dont", out var dontEl) && dontEl.ValueKind == JsonValueKind.Array)
                foreach (var b in dontEl.EnumerateArray())
                {
                    var s = b.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(s) && s.Length <= 200) dontBullets.Add(s);
                }
            if (root.TryGetProperty("patterns", out var pEl) && pEl.ValueKind == JsonValueKind.Array)
                foreach (var b in pEl.EnumerateArray())
                {
                    var s = b.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(s) && s.Length <= 200) patternBullets.Add(s);
                }

            return (doBullets, dontBullets, patternBullets, summary);
        }
        catch (Exception ex)
        {
            return (new List<string>(), new List<string>(), new List<string>(),
                $"(LLM summarize failed: {ex.Message})");
        }
    }

    private static string TruncateForLlm(string s, int maxChars)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxChars) return s ?? "";
        var headLen = (int)(maxChars * 0.6);
        var tailLen = maxChars - headLen - 20;
        if (tailLen < 0) tailLen = 0;
        return s.Substring(0, headLen) +
               $"\n... [truncated {s.Length - headLen - tailLen} chars] ...\n" +
               (tailLen > 0 ? s.Substring(s.Length - tailLen, tailLen) : "");
    }
}

public class ProjectEditKnowledge
{
    public string ProjectName { get; set; } = "";
    public string LastUpdated { get; set; } = "";
    public int Version { get; set; } = 1;
    public List<string> Do { get; set; } = new();
    public List<string> Dont { get; set; } = new();
    public Dictionary<string, List<string>> Patterns { get; set; } = new(StringComparer.Ordinal);
    public List<ProjectEditFailure> RecentFailures { get; set; } = new();
}

public class ProjectEditFailure
{
    public string Ts { get; set; } = "";
    public string File { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Outcome { get; set; } = "";
}
