using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Weaver.Services;

/// <summary>
/// Planning policy extracted from the <c>AgentController.Planning</c> god partial:
/// the prompt-level task classifiers (web need, explicit web commands, script requests,
/// dump vs build task kind), the step-level predicates and steering feedback (web steps,
/// filesystem prep, scraper steering), the classifier-verdict and fetch-command feedback
/// builders, the <c>_command</c> write guards, the web-step re-run guard, and the
/// plan-validation battery (step parsing, creation signals, invented-directory and
/// command-violation gates, JS syntax checking). Pure static policy over the plan model —
/// no controller state; the only side effects are the documented disk/process probes
/// (FindClosestRealDirectory, FindExecutableOnPath, the node --check syntax gate, reading
/// a script's content to tell scrapers from legitimate runs).
/// </summary>
public static class AgentPlanningPolicy
{
    // ── Task-level classification ──────────────────────────────────────────────────────

    /// <summary>Feedback shown whenever a step is rejected because the task needs current external info.</summary>
    public const string WebNeedFeedback =
        "The task needs CURRENT EXTERNAL information (live data, web/API docs, latest versions) that cannot be found inside this repository. " +
        "Use a \"_web_search\" step (put the query in \"change\") or a \"_web_fetch\" step (put the URL in \"change\") — do NOT write code to fetch it.";

    /// <summary>Feedback shown when a _web_search/_web_fetch step is proposed for a task that does NOT need current external info.</summary>
    public const string WebNotNeededFeedback =
        "The task does NOT need CURRENT EXTERNAL information — this _web_search/_web_fetch step is unnecessary and is rejected. " +
        "Work from the DISCOVERY CONTEXT and the repo files already in context: propose a concrete repo edit, _create_file, or _command step instead. " +
        "Only use _web_search/_web_fetch when the task genuinely requires data that cannot come from the project.";

    /// <summary>
    /// True when the task hints at needing CURRENT EXTERNAL information. Delegates to the ONE
    /// web-need classifier (WebNeedClassifier) that both this gate and the news-digest routing
    /// read from — a single source of truth, so dump-task classification and RSS routing can
    /// never drift apart (a news-shaped prompt is inherently a web need).
    /// </summary>
    public static bool TaskHintsWebNeed(string? prompt) => WebNeedClassifier.IsWebNeed(prompt);

    /// <summary>
    /// Decisive detector for EXPLICIT web-search commands ("search the web for…",
    /// "look it up online", "fetch … from the internet"). Unlike TaskHintsWebNeed —
    /// which is deliberately BROAD and only opens the LLM classifier gate — a hit
    /// here IS the verdict: the user directly commanded a web search, so no
    /// classifier call may veto it (the classifier has been observed rejecting
    /// "Search the web for an interesting AI article…" as no-web, which sent the
    /// run down a hallucinated-command path). Repo-internal "search for"/"look up"
    /// phrasing never matches: it lacks the web/internet/online target.
    /// </summary>
    public static readonly Regex ExplicitWebCommandRegex = new Regex(
        @"\b(search|look up|find|fetch|browse|surf|google)\w*\b[^\r\n]{0,80}\b(web|internet|online|search engine)\b"
        + @"|\b(web search|search the web|search the internet|search online|browse the web|surf the web|google search|look up .{0,60} online|find .{0,60} online)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TaskExplicitlyCommandsWeb(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        return ExplicitWebCommandRegex.IsMatch(prompt.ToLowerInvariant());
    }

    /// <summary>
    /// True when the task EXPLICITLY asks for a script/program artifact ("write a python script
    /// that fetches …", "create fetch_poke_data.py …", "add a node script …"). This is the
    /// escape hatch the scraper guards honor: when the user demands a script, writing AND
    /// running it is the task, not the "wrote a Python app to do a fetch" drift — so the
    /// scraper-file guard, the run-a-scraper guard, and the gate's scraper steering all stand
    /// down (the generic missing-web-search gate still applies: research first, then script).
    /// The benchmark shape ("create a folder called X … create a file called data.csv … fetch
    /// the live data") never matches — no interpreter or script artifact is demanded.
    /// </summary>
    public static readonly Regex ExplicitScriptRequestRegex = new Regex(
        @"(?i)\b(write|create|build|implement|add|make|generate)\b[^\r\n]{0,80}\b(python|node|javascript|typescript|bash|powershell|script|program|\.py|\.js|\.ts|\.ps1|\.sh)\b"
        + @"|\b(python|node|javascript|typescript|bash|powershell)\b[^\r\n]{0,60}\b(script|program|file)\b"
        + @"|\b[\w./\\-]+\.(py|js|ts|ps1|sh|bash)\b[^\r\n]{0,80}\b(write|create|build|add|make|generate|run)\b",
        RegexOptions.Compiled);

    public static bool TaskExplicitlyRequestsScript(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        return ExplicitScriptRequestRegex.IsMatch(prompt);
    }

    /// <summary>
    /// Up-front "dump task" classification: the prompt wants fetched web data written into a
    /// demanded output FILE (not a script/function/artifact). A dump task is satisfied by the
    /// deterministic web-step auto-dump — once the demanded file is written with the fresh
    /// data, the completion assessment must NOT route that data back through the LLM (the real
    /// assessor concluded "needs a Python parser to turn the JSON into CSV rows" for a file
    /// that was already written, and forced the planner to re-emit the whole dataset inline).
    /// A script/program request ("write a python script that fetches…") is a BUILD task —
    /// those carry on with the normal planning/editing loop.
    /// </summary>
    /// <summary>
    /// A BUILD + VERIFY demand — the benchmark-23 shape: the prompt asks to BUILD a server /
    /// web app / game / canvas page AND verify it with the live browser test suite
    /// ("VISUALLY VERIFY the fix with the live browser test suite"), while a
    /// "write what you saw to …/report.txt" line is only a REPORTING artifact. A dump task is
    /// a pure fetch-web-data → write-file task; these build-first prompts must NOT be
    /// short-circuited by the dump path, or the build/verification steps (the 6-leg fix, the
    /// browser tests) would never run — the run would complete the instant the report file
    /// exists, exactly what happened to benchmark 23 (classified "dump" because of its
    /// "current leg count" web-hint and its legs_report.txt output target).
    /// </summary>
    public static readonly Regex[] BuildAndVerifyDemandRegexes =
    {
        new(@"\b(build|create|implement|make)\b[^.\n]{0,60}\b(web app|web application|website|game|canvas|server|web page|webpage|page)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(live browser test|browser test|live web test|test suite|visually verify|visual confirmation|visual bugs)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    };

    public static bool TaskDemandsBuildAndVerify(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        return BuildAndVerifyDemandRegexes.Any(r => r.IsMatch(prompt));
    }

    public static bool IsDumpTask(string? prompt, string projectRoot) =>
        TaskHintsWebNeed(prompt) &&
        prompt is { } nonNullPrompt &&
        AgentOsOutputVerifier.TryGetFileOutputTarget(nonNullPrompt, projectRoot, out _) &&
        !TaskExplicitlyRequestsScript(prompt) &&
        !TaskDemandsBuildAndVerify(prompt);

    /// <summary>
    /// True when the prompt demands post-fetch STRUCTURED edits to the demanded file
    /// (tabular columns/rows/values — the benchmark-21 shape: fetch, then add a column,
    /// add rows, mass-edit values). Such a task is a DUMP + EDIT hybrid: the fetch's
    /// eager dump writes the file, but the task is only satisfied after the deterministic
    /// tabular edits land. The dump-task short-circuit (which completes the run the moment
    /// the demanded file exists) must NOT fire for these — it would end the run right after
    /// the fetch and the edits would never execute.
    /// </summary>
    public static readonly Regex[] StructuredEditDemandRegexes =
    {
        new(@"\b(?:add|remove|delete|drop|rename)\b[^.\n]{0,80}\bcolumn\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(?:add|append|insert|delete|remove)\b[^.\n]{0,80}\brows?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(?:mass\s*edit|set\s+.+\s+to\s+.+\s+where|replace\s+.+\s+(?:in|within)\s+(?:the\s+)?column|fill\s+(?:the\s+)?[A-Za-z0-9_]+\s+column)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    };

    public static bool TaskDemandsStructuredEdits(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        return StructuredEditDemandRegexes.Any(r => r.IsMatch(prompt));
    }

    /// <summary>A dump task with NO post-fetch structured edits demanded — the only shape the
    /// deterministic dump short-circuit may complete (the demanded file alone satisfies it).</summary>
    public static bool IsPureDumpTask(string? prompt, string projectRoot) =>
        IsDumpTask(prompt, projectRoot) && !TaskDemandsStructuredEdits(prompt);

    /// <summary>
    /// The up-front dump-vs-build classification surfaced on the kanban card as a badge:
    /// "dump" when the task wants fetched web data written straight into a demanded file
    /// (the deterministic short-circuit applies), "build" when it explicitly asks for a
    /// script/program (normal planning/editing continues). Null for ordinary edit tasks
    /// (no badge).
    /// </summary>
    public static string? ClassifyTaskKind(string? prompt, string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return null;
        if (TestIntentClassifier.Classify(prompt).Intent != TestIntentClassifier.Kind.None) return "test";
        if (IsDumpTask(prompt, projectRoot)) return "dump";
        if (TaskExplicitlyRequestsScript(prompt)) return "build";
        return null;
    }

    /// <summary>
    /// The fetch-in-command guard is deliberately scoped: it rejects a URL-fetching
    /// _command step ONLY when the task hints at needing web data (the
    /// "api.current.ai" hallucination happens on web tasks). On a non-web task a
    /// bare legit `curl https://…/health` or similar stays allowed as a real
    /// terminal command.
    /// </summary>
    public static bool ShouldRejectFetchCommand(string? prompt, string? command) =>
        TaskHintsWebNeed(prompt) && AgentProjectUtilities.LooksLikeContentFetchCommand(command ?? "");

    // ── Step-level policy & steering feedback ──────────────────────────────────────────

    public static bool IsWebStep(string? file)
    {
        return file != null &&
            (file.Equals("_web_search", StringComparison.OrdinalIgnoreCase) ||
             file.Equals("_web_fetch", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True for steps that are filesystem PREP rather than a refusal to use the web tools:
    /// a _create_directory step, or a _command whose leading verb creates a directory
    /// (mkdir/md, New-Item -ItemType Directory). A web-needing task that demands a folder
    /// ("create a folder called X …") must be able to create it up front — bouncing these
    /// for "no web step yet" burned up to 3 planning rounds before the search was
    /// auto-injected (and the _command variant is additionally converted to _create_directory
    /// by ValidatePlanAsync on the batch path).
    /// </summary>
    public static bool IsFilesystemPrepStep(PlanStep step)
    {
        if (step.File == null) return false;
        if (step.File.Equals("_create_directory", StringComparison.OrdinalIgnoreCase)) return true;
        if (!step.File.Equals("_command", StringComparison.OrdinalIgnoreCase)) return false;
        var cmd = step.Change?.TrimStart() ?? "";
        return Regex.IsMatch(cmd, @"^(mkdir|md)\b", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(cmd, @"^new-item\b[^\r\n]*(directory|-itemtype)", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// When the missing-web-search gate is about to reject a _create_file step whose change
    /// clearly asks for a FOLDER (the planner reached for _create_file to make a directory),
    /// return targeted steering toward _create_directory instead of the generic web feedback —
    /// otherwise the next proposal is another mislabeled attempt instead of the fix.
    /// </summary>
    public static string? DirectoryIntentFeedback(PlanStep step)
    {
        if (step.File == null ||
            !step.File.Equals("_create_file", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(step.Change))
            return null;
        var change = step.Change.ToLowerInvariant();
        if (!Regex.IsMatch(change, @"\b(create|make|add|new)\b[^\r\n]{0,40}\b(folder|directory|dir)\b"))
            return null;
        return "This step creates a FOLDER/DIRECTORY — use a \"_create_directory\" step with the relative folder path in \"change\" (no newString). " +
               "\"_create_file\" is for FILES with real content; folder-only creation belongs to _create_directory and needs no web step first.";
    }

    /// <summary>
    /// When the missing-web-search gate is about to reject a _create_file that is really a
    /// SCRAPER/FETCH script — application code that programs an HTTP fetch (requests.get /
    /// urllib / fetch() / Invoke-RestMethod / HttpClient …) against a URL and writes the
    /// result to a file — return targeted steering toward _web_fetch instead of the generic
    /// web feedback. The "wrote a Python app to do a fetch" drift: the planner forgets the
    /// _web_fetch step tool exists and plans a scraper program instead (the system fetches
    /// the URL itself and auto-writes the harvested data to the demanded file).
    /// </summary>
    public static string? ScraperScriptFeedback(PlanStep step)
    {
        if (step.File == null ||
            !step.File.Equals("_create_file", StringComparison.OrdinalIgnoreCase) ||
            !AgentProjectUtilities.LooksLikeScraperScriptContent(step.NewString))
            return null;
        return "This _create_file is a SCRAPER/FETCH script — application code that programs an HTTP request " +
               "(requests.get / urllib / fetch() / Invoke-RestMethod / HttpClient …) to pull live data. On a task " +
               "that needs current/live data, the fetch is a STEP TOOL, not a script: use a \"_web_fetch\" step with " +
               "the real URL in \"change\" (or \"_web_search\" first if you don't have the URL yet). The system fetches " +
               "the URL itself and auto-writes the harvested data to the demanded file — do NOT write code to do the fetching. " +
               "If _web_fetch keeps failing, plan a \"_scraper\" step with the URL instead — the system builds and runs " +
               "a working scraper for your OS and installed packages.";
    }

    /// <summary>
    /// A _command that RUNS a script via an interpreter: <c>python fetch_poke_data.py</c>,
    /// <c>python .\fetch_poke_data.py</c>, <c>cd X && node scripts/build.js</c>, or a
    /// download-and-pipe into an interpreter (<c>curl &lt;url&gt; | python</c>). On a web-needing
    /// task the RUN is the same "wrote a Python app to do a fetch" drift as the scraper-file
    /// guard — the script either IS a scraper (read from disk) or does not exist (the scraper
    /// was rejected, so running it is running nothing). Returns steering feedback, or null when
    /// the command is legitimate (an existing non-scraper script, or no script run at all).
    /// </summary>
    public static readonly Regex RunScriptCommandRegex = new Regex(
        @"(?i)\b(python3?|py|node|deno|bun|ruby|perl|php|bash|sh|pwsh|powershell)\b(?:\s+-[A-Za-z][A-Za-z0-9]*\s*)*\s+([\w.\\/:-]+\.(?:py|pyw|js|mjs|cjs|ts|rb|pl|php|sh|bash|ps1))\b",
        RegexOptions.Compiled);

    public static readonly Regex FetchPipeIntoInterpreterRegex = new Regex(
        @"(?i)\b(curl|wget|curl\.exe|irm|iwr|invoke-restmethod|invoke-webrequest)\b[^\r\n]{0,150}\|\s*(?:&\s*)?\b(python3?|py|node|bash|sh|pwsh|powershell)\b",
        RegexOptions.Compiled);

    public static string? RunScraperScriptReason(PlanStep step, string projectRoot)
    {
        if (step.File == null || !step.File.Equals("_command", StringComparison.OrdinalIgnoreCase))
            return null;
        var cmd = step.Change?.Trim() ?? "";
        var pipe = FetchPipeIntoInterpreterRegex.Match(cmd);
        if (pipe.Success)
            return ScraperRunFeedback($"a fetched HTTP stream piped into '{pipe.Groups[2].Value}'", pipe.Groups[2].Value);
        var m = RunScriptCommandRegex.Match(cmd);
        if (!m.Success) return null;
        var interp = m.Groups[1].Value;
        var script = m.Groups[2].Value.Trim().Trim('"', '\'', '`');
        script = Regex.Replace(script, @"^[.]\\|^[.]/", "");
        var scriptRel = script.Replace('\\', '/');
        string? content = null;
        try
        {
            var full = Path.GetFullPath(Path.Combine(projectRoot, scriptRel.Replace('/', Path.DirectorySeparatorChar)));
            if (System.IO.File.Exists(full)) content = System.IO.File.ReadAllText(full);
        }
        catch { /* unreadable path — treat as nonexistent */ }
        if (content != null)
        {
            if (!AgentProjectUtilities.LooksLikeScraperScriptContent(content))
                return null; // an EXISTING, non-scraper script — a legitimate run
            return ScraperRunFeedback($"'{scriptRel}' (it is a scraper/fetch script)", interp);
        }
        return ScraperRunFeedback(
            $"'{scriptRel}' (the file does not exist — running a script that was never created cannot produce the data)", interp);
    }

    public static string ScraperRunFeedback(string what, string interpreter)
    {
        var envNote = ScraperEnvironmentService.StaticInterpreterAvailable(interpreter)
            ? $"Your machine HAS \"{interpreter}\" installed, but the fetch still belongs to the step tool."
            : $"Your machine does NOT have \"{interpreter}\" installed — the script could not run anyway.";
        return $"This _command RUNS a scraper/fetch script ({what}). {envNote} " +
               "On a task that needs current/live data, the fetch is a STEP TOOL: use a \"_web_fetch\" step with the real URL " +
               "(or \"_web_search\" first if you don't have the URL yet). The system fetches the URL itself and auto-writes the " +
               "demanded file. If _web_fetch keeps failing, plan a \"_scraper\" step with the URL instead — the system builds " +
               "and runs a working scraper for your OS and installed packages. Do NOT write or run scraper code yourself.";
    }

    // ── Web-need verdict & fetch-command feedback ──────────────────────────────────────

    /// <summary>
    /// Parses the classifier's JSON verdict ({"needsWeb", "reason", "query"}) into a
    /// (needsWeb, query, reason) tuple. Returns false when the text isn't a valid
    /// JSON object — the caller treats that as a failed verification (the web-step
    /// gate fails OPEN, the missing-web-search guard fails CLOSED). The reason is
    /// captured so a false no-web veto can be surfaced to the agent panel.
    /// </summary>
    public static bool TryParseWebNeedVerdict(string json, out bool needsWeb, out string? query, out string reason)
    {
        needsWeb = false; query = null; reason = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            needsWeb = root.TryGetProperty("needsWeb", out var nw) && nw.ValueKind == JsonValueKind.True;
            query = needsWeb && root.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String
                ? q.GetString()
                : null;
            reason = root.TryGetProperty("reason", out var rj) && rj.ValueKind == JsonValueKind.String
                ? rj.GetString()?.Trim() ?? ""
                : "";
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Stable prefix identifying a fetch-in-command rejection so the
    /// auto-inject counters can recognize it regardless of the dynamic detail
    /// (the URL, whether a search already ran, whether the URL matches the
    /// harvested results).</summary>
    public const string FetchCommandFeedbackMarker =
        "This _command step fetches content from a URL with a download tool";

    /// <summary>
    /// Feedback shown when a _command step tries to fetch web content instead of
    /// using the web tools (the "Invoke-RestMethod https://api.current.ai/..."
    /// failure mode). The fetched results must land in context via _web_search/
    /// _web_fetch, not in a command's stdout/file — and the URL a _web_fetch
    /// eventually uses must be copied VERBATIM from the harvested search results
    /// (the "wired ...haha-im-in-danger/" hallucinated-URL run fetched garbage and
    /// PowerShell saved an empty object). The builder quotes the attempted URL and
    /// tells the planner whether it matches the results, what a search-first path
    /// looks like, and that inventing URLs is never allowed.
    /// </summary>
    public static string FetchCommandFeedbackFor(
        string originalPrompt, PlanStep step, List<PlanStep> planSoFar, string discoveryContext)
    {
        var url = ExtractCommandUrl(step.Change ?? "");
        var resultsInContext = discoveryContext.Contains("### WEB RESULTS", StringComparison.Ordinal) ||
                               planSoFar.Any(s => IsWebStep(s.File));
        var urlMatchesResults = !string.IsNullOrWhiteSpace(url) &&
                                discoveryContext.Contains(url, StringComparison.Ordinal);
        var sb = new StringBuilder(FetchCommandFeedbackMarker);
        sb.Append(" (curl/wget/Invoke-RestMethod/Invoke-WebRequest/urllib/...). ")
          .Append("Fetching web content is NOT a shell command — the fetched results must land in your CONTEXT, ")
          .Append("not in a command's stdout or in a file on disk.");
        if (resultsInContext)
        {
            if (urlMatchesResults)
            {
                sb.Append(" The URL \"").Append(url).Append("\" you put in this command IS in the harvested search results — ")
                  .Append("use a \"_web_fetch\" step instead and put THAT exact URL verbatim in its \"change\" field; ")
                  .Append("the fetch output is injected into your context.");
            }
            else
            {
                sb.Append(" The URL \"").Append(url).Append("\" is NOT among the harvested search results in your DISCOVERY CONTEXT ")
                  .Append("— it looks invented (a hallucinated URL fetches garbage and the run saves an empty object). ")
                  .Append("Pick a REAL URL verbatim from the '### WEB RESULTS [...] ###' sections and use a \"_web_fetch\" step with that exact URL. ")
                  .Append("NEVER invent, guess, or reconstruct a URL — copy it exactly from the results.");
            }
        }
        else
        {
            sb.Append(" Run a \"_web_search\" step first to obtain REAL URLs, then use a \"_web_fetch\" step with one of those URLs ")
              .Append("verbatim (copy it exactly — never invent, guess, or reconstruct a URL).");
        }
        sb.Append(" Reserve _command for real terminal commands (builds, tests, git, package installs).");
        return sb.ToString();
    }

    /// <summary>Extracts the first http(s) URL from a command string, trimmed of
    /// trailing punctuation, for quoting back in rejection feedback.</summary>
    public static string? ExtractCommandUrl(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var m = Regex.Match(command, @"https?://[^\s""'`]+");
        if (!m.Success) return null;
        return m.Value.TrimEnd(')', ']', '}', ';', ',', '\"');
    }

    // ── Command write guards ───────────────────────────────────────────────────────────

    /// <summary>
    /// Windows PowerShell 5.1 (powershell.exe) rejects `&&` as a statement separator
    /// ("The token '&&' is not a valid statement separator in this version"). The planner
    /// (trained mostly on bash/PS7) chains commands with && and the whole write fails — the
    /// "echo … > file && echo … >> file" run. OS-independent so it can be unit-tested on any
    /// host; the CALLER gates on OperatingSystem.IsWindows() (Linux bash and PS7 allow &&).
    /// Quoted strings are ignored: a literal "a && b" inside quotes is valid PS 5.1.
    /// </summary>
    public static string? WindowsPowerShellAndChainReason(string? change)
    {
        if (string.IsNullOrWhiteSpace(change)) return null;
        var unquoted = Regex.Replace(change, "\"([^\"]*)\"|'([^']*)'", "");
        if (!unquoted.Contains("&&")) return null;
        return "_command chains commands with '&&' — the shell on this machine is Windows PowerShell 5.1, which does NOT support '&&' as a statement separator " +
               "(\"The token '&&' is not a valid statement separator in this version\"). Run ONE command per _command step, separate commands with ';', " +
               "or write the file in a single Set-Content/Out-File/Add-Content step with an absolute path.";
    }

    /// <summary>
    /// Redundant OS-output write guard: once a committed _command step ALREADY satisfies the
    /// task's demanded OS file output (its command references the demanded directory/file),
    /// any NEW _command step writing to that same demanded location is stacked re-work — the
    /// "Search the web … write the data into a text file on my desktop" run proposed
    /// Set-Content, then an echo && chain, then an Invoke-RestMethod redirect, ALL to the
    /// same file. Only fires when the demand is ALREADY satisfied, so a genuine repair (the
    /// verifier says the file is missing or its content is wrong) can still overwrite.
    /// </summary>
    public static string? RedundantOsWriteReason(string? prompt, string? newCommand, IEnumerable<PlanStep> committed)
    {
        if (!AgentOsOutputVerifier.TryGetOsFileOutputDemand(prompt, out var demand)) return null;
        var newCmd = (newCommand ?? "").Replace('\\', '/');
        var demandDir = (demand.DirectoryPath ?? "").Replace('\\', '/');
        // A prompt that NAMES the file pins the exact target — only writes to THAT file are
        // redundant. A generic demand ("a text file on my desktop") falls back to the default
        // name for IsOsOutputWritten, but the planner may legitimately pick any name, so the
        // directory match is what catches stacked writes there.
        var hasRealHint = !string.IsNullOrWhiteSpace(demand.FileNameHint);
        var demandFile = hasRealHint
            ? demand.FileNameHint!
            : AgentOsOutputVerifier.DefaultDumpFileName;
        var writesToDemand = hasRealHint
            ? newCmd.Contains(demandFile, StringComparison.OrdinalIgnoreCase)
            : (demandDir.Length > 0 && newCmd.Contains(demandDir, StringComparison.OrdinalIgnoreCase)) ||
              (demandFile.Length > 0 && newCmd.Contains(demandFile, StringComparison.OrdinalIgnoreCase));
        if (!writesToDemand) return null;
        // Prompt-level "already targeted" check over the committed steps' command text (the
        // same directory/file match used above). This deliberately does NOT consult the disk
        // or AgentOsOutputVerifier.IsOsOutputWritten: the guard runs at planning time before
        // anything executes, on machines where the demanded path may not exist at all.
        var alreadyTargeted = committed.Any(s =>
        {
            var c = (s.Change ?? "").Replace('\\', '/');
            return (demandDir.Length > 0 && c.Contains(demandDir, StringComparison.OrdinalIgnoreCase)) ||
                   (demandFile.Length > 0 && c.Contains(demandFile, StringComparison.OrdinalIgnoreCase));
        });
        if (!alreadyTargeted) return null;
        var where = string.IsNullOrWhiteSpace(demand.FileNameHint)
            ? (demandDir.Length > 0 ? demandDir : "the demanded location")
            : Path.Combine(demand.DirectoryPath ?? "", demand.FileNameHint!);
        return $"The demanded OS output file ({where}) was ALREADY written by a completed _command step — the task's file requirement is satisfied. " +
               "Do NOT add another Set-Content/Out-File/redirect write to the same location. If the run is complete, return planComplete=true; " +
               "only write again if a verifier issue explicitly says the existing file content is wrong.";
    }

    // ── Web-step re-run guard ──────────────────────────────────────────────────────────

    /// <summary>
    /// Web-step re-run guard: once a _web_search/_web_fetch step has been committed AND its
    /// output was harvested into the discovery context ('### WEB RESULTS [...]' present), a
    /// NEW web step targeting the SAME query/URL — or a near-reworded variant — is the
    /// planner re-researching instead of using what the search returned. This is the
    /// "loop of searching": each step's thinking re-derives the research need from the task
    /// text and re-proposes the search. Reworded queries ("AI news article sources" vs
    /// "AI news articles RSS feed or public API") score ~0.2-0.4 Jaccard — far below the
    /// 0.82 edit-step dedup threshold — so the identical-query guard alone lets the loop run.
    /// Web queries are far less diverse than edit anchors, so the guard uses the same 0.35
    /// overlap threshold the additional-step queue uses. Gated on the results being IN
    /// context: a failed/empty search harvests nothing, so retrying it stays allowed.
    /// </summary>
    public static string? RepeatedWebStepReason(PlanStep step, IEnumerable<PlanStep> committed, string discoveryContext)
    {
        if (!IsWebStep(step.File)) return null;
        if (!discoveryContext.Contains("### WEB RESULTS", StringComparison.Ordinal)) return null;
        var normNew = NormalizeChangeForDedup(step.Change);
        if (normNew.Length == 0) return null;
        var newWords = WebQueryWords(normNew);
        if (newWords.Count == 0) return null;
        foreach (var (existing, idx) in committed.Select((s, i) => (s, i)))
        {
            if (!IsWebStep(existing.File)) continue;
            var normExisting = NormalizeChangeForDedup(existing.Change);
            if (normExisting.Length == 0) continue;
            var existingWords = WebQueryWords(normExisting);
            if (existingWords.Count == 0) continue;
            // Jaccard (same as the 0.35 additional-step threshold) PLUS a containment check:
            // unequal-length queries ("AI news article sources" vs "AI news articles RSS feed
            // or public API") score only ~0.2 Jaccard despite being the SAME research intent.
            // Containment = overlap / smaller query — reworded variants keep ~75% of their
            // words in the original, while a genuinely different topic shares almost none.
            var inter = newWords.Intersect(existingWords).Count();
            var jaccard = (double)inter / newWords.Union(existingWords).Count();
            var containment = (double)inter / Math.Min(newWords.Count, existingWords.Count);
            if (jaccard < 0.35 && containment < 0.6) continue;
            if (existing.File.Equals("_web_fetch", StringComparison.OrdinalIgnoreCase))
                return $"An earlier _web_fetch step already fetched \"{existing.Change}\" and its results are in the DISCOVERY CONTEXT under '### WEB RESULTS [...] ###'. Do NOT fetch it again — use the content already returned. If you need a DIFFERENT article, _web_fetch a concrete URL from those results that has not been fetched yet.";
            return $"Step {idx + 1} already ran _web_search \"{existing.Change}\" and its results are in the DISCOVERY CONTEXT under '### WEB RESULTS [...] ###'. Do NOT search the web again — those results are authoritative facts to USE, not to re-derive. If the task still needs an article's full content, _web_fetch a CONCRETE URL listed in the results (never an invented one); if it needs a file written, draw its content from the titles/URLs/summaries already returned.";
        }
        return null;
    }

    /// <summary>
    /// Word set for the web-step re-run comparison, plural-tolerant ("articles" ≡ "article"):
    /// web queries reword plurals far more often than edit anchors do, and token-exact sets
    /// would let "AI news article" dodge "AI news articles". Singularize each word so the
    /// intent comparison is stable across rewording.
    /// </summary>
    private static HashSet<string> WebQueryWords(string normalized)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            words.Add(w.Length > 3 && w.EndsWith("s") && !w.EndsWith("ss") ? w[..^1] : w);
        return words;
    }

    // ── Plan validation ────────────────────────────────────────────────────────────────

    public static PlanStep ParseStepFromJson(string file, string change, string? targetSymbol, int line, string? oldString, string? newString, List<string> refFiles, List<EditPair> edits,
        string? targetType = null, string? targetName = null, bool? insertAfter = null, List<string>? newCode = null, string? fullFile = null)
    {
        // Trim leading/trailing whitespace so LLM-emitted file names like " _web_search \n" still
        // match IsWebStep/IsSpecialMarker and the web-step exclusion (a stray trailing newline
        // used to dodge both and get bounced by the research-verb guard as "search is not an
        // actionable edit" — the exact interleaved-loop deadlock seen in web-needing runs). Only
        // Trim, never collapse internal whitespace: a real path like "src/My  Folder/file.cs"
        // must stay byte-identical or edits silently land on the wrong file.
        var normFile = file.Trim().Replace('\\', '/');
        if (normFile.StartsWith("_edit/", StringComparison.OrdinalIgnoreCase))
            normFile = normFile["_edit/".Length..];
        // FORMAT C/D steps carry targetName+newCode (no oldString): map targetName onto
        // TargetSymbol so AST resolution extracts the exact oldString from the real file.
        var effectiveSymbol = string.IsNullOrWhiteSpace(targetSymbol) ? targetName : targetSymbol;
        // fullFile steps carry the complete file content: surface it as NewString so the
        // existing _create_file path (file missing + NewString set + OldString empty) applies it.
        var effectiveNew = newString;
        if (string.IsNullOrWhiteSpace(effectiveNew) && !string.IsNullOrWhiteSpace(fullFile))
            effectiveNew = fullFile;
        return new PlanStep
        {
            File = normFile,
            Change = change,
            TargetSymbol = effectiveSymbol,
            TargetType = targetType,
            TargetName = targetName,
            InsertAfter = insertAfter,
            NewCode = newCode != null && newCode.Count > 0 ? newCode : null,
            FullFile = fullFile,
            Priority = 1,
            LineNumber = line,
            OldString = oldString,
            NewString = effectiveNew,
            ReferenceFiles = refFiles,
            Edits = edits.Count > 0 ? edits : null
        };
    }

    /// <summary>
    /// True when the task prompt explicitly asks to create a new file/folder/artifact, in
    /// which case a _create_file/_create_directory step is legitimate even when files are
    /// attached. Two narrow patterns, both chosen so edit phrasing never matches:
    ///  1. A literal "new &lt;artifact&gt;" — "create a new file", "needs a new service",
    ///     "add a new test" — where artifact excludes pure edit targets like "method".
    ///  2. An UNambiguous creation verb (create/make/generate/write/scaffold/init) directly
    ///     followed by an article and an artifact noun — "create a helper file", "make a
    ///     folder", "write a test". Verbs like "add"/"needs" are deliberately absent:
    ///     "add a method to the component" is an edit, not a creation.
    /// </summary>
    public static bool PromptSignalsFileCreation(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        var lower = prompt.ToLowerInvariant();
        const string artifact =
            @"(?:file|script|helper|module|service|component|folder|directory|class|interface|stylesheet|config|json|readme|doc|test|spec|tests|page|route|endpoint)";
        if (Regex.IsMatch(lower, @"\bnew\s+" + artifact + @"\b")) return true;
        return Regex.IsMatch(lower,
            @"\b(create|make|generate|write|scaffold|init)\s+(?:a|an|the|another)\s+" + artifact + @"\b");
    }

    /// <summary>
    /// The invented-directory check shared by the incremental per-step guard and the classic
    /// whole-plan pre-execution gate: returns the rejection reason for a _create_file path
    /// whose directory prefix does not exist anywhere in the project, steering to the closest
    /// real directory, or null when the directory is legitimate. Exemptions (the guard only
    /// fires on PLANNER-INVENTED directories): an earlier _create_directory step in the plan
    /// makes the directory real, the USER's task explicitly names the path (legit "write
    /// docs/README.md" into a brand-new folder), and OS paths (rejected by the OS-task veto,
    /// never here). Path.GetDirectoryName returns OS separators (backslashes on Windows) —
    /// normalized so the comparison against FindClosestRealDirectory's '/' paths works.
    /// </summary>
    public static string? InventedCreateDirectoryReason(
        string createCandidate, string originalPrompt, IEnumerable<PlanStep> priorSteps, string projectRoot)
    {
        var createDir = (Path.GetDirectoryName(createCandidate) ?? "").Replace('\\', '/');
        var dirMadeByPriorStep = priorSteps.Any(p =>
            string.Equals(p.File, "_create_directory", StringComparison.OrdinalIgnoreCase) &&
            AgentDiscovery.IsPathAncestorOrEqual(p.Change ?? "", createDir));
        var pathNamedByPrompt = originalPrompt.IndexOf(createCandidate, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                originalPrompt.IndexOf(createDir, StringComparison.OrdinalIgnoreCase) >= 0;
        var createCandidateIsOsPath = createCandidate.StartsWith("/")
            || createCandidate.StartsWith("~")
            || createCandidate.StartsWith("\\\\")
            || Regex.IsMatch(createCandidate, @"^[A-Za-z]:[\\/]");
        if (string.IsNullOrWhiteSpace(createDir) || dirMadeByPriorStep || pathNamedByPrompt || createCandidateIsOsPath)
            return null;
        var closest = AgentDiscovery.FindClosestRealDirectory(createCandidate, projectRoot);
        if (closest == null)
            return $"_create_file directory '{createDir}' does not exist in the project — the file would land at the project root. " +
                   "Put the file under an existing directory instead of inventing a new path.";
        if (!string.Equals(closest, createDir, StringComparison.OrdinalIgnoreCase))
            return $"_create_file directory '{createDir}' does not exist anywhere in the project. " +
                   $"Retarget the file under the closest real directory '{closest}' (or any existing directory) — NEVER invent directory paths.";
        return null;
    }

    /// <summary>The candidate path of a step that CREATES a file: the path in the Change of a
    /// _create_file marker step (dotted path extracted whole so maxhanna.client/src/… prefixes
    /// are captured), or the plain relative File of the create-eligible shape (newString content,
    /// no oldString anchor). Returns null for non-create steps / unparseable paths.</summary>
    public static string? CreateCandidatePath(PlanStep step)
    {
        if (step.File.Equals("_create_file", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(step.Change ?? "", @"([\w.\-/\\]+\.\w{1,10})");
            return m.Success ? m.Groups[1].Value : null;
        }
        if (!AgentProjectUtilities.IsSpecialMarker(step.File) &&
            AgentProjectUtilities.IsRelativePath(step.File) &&
            !string.IsNullOrWhiteSpace(step.NewString) &&
            string.IsNullOrWhiteSpace(step.OldString))
        {
            return step.File.Replace('\\', '/');
        }
        return null;
    }

    /// <summary>
    /// Classic-route mirror of the incremental invented-directory guard: scans a WHOLE plan
    /// (the classic route has no per-step validation) for _create_file steps whose directory
    /// prefix is nonsense, evaluated against the steps BEFORE each create (so an earlier
    /// _create_directory step in the same plan legitimately makes the directory real).
    /// </summary>
    public static List<(PlanStep Step, string Reason)> FindInventedCreateDirectories(
        AgentPlan plan, string originalPrompt, string projectRoot)
    {
        var violations = new List<(PlanStep, string)>();
        if (plan?.Plan == null) return violations;
        var prior = new List<PlanStep>();
        foreach (var step in plan.Plan)
        {
            var candidate = CreateCandidatePath(step);
            if (candidate != null &&
                InventedCreateDirectoryReason(candidate, originalPrompt, prior, projectRoot) is string reason)
            {
                violations.Add((step, reason));
            }
            prior.Add(step);
        }
        return violations;
    }

    /// <summary>
    /// Classic-route mirror of the incremental && / redundant-OS-write guards: the classic
    /// full-plan route has no per-step validation (ValidateIncrementalStepAsync only runs in
    /// the interleaved route), so scan the WHOLE plan before executing any step and reject
    /// Windows PowerShell `&&` chains and _command steps that re-write a demanded OS output
    /// already covered by an earlier plan item. Evaluated against the steps BEFORE each
    /// command so a single legitimate write in the plan is never flagged.
    /// </summary>
    public static List<(PlanStep Step, string Reason)> FindPlanCommandViolations(
        AgentPlan plan, string prompt)
    {
        var violations = new List<(PlanStep, string)>();
        if (plan?.Plan == null) return violations;
        var prior = new List<PlanStep>();
        foreach (var step in plan.Plan)
        {
            if (string.Equals(step.File, "_command", StringComparison.OrdinalIgnoreCase))
            {
                if (OperatingSystem.IsWindows() && WindowsPowerShellAndChainReason(step.Change) is string andReason)
                    violations.Add((step, andReason));
                else if (RedundantOsWriteReason(prompt, step.Change, prior) is string dupReason)
                    violations.Add((step, dupReason));
            }
            prior.Add(step);
        }
        return violations;
    }

    public static string? ValidateJavaScriptLikeCreateFile(string? filePath, string? content)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(content)) return null;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is not ".js" and not ".jsx" and not ".mjs" and not ".cjs" and not ".ts" and not ".tsx")
            return null;

        var node = FindExecutableOnPath("node");
        if (node == null)
            return null;

        var tempPath = Path.Combine(Path.GetTempPath(), $"weaver-syntax-{Guid.NewGuid():N}{ext}");
        try
        {
            System.IO.File.WriteAllText(tempPath, content);
            var psi = new ProcessStartInfo
            {
                FileName = node,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (ext is ".ts" or ".tsx" or ".mts" or ".cts")
            {
                psi.ArgumentList.Add("--experimental-strip-types");
            }
            psi.ArgumentList.Add("--check");
            psi.ArgumentList.Add(tempPath);
            using var process = Process.Start(psi);
            if (process == null) return null;
            var stderr = process.StandardError.ReadToEnd();
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15000);
            if (process.ExitCode != 0)
            {
                var tail = (stderr + "\n" + stdout).Trim();
                return $"JavaScript/TypeScript syntax check failed for '{filePath}' — reject this _create_file until the script parses cleanly. {tail}";
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath); } catch { }
        }
    }

    public static string? FindExecutableOnPath(string name)
    {
        var candidates = new[]
        {
            name,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), name)
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && System.IO.File.Exists(candidate)) return candidate;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir, name + (OperatingSystem.IsWindows() ? ".exe" : ""));
            if (System.IO.File.Exists(full)) return full;
        }
        return null;
    }

    // ── Change dedup helpers (shared by the planning loops' duplicate guards) ──────────

    public static string NormalizeChangeForDedup(string? change)
    {
        if (string.IsNullOrWhiteSpace(change)) return "";
        var norm = change.Trim().ToLowerInvariant();
        norm = Regex.Replace(norm, @"[^\w\s]", "");
        norm = string.Join(" ", norm.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
        return norm;
    }

    public static double CalculateChangeSimilarity(string s1, string s2)
    {
        if (string.IsNullOrWhiteSpace(s1) || string.IsNullOrWhiteSpace(s2)) return 0.0;
        var words1 = new HashSet<string>(s1.Split(' '));
        var words2 = new HashSet<string>(s2.Split(' '));
        var intersection = words1.Intersect(words2).Count();
        var union = words1.Union(words2).Count();
        return union == 0 ? 0.0 : (double)intersection / union;
    }
}
