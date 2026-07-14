using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Weaver.Services;

public static class AgentUtilities
{
    public const int MetaPlanScoreThreshold = 6;

    public static MetaPlanGateDecision EvaluateMetaPlanGate(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return new(false, 0, "Empty prompt");
        var lower = prompt.ToLowerInvariant();

        if (Regex.IsMatch(lower,
                @"\b(add|create|implement)\b.{0,50}\b(method|endpoint)\b.{0,80}\b(table|insert|create\s+table)\b") &&
            !Regex.IsMatch(lower, @"\b(component|frontend|angular|service\s+layer|multiple\s+files)\b"))
        {
            return new(false, 0, "Atomic method/endpoint plus table task");
        }

        var distinctFileHints = Regex.Matches(prompt, @"[\w\-/\\]+\.(cs|ts|tsx|js|jsx|html|css|scss)")
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var newSymbolVerbs = Regex.Matches(lower, @"\b(add|create|implement|build)\b").Count;
        var crossCuttingWords = Regex.Matches(lower,
            @"\b(component|service|controller|endpoint|frontend|backend|scaffold|module|full\s+crud|end.to.end)\b").Count;
        var score = distinctFileHints * 2 + newSymbolVerbs + crossCuttingWords;

        if (Regex.IsMatch(lower, @"\b(end.to.end\s+)?(authentication|password\s+reset)\b"))
            return new(true, Math.Max(score, MetaPlanScoreThreshold), "Inherently cross-layer identity workflow");

        if (distinctFileHints <= 2 && newSymbolVerbs <= 1 && crossCuttingWords <= 1)
            return new(false, score, "Bounded task with at most two explicit files and one architectural concern");

        var useMetaPlan = score >= MetaPlanScoreThreshold;
        return new(useMetaPlan, score,
            $"{distinctFileHints} file hint(s), {newSymbolVerbs} creation verb(s), {crossCuttingWords} cross-cutting term(s)");
    }
    private const int CompactThreshold90 = 2520;
    public static readonly string[] UnsafeEditMarkers =
    {
        "…(truncated)", "â€¦(truncated)", "...(truncated)"
    };

    public enum PreEditVerdict { Proceed, AlreadyDone, Irrelevant }

    public static readonly string[] _verifyPrefixes = {
        "ensure", "verify", "make sure", "confirm", "validate",
        "check", "guarantee", "see if", "determine if", "review"
    };

    public static readonly HashSet<string> _builtInTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "string", "int", "long", "double", "float", "decimal", "bool", "char", "byte",
        "short", "uint", "ulong", "ushort", "sbyte", "object", "dynamic", "void",
        "Task", "ValueTask", "Task<T>", "ValueTask<T>", "IActionResult", "ActionResult",
        "OkResult", "OkObjectResult", "BadRequestResult", "BadRequestObjectResult",
        "NotFoundResult", "StatusCodeResult", "ObjectResult", "RedirectResult",
        "FileResult", "ContentResult", "JsonResult", "IEnumerable<T>", "IQueryable<T>",
        "List<T>", "Dictionary<TKey,TValue>", "HashSet<T>", "IList<T>", "ICollection<T>",
        "HttpResponse", "HttpRequest", "CancellationToken", "DateTime", "TimeSpan",
        "Guid", "Uri", "Exception", "InvalidOperationException", "ArgumentNullException",
        "ArgumentException", "NotSupportedException", "MySqlConnection", "MySqlCommand",
        "MySqlDataReader", "MySqlParameter", "MySqlDbType", "DbConnection", "DbCommand",
        "HttpClient", "HttpContent", "HttpMethod", "HttpStatusCode",
        "IHttpResponseBodyFeature", "IHttpRequestLifetimeFeature", "IHttpConnectionFeature",
        "IHttpWebSocketFeature", "Stream", "PipeWriter", "PipeReader",
        "HttpProtocol", "HttpVersion", "HttpContext", "HttpRequest", "HttpResponse",
        "Model", "ViewDataDictionary", "ViewData", "TempData", "ViewBag", "RouteData"
    };

    public static readonly Regex MethodDeclRegex = new(
        @"(?:(?:public|private|protected|internal)\s+)?(?:(?:static|virtual|override|abstract|sealed|new|partial|async|unsafe)\s+)*(?:\w+(?:\[\])?(?:<[^>]*>)?)\s+(\w+)\s*\(([^)]*)\)",
        RegexOptions.Compiled);

    public static readonly HashSet<string> notTableWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "or", "not", "in", "on", "at", "to", "for", "of", "by",
        "as", "is", "it", "an", "be", "has", "have", "are", "was", "were",
        "from", "into", "with", "without", "using", "where", "when", "while",
        "then", "than", "this", "that", "these", "those", "each", "all",
        "both", "between", "after", "before", "above", "below", "under",
        "over", "through", "during", "until", "since", "within", "about",
        "join", "inner", "outer", "left", "right", "full", "cross", "natural",
        "order", "group", "having", "limit", "offset", "set", "values",
        "select", "insert", "update", "delete", "create", "alter", "drop",
        "true", "false", "null", "default", "unique", "index", "key",
        "primary", "foreign", "check", "cascade", "restrict", "action",
        "count", "sum", "avg", "min", "max", "distinct", "exists", "case",
        "when", "else", "then", "end", "cast", "convert", "coalesce",
        "nullif", "date", "time", "timestamp", "year", "month", "day",
        "hour", "minute", "second", "now", "utc_timestamp",
        "tinyint", "smallint", "mediumint", "int", "integer", "bigint",
        "decimal", "numeric", "float", "double", "real", "bit", "boolean",
        "char", "varchar", "nvarchar", "text", "blob", "binary", "varbinary",
        "enum", "set", "json", "geometry", "point", "linestring", "polygon",
        "return", "returns", "declare", "begin", "end", "if", "else",
        "iterate", "leave", "loop", "repeat", "while", "signal", "resignal",
        "cursor", "handler", "continue", "exit", "undo", "condition",
        "open", "close", "fetch", "into", "call", "rename", "truncate",
        "start", "stop", "commit", "rollback", "savepoint", "release",
        "lock", "unlock", "grant", "revoke", "analyze", "optimize",
        "reorganize", "repair", "check", "checksum", "backup", "restore",
        "utf8", "utf8mb4", "ascii", "latin1", "unicode", "?",
        "auto_increment", "unsigned", "signed", "zerofill",
        "current_timestamp", "current_date", "current_time", "localtime",
        "localtimestamp"
    };

    public static readonly HashSet<string> skipTypes = new(StringComparer.Ordinal)
    {
        "string", "int", "bool", "long", "double", "float", "decimal", "char",
        "byte", "short", "uint", "ulong", "ushort", "sbyte", "object", "void",
        "Task", "ValueTask", "IEnumerable", "ICollection", "IList", "List",
        "Dictionary", "HashSet", "Queue", "Stack", "Tuple", "Nullable",
        "StringBuilder", "StringReader", "StringWriter",
        "HttpResponseMessage", "HttpRequestMessage",
        "ActionResult", "IActionResult", "OkResult", "OkObjectResult",
        "BadRequestResult", "NotFoundResult", "StatusCodeResult",
        "JsonResult", "FileResult", "ContentResult", "RedirectResult",
        "ViewResult", "PartialViewResult", "IQueryable",
        "Thread", "TaskCompletionSource", "CancellationToken",
        "HttpClient", "HttpContext", "HttpRequest", "HttpResponse",
        "Stream", "StreamReader", "StreamWriter", "MemoryStream",
        "FileStream", "BinaryReader", "BinaryWriter", "TextReader", "TextWriter",
        "DateTime", "DateTimeOffset", "TimeSpan", "Guid", "Uri", "Version",
        "Regex", "Match", "Group", "Capture", "StringComparison",
        "Encoding", "UTF8", "Unicode", "ASCII", "Declare", "TryParse",
         "Parse", "Convert", "Math", "Random",
        "Exception", "InvalidOperationException", "ArgumentNullException",
        "ArgumentException", "IOException", "FormatException",
        "Response", "Request", "Delegate", "Func", "Action", "Predicate",
        "NameValueCollection", "IOrderedEnumerable",
        "IServiceProvider", "IDisposable", "IAsyncDisposable",
        "Startup", "Program", "MySqlConnection", "MySqlCommand", "MySqlDataReader",
        "MySqlParameter", "MySqlTransaction", "MySqlException",
        "SqlConnection", "SqlCommand", "SqlDataReader",
        "NpgsqlConnection", "NpgsqlCommand", "NpgsqlDataReader", "?",
        "IConfiguration", "Log", "JsonDocument", "JsonNode", "JsonObject",
        "JsonArray", "JsonValue", "JsonSerializer", "JsonSerializerOptions"
    };

    public static readonly string[] serviceSuffixes = {
        "Service", "Controller", "Handler", "Manager",
        "Provider", "Factory", "Repository", "Helper", "Util", "Extension",
        "Middleware", "Filter", "Attribute", "Converter", "Mapper", "Builder",
        "Adapter", "Proxy", "Facade", "Strategy", "Observer", "Configuration",
        "Options", "Settings"
    };

    public static (PipelineType Type, double CommandScore, double EditScore) ClassifyTask(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return (PipelineType.CommandExecution, 100, 0);
        var lower = prompt.ToLowerInvariant();

        double cmdScore = 0;
        double editScore = 0;

        if (TryDetectSimpleIntent(prompt) != null) cmdScore += 100;

        if (Regex.IsMatch(lower, @"\b(ping|health?|status|check\s+connect|is\s+\S+\s+(up|alive|reachable))\b"))
            cmdScore += 80;

        if (Regex.IsMatch(lower, @"\b(create\s+(a\s+)?(new\s+)?file)\b"))
            cmdScore += 60;

        if (Regex.IsMatch(lower, @"\b(put|place|write|save|download)\s+(a\s+)?(file|data|content|result)\s+(on|to|at|in)\s+(the\s+)?(desktop|downloads|documents|home)\b"))
            cmdScore += 80;

        if (Regex.IsMatch(lower, @"\b(what.*in|contents?\s+of|find\s+files?\s+in|directory\s+contents|structure\s+of|tree|logs?|journal|stdout|stderr|console|output|terminal|logs|process|service)\b"))
            cmdScore += 65;

        if (Regex.IsMatch(lower, @"\b(list)\b") &&
            !Regex.IsMatch(lower, @"\b(list\s+of\s+\w+)\b"))
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

        if (Regex.IsMatch(lower, @"\b(desktop|downloads?|documents?)\b"))
            cmdScore += 55;

        if (Regex.IsMatch(lower, @"\b(what\s+version|is\s+installed|which\s+(port|process|version|branch)|disk\s+(usage|space)|how\s+much\s+(memory|disk)|running\s+process|environment\s+variable|current\s+(directory|path|time|date)|whoami|uptime)\b"))
            cmdScore += 55;

        if (Regex.IsMatch(lower, @"\b(computers?\s+on\s+network|network\s+(scan|devices)|scan\s+(network|ports)|find\s+(devices|computers|hosts)|connected\s+devices)\b"))
            cmdScore += 55;

        if (Regex.IsMatch(lower, @"\b(get|find|search|look\s+up|what\s+is|tell\s+me\s+(about|the)|fetch)\b.{0,60}\b(latest|list|numbers?|info|information|data)\b"))
            cmdScore += 50;

        if (Regex.IsMatch(lower, @"\b(augment|implement|refactor|rewrite|redesign)\b"))
            editScore += 65;

        if (Regex.IsMatch(lower, @"\b(fix|update|change|modify|edit|patch|tweak|adjust)\b"))
            editScore += 55;

        if (Regex.IsMatch(lower, @"\b(add|remove|delete|insert)\b"))
            editScore += 45;

        if (Regex.IsMatch(lower, @"\b(toggle|enable|disable|configure|wire|connect|hook|expose)\b"))
            editScore += 40;


        if (Regex.IsMatch(lower, @"\b(div|button|input|form|dropdown|checkbox|radio|modal|popup|panel|section|tab|sidebar|navbar|header|footer)\b"))
            editScore += 35;

        if (Regex.IsMatch(lower, @"\b(component|template|view|page|layout|widget|element|calendar)\b"))
            editScore += 30;


        var isDataProcessing = Regex.IsMatch(lower, @"\b(row|column|csv|tsv|json|each\s+(row|line)|file.*data|read.*file|fetch.*(from|data|api|endpoint))\b");
        if (!isDataProcessing && Regex.IsMatch(lower, @"\bset\b.{0,40}\bto\b"))
            editScore += 40;


        if (Regex.IsMatch(lower, @"\b(style|css|class|theme|color|font|margin|padding|border|shadow|layout|spacing)\b"))
            editScore += 30;



        if (!isDataProcessing && Regex.IsMatch(lower, @"\b[\w./\\-]+\.\w{2,4}\b"))
            editScore += 20;


        if (Regex.IsMatch(lower, @"\b(show|display|render|preview|view)\b"))
            editScore += 15;


        if (Regex.IsMatch(lower, @"\b(picture|image|photo|thumbnail)\b"))
            editScore += 12;


        bool emailForReading = Regex.IsMatch(lower,
            @"\b(read|check|fetch|inbox|unread|send|compose)\b.{0,40}\b(email|mail)\b");
        bool emailForConfig = Regex.IsMatch(lower, @"\bemail\b") && !emailForReading;

        if (emailForReading) cmdScore += 80;
        if (emailForConfig) editScore += 25;

        if (editScore >= 80) cmdScore -= 30;


        if (cmdScore >= 50 && editScore == 0) editScore -= 40;


        if (editScore > cmdScore) return (PipelineType.CodeEdit, cmdScore, editScore);
        if (cmdScore > editScore) return (PipelineType.CommandExecution, cmdScore, editScore);

        return (PipelineType.CodeEdit, cmdScore, editScore);
    }


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
            AgentUtilities.IsRelativePath(p.File));

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


        var sections = Regex.Split(explorationContext.Trim(), @"(?=^### )", RegexOptions.Multiline);
        var result = new StringBuilder();

        foreach (var rawSection in sections)
        {
            if (string.IsNullOrWhiteSpace(rawSection)) continue;


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
                    break;
                }
                continue;
            }
            if (inFence)
                codeLines.Add(line);
            else
                headerLines.Add(line);
        }

        if (codeLines.Count == 0)
            return string.Join("\n", headerLines);


        var included = new SortedSet<int>();


        for (var i = 0; i < Math.Min(20, codeLines.Count); i++)
            included.Add(i);


        for (var i = 20; i < codeLines.Count; i++)
        {
            if (keywords.Count == 0) break;
            if (keywords.Any(kw => codeLines[i].Contains(kw, StringComparison.OrdinalIgnoreCase)))
            {
                for (var w = Math.Max(0, i - 3); w <= Math.Min(codeLines.Count - 1, i + 3); w++)
                    included.Add(w);
            }

            if (Regex.IsMatch(codeLines[i], @"^\s*((public|private|protected|static|async|export|function|get|set)\s+)*\w+\s*(<[^>]+>)?\s*\([^)]*\)\s*(:\s*[^{;]+)?\s*[{;]", RegexOptions.IgnoreCase))
            {

                for (var w = Math.Max(0, i - 1); w <= Math.Min(codeLines.Count - 1, i + 5); w++)
                    included.Add(w);
            }
        }


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







    public static int DetectIndentWidth(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return 4;

        var lines = source.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');


        var hasTabIndent = false;
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            if (line[0] == '\t') { hasTabIndent = true; break; }
        }
        if (hasTabIndent)
        {

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







    public static string? ExtractJsMethodNameFromChange(string change)
    {
        if (string.IsNullOrWhiteSpace(change)) return null;


        var m = Regex.Match(change,
            @"\b(?:add|create|insert|define|implement)\s+(?:a\s+)?(?:new\s+)?(?:method|function|handler)\s+(?:named\s+|called\s+)?([A-Za-z_$][A-Za-z0-9_$]*)",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;


        m = Regex.Match(change,
            @"\b(?:add|create|insert|define|implement)\s+(?:a\s+)?(?:new\s+)?([A-Za-z_$][A-Za-z0-9_$]*)\s+(?:method|function|handler)\b",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;


        m = Regex.Match(change,
            @"\b(?:add|create|insert|define|implement)\s+(?:the\s+)?([A-Za-z_$][A-Za-z0-9_$]*)\s*\(\s*\)",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;


        m = Regex.Match(change,
            @"\b(?:add|create|insert|define|implement)\s+(?:the\s+)?(?:vm|this|self|that)\.([A-Za-z_$][A-Za-z0-9_$]*)",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;


        m = Regex.Match(change,
            @"\b(?:add|create|insert)\s+(?:a\s+)?(?:new\s+)?([A-Za-z_$][A-Za-z0-9_$]*)\b");
        if (m.Success)
        {
            var candidate = m.Groups[1].Value;

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


        var lines = discoveryContext.Split('\n');
        var hitLine = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (keywords.Any(k => lines[i].Contains(k, StringComparison.OrdinalIgnoreCase))) { hitLine = i; break; }
        }

        if (hitLine < 0)
            return discoveryContext[..maxChars] + "\n...(truncated)";


        var charOffset = lines.Take(hitLine).Sum(l => l.Length + 1);
        var halfWindow = maxChars / 2;
        var start = Math.Max(0, charOffset - halfWindow);
        var end = Math.Min(discoveryContext.Length, start + maxChars);
        var prefix = start > 0 ? "...(truncated head)...\n" : "";
        var suffix = end < discoveryContext.Length ? "\n...(truncated tail)..." : "";
        return prefix + discoveryContext[start..end] + suffix;
    }
    public static string? ExtractFileSectionFromContext(string discoveryContext, string filePath)
    {
        if (string.IsNullOrWhiteSpace(discoveryContext) || string.IsNullOrWhiteSpace(filePath))
            return null;
        var normPath = filePath.Replace('\\', '/').TrimStart('/');
        var fileName = Path.GetFileName(normPath);
        var lines = discoveryContext.Split('\n');
        var startLine = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if ((trimmed.StartsWith("### read ") || trimmed.StartsWith("### list ")) &&
                trimmed.EndsWith(normPath, StringComparison.OrdinalIgnoreCase))
            { startLine = i; break; }
            if (trimmed.StartsWith("### ") && trimmed.EndsWith(normPath, StringComparison.OrdinalIgnoreCase))
            { startLine = i; break; }
        }
        if (startLine < 0)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if ((trimmed.StartsWith("### read ") || trimmed.StartsWith("### list ") || trimmed.StartsWith("### ")) &&
                    trimmed.EndsWith(fileName, StringComparison.OrdinalIgnoreCase) &&
                    trimmed.IndexOfAny(new[] { ' ', '\t' }) > 3)
                { startLine = i; break; }
            }
        }
        if (startLine < 0) return null;
        var endLine = startLine + 1;
        for (var i = startLine + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("### ") && !trimmed.StartsWith("####"))
            { endLine = i; break; }
        }
        return string.Join("\n", lines.Skip(startLine).Take(endLine - startLine));
    }
    public static bool JsMethodExistsInContent(string content, string methodName)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(methodName))
            return false;
        if (methodName.Length < 2) return false;

        var name = Regex.Escape(methodName);


        if (Regex.IsMatch(content, $@"\bfunction\s+{name}\s*\(", RegexOptions.IgnoreCase))
            return true;


        if (Regex.IsMatch(content,
            $@"\b(?:const|let|var)\s+{name}\s*=\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase))
            return true;


        if (Regex.IsMatch(content,
            $@"\b(?:const|let|var)\s+{name}\s*=\s*(?:async\s+)?\([^)]*\)\s*=>",
            RegexOptions.IgnoreCase))
            return true;


        if (Regex.IsMatch(content,
            $@"\b{name}\s*:\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase))
            return true;


        if (Regex.IsMatch(content,
            $@"\b{name}\s*:\s*(?:async\s+)?\([^)]*\)\s*=>",
            RegexOptions.IgnoreCase))
            return true;



        if (Regex.IsMatch(content,
            $@"(?m)^\s*(?:static\s+|async\s+|get\s+|set\s+)?{name}\s*\(",
            RegexOptions.IgnoreCase))
            return true;


        if (Regex.IsMatch(content,
            $@"\b(?:vm|this|self|that)\.{name}\s*=\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase))
            return true;


        if (Regex.IsMatch(content,
            $@"\b(?:vm|this|self|that)\.{name}\s*=\s*(?:async\s+)?\([^)]*\)\s*=>",
            RegexOptions.IgnoreCase))
            return true;


        if (Regex.IsMatch(content,
            $@"\.prototype\.{name}\s*=\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase))
            return true;


        if (Regex.IsMatch(content,
            $@"\bexport\s+(?:async\s+)?function\s+{name}\s*\(",
            RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(content,
            $@"\bexport\s+(?:const|let|var)\s+{name}\s*=",
            RegexOptions.IgnoreCase))
            return true;


        if (Regex.IsMatch(content,
            $@"Object\.defineProperty\s*\([^,]+,\s*['""]{name}['""]",
            RegexOptions.IgnoreCase))
            return true;



        if (Regex.IsMatch(content,
            $@"(?m)^\s*(?:public\s+|private\s+|protected\s+|static\s+|async\s+|get\s+|set\s+|readonly\s+)*{name}\s*\(",
            RegexOptions.IgnoreCase))
            return true;

        return false;
    }




    public static string? ExtractJsMethodNameFromCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;


        var m = Regex.Match(code, @"\bfunction\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*\(", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;


        m = Regex.Match(code,
            @"\b(?:const|let|var)\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;


        m = Regex.Match(code,
            @"\b(?:const|let|var)\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:async\s+)?\([^)]*\)\s*=>",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;


        m = Regex.Match(code,
            @"\b(?:vm|this|self|that)\.([A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;


        m = Regex.Match(code,
            @"\b(?:vm|this|self|that)\.([A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:async\s+)?\([^)]*\)\s*=>",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;


        m = Regex.Match(code,
            @"\b([A-Za-z_$][A-Za-z0-9_$]*)\s*:\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;


        m = Regex.Match(code,
            @"\b([A-Za-z_$][A-Za-z0-9_$]*)\s*:\s*(?:async\s+)?\([^)]*\)\s*=>",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;


        m = Regex.Match(code,
            @"(?m)^\s*(?:static\s+|async\s+|get\s+|set\s+)?([A-Za-z_$][A-Za-z0-9_$]*)\s*\(");
        if (m.Success) return m.Groups[1].Value;


        m = Regex.Match(code,
            @"\.prototype\.([A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;


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


        if (gcd is >= 1 and <= 8) return gcd;



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


                next.Change = $"{next.Change} Include an inline CREATE TABLE IF NOT EXISTS statement " +
                               $"at the top of the method body before any INSERT/UPDATE/SELECT.";
                merged.Add(next);
                i++;
                continue;
            }
            merged.Add(cur);
        }
        return merged;
    }
    public static string StripSpuriousBlankLines(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return code;

        var lines = code.Split('\n');
        if (lines.Length < 6) return code;

        var codeCount = lines.Count(l => !string.IsNullOrWhiteSpace(l));
        var blankCount = lines.Count(l => string.IsNullOrWhiteSpace(l));
        if (codeCount < 3 || blankCount < codeCount * 0.7) return code;
 
        var alternating = 0;
        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]) &&
                string.IsNullOrWhiteSpace(lines[i + 1]))
                alternating++;
        }

        if (alternating < codeCount * 0.5) return code;
 
        var result = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                var hasPrev = result.Count > 0 && !string.IsNullOrWhiteSpace(result[^1]);
                var hasNext = i + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[i + 1]);
                if (hasPrev && hasNext)
                { 
                    var prevTrimmed = result[^1].TrimEnd();
                    var prevIndent = result[^1].TakeWhile(c => c == ' ' || c == '\t').Count();
                    var nextIndent = lines[i + 1].TakeWhile(c => c == ' ' || c == '\t').Count();

                    if ((prevTrimmed.EndsWith(';') || prevTrimmed.EndsWith('}')) &&
                        Math.Abs(prevIndent - nextIndent) <= 1 &&
                        (i == 0 || i - 1 < 0 || string.IsNullOrWhiteSpace(lines[i - 1]) == false))
                    {
                        // Check if line before prev was also blank — if so, skip
                        if (result.Count > 1 && string.IsNullOrWhiteSpace(result[^2]))
                            continue;
                        result.Add(lines[i]);
                        continue;
                    }
                    continue; // Skip spurious blank
                }
            }
            result.Add(lines[i]);
        }

        return string.Join("\n", result);
    }
    public static string CleanVerbatimStringEscapes(string content)
    {
        if (string.IsNullOrEmpty(content)) return content; 

        var regex = new Regex(@"@""(?:""|[^""])*""", RegexOptions.Compiled);
        bool changed = false;

        var result = regex.Replace(content, match =>
        {
            var val = match.Value;


            var inside = val.Substring(2, val.Length - 3);

            bool hasEscapeSeq = inside.Contains(@"\r\n") || inside.Contains(@"\r") || inside.Contains(@"\n") || inside.Contains(@"\t");
            bool looksLikeSql = Regex.IsMatch(inside, @"\b(SELECT|INSERT|UPDATE|DELETE|CREATE\s+TABLE|ALTER\s+TABLE|DROP\s+TABLE|FROM|WHERE|JOIN|VALUES|SET)\b", RegexOptions.IgnoreCase);


            if (hasEscapeSeq && looksLikeSql)
            {
                changed = true;
                var fixedInside = inside
                    .Replace(@"\r\n", "\r\n")
                    .Replace(@"\r", "\r")
                    .Replace(@"\n", "\n")
                    .Replace(@"\t", "\t");


                return "@\"" + fixedInside + "\"";
            }
            return val;
        });

        return changed ? result : content;
    }





    public static string PostEditCSharpFixup(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return content;

        content = CleanVerbatimStringEscapes(content);

        var flatPattern = new Regex(@"\.(SystemSpecs|System|HardwareInfo|Hardware|Specs|SystemInfo|MetaInfo|Details|DataInfo|BenchmarkInfo|BenchData)\??\.([A-Z]\w+)", RegexOptions.IgnoreCase);
        content = flatPattern.Replace(content, m => "." + m.Groups[2].Value);

        content = Regex.Replace(content, @"(\$""[^""]*)\{\{(\w+(?:\.\w+)+)\}\}([^""]*"")", "$1{$2}$3");

        content = Regex.Replace(content,
            @"decimal\.TryParse\s*\(\s*\w+\.Score\??(?:\.Replace\s*""[^""]*""(?:\s*,\s*""[^""]*"")?)?\s*,(\s*out\s+\w+(?:\.\w+)*\s*)\)",
            m =>
            {
                var outVar = m.Groups[1].Value.Trim();

                return $"decimal.TryParse(benchmark.Score, {outVar})";
            });

        content = Regex.Replace(content,
            @"(?<=[^ \t\r\n@])""\s*\r?\n[ \t]*;",
            @""";");

        return content;
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

        content = Regex.Replace(content, @"(?<!\\)([""'])([^\r\n]*?)(?<!\\)\1", m =>
        {
            var quote = m.Groups[1].Value;
            var strContent = m.Groups[2].Value;


            if (strContent.StartsWith(quote)) return m.Value;

            if (strContent.EndsWith(" ") || strContent.EndsWith("\t"))
            {
                strContent = strContent.TrimEnd(' ', '\t');
                return $"{quote}{strContent}{quote}";
            }
            return m.Value;
        });


        content = Regex.Replace(content, @"[ \t]+\r?\n", "\n");

        var pyKeywords = "print|return|if|for|while|def|class|import|from|with|try|except|finally|raise|yield|assert|del|global|nonlocal|pass|break|continue";


        content = Regex.Replace(content, $@"\)\s*({pyKeywords})\b", ")\n$1");

        content = Regex.Replace(content, $@";\s*({pyKeywords})\b", ";\n$1");

        content = Regex.Replace(content, $@"\]\s*({pyKeywords})\b", "]\n$1");

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


            if (inCodeBlock)
            {

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


                if (trimmed.StartsWith("}") || trimmed.StartsWith(")") || trimmed.StartsWith("]"))
                {
                    currentJsIndent = Math.Max(0, currentJsIndent - 2);
                }

                result.Add(new string(' ', currentJsIndent) + trimmed);


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


            value = Regex.Replace(value, @",(?!\s)", ", ");


            value = Regex.Replace(value, @"(\d(?:px|pt|em|rem|ex|ch|vw|vh|vmin|vmax|%|deg|s|ms|fr|dpi|dppx|dpcm|Hz|kHz))(?=\d)", "$1 ");


            value = Regex.Replace(value, @"(\d(?:px|pt|em|rem|ex|ch|vw|vh|vmin|vmax|%|deg|s|ms|fr|dpi|dppx|dpcm|Hz|kHz))(?=[a-z])", "$1 ");


            value = Regex.Replace(value, @"(?<!#)([a-z])(\d)", "$1 $2");


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

    public static bool IsSpecialMarker(string? file) => file != null && (
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
        file.Equals("_explore", StringComparison.OrdinalIgnoreCase));

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

    public static bool HasSuccessfulEdits(IEnumerable<object> steps) =>
        steps.OfType<Dictionary<string, object?>>().Any(s =>
            s.TryGetValue("type", out var t) &&
            (string.Equals(t?.ToString(), "edit", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(t?.ToString(), "rename", StringComparison.OrdinalIgnoreCase)) &&
            s.TryGetValue("status", out var st) && st?.ToString() == "done");


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


                if (afterPos >= text.Length || text[afterPos] == ',' || text[afterPos] == '}' || text[afterPos] == ']')
                    return (UnescapeJsonString(text.Substring(start, pos - start)), pos + 1);


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


        if (nextKeyPos > start + 1 && nextKeyPos < int.MaxValue)
        {
            var end = nextKeyPos - 1;
            while (end > start && text[end] != '"') end--;
            if (end > start && text[end] == '"')
                return (UnescapeJsonString(text.Substring(start, end - start)), end + 1);
        }

        return null;
    }








    public static List<string> ExtractMeaningfulKeywords(string lower)
    {
        var stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {

            "the","a","an","and","or","but","in","on","at","to","for","of","with","from",
            "into","onto","upon","after","before","about","above","below","between",

            "this","that","it","its","their","our","my","your","his","her","we","they","i",

            "is","are","was","were","be","been","being","have","has","had",
            "do","does","did","will","would","should","could","may","might","shall",

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

            "more","less","some","any","all","no","not","also","very","just",
            "nice","nicely","good","better","best","new","old","right","left",
            "please","sure","now","then","when","where","how","why","what","which","who",
            "out","up","down","so","if","else","really","quite","bit","little","lot",

            "need","want","should","must","can","let","help","try","look","see"
        };

        return Regex.Matches(lower, @"\b[a-z]{3,}\b")
            .Select(m => m.Value)
            .Where(w => !stopwords.Contains(w))
            .Distinct()
            .Take(10)
            .ToList();
    }






    public static List<string> ApplyTaskTypeHeuristics(string prompt, List<string> allFiles)
    {
        var lower = prompt.ToLowerInvariant();


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


            foreach (var kw in meaningfulKeywords)
                if (nameLow.Contains(kw))
                    score += 50;


            if ((isStyleTask || isHtmlTask || isJsTask) && pathLow.StartsWith("wwwroot/"))
                score += 25;



            if (nameLow.Contains("agentcontroller")) score -= 200;
            if (nameLow == "filehints") score -= 200;
            if (pathLow.EndsWith(".min.js")) score -= 300;
            if (pathLow.EndsWith(".min.css")) score -= 300;


            if (ext is ".dll" or ".exe" or ".pdb" or ".nupkg" or ".lock" or ".sum")
                score -= 1000;

            return (file: f, score);
        })
        .Where(x => x.score > 0)
        .OrderByDescending(x => x.score)
        .Take(50)
        .Select(x => x.file)
        .ToList();



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






    public static string ExtractRelevantExcerpt(string fileContent, string changeDesc, string? planOldString, int fileBodyTruncation = 8000)
    {
        const int RadiusLines = 60;
        var lines = fileContent.Split('\n');


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


            if (Regex.IsMatch(trimmed, @"^(using|import|namespace|package|from|export|#include|@|\[)", RegexOptions.IgnoreCase))
            {
                structEnd = i + 1;
                continue;
            }


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


        var header = string.Join('\n', lines.Take(structEnd));

        if (targetStart < 0)
        {

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


    private static bool TryNormalizeSkeletonSignature(string line, out string signature)
    {
        signature = null!;
        if (string.IsNullOrWhiteSpace(line)) return false;
        var l = line.Trim();

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


        var tsDecl = Regex.Match(l, @"^\s*(export\s+)?(interface|class)\s+([A-Za-z_][\w]*)", RegexOptions.IgnoreCase);
        if (tsDecl.Success)
        {
            var expo = tsDecl.Groups[1].Value;
            var kind = tsDecl.Groups[2].Value;
            var name = tsDecl.Groups[3].Value;
            signature = (expo + kind + " " + name).Trim() + " { ... }";
            return true;
        }


        var tsMethod = Regex.Match(l, @"^\s*(async\s+)?([A-Za-z_][\w]*)\s*\([^\)]*\)\s*(:\s*[\w<>,\s\[\]]+)?\s*\{?", RegexOptions.IgnoreCase);
        if (tsMethod.Success)
        {
            var async = tsMethod.Groups[1].Value;
            var name = tsMethod.Groups[2].Value;
            signature = (async + name).Trim() + "() { ... }";
            return true;
        }


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


        var goFunc = Regex.Match(l, @"^\s*func\s*(?:\(([^\)]*)\)\s*)?([A-Za-z_][\w]*)\s*\(", RegexOptions.IgnoreCase);
        if (goFunc.Success)
        {
            var recv = goFunc.Groups[1].Value?.Trim();
            var name = goFunc.Groups[2].Value;
            signature = (string.IsNullOrEmpty(recv) ? $"func {name}" : $"func ({recv}) {name}") + "() { ... }";
            return true;
        }


        var rustFn = Regex.Match(l, @"^\s*(pub\s+)?fn\s+([A-Za-z_][\w]*)\s*\(", RegexOptions.IgnoreCase);
        if (rustFn.Success)
        {
            var pub = rustFn.Groups[1].Value;
            var name = rustFn.Groups[2].Value;
            signature = (pub + "fn " + name).Trim() + "() { ... }";
            return true;
        }


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





    public static bool NormalizeSkeletonSignatureForTest(string line, out string signature) => TryNormalizeSkeletonSignature(line, out signature);





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


        sb.Append(turns[0]);
        for (var i = Math.Max(1, turns.Length - keepLastTurns); i < turns.Length; i++)
            sb.Append("Command [").Append(turns[i]);

        conversation.Clear();
        conversation.Append(sb.ToString());
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

    private static readonly HashSet<string> _whitespaceSignificantExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".pyi", ".pyw",
        ".yaml", ".yml",
        ".coffee",
        ".haml", ".slim", ".pug", ".jade",
        ".fs", ".fsx", ".fsi",
        ".nim",
        ".sass",
        ".hs", ".lhs",
        ".elm",
        ".ml", ".mli",
    };
    private static readonly HashSet<string> _endKeywordLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        ".rb",
        ".lua",
        ".ex", ".exs",
        ".sh", ".bash", ".zsh", ".fish",
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
            "length",
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

            ".cs" => ("brace", true,
                "⚠ C# FILE: " +
                "USE FORMAT C (targetType/targetName/newCode) for FULL METHOD replacements or to ADD a new method (via insertAfter:true). " +
                "For SMALL targeted edits (1-5 lines, e.g. adding a field/property, changing a return value): " +
                "USE oldString/newString. This is the ONLY safe way to add properties/fields. " +
                "Do NOT use targetType='class' to add properties/fields. " +
                "INDENTATION: method signature at class-member level, body indented 4 spaces more."),


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


            ".go" => ("brace", true,
                "⚠ GO FILE: brace-based, uses TABS (not spaces) for indentation — never convert tabs to spaces. " +
                "FORMAT C supported: targetType='function', targetName='FunctionName'. " +
                "Preserve ALL error-handling idioms (if err != nil), defer statements, and goroutine patterns."),


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


            ".php" => ("brace", true,
                "⚠ PHP FILE: brace-based. FORMAT C supported: targetType='function'/'method'/'class'. " +
                "Preserve $ sigils on all variables, type hints, and nullable ? modifiers exactly."),
            ".dart" => ("brace", true,
                "⚠ DART FILE: brace-based. FORMAT C supported: targetType='function'/'class'. " +
                "Preserve async/await, null-safety operators (?., ??, ??=, !), and Widget tree indentation."),
            ".groovy" => ("brace", true,
                "⚠ GROOVY FILE: brace-based (Gradle/Groovy DSL). FORMAT C supported: targetType='method'. " +
                "Preserve closure syntax { ... }, GString interpolation, and Gradle DSL patterns."),


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


            ".css" or ".scss" or ".less" => ("brace", false,
                "⚠ CSS/SCSS/LESS FILE: brace-based selectors. Use oldString/newString. " +
                "CRITICAL: oldString MUST be at most 4 lines — never replace an entire CSS block. " +
                "To change a CSS property value, set oldString to the ONE line containing that property " +
                "(copied verbatim from the file), and newString to that line with the new value. " +
                "Example: if changing `flex-direction: row;` to `flex-direction: column;`, " +
                "oldString = \"  flex-direction: row;\" (exact whitespace), newString = \"  flex-direction: column;\". " +
                "Preserve ALL whitespace in property values (e.g. '0 1px 2px rgba(0,0,0,0.5)' — " +
                "every space and comma is significant). Preserve SCSS variables ($var), mixins, and nesting."),


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


            ".sql" => ("plain", false,
                "⚠ SQL FILE: use oldString/newString. Preserve ALL whitespace in multi-line queries. " +
                "Match exact keyword casing (uppercase SQL keywords are conventional). " +
                "Preserve semicolons and comment styles (-- vs)."),
            ".graphql" or ".gql" => ("plain", false,
                "⚠ GRAPHQL FILE: use oldString/newString. Preserve type definitions, " +
                "field arguments, and directive (@deprecated, @skip) syntax exactly."),


            ".md" or ".mdx" => ("plain", false,
                "⚠ MARKDOWN FILE: use oldString/newString. " +
                "Preserve heading levels (# vs ##), list markers (-, *, 1.), " +
                "and fenced code block language tags exactly."),
            ".rst" => ("indent", false,
                "⚠ RST FILE: indentation-significant section underlines. Use oldString/newString. " +
                "Preserve directive syntax (.. directive::) and role syntax (:role:`text`)."),


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
    public static string? ExtractTargetSymbolFromChange(string change)
    {
        if (string.IsNullOrWhiteSpace(change)) return null;

        var m = Regex.Match(change, @"\b(?:class|struct|interface|record)\s+([A-Za-z_]\w*)", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;

        m = Regex.Match(change, @"\b([A-Z]\w*(?:DTO|Dto|Model|Request|Response|Controller|Service))\b");
        if (m.Success) return m.Groups[1].Value;

        m = Regex.Match(change, @"\bmethod\s+([A-Za-z_]\w*)", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;

        m = Regex.Match(change, @"\b(?:in|inside)\s+(?:the\s+)?([A-Z]\w+)\b");
        if (m.Success) return m.Groups[1].Value;

        return null;
    }
    public static string CollapseExcessiveBlankLines(string content, string appliedNewStr)
    {
        if (string.IsNullOrWhiteSpace(appliedNewStr) || string.IsNullOrWhiteSpace(content))
            return content;

        var fileLines = content.Split('\n');

        // Build a set of distinctive needle lines from the edited region
        var needleLines = appliedNewStr.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l) && l.Length >= 5)
            .ToHashSet(StringComparer.Ordinal);

        if (needleLines.Count < 3) return content;
 
        var startIdx = -1;
        var endIdx = -1;
        for (var i = 0; i < fileLines.Length; i++)
        {
            if (needleLines.Contains(fileLines[i].Trim()))
            {
                if (startIdx < 0) startIdx = i;
                endIdx = i;
            }
        }

        if (startIdx < 0 || endIdx <= startIdx) return content; 

        startIdx = Math.Max(0, startIdx - 1);
        endIdx = Math.Min(fileLines.Length - 1, endIdx + 1);
 
        var regionLength = endIdx - startIdx + 1;
        var regionCodeLines = 0;
        var regionBlankLines = 0;
        var regionAlternating = 0;

        for (var i = startIdx; i <= endIdx; i++)
        {
            if (string.IsNullOrWhiteSpace(fileLines[i]))
                regionBlankLines++;
            else
                regionCodeLines++;

            if (i < endIdx &&
                !string.IsNullOrWhiteSpace(fileLines[i]) &&
                string.IsNullOrWhiteSpace(fileLines[i + 1]))
                regionAlternating++;
        }
 
        if (regionCodeLines < 3 || regionAlternating < regionCodeLines * 0.5)
            return content;
 
        var result = new List<string>();
        for (var i = 0; i < fileLines.Length; i++)
        {
            if (i < startIdx || i > endIdx)
            {
                result.Add(fileLines[i]);
                continue;
            }

            // In the edited region
            if (string.IsNullOrWhiteSpace(fileLines[i]))
            {
                // Check: is this a spurious blank line (between two code lines)?
                var prevIsCode = i > startIdx && !string.IsNullOrWhiteSpace(fileLines[i - 1]);
                var nextIsCode = i < endIdx && !string.IsNullOrWhiteSpace(fileLines[i + 1]);

                if (prevIsCode && nextIsCode)
                {
                    // Check if the previous result line is already blank — don't stack
                    if (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1]))
                        continue;

                    // Check if this blank line is at a logical boundary:
                    // Keep it only if the preceding code line ends with '}' or ';'
                    // AND the following code line starts a new logical block
                    var prevTrimmed = fileLines[i - 1].TrimEnd();
                    var nextTrimmed = fileLines[i + 1].TrimStart();

                    // If previous line ends with ';' or '}' and next starts a new
                    // statement that's at the same indentation level, keep one blank
                    var prevIndent = fileLines[i - 1].TakeWhile(c => c == ' ' || c == '\t').Count();
                    var nextIndent = fileLines[i + 1].TakeWhile(c => c == ' ' || c == '\t').Count();

                    // If both lines are at the same base indent and prev ends with ; or },
                    // this MIGHT be an intentional separator — keep it only if there
                    // wasn't already a blank line before the previous code line
                    if ((prevTrimmed.EndsWith(';') || prevTrimmed.EndsWith('}')) &&
                        prevIndent == nextIndent)
                    {
                        // Check if the line before prev was also blank — if so, this
                        // is part of the alternating pattern, skip it
                        if (i > startIdx + 1 && string.IsNullOrWhiteSpace(fileLines[i - 2]))
                            continue;
                        result.Add(fileLines[i]);
                        continue;
                    }

                    // Spurious blank line — skip it
                    continue;
                }

                result.Add(fileLines[i]);
            }
            else
            {
                result.Add(fileLines[i]);
            }
        }

        var fixedContent = string.Join("\n", result);

        // Normalize: collapse 3+ consecutive blank lines to 2 (preserve paragraph breaks)
        fixedContent = Regex.Replace(fixedContent, @"\n{4,}", "\n\n\n");

        return fixedContent != content ? fixedContent : content;
    }
    public static string? DetectExcessiveBlankLines(string newStr)
    {
        if (string.IsNullOrWhiteSpace(newStr)) return null;

        var lines = newStr.Split('\n');
        if (lines.Length < 6) return null;

        var codeLines = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var blankLines = lines.Where(l => string.IsNullOrWhiteSpace(l)).ToList();

        if (codeLines.Count < 3) return null;

        // Count the alternating pattern: code line → blank line → code line
        var alternating = 0;
        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]) &&
                string.IsNullOrWhiteSpace(lines[i + 1]))
                alternating++;
        }

        // If ≥60% of code lines are followed by a blank line, it's the spurious pattern
        if (alternating >= codeLines.Count * 0.6)
        {
            return $"EXCESSIVE BLANK LINES — newString has a blank line between nearly every code line " +
                   $"({blankLines.Count} blank lines for {codeLines.Count} code lines, {alternating} alternating). " +
                   "Remove the spurious blank lines. Statements should be on consecutive lines with normal spacing " +
                   "(one blank line between logical sections at most, not between every single line).";
        }

        return null;
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
            if (trimmed.Length < 15) continue;
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
        const string IndentStep = "  ";
        var lines = html.Split('\n');

        var distinctDepths = lines
            .Where(l => l.Trim().Length > 0)
            .Select(l => GetLeadingWhitespace(l).Length)
            .Distinct().Count();
        if (distinctDepths > 1) { return html; }

        var depth = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0) continue;

            if (Regex.IsMatch(trimmed, @"^</[\w-]"))
            { depth = Math.Max(0, depth - 1); }

            lines[i] = baseIndent + new string(' ', depth * IndentStep.Length) + trimmed;


            var tagMatch = Regex.Match(trimmed, @"^<([\w-]+)[\s>]");
            if (tagMatch.Success)
            {
                var tag = tagMatch.Groups[1].Value;
                var isSelfClosing = trimmed.EndsWith("/>") || VoidHtmlElements.Contains(tag);
                var closedInline = trimmed.Contains($"</{tag}>");
                var isClosing = trimmed.StartsWith("</");
                var isComment = trimmed.StartsWith("<!--");
                if (!isSelfClosing && !closedInline && !isClosing && !isComment)
                { depth++; }
            }
        }

        return string.Join("\n", lines);
    }

    public static string AutoIndentFromFile(string replacement, string fileIndent, string[] fileLines, int start)
    {
        if (!replacement.Contains('{') && !replacement.Contains('}'))
        { return replacement; }

        var indentSize = InferIndentSize(fileLines, start);
        if (indentSize <= 0) return replacement;

        var lines = replacement.Split('\n');
        var depth = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;

            var trimmed = lines[i].TrimStart();

            var lineDepth = depth;
            if (trimmed.StartsWith("}"))
                lineDepth = Math.Max(0, lineDepth - 1);

            var expectedIndent = fileIndent + new string(' ', lineDepth * indentSize);
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
        if (deltas.Count == 0) return 2;

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
    private static string? ExtractDelimitedSection(string text, string sectionName)
    {
        var pattern = $@"<<<{sectionName}>>>\s*(.*?)(?=<<<|$)";
        var m = Regex.Match(text, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    public static double ComputeLineSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 1.0;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
        var aNorm = a.Trim().ToLowerInvariant();
        var bNorm = b.Trim().ToLowerInvariant();
        var maxLen = Math.Max(aNorm.Length, bNorm.Length);
        if (maxLen == 0) return 1.0;

        if (maxLen <= 80)
            return 1.0 - (double)AgentUtilities.ComputeLevenshteinDistance(aNorm, bNorm) / maxLen;

        var common = 0; var minLen = Math.Min(aNorm.Length, bNorm.Length);
        for (var i = 0; i < minLen; i++) { if (aNorm[i] == bNorm[i]) common++; else break; }
        return (double)common / maxLen;
    }


    public static (bool replaced, string newContent, string? matchError, string? snippet) TryReplaceSafe(
     string fileContent, string oldStr, string newStr, int targetLine = 0, string? changeDesc = null)
    {
        if (string.IsNullOrEmpty(oldStr) && string.IsNullOrEmpty(fileContent) && !string.IsNullOrEmpty(newStr))
            return (true, newStr, null, null);


        if (string.IsNullOrEmpty(oldStr) && !string.IsNullOrEmpty(fileContent))
        {
            return (false, fileContent,
                "oldString is empty but the file is non-empty — refusing to perform an unbounded replacement. " +
                "Provide a non-empty, specific anchor.", null);
        }

        var normFile = AgentUtilities.NormalizeLineEndings(fileContent);
        var normOld = AgentUtilities.NormalizeLineEndings(oldStr);


        var matches = new List<int>();
        var searchPos = 0;
        var maxIterations = normFile.Length + 2;
        var iterations = 0;
        while (iterations++ < maxIterations)
        {
            var idx = normFile.IndexOf(normOld, searchPos, StringComparison.Ordinal);
            if (idx < 0) break;
            matches.Add(idx);
            searchPos = idx + Math.Max(1, normOld.Length);
        }


        if (matches.Count == 1)
        {
            var normNew = AgentUtilities.NormalizeLineEndings(newStr);
            return (true, normFile[..matches[0]] + normNew + normFile[(matches[0] + normOld.Length)..], null, null);
        }

        if (matches.Count > 1)
        {
            int chosenIdx = -1;


            var keywords = AgentUtilities.ExtractDisambiguationKeywords(changeDesc);
            if (keywords.Count > 0)
            {
                int bestContextScore = -1;
                for (int i = 0; i < matches.Count; i++)
                {
                    var lookbackStart = Math.Max(0, matches[i] - 2000);
                    var context = normFile.Substring(lookbackStart, matches[i] - lookbackStart).ToLowerInvariant();
                    var score = keywords.Count(k => context.Contains(k));

                    if (score > bestContextScore)
                    {
                        bestContextScore = score;
                        chosenIdx = i;
                    }
                }
            }


            if (chosenIdx == -1 && targetLine > 0)
            {
                var bestDist = int.MaxValue;
                for (int i = 0; i < matches.Count; i++)
                {
                    var matchLine = normFile[..matches[i]].Count(c => c == '\n') + 1;
                    var dist = Math.Abs(matchLine - targetLine);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        chosenIdx = i;
                    }
                }


                if (bestDist > 50) chosenIdx = -1;
            }

            if (chosenIdx >= 0)
            {
                var normNew = AgentUtilities.NormalizeLineEndings(newStr);
                return (true, normFile[..matches[chosenIdx]] + normNew + normFile[(matches[chosenIdx] + normOld.Length)..], null, null);
            }

            var firstLine = normOld.Split('\n')[0].Trim();
            var uniqueLine = AgentUtilities.ExtractMostUniqueLine(normOld, normFile);

            var err = $"oldString found {matches.Count} times in file — include more surrounding lines as anchor context.";
            if (uniqueLine != null)
                err += $" OR use ONLY this unique line as your entire oldString: `{uniqueLine.Trim()}`";

            return (false, fileContent, err, firstLine);
        }


        var firstRealLine = normOld.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (firstRealLine != null)
        {
            var fuzzyIdx = normFile.IndexOf(firstRealLine, StringComparison.Ordinal);
            if (fuzzyIdx >= 0)
            {
                var lineStart = normFile.LastIndexOf('\n', fuzzyIdx) + 1;
                var fileSegment = normFile[lineStart..];
                if (fileSegment.StartsWith(normOld.TrimStart()))
                {
                    var normNew = AgentUtilities.NormalizeLineEndings(newStr);
                    return (true, normFile[..lineStart] + normNew + normFile[(lineStart + normOld.Length)..], null, null);
                }
            }
        }

        return (false, fileContent, "oldString not found verbatim in file", null);
    }

    public static string StripFullFileFence(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var cleaned = value.Replace("\r\n", "\n");
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = cleaned.IndexOf('\n');
            if (firstNewline >= 0)
                cleaned = cleaned[(firstNewline + 1)..];
            else
                return string.Empty;
        }

        if (cleaned.EndsWith("```", StringComparison.Ordinal))
            cleaned = cleaned[..^3];

        return cleaned.TrimStart('\n').TrimEnd('\n');
    }


    public static string RepairJsonNewlines(string json)
    {
        var sb = new StringBuilder(json.Length);
        var inString = false;
        var escaped = false;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (escaped) { sb.Append(c); escaped = false; continue; }
            if (c == '\\' && inString) { sb.Append(c); escaped = true; continue; }
            if (c == '"' && !escaped)
            {

                if (!inString) { inString = true; sb.Append(c); continue; }



                var lookahead = json.Length > i + 1 ? json[i + 1] : '\0';
                if (lookahead == ',' || lookahead == ']' || lookahead == '}' ||
                    lookahead == ':' || lookahead == '\t' ||
                    lookahead == '\n' || lookahead == '\r' || lookahead == ' ')
                {
                    inString = false; sb.Append(c);
                }
                else
                {
                    sb.Append("\\\"");
                }
                continue;
            }
            if (inString && (c == '\n' || c == '\r'))
            {
                sb.Append("\\n");
                if (c == '\r' && i + 1 < json.Length && json[i + 1] == '\n') i++;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    public static List<string> ExtractQuotedStrings(string raw)
    {
        var result = new List<string>();
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim().TrimEnd(',');
            if (trimmed.Length < 2 || !trimmed.StartsWith("\"")) continue;
            var lastQuote = trimmed.LastIndexOf('"');
            if (lastQuote <= 0) continue;
            result.Add(trimmed.Substring(1, lastQuote - 1).Replace("\\\"", "\""));
        }
        return result;
    }

    public static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder();
        var inQuote = false;
        var quoteChar = '\0';
        var prevWasSpace = false;

        foreach (var c in s)
        {
            if ((c == '"' || c == '\'' || c == '`') && (sb.Length == 0 || sb[sb.Length - 1] != '\\'))
            {
                if (!inQuote) { inQuote = true; quoteChar = c; }
                else if (c == quoteChar) { inQuote = false; }
            }

            if (inQuote)
            {
                sb.Append(c);
                prevWasSpace = false;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (!prevWasSpace && sb.Length > 0) { sb.Append(' '); prevWasSpace = true; }
            }
            else
            {
                sb.Append(c);
                prevWasSpace = false;
            }
        }

        return sb.ToString().Trim();
    }

    public static string? BuildExactMatchBlock(string fileContent, string oldStr, int targetLine = 0, string? changeDesc = null)
    {
        if (string.IsNullOrWhiteSpace(oldStr)) return null;
        var normFile = AgentUtilities.NormalizeLineEndings(fileContent);

        var changeLower = (changeDesc ?? "").ToLowerInvariant();
        bool isRemoval = changeLower.Contains("remove") ||
            (changeLower.Contains("delete") && !Regex.IsMatch(changeLower, @"\b(add|create|insert|implement)\b"));

        if (!isRemoval)
        {
            var htmlBlock = AgentUtilities.ExtractFullHtmlBlock(normFile, oldStr);
            if (htmlBlock != null) return htmlBlock;
        }

        var normOld = AgentUtilities.NormalizeLineEndings(oldStr);
        var oldLines = normOld.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (oldLines.Count == 0) return null;

        var fileLines = normFile.Split('\n');
        var candidates = new List<(int startIdx, int score)>();

        for (var i = 0; i < fileLines.Length; i++)
        {
            var score = 0;
            var fIdx = i;
            var oIdx = 0;

            while (fIdx < fileLines.Length && oIdx < oldLines.Count)
            {
                var fileTrim = fileLines[fIdx].Trim();
                var oldTrim = oldLines[oIdx].Trim();

                if (fileTrim == oldTrim || fileTrim.StartsWith(oldTrim) || oldTrim.StartsWith(fileTrim))
                {
                    score++;
                    fIdx++;
                    oIdx++;
                }
                else if (string.IsNullOrEmpty(fileTrim) || string.IsNullOrEmpty(oldTrim))
                {
                    if (string.IsNullOrEmpty(fileTrim)) fIdx++;
                    if (string.IsNullOrEmpty(oldTrim)) oIdx++;
                }
                else
                {
                    break;
                }
            }

            if (score >= Math.Max(1, oldLines.Count / 2))
            {
                candidates.Add((i, score));
            }
        }

        if (candidates.Count == 0) return null;

        int chosenCandidate = -1;

        if (candidates.Count == 1)
        {
            chosenCandidate = 0;
        }
        else
        {

            var keywords = AgentUtilities.ExtractDisambiguationKeywords(changeDesc);
            if (keywords.Count > 0)
            {
                int bestContextScore = -1;
                for (int i = 0; i < candidates.Count; i++)
                {
                    var startIdx = candidates[i].startIdx;
                    var lookbackStart = Math.Max(0, startIdx - 50);
                    var context = string.Join("\n", fileLines.Skip(lookbackStart).Take(startIdx - lookbackStart)).ToLowerInvariant();

                    var score = keywords.Count(k => context.Contains(k));
                    if (score > bestContextScore)
                    {
                        bestContextScore = score;
                        chosenCandidate = i;
                    }
                }
            }


            if (chosenCandidate == -1 && targetLine > 0)
            {
                int bestDist = int.MaxValue;
                for (int i = 0; i < candidates.Count; i++)
                {
                    var dist = Math.Abs(candidates[i].startIdx + 1 - targetLine);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        chosenCandidate = i;
                    }
                }
                if (bestDist > 50) chosenCandidate = -1;
            }
        }

        if (chosenCandidate >= 0)
        {
            var bestStart = candidates[chosenCandidate].startIdx;
            var endIdx = Math.Min(fileLines.Length, bestStart + oldLines.Count);
            var joined = string.Join("\n", fileLines.Skip(bestStart).Take(endIdx - bestStart));
            return string.IsNullOrWhiteSpace(joined) ? null : joined;
        }

        return null;
    }

    public static string? GetUnsafeEditPayloadReason(string oldString, string newString)
    {
        foreach (var marker in UnsafeEditMarkers)
            if (oldString.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                newString.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return $"Edit contains placeholder marker '{marker}'.";
        return null;
    }

    public static bool IsHtmlLikeContent(string content) =>
     content.Contains('<') && Regex.IsMatch(content, @"</?\w+[\s/>]");

    public static string IndentReplacement(string[] fileLines, int start, string replacement)
    {
        if (string.IsNullOrEmpty(replacement) || start >= fileLines.Length)
            return replacement;

        var fileIndent = AgentUtilities.GetLeadingWhitespace(fileLines[start]);
        if (fileIndent.Length == 0)
            return replacement;

        var replLines = replacement.Split('\n');
        var replBaseIndent = replLines.Where(l => l.Length > 0)
                                      .Select(AgentUtilities.GetLeadingWhitespace)
                                      .FirstOrDefault();

        if (replBaseIndent != null && replBaseIndent != fileIndent)
        {
            for (var i = 0; i < replLines.Length; i++)
            {
                if (replLines[i].Length == 0) continue;
                var lineIndent = AgentUtilities.GetLeadingWhitespace(replLines[i]);
                if (lineIndent.StartsWith(replBaseIndent, StringComparison.Ordinal))
                {
                    var excess = lineIndent[replBaseIndent.Length..];
                    replLines[i] = fileIndent + excess + replLines[i][lineIndent.Length..];
                }
                else
                {
                    replLines[i] = fileIndent + replLines[i];
                }
            }
        }

        if (IsHtmlLikeContent(replacement) && replLines.Length > 5)
        {
            return AgentUtilities.AutoIndentHtml(string.Join("\n", replLines), fileIndent);
        }

        var joined = string.Join("\n", replLines);
        var distinctIndentDepths = replLines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => AgentUtilities.GetLeadingWhitespace(l).Length)
            .Distinct()
            .Count();
        return distinctIndentDepths <= 1 && replLines.Length > 2
            ? AgentUtilities.AutoIndentFromFile(joined, fileIndent, fileLines, start)
            : joined;
    }

    public static string? BuildExactMatchHint(string content, string oldString)
    {
        var fileLines = content.Split('\n');
        var oldLines = oldString.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length >= 8)
            .ToList();
        if (oldLines.Count == 0 || fileLines.Length == 0) return null;

        bool IsTrivialLine(string line)
        {
            var t = line.Trim();
            if (t.Length < 12) return true;

            var meaningful = new string(t.Where(char.IsLetterOrDigit).ToArray());
            if (meaningful.Length < 12) return true;

            if (Regex.IsMatch(t, @"^\s*[\w-]+\s*:\s*[\w\d#.()-]+\s*;?\s*$"))
            {

                return true;
            }
            return false;
        }

        var results = new List<(int fileIdx, double score, string line)>();
        for (var fi = 0; fi < fileLines.Length; fi++)
        {
            var fLine = fileLines[fi];
            if (IsTrivialLine(fLine)) continue;
            var bestSim = oldLines.Max(o => ComputeLineSimilarity(fLine, o));
            if (bestSim >= 0.50)
                results.Add((fi, bestSim, fLine));
        }

        var best = results
            .OrderByDescending(r => r.score)
            .ThenByDescending(r => r.line.Trim().Length)
            .Take(3)
            .ToList();
        if (best.Count == 0) return null;

        var sb = new StringBuilder();
        foreach (var b in best)
        {
            sb.AppendLine($"  ({(b.score * 100):F0}% match) line {b.fileIdx + 1}: {b.line}");

            var llmLine = oldLines
                .OrderByDescending(o => ComputeLineSimilarity(b.line, o))
                .FirstOrDefault();
            if (llmLine != null && llmLine != b.line.Trim())
            {
                var fileTrimmed = b.line.Trim();
                var diff = DescribeLineDiff(llmLine, fileTrimmed);
                if (diff != null)
                    sb.AppendLine($"    └ DIFF: {diff}");
            }
        }
        return sb.ToString();
    }

    public static string? DescribeLineDiff(string llm, string file)
    {
        if (string.Equals(llm, file, StringComparison.Ordinal)) return null;

        var diffs = new List<string>();


        var llmNoCommaSpace = Regex.Replace(llm, @",\s*", ",");
        var fileNoCommaSpace = Regex.Replace(file, @",\s*", ",");
        if (llmNoCommaSpace == fileNoCommaSpace && llm != file)
            diffs.Add("the file has spaces after commas that you omitted — e.g. 'rgba(255,255,255)' should be 'rgba(255, 255, 255)'");


        var llmNoColonSpace = Regex.Replace(llm, @":\s*", ":");
        var fileNoColonSpace = Regex.Replace(file, @":\s*", ":");
        if (llmNoColonSpace == fileNoColonSpace && llmNoCommaSpace != fileNoCommaSpace)
            diffs.Add("the file has spaces after colons that you omitted — e.g. 'padding:16px' should be 'padding: 16px'");


        var llmNoEqSpace = Regex.Replace(llm, @"\s*=\s*", "=");
        var fileNoEqSpace = Regex.Replace(file, @"\s*=\s*", "=");
        if (llmNoEqSpace == fileNoEqSpace && llmNoCommaSpace != fileNoCommaSpace && llmNoColonSpace != fileNoColonSpace)
            diffs.Add("the file has spaces around '=' that you omitted — e.g. 'x=0' should be 'x = 0'");


        var llmNoParenSpace = Regex.Replace(llm, @"\(\s+", "(").Replace(")", " )").Replace(") )", "))");
        var fileNoParenSpace = Regex.Replace(file, @"\(\s+", "(").Replace(")", " )").Replace(") )", "))");
        if (llmNoParenSpace == fileNoParenSpace && llm != file
            && llmNoCommaSpace == fileNoCommaSpace && llmNoColonSpace == fileNoColonSpace)
            diffs.Add("the file has different whitespace inside parens");


        if (diffs.Count == 0)
        {
            var minLen = Math.Min(llm.Length, file.Length);
            var firstDiff = -1;
            for (var i = 0; i < minLen; i++)
            {
                if (llm[i] != file[i]) { firstDiff = i; break; }
            }
            if (firstDiff >= 0)
            {
                var ctx = Math.Max(0, firstDiff - 8);
                var llmCtx = llm.Substring(ctx, Math.Min(20, llm.Length - ctx));
                var fileCtx = file.Substring(ctx, Math.Min(20, file.Length - ctx));
                diffs.Add($"first difference at position {firstDiff}: you wrote '{llmCtx}' but file has '{fileCtx}'");
            }
            else
            {
                diffs.Add($"length differs: you wrote {llm.Length} chars, file has {file.Length} chars");
            }
        }

        return string.Join("; ", diffs);
    }

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


    public static string? DetectWrongSectionEdit(
        string oldStr, string fileContent, string stepChange, string relPath)
    {
        if (string.IsNullOrWhiteSpace(oldStr) || string.IsNullOrWhiteSpace(stepChange))
        { return null; }

        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        if (ext is not (".html" or ".htm" or ".cshtml" or ".razor" or ".vue" or ".svelte"))
        { return null; }

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


        if (sections.Count < 2) return null;

        var normFile = NormalizeLineEndings(fileContent);
        var normOld = NormalizeLineEndings(oldStr);
        var oldStrIdx = normFile.IndexOf(normOld, StringComparison.Ordinal);
        if (oldStrIdx < 0) return null;

        string? actualSection = null;
        foreach (var (name, divStart, divEnd) in sections)
        {
            if (oldStrIdx >= divStart && oldStrIdx <= divEnd)
            {
                actualSection = name;
                break;
            }
        }



        if (actualSection == null) { return null; }


        var stepLower = stepChange.ToLowerInvariant();
        string? targetSection = null;
        foreach (var (name, _, _) in sections)
        {


            if (Regex.IsMatch(stepLower, $@"\b{Regex.Escape(name.ToLowerInvariant())}\b"))
            {
                targetSection = name;
                break;
            }
        }


        if (targetSection == null) return null;

        if (string.Equals(actualSection, targetSection, StringComparison.OrdinalIgnoreCase))
        { return null; }

        var targetSectionEntry = sections.FirstOrDefault(s =>
            string.Equals(s.name, targetSection, StringComparison.OrdinalIgnoreCase));

        var error = new StringBuilder();
        error.AppendLine($"WRONG SECTION — the step description references the '{targetSection}' section, " +
                         $"but your oldString was found in the '{actualSection}' section.");
        error.AppendLine();
        error.AppendLine($"You MUST find the section marked with *ngIf=\"... === '{targetSection}'\" " +
                         $"and use lines from THAT section as your oldString.");
        error.AppendLine($"Do NOT edit the '{actualSection}' section.");


        if (targetSectionEntry.divEnd > targetSectionEntry.divStart)
        {
            var sectionContent = normFile.Substring(
                targetSectionEntry.divStart,
                Math.Min(targetSectionEntry.divEnd - targetSectionEntry.divStart + 6, 3000));


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

    private static int FindMatchingCloseDiv(string content, int openDivIdx)
    {
        if (openDivIdx < 0 || openDivIdx >= content.Length) return -1;

        var depth = 0;
        var pos = openDivIdx;

        while (pos < content.Length)
        {
            var nextOpen = content.IndexOf("<div", pos, StringComparison.OrdinalIgnoreCase);
            var nextClose = content.IndexOf("</div>", pos, StringComparison.OrdinalIgnoreCase);

            if (nextClose < 0) return -1;

            if (nextOpen >= 0 && nextOpen < nextClose)
            {

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
            var declPattern = $@"\b(class|record|struct)\s+{Regex.Escape(targetSymbol)}\b";
            var symPattern = $@"\b{Regex.Escape(targetSymbol)}\s*[\(<{{]";
            var foundDecl = false;
            for (var i = 0; i < lines.Length; i++)
            {
                if (Regex.IsMatch(lines[i], declPattern, RegexOptions.IgnoreCase))
                {
                    candidates.Add((i + 1, 99));
                    foundDecl = true;
                    break;
                }
            }
            if (!foundDecl)
            {
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!Regex.IsMatch(lines[i], symPattern, RegexOptions.IgnoreCase)) continue;
                    candidates.Add((i + 1, 92));
                    break;
                }
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

        if (changeDesc.TrimStart().StartsWith("<"))
        {
            var textFragments = Regex.Matches(changeDesc, @">([^<]{8,})<")
                .Select(m => m.Groups[1].Value.Trim())
                .Where(t => t.Length >= 8)
                .OrderByDescending(t => t.Length)
                .ToList();
            if (textFragments.Count > 0)
            {
                for (var i = 0; i < lines.Length; i++)
                {
                    if (textFragments.Any(t => lines[i].Contains(t, StringComparison.Ordinal)))
                        candidates.Add((i + 1, 90));
                }
            }
        }

        if (plannerLineNumber > 0 && plannerLineNumber <= lines.Length)
        {
            candidates.Add((plannerLineNumber, 1));
        }

        if (candidates.Count == 0)
            return 0;

        var best = candidates
            .GroupBy(c => c.line)
            .Select(g => (line: g.Key, score: g.Max(x => x.score)))
            .OrderByDescending(x => x.score)
            .ThenBy(x => plannerLineNumber > 0 ? Math.Abs(x.line - plannerLineNumber) : x.line)
            .First();

        return best.line;
    }

    public static string? FindTypeDefinitionInContext(string typeName, string context)
    {
        if (string.IsNullOrWhiteSpace(context) || string.IsNullOrWhiteSpace(typeName))
        { return null; }
        var declPattern = @"(class|record|struct)\s+" + Regex.Escape(typeName) + @"\b";
        var decl = Regex.Match(context, declPattern);
        if (!decl.Success) { return null; }

        var startIdx = decl.Index;
        var braceStart = context.IndexOf('{', startIdx);
        if (braceStart < 0) return null;

        var depth = 0;
        var endIdx = -1;
        for (var i = braceStart; i < context.Length; i++)
        {
            if (context[i] == '{') depth++;
            else if (context[i] == '}') { depth--; if (depth == 0) { endIdx = i; break; } }
        }
        if (endIdx < 0) return null;

        return context[startIdx..(endIdx + 1)].Trim();
    }

    public static string ExtractTypeNameForLog(string classDef)
    {
        var m = Regex.Match(classDef, @"\b(class|record|struct)\s+([A-Za-z_][A-Za-z0-9_]*)");
        return m.Success ? m.Groups[2].Value : "?";
    }

    public static int CountRoslynErrors(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return 0;
        try
        {
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(content);
            return tree.GetDiagnostics().Count(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        }
        catch
        {
            return 0;
        }
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
        var pattern = $@"{fieldName}:\s*(.*?)(?=\s*(?:FILE:|CHANGE:|DESCRIPTION:|<<<|$))";
        var m = Regex.Match(text, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    public static bool HasUnbalancedBraces(string content)
    {
        var depth = 0;
        var inSingle = false;
        var inDouble = false;
        var inTemplate = false;
        var inLineComment = false;
        var inBlockComment = false;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            var n = i + 1 < content.Length ? content[i + 1] : '\0';

            if (inLineComment && c == '\n') { inLineComment = false; continue; }
            if (inBlockComment && c == '*' && n == '/') { inBlockComment = false; i++; continue; }
            if (inBlockComment) continue;
            if (inLineComment) continue;

            if (!inSingle && !inDouble && !inTemplate)
            {
                if (c == '/' && n == '/') { inLineComment = true; i++; continue; }
                if (c == '/' && n == '*') { inBlockComment = true; i++; continue; }
            }

            if (c == '"' && !inSingle && !inTemplate) { inDouble = !inDouble; continue; }
            if (c == '\'' && !inDouble && !inTemplate) { inSingle = !inSingle; continue; }
            if (c == '`' && !inSingle && !inDouble) { inTemplate = !inTemplate; continue; }
            if (c == '\\' && (inSingle || inDouble || inTemplate)) { i++; continue; }

            if (!inSingle && !inDouble && !inTemplate)
            {
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth < 0) return true;
                }
            }
        }

        return depth != 0;
    }
}
