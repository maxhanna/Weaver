using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    public const int MaxArchBuildTools = 20;
    public const int MaxArchSchemas = 20;
    public const int MaxArchEndpoints = 40;
    public const int MaxArchMethods = 60;

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

    /// <summary>
    /// Full context dump — used at UnifiedPipeline load time for the discovery/planning header.
    /// Includes architecture overview, all do/don't bullets, all patterns, and recent failures.
    /// </summary>
    public static string FormatForContext(ProjectEditKnowledge? k)
    {
        if (k == null) return "";
        var sb = new StringBuilder();
        sb.AppendLine("### PRIOR EDIT KNOWLEDGE FOR THIS PROJECT ###");
        sb.AppendLine($"Project: {k.ProjectName}  |  Last updated: {k.LastUpdated}");
        sb.AppendLine("These are accumulated lessons from prior edits in this project.");
        sb.AppendLine("USE them — don't repeat mistakes that are already recorded here.");
        sb.AppendLine();

        // Architecture overview (build tools + schema summaries + API surface)
        AppendArchitectureContext(sb, k.Architecture, fileExt: null, taskDescription: null);

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
                sb.AppendLine($"  [{f.Ts}] {f.File} — {f.Outcome}: {f.Reason}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Filtered context — used per edit step. Only surfaces knowledge relevant to the
    /// current file extension and task keywords. Keeps the edit prompt lean.
    /// </summary>
    public static string FormatForContext(ProjectEditKnowledge? k, string? fileExt, string? taskDescription)
    {
        if (k == null) return "";

        var sb = new StringBuilder();
        sb.AppendLine("### EDIT KNOWLEDGE (relevant to this file/task) ###");

        // Architecture: filter to relevant APIs, methods, and schemas
        AppendArchitectureContext(sb, k.Architecture, fileExt, taskDescription);

        // Do/Don't bullets: always include — they're global lessons (short, high-value)
        if (k.Do != null && k.Do.Count > 0)
        {
            sb.AppendLine("DO:");
            foreach (var b in k.Do) sb.AppendLine($"  + {b}");
            sb.AppendLine();
        }
        if (k.Dont != null && k.Dont.Count > 0)
        {
            sb.AppendLine("DON'T:");
            foreach (var b in k.Dont) sb.AppendLine($"  - {b}");
            sb.AppendLine();
        }

        // Patterns: only for the current file extension (+ any global .* patterns)
        if (k.Patterns != null && k.Patterns.Count > 0)
        {
            var relevantPatterns = k.Patterns
                .Where(kvp => (kvp.Value?.Count ?? 0) > 0 &&
                              (string.IsNullOrEmpty(fileExt) ||
                               string.Equals(kvp.Key, fileExt, StringComparison.OrdinalIgnoreCase) ||
                               kvp.Key == ".*"))
                .ToList();
            if (relevantPatterns.Count > 0)
            {
                sb.AppendLine($"FILE-TYPE PATTERNS ({fileExt ?? "all"}):");
                foreach (var (ext, bullets) in relevantPatterns)
                {
                    foreach (var b in bullets!) sb.AppendLine($"    + {b}");
                }
                sb.AppendLine();
            }
        }

        // Recent failures: prioritise same file, then same extension, then task-keyword matches
        if (k.RecentFailures != null && k.RecentFailures.Count > 0)
        {
            var taskWords = ExtractKeywords(taskDescription);
            var scored = k.RecentFailures
                .Select(f =>
                {
                    var fExt = Path.GetExtension(f.File ?? "").ToLowerInvariant();
                    var score = 0;
                    // Same extension scores higher than cross-extension noise
                    if (!string.IsNullOrEmpty(fileExt) &&
                        string.Equals(fExt, fileExt, StringComparison.OrdinalIgnoreCase)) score += 10;
                    // Keyword overlap with task description
                    if (taskWords.Count > 0)
                    {
                        var reasonWords = ExtractKeywords(f.Reason + " " + f.File);
                        score += taskWords.Intersect(reasonWords, StringComparer.OrdinalIgnoreCase).Count() * 2;
                    }
                    return (f, score);
                })
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .Take(5)
                .Select(x => x.f)
                .ToList();

            if (scored.Count > 0)
            {
                sb.AppendLine("RECENT FAILURES (avoid repeating):");
                foreach (var f in scored)
                    sb.AppendLine($"  [{f.Ts}] {f.File} — {f.Outcome}: {f.Reason}");
                sb.AppendLine();
            }
        }

        var result = sb.ToString();
        // If only the header was written (no content) return empty so callers can skip it
        if (result.Trim() == "### EDIT KNOWLEDGE (relevant to this file/task) ###") return "";
        return result;
    }

    // ── Architecture context block ────────────────────────────────────────────

    private static void AppendArchitectureContext(
        StringBuilder sb, ProjectArchitecture? arch,
        string? fileExt, string? taskDescription)
    {
        if (arch == null) return;

        var taskWords = ExtractKeywords(taskDescription);
        var hasAny = false;
        var archSb = new StringBuilder();

        if (arch.BuildTools != null && arch.BuildTools.Count > 0)
        {
            archSb.AppendLine("BUILD / FRAMEWORK:");
            foreach (var t in arch.BuildTools) archSb.AppendLine($"  {t}");
            hasAny = true;
        }

        // Database schemas: always include (they rarely exceed budget and are high-value)
        if (arch.DatabaseSchemas != null && arch.DatabaseSchemas.Count > 0)
        {
            archSb.AppendLine("DATABASE SCHEMAS (table / column signatures):");
            foreach (var s in arch.DatabaseSchemas) archSb.AppendLine($"  {s}");
            hasAny = true;
        }

        // API endpoints: filter to task-relevant ones if a task is provided
        if (arch.ApiEndpoints != null && arch.ApiEndpoints.Count > 0)
        {
            List<string> endpoints;
            if (taskWords.Count > 0)
            {
                endpoints = arch.ApiEndpoints
                    .Where(e => taskWords.Any(w =>
                        e.Contains(w, StringComparison.OrdinalIgnoreCase)))
                    .Take(15)
                    .ToList();
                // Always add at least a handful if none matched
                if (endpoints.Count == 0)
                    endpoints = arch.ApiEndpoints.Take(8).ToList();
            }
            else
            {
                endpoints = arch.ApiEndpoints.Take(15).ToList();
            }
            if (endpoints.Count > 0)
            {
                archSb.AppendLine("API ENDPOINTS (existing — do not duplicate):");
                foreach (var e in endpoints) archSb.AppendLine($"  {e}");
                hasAny = true;
            }
        }

        // Key methods: filter to relevant file extension and task keywords
        if (arch.KeyMethods != null && arch.KeyMethods.Count > 0)
        {
            List<string> methods;
            if (taskWords.Count > 0 || !string.IsNullOrEmpty(fileExt))
            {
                methods = arch.KeyMethods
                    .Where(m => (string.IsNullOrEmpty(fileExt) ||
                                 m.Contains(fileExt, StringComparison.OrdinalIgnoreCase) ||
                                 IsExtensionRelated(m, fileExt)) &&
                                (taskWords.Count == 0 ||
                                 taskWords.Any(w => m.Contains(w, StringComparison.OrdinalIgnoreCase))))
                    .Take(20)
                    .ToList();
            }
            else
            {
                methods = arch.KeyMethods.Take(20).ToList();
            }
            if (methods.Count > 0)
            {
                archSb.AppendLine("KNOWN METHOD SIGNATURES (informational — preserve these contracts where referenced; this list is NOT exhaustive):");
                foreach (var m in methods) archSb.AppendLine($"  {m}");
                hasAny = true;
            }
        }

        if (hasAny)
        {
            sb.AppendLine("## PROJECT ARCHITECTURE");
            sb.Append(archSb);
            sb.AppendLine();
        }
    }

    private static bool IsExtensionRelated(string entry, string fileExt) =>
        fileExt switch
        {
            ".cs" => entry.Contains(".cs") || entry.Contains("Controller") ||
                     entry.Contains("Service") || entry.Contains("Task") ||
                     Regex.IsMatch(entry, @"\b(public|private|protected|internal)\b"),
            ".ts" or ".tsx" => entry.Contains(".ts") || entry.Contains("component") ||
                               entry.Contains("service") || Regex.IsMatch(entry, @"[a-z]+\(.*\)"),
            ".js" or ".jsx" => entry.Contains(".js"),
            ".sql" => entry.Contains("TABLE") || entry.Contains("SELECT") || entry.Contains("INSERT"),
            _ => false
        };

    private static HashSet<string> ExtractKeywords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Split on non-word chars, keep tokens >= 4 chars, strip common stopwords
        var stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "this", "that", "with", "from", "file", "code", "line", "edit",
            "step", "make", "have", "been", "will", "should", "would", "could",
            "must", "into", "also", "them", "they", "then", "when", "what",
            "your", "their", "there"
        };
        return Regex.Matches(text, @"\b[A-Za-z][A-Za-z0-9]{3,}\b")
            .Select(m => m.Value)
            .Where(w => !stopwords.Contains(w))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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

    // ── Architecture extraction ──────────────────────────────────────────────

    /// <summary>
    /// Called after a successful edit. Extracts architecture facts from the written content
    /// (API endpoints from controllers, DB schemas from SQL files, build info from project
    /// files, key method signatures from services) and persists them into the knowledge store.
    /// Fire-and-forget safe — never throws.
    /// </summary>
    public async Task UpdateArchitectureAsync(
        string projectRoot, string relPath, string newContent, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newContent) || string.IsNullOrWhiteSpace(relPath)) return;

        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        var nameLow = Path.GetFileName(relPath).ToLowerInvariant();

        // Decide what to extract based on file type / name
        var buildTools   = new List<string>();
        var schemas      = new List<string>();
        var endpoints    = new List<string>();
        var methods      = new List<string>();

        try
        {
            if (ext == ".csproj" || ext == ".sln" || nameLow == "package.json")
            {
                ExtractBuildTools(newContent, ext, buildTools);
            }
            else if (ext == ".sql" || nameLow.EndsWith(".sql"))
            {
                ExtractSqlSchemas(newContent, schemas);
            }
            else if (ext == ".cs" && IsControllerFile(relPath))
            {
                ExtractCsApiEndpoints(newContent, relPath, endpoints);
                ExtractCsKeyMethods(newContent, relPath, methods);
            }
            else if (ext == ".cs")
            {
                ExtractCsKeyMethods(newContent, relPath, methods);
            }
            else if (ext is ".ts" or ".tsx")
            {
                ExtractTsKeyMethods(newContent, relPath, methods);
            }

            if (buildTools.Count == 0 && schemas.Count == 0 &&
                endpoints.Count == 0 && methods.Count == 0)
                return;

            var path = GetEditKnowledgeFilePath(projectRoot);
            var sem = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            await sem.WaitAsync(cts.Token);
            try
            {
                ProjectEditKnowledge k;
                if (System.IO.File.Exists(path))
                {
                    try
                    {
                        var raw = await System.IO.File.ReadAllTextAsync(path, Encoding.UTF8, cts.Token);
                        k = (string.IsNullOrWhiteSpace(raw) ? null :
                             JsonSerializer.Deserialize<ProjectEditKnowledge>(raw,
                                 new JsonSerializerOptions { PropertyNameCaseInsensitive = true }))
                            ?? new ProjectEditKnowledge();
                    }
                    catch { k = new ProjectEditKnowledge(); }
                }
                else { k = new ProjectEditKnowledge(); }

                k.Architecture ??= new ProjectArchitecture();

                MergeArchList(k.Architecture.BuildTools   ??= new(), buildTools,  MaxArchBuildTools);
                MergeArchList(k.Architecture.DatabaseSchemas ??= new(), schemas,  MaxArchSchemas);
                MergeArchList(k.Architecture.ApiEndpoints ??= new(), endpoints,   MaxArchEndpoints);
                MergeArchList(k.Architecture.KeyMethods   ??= new(), methods,     MaxArchMethods);

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
                await System.IO.File.WriteAllTextAsync(tmp, json, Encoding.UTF8, cts.Token);
                if (System.IO.File.Exists(path))
                    System.IO.File.Replace(tmp, path, null);
                else
                    System.IO.File.Move(tmp, path);
            }
            finally { sem.Release(); }
        }
        catch (Exception ex)
        {
            _logger?.Invoke("warn", $"UpdateArchitecture skipped for {relPath}: {ex.Message}");
        }
    }

    private static void MergeArchList(List<string> dest, List<string> incoming, int cap)
    {
        foreach (var item in incoming)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            // Replace existing entry for the same signature prefix (same method/table name)
            var key = item.Split('(')[0].Split(' ').Last().Trim();
            var existing = dest.FindIndex(d =>
                d.Split('(')[0].Split(' ').Last().Trim()
                    .Equals(key, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
                dest[existing] = item; // update to latest
            else
            {
                dest.Add(item);
                while (dest.Count > cap) dest.RemoveAt(0);
            }
        }
    }

    private static bool IsControllerFile(string relPath) =>
        relPath.Contains("Controller", StringComparison.OrdinalIgnoreCase) ||
        relPath.EndsWith("Controller.cs", StringComparison.OrdinalIgnoreCase);

    // ── Build tools extraction (csproj / package.json) ──────────────────────

    private static void ExtractBuildTools(string content, string ext, List<string> result)
    {
        if (ext == ".csproj")
        {
            // Target framework
            var tfm = Regex.Match(content, @"<TargetFramework(?:s)?>\s*([^<]+)\s*</TargetFramework");
            if (tfm.Success) result.Add($"TargetFramework: {tfm.Groups[1].Value.Trim()}");

            // SDK / project type
            var sdk = Regex.Match(content, @"<Project\s+Sdk=""([^""]+)""");
            if (sdk.Success) result.Add($"SDK: {sdk.Groups[1].Value.Trim()}");

            // Key PackageReferences (not test packages)
            foreach (Match m in Regex.Matches(content,
                @"<PackageReference\s+Include=""([^""]+)""\s+Version=""([^""]+)"""))
            {
                var pkg = m.Groups[1].Value;
                if (pkg.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
                    pkg.Contains("Mock", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add($"Package: {pkg} {m.Groups[2].Value}");
                if (result.Count >= MaxArchBuildTools) break;
            }
        }
        else if (ext == ".json") // package.json
        {
            // Main framework entries from dependencies / devDependencies
            foreach (Match m in Regex.Matches(content,
                @"""(angular|react|vue|next|nuxt|svelte|express|fastify|nestjs|@angular/core)[^""]*""\s*:\s*""([^""]+)""",
                RegexOptions.IgnoreCase))
            {
                result.Add($"npm: {m.Groups[1].Value}@{m.Groups[2].Value}");
                if (result.Count >= 10) break;
            }
        }
    }

    // ── SQL schema extraction ────────────────────────────────────────────────

    private static void ExtractSqlSchemas(string content, List<string> result)
    {
        // CREATE TABLE statements → table(col type, ...)
        foreach (Match m in Regex.Matches(content,
            @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?[`""\[]?(\w+)[`""\]]?\s*\(([^;]{0,800})\)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var tableName = m.Groups[1].Value;
            var body = m.Groups[2].Value;
            // Extract column names + types (first two tokens per line)
            var cols = Regex.Matches(body, @"^\s*[`""]?(\w+)[`""]?\s+(\w+)", RegexOptions.Multiline)
                .Cast<Match>()
                .Where(c => !c.Groups[1].Value.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase) &&
                            !c.Groups[1].Value.Equals("UNIQUE", StringComparison.OrdinalIgnoreCase) &&
                            !c.Groups[1].Value.Equals("INDEX", StringComparison.OrdinalIgnoreCase) &&
                            !c.Groups[1].Value.Equals("KEY", StringComparison.OrdinalIgnoreCase) &&
                            !c.Groups[1].Value.Equals("CONSTRAINT", StringComparison.OrdinalIgnoreCase))
                .Select(c => $"{c.Groups[1].Value} {c.Groups[2].Value}")
                .Take(12);
            result.Add($"TABLE {tableName}({string.Join(", ", cols)})");
            if (result.Count >= MaxArchSchemas) break;
        }

        // ALTER TABLE ADD COLUMN — update existing schema entry or add incremental note
        foreach (Match m in Regex.Matches(content,
            @"ALTER\s+TABLE\s+[`""\[]?(\w+)[`""\]]?\s+ADD\s+(?:COLUMN\s+)?[`""\[]?(\w+)[`""\]]?\s+(\w+)",
            RegexOptions.IgnoreCase))
        {
            result.Add($"ALTER {m.Groups[1].Value} ADD {m.Groups[2].Value} {m.Groups[3].Value}");
            if (result.Count >= MaxArchSchemas) break;
        }
    }

    // ── C# endpoint extraction ───────────────────────────────────────────────

    private static readonly Regex CsRouteAttrRegex = new(
        @"\[(?:Http(?:Get|Post|Put|Delete|Patch)|Route)\s*\(\s*""([^""]*)""\s*\)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CsMethodSigRegex = new(
        @"^\s*(?:public|internal)\s+(?:async\s+)?(?:Task<[^>]+>|IActionResult|ActionResult[^<\n]*|[A-Za-z_][A-Za-z0-9_<>?\[\]]*)\s+([A-Z][A-Za-z0-9_]+)\s*\(([^)]{0,200})\)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static void ExtractCsApiEndpoints(string content, string relPath, List<string> result)
    {
        // Detect controller-level route prefix
        var controllerRoute = "";
        var ctrlRouteMatch = Regex.Match(content,
            @"\[Route\s*\(\s*""([^""]*)""\s*\)\]", RegexOptions.IgnoreCase);
        if (ctrlRouteMatch.Success) controllerRoute = ctrlRouteMatch.Groups[1].Value.TrimEnd('/');

        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var routeMatch = CsRouteAttrRegex.Match(lines[i]);
            if (!routeMatch.Success) continue;

            var verb = Regex.Match(lines[i],
                @"\[(?<v>HttpGet|HttpPost|HttpPut|HttpDelete|HttpPatch)",
                RegexOptions.IgnoreCase);
            var httpVerb = verb.Success
                ? verb.Groups["v"].Value.Replace("Http", "").ToUpper()
                : "GET";

            var routeSuffix = routeMatch.Groups[1].Value.Trim('/');
            var fullRoute = string.IsNullOrEmpty(routeSuffix)
                ? "/" + controllerRoute
                : "/" + controllerRoute.TrimEnd('/') + "/" + routeSuffix;

            // Find the method signature on the next 3 lines
            for (var j = i + 1; j < Math.Min(i + 4, lines.Length); j++)
            {
                var sigMatch = CsMethodSigRegex.Match(lines[j]);
                if (!sigMatch.Success) continue;

                var methodName = sigMatch.Groups[1].Value;
                var paramsList = sigMatch.Groups[2].Value.Trim();
                // Trim param annotations ([FromBody], [FromQuery], etc.)
                paramsList = Regex.Replace(paramsList, @"\[[^\]]+\]\s*", "");
                result.Add($"{httpVerb} {fullRoute} → {methodName}({paramsList})");
                break;
            }

            if (result.Count >= MaxArchEndpoints) break;
        }
    }

    // ── C# key method signatures ─────────────────────────────────────────────

    private static readonly Regex CsPublicMethodRegex = new(
        @"^\s*(?<mods>(?:(?:public|internal|protected|static|async|virtual|override|abstract)\s+)+)"
        + @"(?<ret>[A-Za-z_][A-Za-z0-9_<>\[\],\s\?\|]*?)\s+"
        + @"(?<name>[A-Z][A-Za-z0-9_]+)\s*\((?<params>[^)]{0,200})\)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static void ExtractCsKeyMethods(string content, string relPath, List<string> result)
    {
        var fileName = Path.GetFileNameWithoutExtension(relPath);
        foreach (Match m in CsPublicMethodRegex.Matches(content))
        {
            var mods = m.Groups["mods"].Value;
            if (mods.Contains("private") || mods.Contains("protected")) continue;
            if (mods.Contains("override")) continue; // base class is canonical

            var name = m.Groups["name"].Value;
            if (name.Length < 4) continue;
            // Skip constructors
            if (name == fileName || name == Path.GetFileName(relPath).Replace(".cs", "")) continue;

            var ret = m.Groups["ret"].Value.Trim();
            var parms = Regex.Replace(m.Groups["params"].Value.Trim(), @"\[[^\]]+\]\s*", "");
            result.Add($"{ret} {fileName}.{name}({parms})");
            if (result.Count >= MaxArchMethods) break;
        }
    }

    // ── TypeScript key method signatures ─────────────────────────────────────

    private static readonly Regex TsPublicMethodRegex = new(
        @"^\s*(?:(?:public|async|static)\s+)*(?<name>[a-z][A-Za-z0-9_]{3,})\s*\((?<params>[^)]{0,200})\)\s*(?::\s*(?<ret>[A-Za-z][A-Za-z0-9_<>\[\]\|\s,?]*?))?(?:\s*\{|=>)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static void ExtractTsKeyMethods(string content, string relPath, List<string> result)
    {
        var fileName = Path.GetFileNameWithoutExtension(relPath);
        // Detect class name
        var className = "";
        var classMatch = Regex.Match(content,
            @"(?:export\s+)?(?:default\s+)?(?:abstract\s+)?class\s+([A-Z][A-Za-z0-9_]*)");
        if (classMatch.Success) className = classMatch.Groups[1].Value;

        foreach (Match m in TsPublicMethodRegex.Matches(content))
        {
            var name = m.Groups["name"].Value;
            if (name == "constructor") continue;

            var parms = m.Groups["params"].Value.Trim();
            var ret = m.Groups["ret"].Success ? $": {m.Groups["ret"].Value.Trim()}" : "";
            var owner = string.IsNullOrEmpty(className) ? fileName : className;
            result.Add($"{owner}.{name}({parms}){ret}");
            if (result.Count >= MaxArchMethods) break;
        }
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
    /// <summary>Accumulated structural facts about the project (build tools, DB schema, API surface, key method signatures).</summary>
    public ProjectArchitecture? Architecture { get; set; }
}

public class ProjectArchitecture
{
    /// <summary>Framework / SDK / package summary (e.g. "TargetFramework: net10.0", "Package: Newtonsoft.Json 13.0.3").</summary>
    public List<string> BuildTools { get; set; } = new();
    /// <summary>Database table/column signatures extracted from SQL files.</summary>
    public List<string> DatabaseSchemas { get; set; } = new();
    /// <summary>HTTP verb + route + handler signature for controller actions.</summary>
    public List<string> ApiEndpoints { get; set; } = new();
    /// <summary>Public method signatures from services and controllers.</summary>
    public List<string> KeyMethods { get; set; } = new();
}

public class ProjectEditFailure
{
    public string Ts { get; set; } = "";
    public string File { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Outcome { get; set; } = "";
}
