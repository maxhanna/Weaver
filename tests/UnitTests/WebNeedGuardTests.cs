using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Weaver;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the missing-web-search gate helpers (Services/AgentPlanningPolicy.cs). TaskHintsWebNeed is a deliberately
/// BROAD regex gate — it only opens the LLM verification, never rejects on its
/// own — so it must catch web-flavored tasks (including noisy "search for" /
/// "look up" phrasing that may be repo-internal) without snagging plain coding
/// tasks. IsWebStep recognizes the _web_search/_web_fetch markers so the guard
/// stands down once a web step is planned.
/// </summary>
public class WebNeedGuardTests
{
    private static bool Hints(string? p) => AgentPlanningPolicy.TaskHintsWebNeed(p);
    private static bool IsWeb(string? f) => AgentPlanningPolicy.IsWebStep(f);

    [Theory]
    [InlineData("Perform 3 independent web searches and write the results to a file")]
    [InlineData("Fetch the current Bitcoin halving date from the internet")]
    [InlineData("look up the latest API docs for the Stripe library")]
    [InlineData("search for the up to date version numbers online")]
    [InlineData("Find out what today's exchange rates are")]
    [InlineData("Search the web for three facts and save them to internet_facts.txt")]
    public void TaskHintsWebNeed_DetectsWebFlavoredTasks(string prompt)
    {
        Assert.True(Hints(prompt));
    }

    [Theory]
    [InlineData("Refactor the login component and add tests")]
    [InlineData("Fix the bug in the file picker")]
    [InlineData("Add a property to the DTO")]
    [InlineData("Search the repo for the add function and explain it")]
    [InlineData("Look at the existing tests and make them pass")]
    public void TaskHintsWebNeed_IgnoresPlainCodingTasks(string prompt)
    {
        Assert.False(Hints(prompt));
    }

    // The whole point of the LLM verification: noisy phrasing like "search for"
    // (which often means searching the REPO) must still OPEN the gate — the regex
    // hints broad, the LLM classifier is the real decider. These lock that contract.
    [Theory]
    [InlineData("search for the add function in the repo and explain it")]
    [InlineData("look up the config value in our codebase")]
    [InlineData("find out where the retry logic lives in this project")]
    public void TaskHintsWebNeed_OpensGateForNoisyRepoInternalPhrasing(string prompt)
    {
        Assert.True(Hints(prompt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TaskHintsWebNeed_BlankPromptsNeverHint(string? prompt)
    {
        Assert.False(Hints(prompt));
    }

    [Theory]
    [InlineData("_web_search", true)]
    [InlineData("_web_fetch", true)]
    [InlineData("_create_file", false)]
    [InlineData("_command", false)]
    [InlineData("_create_directory", false)]
    [InlineData(null, false)]
    public void IsWebStep_RecognizesWebMarkers(string? file, bool expected)
    {
        Assert.Equal(expected, IsWeb(file));
    }

    // ── TaskExplicitlyCommandsWeb (decisive explicit web-search commands) ──

    private static bool ExplicitWeb(string? p) => AgentPlanningPolicy.TaskExplicitlyCommandsWeb(p);

    // A direct web-search command is AUTHORITATIVE — the classifier may not veto it.
    // These are the exact phrasings that must bypass the LLM verification entirely.
    [Theory]
    [InlineData("Search the web for an interesting and relevant AI article and write the data into a text file on my desktop")]
    [InlineData("search the internet for the latest npm versions and save them to a file")]
    [InlineData("look up the current exchange rates online")]
    [InlineData("fetch the current weather from the web")]
    [InlineData("browse the web for job postings and summarize them")]
    [InlineData("find an interesting AI article online and write it to a file")]
    [InlineData("google for the latest AI news online")]
    [InlineData("do a web search for recent advances in machine learning")]
    public void TaskExplicitlyCommandsWeb_DetectsDirectWebSearchCommands(string prompt)
    {
        Assert.True(ExplicitWeb(prompt));
    }

    // Noisy phrasing that usually means searching the REPO must stay with the LLM
    // classifier — only the classifier decides those, never the deterministic regex.
    [Theory]
    [InlineData("search for the add function in the repo and explain it")]
    [InlineData("look up the config value in our codebase")]
    [InlineData("find out where the retry logic lives in this project")]
    [InlineData("Refactor the login component and add tests")]
    [InlineData("Use the Google Maps API to geocode addresses")]
    public void TaskExplicitlyCommandsWeb_LeavesRepoInternalPhrasingToTheClassifier(string prompt)
    {
        Assert.False(ExplicitWeb(prompt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TaskExplicitlyCommandsWeb_BlankPromptsNeverFire(string? prompt)
    {
        Assert.False(ExplicitWeb(prompt));
    }

    // ── TryParseWebNeedVerdict (classifier JSON parsing + reason capture) ──

    private static (bool Ok, bool NeedsWeb, string? Query, string Reason) ParseVerdict(string json)
    {
        var ok = AgentPlanningPolicy.TryParseWebNeedVerdict(json, out var needsWeb, out var query, out var reason);
        return (ok, needsWeb, query, reason);
    }

    [Fact]
    public void TryParseWebNeedVerdict_ParsesWebTaskWithQuery()
    {
        var (ok, needsWeb, query, reason) = ParseVerdict(
            "{\"needsWeb\": true, \"reason\": \"task asks for live data\", \"query\": \"latest npm version of lodash\"}");
        Assert.True(ok);
        Assert.True(needsWeb);
        Assert.Equal("latest npm version of lodash", query);
        Assert.Equal("task asks for live data", reason);
    }

    // The exact failure mode from the "Search the web for an AI article" run: the
    // classifier vetoes with a reason. The reason must come back so the agent panel
    // can surface it instead of showing only the generic rejection line.
    [Fact]
    public void TryParseWebNeedVerdict_ParsesNoWebVetoWithReason()
    {
        var (ok, needsWeb, query, reason) = ParseVerdict(
            "{\"needsWeb\": false, \"reason\": \"the article content can be written from the model's own knowledge\", \"query\": \"\"}");
        Assert.True(ok);
        Assert.False(needsWeb);
        Assert.Null(query);
        Assert.Contains("own knowledge", reason);
    }

    [Fact]
    public void TryParseWebNeedVerdict_MissingReasonDefaultsEmpty()
    {
        var (ok, _, _, reason) = ParseVerdict("{\"needsWeb\": false}");
        Assert.True(ok);
        Assert.Equal("", reason);
    }

    [Fact]
    public void TryParseWebNeedVerdict_MalformedJsonFails()
    {
        var (ok, _, _, _) = ParseVerdict("{\"needsWeb\": tru");
        Assert.False(ok);
    }

    [Fact]
    public void TryParseWebNeedVerdict_NonObjectRootFails()
    {
        var (ok, _, _, _) = ParseVerdict("[1,2,3]");
        Assert.False(ok);
    }

    [Fact]
    public void TryParseWebNeedVerdict_BlankOrEmptyJsonFails()
    {
        Assert.False(ParseVerdict("").Ok);
        Assert.False(ParseVerdict("   ").Ok);
    }

    // ── ShouldRejectFetchCommand (scoped fetch-in-command guard) ──
    // The guard fires ONLY on tasks that hint at needing web data, so a bare legit
    // `curl https://…/health` on a non-web task stays allowed as a real command.

    private static bool RejectFetch(string? prompt, string? command) =>
        AgentPlanningPolicy.ShouldRejectFetchCommand(prompt, command);

    [Fact]
    public void ShouldRejectFetchCommand_RejectsOnWebHintingTask()
    {
        const string prompt = "Search the web for an interesting and relevant AI article and write the data into a text file on my desktop";
        const string command = "Invoke-RestMethod https://api.current.ai/articles | Select-Object title, summary, url | ConvertTo-Csv -NoTypeInformation | Set-Content \"C:\\Users\\Saint\\Desktop\\ai_article_data.txt\"";
        Assert.True(RejectFetch(prompt, command));
    }

    [Fact]
    public void ShouldRejectFetchCommand_AllowsCurlHealthCheckOnNonWebTask()
    {
        Assert.False(RejectFetch("Check the uptime of the local service and report the response code", "curl https://my-server/health"));
    }

    [Fact]
    public void ShouldRejectFetchCommand_AllowsFetchCommandOnNonWebTask()
    {
        // The api.current.ai-style command on a task with NO web hint is a legit terminal
        // command (the relaxation: the guard only polices web-needing tasks).
        Assert.False(RejectFetch("Run the build and fix the failing tests", "Invoke-RestMethod https://api.current.ai/articles | Out-File out.json"));
    }

    [Fact]
    public void ShouldRejectFetchCommand_AllowsLegitUrlCommandsOnWebTask()
    {
        // Detector alone says no (git clone is not a content fetch) — hint doesn't matter.
        Assert.False(RejectFetch("search the web for the latest release and clone it", "git clone https://github.com/foo/bar.git"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ShouldRejectFetchCommand_BlankPromptsNeverReject(string? prompt)
    {
        Assert.False(RejectFetch(prompt, "curl https://example.com/data.json"));
    }

    // ── WebNotNeededFeedback (rejection feedback for web steps on non-web tasks) ──

    [Fact]
    public void WebNotNeededFeedback_ExistsAndIsDistinctFromWebNeedFeedback()
    {
        var notNeeded = AgentPlanningPolicy.WebNotNeededFeedback;
        var needed = AgentPlanningPolicy.WebNeedFeedback;
        Assert.False(string.IsNullOrWhiteSpace(notNeeded));
        Assert.NotEqual(needed, notNeeded);
    }

    [Fact]
    public void WebNotNeededFeedback_SteersAwayFromWebTools()
    {
        var fb = AgentPlanningPolicy.WebNotNeededFeedback;
        // The feedback must tell the model to drop the web step and work from repo context.
        Assert.Contains("does NOT need CURRENT EXTERNAL information", fb);
        Assert.Contains("_web_search/_web_fetch step is unnecessary", fb);
        Assert.Contains("DISCOVERY CONTEXT", fb);
    }

    // ── BuildFallbackWebQuery (query used by the auto-injected _web_search step) ──

    private static readonly MethodInfo FallbackQueryMethod = typeof(Weaver.Controllers.AgentController)
        .GetMethod("BuildFallbackWebQuery", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildFallbackWebQuery static method not found.");

    private static string FallbackQuery(string? p) => (string)(FallbackQueryMethod.Invoke(null, new object?[] { p }) ?? "");

    [Fact]
    public void BuildFallbackWebQuery_FlattensPromptAndCollapsesWhitespace()
    {
        var q = FallbackQuery("Perform\n   three independent\r\n web searches\t today");
        Assert.Equal("Perform three independent web searches today", q);
    }

    [Fact]
    public void BuildFallbackWebQuery_TruncatesVeryLongPrompts()
    {
        var longPrompt = string.Concat(Enumerable.Repeat("search the web for current prices of gold silver copper ", 8)); // > 160 chars
        var q = FallbackQuery(longPrompt);
        Assert.True(q.Length <= 160, $"query should be truncated to <=160 chars, was {q.Length}");
        Assert.StartsWith("search the web for current prices", q);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildFallbackWebQuery_BlankPromptReturnsGenericQuery(string? prompt)
    {
        Assert.Equal("latest information", FallbackQuery(prompt));
    }

    // ── IsFilesystemPrepStep (missing-web-search gate exemption) ──
    // A web-needing task that demands a folder must be able to create it up front: the gate
    // treats _create_directory AND mkdir-style _command steps as filesystem PREP instead of
    // bouncing them for "no web step yet" (the "failed over and over to create a directory"
    // report: mkdir/_create_file proposals were rejected 3× before _create_directory landed).

    private static bool IsPrep(PlanStep step) => AgentPlanningPolicy.IsFilesystemPrepStep(step);

    [Theory]
    [InlineData("_create_directory")]
    public void IsFilesystemPrepStep_ExemptsCreateDirectory(string file)
    {
        Assert.True(IsPrep(new PlanStep { File = file, Change = "benchmark_test_16" }));
    }

    [Theory]
    [InlineData("mkdir \"benchmark_test_16\"")]
    [InlineData("mkdir -p benchmark_test_16")]
    [InlineData("md benchmark_test_16")]
    [InlineData("New-Item -ItemType Directory -Path \"benchmark_test_16\" -Force")]
    public void IsFilesystemPrepStep_ExemptsDirectoryCreationCommands(string command)
    {
        Assert.True(IsPrep(new PlanStep { File = "_command", Change = command }));
    }

    [Theory]
    [InlineData("dotnet build")]
    [InlineData("npm install")]
    [InlineData("git push")]
    [InlineData("rm -rf benchmark_test_16")]
    [InlineData("Set-Content -Path out.txt -Value data")]
    public void IsFilesystemPrepStep_DoesNotExemptOtherCommands(string command)
    {
        Assert.False(IsPrep(new PlanStep { File = "_command", Change = command }));
    }

    [Fact]
    public void IsFilesystemPrepStep_DoesNotExemptFilesOrEdits()
    {
        Assert.False(IsPrep(new PlanStep { File = "_create_file", Change = "benchmark_test_16/pokemon_data.csv" }));
        Assert.False(IsPrep(new PlanStep { File = "src/app/app.ts", Change = "fix the bug" }));
    }

    // ── DirectoryIntentFeedback (steering when _create_file is really a folder) ──
    // When the planner reaches for _create_file to make a directory, the gate must reject
    // with TARGETED steering toward _create_directory instead of the generic web feedback.

    private static string? DirFeedback(PlanStep step) => AgentPlanningPolicy.DirectoryIntentFeedback(step);

    [Theory]
    [InlineData("Create benchmark_test_16 directory")]
    [InlineData("create a folder called benchmark_test_16")]
    [InlineData("Make a directory named benchmark_test_16")]
    [InlineData("add new folder benchmark_test_16")]
    public void DirectoryIntentFeedback_SteersFolderMislabelsToCreateDirectory(string change)
    {
        var fb = DirFeedback(new PlanStep { File = "_create_file", Change = change, NewString = "x" });
        Assert.NotNull(fb);
        Assert.Contains("FOLDER/DIRECTORY", fb);
        Assert.Contains("_create_directory", fb);
    }

    [Fact]
    public void DirectoryIntentFeedback_NullForRealFiles()
    {
        Assert.Null(DirFeedback(new PlanStep { File = "_create_file", Change = "benchmark_test_16/pokemon_data.csv", NewString = "data" }));
        Assert.Null(DirFeedback(new PlanStep { File = "_create_file", Change = "Fix the README to add a section", NewString = "x" }));
        Assert.Null(DirFeedback(new PlanStep { File = "_create_directory", Change = "Create benchmark_test_16 directory" }));
        Assert.Null(DirFeedback(new PlanStep { File = "_command", Change = "mkdir benchmark_test_16" }));
    }

    // ── ScraperScriptFeedback (steering when _create_file is a scraper/fetch script) ──
    // The "wrote a Python app to do a fetch" drift: on a web-needing task the planner
    // forgets _web_fetch exists and plans a script that programs the HTTP request
    // (requests.get + open(...,"w")). The gate must steer to _web_fetch.

    private static string? ScraperFb(PlanStep step) => AgentPlanningPolicy.ScraperScriptFeedback(step);

    [Theory]
    [InlineData("import requests\nresp = requests.get(\"https://pokeapi.co/api/v2/pokemon?limit=1025\")\nwith open(\"pokemon_data.csv\", \"w\") as f: f.write(resp.text)\n")]
    [InlineData("from urllib.request import urlopen\nhtml = urlopen(\"https://api.example.com/data\").read()\nopen(\"out.json\", \"wb\").write(html)\n")]
    [InlineData("fetch(\"https://api.example.com/data\").then(r => r.text()).then(t => fs.writeFileSync(\"out.txt\", t))\n")]
    [InlineData("$r = Invoke-RestMethod -Uri \"https://api.example.com/data\"; $r | Set-Content -Path out.json\n")]
    public void ScraperScriptFeedback_SteersScraperScriptsToWebFetch(string content)
    {
        var fb = ScraperFb(new PlanStep { File = "_create_file", Change = "benchmark_test_16/fetch.py", NewString = content });
        Assert.NotNull(fb);
        Assert.Contains("SCRAPER/FETCH script", fb);
        Assert.Contains("_web_fetch", fb);
    }

    [Fact]
    public void ScraperScriptFeedback_NullForNormalCodeAndNonScraperFiles()
    {
        // An HTTP client SERVICE (fetches, returns data, no file write) is NOT a scraper.
        Assert.Null(ScraperFb(new PlanStep { File = "_create_file", Change = "src/clients/WeatherClient.cs",
            NewString = "public class WeatherClient { var c = new HttpClient(); var s = c.GetStringAsync(\"https://api.weather.com\").Result; return s; }" }));
        // A file with a URL but no fetch word, or a fetch without a URL, is not a scraper.
        Assert.Null(ScraperFb(new PlanStep { File = "_create_file", Change = "links.md", NewString = "See https://example.com/docs" }));
        Assert.Null(ScraperFb(new PlanStep { File = "_create_file", Change = "scrape.py", NewString = "import requests\nprint('no url')\n" }));
        // Non-create_file steps never get scraper feedback.
        Assert.Null(ScraperFb(new PlanStep { File = "_command", Change = "python fetch.py" }));
        Assert.Null(ScraperFb(new PlanStep { File = "_web_fetch", Change = "https://pokeapi.co" }));
    }

    // ── LooksLikeScraperScriptContent (the detector itself, used by the feedback) ──

    [Fact]
    public void LooksLikeScraperScriptContent_DetectsFetchAndWriteScripts()
    {
        Assert.True(AgentProjectUtilities.LooksLikeScraperScriptContent(
            "import requests\nresp = requests.get(\"https://pokeapi.co/api/v2/pokemon?limit=1025\")\nwith open(\"pokemon_data.csv\", \"w\") as f: f.write(resp.text)\n"));
        Assert.False(AgentProjectUtilities.LooksLikeScraperScriptContent("just some text"));
        Assert.False(AgentProjectUtilities.LooksLikeScraperScriptContent(null));
        Assert.False(AgentProjectUtilities.LooksLikeScraperScriptContent(""));
    }

    // ── SCRAPER-FILE GUARD in ValidateIncrementalStepAsync (fires even AFTER a web step ran) ──
    // The missing-web-search gate only rejects while NO web step is in the plan, so a scraper
    // proposed AFTER the search used to sail straight through and land on disk. The validator
    // guard closes that hole: a _create_file whose payload is a scraper/fetch script is
    // rejected on any web-needing task, web step in the plan or not. Scoped to
    // TaskHintsWebNeed like the fetch-in-command guard — a task with no live-data hint that
    // explicitly asks for a standalone scraper utility still gets it.

    private static readonly MethodInfo ScraperValidateMethod = typeof(Weaver.Controllers.AgentController).GetMethod(
        "ValidateIncrementalStepAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static (bool valid, string? reason) ValidateScraperStep(PlanStep step, string prompt, List<PlanStep> planSoFar)
    {
        var controller = RuntimeHelpers.GetUninitializedObject(typeof(Weaver.Controllers.AgentController));
        var task = (Task<(bool valid, string? reason)>)ScraperValidateMethod.Invoke(controller, new object?[]
        {
            step, prompt, /*discoveryContext*/ "", planSoFar,
            /*projectRoot*/ ".", /*emitSse*/ false, CancellationToken.None, /*skipLlm*/ false,
            /*lastStepCompletionNote*/ null, /*attachedFiles*/ null
        })!;
        var result = task.GetAwaiter().GetResult();
        return (result.valid, result.reason);
    }

    private const string WebTaskPrompt =
        "Create a folder called 'benchmark_test_16' at the project root. Inside it, create a file called 'pokemon_data.csv'. " +
        "Search the web to find the PokeAPI endpoint, then fetch the live Pokemon data (id numbers, stats and types) and write the data into pokemon_data.csv.";

    private static PlanStep ScraperFileStep() => new()
    {
        File = "_create_file",
        Change = "fetch_poke_data.py",
        NewString = "import requests\n" +
                    "resp = requests.get(\"https://pokeapi.co/api/v2/pokemon?limit=1025\")\n" +
                    "with open(\"pokemon_data.csv\", \"w\") as f:\n" +
                    "    f.write(resp.text)\n"
    };

    [Fact]
    public void ScraperCreateFile_RejectedEvenWhenWebStepAlreadyRan()
    {
        // The exact benchmark-run shape: search already committed, then the planner still
        // reaches for the Python scraper. The missing-web-search gate is silent here — the
        // validator guard must reject with scraper steering.
        var (valid, reason) = ValidateScraperStep(ScraperFileStep(), WebTaskPrompt,
            new List<PlanStep> { new() { File = "_web_search", Change = "PokeAPI endpoints" } });
        Assert.False(valid);
        Assert.NotNull(reason);
        Assert.Contains("SCRAPER/FETCH script", reason);
        Assert.Contains("_web_fetch", reason);
    }

    [Fact]
    public void ScraperCreateFile_RejectedEvenBeforeAnyWebStep()
    {
        // Same guard, earlier in the run: no web step yet, but the task hints at web need.
        var (valid, reason) = ValidateScraperStep(ScraperFileStep(), WebTaskPrompt, new List<PlanStep>());
        Assert.False(valid);
        Assert.NotNull(reason);
        Assert.Contains("SCRAPER/FETCH script", reason);
        Assert.Contains("_web_fetch", reason);
    }

    [Fact]
    public void ScraperCreateFile_AllowedOnNonWebTask()
    {
        // A task that explicitly asks for a standalone scraper utility with NO live-data hint
        // ("build me a script that downloads the CSV from our mirror") must still get it — the
        // guard is scoped to TaskHintsWebNeed, mirroring the fetch-in-command guard.
        var (valid, reason) = ValidateScraperStep(ScraperFileStep(),
            "Add a script fetch_poke_data.py that downloads the CSV snapshot from our internal mirror and writes it next to the script.",
            new List<PlanStep>());
        Assert.True(valid, reason);
    }

    // ── RUN-A-SCRAPER GUARD (the _command that executes the scraper) ──
    // The run that followed the scraper _create_file landing: `python fetch_poke_data.py`,
    // `python .\fetch_poke_data.py`, or `curl <url> | python`. The validator rejects it on
    // web-needing tasks (existing scraper on disk, or a script that does not exist), steers
    // to _web_fetch/_scraper, and stands down for non-web tasks, existing non-scraper scripts,
    // and prompts that explicitly demand a script.

    private const string ScraperContent =
        "import requests\n" +
        "resp = requests.get(\"https://pokeapi.co/api/v2/pokemon?limit=1025\")\n" +
        "with open(\"pokemon_data.csv\", \"w\") as f:\n" +
        "    f.write(resp.text)\n";

    private static (bool valid, string? reason) ValidateScraperCommandAt(
        PlanStep step, string prompt, List<PlanStep> planSoFar, string projectRoot)
    {
        var controller = RuntimeHelpers.GetUninitializedObject(typeof(Weaver.Controllers.AgentController));
        var task = (Task<(bool valid, string? reason)>)ScraperValidateMethod.Invoke(controller, new object?[]
        {
            step, prompt, /*discoveryContext*/ "", planSoFar,
            projectRoot, /*emitSse*/ false, CancellationToken.None, /*skipLlm*/ false,
            /*lastStepCompletionNote*/ null, /*attachedFiles*/ null
        })!;
        var result = task.GetAwaiter().GetResult();
        return (result.valid, result.reason);
    }

    [Fact]
    public void RunScraperScriptCommand_ExistingScraperFile_Rejected()
    {
        // fetch_poke_data.py exists on disk from an earlier (pre-guard) run and IS a scraper:
        // `python fetch_poke_data.py` on a web task must be rejected and steered to _web_fetch.
        var root = Path.Combine(Path.GetTempPath(), "run-scraper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "fetch_poke_data.py"), ScraperContent);
            var (valid, reason) = ValidateScraperCommandAt(
                new PlanStep { File = "_command", Change = "python fetch_poke_data.py" },
                WebTaskPrompt, new List<PlanStep>(), root);
            Assert.False(valid);
            Assert.NotNull(reason);
            Assert.Contains("RUNS a scraper/fetch script", reason);
            Assert.Contains("_web_fetch", reason);
            Assert.Contains("_scraper", reason);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RunScraperScriptCommand_NonexistentScript_Rejected()
    {
        // The script never landed (the scraper-file guard rejected the create), so running it
        // is running nothing — reject and steer to _web_fetch.
        var (valid, reason) = ValidateScraperCommandAt(
            new PlanStep { File = "_command", Change = "python .\\fetch_poke_data.py" },
            WebTaskPrompt, new List<PlanStep>(), Path.GetTempPath());
        Assert.False(valid);
        Assert.NotNull(reason);
        Assert.Contains("does not exist", reason);
        Assert.Contains("_web_fetch", reason);
    }

    [Fact]
    public void FetchPipeIntoInterpreter_Rejected()
    {
        // `curl <url> | python` downloads and executes — the same drift in one command. It is
        // rejected (by the fetch-in-command guard, which fires earlier in the validator) and
        // steered to the web tools regardless of which guard's text lands.
        var (valid, reason) = ValidateScraperCommandAt(
            new PlanStep { File = "_command", Change = "curl https://pokeapi.co/api/v2/pokemon?limit=1025 | python" },
            WebTaskPrompt, new List<PlanStep>(), Path.GetTempPath());
        Assert.False(valid);
        Assert.NotNull(reason);
        Assert.Contains("_web_fetch", reason);
    }

    [Fact]
    public void RunScraperScriptCommand_NonWebTask_Allowed()
    {
        // No live-data hint: running an existing utility script stays legitimate.
        var (valid, reason) = ValidateScraperCommandAt(
            new PlanStep { File = "_command", Change = "python scripts/refresh.py" },
            "Run scripts/refresh.py to regenerate the local mirror files.",
            new List<PlanStep>(), Path.GetTempPath());
        Assert.True(valid, reason);
    }

    [Fact]
    public void RunExistingNonScraperScript_AllowedEvenOnWebTask()
    {
        // An existing script that is NOT a scraper (no URL fetch + write) is a legitimate run
        // even on a web task — e.g. "search for X, then run the repo's analyze.py".
        var root = Path.Combine(Path.GetTempPath(), "run-nonscraper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "analyze.py"), "print('hello')\n");
            var (valid, reason) = ValidateScraperCommandAt(
                new PlanStep { File = "_command", Change = "python analyze.py" },
                WebTaskPrompt, new List<PlanStep>(), root);
            Assert.True(valid, reason);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ExplicitScriptRequest_StandsDownBothScraperGuards()
    {
        // The user's escape hatch: "unless the prompt specifically asks for a python script!".
        // With an explicit script demand, writing the scraper AND running it are the task —
        // both guards stand down (a web step already exists, so the missing-web-search gate
        // is silent and the validator decides alone).
        const string explicitPrompt =
            "Write a python script fetch_poke_data.py that fetches the PokeAPI data and writes it into benchmark_test_16/pokemon_data.csv.";
        var withWebStep = new List<PlanStep> { new() { File = "_web_search", Change = "PokeAPI endpoints" } };
        var (createValid, createReason) = ValidateScraperStep(ScraperFileStep(), explicitPrompt, withWebStep);
        Assert.True(createValid, createReason);
        var (runValid, runReason) = ValidateScraperCommandAt(
            new PlanStep { File = "_command", Change = "python fetch_poke_data.py" },
            explicitPrompt, withWebStep, Path.GetTempPath());
        Assert.True(runValid, runReason);
    }
}
