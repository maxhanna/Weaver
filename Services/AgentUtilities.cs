using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Weaver.Services;

public static class AgentUtilities
{ 
    private const int CompactThreshold90 = 2520; 

    public static (PipelineType Type, double CommandScore, double EditScore) ClassifyTask(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return (PipelineType.CommandExecution, 100, 0);
        var lower = prompt.ToLowerInvariant();

        // ── Score-based routing ──────────────────────────────────────────────
        // Both pipelines get weighted scores. The highest-confidence pipeline
        // wins. This is more robust than the old first-match-wins chain for
        // ambiguous prompts like "augment the email settings in the UI panel".
        double cmdScore = 0;
        double editScore = 0;

        // ── CommandExecution signals (high-weight, unambiguous) ──────────────
        if (TryDetectSimpleIntent(prompt) != null) cmdScore += 100;

        if (Regex.IsMatch(lower, @"\b(ping|health?|status|check\s+connect|is\s+\S+\s+(up|alive|reachable))\b"))
            cmdScore += 80;

        if (Regex.IsMatch(lower, @"\b(create\s+(a\s+)?(new\s+)?file)\b"))
            cmdScore += 60;

        if (Regex.IsMatch(lower, @"\b(put|place|write|save|download)\s+(a\s+)?(file|data|content|result)\s+(on|to|at|in)\s+(the\s+)?(desktop|downloads|documents|home)\b"))
            cmdScore += 80;

        if (Regex.IsMatch(lower, @"\b(what.*in|contents?\s+of|find\s+files?\s+in|directory\s+contents|structure\s+of|tree|logs?|journal|stdout|stderr|console|output|terminal|logs|process|service)\b"))
            cmdScore += 65;
        // "list" alone is ambiguous (data list vs file listing) — use lower weight
        if (Regex.IsMatch(lower, @"\b(list)\b") &&
            !Regex.IsMatch(lower, @"\b(list\s+of\s+\w+)\b")) // "list of plants" is data, not filesystem
            cmdScore += 20;

        if (Regex.IsMatch(lower, @"\b(inbox|unread|read\s+(my\s+)?email|check\s+(my\s+)?email|fetch\s+email|read\s+mail|check\s+mail)\b"))
            cmdScore += 85;

        if (Regex.IsMatch(lower, @"\b(docker|container|compose|podman|kubernetes|kubectl|helm)\b"))
            cmdScore += 60;

        if (Regex.IsMatch(lower, @"\b(start|stop|restart|reload)\s+(service|process|daemon|server|application)\b"))
            cmdScore += 60;

        if (Regex.IsMatch(lower, @"\b(install|uninstall|remove|update|upgrade|downgrade)\s+(package|tool|module|library|dependency|sdk|runtime|plugin|extension)\b"))
            cmdScore += 60;

        if (Regex.IsMatch(lower, @"\b(rename|move)\b.{1,60}(\.\w+|[\\/]).{0,60}\bto\b"))
            cmdScore += 65;

        if (Regex.IsMatch(lower, @"\b(copy|duplicate|backup)\s+\S+"))
            cmdScore += 60;

        // Writing files outside the project (desktop, external paths) = terminal operation
        if (Regex.IsMatch(lower, @"\b(desktop|downloads?|documents?)\b"))
            cmdScore += 55;

        if (Regex.IsMatch(lower, @"\b(what\s+version|is\s+installed|which\s+(port|process|version|branch)|disk\s+(usage|space)|how\s+much\s+(memory|disk)|running\s+process|environment\s+variable|current\s+(directory|path|time|date)|whoami|uptime)\b"))
            cmdScore += 55;

        if (Regex.IsMatch(lower, @"\b(computers?\s+on\s+network|network\s+(scan|devices)|scan\s+(network|ports)|find\s+(devices|computers|hosts)|connected\s+devices)\b"))
            cmdScore += 55;

        if (Regex.IsMatch(lower, @"\b(get|find|search|look\s+up|what\s+is|tell\s+me\s+(about|the)|fetch)\b.{0,60}\b(latest|list|numbers?|info|information|data)\b"))
            cmdScore += 50;

        // ── CodeEdit signals ─────────────────────────────────────────────────
        // Strong edit verbs — word-boundary to avoid "add" inside "address"
        if (Regex.IsMatch(lower, @"\b(augment|implement|refactor|rewrite|redesign)\b"))
            editScore += 65;

        if (Regex.IsMatch(lower, @"\b(fix|update|change|modify|edit|patch|tweak|adjust)\b"))
            editScore += 55;

        if (Regex.IsMatch(lower, @"\b(add|remove|delete|insert)\b"))
            editScore += 45;

        if (Regex.IsMatch(lower, @"\b(toggle|enable|disable|configure|wire|connect|hook|expose)\b"))
            editScore += 40;

        // UI/component keywords — user wants to modify a UI element
        if (Regex.IsMatch(lower, @"\b(div|button|input|form|dropdown|checkbox|radio|modal|popup|panel|section|tab|sidebar|navbar|header|footer)\b"))
            editScore += 35;

        if (Regex.IsMatch(lower, @"\b(component|template|view|page|layout|widget|element|calendar)\b"))
            editScore += 30;

        // Programmatic assignment — "set X to Y" is a code-edit pattern
        // Suppress when prompt is about reading/processing data from a file (false positive:
        // "set of skills, and write it down next to the pokemon's name" should not trigger)
        var isDataProcessing = Regex.IsMatch(lower, @"\b(row|column|csv|tsv|json|each\s+(row|line)|file.*data|read.*file|fetch.*(from|data|api|endpoint))\b");
        if (!isDataProcessing && Regex.IsMatch(lower, @"\bset\b.{0,40}\bto\b"))
            editScore += 40;

        // Style/design keywords
        if (Regex.IsMatch(lower, @"\b(style|css|class|theme|color|font|margin|padding|border|shadow|layout|spacing)\b"))
            editScore += 30;

        // File path mentioned — likely editing a specific file
        // Suppress for data-processing contexts (reading CSV/TSV/JSON, not editing code)
        if (!isDataProcessing && Regex.IsMatch(lower, @"\b[\w./\\-]+\.\w{2,4}\b"))
            editScore += 20;

        // Display/show/preview — likely a UI code change, not a terminal command
        if (Regex.IsMatch(lower, @"\b(show|display|render|preview|view)\b"))
            editScore += 15;

        // Visual media — images, photos, thumbnails in UI context
        if (Regex.IsMatch(lower, @"\b(picture|image|photo|thumbnail)\b"))
            editScore += 12;

        // ── Hybrid signal: "email" in editing context vs reading context ─────
        bool emailForReading = Regex.IsMatch(lower,
            @"\b(read|check|fetch|inbox|unread|send|compose)\b.{0,40}\b(email|mail)\b");
        bool emailForConfig = Regex.IsMatch(lower, @"\bemail\b") && !emailForReading;

        if (emailForReading) cmdScore += 80;
        if (emailForConfig) editScore += 25; // configuring email settings = edit

        // ── Penalties for conflicting signals ────────────────────────────────
        // If prompt is clearly about editing (strong edit verbs + UI keywords),
        // suppress command signals from generic keywords
        if (editScore >= 80) cmdScore -= 30;

        // If prompt is purely a read/query with no edit intent, suppress edit
        if (cmdScore >= 50 && editScore == 0) editScore -= 40;

        // ── Decision ─────────────────────────────────────────────────────────
        if (editScore > cmdScore) return (PipelineType.CodeEdit, cmdScore, editScore);
        if (cmdScore > editScore) return (PipelineType.CommandExecution, cmdScore, editScore);

        return (PipelineType.CodeEdit, cmdScore, editScore);
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
        // Handles quoted paths and unquoted paths.
        var renameMatch = Regex.Match(p,
            @"\b(?:rename|move)\s+(?:""([^""]+)""|'([^']+)'|([^\s]+))\s+(?:to|→|-?>)\s+(?:""([^""]+)""|'([^']+)'|([^\s]+))",
            RegexOptions.IgnoreCase);

        if (renameMatch.Success)
        {
            var src = (renameMatch.Groups[1].Value + renameMatch.Groups[2].Value + renameMatch.Groups[3].Value).Replace('\\', '/').Trim('/', ' ', '"', '\'');
            var dst = (renameMatch.Groups[4].Value + renameMatch.Groups[5].Value + renameMatch.Groups[6].Value).Replace('\\', '/').Trim('/', ' ', '"', '\'');
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
        if (Regex.IsMatch(lower, @"\b(ping\s+\S|check\s+(connect|reach|host)|test\s+connect|is\s+(it|this|that|the\s+(server|host|site|website|service|database|connection|network))\s+(up|alive|reachable|down|online|offline))\b"))
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
    /// <summary>
    /// Deterministically ensures that plans creating new C# controllers 
    /// also update the Angular proxy.conf.js file with the new API route.
    /// </summary>
    public static AgentPlan? EnforceProxyConfigForControllers(AgentPlan? plan, string projectRoot)
    {
        if (plan?.Plan == null || plan.Plan.Count == 0) return plan;

        // 1. Check if proxy.conf.js exists in the project
        var proxyFiles = Directory.GetFiles(projectRoot, "proxy.conf.js", SearchOption.AllDirectories);
        if (proxyFiles.Length == 0) return plan;

        var proxyRelPath = Path.GetRelativePath(projectRoot, proxyFiles[0]).Replace('\\', '/');

        // 2. Check if proxy.conf.js is already being updated in the plan
        bool hasProxyUpdate = plan.Plan.Any(p =>
            p.File != null &&
            p.File.EndsWith("proxy.conf.js", StringComparison.OrdinalIgnoreCase));

        if (hasProxyUpdate) return plan;

        // 3. Check if the plan creates a new Controller.cs file
        var controllerStep = plan.Plan.FirstOrDefault(p =>
            p.File != null &&
            p.File.EndsWith("Controller.cs", StringComparison.OrdinalIgnoreCase) &&
            AgentUtilities.IsRelativePath(p.File));

        if (controllerStep == null) return plan;

        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, controllerStep.File.Replace('/', Path.DirectorySeparatorChar)));

        // If the controller file already exists, it's an edit, not a creation. Skip.
        if (System.IO.File.Exists(fullPath)) return plan;

        // 4. Extract controller name to generate the route (e.g., HealthTrackerController -> /healthtracker)
        var controllerName = Path.GetFileNameWithoutExtension(controllerStep.File);
        var baseName = controllerName.EndsWith("Controller", StringComparison.OrdinalIgnoreCase)
            ? controllerName.Substring(0, controllerName.Length - "Controller".Length)
            : controllerName;

        var route = "/" + baseName.ToLowerInvariant();
        
        try
        {
            var proxyContent = System.IO.File.ReadAllText(proxyFiles[0]);
            // Check for "/route" or "/route,"
            if (proxyContent.Contains($"\"{route}\"", StringComparison.OrdinalIgnoreCase) ||
                proxyContent.Contains($"\"{route},", StringComparison.OrdinalIgnoreCase))
            {
                // Route already exists, don't inject the step!
                return plan;
            }
        }
        catch { /* if read fails, proceed with injection */ }

        // 5. Inject the proxy.conf.js update step at the end of the plan
        plan.Plan.Add(new PlanStep
        {
            File = proxyRelPath,
            Change = $"Add the new route '{route}' to the context array in proxy.conf.js so the Angular dev server proxies API calls to the new backend controller. Do NOT duplicate existing routes.",
            Priority = 1
        });

        return plan;
    }

    public static string DistillExplorationContext(
        string explorationContext,
        string targetRelPath,
        string changeDesc,
        string? targetSymbol,
        int maxChars = 7_000)
    {
        if (string.IsNullOrWhiteSpace(explorationContext)) return "";

        var keywords = AgentUtilities.ExtractMeaningfulKeywords(changeDesc.ToLowerInvariant())
            .Where(k => k.Length >= 4)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(targetSymbol))
            keywords.Add(targetSymbol);

        var normalizedTarget = targetRelPath.Replace('\\', '/');

        // Split on section headers ("### ") — each file read in the loop produces one section
        var sections = Regex.Split(explorationContext.Trim(), @"(?=^### )", RegexOptions.Multiline);
        var result = new StringBuilder();

        foreach (var rawSection in sections)
        {
            if (string.IsNullOrWhiteSpace(rawSection)) continue;

            // Skip the target file — already shown verbatim in the main prompt
            var firstLine = rawSection.Split('\n')[0];
            if (firstLine.Contains("TARGET FILE:", StringComparison.OrdinalIgnoreCase)) continue;
            var sectionPath = Regex.Match(firstLine, @"###\s+([^\s(]+)").Groups[1].Value
                .Replace('\\', '/').Trim();
            if (string.Equals(sectionPath, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                continue;

            var distilled = DistillFileSection(rawSection, keywords);
            if (string.IsNullOrWhiteSpace(distilled)) continue;

            var budget = maxChars - result.Length;
            if (budget < 100) { result.AppendLine("... [context budget exhausted]"); break; }
            if (distilled.Length > budget)
                distilled = distilled[..budget] + "\n    // ... [truncated]";
            result.AppendLine(distilled);
        }

        return result.ToString();
    }

    private static string DistillFileSection(string section, HashSet<string> keywords, int maxCharsPerSection = 1_800)
    {
        var lines = section.Split('\n');
        var headerLines = new List<string>();
        var codeLines = new List<string>();
        var openingFence = "";
        var inFence = false;
        var pastFirstFence = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```"))
            {
                if (!pastFirstFence)
                {
                    pastFirstFence = true;
                    inFence = true;
                    openingFence = line;
                }
                else if (inFence)
                {
                    inFence = false;
                    break; // stop after the first code block
                }
                continue;
            }
            if (inFence)
                codeLines.Add(line);
            else
                headerLines.Add(line);
        }

        if (codeLines.Count == 0)
            return string.Join("\n", headerLines); // prose-only section, keep as-is

        // ── Determine which code lines to include ────────────────────────────
        var included = new SortedSet<int>();

        // Always include the first 20 lines — imports and type declarations live here
        for (var i = 0; i < Math.Min(20, codeLines.Count); i++)
            included.Add(i);

        // Include ±3 lines around any keyword match throughout the rest of the file
        for (var i = 20; i < codeLines.Count; i++)
        {
            if (keywords.Count == 0) break;
            if (keywords.Any(kw => codeLines[i].Contains(kw, StringComparison.OrdinalIgnoreCase)))
            {
                for (var w = Math.Max(0, i - 3); w <= Math.Min(codeLines.Count - 1, i + 3); w++)
                    included.Add(w);
            }
            // Matches: `async methodName(...)`, `public methodName(...): Type`, `methodName(...) {`
            if (Regex.IsMatch(codeLines[i], @"^\s*((public|private|protected|static|async|export|function|get|set)\s+)*\w+\s*(<[^>]+>)?\s*\([^)]*\)\s*(:\s*[^{;]+)?\s*[{;]", RegexOptions.IgnoreCase))
            {
                // Keep the signature and the next 5 lines (body/return type)
                for (var w = Math.Max(0, i - 1); w <= Math.Min(codeLines.Count - 1, i + 5); w++)
                    included.Add(w);
            }
        }

        // ── Build output, inserting gap markers between non-contiguous ranges ─
        var result = new List<string>(headerLines) { openingFence };
        var prevIdx = -2;

        foreach (var idx in included)
        {
            if (prevIdx >= 0 && idx > prevIdx + 1)
                result.Add("    // ...");
            result.Add(codeLines[idx]);
            prevIdx = idx;
        }

        if (prevIdx < codeLines.Count - 1)
            result.Add("    // ...");

        result.Add("```");

        var output = string.Join("\n", result);
        return output.Length > maxCharsPerSection
            ? output[..maxCharsPerSection] + "\n    // ... [truncated]"
            : output;
    }
    /// <summary>
    /// Fixes indentation for multiline method arguments inside (), [], and {}
    /// when the LLM or ReindentByBraceDepth flattens them to the base indent.
    /// </summary>
    public static string FixMultilineParenIndentation(string code)
    {
        var lines = code.Split('\n');
        if (lines.Length <= 2) return code;

        var indentUnit = "    ";
        var changed = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) continue;

            var lastChar = trimmed[^1];
            if (lastChar == '(' || lastChar == '[' || lastChar == '{')
            {
                var openChar = lastChar;
                var closeChar = openChar == '(' ? ')' : openChar == '[' ? ']' : '}';

                var depth = 0;
                var closedOnSameLine = false;
                foreach (var c in trimmed)
                {
                    if (c == openChar) depth++;
                    else if (c == closeChar) depth--;
                    if (depth == 0 && c == closeChar) { closedOnSameLine = true; break; }
                }

                if (!closedOnSameLine)
                {
                    var currentIndent = line.Substring(0, line.Length - trimmed.Length);
                    var nextIndent = currentIndent + indentUnit;

                    var blockDepth = 1;
                    for (var j = i + 1; j < lines.Length; j++)
                    {
                        var innerLine = lines[j];
                        var innerTrimmed = innerLine.TrimStart();

                        if (string.IsNullOrWhiteSpace(innerLine))
                        {
                            continue;
                        }

                        var innerCurrentIndent = innerLine.Substring(0, innerLine.Length - innerTrimmed.Length);
                        var startsWithAnyClose = innerTrimmed.StartsWith("}") || innerTrimmed.StartsWith("]") || innerTrimmed.StartsWith(")");
                        var isClosingLine = innerTrimmed.StartsWith(closeChar);

                        foreach (var c in innerTrimmed)
                        {
                            if (c == openChar) blockDepth++;
                            else if (c == closeChar) blockDepth--;
                        }

                        if (isClosingLine && blockDepth <= 0)
                        {
                            if (innerCurrentIndent.Length != currentIndent.Length)
                            {
                                lines[j] = currentIndent + innerTrimmed;
                                changed = true;
                            }
                            break;
                        }
                        else if (!startsWithAnyClose)
                        {
                            if (innerCurrentIndent.Length < nextIndent.Length)
                            {
                                lines[j] = nextIndent + innerTrimmed;
                                changed = true;
                            }
                        }
                    }
                }
            }
        }

        return changed ? string.Join("\n", lines) : code;
    }
    public static List<string> ExtractSignatureTokens(string sigLine)
    {
        if (string.IsNullOrWhiteSpace(sigLine))
            return new List<string>();

        var parenIdx = sigLine.IndexOf('(');
        var head = parenIdx >= 0 ? sigLine.Substring(0, parenIdx) : sigLine;

        string paramsRegion = "";
        string returnRegion = "";
        if (parenIdx >= 0)
        {
            var depth = 0;
            var closeIdx = -1;
            for (var i = parenIdx; i < sigLine.Length; i++)
            {
                if (sigLine[i] == '(') depth++;
                else if (sigLine[i] == ')')
                {
                    depth--;
                    if (depth == 0) { closeIdx = i; break; }
                }
            }
            if (closeIdx >= 0)
            {
                paramsRegion = sigLine.Substring(parenIdx, closeIdx - parenIdx + 1);
                var after = sigLine.Substring(closeIdx + 1);
                var braceIdx = after.IndexOf('{');
                returnRegion = braceIdx >= 0 ? after.Substring(0, braceIdx) : after;
            }
        }

        var combined = head + " " + paramsRegion + " " + returnRegion;
        var normalized = Regex.Replace(combined.Trim(), @"\s+", " ");
        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
    /// <summary>Extracts quoted code/HTML snippets from a step's change description for
    /// exact-target comparison — two steps quoting the same element are the same edit
    /// even if the surrounding prose differs.</summary>
    private static HashSet<string> ExtractQuotedSnippets(string text)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (Match m in Regex.Matches(text, @"<[^>]+>.*?</\w+>|`[^`]+`"))
        {
            var norm = Regex.Replace(m.Value.ToLowerInvariant(), @"\s+", " ").Trim();
            if (norm.Length >= 15) result.Add(norm);
        }
        return result;
    }
    /// <summary>
    /// Collapses near-duplicate plan steps that target the same file and describe the
    /// same underlying edit in different words. Exact-string dedup (DeduplicateSteps)
    /// only catches identical text; it misses cases where the planner (or the per-step
    /// decoupling audit) independently produces multiple steps that all mean "do the
    /// same thing" with different phrasing. Left unchecked, that duplication multiplies
    /// during decoupling — e.g. 3 duplicate "remove priority tag from all 3 columns"
    /// steps each get decoupled into 3 column-specific sub-steps = 9 steps for 3 real
    /// edits, with inconsistent guessed line numbers for each duplicate set.
    ///
    /// This NEVER reduces distinct, intentional steps: two steps that reference
    /// different locations (todo vs. doing vs. done) are always kept, even if their
    /// wording is otherwise very similar.
    /// </summary>
    public static List<PlanStep> DeduplicateSimilarSteps(List<PlanStep> steps, double similarityThreshold = 0.72)
    {
        if (steps.Count <= 1) return steps;

        var keep = new List<PlanStep>();
        var keptSignatures = new List<(HashSet<string> keywords, HashSet<string> quoted, string file, string? locationTag)>();

        foreach (var step in steps)
        {
            var file = (step.File ?? "").Trim();
            var change = step.Change ?? "";
            var keywords = AgentUtilities.ExtractMeaningfulKeywords(change.ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var quoted = ExtractQuotedSnippets(change);
            var locationTag = AgentUtilities.ExtractLocationTag(change);

            var isDuplicate = false;
            for (var i = 0; i < keep.Count; i++)
            {
                var (existingKeywords, existingQuoted, existingFile, existingLocationTag) = keptSignatures[i];
                if (!string.Equals(existingFile, file, StringComparison.OrdinalIgnoreCase)) continue;

                // Different explicit locations (todo vs. doing vs. done) are ALWAYS distinct —
                // never collapse steps just because their wording overlaps if they name
                // different targets. This is what protects the user's actual 3-column intent.
                if (locationTag != null && existingLocationTag != null &&
                    !string.Equals(locationTag, existingLocationTag, StringComparison.OrdinalIgnoreCase))
                    continue;

                var keywordSim = JaccardSimilarity(keywords, existingKeywords);
                var quotedOverlap = quoted.Count > 0 && existingQuoted.Count > 0 && quoted.Overlaps(existingQuoted);

                if (keywordSim >= similarityThreshold || quotedOverlap)
                {
                    isDuplicate = true;
                    break;
                }
            }

            if (isDuplicate) continue;

            keep.Add(step);
            keptSignatures.Add((keywords, quoted, file, locationTag));
        }

        return keep;
    }

    private static double JaccardSimilarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 0;
        var intersection = a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count();
        var union = a.Union(b, StringComparer.OrdinalIgnoreCase).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    /// <summary>
    /// Detects the indentation WIDTH (number of columns per indent level) used in a source file.
    /// Uses GCD over all observed leading-space counts so that 4/8/12 → 4 and 2/4/6 → 2.
    /// Tab-indented files return 4 (conventional tab stop). Mixed tab/space files derive width
    /// from the space-indented lines only. Falls back to 4 when indeterminate.
    /// </summary>
    public static int DetectIndentWidth(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return 4;

        var lines = source.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        // ── Tab-indented files: convention is tab width = 4 ──
        var hasTabIndent = false;
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            if (line[0] == '\t') { hasTabIndent = true; break; }
        }
        if (hasTabIndent)
        {
            // If there are also space-indented lines, derive from those; otherwise default to 4.
            var spaceIndents = new HashSet<int>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Length > 0 && line[0] == '\t') continue;
                var n = 0;
                while (n < line.Length && line[n] == ' ') n++;
                if (n > 0) spaceIndents.Add(n);
            }
            if (spaceIndents.Count == 0) return 4;
            return DetectIndentWidthFromIndents(spaceIndents);
        }

        // ── Pure space-indented: collect all distinct indent amounts ──
        var indentSet = new HashSet<int>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var n = 0;
            while (n < line.Length && line[n] == ' ') n++;
            if (n > 0) indentSet.Add(n);
        }

        if (indentSet.Count == 0) return 4;
        return DetectIndentWidthFromIndents(indentSet);
    }
    /// <summary>
    /// Extracts the method/function name that a step intends to add/insert into a .js file.
    /// Handles many phrasings:
    ///   "add method fooBar", "create function baz", "insert handler onClick",
    ///   "add fooBar() method", "add vm.doSomething", "add this.handleClick",
    ///   "define method named processQueue", "implement onFileSelected handler"
    /// </summary>
    public static string? ExtractJsMethodNameFromChange(string change)
    {
        if (string.IsNullOrWhiteSpace(change)) return null;

        // Pattern 1: "<verb> method/function/handler (named|called) X"
        var m = Regex.Match(change,
            @"\b(?:add|create|insert|define|implement)\s+(?:a\s+)?(?:new\s+)?(?:method|function|handler)\s+(?:named\s+|called\s+)?([A-Za-z_$][A-Za-z0-9_$]*)",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;

        // Pattern 2: "<verb> X method/function/handler"  (name BEFORE the noun)
        m = Regex.Match(change,
            @"\b(?:add|create|insert|define|implement)\s+(?:a\s+)?(?:new\s+)?([A-Za-z_$][A-Za-z0-9_$]*)\s+(?:method|function|handler)\b",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;

        // Pattern 3: "<verb> X() method"  (name with empty parens)
        m = Regex.Match(change,
            @"\b(?:add|create|insert|define|implement)\s+(?:the\s+)?([A-Za-z_$][A-Za-z0-9_$]*)\s*\(\s*\)",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;

        // Pattern 4: "<verb> vm.X" / "<verb> this.X" / "<verb> self.X"
        m = Regex.Match(change,
            @"\b(?:add|create|insert|define|implement)\s+(?:the\s+)?(?:vm|this|self|that)\.([A-Za-z_$][A-Za-z0-9_$]*)",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;

        // Pattern 5: bare "<verb> X" (last resort — only if X looks like a method name)
        m = Regex.Match(change,
            @"\b(?:add|create|insert)\s+(?:a\s+)?(?:new\s+)?([A-Za-z_$][A-Za-z0-9_$]*)\b");
        if (m.Success)
        {
            var candidate = m.Groups[1].Value;
            // Reject common English words that aren't method names
            var stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "new", "code", "logic", "feature", "support",
            "validation", "test", "tests", "file", "block", "section", "comment"
        };
            if (!stopwords.Contains(candidate) && candidate.Length >= 3) return candidate;
        }

        return null;
    }
    public static string BuildValidatorContextExcerpt(string discoveryContext, string stepChange, int maxChars = 9000)
    {
        if (discoveryContext.Length <= maxChars) return discoveryContext;

        var keywords = ExtractMeaningfulKeywords(stepChange.ToLowerInvariant())
            .Where(k => k.Length >= 4).ToList();

        // Find the first line that actually mentions one of the step's keywords/symbols
        var lines = discoveryContext.Split('\n');
        var hitLine = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (keywords.Any(k => lines[i].Contains(k, StringComparison.OrdinalIgnoreCase))) { hitLine = i; break; }
        }

        if (hitLine < 0)
            return discoveryContext[..maxChars] + "\n...(truncated)";

        // Center a window on the hit so the validator actually sees the referenced code
        var charOffset = lines.Take(hitLine).Sum(l => l.Length + 1);
        var halfWindow = maxChars / 2;
        var start = Math.Max(0, charOffset - halfWindow);
        var end = Math.Min(discoveryContext.Length, start + maxChars);
        var prefix = start > 0 ? "...(truncated head)...\n" : "";
        var suffix = end < discoveryContext.Length ? "\n...(truncated tail)..." : "";
        return prefix + discoveryContext[start..end] + suffix;
    } 
    public static bool JsMethodExistsInContent(string content, string methodName)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(methodName))
            return false;
        if (methodName.Length < 2) return false;

        var name = Regex.Escape(methodName);

        // 1. function declaration:  function foo(
        if (Regex.IsMatch(content, $@"\bfunction\s+{name}\s*\(", RegexOptions.IgnoreCase))
            return true;

        // 2. function expression:  const/let/var foo = function(   |   const foo = async function(
        if (Regex.IsMatch(content,
            $@"\b(?:const|let|var)\s+{name}\s*=\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase))
            return true;

        // 3. arrow function:  const foo = (...) =>   |   const foo = async (...) =>
        if (Regex.IsMatch(content,
            $@"\b(?:const|let|var)\s+{name}\s*=\s*(?:async\s+)?\([^)]*\)\s*=>",
            RegexOptions.IgnoreCase))
            return true;

        // 4. object property function:  foo: function(   |   foo: async function(
        if (Regex.IsMatch(content,
            $@"\b{name}\s*:\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase))
            return true;

        // 5. object property arrow:  foo: () =>   |   foo: async () =>
        if (Regex.IsMatch(content,
            $@"\b{name}\s*:\s*(?:async\s+)?\([^)]*\)\s*=>",
            RegexOptions.IgnoreCase))
            return true;

        // 6. object method shorthand (line-anchored to avoid matching call sites):
        //    foo(  |  async foo(  |  static foo(
        if (Regex.IsMatch(content,
            $@"(?m)^\s*(?:static\s+|async\s+|get\s+|set\s+)?{name}\s*\(",
            RegexOptions.IgnoreCase))
            return true;

        // 7. vm.foo = function(   |   this.foo = function(   |   self.foo = function(
        if (Regex.IsMatch(content,
            $@"\b(?:vm|this|self|that)\.{name}\s*=\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase))
            return true;

        // 8. vm.foo = (...) =>   |   this.foo = (...) =>
        if (Regex.IsMatch(content,
            $@"\b(?:vm|this|self|that)\.{name}\s*=\s*(?:async\s+)?\([^)]*\)\s*=>",
            RegexOptions.IgnoreCase))
            return true;

        // 9. Class.prototype.foo = function(
        if (Regex.IsMatch(content,
            $@"\.prototype\.{name}\s*=\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase))
            return true;

        // 10. export function foo(   |   export const foo =
        if (Regex.IsMatch(content,
            $@"\bexport\s+(?:async\s+)?function\s+{name}\s*\(",
            RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(content,
            $@"\bexport\s+(?:const|let|var)\s+{name}\s*=",
            RegexOptions.IgnoreCase))
            return true;

        // 11. Object.defineProperty(obj, 'foo', ...)
        if (Regex.IsMatch(content,
            $@"Object\.defineProperty\s*\([^,]+,\s*['""]{name}['""]",
            RegexOptions.IgnoreCase))
            return true;

        // 12. class method declaration inside a class body (line-anchored):
        //     public/private/protected/static/async/get/set + foo(
        if (Regex.IsMatch(content,
            $@"(?m)^\s*(?:public\s+|private\s+|protected\s+|static\s+|async\s+|get\s+|set\s+|readonly\s+)*{name}\s*\(",
            RegexOptions.IgnoreCase))
            return true;

        return false;
    }
    /// <summary>
    /// Extracts the method name defined by a piece of JavaScript source code.
    /// Recognizes declarations only (not call sites).
    /// </summary>
    public static string? ExtractJsMethodNameFromCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        // function foo(
        var m = Regex.Match(code, @"\bfunction\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*\(", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;

        // const/let/var foo = function(   |   const foo = async function(
        m = Regex.Match(code,
            @"\b(?:const|let|var)\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;

        // const foo = (...) =>   |   const foo = async (...) =>
        m = Regex.Match(code,
            @"\b(?:const|let|var)\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:async\s+)?\([^)]*\)\s*=>",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;

        // vm.foo = function(   |   this.foo = function(   |   self.foo = function(
        m = Regex.Match(code,
            @"\b(?:vm|this|self|that)\.([A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;

        // vm.foo = (...) =>   |   this.foo = (...) =>
        m = Regex.Match(code,
            @"\b(?:vm|this|self|that)\.([A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:async\s+)?\([^)]*\)\s*=>",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;

        // foo: function(   |   foo: async function(
        m = Regex.Match(code,
            @"\b([A-Za-z_$][A-Za-z0-9_$]*)\s*:\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;

        // foo: () =>   |   foo: async () =>
        m = Regex.Match(code,
            @"\b([A-Za-z_$][A-Za-z0-9_$]*)\s*:\s*(?:async\s+)?\([^)]*\)\s*=>",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;

        // Object method shorthand at start of line:  foo(  or  async foo(  or  static foo(
        m = Regex.Match(code,
            @"(?m)^\s*(?:static\s+|async\s+|get\s+|set\s+)?([A-Za-z_$][A-Za-z0-9_$]*)\s*\(");
        if (m.Success) return m.Groups[1].Value;

        // Class.prototype.foo = function(
        m = Regex.Match(code,
            @"\.prototype\.([A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;

        // export function foo(   |   export const foo =
        m = Regex.Match(code,
            @"\bexport\s+(?:async\s+)?function\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*\(",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;

        m = Regex.Match(code,
            @"\bexport\s+(?:const|let|var)\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*=",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;

        return null;
    }
    private static int DetectIndentWidthFromIndents(HashSet<int> indents)
    {
        var list = indents.ToList();
        var gcd = list[0];
        for (var i = 1; i < list.Count; i++)
        {
            gcd = Gcd(gcd, list[i]);
            if (gcd == 1) break;
        }

        // Sanity band — common indent widths are 1..8.
        if (gcd is >= 1 and <= 8) return gcd;

        // Pathological GCD (e.g. 16 because everything was a single 16-space indent).
        // Fall back to the smallest observed indent, clamped.
        var min = list.Min();
        return (min > 0 && min <= 8) ? min : 4;
    }

    private static int Gcd(int a, int b)
    {
        while (b > 0)
        {
            var t = b;
            b = a % b;
            a = t;
        }
        return a;
    }

    /// <summary>Extracts an explicit location tag (todo/doing/done/selfImproving) from a
    /// step's change description, if present, so distinct-location steps are never merged.</summary>
    public static string? ExtractLocationTag(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"\b(todo|doing|done|selfimproving|self-improving)\b", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant().Replace("-", "") : null;
    }
    public static List<PlanStep> RemergeTableCreationSplits(List<PlanStep> steps)
    {
        var merged = new List<PlanStep>();
        for (var i = 0; i < steps.Count; i++)
        {
            var cur = steps[i];
            var curLower = (cur.Change ?? "").ToLowerInvariant();
            var isTableCreationStep = Regex.IsMatch(curLower, @"\bcreate\s+table\b") &&
                                       !Regex.IsMatch(curLower, @"\binsert\b|\bupdate\b");

            if (isTableCreationStep && i + 1 < steps.Count &&
                string.Equals(cur.File, steps[i + 1].File, StringComparison.OrdinalIgnoreCase))
            {
                var next = steps[i + 1];
                // Fold the table-creation intent into the next step's description
                // and drop this step entirely — it was never supposed to be separate.
                next.Change = $"{next.Change} Include an inline CREATE TABLE IF NOT EXISTS statement " +
                               $"at the top of the method body before any INSERT/UPDATE/SELECT.";
                merged.Add(next);
                i++; // skip the merged-in step
                continue;
            }
            merged.Add(cur);
        }
        return merged;
    }
    public static string CleanVerbatimStringEscapes(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;

        // Match verbatim strings: @"(?:[^"]|"")*"
        // Regex pattern: @"(?:""|[^"])*"
        // In C# verbatim string syntax, this is written as: @"@""(?:""|[^""])*"""
        var regex = new Regex(@"@""(?:""|[^""])*""", RegexOptions.Compiled);
        bool changed = false;

        var result = regex.Replace(content, match =>
        {
            var val = match.Value;
            // val starts with @" and ends with "
            // Substring(2, val.Length - 3) extracts the inner content
            var inside = val.Substring(2, val.Length - 3);

            bool hasEscapeSeq = inside.Contains(@"\r\n") || inside.Contains(@"\r") || inside.Contains(@"\n") || inside.Contains(@"\t");
            bool looksLikeSql = Regex.IsMatch(inside, @"\b(SELECT|INSERT|UPDATE|DELETE|CREATE\s+TABLE|ALTER\s+TABLE|DROP\s+TABLE|FROM|WHERE|JOIN|VALUES|SET)\b", RegexOptions.IgnoreCase);

            // Only replace if it has escape sequences AND looks like SQL to avoid breaking file paths (e.g. C:\newfolder)
            if (hasEscapeSeq && looksLikeSql)
            {
                changed = true;
                var fixedInside = inside
                    .Replace(@"\r\n", "\r\n")
                    .Replace(@"\r", "\r")
                    .Replace(@"\n", "\n")
                    .Replace(@"\t", "\t");

                // Reconstruct the verbatim string: @" + content + "
                return "@\"" + fixedInside + "\"";
            }
            return val;
        });

        return changed ? result : content;
    }

    /// <summary>
    /// Post-edit fixup for .cs files: cleans verbatim string escapes that survived pre-edit processing
    /// and flattens hallucinated DTO property wrappers (e.g. dto.SystemSpecs?.OS → dto.OS).
    /// </summary>
    public static string PostEditCSharpFixup(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return content;

        // 1. Clean any remaining verbatim string escapes (safety net after file write)
        content = CleanVerbatimStringEscapes(content);

        // 2. Flatten hallucinated DTO property wrappers.
        //    LLM often nests flat DTO properties under an invented sub-object (SystemSpecs, System, Hardware, Specs).
        //    Pattern: dtoVar.Word?.Property → dtoVar.Property
        //    Only flatten when Word is not a recognized type or keyword.
        var flatPattern = new Regex(@"\.(SystemSpecs|System|HardwareInfo|Hardware|Specs|SystemInfo|MetaInfo|Details|DataInfo|BenchmarkInfo|BenchData)\??\.([A-Z]\w+)", RegexOptions.IgnoreCase);
        content = flatPattern.Replace(content, m => "." + m.Groups[2].Value);

        // 3. Fix doubled braces in interpolated strings: $"text {{variable}} text" → $"text {variable} text"
        //    LLM sometimes writes {{ex.Message}} when it means {ex.Message} in $"" strings.
        content = Regex.Replace(content, @"(\$""[^""]*)\{\{(\w+(?:\.\w+)+)\}\}([^""]*"")", "$1{$2}$3");

        // 4. Fix malformed Score?.Replace or TryParse on Score (Score is string?, not numeric).
        //    Pattern: .Score?.Replace "%", string.Empty  → replace entire TryParse+Replace with direct Score assignment
        content = Regex.Replace(content,
            @"decimal\.TryParse\s*\(\s*\w+\.Score\??(?:\.Replace\s*""[^""]*""(?:\s*,\s*""[^""]*"")?)?\s*,(\s*out\s+\w+(?:\.\w+)*\s*)\)",
            m =>
            {
                var outVar = m.Groups[1].Value.Trim();
                // Replace with direct assignment: decimal.TryParse(benchmark.Score, out score) — clean
                return $"decimal.TryParse(benchmark.Score, {outVar})";
            });

        // 5. Merge stray semicolon after string closing: ..."\n\t\t; → ...";
        //    LLM/formatter sometimes puts the ; on its own line after the closing " of a string literal
        content = Regex.Replace(content,
            @"(?<=[^ \t\r\n@])""\s*\r?\n[ \t]*;",
            @""";");

        return content;
    }
    
    /// <summary>
    /// Deterministically ensures that plans creating new Angular components 
    /// use the CLI scaffolding command and update app.module.ts.
    /// </summary>
    public static AgentPlan? EnforceAngularScaffolding(AgentPlan plan, string projectRoot)
    {
        if (plan?.Plan == null || plan.Plan.Count == 0) return plan;

        // 1. Check if the plan creates a new .component.ts file that doesn't exist yet
        var compStep = plan.Plan.FirstOrDefault(p =>
            p.File != null &&
            p.File.EndsWith(".component.ts", StringComparison.OrdinalIgnoreCase) &&
            IsRelativePath(p.File));

        if (compStep == null) return plan;

        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, compStep.File.Replace('/', Path.DirectorySeparatorChar)));

        // If the file already exists, it's an edit, not a creation. Skip injection.
        if (File.Exists(fullPath)) return plan;

        // 2. Check if a scaffolding command already exists
        bool hasScaffoldCommand = plan.Plan.Any(p =>
            p.File == "_command" &&
            p.Change != null &&
            p.Change.Contains("ng g c", StringComparison.OrdinalIgnoreCase));

        if (!hasScaffoldCommand)
        {
            // Extract project root folder (e.g., "maxhanna.client")
            var rootFolder = compStep.File.Split('/')[0];
            var dir = Path.GetDirectoryName(compStep.File)?.Replace('\\', '/');
            var name = Path.GetFileNameWithoutExtension(compStep.File).Replace(".component", "");

            // Generate the CLI command
            var cmd = $"{(rootFolder.Contains(".") ? $"cd {rootFolder}; " : "")}npx ng g c {dir}/{name} --skip-tests";

            // Insert the scaffolding command at the very beginning
            plan.Plan.Insert(0, new PlanStep
            {
                File = "_command",
                Change = cmd,
                Priority = 1
            });
        }

        // 3. Check if app.module.ts is being updated
        bool hasModuleUpdate = plan.Plan.Any(p =>
            p.File != null &&
            p.File.EndsWith("app.module.ts", StringComparison.OrdinalIgnoreCase));

        if (!hasModuleUpdate)
        {
            var rootFolder = compStep.File.Split('/')[0];
            var modulePath = $"{rootFolder}/src/app/app.module.ts";
            var componentName = Path.GetFileNameWithoutExtension(compStep.File).Replace(".component", "");

            // Insert the module update step at index 1 (after scaffolding, before edits)
            plan.Plan.Insert(1, new PlanStep
            {
                File = modulePath,
                Change = $"Register the new {componentName} component in the @NgModule declarations array",
                Priority = 1
            });
        }

        return plan;
    } 
    public static HashSet<string> ExtractSqlTableNames(string source)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "the", "and", "or", "not", "in", "on", "at", "to", "for", "of", "by",
            "as", "is", "it", "from", "join", "inner", "outer", "left", "right",
            "where", "set", "values", "select", "insert", "update", "delete",
            "order", "group", "having", "limit", "offset", "true", "false", "null",
            "count", "sum", "avg", "min", "max", "distinct",
            "date", "time", "year", "month", "day", "hour", "minute", "second",
            "now", "between", "like", "exists", "case", "when", "then", "else", "end",
            "return", "returns", "declare", "begin", "if", "else",
            "start", "stop", "commit", "rollback", "savepoint",
            "int", "integer", "bigint", "smallint", "tinyint",
            "decimal", "numeric", "float", "double", "real",
            "char", "varchar", "text", "blob", "binary",
            "enum", "set", "json", "boolean", "bit",
            "default", "unique", "index", "key", "primary", "foreign",
            "cascade", "restrict", "action", "check",
            "auto_increment", "unsigned", "signed", "zerofill",
            "character", "collate", "charset", "engine", "row_format"
        };
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match sm in Regex.Matches(source,
            @"@?""(?:[^""\\]*(?:\\.[^""\\]*)*)""", RegexOptions.Singleline))
        {
            var val = sm.Value;
            if (!Regex.IsMatch(val, @"\b(SELECT|INSERT|UPDATE|DELETE)\b", RegexOptions.IgnoreCase))
                continue;
            foreach (Match m in Regex.Matches(val,
                @"(?:FROM|JOIN|INTO|UPDATE|TABLE(?:\s+IF\s+NOT\s+EXISTS)?)\s+`?(\w+(?:\.\w+)?)`?",
                RegexOptions.IgnoreCase))
            {
                var tbl = m.Groups[1].Value;
                if (tbl.Contains('.')) tbl = tbl.Split('.')[^1];
                if (tbl.Length > 2 && !skip.Contains(tbl) && !char.IsDigit(tbl[0]))
                    tables.Add(tbl);
            }
        }
        return tables;
    }
    public static string AutoFixSqlWhitespace(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return content;
        var result = content;
        var changed = false;

        var stringRegex = new Regex(@"@?""(?:[^""\\]|\\.|"""")*""", RegexOptions.Singleline);
        var matches = stringRegex.Matches(result);
        foreach (Match m in matches)
        {
            var sqlStr = m.Value;

            if (!Regex.IsMatch(sqlStr, @"\b(SELECT|INSERT|UPDATE|DELETE|CREATE\s+TABLE|ALTER\s+TABLE)\b", RegexOptions.IgnoreCase))
                continue;

            var fixedSql = sqlStr;

            var keywordDigit = new Regex(@"\b(INTERVAL|MINUTE|HOUR|DAY|MONTH|YEAR|SECOND|MICROSECOND|WEEK|QUARTER|LIMIT|OFFSET|TOP|SELECT|DELETE|UPDATE|INSERT|FROM|WHERE|JOIN|AND|OR|NOT|IN|ON|AS|BY|ORDER|GROUP|HAVING|UNION|INTO|VALUES|SET|CREATE|TABLE|ALTER|DROP|CASE|WHEN|THEN|ELSE|END|EXISTS|DISTINCT|WITH|ALL)(\d)", RegexOptions.IgnoreCase);
            fixedSql = keywordDigit.Replace(fixedSql, "$1 $2");

            var keywordStar = new Regex(@"\b(SELECT|DELETE|DISTINCT|ALL)\*", RegexOptions.IgnoreCase);
            fixedSql = keywordStar.Replace(fixedSql, "$1 *");

            var keywordParen = new Regex(@"\b(SELECT|FROM|WHERE|JOIN|INNER|LEFT|RIGHT|OUTER|AND|OR|NOT|IN|BETWEEN|LIKE|IS|ON|AS|BY|ORDER|GROUP|HAVING|LIMIT|OFFSET|UNION|INSERT|INTO|VALUES|UPDATE|SET|DELETE|CREATE|TABLE|ALTER|DROP|CASE|WHEN|THEN|ELSE|END|EXISTS|DISTINCT|WITH)\(", RegexOptions.IgnoreCase);
            fixedSql = keywordParen.Replace(fixedSql, "$1 (");

            if (fixedSql != sqlStr)
            {
                result = result.Replace(sqlStr, fixedSql);
                changed = true;
            }
        }

        return changed ? result : content;
    }
    public static string AutoFixPythonStatements(string content, string relPath)
    {
        if (string.IsNullOrWhiteSpace(content)) return content;
        if (!Path.GetExtension(relPath).Equals(".py", StringComparison.OrdinalIgnoreCase)) return content;

        // ✅ DETERMINISTIC FIX: Remove trailing spaces inside string literals.
        // This enforces the rule "NEVER add trailing spaces inside string literals" 
        // which the LLM frequently ignores for Python input() prompts.
        content = Regex.Replace(content, @"(?<!\\)([""'])([^\r\n]*?)(?<!\\)\1", m =>
        {
            var quote = m.Groups[1].Value;
            var strContent = m.Groups[2].Value;

            // Skip if it's a triple-quoted string
            if (strContent.StartsWith(quote)) return m.Value;

            if (strContent.EndsWith(" ") || strContent.EndsWith("\t"))
            {
                strContent = strContent.TrimEnd(' ', '\t');
                return $"{quote}{strContent}{quote}";
            }
            return m.Value;
        });

        // Remove trailing whitespace from lines (LLMs often add spaces before newlines in JSON arrays)
        content = Regex.Replace(content, @"[ \t]+\r?\n", "\n");

        var pyKeywords = "print|return|if|for|while|def|class|import|from|with|try|except|finally|raise|yield|assert|del|global|nonlocal|pass|break|continue";

        // Fix `) print(` -> `)\nprint(` (with or without spaces)
        content = Regex.Replace(content, $@"\)\s*({pyKeywords})\b", ")\n$1");
        // Fix `; print(` -> `;\nprint(`
        content = Regex.Replace(content, $@";\s*({pyKeywords})\b", ";\n$1");
        // Fix `] print(` -> `]\nprint(`
        content = Regex.Replace(content, $@"\]\s*({pyKeywords})\b", "]\n$1");
        // Fix `} print(` -> `}\nprint(`
        content = Regex.Replace(content, $@"\}}\s*({pyKeywords})\b", "}\n$1");

        return content;
    }
    public static string AutoFixHtmlIndentation(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return content;

        var lines = content.Split('\n');
        var result = new List<string>();
        var depth = 0;
        var inCodeBlock = false;
        var codeBlockBaseIndent = -1;
        var jsBraceDepth = 0;

        var voidElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr" };

        for (int i = 0; i < lines.Length; i++)
        {
            var originalLine = lines[i];
            var trimmed = originalLine.Trim();

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                result.Add("");
                continue;
            }

            // Preserve and re-align formatting inside script/style/pre blocks
            if (inCodeBlock)
            {
                // Handle the closing tag of the code block
                if (trimmed.Contains("</script>", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Contains("</style>", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Contains("</pre>", StringComparison.OrdinalIgnoreCase))
                {
                    inCodeBlock = false;
                    codeBlockBaseIndent = -1;
                    jsBraceDepth = 0;
                    result.Add(new string(' ', (depth - 1) * 2) + trimmed);
                    depth = Math.Max(0, depth - 1);
                    continue;
                }

                if (codeBlockBaseIndent == -1)
                {
                    codeBlockBaseIndent = (depth + 1) * 2;
                }

                int currentJsIndent = codeBlockBaseIndent + (jsBraceDepth * 2);

                // Dedent lines that start with closing braces/parens
                if (trimmed.StartsWith("}") || trimmed.StartsWith(")") || trimmed.StartsWith("]"))
                {
                    currentJsIndent = Math.Max(0, currentJsIndent - 2);
                }

                result.Add(new string(' ', currentJsIndent) + trimmed);

                // Update brace depth for subsequent lines
                int opens = trimmed.Count(c => c == '{');
                int closes = trimmed.Count(c => c == '}');
                jsBraceDepth = Math.Max(0, jsBraceDepth + opens - closes);

                continue;
            }

            var matches = Regex.Matches(trimmed, @"<(/?)([a-zA-Z0-9]+)[^>]*?(/?)>");
            int adjust = 0;
            bool startsWithClosing = trimmed.StartsWith("</");

            foreach (Match m in matches)
            {
                bool isClosing = m.Groups[1].Value == "/";
                string tag = m.Groups[2].Value.ToLower();
                bool isSelfClosing = m.Groups[3].Value == "/" || voidElements.Contains(tag);

                if (!isClosing && !isSelfClosing)
                {
                    adjust++;
                    if ((tag == "script" || tag == "style" || tag == "pre") &&
                        !trimmed.Contains($"</{tag}>", StringComparison.OrdinalIgnoreCase))
                    {
                        inCodeBlock = true;
                    }
                }
                else if (isClosing)
                {
                    adjust--;
                }
            }

            int currentDepth = depth;
            if (startsWithClosing) currentDepth = Math.Max(0, depth - 1);

            result.Add(new string(' ', currentDepth * 2) + trimmed);

            depth = Math.Max(0, depth + adjust);
        }

        return string.Join("\n", result);
    }
    public static string AutoFixCssWhitespace(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return content;

        var lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            // Skip comments, at-rules, selectors, and lines with URLs/strings to avoid corruption
            if (trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed.StartsWith("*") ||
                trimmed.StartsWith("@") || trimmed.StartsWith("&") ||
                trimmed.Contains("{") || trimmed.Contains("}") ||
                trimmed.Contains("url(") || trimmed.Contains("\""))
            {
                continue;
            }

            // Find the first colon that is not inside parentheses
            int colonIdx = -1;
            int parenDepth = 0;
            for (int j = 0; j < trimmed.Length; j++)
            {
                if (trimmed[j] == '(') parenDepth++;
                else if (trimmed[j] == ')') parenDepth = Math.Max(0, parenDepth - 1);
                else if (trimmed[j] == ':' && parenDepth == 0)
                {
                    colonIdx = j;
                    break;
                }
            }

            if (colonIdx <= 0 || colonIdx == trimmed.Length - 1) continue;

            var prop = trimmed.Substring(0, colonIdx).TrimEnd();
            var valueWithSemi = trimmed.Substring(colonIdx + 1);

            string trailingComment = "";
            var commentIdx = valueWithSemi.IndexOf("//");
            if (commentIdx >= 0)
            {
                trailingComment = " " + valueWithSemi.Substring(commentIdx).TrimEnd();
                valueWithSemi = valueWithSemi.Substring(0, commentIdx);
            }

            var value = valueWithSemi.Trim();
            if (value.Length == 0) continue;

            // 1. Space after comma: `0,0` -> `0, 0`
            value = Regex.Replace(value, @",(?!\s)", ", ");

            // 2. Space between unit and number: `10px20px` -> `10px 20px`
            value = Regex.Replace(value, @"(\d(?:px|pt|em|rem|ex|ch|vw|vh|vmin|vmax|%|deg|s|ms|fr|dpi|dppx|dpcm|Hz|kHz))(?=\d)", "$1 ");

            // 3. Space between unit and word: `10pxauto` -> `10px auto`
            value = Regex.Replace(value, @"(\d(?:px|pt|em|rem|ex|ch|vw|vh|vmin|vmax|%|deg|s|ms|fr|dpi|dppx|dpcm|Hz|kHz))(?=[a-z])", "$1 ");

            // 4. Space between word and number: `bold12px` -> `bold 12px` (but avoid breaking hex colors like #fff0)
            value = Regex.Replace(value, @"(?<!#)([a-z])(\d)", "$1 $2");

            // 5. Clean up double spaces just in case
            value = Regex.Replace(value, @"\s+", " ").Trim();

            var leadingWhitespace = line.Substring(0, line.Length - trimmed.Length);
            lines[i] = leadingWhitespace + prop + ": " + value + trailingComment;
        }

        return string.Join("\n", lines);
    }
    public static StepExplorationResponse ParseStepExplorationResponse(string raw)
    {
        var empty = new StepExplorationResponse { FilesToRead = new List<string>() };
        if (string.IsNullOrWhiteSpace(raw)) return empty;
        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```",
                    RegexOptions.IgnoreCase);
                if (m.Success) cleaned = m.Groups[1].Value.Trim();
            }
            var fb = cleaned.IndexOf('{'); var lb = cleaned.LastIndexOf('}');
            if (fb >= 0 && lb > fb) cleaned = cleaned[fb..(lb + 1)];

            using var doc = JsonDocument.Parse(cleaned,
                new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return empty;

            var ready = root.TryGetProperty("ready", out var rEl) && rEl.ValueKind == JsonValueKind.True && rEl.GetBoolean();

            var files = new List<string>();
            if (root.TryGetProperty("filesToRead", out var fArr) &&
                fArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in fArr.EnumerateArray())
                {
                    if (f.ValueKind == JsonValueKind.String)
                    {
                        var s = f.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            files.Add(s.Replace('\\', '/'));
                    }
                }
            }

            var refined = root.TryGetProperty("refinedChange", out var rcEl) && rcEl.ValueKind == JsonValueKind.String ? rcEl.GetString() : null;
            var symbol = root.TryGetProperty("targetSymbol", out var tsEl) && tsEl.ValueKind == JsonValueKind.String ? tsEl.GetString() : null;
            var range = root.TryGetProperty("estimatedLineRange", out var lrEl) && lrEl.ValueKind == JsonValueKind.String ? lrEl.GetString() : null;

            var conf = 0;
            if (root.TryGetProperty("confidence", out var cEl) && cEl.ValueKind == JsonValueKind.Number)
                conf = cEl.GetInt32();

            return new StepExplorationResponse
            {
                Ready = ready,
                FilesToRead = files,
                RefinedChange = refined,
                TargetSymbol = symbol,
                LineRange = range,
                Confidence = conf
            };
        }
        catch { return empty; }
    }

    public static bool IsSpecialMarker(string file) =>
        file.Equals("_git", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_rename", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_delete_file", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_show", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_display", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_ping", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_package_install", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_create_file", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_command", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_web_search", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_web_fetch", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_explore", StringComparison.OrdinalIgnoreCase);
   
    public static bool IsPathUnderRoot(string fullPath, string root)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        fullPath = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)) return true;
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeLineEndings(string s) => s.Replace("\r\n", "\n");

    public static string StripLineLeadingWhitespace(string s)
    {
        var lines = s.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimStart();
        return string.Join("\n", lines);
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "\n[Preview ended; omitted remainder is not code.]";

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
        // Word-boundary match to avoid "add" matching inside "address"
        return verbs.Any(v => Regex.IsMatch(lower, $@"\b{Regex.Escape(v)}\b"));
    } 

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

        var m = Regex.Match(changeDesc, @"(?:\s+to\s+|[ \t]*[→\u2192][ \t]*)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var after = changeDesc[(m.Index + m.Length)..].Trim().Trim(' ', '"', '\'');

        if (string.IsNullOrWhiteSpace(after)) return null;

        var dir = Path.GetDirectoryName(currentRelPath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        var target = after.Contains('/') || after.Contains('\\')
            ? after.Replace('\\', '/')
            : (string.IsNullOrEmpty(dir) ? after : dir.Replace('\\', '/') + "/" + after);

        return string.IsNullOrWhiteSpace(target) || target.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ? null : target;
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
            sb.AppendLine($"```\n{output}\n```");
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
        if (string.IsNullOrEmpty(json)) return json;
        return Regex.Replace(json,
            @"(?<=[\{\,])\s*([a-zA-Z_$][a-zA-Z0-9_$]*)\s*(?=:)",
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
        if (inString) { sb.Append('"'); changed = true; }
        return changed ? sb.ToString() : null;
    }

    public static bool IsRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (Path.IsPathRooted(path)) return false;

        var specialMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "_git", "_ping", "_show", "_display", "_create_file", "_package_install",
            "_command", "_web_search", "_web_fetch", "_explore", "_rename", "_rename_file",
            "_move_file", "_delete_file", "_continue"
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

  
    public static List<AgentStep> ExtractEditPairs(string text, string defaultPath)
    {
        var steps = new List<AgentStep>();

        // Fix common unquoted-key LLM blunder
        var unquotedNew = text.IndexOf(",newString\"", StringComparison.OrdinalIgnoreCase);
        var unquotedOld = text.IndexOf(",oldString\"", StringComparison.OrdinalIgnoreCase);
        if (unquotedNew >= 0 || unquotedOld >= 0)
        {
            var fixedText = text;
            if (unquotedNew >= 0) fixedText = fixedText.Substring(0, unquotedNew + 1) + "\"" + fixedText.Substring(unquotedNew + 1);
            if (unquotedOld >= 0) fixedText = fixedText.Substring(0, unquotedOld + 1) + "\"" + fixedText.Substring(unquotedOld + 1);
            return ExtractEditPairs(fixedText, defaultPath);
        }

        var i = 0;
        while (i < text.Length)
        {
            var oldKeyIdx = text.IndexOf("\"oldString\"", i, StringComparison.OrdinalIgnoreCase);
            var newKeyIdx = text.IndexOf("\"newString\"", i, StringComparison.OrdinalIgnoreCase);
            if (oldKeyIdx < 0 || newKeyIdx < 0) break;

            string firstKey, secondKey;
            int firstIdx, secondIdx;
            if (oldKeyIdx < newKeyIdx)
            { firstKey = "oldString"; secondKey = "newString"; firstIdx = oldKeyIdx; secondIdx = newKeyIdx; }
            else
            { firstKey = "newString"; secondKey = "oldString"; firstIdx = newKeyIdx; secondIdx = oldKeyIdx; }

            var firstVal = ExtractJsonStringValue(text, firstIdx + firstKey.Length);
            if (firstVal == null) { i = firstIdx + 1; continue; }

            var secKeyPos = text.IndexOf("\"" + secondKey + "\"", firstVal.Value.EndPos, StringComparison.OrdinalIgnoreCase);
            if (secKeyPos < 0) { i = firstIdx + 1; continue; }

            var secVal = ExtractJsonStringValue(text, secKeyPos + secondKey.Length);
            if (secVal == null) { i = firstIdx + 1; continue; }

            var oldStr = firstKey == "oldString" ? firstVal.Value.Text : secVal.Value.Text;
            var newStr = firstKey == "newString" ? firstVal.Value.Text : secVal.Value.Text;

            if (!string.IsNullOrEmpty(oldStr) || !string.IsNullOrEmpty(newStr))
                steps.Add(new AgentStep
                {
                    Index = steps.Count,
                    Type = "edit",
                    Path = defaultPath,
                    OldString = oldStr ?? "",
                    NewString = newStr ?? "",
                    Description = "LLM edit (extracted)"
                });

            i = secVal.Value.EndPos;
        }
        return steps;
    }

    private static (string Text, int EndPos)? ExtractJsonStringValue(string text, int keyEndPos)
    {
        var pos = keyEndPos;
        while (pos < text.Length && text[pos] != ':') pos++;
        if (pos >= text.Length) return null;
        pos++;
        while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
        if (pos >= text.Length || text[pos] != '"') return null;
        pos++;

        var start = pos;

        // Find the next JSON key or structure end as an anchor boundary
        var afterKeyStart = keyEndPos + 5;
        var nextKeyPos = int.MaxValue;
        foreach (var key in new[] { "\"oldString\"", "\"newString\"", "\"path\"", "\"toPath\"", "\"description\"", "\"edits\"" })
        {
            var kpos = text.IndexOf(key, afterKeyStart, StringComparison.OrdinalIgnoreCase);
            if (kpos >= 0 && kpos < nextKeyPos) nextKeyPos = kpos;
        }
        var structureEnd = Math.Min(
            nextKeyPos < int.MaxValue ? nextKeyPos : int.MaxValue,
            text.Length);

        while (pos < text.Length && pos <= structureEnd)
        {
            if (text[pos] == '\\') { pos += 2; continue; }

            if (text[pos] == '"')
            {
                var afterPos = pos + 1;
                while (afterPos < text.Length && char.IsWhiteSpace(text[afterPos])) afterPos++;

                // Valid JSON structural transitions: , } ] end-of-text
                if (afterPos >= text.Length || text[afterPos] == ',' || text[afterPos] == '}' || text[afterPos] == ']')
                    return (UnescapeJsonString(text.Substring(start, pos - start)), pos + 1);

                // If followed by "key": pattern, this is the closing delimiter
                if (text[afterPos] == '"' && afterPos + 3 < text.Length)
                {
                    var keyEnd = text.IndexOf('"', afterPos + 1);
                    if (keyEnd > afterPos + 1)
                    {
                        var afterKey = keyEnd + 1;
                        while (afterKey < text.Length && char.IsWhiteSpace(text[afterKey])) afterKey++;
                        if (afterKey < text.Length && text[afterKey] == ':')
                            return (UnescapeJsonString(text.Substring(start, pos - start)), pos + 1);
                    }
                }
            }
            pos++;
        }

        // Fallback: walk backward from nextKeyPos to find the last quote
        if (nextKeyPos > start + 1 && nextKeyPos < int.MaxValue)
        {
            var end = nextKeyPos - 1;
            while (end > start && text[end] != '"') end--;
            if (end > start && text[end] == '"')
                return (UnescapeJsonString(text.Substring(start, end - start)), end + 1);
        }

        return null;
    }
  

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
                    if (i + 4 < s.Length && int.TryParse(s.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code))
                    {
                        sb.Append((char)code);
                        i += 4;
                    }
                    break;
                default: sb.Append(s[i]); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// For large files, returns a "skeleton" of the file, keeping the structural header
    /// and the excerpt most likely to contain the relevant edit target, while replacing
    /// other large blocks with method/class signatures.
    /// </summary>
    public static string ExtractRelevantExcerpt(string fileContent, string changeDesc, string? planOldString, int fileBodyTruncation = 8000)
    {
        const int RadiusLines = 60;
        var lines = fileContent.Split('\n');

        // ── Step 1: Always include structural header (imports + declaration) ──
        var structEnd = 0;
        var foundClassLine = -1;
        for (var i = 0; i < Math.Min(lines.Length, 100); i++)
        {
            var trimmed = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                structEnd = i + 1;
                continue;
            }

            // Header lines (using, import, etc.)
            if (Regex.IsMatch(trimmed, @"^(using|import|namespace|package|from|export|#include|@|\[)", RegexOptions.IgnoreCase))
            {
                structEnd = i + 1;
                continue;
            }

            // Declaration lines (class, interface, etc.)
            if (Regex.IsMatch(trimmed, @"\b(class|interface|struct|record|enum|function|void)\b", RegexOptions.IgnoreCase))
            {
                foundClassLine = i;
                structEnd = i + 1;
                if (i + 1 < lines.Length && lines[i + 1].Trim() == "{") structEnd = i + 2;
                break;
            }

            if (foundClassLine == -1 && i > 50) break;
        }
        if (foundClassLine >= 0) structEnd = Math.Max(structEnd, foundClassLine + 1);

        // ── Step 2: Find the target region ──
        var targetStart = -1;
        var targetEnd = -1;

        if (!string.IsNullOrWhiteSpace(planOldString))
        {
            var anchor = planOldString.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length >= 8);
            if (anchor != null)
            {
                for (var i = structEnd; i < lines.Length; i++)
                {
                    if (!lines[i].Contains(anchor, StringComparison.OrdinalIgnoreCase)) continue;
                    targetStart = Math.Max(structEnd, i - 15);
                    targetEnd = Math.Min(lines.Length, i + planOldString.Split('\n').Length + RadiusLines);
                    break;
                }
            }
        }

        if (targetStart < 0)
        {
            var keywords = AgentUtilities.ExtractMeaningfulKeywords(changeDesc.ToLowerInvariant()).Where(kw => kw.Length >= 5).ToList();
            if (keywords.Count > 0)
            {
                for (var i = structEnd; i < lines.Length; i++)
                {
                    if (!keywords.Any(kw => lines[i].Contains(kw, StringComparison.OrdinalIgnoreCase))) continue;
                    targetStart = Math.Max(structEnd, i - 20);
                    targetEnd = Math.Min(lines.Length, i + RadiusLines);
                    break;
                }
            }
        }

        // ── Step 3: Assemble with Skeleton ──
        var header = string.Join('\n', lines.Take(structEnd));

        if (targetStart < 0)
        {
            // No target found: provide skeleton of the entire body
            var bodySkeleton = GetSkeletonForRange(lines, structEnd, lines.Length);
            return header + "\n" + bodySkeleton;
        }

        var preSkeleton = GetSkeletonForRange(lines, structEnd, targetStart);
        var excerpt = string.Join('\n', lines.Skip(targetStart).Take(targetEnd - targetStart));
        var postSkeleton = GetSkeletonForRange(lines, targetEnd, lines.Length);

        var result = new StringBuilder();
        result.AppendLine(header);
        if (!string.IsNullOrWhiteSpace(preSkeleton)) result.AppendLine(preSkeleton);
        result.AppendLine(excerpt);
        if (!string.IsNullOrWhiteSpace(postSkeleton)) result.AppendLine(postSkeleton);

        return result.ToString();
    }

    // Normalize a single line into a compact skeleton signature when possible.
    private static bool TryNormalizeSkeletonSignature(string line, out string signature)
    {
        signature = null!;
        if (string.IsNullOrWhiteSpace(line)) return false;
        var l = line.Trim();
        // Preserve attributes like [HttpGet], [Route(...)] as lightweight skeleton markers
        if (l.StartsWith("["))
        {
            var am = Regex.Match(l, @"^\s*\[\s*([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase);
            if (am.Success)
            {
                signature = am.Groups[1].Value + " { ... }";
                return true;
            }
            return false;
        }

        // Ignore single-line comments
        if (l.StartsWith("//") || l.StartsWith("/*")) return false;

        // C# class/struct/record/interface
        var csClass = Regex.Match(l, @"^\s*(public|internal|protected|private)?\s*(?:sealed|partial|static|abstract|unsafe|readonly)?\s*(class|struct|record|interface)\s+([A-Za-z_][\w<>]*)\s*(?:[:\{].*)?$", RegexOptions.IgnoreCase);
        if (csClass.Success)
        {
            var mods = csClass.Groups[1].Value;
            var kind = csClass.Groups[2].Value;
            var name = csClass.Groups[3].Value;
            signature = (mods + " " + kind + " " + name).Trim() + " { ... }";
            return true;
        }

        // C# method (includes async, modifiers and generic return types)
        var csMethod = Regex.Match(l, @"^\s*(?!(?:func\b|pub\s+fn\b))(public|private|protected|internal)?\s*(?:static|async|virtual|override|extern|unsafe|sealed|partial)?\s*([\w<>,\s\[\]]+?)\s+([A-Za-z_][\w]*)\s*\([^\)]*\)\s*(?:\{|$)", RegexOptions.IgnoreCase);
        if (csMethod.Success)
        {
            var mods = csMethod.Groups[1].Value;
            var ret = csMethod.Groups[2].Value.Trim();
            var name = csMethod.Groups[3].Value;
            var hasAsync = Regex.IsMatch(l, @"\basync\b", RegexOptions.IgnoreCase);
            string head;
            if (!string.IsNullOrWhiteSpace(mods))
                head = mods + (hasAsync ? " async " : " ") + ret + " " + name;
            else
                head = (hasAsync ? "async " : "") + ret + " " + name;
            head = Regex.Replace(head, @"\t+|\s{2,}", " ").Trim();
            signature = head + "() { ... }";
            return true;
        }

        // TypeScript / JS: export interface/class
        var tsDecl = Regex.Match(l, @"^\s*(export\s+)?(interface|class)\s+([A-Za-z_][\w]*)", RegexOptions.IgnoreCase);
        if (tsDecl.Success)
        {
            var expo = tsDecl.Groups[1].Value;
            var kind = tsDecl.Groups[2].Value;
            var name = tsDecl.Groups[3].Value;
            signature = (expo + kind + " " + name).Trim() + " { ... }";
            return true;
        }

        // TypeScript / JS method (async optional)
        var tsMethod = Regex.Match(l, @"^\s*(async\s+)?([A-Za-z_][\w]*)\s*\([^\)]*\)\s*(:\s*[\w<>,\s\[\]]+)?\s*\{?", RegexOptions.IgnoreCase);
        if (tsMethod.Success)
        {
            var async = tsMethod.Groups[1].Value;
            var name = tsMethod.Groups[2].Value;
            signature = (async + name).Trim() + "() { ... }";
            return true;
        }

        // Python def/class
        var pyDef = Regex.Match(l, @"^\s*def\s+([A-Za-z_][\w]*)\s*\([^\)]*\)\s*:\s*$", RegexOptions.IgnoreCase);
        if (pyDef.Success)
        {
            signature = $"def {pyDef.Groups[1].Value}() {{ ... }}";
            return true;
        }
        var pyClass = Regex.Match(l, @"^\s*class\s+([A-Za-z_][\w]*)(?:\([^\)]*\))?\s*:\s*$", RegexOptions.IgnoreCase);
        if (pyClass.Success)
        {
            signature = $"class {pyClass.Groups[1].Value}() {{ ... }}";
            return true;
        }

        // Go: func (receiver) Name(...)
        var goFunc = Regex.Match(l, @"^\s*func\s*(?:\(([^\)]*)\)\s*)?([A-Za-z_][\w]*)\s*\(", RegexOptions.IgnoreCase);
        if (goFunc.Success)
        {
            var recv = goFunc.Groups[1].Value?.Trim();
            var name = goFunc.Groups[2].Value;
            signature = (string.IsNullOrEmpty(recv) ? $"func {name}" : $"func ({recv}) {name}") + "() { ... }";
            return true;
        }

        // Rust: pub fn / fn
        var rustFn = Regex.Match(l, @"^\s*(pub\s+)?fn\s+([A-Za-z_][\w]*)\s*\(", RegexOptions.IgnoreCase);
        if (rustFn.Success)
        {
            var pub = rustFn.Groups[1].Value;
            var name = rustFn.Groups[2].Value;
            signature = (pub + "fn " + name).Trim() + "() { ... }";
            return true;
        }

        // Fallback: detect function-like lines with parentheses but avoid single-word lines
        var funcLike = Regex.Match(l, @"^\s*([A-Za-z_][\w]*)\s*\([^\)]*\)\s*\{?\s*$");
        if (funcLike.Success)
        {
            var name = funcLike.Groups[1].Value;
            if (string.Equals(name, "func", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "pub", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "export", StringComparison.OrdinalIgnoreCase))
                return false;
            signature = name + "() { ... }";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Test helper: exposes the internal TryNormalizeSkeletonSignature for unit tests.
    /// Returns true when the line can be normalized into a compact skeleton signature.
    /// </summary>
    public static bool NormalizeSkeletonSignatureForTest(string line, out string signature) => TryNormalizeSkeletonSignature(line, out signature);

    /// <summary>
    /// Scans a range of lines for class, method, and property signatures, 
    /// returning a condensed "skeleton" view for the LLM.
    /// </summary>
    public static string GetSkeletonForRange(string[] allLines, int start, int end)
    {
        if (start >= end) return "";
        var skeleton = new StringBuilder();

        int omittedCount = 0;
        for (int i = start; i < end; i++)
        {
            var line = allLines[i];
            if (TryNormalizeSkeletonSignature(line, out var normalized))
            {
                if (omittedCount > 0)
                {
                    skeleton.AppendLine($"... [{omittedCount} lines omitted]");
                    omittedCount = 0;
                }
                skeleton.AppendLine(normalized);
            }
            else
            {
                omittedCount++;
            }
        }
        if (omittedCount > 0) skeleton.AppendLine($"... [{omittedCount} lines omitted]");

        return skeleton.ToString();
    }
    public static IEnumerable<string> GeneratePlanJsonCandidates(string json)
    {
        // Candidate 1 — as-is
        yield return json;

        // Candidate 2 — quote unquoted keys
        var quoted = Regex.Replace(json,
            @"(?<=[{,])\s*([a-zA-Z_$][\w$]*)\s*(?=:)",
            m => m.Value.Replace(m.Groups[1].Value, $"\"{m.Groups[1].Value}\""));
        if (quoted != json) yield return quoted;

        // Candidate 3 — escape bare newlines inside string values
        var repaired = RepairJsonStringValues(json);
        if (repaired != null && repaired != json) yield return repaired;

        // Candidate 4 — both repairs combined
        if (repaired != null && repaired != json)
        {
            var both = Regex.Replace(repaired,
                @"(?<=[{,])\s*([a-zA-Z_$][\w$]*)\s*(?=:)",
                m => m.Value.Replace(m.Groups[1].Value, $"\"{m.Groups[1].Value}\""));
            if (both != repaired) yield return both;
        }

        // ── NEW: Candidate 5 — full repair (handles raw newlines AND unescaped quotes)
        var fullyRepaired = RepairJsonString(json);
        if (fullyRepaired != null) yield return fullyRepaired;

        // ── NEW: Candidate 6 — full repair + quote unquoted keys
        if (fullyRepaired != null)
        {
            var quotedFull = Regex.Replace(fullyRepaired,
                @"(?<=[{,])\s*([a-zA-Z_$][\w$]*)\s*(?=:)",
                m => m.Value.Replace(m.Groups[1].Value, $"\"{m.Groups[1].Value}\""));
            if (quotedFull != fullyRepaired) yield return quotedFull;
        }
        // Candidate 7 — truncation repair (closes missing brackets/strings)
        var truncFixed = TryRepairTruncatedPlanJson(json);
        if (truncFixed != null && truncFixed != json) yield return truncFixed;

        // Candidate 8 — truncation repair + full string repair combined
        if (truncFixed != null)
        {
            var truncAndRepaired = RepairJsonString(truncFixed);
            if (truncAndRepaired != null && truncAndRepaired != truncFixed)
                yield return truncAndRepaired;
        }
    }

    public static int EstimateTokens(string text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Length / 4; 

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
    /// <summary>
    /// Repairs JSON that was cut off mid-stream by closing unclosed strings and brackets.
    /// Strategy A: close from current position and re-parse.
    /// Strategy B: cut back to the last complete plan step and close with ]}.
    /// Returns the repaired string if it parses as a plan, null if unrecoverable.
    /// </summary>
    public static string? TryRepairTruncatedPlanJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var stack = new Stack<char>();
        var inString = false;
        var lastPlanItemEnd = -1; // char index after the last complete plan-step closing }

        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; } // skip escaped char
                if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c is '{' or '[') { stack.Push(c); continue; }
            if (c == '}' && stack.Count > 0 && stack.Peek() == '{')
            {
                stack.Pop();
                // depth 2 = inside outer-object → plan-array → just closed a step object
                if (stack.Count == 2) lastPlanItemEnd = i + 1;
                continue;
            }
            if (c == ']' && stack.Count > 0 && stack.Peek() == '[') stack.Pop();
        }

        // Already balanced
        if (stack.Count == 0 && !inString) return null;

        var parseOpts = new JsonDocumentOptions { AllowTrailingCommas = true };

        bool IsPlan(string s)
        {
            try
            {
                using var doc = JsonDocument.Parse(s, parseOpts);
                return doc.RootElement.TryGetProperty("plan", out var arr)
                       && arr.ValueKind == JsonValueKind.Array
                       && arr.GetArrayLength() > 0;
            }
            catch { return false; }
        }

        // ── Strategy A: close from current position ──────────────────────────────
        {
            var sb = new StringBuilder(raw.TrimEnd());
            if (inString) sb.Append('"');                          // close open string
            while (sb.Length > 0 && sb[^1] is ',' or ':')         // trim trailing noise
                sb.Remove(sb.Length - 1, 1);
            foreach (var ch in stack)                              // close brackets
                sb.Append(ch == '{' ? '}' : ']');

            var candidate = sb.ToString();
            if (IsPlan(candidate)) return candidate;

            // Also try with string-value escaping on top
            var escaped = RepairJsonStringValues(candidate);
            if (escaped != null && IsPlan(escaped)) return escaped;

            // And full repair (handles unescaped inner quotes from C# code)
            var fullyRepaired = RepairJsonString(candidate);
            if (fullyRepaired != null && IsPlan(fullyRepaired)) return fullyRepaired;
        }

        // ── Strategy B: cut to last complete plan item, close with ]} ────────────
        if (lastPlanItemEnd > 0)
        {
            var cut = raw[..lastPlanItemEnd].TrimEnd(',', ' ', '\t', '\r', '\n') + "]}";
            if (IsPlan(cut)) return cut;

            var cutRepaired = RepairJsonString(cut);
            if (cutRepaired != null && IsPlan(cutRepaired)) return cutRepaired;
        }

        return null;
    }

    private static readonly HashSet<string> _whitespaceSignificantExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".pyi", ".pyw",
        ".yaml", ".yml",
        ".coffee",
        ".haml", ".slim", ".pug", ".jade",
        ".fs", ".fsx", ".fsi",
        ".nim",
        ".sass",
        ".hs", ".lhs",          // Haskell
        ".elm",                 // Elm
        ".ml", ".mli",          // OCaml (off-side rule)
    };
    private static readonly HashSet<string> _endKeywordLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        ".rb",                              // Ruby:   def/end
        ".lua",                             // Lua:    function/end
        ".ex", ".exs",                      // Elixir: do/end
        ".sh", ".bash", ".zsh", ".fish",    // Shell:  if/fi, for/done, case/esac
    };

    private static string ResolveWorkspaceRoot(IConfiguration _config, IWebHostEnvironment _env)
    {
        var configuredRoot = _config.GetValue<string>("Editor:WorkspaceRoot");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            return Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.GetFullPath(Path.Combine(_env.ContentRootPath, configuredRoot));
        return Path.GetFullPath(Path.Combine(_env.ContentRootPath, ".."));
    }

    public static string GetProjectRoot(string project, IConfiguration _config, IWebHostEnvironment _env)
    {
        var workspaceRoot = ResolveWorkspaceRoot(_config, _env);
        var projectSegment = string.IsNullOrWhiteSpace(project) ? "" :
            project.Trim().TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(workspaceRoot, projectSegment));
    }

    public static async Task<(StringBuilder fileContents, string warn)> GetReplanFileContents(List<object> executedSteps, string projectRoot, List<string>? attachedFiles, CancellationToken ct)
    { 
        var fileContents = new StringBuilder();
        var pathsToRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string warn = "";
        foreach (var step in executedSteps.OfType<Dictionary<string, object?>>())
        {
            var p = step.GetValueOrDefault("path")?.ToString();
            if (!string.IsNullOrWhiteSpace(p)) pathsToRead.Add(p.Replace('\\', '/'));
        }
        if (attachedFiles != null) { foreach (var f in attachedFiles) pathsToRead.Add(f.Replace('\\', '/')); }

        foreach (var relPath in pathsToRead)
        {
            if (string.IsNullOrWhiteSpace(relPath)) continue;
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(
                    Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch
            { 
                continue;
            }
            if (!System.IO.File.Exists(fullPath)) continue;
            if (!IsPathUnderRoot(fullPath, projectRoot)) continue;

            try
            {
                var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
 
                const int MaxCharsPerFile = 8000;
                if (content.Length > MaxCharsPerFile)
                    content = content[..MaxCharsPerFile]
                              + $"\n… (truncated — full file is {content.Length} chars)";

                fileContents.AppendLine($"### {relPath}");
                fileContents.AppendLine("```");
                fileContents.AppendLine(content);
                fileContents.AppendLine("```");
                fileContents.AppendLine();
            }
            catch (Exception ex)
            { 
                warn = $"Replan: could not read {relPath} for context: {ex.Message}";
            }
        }

        return (fileContents, warn);
    }

    /// <summary>
    /// Returns a platform-agnostic sandbox directory for benchmark tasks.
    /// Windows: %USERPROFILE%\Desktop\benchmark_sandbox
    /// Linux:   ~/Desktop/benchmark_sandbox  (or ~/benchmark_sandbox if no Desktop)
    /// macOS:   ~/Desktop/benchmark_sandbox
    /// </summary>
    public static string GetBenchmarkSandboxPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var root = !string.IsNullOrEmpty(desktop) ? desktop : Path.Combine(home, "Desktop");
        if (!Directory.Exists(root))
            root = home;
        return Path.Combine(root, "benchmark_sandbox");
    }

    public static bool IsWhitespaceSignificant(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var ext = Path.GetExtension(filePath);
        return _whitespaceSignificantExts.Contains(ext) || _endKeywordLanguages.Contains(ext);
    }
    public static bool IsAngularTemplate(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length < 20)
            return false;
        return Regex.IsMatch(content, @"\*ng(If|For|Switch)") ||
               Regex.IsMatch(content, @"\(click\)|\(change\)|\(keydown\)|\(submit\)|\(focus\)|\(blur\)") ||
               (content.Contains("{{") && content.Contains("}}"));
    }
    public static string? FindLastReturnLine(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        var lines = code.Split('\n', StringSplitOptions.None);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("return ") && trimmed.EndsWith(";"))
                return lines[i];
        }
        return null;
    }


    public static List<string> FindExistingTestFiles(string projectRoot)
    {
        var patterns = new[] { "*Test*.cs", "*Tests.cs", "*.Specs.cs", "*.specs.cs" };
        var dirs = new[] { "test", "tests", "Test", "Tests" };
        var result = new List<string>();

        foreach (var p in patterns)
        {
            try
            {
                result.AddRange(Directory.EnumerateFiles(projectRoot, p, SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\") && !f.Contains("\\node_modules\\") && !f.Contains("\\.git\\")));
            }
            catch { }
        }

        foreach (var d in dirs)
        {
            var dp = Path.Combine(projectRoot, d);
            if (Directory.Exists(dp))
            {
                try { result.AddRange(Directory.EnumerateFiles(dp, "*.cs", SearchOption.AllDirectories)); }
                catch { }
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool FileContains(string filePath, params string[] keywords)
    {
        try
        {
            using var sr = new StreamReader(filePath, Encoding.UTF8);
            var header = sr.ReadToEnd();
            return keywords.Any(k => header.Contains(k, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    public static string FindOrDetermineTestDir(string projectRoot, List<string> existingTestFiles)
    {
        if (existingTestFiles.Count > 0)
        {
            var dir = Path.GetDirectoryName(existingTestFiles[0]);
            if (dir != null) return dir;
        }
        return Path.Combine(projectRoot, "tests");
    }

    public static string GetTestFilePath(string projectRoot, string sourceFilePath, string testDir)
    {
        var fileName = Path.GetFileNameWithoutExtension(sourceFilePath);
        var ext = Path.GetExtension(sourceFilePath);
        return Path.Combine(testDir, $"{fileName}Tests{ext}");
    }

    public static bool IsBuiltinIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;

        // Lowercase keywords / control flow.
        var keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if","for","while","switch","return","using","lock","catch","throw",
            "function","typeof","instanceof","in","of","do","else","try","finally",
            "await","async","yield","new","delete","void","this","super","extends",
            "implements","interface","class","struct","enum","namespace","import",
            "export","from","as","is","out","ref","params","var","let","const",
        };
        if (keywords.Contains(name)) return true;

        var builtins = new HashSet<string>(StringComparer.Ordinal)
        {
            "Math","JSON","Object","Array","String","Number","Boolean","Date",
            "Promise","Map","Set","WeakMap","WeakSet","Symbol","Reflect","Proxy",
            "Error","TypeError","RangeError","SyntaxError","RegExp","Function",
            "Console","console","window","document","globalThis","global",
            "Number","BigInt","Intl","WebAssembly","process","Buffer",
            "Task","List","Dictionary","HashSet","Enumerable","Action","Func",
            "Tuple","ValueTuple","KeyValuePair","Nullable","Convert","Console",
            "Exception","InvalidOperationException","ArgumentException","Guid",
            "DateTime","TimeSpan","StringBuilder","Regex","Encoding","JsonSerializer",
            "Path","File","Directory","Environment","Math","Random","CancellationToken",
            "length", // Added to prevent false positives in the hallucinated property guard
        };
        if (builtins.Contains(name)) return true;

        var standardMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "ToString", "Trim", "TrimStart", "TrimEnd", "Substring", "Split",
            "Replace", "Contains", "StartsWith", "EndsWith", "IndexOf", "LastIndexOf",
            "ToUpper", "ToLower", "Equals", "Compare", "CompareTo", "Concat", "Join",
            "IsNullOrEmpty", "IsNullOrWhiteSpace", "Format", "PadLeft", "PadRight",
            "Select", "Where", "FirstOrDefault", "First", "Last", "LastOrDefault",
            "Any", "All", "Count", "Sum", "Min", "Max", "Average", "ToList",
            "ToArray", "ToDictionary", "ToHashSet", "Distinct", "GroupBy",
            "OrderBy", "OrderByDescending", "ThenBy", "Skip", "Take", "Single",
            "SingleOrDefault", "ElementAt", "Reverse", "Add", "AddRange", "Remove",
            "RemoveAt", "Clear", "ContainsKey", "ContainsValue", "TryGetValue",
            "map", "filter", "reduce", "forEach", "find", "findIndex", "includes",
            "join", "concat", "flat", "flatMap", "some", "every", "sort", "push",
            "pop", "shift", "unshift", "splice", "slice", "stringify", "parse",
            "floor", "ceil", "round", "abs", "min", "max", "pow", "sqrt", "toFixed"
        };
        if (standardMethods.Contains(name)) return true;

        return false;
    }

    public static string ReindentByBraceDepth(string code, string baseIndent, string indentUnit = "  ")
    {
        var lines = code.Split('\n');
        var result = new List<string>();
        var depth = 0;
        var inSQ = false;
        var inDQ = false;
        var inTmpl = false;
        var inVerbatim = false;
        var inLineComment = false;
        var inBlockComment = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                result.Add(line);
                continue;
            }
            if (inVerbatim || inBlockComment)
            {
                result.Add(line);
            }
            else
            {
                var effectiveDepth = trimmed[0] == '}' ? depth - 1 : depth;
                if (effectiveDepth < 0) effectiveDepth = 0;

                var indent = baseIndent + string.Concat(Enumerable.Repeat(indentUnit, effectiveDepth));
                result.Add(indent + trimmed);
            }

            for (var i = 0; i < trimmed.Length; i++)
            {
                var c = trimmed[i];
                var p = i > 0 ? trimmed[i - 1] : '\0';

                if (inLineComment) break;

                if (inBlockComment)
                {
                    if (c == '*' && i + 1 < trimmed.Length && trimmed[i + 1] == '/')
                    {
                        inBlockComment = false;
                        i++;
                    }
                    continue;
                }

                if (inVerbatim)
                {
                    if (c == '"')
                    {
                        if (i + 1 < trimmed.Length && trimmed[i + 1] == '"')
                        {
                            i++;
                        }
                        else
                        {
                            inVerbatim = false;
                        }
                    }
                    continue;
                }

                if (inSQ || inDQ || inTmpl)
                {
                    if (c == '\\' && (inDQ || inTmpl)) { i++; continue; }
                    if (c == '\'' && inSQ) inSQ = false;
                    else if (c == '"' && inDQ) inDQ = false;
                    else if (c == '`' && inTmpl) inTmpl = false;
                    continue;
                }

                if (c == '/' && i + 1 < trimmed.Length && trimmed[i + 1] == '/')
                {
                    inLineComment = true;
                    break;
                }
                if (c == '/' && i + 1 < trimmed.Length && trimmed[i + 1] == '*')
                {
                    inBlockComment = true;
                    i++;
                    continue;
                }
                if (c == '@' && i + 1 < trimmed.Length && trimmed[i + 1] == '"')
                {
                    inVerbatim = true;
                    i++;
                    continue;
                }
                if (c == '\'') { inSQ = true; continue; }
                if (c == '"') { inDQ = true; continue; }
                if (c == '`') { inTmpl = true; continue; }

                if (c == '{') depth++;
                else if (c == '}') depth--;
            }

            inLineComment = false;
            if (depth < 0) depth = 0;
        }

        return string.Join("\n", result);
    }
    public static string? DetectHallucinatedProperties(string oldStr, string newStr, string fileContent, string relPath)
    {
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        if (ext is not (".ts" or ".tsx" or ".js" or ".jsx" or ".cs" or ".vb")) return null;

        var newProps = new HashSet<string>(StringComparer.Ordinal);
        // Match .X in this.X or obj.X
        foreach (Match m in Regex.Matches(newStr, @"\.([A-Za-z_]\w*)", RegexOptions.Compiled))
        {
            var name = m.Groups[1].Value;
            if (!IsBuiltinIdentifier(name)) newProps.Add(name);
        }

        var oldProps = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(oldStr, @"\.([A-Za-z_]\w*)", RegexOptions.Compiled))
        {
            oldProps.Add(m.Groups[1].Value);
        }

        var introducedProps = newProps.Except(oldProps).ToList();
        var trulyInvented = new List<string>();

        var fileWords = new HashSet<string>(fileContent.Split(new[] { ' ', '\n', '\r', '\t', '.', ';', ',', '(', ')', '[', ']', '{', '}', '<', '>', '=', '!', '?', '|', '&', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries));

        foreach (var prop in introducedProps)
        {
            if (Regex.IsMatch(newStr, $@"\b{Regex.Escape(prop)}\s*[:=]")) { continue; }
            if (fileWords.Contains(prop)) { continue; }

            var existingSimilar = fileWords.FirstOrDefault(w =>
                (w.Length > 3) &&
                ((w + "s" == prop) || (w + "es" == prop) ||
                 (prop + "s" == w) || (prop + "es" == w) ||
                 (w + "Array" == prop) || (w + "List" == prop) ||
                 (prop + "Array" == w) || (prop + "List" == w)));

            if (existingSimilar != null)
            {
                trulyInvented.Add($"{prop} (did you mean '{existingSimilar}'?)");
            }
        }

        if (trulyInvented.Count > 0)
        {
            var preview = string.Join(", ", trulyInvented.Take(5));
            return $"HALLUCINATED PROPERTY — newString references [{preview}] which do NOT appear anywhere in {relPath}. " +
                   "The LLM invented properties by modifying the name of existing properties (e.g., pluralizing). " +
                   "Use ONLY properties that already appear in the file. If you need a collection, check if the existing singular property can be used, or explicitly declare the new property in the same edit.";
        }

        return null;
    }
    public static async Task<string?> DetectTestFramework(string projectRoot, CancellationToken ct)
    {
        try
        {
            foreach (var csproj in Directory.EnumerateFiles(projectRoot, "*.csproj", SearchOption.AllDirectories))
            {
                var content = await System.IO.File.ReadAllTextAsync(csproj, Encoding.UTF8, ct);
                if (content.Contains("xunit", StringComparison.OrdinalIgnoreCase)) return "xunit";
                if (content.Contains("nunit", StringComparison.OrdinalIgnoreCase)) return "nunit";
                if (content.Contains("MSTest", StringComparison.OrdinalIgnoreCase)) return "mstest";
            }
        }
        catch { }
        return null;
    }
    public static (string family, bool supportsFormatC, string llmHint) GetLanguageProfile(string ext)
    {
        return ext.ToLowerInvariant() switch
        {
            // ── C# (Roslyn AST) ────────────────────────────────────────────────────
            ".cs" => ("brace", true,
                "⚠ C# FILE: " +
                "USE FORMAT C (targetType/targetName/newCode) for FULL METHOD replacements or to ADD a new method (via insertAfter:true). " +
                "For SMALL targeted edits (1-5 lines, e.g. adding a field/property, changing a return value): " +
                "USE oldString/newString. This is the ONLY safe way to add properties/fields. " +
                "Do NOT use targetType='class' to add properties/fields. " +
                "INDENTATION: method signature at class-member level, body indented 4 spaces more."),

            // ── TypeScript / JavaScript ────────────────────────────────────────────
            ".ts" or ".tsx" => ("brace", true,
                "⚠ TS FILE: preserve ALL indentation exactly. Methods inside a class MUST be indented. " +
                "Preserve inline formatting: keep a space after colons in object literals ({key: value}) " +
                "and after commas in arrays/objects. " +
                "You can use FORMAT C (targetType='method', targetName='name') for full method replacements. " +
                "For small targeted edits (< 10 lines) prefer oldString/newString." +
                " Do NOT use targetType='class' — class REPLACE is blocked for .ts files. " +
                "Use insertAfter:true with targetType='method' to add methods, " +
                "or oldString/newString to add properties."),
            ".js" or ".jsx" => ("brace", true,
                "⚠ JS FILE: preserve ALL indentation exactly. " +
                "Preserve inline formatting: keep a space after colons in object literals ({key: value}) " +
                "and after commas in arrays/objects. " +
                "FORMAT C supported (targetType='function'/'method', targetName='name'). " +
                "For small edits prefer oldString/newString."),

            // ── JVM / CLR family (brace-based, regex-targeted) ────────────────────
            ".java" => ("brace", true,
                "⚠ JAVA FILE: brace-based, similar to C#. " +
                "FORMAT C supported: targetType='method'/'class'/'interface'. " +
                "Preserve ALL annotations (@Override, @Autowired, etc.) exactly. " +
                "NEVER alter generic type parameters or throws clauses."),
            ".kt" or ".kts" => ("brace", true,
                "⚠ KOTLIN FILE: brace-based. FORMAT C supported: targetType='function'/'class'. " +
                "Preserve data class properties, suspend/inline/override modifiers, and lambda syntax exactly."),
            ".scala" => ("brace", true,
                "⚠ SCALA FILE: brace-based. FORMAT C supported: targetType='method'/'class'/'object'. " +
                "Preserve implicits, case class syntax, and for-comprehension indentation exactly."),

            // ── Go ─────────────────────────────────────────────────────────────────
            ".go" => ("brace", true,
                "⚠ GO FILE: brace-based, uses TABS (not spaces) for indentation — never convert tabs to spaces. " +
                "FORMAT C supported: targetType='function', targetName='FunctionName'. " +
                "Preserve ALL error-handling idioms (if err != nil), defer statements, and goroutine patterns."),

            // ── Systems languages ──────────────────────────────────────────────────
            ".rs" => ("brace", true,
                "⚠ RUST FILE: brace-based. FORMAT C supported: targetType='function'/'impl'. " +
                "Preserve ALL lifetime annotations ('a), borrow markers (&, &mut), " +
                "ownership semantics, trait bounds, and match arm patterns EXACTLY."),
            ".c" or ".cpp" or ".cc" or ".cxx" or ".h" or ".hpp" => ("brace", false,
                "⚠ C/C++ FILE: brace-based, preprocessor directives must stay on their own line. " +
                "Use oldString/newString. Preserve #include order, extern \"C\", " +
                "template parameters, and pointer/reference syntax exactly."),
            ".swift" => ("brace", true,
                "⚠ SWIFT FILE: brace-based. FORMAT C supported: targetType='function'/'class'/'struct'. " +
                "Preserve access modifiers (open/public/internal/fileprivate/private), " +
                "property wrappers (@State, @Binding), and optional chaining exactly."),

            // ── Scripting / dynamic ────────────────────────────────────────────────
            ".php" => ("brace", true,
                "⚠ PHP FILE: brace-based. FORMAT C supported: targetType='function'/'method'/'class'. " +
                "Preserve $ sigils on all variables, type hints, and nullable ? modifiers exactly."),
            ".dart" => ("brace", true,
                "⚠ DART FILE: brace-based. FORMAT C supported: targetType='function'/'class'. " +
                "Preserve async/await, null-safety operators (?., ??, ??=, !), and Widget tree indentation."),
            ".groovy" => ("brace", true,
                "⚠ GROOVY FILE: brace-based (Gradle/Groovy DSL). FORMAT C supported: targetType='method'. " +
                "Preserve closure syntax { ... }, GString interpolation, and Gradle DSL patterns."),

            // ── End-keyword languages (def/end, if/fi, function/end) ──────────────
            ".rb" => ("end-keyword", true,
                "⚠ RUBY FILE: uses def/end, do/end, class/end block terminators — NOT braces. " +
                "FORMAT C supported: targetType='method', targetName='method_name' (snake_case). " +
                "Use oldString/newString for small edits. " +
                "Preserve Ruby idioms: ||=, &., symbol literals, and block/proc/lambda syntax."),
            ".lua" => ("end-keyword", false,
                "⚠ LUA FILE: uses function/end, if/end, for/end block terminators — NOT braces. " +
                "Use oldString/newString only. Preserve Lua table syntax, colon-method calls (:), " +
                "and global vs local variable scoping."),
            ".ex" or ".exs" => ("end-keyword", false,
                "⚠ ELIXIR FILE: uses do/end block terminators and pipe operators |>. " +
                "Use oldString/newString. Preserve pattern matching, atoms (:name), " +
                "and module attribute syntax (@doc, @spec)."),
            ".sh" or ".bash" or ".zsh" or ".fish" => ("end-keyword", false,
                "⚠ SHELL SCRIPT: uses if/fi, for/done, while/done, case/esac terminators. " +
                "Use oldString/newString. Preserve $() vs ``, quoting rules, " +
                "and test [ ] vs [[ ]] distinctions exactly."),
            ".ps1" or ".psm1" or ".psd1" => ("brace", false,
                "⚠ POWERSHELL FILE: brace-based, $Variables, -Flags syntax. " +
                "Use oldString/newString. Preserve $_ pipeline variable, " +
                "cmdlet verb-noun naming, and parameter attribute syntax."),

            // ── Whitespace-significant / indent-based ─────────────────────────────
            ".py" or ".pyi" => ("indent", false,
                "⚠ PYTHON FILE: indentation IS the syntax — do NOT alter indent levels. " +
                "Use oldString/newString only. FORMAT C is NOT supported. " +
                "Copy every leading space/tab from the file exactly into oldString and newString. " +
                "Preserve type hints, decorators (@), and docstring quotes."),
            ".yaml" or ".yml" => ("indent", false,
                "⚠ YAML FILE: whitespace-significant. Use oldString/newString only. " +
                "NEVER change indentation levels — copy exactly. " +
                "Preserve anchors (&), aliases (*), and multiline block styles (|, >)."),
            ".fs" or ".fsx" or ".fsi" => ("indent", false,
                "⚠ F# FILE: whitespace-significant (offside rule). Use oldString/newString only. " +
                "Preserve pipeline |> operators, computation expressions, and discriminated union syntax."),
            ".hs" or ".lhs" => ("indent", false,
                "⚠ HASKELL FILE: whitespace-significant. Use oldString/newString only. " +
                "Preserve do-notation alignment, type class instances, and where-clause indentation."),
            ".coffee" => ("indent", false,
                "⚠ COFFEESCRIPT FILE: whitespace-significant, no braces. Use oldString/newString only."),

            // ── Tag / markup ───────────────────────────────────────────────────────
            ".html" or ".htm" => ("tag", false,
                "⚠ HTML FILE: tag-based indentation — child elements MUST be indented more than parent. " +
                "Use oldString/newString. Preserve attribute quoting, void element self-closing, " +
                "and Angular/Vue directive syntax exactly."),
            ".xml" or ".xaml" or ".axaml" => ("tag", false,
                "⚠ XML FILE: tag-based. Use oldString/newString. " +
                "Preserve namespace prefixes (xmlns:), attribute order, and CDATA sections."),
            ".cshtml" or ".razor" => ("tag", false,
                "⚠ RAZOR FILE: HTML with @C# expressions. Use oldString/newString. " +
                "Preserve @model, @inject, @Html.* helpers, and @{ } code blocks exactly."),
            ".vue" => ("tag", true,
                "⚠ VUE FILE: <template>/<script>/<style> sections. " +
                "FORMAT C supported for methods inside <script>. " +
                "For template changes use oldString/newString. Preserve v-bind/:, v-on/@, v-model directives."),
            ".svelte" => ("tag", false,
                "⚠ SVELTE FILE: <script>/<style>/template sections. Use oldString/newString. " +
                "Preserve $: reactive declarations, {#if}, {#each} blocks, and slot syntax."),
            ".svg" => ("tag", false,
                "⚠ SVG FILE: XML tag-based. Use oldString/newString. " +
                "Preserve viewBox, transform attributes, and path d= values exactly."),

            // ── Stylesheets ────────────────────────────────────────────────────────
            ".css" or ".scss" or ".less" => ("brace", false,
                "⚠ CSS/SCSS/LESS FILE: brace-based selectors. Use oldString/newString. " +
                "CRITICAL: oldString MUST be at most 4 lines — never replace an entire CSS block. " +
                "To change a CSS property value, set oldString to the ONE line containing that property " +
                "(copied verbatim from the file), and newString to that line with the new value. " +
                "Example: if changing `flex-direction: row;` to `flex-direction: column;`, " +
                "oldString = \"  flex-direction: row;\" (exact whitespace), newString = \"  flex-direction: column;\". " +
                "Preserve ALL whitespace in property values (e.g. '0 1px 2px rgba(0,0,0,0.5)' — " +
                "every space and comma is significant). Preserve SCSS variables ($var), mixins, and nesting."),

            // ── Config / data ──────────────────────────────────────────────────────
            ".json" => ("config", false,
                "⚠ JSON FILE: strict syntax — use oldString/newString only. " +
                "NO trailing commas, NO comments. Preserve ALL nested object structure exactly. " +
                "When editing arrays, include the full surrounding element for uniqueness."),
            ".toml" => ("config", false,
                "⚠ TOML FILE: use oldString/newString. Preserve [section] headers, " +
                "[[array-of-tables]], and inline table {key=val} syntax exactly."),
            ".env" or ".ini" => ("config", false,
                "⚠ CONFIG FILE: key=value pairs. Use oldString/newString. " +
                "Preserve comment lines (#) and section headers ([section]) exactly."),
            ".proto" => ("brace", false,
                "⚠ PROTOBUF FILE: brace-based. Use oldString/newString. " +
                "Preserve field numbers, oneof blocks, and option statements exactly."),

            // ── Query / data ───────────────────────────────────────────────────────
            ".sql" => ("plain", false,
                "⚠ SQL FILE: use oldString/newString. Preserve ALL whitespace in multi-line queries. " +
                "Match exact keyword casing (uppercase SQL keywords are conventional). " +
                "Preserve semicolons and comment styles (-- vs /* */)."),
            ".graphql" or ".gql" => ("plain", false,
                "⚠ GRAPHQL FILE: use oldString/newString. Preserve type definitions, " +
                "field arguments, and directive (@deprecated, @skip) syntax exactly."),

            // ── Documentation ──────────────────────────────────────────────────────
            ".md" or ".mdx" => ("plain", false,
                "⚠ MARKDOWN FILE: use oldString/newString. " +
                "Preserve heading levels (# vs ##), list markers (-, *, 1.), " +
                "and fenced code block language tags exactly."),
            ".rst" => ("indent", false,
                "⚠ RST FILE: indentation-significant section underlines. Use oldString/newString. " +
                "Preserve directive syntax (.. directive::) and role syntax (:role:`text`)."),

            // ── Default ────────────────────────────────────────────────────────────
            _ => ("plain", false,
                "⚠ Preserve ALL indentation and whitespace exactly as shown in the file. " +
                "Use oldString/newString. Copy every leading space/tab character-for-character.")
        };
    }
    public static string ReindentToLevel(string code, string indent)
    {
        if (string.IsNullOrEmpty(code)) return code;
        var lines = code.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
                lines[i] = indent + lines[i].TrimStart();
        }
        return string.Join("\n", lines);
    }

    public static string StripClassWrapper(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return code;
        var lines = code.Split('\n').ToList();
        // Remove leading "export class X {" or "class X {" lines
        while (lines.Count > 0)
        {
            var trimmed = lines[0].Trim();
            if (trimmed.Length == 0 ||
                Regex.IsMatch(trimmed, @"^(export\s+)?(default\s+)?(abstract\s+)?class\s+\w+"))
            {
                lines.RemoveAt(0);
            }
            else break;
        }
        while (lines.Count > 0)
        {
            var trimmed = lines[^1].Trim();
            if (trimmed == "}" || trimmed.Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }
            else break;
        }
        return string.Join("\n", lines);
    }
    public static string UnescapeString(string s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        return s.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
    } 
    public static List<string> ExtractDisambiguationKeywords(string? changeDesc)
    {
        if (string.IsNullOrWhiteSpace(changeDesc)) return new List<string>();
        var stopWords = new HashSet<string> {
        "from", "remove", "delete", "update", "method", "function", "class",
        "property", "field", "variable", "code", "block", "line", "target",
        "change", "modify", "replace", "insert", "create", "implement",
        "ensure", "make", "file", "edit", "add", "element", "span", "div"
    };

        return Regex.Matches(changeDesc.ToLowerInvariant(), @"\b[a-z]{4,}\b")
            .Select(m => m.Value)
            .Where(w => !stopWords.Contains(w))
            .Distinct()
            .ToList();
    }

    public static string? ExtractMostUniqueLine(string oldStr, string fileContent)
    {
        var normFile = AgentUtilities.NormalizeLineEndings(fileContent);
        var oldLines = oldStr.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (oldLines.Count <= 1) return null;

        string? bestLine = null;
        int bestCount = int.MaxValue;

        foreach (var line in oldLines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 15) continue; // skip short lines like </div>
            var count = normFile.Split(new[] { trimmed }, StringSplitOptions.None).Length - 1;
            if (count < bestCount)
            {
                bestCount = count;
                bestLine = line;
            }
        }
        return bestLine;
    }
    public static string? ExtractFullHtmlBlock(string fileContent, string oldStr)
    {
        var firstLine = oldStr.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.TrimStart();
        if (string.IsNullOrEmpty(firstLine) || !firstLine.StartsWith("<")) return null;

        var tagMatch = Regex.Match(firstLine, @"^<([a-zA-Z0-9]+)");
        if (!tagMatch.Success) return null;
        var tagName = tagMatch.Groups[1].Value;

        var normFile = AgentUtilities.NormalizeLineEndings(fileContent);
        var startIdx = normFile.IndexOf(firstLine, StringComparison.Ordinal);
        if (startIdx < 0) return null;

        var depth = 0;
        var pos = startIdx;
        while (pos < normFile.Length)
        {
            var nextOpen = normFile.IndexOf($"<{tagName}", pos, StringComparison.OrdinalIgnoreCase);
            var nextClose = normFile.IndexOf($"</{tagName}>", pos, StringComparison.OrdinalIgnoreCase);

            if (nextClose < 0) return null;

            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                // Verify it's a real tag, not just a substring
                var charAfter = nextOpen + tagName.Length + 1 < normFile.Length
                    ? normFile[nextOpen + tagName.Length + 1]
                    : '\0';
                if (charAfter == ' ' || charAfter == '>' || charAfter == '\t' || charAfter == '\n' || charAfter == '\r')
                {
                    depth++;
                }
                pos = nextOpen + tagName.Length + 1;
            }
            else
            {
                if (depth <= 0)
                {
                    var endIdx = nextClose + tagName.Length + 3;
                    return normFile.Substring(startIdx, endIdx - startIdx);
                }
                depth--;
                pos = nextClose + tagName.Length + 3;
            }
        }
        return null;
    }
    public static int ComputeLevenshteinDistance(string a, string b)
    {
        var m = a.Length; var n = b.Length;
        if (m == 0) return n; if (n == 0) return m;
        var d = new int[n + 1];
        for (var i = 0; i <= n; i++) d[i] = i;
        for (var i = 1; i <= m; i++)
        {
            var prev = d[0]; d[0] = i;
            for (var j = 1; j <= n; j++)
            {
                var temp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1),
                    prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = temp;
            }
        }
        return d[n];
    }
    public static string ReconstructFromVerbatimDiff(string verbatimBlock, string llmNewStr)
    {
        if (string.IsNullOrEmpty(verbatimBlock) || string.IsNullOrEmpty(llmNewStr))
            return llmNewStr ?? "";

        var verbatimLines = verbatimBlock.Split('\n');
        var newLines = llmNewStr.Split('\n');

        var newToVerbatim = LcsAlign(verbatimLines, newLines);

        var result = new List<string>(newLines.Length);
        for (var j = 0; j < newLines.Length; j++)
        {
            if (newToVerbatim[j] >= 0)
                result.Add(verbatimLines[newToVerbatim[j]]);
            else
                result.Add(newLines[j]);
        }

        var reconstructed = string.Join("\n", result);
        if (reconstructed.Split('\n').Length < newLines.Length)
            return llmNewStr;

        return reconstructed;
    }

    private static int[] LcsAlign(string[] a, string[] b)
    {
        var n = a.Length;
        var m = b.Length;
        var dp = new int[n + 1, m + 1];

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                if (LinesMatchPrefixTolerant(a[i - 1], b[j - 1]))
                    dp[i, j] = dp[i - 1, j - 1] + 1;
                else
                    dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
            }
        }

        var newToVerbatim = new int[m];
        for (var j = 0; j < m; j++) newToVerbatim[j] = -1;

        var ii = n;
        var jj = m;
        while (ii > 0 && jj > 0)
        {
            if (LinesMatchPrefixTolerant(a[ii - 1], b[jj - 1]))
            {
                newToVerbatim[jj - 1] = ii - 1;
                ii--; jj--;
            }
            else if (dp[ii - 1, jj] >= dp[ii, jj - 1])
                ii--;
            else
                jj--;
        }

        return newToVerbatim;
    }

    private static bool LinesMatchPrefixTolerant(string x, string y)
    {
        var xt = x.Trim();
        var yt = y.Trim();
        if (xt.Length == 0 || yt.Length == 0)
            return xt.Length == 0 && yt.Length == 0;
        return xt == yt
            || (xt.Length >= yt.Length && xt.StartsWith(yt, StringComparison.Ordinal))
            || (yt.Length >= xt.Length && yt.StartsWith(xt, StringComparison.Ordinal));
    }
    public static AgentPlan DeduplicatePlan(AgentPlan? plan)
    {
        if (plan?.Plan == null || plan.Plan.Count == 0)
            return plan!;

        var seen = new HashSet<string>();
        var unique = new List<PlanStep>();

        foreach (var step in plan.Plan)
        {
            var key = step.File + "\n" + step.OldString + "\n" + step.NewString + "\n" + step.Change;

            if (!seen.Contains(key))
            {
                seen.Add(key);
                unique.Add(step);
            }
        }

        plan.Plan = unique;
        return plan;
    }

    public static string? DetectDuplicatePropertyAddition(string oldStr, string newStr)
    {
        string StripStrings(string s)
        {
            s = Regex.Replace(s, @"`[^`]*`", "``", RegexOptions.Singleline);
            s = Regex.Replace(s, @"""[^""]*""", "\"\"", RegexOptions.Singleline);
            s = Regex.Replace(s, @"'[^']*'", "''", RegexOptions.Singleline);
            return s;
        }

        var cleanOld = StripStrings(oldStr);
        var cleanNew = StripStrings(newStr);

        var keyRegex = new Regex(@"^\s*(?:'([^']+)'|""([^""]+)""|(\w+))\s*:", RegexOptions.Multiline);

        var oldCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in keyRegex.Matches(cleanOld))
        {
            var key = (m.Groups[1].Value ?? m.Groups[2].Value ?? m.Groups[3].Value).Trim();
            if (string.IsNullOrEmpty(key)) continue;
            if (!oldCounts.ContainsKey(key)) oldCounts[key] = 0;
            oldCounts[key]++;
        }

        var newCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in keyRegex.Matches(cleanNew))
        {
            var key = (m.Groups[1].Value ?? m.Groups[2].Value ?? m.Groups[3].Value).Trim();
            if (string.IsNullOrEmpty(key)) continue;
            if (!newCounts.ContainsKey(key)) newCounts[key] = 0;
            newCounts[key]++;
        }

        foreach (var kvp in newCounts)
        {
            oldCounts.TryGetValue(kvp.Key, out var oldVal);
            if (kvp.Value > oldVal && kvp.Value > 1)
            {
                return $"DUPLICATE PROPERTY ADDITION — newString contains {kvp.Value} occurrences of property '{kvp.Key}' " +
                       $"but oldString only had {oldVal}. You added a duplicate property instead of modifying the existing one. " +
                       "MODIFY the existing property value instead of adding a new one with the same name. Include the ENTIRE existing backtick string in oldString.";
            }
        }
        return null;
    }

    public static string GetLeadingWhitespace(string s)
    {
        var i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        return s[..i];
    }
    private static readonly HashSet<string> VoidHtmlElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input",
        "link", "meta", "param", "source", "track", "wbr"
    };

    public static string AutoIndentHtml(string html, string baseIndent)
    {
        const string IndentStep = "  "; // 2 spaces per nesting level
        var lines = html.Split('\n');

        // If the content already has relative structure, preserve it exactly
        var distinctDepths = lines
            .Where(l => l.Trim().Length > 0)
            .Select(l => AgentUtilities.GetLeadingWhitespace(l).Length)
            .Distinct().Count();
        if (distinctDepths > 1) return html;

        var depth = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0) continue;

            // Closing tag → dedent BEFORE placing this line
            if (Regex.IsMatch(trimmed, @"^</[\w-]"))
                depth = Math.Max(0, depth - 1);

            lines[i] = baseIndent + new string(' ', depth * IndentStep.Length) + trimmed;

            // Opening tag: indent the NEXT line if this tag doesn't close on the same line
            var tagMatch = Regex.Match(trimmed, @"^<([\w-]+)[\s>]");
            if (tagMatch.Success)
            {
                var tag = tagMatch.Groups[1].Value;
                var isSelfClosing = trimmed.EndsWith("/>") || VoidHtmlElements.Contains(tag);
                var closedInline = trimmed.Contains($"</{tag}>");
                var isClosing = trimmed.StartsWith("</");
                var isComment = trimmed.StartsWith("<!--");
                if (!isSelfClosing && !closedInline && !isClosing && !isComment)
                    depth++;
            }
        }

        return string.Join("\n", lines);
    }
    /// <summary>Auto-indent replacement lines based on brace depth, using the file's indent style.</summary>
    public static string AutoIndentFromFile(string replacement, string fileIndent, string[] fileLines, int start)
    {
        if (!replacement.Contains('{') && !replacement.Contains('}'))
            return replacement;

        // Infer indent size from the file (difference between parent and child indent levels)
        var indentSize = AgentUtilities.InferIndentSize(fileLines, start);
        if (indentSize <= 0) return replacement;

        var lines = replacement.Split('\n');
        var depth = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;

            var trimmed = lines[i].TrimStart();
            // Compute indent depth for THIS line only (don't mutate depth yet)
            var lineDepth = depth;
            if (trimmed.StartsWith("}"))
                lineDepth = Math.Max(0, lineDepth - 1);

            var expectedIndent = fileIndent + new string(' ', lineDepth * indentSize);
            var lineIndent = AgentUtilities.GetLeadingWhitespace(lines[i]);
            if (lineIndent != expectedIndent)
                lines[i] = expectedIndent + trimmed;

            // Update depth for the NEXT line by counting braces in this line
            foreach (var c in trimmed)
            {
                if (c == '{') depth++;
                if (c == '}') depth = Math.Max(0, depth - 1);
            }
        }
        return string.Join("\n", lines);
    }
    /// <summary>Infer the file's indent size (e.g. 2 or 4) by sampling indentation deltas.</summary>
    public static int InferIndentSize(string[] fileLines, int start)
    {
        var sampleStart = Math.Max(0, start - 5);
        var sampleEnd = Math.Min(fileLines.Length, start + 20);
        var deltas = new List<int>();
        for (var i = sampleStart + 1; i < sampleEnd; i++)
        {
            var prev = GetLeadingWhitespace(fileLines[i - 1]).Length;
            var curr = GetLeadingWhitespace(fileLines[i]).Length;
            var delta = Math.Abs(curr - prev);
            if (delta > 0 && delta <= 8)
                deltas.Add(delta);
        }
        if (deltas.Count == 0) return 2; // default
        // Use mode (most common delta) — more reliable than average for mixed indentation
        var mode = deltas.GroupBy(d => d).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
        return mode < 2 ? 2 : mode > 4 ? 4 : mode;
    }

    public static string AutoIndentFullFile(string fullContent, string[] originalLines)
    {
        var indentSize = InferIndentSize(originalLines, 0);
        if (indentSize <= 0) return fullContent;

        var lines = fullContent.Split('\n');
        var depth = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;

            var trimmed = lines[i].TrimStart();
            var lineDepth = depth;
            if (trimmed.StartsWith("}"))
                lineDepth = Math.Max(0, lineDepth - 1);

            var expectedIndent = new string(' ', lineDepth * indentSize);
            var lineIndent = GetLeadingWhitespace(lines[i]);
            if (lineIndent != expectedIndent)
                lines[i] = expectedIndent + trimmed;

            foreach (var c in trimmed)
            {
                if (c == '{') depth++;
                if (c == '}') depth = Math.Max(0, depth - 1);
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>Find the last position where braces are balanced in partial content.</summary>
    public static string FindLastBalancedPrefix(string content)
    {
        var depth = 0;
        var lastBalanced = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '{') depth++;
            if (content[i] == '}') depth = Math.Max(0, depth - 1);
            if (depth == 0) lastBalanced = i + 1;
        }
        return content[..Math.Max(lastBalanced, content.Length / 2)];
    }
    public static bool IsFullFileTruncated(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var opens = content.Count(c => c == '{');
        var closes = content.Count(c => c == '}');
        return opens > closes;
    }
    private static bool LooksLikePlanJson(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        Regex.IsMatch(text, @"""?plan""?\s*:", RegexOptions.IgnoreCase);

    public static AgentPlan? ParsePlan(string jsonString)
    {
        if (string.IsNullOrWhiteSpace(jsonString)) return null;
        var cleaned = jsonString.Trim();
        if (cleaned.StartsWith("```"))
        {
            var fm = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            cleaned = fm.Success ? fm.Groups[1].Value.Trim() : cleaned.TrimStart('`');
        }
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        var truncRepaired = AgentUtilities.TryRepairTruncatedPlanJson(cleaned);
        if (truncRepaired != null)
        {
            var truncOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true };
            foreach (var candidate in AgentUtilities.GeneratePlanJsonCandidates(truncRepaired))
            {
                try
                {
                    var deserializedPlan = JsonSerializer.Deserialize<AgentPlan>(candidate, truncOpts);
                    if (deserializedPlan?.Plan?.Count > 0)
                        return AgentUtilities.DeduplicatePlan(deserializedPlan);
                }
                catch { }
            }
        }
        var jsonBlocks = AgentUtilities.ExtractJsonBlocks(cleaned).Where(LooksLikePlanJson).OrderByDescending(b => b.Length).ToList();
        if (LooksLikePlanJson(cleaned) && cleaned.StartsWith("{"))
        {
            jsonBlocks.Insert(0, cleaned);
        }
        var fb = cleaned.IndexOf('{');
        var lb = cleaned.LastIndexOf('}');
        if (fb >= 0 && lb > fb)
        {
            var bc = cleaned[fb..(lb + 1)];
            if (LooksLikePlanJson(bc))
            {
                jsonBlocks.Add(bc);
            }
        }
        foreach (var candidate in jsonBlocks.Distinct())
        {
            foreach (var repaired in GeneratePlanJsonCandidates(candidate))
            {
                try
                {
                    var result = JsonSerializer.Deserialize<AgentPlan>(repaired, opts);
                    if (result?.Plan != null)
                    {
                        return DeduplicatePlan(result);
                    }
                }
                catch { }
            }
        }
        var arrayCandidates = new List<string> { cleaned };
        var f2 = cleaned.IndexOf('['); var l2 = cleaned.LastIndexOf(']');
        if (f2 >= 0 && l2 > f2) arrayCandidates.Add(cleaned[f2..(l2 + 1)]);
        foreach (var block in arrayCandidates.Distinct())
        {
            try
            {
                var c = block.Trim();
                if (!c.StartsWith("[")) continue;
                var steps = JsonSerializer.Deserialize<List<PlanStep>>(c, opts);
                if (steps is { Count: > 0 }) return new AgentPlan { Summary = "Parsed array", Plan = steps, Score = 0 };
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Parse a plan in delimiter format (replaces JSON for output robustness).
    /// Format:
    ///   <<<THINKING>>>
    ///   analysis text
    ///   <<<SUMMARY>>>
    ///   one line summary
    ///   <<<SCORE>>> 85
    ///   <<<STEP 1>>>
    ///   FILE: relative/path.cs
    ///   CHANGE: description
    ///   <<<OLD>>>
    ///   old code
    ///   <<<NEW>>>
    ///   new code
    ///   <<<STEP END>>>
    ///   <<<STEP 2>>>
    ///   FILE: _command
    ///   CHANGE: cmd
    ///   <<<STEP END>>>
    /// </summary>
    public static AgentPlan? ParseDelimitedPlan(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```"))
        {
            var m = Regex.Match(trimmed, @"```(?:text)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (m.Success) trimmed = m.Groups[1].Value.Trim();
        }

        // Normalize ### STEP N ### to <<<STEP N>>> so both formats use the same downstream regex
        trimmed = Regex.Replace(trimmed, @"###\s*STEP\s*(\d+)\s*###", "<<<STEP $1>>>", RegexOptions.IgnoreCase);
        // Also normalize ###STEPN### (no spaces)
        trimmed = Regex.Replace(trimmed, @"###STEP(\d+)###", "<<<STEP $1>>>", RegexOptions.IgnoreCase);

        var thinking = ExtractDelimitedSection(trimmed, "THINKING");
        var summary = ExtractDelimitedSection(trimmed, "SUMMARY");
        var scoreMatch = Regex.Match(trimmed, @"<<<SCORE>>>\s*(\d+)");
        var score = scoreMatch.Success && int.TryParse(scoreMatch.Groups[1].Value, out var s) ? Math.Clamp(s, 0, 100) : 50;
        var doneMatch = Regex.Match(trimmed, @"<<<DONE>>>\s*(true|false)", RegexOptions.IgnoreCase);
        var complete = doneMatch.Success && bool.TryParse(doneMatch.Groups[1].Value, out var d) && d;

        var steps = new List<PlanStep>();
        var stepPattern = new Regex(@"<<<STEP\s*\d+>>>\s*(.*?)(?=<<<STEP\s*\d+>>>|<<<DONE>>>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var stepMatches = stepPattern.Matches(trimmed);
        // Also match steps terminated by <<<STEP END>>>
        var stepEndPattern = new Regex(@"<<<STEP\s*\d+>>>\s*(.*?)<<<STEP END>>>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var stepEndMatches = stepEndPattern.Matches(trimmed);

        // Use step-end matches if available (more reliable), otherwise fall back to step-start matches
        var preferredMatches = stepEndMatches.Count > 0 ? stepEndMatches : stepMatches;

        foreach (Match m in preferredMatches)
        {
            var content = m.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(content)) continue;

            var file = ExtractField(content, "FILE");
            var change = ExtractField(content, "CHANGE");
            if (string.IsNullOrWhiteSpace(file) && string.IsNullOrWhiteSpace(change)) continue;

            var oldString = ExtractDelimitedSection(content, "OLD");
            var newString = ExtractDelimitedSection(content, "NEW");

            steps.Add(new PlanStep
            {
                File = file ?? "",
                Change = change ?? "",
                OldString = oldString ?? "",
                NewString = newString ?? "",
                Priority = 1
            });
        }

        if (steps.Count == 0 && !complete) return null;

        return new AgentPlan
        {
            Thinking = thinking ?? "",
            Summary = summary ?? "",
            Score = score,
            Plan = steps
        };
    } 
    private static string? ExtractDelimitedSection(string text, string sectionName)
    {
        var pattern = $@"<<<{sectionName}>>>\s*(.*?)(?=<<<|$)";
        var m = Regex.Match(text, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }
    /// <summary>
    /// Collects lines until the opening '(' of a method/constructor signature
    /// is matched by its closing ')', handling multi-line parameter lists.
    /// </summary>
    public static string CollectCompleteSignatureLine(string[] lines)
    {
        var sb = new StringBuilder();
        var depth = 0;
        var foundParen = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(trimmed);
            foreach (var c in trimmed)
            {
                if (c == '(') { depth++; foundParen = true; }
                else if (c == ')') depth--;
            }
            if (foundParen && depth == 0) break;
            if (foundParen && trimmed.EndsWith('{')) break;
        }
        return sb.ToString().Trim();
    }
    /// <summary>
    /// For HTML/Angular/Razor templates, deterministically verifies that the
    /// oldString being replaced is located within the correct *ngIf section.
    ///
    /// This catches the #1 HTML editing failure: the LLM edits a section that
    /// looks similar to the target (e.g., editing the 'users' tab when the step
    /// asks for the 'general' tab). The LLM verify gate frequently misses this
    /// because the sections have similar structure (both have search inputs,
    /// coordinate lists, etc.).
    ///
    /// Algorithm:
    ///   1. Scan the file for all *ngIf="someVar === 'sectionName'" directives
    ///   2. Find the div boundaries for each section
    ///   3. Determine which section the oldString falls in
    ///   4. Determine which section the step description references
    ///   5. If they don't match, reject with a directive error that includes
    ///      the CORRECT section's content so the LLM can copy from it
    /// </summary>
    public static string? DetectWrongSectionEdit(
        string oldStr, string fileContent, string stepChange, string relPath)
    {
        if (string.IsNullOrWhiteSpace(oldStr) || string.IsNullOrWhiteSpace(stepChange))
            return null;

        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        if (ext is not (".html" or ".htm" or ".cshtml" or ".razor" or ".vue" or ".svelte"))
            return null;

        // ── 1. Find all named conditional sections in the file ──────────────
        // Matches: *ngIf="activeDataTab === 'general'" or *ngIf="activeTab == 'users'"
        var sectionRegex = new Regex(
            @"\*ngIf\s*=\s*""(\w+)\s*={2,3}\s*'([^']+)'""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var sections = new List<(string name, int divStart, int divEnd)>();
        foreach (Match m in sectionRegex.Matches(fileContent))
        {
            var name = m.Groups[2].Value;
            var divStart = fileContent.LastIndexOf("<div", m.Index, StringComparison.Ordinal);
            if (divStart < 0) continue;
            var divEnd = FindMatchingCloseDiv(fileContent, divStart);
            if (divEnd < 0) continue;
            sections.Add((name, divStart, divEnd));
        }

        // Need at least 2 sections for wrong-section to be possible
        if (sections.Count < 2) return null;

        // ── 2. Find which section the oldStr is in ──────────────────────────
        var normFile = AgentUtilities.NormalizeLineEndings(fileContent);
        var normOld = AgentUtilities.NormalizeLineEndings(oldStr);
        var oldStrIdx = normFile.IndexOf(normOld, StringComparison.Ordinal);
        if (oldStrIdx < 0) return null; // Let other guards handle "not found"

        string? actualSection = null;
        foreach (var (name, divStart, divEnd) in sections)
        {
            if (oldStrIdx >= divStart && oldStrIdx <= divEnd)
            {
                actualSection = name;
                break;
            }
        }

        // If oldStr is not inside any named section (e.g. it's in the <head> or
        // common area), don't trigger — other guards will catch real issues.
        if (actualSection == null) return null;

        // ── 3. Determine target section from step description ───────────────
        var stepLower = stepChange.ToLowerInvariant();
        string? targetSection = null;
        foreach (var (name, _, _) in sections)
        {
            // Match the section name as a standalone word in the step description
            // e.g., step contains "general data tab" → matches section "general"
            if (Regex.IsMatch(stepLower, $@"\b{Regex.Escape(name.ToLowerInvariant())}\b"))
            {
                targetSection = name;
                break;
            }
        }

        // Step doesn't reference any named section — can't verify
        if (targetSection == null) return null;

        // ── 4. Correct section — no problem ─────────────────────────────────
        if (string.Equals(actualSection, targetSection, StringComparison.OrdinalIgnoreCase))
            return null;

        // ── 5. Wrong section! Build a helpful error with the correct content ─
        var targetSectionEntry = sections.FirstOrDefault(s =>
            string.Equals(s.name, targetSection, StringComparison.OrdinalIgnoreCase));

        var error = new StringBuilder();
        error.AppendLine($"WRONG SECTION — the step description references the '{targetSection}' section, " +
                         $"but your oldString was found in the '{actualSection}' section.");
        error.AppendLine();
        error.AppendLine($"You MUST find the section marked with *ngIf=\"... === '{targetSection}'\" " +
                         $"and use lines from THAT section as your oldString.");
        error.AppendLine($"Do NOT edit the '{actualSection}' section.");

        // Include the CORRECT section's content so the LLM can copy from it
        if (targetSectionEntry.divEnd > targetSectionEntry.divStart)
        {
            var sectionContent = normFile.Substring(
                targetSectionEntry.divStart,
                Math.Min(targetSectionEntry.divEnd - targetSectionEntry.divStart + 6, 3000));

            // Trim to a reasonable size — show first ~40 lines
            var sectionLines = sectionContent.Split('\n');
            if (sectionLines.Length > 45)
            {
                sectionContent = string.Join('\n', sectionLines.Take(40)) +
                                 "\n... (section continues)";
            }

            error.AppendLine();
            error.AppendLine($"═══ CORRECT SECTION CONTENT (*ngIf=\"... === '{targetSection}'\") ═══");
            error.AppendLine("```html");
            error.AppendLine(sectionContent);
            error.AppendLine("```");
            error.AppendLine();
            error.AppendLine($"Pick a unique line from the CORRECT section above as your oldString.");
        }

        return error.ToString();
    }
    /// <summary>
    /// Finds the matching closing </div> tag for the <div at the given index,
    /// tracking div nesting depth. Handles comments and attribute strings
    /// naively (sufficient for well-formed Angular templates).
    /// Returns the index of the closing </div> tag, or -1 if not found.
    /// </summary>
    private static int FindMatchingCloseDiv(string content, int openDivIdx)
    {
        if (openDivIdx < 0 || openDivIdx >= content.Length) return -1;

        var depth = 0;
        var pos = openDivIdx;

        while (pos < content.Length)
        {
            var nextOpen = content.IndexOf("<div", pos, StringComparison.OrdinalIgnoreCase);
            var nextClose = content.IndexOf("</div>", pos, StringComparison.OrdinalIgnoreCase);

            if (nextClose < 0) return -1; // unmatched — malformed HTML

            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                // Verify it's actually a <div tag (not <divider, <divide, etc.)
                var charAfter = nextOpen + 4 < content.Length
                    ? content[nextOpen + 4]
                    : '\0';
                if (charAfter == ' ' || charAfter == '>' || charAfter == '\t' ||
                    charAfter == '\n' || charAfter == '\r')
                {
                    depth++;
                }
                pos = nextOpen + 4;
            }
            else
            {
                if (depth <= 0) return nextClose;
                depth--;
                pos = nextClose + 6;
            }
        }

        return -1;
    }
    /// <summary>
    /// Extracts the verbatim file section most relevant to the change description using
    /// keyword matching. Used to show the LLM the ACTUAL target section when its oldString
    /// matched a different section of the file (e.g. another popup with a similar structure).
    /// </summary>
    public static string? ExtractVerbatimTargetSection(
        string fileContent, string changeDesc, int contextLines = 10, int centerLine = 0)
    {
        if (string.IsNullOrWhiteSpace(fileContent) || string.IsNullOrWhiteSpace(changeDesc))
            return null;

        var lines = fileContent.Split('\n');
        var anchorIdx = centerLine > 0 && centerLine <= lines.Length
            ? centerLine - 1
            : -1;

        if (anchorIdx < 0)
        {
            var resolved = ResolveTargetLineNumber(fileContent, changeDesc);
            if (resolved > 0) anchorIdx = resolved - 1;
        }

        if (anchorIdx < 0)
        {
            var words = changeDesc.ToLowerInvariant()
                .Split(new[] { ' ', '-', '_', '/', '\\', '(', ')', '"', '\'', ',', '.', ':', ';' },
                       StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 4)
                .Distinct()
                .ToList();

            if (words.Count == 0) return null;

            var bestScore = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                var lineLower = lines[i].ToLowerInvariant();
                var score = words.Sum(w => lineLower.Contains(w) ? 1 : 0);
                if (score > bestScore) { bestScore = score; anchorIdx = i; }
            }

            if (anchorIdx < 0 || bestScore == 0) return null;
        }

        var start = Math.Max(0, anchorIdx - contextLines);
        var end = Math.Min(lines.Length - 1, anchorIdx + contextLines);
        return string.Join("\n", lines[start..(end + 1)]);
    }

    /// <summary>
    /// Deterministically resolves the 1-based target line for an edit step by scanning the
    /// actual file — never trusting planner-guessed line numbers alone.
    /// </summary>
    public static int ResolveTargetLineNumber(
        string fileContent,
        string changeDesc,
        string? targetSymbol = null,
        string? estimatedLineRange = null,
        int plannerLineNumber = 0)
    {
        if (string.IsNullOrWhiteSpace(fileContent) || string.IsNullOrWhiteSpace(changeDesc))
            return plannerLineNumber > 0 ? plannerLineNumber : 0;

        var lines = fileContent.Split('\n');
        var candidates = new List<(int line, int score)>();
        var changeLower = changeDesc.ToLowerInvariant();
        var isInsertion = Regex.IsMatch(changeDesc,
            @"\b(add|insert|append|expand|include|new)\b", RegexOptions.IgnoreCase);

        if (!string.IsNullOrWhiteSpace(estimatedLineRange))
        {
            var rangeMatch = Regex.Match(estimatedLineRange, @"(\d+)\s*[-–~]\s*(\d+)");
            if (rangeMatch.Success &&
                int.TryParse(rangeMatch.Groups[1].Value, out var rangeStart) &&
                int.TryParse(rangeMatch.Groups[2].Value, out var rangeEnd))
            {
                var mid = (rangeStart + rangeEnd) / 2;
                if (mid >= 1 && mid <= lines.Length)
                    candidates.Add((mid, 100));
            }
            else
            {
                var singleMatch = Regex.Match(estimatedLineRange, @"(\d+)");
                if (singleMatch.Success &&
                    int.TryParse(singleMatch.Groups[1].Value, out var singleLine) &&
                    singleLine >= 1 && singleLine <= lines.Length)
                    candidates.Add((singleLine, 95));
            }
        }

        if (!string.IsNullOrWhiteSpace(targetSymbol))
        {
            var symPattern = $@"\b{Regex.Escape(targetSymbol)}\s*[\(<{{]";
            for (var i = 0; i < lines.Length; i++)
            {
                if (!Regex.IsMatch(lines[i], symPattern, RegexOptions.IgnoreCase)) continue;
                candidates.Add((i + 1, 92));
                break;
            }
        }

        if (isInsertion)
        {
            AddInsertionLineCandidates(lines, changeLower, candidates);
        }
        else
        {
            foreach (Match qm in Regex.Matches(changeDesc, @"['""]([^'""]{4,})['""]"))
            {
                var quoted = qm.Groups[1].Value;
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains(quoted, StringComparison.OrdinalIgnoreCase))
                        candidates.Add((i + 1, 88));
                }
            }
        }

        var keywords = ExtractMeaningfulKeywords(changeLower).Where(w => w.Length >= 4).ToList();
        if (keywords.Count > 0)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var lineLower = lines[i].ToLowerInvariant();
                var hitCount = keywords.Count(w => lineLower.Contains(w));
                if (hitCount >= 2)
                    candidates.Add((i + 1, 45 + hitCount * 8));
            }
        }

        if (plannerLineNumber > 0 && plannerLineNumber <= lines.Length)
        {
            var plannerLine = lines[plannerLineNumber - 1];
            var plannerHits = keywords.Count(w => plannerLine.Contains(w, StringComparison.OrdinalIgnoreCase));
            candidates.Add((plannerLineNumber, plannerHits >= 2 ? 55 + plannerHits * 5 : 15));
        }

        if (candidates.Count == 0)
            return plannerLineNumber > 0 ? plannerLineNumber : 0;

        var best = candidates
            .GroupBy(c => c.line)
            .Select(g => (line: g.Key, score: g.Max(x => x.score)))
            .OrderByDescending(x => x.score)
            .ThenBy(x => plannerLineNumber > 0 ? Math.Abs(x.line - plannerLineNumber) : x.line)
            .First();

        return best.line;
    }

    private static void AddInsertionLineCandidates(
        string[] lines, string changeLower, List<(int line, int score)> candidates)
    {
        var containerHints = new List<(string pattern, int weight)>();
        if (changeLower.Contains("faq-container") || changeLower.Contains("faq container"))
            containerHints.Add(("faq-container", 80));
        if (changeLower.Contains("discord-panel") || changeLower.Contains("discord panel"))
            containerHints.Add(("discord-panel", 75));
        if (changeLower.Contains("faq-content") || changeLower.Contains("faq content"))
            containerHints.Add(("faq-content", 78));
        if (containerHints.Count == 0 && changeLower.Contains("faq"))
        {
            containerHints.Add(("faq-container", 70));
            containerHints.Add(("discord-panel", 65));
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var lineLower = lines[i].ToLowerInvariant();
            foreach (var (pattern, weight) in containerHints)
            {
                if (!lineLower.Contains(pattern, StringComparison.Ordinal)) continue;

                var score = weight;
                if (lineLower.Contains("faq", StringComparison.Ordinal)) score += 8;
                if (lineLower.Contains("popup-panel", StringComparison.Ordinal)) score -= 40;
                candidates.Add((i + 1, score));

                for (var j = i; j < Math.Min(i + 15, lines.Length); j++)
                {
                    var probe = lines[j];
                    if (probe.Contains("FAQ entries go here", StringComparison.OrdinalIgnoreCase))
                        candidates.Add((j + 1, weight + 35));
                    if (probe.TrimStart().StartsWith("</details>", StringComparison.OrdinalIgnoreCase))
                        candidates.Add((j + 1, weight + 20));
                }
            }

            if (lineLower.Contains("faq entries go here", StringComparison.Ordinal))
                candidates.Add((i + 1, 90));
        }
    } 

    private static string? ExtractField(string text, string fieldName)
    {
        // Match fieldName: followed by content up to the next field name, <<<tag>>>, or end of string
        // Handles both same-line (FILE: pathCHANGE: desc) and multi-line formats
        var pattern = $@"{fieldName}:\s*(.*?)(?=\s*(?:FILE:|CHANGE:|DESCRIPTION:|<<<|$))";
        var m = Regex.Match(text, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }
}
