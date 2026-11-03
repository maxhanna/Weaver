using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Weaver.Services;

using static Weaver.Services.AgentTokenMetrics;
using static Weaver.Services.AgentEditHeuristics;
using static Weaver.Services.AgentPlanParsing;
using static Weaver.Services.AgentMethodInventory;
using static Weaver.Services.AgentProjectUtilities;
using static Weaver.Services.AgentDiscovery;
using static Weaver.Services.AgentTextUtilities;
using static Weaver.Services.AgentCodeFormatting;
using static Weaver.Services.AgentSkeleton;
using static Weaver.Services.AgentDiffUtilities;
using static Weaver.Services.AgentJsonUtilities;

/// <summary>Part of the split of the former AgentUtilities monolith.</summary>
public static class AgentPlanParsing
{
    public static readonly string[] _verifyPrefixes = {
        "ensure", "verify", "make sure", "confirm", "validate",
        "check", "guarantee", "see if", "determine if", "review"
    };

    public static AgentPlan? TryDetectSimpleIntent(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return null;
        var p = prompt.Trim();
        var lower = p.ToLowerInvariant();
        var renameMatch = Regex.Match(p,
            @"\b(?:rename|move)\s+(?:""([^""]+)""|'([^']+)'|([^\s]+))\s+(?:to|→|-?>)\s+(?:""([^""]+)""|'([^']+)'|([^\s]+))",
            RegexOptions.IgnoreCase);
        if (renameMatch.Success)
        {
            var src = (renameMatch.Groups[1].Value + renameMatch.Groups[2].Value + renameMatch.Groups[3].Value).Replace('\\', '/').Trim('/', ' ', '"', '\'');
            var dst = (renameMatch.Groups[4].Value + renameMatch.Groups[5].Value + renameMatch.Groups[6].Value).Replace('\\', '/').Trim('/', ' ', '"', '\'');
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
        var folderMatch = Regex.Match(p,
            @"\b(?:create|make)\s+(?:a\s+)?(?:new\s+)?(?:folder|directory)\s+(?:called\s+|named\s+)?['""]?([\w./\\-]+)['""]?",
            RegexOptions.IgnoreCase);
        if (folderMatch.Success)
        {
            var folderPath = folderMatch.Groups[1].Value.Replace('\\', '/').Trim('/', ' ', '"', '\'');
            return new AgentPlan
            {
                Thinking = $"Direct folder creation detected: {folderPath}",
                Summary = $"Create folder {folderPath}",
                Plan = new List<PlanStep>
                {
                    new() { File = "_create_directory", Change = folderPath, Priority = 1 }
                }
            };
        }
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
        return null;
    }

    public static AgentPlan? EnforceProxyConfigForControllers(AgentPlan? plan, string projectRoot)
    {
        if (plan?.Plan == null || plan.Plan.Count == 0) return plan;
        var proxyFiles = Directory.GetFiles(projectRoot, "proxy.conf.js", SearchOption.AllDirectories);
        if (proxyFiles.Length == 0) return plan;
        var proxyRelPath = Path.GetRelativePath(projectRoot, proxyFiles[0]).Replace('\\', '/');
        bool hasProxyUpdate = plan.Plan.Any(p =>
            p.File != null &&
            p.File.EndsWith("proxy.conf.js", StringComparison.OrdinalIgnoreCase));
        if (hasProxyUpdate) return plan;
        var controllerStep = plan.Plan.FirstOrDefault(p =>
            p.File != null &&
            p.File.EndsWith("Controller.cs", StringComparison.OrdinalIgnoreCase) &&
            IsRelativePath(p.File));
        if (controllerStep == null) return plan;
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, controllerStep.File.Replace('/', Path.DirectorySeparatorChar)));
        if (System.IO.File.Exists(fullPath)) return plan;
        var controllerName = Path.GetFileNameWithoutExtension(controllerStep.File);
        var baseName = controllerName.EndsWith("Controller", StringComparison.OrdinalIgnoreCase)
            ? controllerName.Substring(0, controllerName.Length - "Controller".Length)
            : controllerName;
        var route = "/" + baseName.ToLowerInvariant();
        try
        {
            var proxyContent = System.IO.File.ReadAllText(proxyFiles[0]);
            if (proxyContent.Contains($"\"{route}\"", StringComparison.OrdinalIgnoreCase) ||
                proxyContent.Contains($"\"{route},", StringComparison.OrdinalIgnoreCase))
            {
                return plan;
            }
        }
        catch { }
        plan.Plan.Add(new PlanStep
        {
            File = proxyRelPath,
            Change = $"Add the new route '{route}' to the context array in proxy.conf.js so the Angular dev server proxies API calls to the new backend controller. Do NOT duplicate existing routes.",
            Priority = 1
        });
        return plan;
    }

    public static List<PlanStep> DeduplicateSimilarSteps(List<PlanStep> steps, double similarityThreshold = 0.72)
    {
        if (steps.Count <= 1) return steps;
        var keep = new List<PlanStep>();
        var keptSignatures = new List<(HashSet<string> keywords, HashSet<string> quoted, string file, string? locationTag)>();
        foreach (var step in steps)
        {
            var file = (step.File ?? "").Trim();
            var change = step.Change ?? "";
            var keywords = ExtractMeaningfulKeywords(change.ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var quoted = ExtractQuotedSnippets(change);
            var locationTag = ExtractLocationTag(change);
            var isDuplicate = false;
            for (var i = 0; i < keep.Count; i++)
            {
                var (existingKeywords, existingQuoted, existingFile, existingLocationTag) = keptSignatures[i];
                if (!string.Equals(existingFile, file, StringComparison.OrdinalIgnoreCase)) continue;
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

    internal static double JaccardSimilarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 0;
        var intersection = a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count();
        var union = a.Union(b, StringComparer.OrdinalIgnoreCase).Count();
        return union == 0 ? 0 : (double)intersection / union;
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
            if (isTableCreationStep)
            {
                // Table creation is no longer merged into the endpoint edit. Instead it
                // becomes its own _sql_migration step: the executor appends the DDL to
                // migrations/schema_changes.md for the user to apply manually, keeping
                // CREATE TABLE out of the method body. If the step already carries the DDL
                // in NewString, keep it; otherwise the executor drafts the CREATE TABLE
                // from the description.
                var migrationStep = new PlanStep
                {
                    File = "_sql_migration",
                    Change = cur.Change ?? "create new SQL table",
                    NewString = cur.NewString,
                    Priority = cur.Priority
                };
                merged.Add(migrationStep);
                continue;
            }
            merged.Add(cur);
        }
        return merged;
    }

    public static AgentPlan? EnforceAngularScaffolding(AgentPlan plan, string projectRoot)
    {
        if (plan?.Plan == null || plan.Plan.Count == 0) return plan;
        var compStep = plan.Plan.FirstOrDefault(p =>
            p.File != null &&
            p.File.EndsWith(".component.ts", StringComparison.OrdinalIgnoreCase) &&
            IsRelativePath(p.File));
        if (compStep == null) return plan;
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, compStep.File.Replace('/', Path.DirectorySeparatorChar)));
        if (File.Exists(fullPath)) return plan;
        bool hasScaffoldCommand = plan.Plan.Any(p =>
            p.File == "_command" &&
            p.Change != null &&
            p.Change.Contains("ng g c", StringComparison.OrdinalIgnoreCase));
        if (!hasScaffoldCommand)
        {
            var rootFolder = compStep.File.Split('/')[0];
            var dir = Path.GetDirectoryName(compStep.File)?.Replace('\\', '/');
            var name = Path.GetFileNameWithoutExtension(compStep.File).Replace(".component", "");
            var cmd = $"{(rootFolder.Contains(".") ? $"cd {rootFolder}; " : "")}npx ng g c {dir}/{name} --skip-tests";
            plan.Plan.Insert(0, new PlanStep
            {
                File = "_command",
                Change = cmd,
                Priority = 1
            });
        }
        bool hasModuleUpdate = plan.Plan.Any(p =>
            p.File != null &&
            p.File.EndsWith("app.module.ts", StringComparison.OrdinalIgnoreCase));
        if (!hasModuleUpdate)
        {
            var rootFolder = compStep.File.Split('/')[0];
            var modulePath = $"{rootFolder}/src/app/app.module.ts";
            var componentName = Path.GetFileNameWithoutExtension(compStep.File).Replace(".component", "");
            plan.Plan.Insert(1, new PlanStep
            {
                File = modulePath,
                Change = $"Register the new {componentName} component in the @NgModule declarations array",
                Priority = 1
            });
        }
        return plan;
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
            var parseOpts = new JsonDocumentOptions { AllowTrailingCommas = true };
            var seenObjects = new List<string>();
            var depth = 0;
            var inString = false;
            var escaped = false;
            var start = -1;
            for (var i = 0; i < cleaned.Length; i++)
            {
                var c = cleaned[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }
                    if (c == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"')
                {
                    inString = true;
                    continue;
                }
                if (c == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                    continue;
                }
                if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        var candidate = cleaned[start..(i + 1)];
                        try
                        {
                            using var doc = JsonDocument.Parse(candidate, parseOpts);
                            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                                seenObjects.Add(candidate);
                        }
                        catch
                        {
                            // Ignore incomplete or malformed fragments. They will be skipped.
                        }
                        start = -1;
                    }
                }
            }
            if (seenObjects.Count > 0)
            {
                var finalCandidate = seenObjects[^1];
                using var doc = JsonDocument.Parse(finalCandidate, parseOpts);
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
            var fb = cleaned.IndexOf('{');
            var lb = cleaned.LastIndexOf('}');
            if (fb >= 0 && lb > fb)
            {
                var fallbackCandidate = cleaned[fb..(lb + 1)];
                using var fallbackDoc = JsonDocument.Parse(fallbackCandidate, parseOpts);
                var fallbackRoot = fallbackDoc.RootElement;
                if (fallbackRoot.ValueKind != JsonValueKind.Object) return empty;
                var readyFallback = fallbackRoot.TryGetProperty("ready", out var rEl2) && rEl2.ValueKind == JsonValueKind.True && rEl2.GetBoolean();
                var filesFallback = new List<string>();
                if (fallbackRoot.TryGetProperty("filesToRead", out var fArr2) && fArr2.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in fArr2.EnumerateArray())
                    {
                        if (f.ValueKind == JsonValueKind.String)
                        {
                            var s = f.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) filesFallback.Add(s.Replace('\\', '/'));
                        }
                    }
                }
                var refinedFallback = fallbackRoot.TryGetProperty("refinedChange", out var rcEl2) && rcEl2.ValueKind == JsonValueKind.String ? rcEl2.GetString() : null;
                var symbolFallback = fallbackRoot.TryGetProperty("targetSymbol", out var tsEl2) && tsEl2.ValueKind == JsonValueKind.String ? tsEl2.GetString() : null;
                var rangeFallback = fallbackRoot.TryGetProperty("estimatedLineRange", out var lrEl2) && lrEl2.ValueKind == JsonValueKind.String ? lrEl2.GetString() : null;
                var confFallback = 0;
                if (fallbackRoot.TryGetProperty("confidence", out var cEl2) && cEl2.ValueKind == JsonValueKind.Number)
                    confFallback = cEl2.GetInt32();
                return new StepExplorationResponse
                {
                    Ready = readyFallback,
                    FilesToRead = filesFallback,
                    RefinedChange = refinedFallback,
                    TargetSymbol = symbolFallback,
                    LineRange = rangeFallback,
                    Confidence = confFallback
                };
            }
        }
        catch { }
        return empty;
    }

    public static bool TaskExpectsFileChanges(string prompt)
    {
        var lower = prompt.ToLowerInvariant();
        string[] verbs = {
            "add","implement","fix","update","change","create","modify","remove","delete",
            "refactor","edit","write","toggle","enable","disable","insert","set","make",
            "build","install","configure","hook","wire","connect","show","hide","display",
            "save","persist","store","expose","include"
        };
        return verbs.Any(v => Regex.IsMatch(lower, $@"\b{Regex.Escape(v)}\b"));
    }

    public static List<AgentStep> ExtractEditPairs(string text, string defaultPath)
    {
        var steps = new List<AgentStep>();
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

    public static IEnumerable<string> GeneratePlanJsonCandidates(string json)
    {
        yield return json;
        var quoted = Regex.Replace(json,
            @"(?<=[{,])\s*([a-zA-Z_$][\w$]*)\s*(?=:)",
            m => m.Value.Replace(m.Groups[1].Value, $"\"{m.Groups[1].Value}\""));
        if (quoted != json) yield return quoted;
        var repaired = RepairJsonStringValues(json);
        if (repaired != null && repaired != json) yield return repaired;
        if (repaired != null && repaired != json)
        {
            var both = Regex.Replace(repaired,
                @"(?<=[{,])\s*([a-zA-Z_$][\w$]*)\s*(?=:)",
                m => m.Value.Replace(m.Groups[1].Value, $"\"{m.Groups[1].Value}\""));
            if (both != repaired) yield return both;
        }
        var fullyRepaired = RepairJsonString(json);
        if (fullyRepaired != null) yield return fullyRepaired;
        if (fullyRepaired != null)
        {
            var quotedFull = Regex.Replace(fullyRepaired,
                @"(?<=[{,])\s*([a-zA-Z_$][\w$]*)\s*(?=:)",
                m => m.Value.Replace(m.Groups[1].Value, $"\"{m.Groups[1].Value}\""));
            if (quotedFull != fullyRepaired) yield return quotedFull;
        }
        var truncFixed = TryRepairTruncatedPlanJson(json);
        if (truncFixed != null && truncFixed != json) yield return truncFixed;
        if (truncFixed != null)
        {
            var truncAndRepaired = RepairJsonString(truncFixed);
            if (truncAndRepaired != null && truncAndRepaired != truncFixed)
                yield return truncAndRepaired;
        }
    }

    public static string? TryRepairTruncatedPlanJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var stack = new Stack<char>();
        var inString = false;
        var lastPlanItemEnd = -1;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c is '{' or '[') { stack.Push(c); continue; }
            if (c == '}' && stack.Count > 0 && stack.Peek() == '{')
            {
                stack.Pop();
                if (stack.Count == 2) lastPlanItemEnd = i + 1;
                continue;
            }
            if (c == ']' && stack.Count > 0 && stack.Peek() == '[') stack.Pop();
        }
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
        {
            var sb = new StringBuilder(raw.TrimEnd());
            if (inString) sb.Append('"');
            while (sb.Length > 0 && sb[^1] is ',' or ':')
                sb.Remove(sb.Length - 1, 1);
            foreach (var ch in stack)
                sb.Append(ch == '{' ? '}' : ']');
            var candidate = sb.ToString();
            if (IsPlan(candidate)) return candidate;
            var escaped = RepairJsonStringValues(candidate);
            if (escaped != null && IsPlan(escaped)) return escaped;
            var fullyRepaired = RepairJsonString(candidate);
            if (fullyRepaired != null && IsPlan(fullyRepaired)) return fullyRepaired;
        }
        if (lastPlanItemEnd > 0)
        {
            var cut = raw[..lastPlanItemEnd].TrimEnd(',', ' ', '\t', '\r', '\n') + "]}";
            if (IsPlan(cut)) return cut;
            var cutRepaired = RepairJsonString(cut);
            if (cutRepaired != null && IsPlan(cutRepaired)) return cutRepaired;
        }
        return null;
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

    internal static bool LooksLikePlanJson(string text) =>
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
        var truncRepaired = TryRepairTruncatedPlanJson(cleaned);
        if (truncRepaired != null)
        {
            var truncOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true };
            foreach (var candidate in GeneratePlanJsonCandidates(truncRepaired))
            {
                try
                {
                    var deserializedPlan = JsonSerializer.Deserialize<AgentPlan>(candidate, truncOpts);
                    if (deserializedPlan?.Plan?.Count > 0)
                        return DeduplicatePlan(deserializedPlan);
                }
                catch { }
            }
        }
        var jsonBlocks = ExtractJsonBlocks(cleaned).Where(LooksLikePlanJson).OrderByDescending(b => b.Length).ToList();
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

    public static AgentPlan? ParseDelimitedPlan(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```"))
        {
            var m = Regex.Match(trimmed, @"```(?:text)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (m.Success) trimmed = m.Groups[1].Value.Trim();
        }
        trimmed = Regex.Replace(trimmed, @"###\s*STEP\s*(\d+)\s*###", "<<<STEP $1>>>", RegexOptions.IgnoreCase);
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
        var stepEndPattern = new Regex(@"<<<STEP\s*\d+>>>\s*(.*?)<<<STEP END>>>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var stepEndMatches = stepEndPattern.Matches(trimmed);
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

    internal static string? ExtractDelimitedSection(string text, string sectionName)
    {
        var pattern = $@"<<<{sectionName}>>>\s*(.*?)(?=<<<|$)";
        var m = Regex.Match(text, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }
}
