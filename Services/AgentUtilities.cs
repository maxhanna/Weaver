using System.Text;
using System.Text.RegularExpressions;

namespace MaestroBackend.Services;

public static class AgentUtilities
{ 
    private const int CompactThreshold75 = 2100;
    private const int CompactThreshold90 = 2520;
    private static readonly HashSet<string> ExplorationStepTypes =
        new(StringComparer.OrdinalIgnoreCase) { "read", "list", "glob", "grep", "web" };

    public static PipelineType ClassifyTask(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return PipelineType.CommandExecution;
        var lower = prompt.ToLowerInvariant();

        // Quick check: pure ping/health/status with no file changes
        if (!TaskExpectsFileChanges(prompt) &&
            Regex.IsMatch(lower, @"\b(ping|health?|status|check\s+connect|is\s+\S+\s+(up|alive|reachable))\b"))
            return PipelineType.CommandExecution;

        // Command execution: known simple intents (git, package_install, rename, etc.)
        if (TryDetectSimpleIntent(prompt) != null)
            return PipelineType.CommandExecution;

        // Rename/move is always command-execution regardless of phrasing
        if (Regex.IsMatch(lower, @"\b(rename|move)\b.{1,60}\bto\b"))
            return PipelineType.CommandExecution;

        // Directory listing / exploration — needs agentic terminal control, not hallucination
        if (Regex.IsMatch(lower, @"\b(list|what.*in|contents? of|(?:list|show|find|explore|browse)\s+files?\s+in|directory\s+(contents?|listing)|structure\s+of|tree)\b"))
            return PipelineType.CommandExecution;

        // System info / version / environment queries — needs terminal, not code edit
        if (Regex.IsMatch(lower, @"\b(what\s+version|is\s+(\S+\s+)?(installed|running|available)|which\s+(port|process|version|branch)|disk\s+(usage|space|free)|how\s+much\s+(memory|disk|space)|free\s+(memory|disk|space)|running\s+process(es)?|environment\s+variables?|current\s+(directory|path|branch|time|date)|whoami|uptime|list\s+(process|service|container|running))\b"))
            return PipelineType.CommandExecution;

        // Network scanning / discovery
        if (Regex.IsMatch(lower, @"\b(computers?\s+(\S+\s+)?on\s+(the\s+)?network|network\s+(scan|devices?|computers?|discover)|scan\s+(network|devices?|ports?)|find\s+(devices?|computers?|hosts|(\S+\s+){0,2}on\s+(the\s+)?network)|connected\s+devices|what'?s?\s+(\S+\s+){0,3}on\s+((my|the)\s+)?network)\b"))
            return PipelineType.CommandExecution;

        // File operations — copy, duplicate, backup files
        if (Regex.IsMatch(lower, @"\b(copy|duplicate|backup)\s+\S+"))
            return PipelineType.CommandExecution;

        // Package/tool/software installation and management
        if (Regex.IsMatch(lower, @"\b(install|uninstall|remove|update|upgrade|downgrade)\s+(\S+\s+){0,3}(package|tool|module|library|dependency|sdk|runtime|plugin|extension|app|application|software)s?\b"))
            return PipelineType.CommandExecution;

        // Docker / container operations
        if (Regex.IsMatch(lower, @"\b(docker|container|compose|podman|kubernetes|kubectl|helm)\b"))
            return PipelineType.CommandExecution;

        // Process / service / server management
        if (Regex.IsMatch(lower, @"\b(start|stop|restart|reload)\s+(service|process|daemon|server|application)\b"))
            return PipelineType.CommandExecution;

        // Read/show file content (cat/type) — just display, no edit
        if (Regex.IsMatch(lower, @"\b(cat|type)\s+\S+"))
            return PipelineType.CommandExecution;

        // Check/verify/validate something without intending to change it
        if (Regex.IsMatch(lower, @"\b(check\s+if|check\s+whether|verify|validate)\b") && !TaskExpectsFileChanges(prompt))
            return PipelineType.CommandExecution;

        // Create file — needs terminal + possibly web research, not code editing
        if (Regex.IsMatch(lower, @"\bcreate\s+(a\s+)?(new\s+)?file\b"))
            return PipelineType.CommandExecution;

        // Web search or fetch — user wants info retrieved, not code edited
        if (Regex.IsMatch(lower, @"\b(get|find|search|look\s+up|what\s+is|tell\s+me\s+(about|the))\b.{0,60}\b(latest|list|numbers?|info|information|data)\b"))
            return PipelineType.CommandExecution;

        // Email — read emails, inbox, unread, etc., not code editing
        if (Regex.IsMatch(lower, @"\b(email|inbox|unread|read\s+(my\s+)?email|check\s+(my\s+)?email|fetch\s+email)\b"))
            return PipelineType.CommandExecution;

        // Default: needs the full planning pipeline
        return PipelineType.CodeEdit;
    }


    /// <summary>
    /// Detects simple, self-contained intents directly from the prompt string
    /// without any LLM call or file discovery.
    /// Returns a ready-to-execute AgentPlan, or null if the prompt needs full
    /// pipeline analysis.
    /// </summary>
    public static AgentPlan? TryDetectSimpleIntent(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return null;

        var p = prompt.Trim();
        var lower = p.ToLowerInvariant();

        // ── Rename / Move file ────────────────────────────────────────────────
        // Matches: "rename X to Y", "rename X → Y", "move X to Y", etc.
        // Deliberately lenient: captures any token that looks like a filename/path
        var renameMatch = Regex.Match(p,
            @"\b(?:rename|move)\s+['""]?([\w./\\-]+(?:\.[\w.-]+)?)['""]?\s+(?:to|→|-?>)\s+['""]?(\.?[\w./\\-]+(?:\.[\w.-]+)?)['""]?",
            RegexOptions.IgnoreCase);
        if (renameMatch.Success)
        {
            var src = renameMatch.Groups[1].Value.Replace('\\', '/').Trim('/', ' ');
            var dst = renameMatch.Groups[2].Value.Replace('\\', '/').Trim('/', ' ');
            // If dst is a bare name (no dir), inherit the source's directory
            if (!dst.Contains('/') && src.Contains('/'))
            {
                var srcDir = src.Substring(0, src.LastIndexOf('/') + 1);
                dst = srcDir + dst;
            }
            return new AgentPlan
            {
                Thinking = $"Direct file rename detected: {src} → {dst}",
                Summary = $"Rename {src} to {dst}",
                Plan = new List<PlanStep>
                {
                    new() { File = "_rename", Change = $"{src} → {dst}", Priority = 1 }
                }
            };
        }

        // ── Delete file ───────────────────────────────────────────────────────
        var deleteMatch = Regex.Match(p,
            @"\b(?:delete|remove)\s+(?:the\s+)?file\s+['""]?([\w./\\-]+(?:\.[\w.-]+)?)['""]?",
            RegexOptions.IgnoreCase);
        if (deleteMatch.Success)
        {
            var target = deleteMatch.Groups[1].Value.Replace('\\', '/');
            return new AgentPlan
            {
                Thinking = $"Direct file delete detected: {target}",
                Summary = $"Delete file {target}",
                Plan = new List<PlanStep>
                {
                    new() { File = "_delete_file", Change = target, Priority = 1 }
                }
            };
        }

        // ── Git pull ──────────────────────────────────────────────────────────
        if (Regex.IsMatch(lower, @"\b(git\s+pull|pull\s+(all\s+)?change|pull\s+from\s+git|pull\s+latest)\b")
            || (lower.Contains("pull") && lower.Contains("git") && !lower.Contains("request")))
        {
            return new AgentPlan
            {
                Thinking = "Direct git pull intent detected from prompt.",
                Summary = "Pull latest changes from the remote repository and show the result.",
                Plan = new List<PlanStep>
                {
                    new() { File = "_git",  Change = "pull all changes",             Priority = 1 },
                    new() { File = "_show", Change = "show what was pulled from git", Priority = 2 }
                }
            };
        }

        // ── Git commit ────────────────────────────────────────────────────────
        if (Regex.IsMatch(lower, @"\b(git\s+commit|commit\s+all|commit\s+change|commit\s+everything)\b"))
        {
            var msgMatch = Regex.Match(p, "\"([^\"]+)\"");
            var msg = msgMatch.Success ? msgMatch.Groups[1].Value : $"Auto-commit {DateTime.Now:yyyy-MM-dd HH:mm}";
            return new AgentPlan
            {
                Thinking = "Direct git commit intent detected.",
                Summary = $"Commit all staged changes: {msg}",
                Plan = new List<PlanStep>
                {
                    new() { File = "_git", Change = $"commit all changes with message \"{msg}\"", Priority = 1 }
                }
            };
        }

        // ── Git push / sync ───────────────────────────────────────────────────
        if (Regex.IsMatch(lower, @"\b(git\s+(push|sync)|push\s+(to\s+)?(remote|origin|git)|sync\s+(with\s+)?remote)\b"))
        {
            return new AgentPlan
            {
                Thinking = "Direct git sync intent detected.",
                Summary = "Sync with remote (pull then push).",
                Plan = new List<PlanStep>
                {
                    new() { File = "_git", Change = "sync with remote (pull then push)", Priority = 1 }
                }
            };
        }

        // ── Git revert / discard ──────────────────────────────────────────────
        if (Regex.IsMatch(lower, @"\b(git\s+revert|revert\s+all|discard\s+all|undo\s+all\s+change)\b"))
        {
            return new AgentPlan
            {
                Thinking = "Direct git revert intent detected.",
                Summary = "Discard all local working-tree changes.",
                Plan = new List<PlanStep>
                {
                    new() { File = "_git", Change = "revert all changes", Priority = 1 }
                }
            };
        }

        // ── Ping / connectivity ───────────────────────────────────────────────
        if (Regex.IsMatch(lower, @"\b(ping\s+\S|check\s+(connect|reach|host)|test\s+connect|is\s+\S+\s+(up|alive|reachable))\b"))
        {
            return new AgentPlan
            {
                Thinking = "Direct ping/connectivity check detected.",
                Summary = "Test network connectivity.",
                Plan = new List<PlanStep>
                {
                    new() { File = "_ping", Change = p, Priority = 1 }
                }
            };
        }

        // ── Package install ───────────────────────────────────────────────────
        if (Regex.IsMatch(lower, @"\b(install\s+package|npm\s+install|dotnet\s+add\s+package|pip\s+install)\b"))
        {
            return new AgentPlan
            {
                Thinking = "Direct package install intent detected.",
                Summary = "Install the requested package.",
                Plan = new List<PlanStep>
                {
                    new() { File = "_package_install", Change = p, Priority = 1 }
                }
            };
        }

        return null; // needs full pipeline
    } 

    public static bool IsSpecialMarker(string file) =>
        file.Equals("_git", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_rename", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_delete_file", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_show", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_display", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_ping", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_package_install", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_create_file", StringComparison.OrdinalIgnoreCase);

    public static string InferTargetFolder(string fileName, string projectRoot)
    {
        var name = Path.GetFileName(fileName.Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var dir = Path.GetDirectoryName(fileName.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(dir))
            return dir.Replace(Path.DirectorySeparatorChar, '/') + "/";

        if (name.EndsWith("Controller.cs", StringComparison.OrdinalIgnoreCase))
            return "Controllers/";
        if (name.EndsWith("Service.cs", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("Manager.cs", StringComparison.OrdinalIgnoreCase))
            return "Services/";
        if (name.EndsWith("Pipeline.cs", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Pipeline", StringComparison.OrdinalIgnoreCase))
            return "Pipelines/";
        if (name.EndsWith("Router.cs", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Router", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("Routing.cs", StringComparison.OrdinalIgnoreCase))
            return "Routing/";

        var frontendExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".html", ".js", ".css", ".mjs", ".ts", ".tsx", ".jsx", ".vue", ".svelte" };
        if (frontendExts.Contains(Path.GetExtension(name)))
            return "wwwroot/";

        var configFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "appsettings.json", ".gitignore", "appsettings.development.json", "appsettings.production.json"
        };
        if (configFiles.Contains(name))
            return string.Empty;

        if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return "Docs/";

        var modelsDir = Path.Combine(projectRoot, "Models");
        if ((name.EndsWith("Dto.cs", StringComparison.OrdinalIgnoreCase) ||
             name.EndsWith("Model.cs", StringComparison.OrdinalIgnoreCase) ||
             name.EndsWith("Entity.cs", StringComparison.OrdinalIgnoreCase)) &&
            Directory.Exists(modelsDir))
            return "Models/";

        return string.Empty;
    }

    public static bool IsPathUnderRoot(string fullPath, string root)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        fullPath = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)) return true;
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeLineEndings(string s) => s.Replace("\r\n", "\n");

    public static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "\n…(truncated)";

    public static string NormalizeUiStatus(string? status) => status switch
    {
        "written" or "ok" or "created" or "modified" => "done",
        "running" => "running",
        "error" => "error",
        _ => status ?? "pending"
    };

    public static bool TaskExpectsFileChanges(string prompt)
    {
        var lower = prompt.ToLowerInvariant();
        string[] verbs = {
            "add","implement","fix","update","change","create","modify","remove","delete",
            "refactor","edit","write","toggle","enable","disable","insert","set","make",
            "build","install","configure","hook","wire","connect","show","hide","display",
            "save","persist","store","expose","include"
        };
        return verbs.Any(v => lower.Contains(v, StringComparison.Ordinal));
    }

    public static bool BatchWasExplorationOnly(IReadOnlyList<AgentStep> batch) =>
        batch.Count > 0 && batch.All(s => ExplorationStepTypes.Contains(s.Type ?? ""));

    public static List<string> FindSimilarFiles(string missingPath, string projectRoot)
    {
        var name = Path.GetFileName(missingPath.Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = missingPath;
        var found = new List<string>();
        if (!Directory.Exists(projectRoot)) return found;

        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "node_modules", ".git", "bin", "obj", "dist" };

        foreach (var file in Directory.EnumerateFiles(projectRoot, "*.*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
            if (skip.Any(s => rel.Contains("/" + s + "/", StringComparison.OrdinalIgnoreCase))) continue;
            if (rel.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(file).Equals(name, StringComparison.OrdinalIgnoreCase))
                found.Add(rel);
            if (found.Count >= 10) break;
        }
        return found;
    }

    public static string? ExtractTargetPath(string changeDesc, string currentRelPath, string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(changeDesc)) return null;

        var idx = changeDesc.LastIndexOf(" to ", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = changeDesc.LastIndexOf(" → ", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var after = changeDesc[(idx + 4)..].Trim().Trim(' ', '"', '\'');
        if (string.IsNullOrWhiteSpace(after)) return null;

        var dir = Path.GetDirectoryName(currentRelPath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        var target = after.Contains('/') || after.Contains('\\')
            ? after.Replace('\\', '/')
            : (string.IsNullOrEmpty(dir) ? after : dir.Replace('\\', '/') + "/" + after);

        return string.IsNullOrWhiteSpace(target) || target.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ? null : target;
    }

    public static string ReconstructDiscoveryContext(List<object> steps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ONLY use paths that appear below. Do NOT invent paths.");
        sb.AppendLine();
        foreach (var item in steps)
        {
            if (item is not Dictionary<string, object?> r) continue;
            var type = r.TryGetValue("type", out var t) ? t?.ToString() : string.Empty;

            if (type is "list" or "grep" or "glob" or "read")
            {
                if (r.TryGetValue("output", out var output) && output != null)
                {
                    sb.AppendLine($"### {type} {r.GetValueOrDefault("path") ?? r.GetValueOrDefault("description")}");
                    sb.AppendLine(output.ToString() ?? string.Empty);
                    sb.AppendLine();
                }
            }
            else if (type == "edit")
            {
                var status = r.TryGetValue("status", out var st) ? st?.ToString() : string.Empty;
                if (status is "modified" or "created")
                {
                    var path = r.TryGetValue("path", out var p) ? p?.ToString() : string.Empty;
                    var newContent = r.TryGetValue("newContent", out var nc) ? nc?.ToString() : string.Empty;
                    if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(newContent))
                    {
                        sb.AppendLine($"### edited {path} (current content)");
                        sb.AppendLine(newContent);
                        sb.AppendLine();
                    }
                }
            }
        }
        return sb.ToString();
    }

    public static string BuildDiscoveryTextFromSteps(List<object> steps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ONLY use paths that appear below. Do NOT invent paths.");
        sb.AppendLine();
        foreach (var item in steps)
        {
            if (item is not Dictionary<string, object?> r) continue;
            if (!r.TryGetValue("output", out var output) || output == null || string.IsNullOrEmpty(output.ToString())) continue;
            sb.AppendLine($"### {r.GetValueOrDefault("type")} {r.GetValueOrDefault("path") ?? r.GetValueOrDefault("description")}");
            sb.AppendLine(output.ToString());
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string BuildDiffPreview(string? oldStr, string? newStr)
    {
        if (string.IsNullOrEmpty(oldStr) && string.IsNullOrEmpty(newStr)) return string.Empty;
        var oldLines = (oldStr ?? string.Empty).Split('\n');
        var newLines = (newStr ?? string.Empty).Split('\n');
        var sb = new StringBuilder();
        for (int i = 0, j = 0; i < oldLines.Length || j < newLines.Length;)
        {
            if (i < oldLines.Length && j < newLines.Length && oldLines[i] == newLines[j])
            {
                sb.Append("  ").AppendLine(oldLines[i]);
                i++; j++;
            }
            else
            {
                if (i < oldLines.Length) { sb.Append("- ").AppendLine(oldLines[i]); i++; }
                if (j < newLines.Length) { sb.Append("+ ").AppendLine(newLines[j]); j++; }
            }
        }
        return sb.ToString().TrimEnd();
    }

    public static string QuoteJsonKeys(string json)
    {
        return Regex.Replace(json,
            @"(?<=[\{\,])\s*([a-zA-Z_$][a-zA-Z0-9_$]*)\s*(?=:)" ,
            m => $"\"{m.Groups[1].Value}\"");
    }

    public static string? RepairJsonString(string json)
    {
        if (string.IsNullOrEmpty(json)) return json;
        var sb = new StringBuilder(json.Length);
        var inString = false; var depth = 0; var valueStartDepth = 0; var changed = false;

        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (!inString)
            {
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                if (c == '"') { inString = true; valueStartDepth = depth; }
                sb.Append(c);
                continue;
            }

            if (c == '\\') { sb.Append(c); i++; if (i < json.Length) sb.Append(json[i]); continue; }

            if (c == '"')
            {
                var nextNonWs = -1;
                for (var j = i + 1; j < json.Length; j++)
                    if (!char.IsWhiteSpace(json[j])) { nextNonWs = j; break; }

                if (nextNonWs >= 0 && depth == valueStartDepth &&
                    (json[nextNonWs] == ',' || json[nextNonWs] == '}' || json[nextNonWs] == ']' || json[nextNonWs] == ':'))
                {
                    sb.Append(c);
                    inString = false;
                }
                else { sb.Append("\\\""); changed = true; }
                continue;
            }

            if (c == '\n') { sb.Append("\\n"); changed = true; continue; }
            if (c == '\r') { sb.Append("\\r"); changed = true; continue; }
            if (c == '\t') { sb.Append("\\t"); changed = true; continue; }
            sb.Append(c);
        }
        return changed ? sb.ToString() : null;
    }

    public static bool IsRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (Path.IsPathRooted(path)) return false;

        var specialMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "_git", "_ping", "_show", "_display", "_create_file", "_package_install"
        };
        return !specialMarkers.Contains(path);
    }

    public static List<string> ExtractJsonBlocks(string text)
    {
        var blocks = new List<string>();
        var depth = 0; var start = -1; var inString = false;

        for (var i = 0; i < text.Length; i++)
        {
            if (inString)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == '"') inString = false;
                continue;
            }
            if (text[i] == '"') { inString = true; continue; }
            if (text[i] == '{') { if (depth == 0) start = i; depth++; }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0 && start >= 0) { blocks.Add(text.Substring(start, i - start + 1)); start = -1; }
            }
        }
        return blocks;
    }

    public static string? RepairJsonStringValues(string json)
    {
        var sb = new StringBuilder(json.Length + 64);
        var inString = false;
        var changed = false;

        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (!inString)
            {
                if (c == '"') inString = true;
                sb.Append(c);
                continue;
            }

            if (c == '\\') { sb.Append(c); i++; if (i < json.Length) sb.Append(json[i]); continue; }
            if (c == '"') { sb.Append(c); inString = false; continue; }

            switch (c)
            {
                case '\n': sb.Append("\\n"); changed = true; break;
                case '\r': sb.Append("\\r"); changed = true; break;
                case '\t': sb.Append("\\t"); changed = true; break;
                default: sb.Append(c); break;
            }
        }
        return changed ? sb.ToString() : null;
    }
    // FIX 2: Also count 'rename' steps as successful work so Phase 4 does not
    // re-enter the plan+edit loop after a rename completes.  Previously only
    // 'edit' was checked, so every rename caused two extra spurious LLM calls
    // that would pick an unrelated file (e.g. app.js) and try to patch it.
    public static bool HasSuccessfulEdits(IEnumerable<object> steps) =>
        steps.OfType<Dictionary<string, object?>>().Any(s =>
            s.TryGetValue("type", out var t) &&
            (string.Equals(t?.ToString(), "edit", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(t?.ToString(), "rename", StringComparison.OrdinalIgnoreCase)) &&
            s.TryGetValue("status", out var st) && st?.ToString() == "done");
    /// <summary>
    /// Strips stopwords and generic action verbs from a prompt, returning
    /// the domain-meaningful terms that are actually useful for file matching.
    /// Replaces the old ExtractSearchKeywords which included words like "Make", "more",
    /// "sensitive" that produce grep noise across every file in the codebase.
    /// </summary>
    public static List<string> ExtractMeaningfulKeywords(string lower)
    {
        var stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Articles, prepositions, conjunctions
            "the","a","an","and","or","but","in","on","at","to","for","of","with","from",
            "into","onto","upon","after","before","about","above","below","between",
            // Pronouns
            "this","that","it","its","their","our","my","your","his","her","we","they","i",
            // Auxiliary verbs
            "is","are","was","were","be","been","being","have","has","had",
            "do","does","did","will","would","should","could","may","might","shall",
            // Generic action verbs (too broad — match everything)
            "make","making","makes","made",
            "fix","fixing","fixes","fixed",
            "add","adding","adds","added",
            "change","changing","changes","changed",
            "update","updating","updates","updated",
            "edit","editing","edits","edited",
            "modify","modifying","modifies","modified",
            "create","creating","creates","created",
            "delete","deleting","deletes","deleted",
            "remove","removing","removes","removed",
            "set","get","put","use","using","used",
            "show","hide","display",
            // Vague adjectives / adverbs
            "more","less","some","any","all","no","not","also","very","just",
            "nice","nicely","good","better","best","new","old","right","left",
            "please","sure","now","then","when","where","how","why","what","which","who",
            "out","up","down","so","if","else","really","quite","bit","little","lot",
            // Common filler
            "need","want","should","must","can","let","help","try","look","see"
        };

        return Regex.Matches(lower, @"\b[a-z]{3,}\b")
            .Select(m => m.Value)
            .Where(w => !stopwords.Contains(w))
            .Distinct()
            .Take(10)
            .ToList();
    }

    /// <summary>
    /// Scores project files using task-type heuristics — no LLM required.
    /// Detects intent (styling, HTML, JS, backend, config) from prompt keywords,
    /// then assigns extension + filename scores. Returns ordered candidate list.
    /// </summary>
    public static List<string> ApplyTaskTypeHeuristics(string prompt, List<string> allFiles)
    {
        var lower = prompt.ToLowerInvariant();

        // Detect what kind of task this is (multiple can be true)
        var isStyleTask = Regex.IsMatch(lower, @"\b(style|css|color|theme|layout|spacing|font|design|ui|ux|look|appear|brand|visual|margin|padding|border|shadow|panel|card)\b");
        var isHtmlTask = Regex.IsMatch(lower, @"\b(html|template|page|view|markup|modal|popup|section|div)\b");
        var isJsTask = Regex.IsMatch(lower, @"\b(javascript|script|function|event|click|toggle|show|hide|angular|react|vue|component|state|behavior)\b");
        var isBackendTask = Regex.IsMatch(lower, @"\b(api|endpoint|controller|service|database|model|route|logic|backend|server|c#|csharp|dotnet)\b");
        var isConfigTask = Regex.IsMatch(lower, @"\b(config|setting|option|appsettings|environment|json)\b");

        var meaningfulKeywords = ExtractMeaningfulKeywords(lower);

        var scored = allFiles.Select(f =>
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            var nameLow = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
            var pathLow = f.ToLowerInvariant();
            var score = 0;

            // ── Extension scoring by task type ──────────────────────────────
            if (isStyleTask)
            {
                if (ext is ".css" or ".scss" or ".sass" or ".less") score += 120;
                else if (ext is ".html" or ".htm") score += 60;
                else if (ext is ".js" or ".ts") score += 20;
            }
            if (isHtmlTask)
            {
                if (ext is ".html" or ".htm") score += 120;
                else if (ext is ".css" or ".scss") score += 50;
                else if (ext is ".js" or ".ts") score += 30;
            }
            if (isJsTask)
            {
                if (ext is ".js" or ".ts" or ".jsx" or ".tsx") score += 120;
                else if (ext is ".html" or ".htm") score += 40;
            }
            if (isBackendTask)
            {
                if (ext == ".cs") score += 120;
                else if (ext == ".json") score += 30;
            }
            if (isConfigTask)
            {
                if (ext is ".json" or ".yaml" or ".yml") score += 120;
            }

            // ── Boost if the filename contains a meaningful prompt keyword ──
            foreach (var kw in meaningfulKeywords)
                if (nameLow.Contains(kw))
                    score += 50;

            // ── Frontend folder boost for frontend tasks ───────────────────
            if ((isStyleTask || isHtmlTask || isJsTask) && pathLow.StartsWith("wwwroot/"))
                score += 25;

            // ── Penalize known-large / known-noisy files ───────────────────
            // These are almost never the target of a specific edit request
            if (nameLow.Contains("agentcontroller")) score -= 200;
            if (nameLow == "filehints") score -= 200;
            if (pathLow.EndsWith(".min.js")) score -= 300;
            if (pathLow.EndsWith(".min.css")) score -= 300;

            // ── Penalize non-text / generated artifacts ────────────────────
            if (ext is ".dll" or ".exe" or ".pdb" or ".nupkg" or ".lock" or ".sum")
                score -= 1000;

            return (file: f, score);
        })
        .Where(x => x.score > 0)
        .OrderByDescending(x => x.score)
        .Take(50)
        .Select(x => x.file)
        .ToList();

        // Fallback: if no file scored positively (e.g. novel task type), include
        // common entry-point files so the LLM always has something to work with
        if (scored.Count == 0)
        {
            scored = allFiles
                .Where(f =>
                {
                    var name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return name is "index" or "app" or "main" or "program" or "startup"
                                or "styles" or "global" or "layout"
                        && ext is ".html" or ".js" or ".ts" or ".css" or ".cs";
                })
                .Take(10)
                .ToList();
        }

        return scored;
    }

    public static string UnescapeJsonString(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\') { sb.Append(s[i]); continue; }
            i++;
            if (i >= s.Length) { sb.Append('\\'); break; }
            switch (s[i])
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u':
                    if (i + 4 < s.Length && int.TryParse(s.Substring(i + 1, 4),
                        System.Globalization.NumberStyles.HexNumber, null, out var code))
                    { sb.Append((char)code); i += 4; }
                    else sb.Append('u');
                    break;
                default: sb.Append(s[i]); break;
            }
        }
        return sb.ToString();
    }
    public static IEnumerable<string> GeneratePlanJsonCandidates(string json)
    {
        // Candidate 1 — as-is
        yield return json;

        // Candidate 2 — quote unquoted keys  {thinking: → {"thinking":
        var quoted = Regex.Replace(json,
            @"(?<=[{,])\s*([a-zA-Z_$][\w$]*)\s*(?=:)",
            m => m.Value.Replace(m.Groups[1].Value, $"\"{m.Groups[1].Value}\""));
        if (quoted != json) yield return quoted;

        // Candidate 3 — escape bare newlines inside string values
        var repaired = AgentUtilities.RepairJsonStringValues(json);
        if (repaired != null && repaired != json) yield return repaired;

        // Candidate 4 — both repairs combined
        if (repaired != null && repaired != json)
        {
            var both = Regex.Replace(repaired,
                @"(?<=[{,])\s*([a-zA-Z_$][\w$]*)\s*(?=:)",
                m => m.Value.Replace(m.Groups[1].Value, $"\"{m.Groups[1].Value}\""));
            if (both != repaired) yield return both;
        }
    }

    public static int EstimateTokens(string text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Length / 4;

    public static string CompactDiscoveryContext(string discoveryContext, HashSet<string?> keepFull)
    {
        if (string.IsNullOrEmpty(discoveryContext) || EstimateTokens(discoveryContext) < CompactThreshold75)
            return discoveryContext;

        var sb = new StringBuilder(discoveryContext.Length / 2);
        var blocks = discoveryContext.Split("### ", StringSplitOptions.None);
        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block)) continue;
            var header = block;
            var contentStart = block.IndexOf('\n');
            string? body = null;
            if (contentStart > 0)
            {
                header = block[..contentStart].Trim();
                body = block[contentStart..].Trim();
            }
            // Extract file path from header: "read path/to/file" or "path/to/file"
            var filePath = header.StartsWith("read ", StringComparison.Ordinal)
                ? header[5..].Trim()
                : header.Trim();
            if (filePath == null || keepFull.Contains(filePath))
            {
                sb.Append("### ").Append(block);
                continue;
            }
            var lines = (body ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            sb.Append("### ").AppendLine(header);
            sb.Append("  [compacted — ").Append(lines.Length).AppendLine(" lines]");
            if (lines.Length > 0)
            {
                var first = lines[0].Trim();
                if (first.Length > 0) sb.Append("  first: ").AppendLine(first.Length > 200 ? first[..200] + "…" : first);
                if (lines.Length > 1)
                {
                    var last = lines[^1].Trim();
                    if (last.Length > 0 && last != first)
                        sb.Append("  last:  ").AppendLine(last.Length > 200 ? last[..200] + "…" : last);
                }
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static void CompactConversation(StringBuilder conversation, int keepLastTurns = 3)
    {
        if (conversation == null || conversation.Length == 0) return;
        var text = conversation.ToString();
        if (EstimateTokens(text) < CompactThreshold90) return;

        var turns = text.Split("Command [", StringSplitOptions.None);
        if (turns.Length <= keepLastTurns + 1) return;

        var sb = new StringBuilder();
        sb.AppendLine("## Prior context (compacted)");
        sb.AppendLine("Earlier commands and their results are summarized below.");
        sb.AppendLine("The last " + keepLastTurns + " turns are preserved in full after this.");
        sb.AppendLine();

        var lines = new List<string>();
        for (var i = 1; i < turns.Length - keepLastTurns; i++)
        {
            var cmdText = turns[i];
            var nl = cmdText.IndexOf('\n');
            lines.Add("  " + (nl > 0 ? cmdText[..nl].Trim() : cmdText.Trim()));
        }
        if (lines.Count > 0)
        {
            sb.AppendLine("Executed:");
            foreach (var l in lines) sb.AppendLine(l);
        }
        sb.AppendLine();

        // Keep initial system prompt + task (turns[0]) + last N turns in full
        sb.Append(turns[0]);
        for (var i = Math.Max(1, turns.Length - keepLastTurns); i < turns.Length; i++)
            sb.Append("Command [").Append(turns[i]);

        conversation.Clear();
        conversation.Append(sb.ToString());
    }
}
