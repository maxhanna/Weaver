using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Weaver;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Integration coverage for the OS-filesystem web-task path through the REAL interleaved
/// pipeline (Orchestrate → discovery → checklist → incremental plan → execute → verify),
/// driven with a SCRIPTED fake LLM and a fake HTTP client that answers the web search/fetch
/// GETs. This is the regression test for the "Search the web for an interesting and relevant
/// AI article and write the data into a text file on my desktop" class of run:
///
///   1. The planner's step 1 is a _web_search step, which EXECUTES against the fake
///      DuckDuckGo endpoint and its output is harvested into the discovery context
///      (### WEB RESULTS [query] ###).
///   2. The planner's step 2 must be a _web_fetch of a concrete URL FROM those results —
///      NOT an invented edit to a repo file (the pre-fix behavior: the model drifted into
///      writing a Selenium/Python scraper or an application-code edit instead of using the
///      tool surface).
///   3. The step-2 planner turn must actually SEE the harvested results (the injection
///      feature), asserted on the recorded step-2 user prompt.
///   4. When the task demands an OS output file, the successful _web_fetch step auto-dumps
///      the harvested web results to the demanded target path IN THAT SAME STEP (the data
///      is used up while fresh instead of being compacted through later steps, and CRUD on
///      the demanded path happens right away) — the run genuinely completes with the file
///      on disk, zero repo edits, and every LLM call the script accounts for (Unmatched
///      must be empty).
///
/// A second trace covers the steering path: a run that demands an OS output file but has no
/// web content to dump; the premature planComplete is REJECTED with feedback and the planner
/// is steered to plan the _command write itself.
/// </summary>
public class WebTaskInterleavedPipelineIntegrationTests : IDisposable
{
    private const string SearchQuery = "AI research breakthroughs latest";
    private const string LinksRel = "src/links.ts"; // fixture the cross-tool edit arm writes to

    private readonly string _base;
    private readonly string _projectRoot;
    private readonly string _dumpTarget;   // test 1: the file the auto-dump must create
    private readonly string _steerTarget;  // test 2: the file the steered _command must create
    private readonly string _neverWrittenTarget; // test 3: the file NO run may ever create
    private readonly string _newsTarget;   // test 4: the file the news-digest eager dump must create
    private readonly string _folderPath;    // tests: repo-relative folder the planner must create first
    private readonly string _csvPath;       // tests: the demanded data file inside _folderPath
    private readonly DatabaseService _db;
    private readonly BoardDataService _boardData;
    private readonly WebTaskScriptedClientFactory _clientFactory;

    public WebTaskInterleavedPipelineIntegrationTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "weaver_webtask_" + Guid.NewGuid().ToString("N"));
        _projectRoot = Path.Combine(_base, "proj");
        Directory.CreateDirectory(_projectRoot);
        Directory.CreateDirectory(Path.Combine(_base, "data"));
        Directory.CreateDirectory(Path.Combine(_base, "dump"));
        Directory.CreateDirectory(Path.Combine(_base, "dump2"));
        Directory.CreateDirectory(Path.Combine(_base, "dump3"));
        Directory.CreateDirectory(Path.Combine(_base, "dump4"));
        _dumpTarget = Path.Combine(_base, "dump", "ai_article_data.txt");
        _steerTarget = Path.Combine(_base, "dump2", "report.txt");
        _folderPath = Path.Combine(_projectRoot, "benchmark_test_16");
        _csvPath = Path.Combine(_folderPath, "pokemon_data.csv");
        _neverWrittenTarget = Path.Combine(_base, "dump3", "out.txt");
        _newsTarget = Path.Combine(_base, "dump4", "news_roundup.txt");

        _db = new DatabaseService(
            Path.Combine(_base, "data", "weaver.db"),
            Path.Combine(_base, "data"),
            Path.Combine(_base, "data", "weaverconfig.json"));
        _boardData = new BoardDataService(_db, NullLogger<BoardDataService>.Instance);
        _clientFactory = new WebTaskScriptedClientFactory(PlannerMode.WebChain, _steerTarget);
    }

    /// <summary>A canned live-browser runner so a scripted _browser_test plan step
    /// passes deterministically without launching a real server (mirrors
    /// BenchmarkLiveWebTestTests.FakeBrowserAutomationService). When FailUiTestCount is
    /// > 0, the first that-many UI-test calls FAIL exactly like the benchmark-22 live
    /// failure (the server process exits before becoming ready — missing 'express'),
    /// then subsequent calls pass — letting the repair-loop re-run prove the server
    /// starts after the fix.</summary>
    private sealed class FakeBrowserAutomationService : BrowserAutomationService
    {
        public string? LastUiTarget { get; private set; }
        public string? LastApiTarget { get; private set; }
        /// <summary>Number of leading RunUiTestAsync calls that fail (server could not
        /// start). int.MaxValue fails every call.</summary>
        public int FailUiTestCount { get; set; }

        public override Task<BrowserTestReport> RunUiTestAsync(string projectRoot, string target, string? prompt, CancellationToken ct = default)
        {
            LastUiTarget = target;
            if (FailUiTestCount > 0)
            {
                FailUiTestCount--;
                return Task.FromResult(new BrowserTestReport
                {
                    Target = target, Mode = "failed", Passed = false,
                    LaunchError = "Cannot find module 'express'",
                    Findings =
                    {
                        new TestFinding("fail",
                            "server process exited before becoming ready — node: Cannot find module 'express'")
                    }
                });
            }
            return Task.FromResult(new BrowserTestReport { Target = target, Mode = "http", Passed = true });
        }

        public override Task<BrowserTestReport> RunApiTestAsync(string projectRoot, string target, CancellationToken ct = default)
        {
            LastApiTarget = target;
            return Task.FromResult(new BrowserTestReport { Target = target, Mode = "http", Passed = true });
        }
    }

    public void Dispose()
    {
        _clientFactory.Dispose();
        try { Directory.Delete(_base, true); } catch { }
    }

    [Fact]
    public async Task WebTask_FetchDemandsOsFile_AutoDumpsInTheSameFetchStep()
    {
        var controller = BuildController();
        // The between-steps WEB assessment (new) runs after each successful web step. Script
        // it content-aware: incomplete until the demanded dump file exists — so the search
        // step keeps planning, and only after the FETCH step lands its eager auto-dump does
        // the assessment accept completion.
        _clientFactory.WebAssessComplete = () => File.Exists(_dumpTarget);
        // The target is an absolute temp path (quoted so spaces are safe) — the auto-dump
        // must write HERE, never to the real desktop.
        // Deliberately NOT a news-marked prompt ("…interesting and relevant AI article and
        // write the data into a text file…" now routes to the news digest via rule C) — this
        // test locks the DuckDuckGo harvest → fetch-step eager-dump machinery.
        var prompt = $"Search the web for recent AI breakthroughs and write the data into a text file at \"{_dumpTarget}\".";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        // The run finished complete AND the demanded file exists with the harvested data.
        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}");
        Assert.True(File.Exists(_dumpTarget), $"auto-dump should have written {_dumpTarget}");
        var dumped = File.ReadAllText(_dumpTarget, Encoding.UTF8);
        Assert.Contains("AlphaFold 3", dumped);          // the harvested DuckDuckGo result
        Assert.Contains("### WEB RESULTS", dumped);      // structured section per result
        Assert.Contains("Task: Search the web", dumped); // the header

        // Step 1 actually EXECUTED a web search and its output was harvested: the result
        // carries the full DuckDuckGo-shaped output including the article URLs.
        var searchResult = allSteps.OfType<Dictionary<string, object?>>()
            .Single(r => r.GetValueOrDefault("type")?.ToString() == "_web_search");
        Assert.Equal("done", searchResult.GetValueOrDefault("status")?.ToString());
        var searchOutput = searchResult.GetValueOrDefault("output")?.ToString() ?? "";
        Assert.Contains("https://example.com/alphafold3", searchOutput);
        Assert.Contains("https://example.com/llm-benchmarks", searchOutput);

        // The step-2 planner turn SAW the harvested results — the injection feature. The
        // recorded user prompt must contain the WEB RESULTS section AND the nudge that
        // steers step 2 to _web_fetch a concrete URL from it.
        var step2Prompt = Assert.Single(_clientFactory.Step2PlannerPrompts);
        Assert.Contains("### WEB RESULTS", step2Prompt);
        Assert.Contains("https://example.com/alphafold3", step2Prompt);
        Assert.Contains("### WEB RESULTS ARE IN CONTEXT ###", step2Prompt);
        Assert.Contains("_web_fetch step with THAT exact URL from the results", step2Prompt);

        // The plan is _web_search → _web_fetch. The demanded file was written INSIDE the
        // fetch step by the deterministic eager auto-dump — no extra LLM-planned write step
        // exists and no plan-complete gate needed to add one.
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_web_search", "_web_fetch" }, plan!.Plan.Select(s => s.File).ToArray());
        Assert.Equal("AI research breakthroughs latest", plan.Plan[0].Change);
        Assert.Equal("https://example.com/alphafold3", plan.Plan[1].Change);

        // The eager auto-dump is recorded as a created OS-file step result (os=true — an
        // OS-filesystem write, not a repo edit) carrying the demanded path.
        var dumpResult = allSteps.OfType<Dictionary<string, object?>>()
            .Single(r => r.GetValueOrDefault("type")?.ToString() == "create" &&
                         r.GetValueOrDefault("os") is true);
        Assert.Equal("created", dumpResult.GetValueOrDefault("status")?.ToString());
        Assert.Equal(_dumpTarget, dumpResult.GetValueOrDefault("path")?.ToString());

        // ZERO repo file edits were applied — the core "not an invented edit" assertion. A
        // pre-fix run would have planned an edit to a repo file (or a _create_file for a
        // scraper script) and applied it. The OS-file auto-dump (os=true) is not a repo edit.
        var editResults = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("os") is not true &&
                        r.GetValueOrDefault("type")?.ToString() is "edit" or "create" &&
                        r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
            .ToList();
        Assert.Empty(editResults);

        // Every LLM call the pipeline made was one the script accounted for.
        Assert.Empty(_clientFactory.Unmatched);

        // Sanity: the scripted LLM saw the expected call kinds (checklist + 2 planner turns
        // — the search and the fetch; no verify/cohesion/post-verify — the dump is
        // deterministic and no repo edits exist to verify).
        Assert.Contains("checklist", _clientFactory.Calls);
        Assert.Equal(2, _clientFactory.Calls.Count(c => c == "planner-step"));
        Assert.DoesNotContain("verify", _clientFactory.Calls);
        // The WEB-ONLY completion assessment ran after the SEARCH step (incomplete — the
        // demanded file did not exist yet). After the FETCH step the eager dump had ALREADY
        // written the file, so the dump-task short-circuit completed the run DETERMINISTICALLY
        // — no second assessment, and the fetched data never round-trips through the LLM (the
        // fix for the "planner re-emits the whole dataset inline" failure).
        Assert.Equal(1, _clientFactory.Calls.Count(c => c == "web-assess"));
    }

    [Fact]
    public async Task VisualVerifyTask_BuildThenPlanComplete_AutoInjectsBrowserTest()
    {
        // Regression for benchmark 22: a build-then-VISUALLY-VERIFY task ("check it for
        // visual bugs", "you must LOOK at the rendered page") used to declare the plan
        // complete after the BUILD steps — no _browser_test step, no server spin-up, no
        // Test Browser tab. The plan-complete visual gate must DETERMINISTICALLY auto-inject
        // the _browser_test step the moment the planner tries to close the run without it
        // (the weak-model failure was a planner that answered the steering by proposing
        // _command steps forever, whose rejections reset the regen counter so the 3-strike
        // cap never fired) — and only accept completion once that step actually ran.
        _clientFactory.Mode = PlannerMode.VisualVerifyBuild;
        var controller = BuildController();
        var prompt = "Create a folder called 'benchmark_test_22' at the project root. Inside it, build a small web game " +
                     "and then CHECK IT FOR VISUAL BUGS — you must LOOK at the rendered page to visually confirm " +
                     "the heading renders on screen.";
        Assert.True(TestIntentClassifier.HasVisualInspectionHint(prompt),
            "the test prompt must fire the visual-inspection hint");
        Assert.True(TestIntentClassifier.HasEditIntent(prompt),
            "the test prompt is a build task — must NOT short-circuit in discovery");
        Assert.False(TestIntentClassifier.DemandsLiveBrowserTest(prompt),
            "this prompt names no explicit browser test — the classifier is still consulted");

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        // The run completed AND actually executed a live browser test step — the visual
        // verification is no longer skipped.
        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}");
        Assert.True(plan!.Plan.Any(s => string.Equals(s.File, "_browser_test", StringComparison.OrdinalIgnoreCase)),
            "the final plan must include a _browser_test step");
        Assert.True(allSteps.OfType<Dictionary<string, object?>>()
                .Any(r => r.GetValueOrDefault("type")?.ToString() == "browser_test"),
            "the run must have actually executed the browser test step");
        // The gate consulted the visual classifier, then INJECTED the browser test itself
        // (deterministic — no planner round is trusted to propose _browser_test).
        Assert.Contains(_clientFactory.Calls, c => c == "visual-classifier");
    }

    [Fact]
    public async Task VisualVerifyTask_ExplicitBrowserTestDemand_BypassesClassifier()
    {
        // Regression for benchmark 23: the prompt EXPLICITLY names the live browser test as
        // a requirement ("pass/fail only based on the number of legs confirmed by the browser
        // test"), but the LLM classifier read the dominant build/implement language and
        // answered needsVisual=false — vetoing the browser test so the run "completed" without
        // ever verifying. An explicit browser-test demand must BYPASS the classifier and
        // inject the browser test deterministically: the task itself names the requirement, so
        // no model opinion can veto it.
        _clientFactory.Mode = PlannerMode.VisualVerifyBuild;
        var controller = BuildController();
        var prompt = "Create a folder called 'benchmark_test_23' at the project root. Inside it, build a small web game " +
                     "with a nice visual appearance, then use the live browser test to confirm the spider has 4 legs, " +
                     "then add 2 more legs so it has 6. The benchmark passes only if the leg count confirmed by the " +
                     "browser test goes from 4 to 6.";
        Assert.True(TestIntentClassifier.HasVisualInspectionHint(prompt),
            "the prompt must fire the visual-inspection hint (visual appearance)");
        Assert.True(TestIntentClassifier.HasEditIntent(prompt),
            "the prompt is a build task — must NOT short-circuit in discovery");
        Assert.True(TestIntentClassifier.DemandsLiveBrowserTest(prompt),
            "the prompt explicitly names the browser test — the gate must bypass the classifier");

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}");
        Assert.True(plan!.Plan.Any(s => string.Equals(s.File, "_browser_test", StringComparison.OrdinalIgnoreCase)),
            "the final plan must include a _browser_test step");
        Assert.True(allSteps.OfType<Dictionary<string, object?>>()
                .Any(r => r.GetValueOrDefault("type")?.ToString() == "browser_test"),
            "the run must have actually executed the browser test step");
        // The classifier is NEVER consulted — the explicit demand overrides it (even though
        // the scripted classifier would have answered needsVisual=false for this prompt).
        Assert.DoesNotContain(_clientFactory.Calls, c => c == "visual-classifier");
    }

    [Fact]
    public async Task VisualVerifyTask_StylingEdit_DoesNotForceABrowserTest()
    {
        // Precision lock: a task that merely MENTIONS visual styling ("style the button's
        // visual appearance") is a build/edit task, not a visual-INSPECTION demand — the
        // plan-complete gate must NOT force a _browser_test on it. The scripted classifier
        // answers needsVisual=false, and the run completes with just the build step.
        _clientFactory.Mode = PlannerMode.VisualVerifyBuild;
        var controller = BuildController();
        var prompt = "Style the button on the page with better visual appearance and a nicer layout.";
        Assert.True(TestIntentClassifier.HasVisualInspectionHint(prompt),
            "'visual appearance' must fire the hint (the gate then defers to the classifier)");

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}");
        Assert.True(!plan!.Plan.Any(s => string.Equals(s.File, "_browser_test", StringComparison.OrdinalIgnoreCase)),
            "a pure styling edit must never get a forced browser test");
        Assert.True(!allSteps.OfType<Dictionary<string, object?>>()
            .Any(r => r.GetValueOrDefault("type")?.ToString() == "browser_test"));
    }

    [Fact]
    public async Task VisualVerifyTask_FailedBrowserTest_BlocksCompletion()
    {
        // Regression for the benchmark-22 live failure: the browser test FAILED (the server
        // process exited before becoming ready — `node app.js` crashed with "Cannot find
        // module 'express'"), and the run then COMPLETED anyway as "Verified complete":
        // the file-level LLM verifier reads FILES, not a running server, so the broken
        // server looked fine on disk and the run was declared done over a game that could
        // never render. A failed live web test is DETERMINISTIC ground truth — the visual
        // verification the task demanded cannot have happened — so PostExecuteVerify must
        // surface it as a CONFIRMED issue, the repair loop must feed it to the replanner,
        // and NOTHING (triage, phantom-skip, churn breaker, replanner-exhausted fallback)
        // may complete the run while the browser test still fails.
        _clientFactory.Mode = PlannerMode.VisualVerifyBuild;
        var controller = BuildController();
        var fakeBrowser = (FakeBrowserAutomationService)GetField(controller, "_browserTestService");
        fakeBrowser.FailUiTestCount = int.MaxValue; // the server NEVER starts
        var prompt = "Create a folder called 'benchmark_test_22' at the project root. Inside it, build a small web game " +
                     "and then CHECK IT FOR VISUAL BUGS — you must LOOK at the rendered page to visually confirm " +
                     "the heading renders on screen. Use the live browser test suite.";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        // The run must NOT complete: the browser test failed (server never started), and the
        // file-level verifier saying "done" must not override that ground truth.
        Assert.False(complete, $"a run whose live web test failed must stay incomplete — plan summary: {plan?.Summary}");
        // No verified_complete entry is ever recorded.
        Assert.True(!allSteps.OfType<Dictionary<string, object?>>()
            .Any(r => r.GetValueOrDefault("type")?.ToString() == "verified_complete"));
        // The failed browser test is recorded with status "error" (server could not start).
        var failedTest = allSteps.OfType<Dictionary<string, object?>>()
            .Last(r => r.GetValueOrDefault("type")?.ToString() == "browser_test");
        Assert.Equal("error", failedTest.GetValueOrDefault("status")?.ToString());
        // The deterministic failure reached the repair-loop replanner (the plan-fixer ran and
        // saw the live-web-test failure as the issue to fix).
        Assert.NotEmpty(_clientFactory.RepairUserPrompts);
        Assert.Contains("Live web test failed", _clientFactory.RepairUserPrompts[0], StringComparison.OrdinalIgnoreCase);
        // The default scripted replanner proposes no steps — the repair loop must then fall
        // to the "replanner exhausted" path and STILL not complete over the failing test.
        Assert.Contains("replanner", _clientFactory.Calls);
    }

    [Fact]
    public async Task VisualVerifyTask_FailedBrowserTest_RepairReRun_UnblocksCompletion()
    {
        // The recovery half of the benchmark-22 fix: the browser test fails on the first run
        // (server could not start — missing 'express'), the repair loop's replanner produces
        // a fix (a dependency-free plain-http server), and the loop must RE-RUN the live web
        // test deterministically after the repair — only the browser test itself can prove
        // the server starts. A passing re-run REPLACES the stale failure (its fresh result
        // is what the deterministic completion check sees), so the run completes.
        _clientFactory.Mode = PlannerMode.VisualVerifyBrokenServer;
        var controller = BuildController();
        var fakeBrowser = (FakeBrowserAutomationService)GetField(controller, "_browserTestService");
        fakeBrowser.FailUiTestCount = 1; // first live web test fails, the re-run passes
        var prompt = "Create a folder called 'benchmark_test_22' at the project root. Inside it, build a small web game " +
                     "and then CHECK IT FOR VISUAL BUGS — you must LOOK at the rendered page to visually confirm " +
                     "the heading renders on screen. Use the live browser test suite.";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        Assert.True(complete, $"pipeline should complete after the repair re-run passed — plan summary: {plan?.Summary}");
        // The repair actually created the fixed server file (dependency-free http server).
        var serverPath = Path.Combine(_projectRoot, "benchmark_test_22", "server.js");
        Assert.True(File.Exists(serverPath), "the repair step must have created server.js");
        var serverContent = File.ReadAllText(serverPath, Encoding.UTF8);
        Assert.DoesNotContain("express", serverContent, StringComparison.OrdinalIgnoreCase);
        // TWO browser_test results: the original failure AND the repair-loop re-run that
        // passed — the re-run (last) result is what the completion check saw.
        var browserTests = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("type")?.ToString() == "browser_test").ToList();
        Assert.Equal(2, browserTests.Count);
        Assert.Equal("error", browserTests[0].GetValueOrDefault("status")?.ToString());
        Assert.Equal("done", browserTests[1].GetValueOrDefault("status")?.ToString());
        Assert.Contains("re-run", browserTests[1].GetValueOrDefault("description")?.ToString() ?? "",
            StringComparison.OrdinalIgnoreCase);
        // The repair replanner was fed the deterministic live-web-test failure.
        Assert.NotEmpty(_clientFactory.RepairUserPrompts);
        Assert.Contains("Live web test failed", _clientFactory.RepairUserPrompts[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VisualVerifyTask_HaltedLoop_StillRunsBrowserTest()
    {
        // Regression for the benchmark-22 exit that does NOT go through planComplete: the
        // planner's step proposals were rejected until the regen budget exhausted (the real
        // live failure — the oversized-anchor edit to backend/server.py was rejected twice,
        // then the response was unparseable), so the interleaved loop broke with
        // planCompleteDeclared=false. The planComplete visual gate never fired (it only runs
        // when the planner DECLARES completion), so a pre-fix run halted into post-execution
        // verification with NO _browser_test ever executed and finished "incomplete" without
        // ever opening the browser. The halted-run visual gate must inject + execute the
        // live web test deterministically the moment the loop breaks without one.
        _clientFactory.Mode = PlannerMode.VisualVerifyHalted;
        var controller = BuildController();
        var prompt = "Create a folder called 'benchmark_test_22' at the project root. Inside it, build a small web game " +
                     "and then CHECK IT FOR VISUAL BUGS — you must LOOK at the rendered page to visually confirm " +
                     "the heading renders on screen. Use the live browser test suite.";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        // The loop broke without planComplete (the planner's later turns were all rejected),
        // yet the run still executed the live browser test and completed.
        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}");
        var browserTests = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("type")?.ToString() == "browser_test").ToList();
        Assert.NotEmpty(browserTests);
        Assert.Equal("done", browserTests[^1].GetValueOrDefault("status")?.ToString());
        // The prompt explicitly names the live browser test, so the halted-run gate BYPASSES
        // the classifier and injects the browser test deterministically (no LLM dependency).
        Assert.DoesNotContain(_clientFactory.Calls, c => c == "visual-classifier");
    }

    [Fact]
    public async Task WebTask_NewsDigestSearch_DemandsOsFile_AutoDumpsInTheSameStep()
    {
        _clientFactory.Mode = PlannerMode.NewsDigestWrite;
        // Incomplete until the demanded file exists — so the search step keeps the run going,
        // and only after the eager dump lands (inside that same search step) does the
        // between-steps assessment accept completion.
        _clientFactory.WebAssessComplete = () => File.Exists(_newsTarget);
        var controller = BuildController();
        // News-marked phrasing (rule A: "news") + an OS-file demand — the _web_search step
        // routes to the fresh-news DIGEST (Google News/Bing RSS answered by the scripted
        // handler), and the eager OS-dump writes the digest straight to the demanded file IN
        // THE SEARCH STEP: no fetch, no _command, no later step where the digest could be
        // compacted or fabricated.
        var prompt = $"Fetch a recent AI news article and create a text file with the data on my desktop at \"{_newsTarget}\"";
        Assert.True(AgentOsOutputVerifier.TryGetOsFileOutputDemand(prompt, out _),
            $"the test prompt must demand an OS output file: {prompt}");

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        // The run finished complete AND the demanded file exists with the digest data.
        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        Assert.True(File.Exists(_newsTarget), $"eager dump should have written {_newsTarget}");
        var dumped = File.ReadAllText(_newsTarget, Encoding.UTF8);
        Assert.Contains("### WEB RESULTS", dumped);
        // The digest item (real RSS title from the scripted feed) rode verbatim into the file.
        Assert.Contains("AlphaFold 3 predicts protein structures with atom-level accuracy", dumped);
        Assert.Contains("Task: Fetch a recent AI news article", dumped); // the dump header

        // The run needed ONLY the search step — the digest WAS the demanded data, written in
        // that step; no fetch or command write was ever planned.
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_web_search" }, plan!.Plan.Select(s => s.File).ToArray());

        // The eager dump is recorded as a created OS-file step result (os=true) with the
        // demanded path.
        var dumpResult = allSteps.OfType<Dictionary<string, object?>>()
            .Single(r => r.GetValueOrDefault("type")?.ToString() == "create" &&
                         r.GetValueOrDefault("os") is true);
        Assert.Equal("created", dumpResult.GetValueOrDefault("status")?.ToString());
        Assert.Equal(_newsTarget, dumpResult.GetValueOrDefault("path")?.ToString());

        // ZERO repo edits — the OS-file auto-dump is not a repo edit.
        var editResults = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("os") is not true &&
                        r.GetValueOrDefault("type")?.ToString() is "edit" or "create" &&
                        r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
            .ToList();
        Assert.Empty(editResults);

        // Exactly ONE planner turn (the search) — the digest search itself satisfied the task.
        Assert.Equal(1, _clientFactory.Calls.Count(c => c == "planner-step"));
        Assert.Empty(_clientFactory.Step2PlannerPrompts);
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task WebTask_NewsArticlePrompt_DumpShortCircuit_ZeroAssessmentCallsAndNoFollowUp()
    {
        // The news-article field failure: "Fetch a recent AI news article and create a text
        // file on the desktop…" was NOT classified as a dump task (TaskHintsWebNeed missed
        // the "news" phrasing), so the run did web_search → _web_fetch of a bot-walled site
        // instead of the RSS digest → straight-to-file dump. With the fix the prompt IS a
        // dump task: the _web_search routes to the RSS digest, the digest auto-dumps to the
        // demanded file IN that search step, and the deterministic dump short-circuit
        // completes the run — ZERO completion-assessment LLM calls, ZERO follow-up steps.
        _clientFactory.Mode = PlannerMode.NewsDigestWrite;
        // Deliberately leave WebAssessComplete null — the dump-task short-circuit (not the
        // scripted assessor) must complete the run; any "web-assess" call proves it fired.
        var controller = BuildController();
        var prompt = $"Fetch a recent AI news article and create a text file with the data on my desktop at \"{_newsTarget}\"";
        Assert.True(AgentOsOutputVerifier.TryGetOsFileOutputDemand(prompt, out _),
            $"the test prompt must demand an OS output file: {prompt}");

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        Assert.True(File.Exists(_newsTarget), $"the eager dump should have written {_newsTarget}");
        var dumped = File.ReadAllText(_newsTarget, Encoding.UTF8);
        Assert.Contains("AlphaFold 3 predicts protein structures with atom-level accuracy", dumped);

        // ZERO completion-assessment LLM calls — the dump-task short-circuit replaced the
        // assessment, so the planner never got a chance to re-open the case.
        Assert.DoesNotContain(_clientFactory.Calls, c => c is "web-assess" or "assess");

        // ZERO follow-up steps: exactly ONE planner turn, the plan is just the search, and
        // no fetch/_command/_create_file step was ever proposed after it.
        Assert.Equal(1, _clientFactory.Calls.Count(c => c == "planner-step"));
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_web_search" }, plan!.Plan.Select(s => s.File).ToArray());
        Assert.Empty(_clientFactory.Step2PlannerPrompts);

        // The dump is recorded as a created OS-file result at the demanded path.
        var dumpResult = allSteps.OfType<Dictionary<string, object?>>()
            .Single(r => r.GetValueOrDefault("type")?.ToString() == "create" &&
                         r.GetValueOrDefault("os") is true);
        Assert.Equal(_newsTarget, dumpResult.GetValueOrDefault("path")?.ToString());

        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task WebTask_HallucinatedUrlCommand_RejectedAndSteeredToWebFetchVerbatimUrl()
    {
        _clientFactory.Mode = PlannerMode.CommandFetchSteer;
        // Incomplete until the demanded file exists — keeps the run going until the steered
        // _web_fetch + eager dump lands.
        _clientFactory.WebAssessComplete = () => File.Exists(_dumpTarget);
        var controller = BuildController();
        var prompt = $"Search the web for recent AI breakthroughs and write the data into a text file at \"{_dumpTarget}\"";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        // The run completed AND the demanded file exists (the eager dump after the fetch).
        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        Assert.True(File.Exists(_dumpTarget), $"the eager dump should have written {_dumpTarget}");
        Assert.Contains("AlphaFold 3", File.ReadAllText(_dumpTarget, Encoding.UTF8));

        // The hallucinated Invoke-RestMethod _command NEVER executed — no _command step
        // result exists and the invented domain was never hit over HTTP.
        Assert.DoesNotContain(allSteps.OfType<Dictionary<string, object?>>(),
            r => r.GetValueOrDefault("type")?.ToString() is "command" or "_command");
        Assert.DoesNotContain(_clientFactory.FetchedUrls,
            u => u.Contains("www.example.com", StringComparison.OrdinalIgnoreCase));

        // The rejection feedback reached a LATER planner turn, naming the invented URL and
        // demanding a verbatim result URL via _web_fetch.
        Assert.True(_clientFactory.PlannerUserPrompts.Skip(1).Any(p =>
                p.Contains("www.example.com/latest-ai-breakthrough", StringComparison.Ordinal) &&
                p.Contains("verbatim", StringComparison.Ordinal) &&
                p.Contains("_web_fetch", StringComparison.Ordinal) &&
                p.Contains("NOT among the harvested search results", StringComparison.Ordinal)),
            $"the fetch-in-command feedback should have reached a later planner prompt:\n{string.Join("\n---\n", _clientFactory.PlannerUserPrompts.Skip(1))}");

        // The FINAL plan is search → fetch of the REAL URL from the results; no _command.
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_web_search", "_web_fetch" }, plan!.Plan.Select(s => s.File).ToArray());
        Assert.Equal("https://example.com/alphafold3", plan.Plan[1].Change);
        Assert.DoesNotContain(plan.Plan, s => s.File == "_command");

        // ZERO repo edits (the OS-file auto-dump result carries os=true and is not a repo edit).
        var editResults = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("os") is not true &&
                        r.GetValueOrDefault("type")?.ToString() is "edit" or "create" &&
                        r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
            .ToList();
        Assert.Empty(editResults);
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task WebTask_RepoRelativeDemand_FolderCreatedBeforeFetch_DumpWritesRealData()
    {
        // The benchmark-task shape: the task demands a folder + a data file at the PROJECT
        // ROOT ("create a folder called benchmark_test_16 … pokemon_data.csv") AND a live
        // fetch. Two things made this brittle before: (1) the missing-web-search guard
        // rejected the mkdir/_create_directory proposals 3× before auto-injecting the search
        // ("what's giving this a hard time?" — the planner could not even create the demanded
        // folder), and (2) the eager dump only knew OS paths — a repo-relative demand was
        // invisible, so nothing pre-created the destination and the fetched data had nowhere
        // deterministic to land. Now: _create_directory is allowed as filesystem prep, and
        // the web step pre-creates the demanded folder BEFORE the fetch, so the fetch's
        // eager dump writes the real data into it. And because this is a DUMP task, the run
        // completes DETERMINISTICALLY the moment the dump lands — the scripted redundant
        // _create_file is never proposed, so the fetched data never round-trips through the
        // planner/LLM (the "re-emit 1025 rows inline" failure).
        _clientFactory.Mode = PlannerMode.RepoRelativeDump;
        // The between-steps assessment is scripted to NEVER complete — but the dump-task
        // short-circuit must complete the run anyway (the demanded file already exists with
        // real data), WITHOUT consulting this scripted assessor for the fetch step.
        _clientFactory.WebAssessComplete = () => false;
        var controller = BuildController();
        var folderPath = Path.Combine(_projectRoot, "benchmark_test_16");
        var csvPath = Path.Combine(folderPath, "pokemon_data.csv");
        var prompt = "Create a folder called 'benchmark_test_16' at the project root. Inside it, create a file called 'pokemon_data.csv'. " +
                     "Search the web to find the PokeAPI endpoint, then fetch the live Pokemon data (id numbers, stats and types) and write the data into pokemon_data.csv.";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        // The folder exists and the demanded file exists with the REAL fetched data — the
        // web step pre-created the destination before the fetch and the eager dump filled it.
        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        Assert.True(Directory.Exists(folderPath), $"the demanded folder should exist at {folderPath}");
        Assert.True(File.Exists(csvPath), $"the demanded file should exist at {csvPath}");
        var dumped = File.ReadAllText(csvPath, Encoding.UTF8);
        Assert.Contains("### WEB RESULTS", dumped);   // structured dump section
        Assert.Contains("AlphaFold 3", dumped);       // the real fetched content

        // The plan LED with the folder (filesystem prep allowed BEFORE the web steps — the
        // missing-web-search guard no longer bounces _create_directory), then search → fetch.
        // The dump-task short-circuit completed the run the moment the fetch's eager dump wrote
        // the demanded file, so the scripted redundant _create_file was NEVER proposed — the
        // final plan is the 3 real steps, and no LLM re-emitted the data.
        Assert.NotNull(plan);
        Assert.True(plan!.Plan.Count == 3,
            $"plan={string.Join(",", plan.Plan.Select(s => s.File + ":" + s.Change))}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched=[{string.Join(";", _clientFactory.Unmatched)}]");
        Assert.Equal(new[] { "_create_directory", "_web_search", "_web_fetch" }, plan.Plan.Select(s => s.File).ToArray());
        Assert.Equal("benchmark_test_16", plan.Plan[0].Change);

        // The eager dump result is a REPO edit (os != true — the file lives inside the
        // project root, unlike a desktop/absolute-path OS dump) carrying the demanded path.
        var dumpResult = allSteps.OfType<Dictionary<string, object?>>()
            .Single(r => r.GetValueOrDefault("type")?.ToString() == "create" &&
                         r.GetValueOrDefault("os") is not true &&
                         string.Equals(r.GetValueOrDefault("path")?.ToString(),
                             csvPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("created", dumpResult.GetValueOrDefault("status")?.ToString());

        // NO redundant _create_file (the planner's n==4 turn is never reached) and no
        // "file already created earlier in this run" skip result — the short-circuit completed
        // the run first. The only create results are the demanded folder and the dump itself.
        var createFileResults = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("type")?.ToString() == "create" &&
                        string.Equals(r.GetValueOrDefault("reason")?.ToString(),
                            "file already created earlier in this run", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(createFileResults);

        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task WebTask_DumpTask_FileWritten_CompletesWithZeroAssessmentCalls()
    {
        // The "1025-row inline re-emit" regression: after the fetch's eager dump writes the
        // demanded file, the dump-task short-circuit must complete the run DETERMINISTICALLY —
        // the completion-assessment LLM ("web-assess") is NEVER called, so the planner can never
        // re-emit the whole dataset inline as a _create_file. Step 1 is a DIRECT _web_fetch (a
        // web step, so the missing-web-search guard admits it without forcing a search first),
        // and the dump writes the demanded file in that same step.
        _clientFactory.Mode = PlannerMode.FetchFirstDump;
        var controller = BuildController();
        var prompt = $"Fetch the live data from the API and write the data into a text file at \"{_dumpTarget}\".";
        Assert.True(AgentOsOutputVerifier.TryGetOsFileOutputDemand(prompt, out _),
            $"the test prompt must demand an OS output file: {prompt}");

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        Assert.True(File.Exists(_dumpTarget), $"the eager dump should have written {_dumpTarget}");
        Assert.Contains("AlphaFold 3", File.ReadAllText(_dumpTarget, Encoding.UTF8));

        // THE regression: zero completion-assessment LLM calls. The dump-task short-circuit
        // replaced the assessment — no "web-assess", no "assess", no "post-verify"-style
        // completion round that could reopen the case and make the planner re-emit the data.
        Assert.DoesNotContain(_clientFactory.Calls, c => c is "web-assess" or "assess");

        // And no re-emit: exactly ONE planner turn (the fetch), the plan is just that fetch,
        // and no _create_file/edit step was ever proposed.
        Assert.Equal(1, _clientFactory.Calls.Count(c => c == "planner-step"));
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_web_fetch" }, plan!.Plan.Select(s => s.File).ToArray());
        var createFileSteps = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("type")?.ToString() == "_create_file" ||
                        (r.GetValueOrDefault("type")?.ToString() == "create" &&
                         r.GetValueOrDefault("os") is not true &&
                         string.Equals(r.GetValueOrDefault("reason")?.ToString(),
                             "file already created earlier in this run", StringComparison.Ordinal)))
            .ToList();
        Assert.Empty(createFileSteps);

        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task WebTask_Benchmark21_FetchThenCsvEdits_DumpBadge_TabularFastPath_NoCorrectiveSteps()
    {
        // The live benchmark-21 shape run through the REAL interleaved pipeline: the prompt
        // is the actual benchmark plan description (from BenchmarkService), the planner is
        // scripted to propose the exact step sequence (folder → search → fetch → 8 tabular
        // CSV edits), and the fake HTTP answers the PokeAPI list with list-shaped JSON so
        // the fetch's eager dump writes a CLEAN id,name,url CSV the tabular editor can edit.
        //
        // This run must prove four things end-to-end:
        //  1. DUMP BADGE — the task classifies as a "dump" (web need + demanded file) and the
        //     badge lands on the card BEFORE planning.
        //  2. NO PREMATURE SHORT-CIRCUIT — despite the eager dump writing the file in the
        //     fetch step, the run does NOT complete there (the task also demands structured
        //     edits, so IsPureDumpTask is false) — the between-steps assessment keeps the run
        //     going until the edits land.
        //  3. TABULAR FAST-PATH — every edit step is claimed by the tabular fast-path with
        //     zero LLM (each result carries tabular:true), so the CSV is edited structurally.
        //  4. NO CORRECTIVE STEPS — the plan is exactly the 11 scripted steps, the last
        //     planner turn completes the plan, and the final CSV passes every benchmark-21
        //     acceptance check.
        const string cardId = "benchmark-21-card";
        await _boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));
        _clientFactory.Mode = PlannerMode.FetchThenCsvEdits;
        var csvPath = Path.Combine(_projectRoot, "benchmark_test_21", "pokemon_data.csv");
        // Incomplete until every edit has landed: the between-steps assessment after the
        // search/fetch steps must keep the run going; only the fully-edited CSV (mass edit
        // filled the original rows' empty type cells and the per-row edits overwrote the
        // custom rows' 'unknown' placeholder) satisfies it.
        _clientFactory.WebAssessComplete = () =>
        {
            if (!File.Exists(csvPath)) return false;
            var content = File.ReadAllText(csvPath, Encoding.UTF8);
            return content.Contains("electric", StringComparison.Ordinal) &&
                   content.Contains("ghost", StringComparison.Ordinal) &&
                   content.Contains("fairy", StringComparison.Ordinal) &&
                   !content.Contains("unknown", StringComparison.Ordinal);
        };
        var controller = BuildController();
        var prompt = BenchmarkService.GetBenchmarkPlans().First(p => p.Level == 21).Description;

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt, cardId);

        // 1. The dump badge landed on the card.
        var raw = await _boardData.LoadRawAsync();
        Assert.Equal("dump", ReadCardProperty(raw!, cardId, "_taskKind"));

        // 2 + 4. The run completed with EXACTLY the scripted plan — the fetch's eager dump
        // did NOT short-circuit it, and no corrective step was ever proposed.
        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        Assert.NotNull(plan);
        var planFiles = plan!.Plan.Select(s => s.File).ToArray();
        Assert.Equal(11, planFiles.Length); // the 11 scripted steps — no corrective steps added
        Assert.Equal(new[]
        {
            "_create_directory", "_web_search", "_web_fetch",
            "benchmark_test_21/pokemon_data.csv", "benchmark_test_21/pokemon_data.csv", "benchmark_test_21/pokemon_data.csv",
            "benchmark_test_21/pokemon_data.csv", "benchmark_test_21/pokemon_data.csv", "benchmark_test_21/pokemon_data.csv",
            "benchmark_test_21/pokemon_data.csv", "benchmark_test_21/pokemon_data.csv"
        }, planFiles);
        // 11 step turns + 1 planComplete turn — no corrective step was ever proposed.
        Assert.Equal(12, _clientFactory.Calls.Count(c => c == "planner-step"));
        // The between-steps completion assessment ran after the two web steps (both
        // incomplete — the fetch did NOT short-circuit the dump+edit hybrid), plus the
        // planComplete re-check once every edit had landed.
        Assert.Equal(3, _clientFactory.Calls.Count(c => c == "web-assess"));

        // 3. Every edit step was claimed by the TABULAR fast-path (zero-LLM structural edit).
        var editResults = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("type")?.ToString() == "edit")
            .ToList();
        Assert.Equal(8, editResults.Count);
        Assert.All(editResults, r => Assert.True(
            Assert.IsType<bool>(r.GetValueOrDefault("tabular")),
            $"expected tabular flag on edit result — {r.GetValueOrDefault("reason")}"));
        Assert.All(editResults, r => Assert.Equal("done", r.GetValueOrDefault("status")?.ToString()));

        // The final CSV satisfies every benchmark-21 acceptance check (mass edit replaced the
        // placeholder; the per-row edits applied; the new rows landed).
        var final = File.ReadAllText(csvPath, Encoding.UTF8);
        Assert.StartsWith("FETCHED_AT:", final);
        Assert.Contains("type", final);
        Assert.Contains("weavmon", final);
        Assert.Contains("kanbanite", final);
        Assert.Contains("bugcatcher", final);
        Assert.Contains("normal", final);
        Assert.Contains("electric", final);
        Assert.Contains("ghost", final);
        Assert.Contains("fairy", final);
        Assert.DoesNotContain("unknown", final);

        // The real benchmark evaluation: every acceptance check passes → status "completed".
        var score = await new BenchmarkService(_db).EvaluateAsync(
            21, _projectRoot, successfulEdits: 8, failedEdits: 0, stepCount: 11,
            durationMs: 0, modelUsed: "scripted-test");
        Assert.Equal("completed", score.Status);
        Assert.Empty(score.FailedSteps);

        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task WebTask_MkdirCommand_AcceptedAsFilesystemPrep_NoDirectoryCreationBounce()
    {
        // The "failed over and over to create a directory" field report: the planner's FIRST
        // step is a _command mkdir on a web-needing task. Before the fix, the missing-web-search
        // guard rejected it (and a _create_file mislabel) up to 3× with generic web feedback
        // before the planner stumbled onto _create_directory. Now mkdir-style _command steps are
        // filesystem PREP like _create_directory: accepted on round 1, executed for real, and the
        // folder exists before any web step runs.
        _clientFactory.Mode = PlannerMode.MkdirCommandPrep;
        _clientFactory.WebAssessComplete = () => File.Exists(_csvPath);
        var controller = BuildController();
        var prompt = "Create a folder called 'benchmark_test_16' at the project root. Inside it, create a file called 'pokemon_data.csv'. " +
                     "Search the web to find the PokeAPI endpoint, then fetch the live Pokemon data (id numbers, stats and types) and write the data into pokemon_data.csv.";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        // Regression lock: the mkdir _command ENTERED the plan at index 0 and EXECUTED — the old
        // guard rejected it, so it never appeared in the plan (the run fell back to the
        // web-step pre-create instead). The folder is on disk before any web step.
        Assert.NotNull(plan);
        Assert.Equal("_command", plan!.Plan[0].File);
        Assert.Contains("mkdir", plan.Plan[0].Change, StringComparison.OrdinalIgnoreCase);
        var mkdirResult = Assert.Single(allSteps.OfType<Dictionary<string, object?>>(),
            r => r.GetValueOrDefault("type")?.ToString() == "command" &&
                 r.GetValueOrDefault("status")?.ToString() == "done");
        Assert.Contains("mkdir", mkdirResult.GetValueOrDefault("command")?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(_folderPath), $"the mkdir _command must have created {_folderPath}");
        Assert.True(File.Exists(_csvPath), $"the demanded file should exist at {_csvPath}");
        Assert.Contains("### WEB RESULTS", File.ReadAllText(_csvPath, Encoding.UTF8));
        Assert.Equal(new[] { "_command", "_web_search", "_web_fetch" }, plan.Plan.Select(s => s.File).ToArray());
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task WebTask_PathlessCreateFile_ScopedIntoMkdirCreatedFolder()
    {
        // The benchmark-22 shape that landed server.js at the SANDBOX ROOT: the planner mkdir's
        // the folder with a _command step, then plans a PATHLESS _create_file ("Create server.js
        // backend implementation…") whose change is a prose description, not the full path. The
        // interleaved route skips the classic mkdir→_create_directory conversion (that happens in
        // ValidatePlanAsync, whole-plan only), so the pathless scoping must recognize the mkdir
        // _command result as the implied directory and land the file INSIDE benchmark_test_22/ —
        // never at the project root.
        _clientFactory.Mode = PlannerMode.MkdirThenPathlessCreateFile;
        var controller = BuildController();
        var targetFolder = Path.Combine(_projectRoot, "benchmark_test_22");
        var prompt = "Create a folder called 'benchmark_test_22' at the project root. Inside it, create a server.js " +
                     "that serves index.html at / and exposes a GET /api/health endpoint returning {\"status\": \"ok\"}.";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        Assert.True(Directory.Exists(targetFolder), $"the mkdir step must create {targetFolder}");
        // The pathless _create_file was scoped into the folder: it created benchmark_test_22/server.js…
        var createResult = Assert.Single(allSteps.OfType<Dictionary<string, object?>>(),
            r => r.GetValueOrDefault("type")?.ToString() == "create" && r.GetValueOrDefault("status")?.ToString() is "done" or "created");
        Assert.Equal("benchmark_test_22/server.js", createResult.GetValueOrDefault("path")?.ToString());
        Assert.True(File.Exists(Path.Combine(targetFolder, "server.js")), "server.js must land inside benchmark_test_22/");
        // …and NOT a root-level server.js (the pre-fix behavior).
        Assert.False(File.Exists(Path.Combine(_projectRoot, "server.js")), "no root-level server.js may exist");
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task WebTask_FailedCommand_IsReportedAndRecoveredWithACorrectedCommand()
    {
        // The benchmark-22 deadlock: `node server.js` crashed with a SyntaxError but the step
        // was reported "done" — no recovery edit was ever planned and the planner deadlocked
        // re-proposing the same broken command until the slot-failure valve threw. The fix:
        // (a) a _command whose output shows a hard failure is reported status=error (never
        // done), and (b) the interleaved loop feeds the failure output back to the planner,
        // which plans a recovery EDIT of the crashing file.
        _clientFactory.Mode = PlannerMode.BrokenCommandRecovery;
        var controller = BuildController();
        // Edit intent ("fix") so discovery does NOT short-circuit into the test-intent web
        // test — the run must go through the interleaved planner where the command runs.
        var prompt = "Fix the server so `node server.js` starts without errors.";
        Assert.True(TestIntentClassifier.HasEditIntent(prompt), "the test prompt must route through planning");

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        // The failing command is REPORTED as error — pre-fix it was "done".
        var failedCmd = Assert.Single(allSteps.OfType<Dictionary<string, object?>>(),
            r => r.GetValueOrDefault("type")?.ToString() == "command" &&
                 (r.GetValueOrDefault("command")?.ToString() ?? "").Contains("node benchmark_test_22/server.js") &&
                 r.GetValueOrDefault("status")?.ToString() == "error");
        Assert.Contains("boom", failedCmd.GetValueOrDefault("error")?.ToString() ?? "", StringComparison.Ordinal);
        // The recovery EDIT landed — the crashing line was replaced.
        var serverPath = Path.Combine(_projectRoot, "benchmark_test_22", "server.js");
        Assert.True(File.Exists(serverPath), "server.js must exist after the create step");
        Assert.Contains("server.listen(process.env.PORT || 8765);", File.ReadAllText(serverPath, Encoding.UTF8));
        Assert.DoesNotContain("throw new Error(\"boom\");", File.ReadAllText(serverPath, Encoding.UTF8));
        // The planner SAW the failure feedback (the error output + the recovery demand).
        Assert.Contains(_clientFactory.PlannerUserPrompts, p => p.Contains("FAILED", StringComparison.OrdinalIgnoreCase));
        // The run did NOT die with the "3 slots in a row" exception — it recovered.
        Assert.Contains(_clientFactory.Calls, c => c == "planner-step");
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task WebTask_ServerStart_PortInUse_RecoversOnFreePort_NoPlannerRound()
    {
        // The benchmark-22 live failure: `node server.js` defaulting to 8765 while another
        // process holds the port → EADDRINUSE. Instead of failing the step and feeding it
        // back to the planner for a recovery round, the system must recover DETERMINISTICALLY
        // on a free port (zero thinking, zero new plan steps), mark the step done, and carry
        // the resolved URL forward in context so the next connection/navigation targets the
        // port that actually answered.
        _clientFactory.Mode = PlannerMode.EaddrInUseRecovery;
        var controller = BuildController();
        var prompt = "Create a folder called 'benchmark_test_22' at the project root. Write a Node.js server " +
                     "in it that reads the PORT environment variable (default 8765) and serves any request, " +
                     "then start the server.";
        // The harness terminal inherits THIS session's environment, where PORT may be set
        // (e.g. PORT=0 on this host) — `process.env.PORT || 8765` would then bind port 0 and
        // never hit the port conflict. Clear it so the server genuinely defaults to 8765 and
        // the EADDRINUSE failure is real (mirroring the user's environment, where PORT is
        // unset).
        var setupTermField = typeof(AgentController).GetField("_terminal", BindingFlags.NonPublic | BindingFlags.Instance);
        if (setupTermField?.GetValue(controller) is TerminalService setupTerminal)
        {
            setupTerminal.Start();
            await setupTerminal.WriteStdinAsync("Remove-Item Env:PORT -Force -ErrorAction SilentlyContinue");
        }

        // Hold port 8765 so the server's default port is genuinely taken. Bind EXACTLY like
        // node does — IPv6-any with dual-mode (`::` + v4-mapped) — because an IPv4-only
        // loopback bind does NOT conflict with node's dual-stack listen on Windows. If 8765
        // is ALREADY in use the bind throws — which is fine, the port is busy either way and
        // the EADDRINUSE failure is deterministic.
        System.Net.Sockets.TcpListener? holder = null;
        int? recoveredPort = null;
        try
        {
            holder = new System.Net.Sockets.TcpListener(System.Net.IPAddress.IPv6Any, 8765);
            holder.Server.DualMode = true; // node's default: `::` with IPV6_V6ONLY=0
            holder.Start();
        }
        catch (System.Net.Sockets.SocketException) { /* already in use — even better */ }
        try
        {
            var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);
            Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
            // The server-start step did NOT error — it was recovered deterministically. The
            // recovered re-run carries the PORT injection (the retry command).
            var serverStep = Assert.Single(allSteps.OfType<Dictionary<string, object?>>(),
                r => r.GetValueOrDefault("type")?.ToString() == "command" &&
                     (r.GetValueOrDefault("command")?.ToString() ?? "").Contains("node benchmark_test_22/server.js"));
            Assert.Equal("done", serverStep.GetValueOrDefault("status")?.ToString());
            Assert.True(serverStep.GetValueOrDefault("portRecovered") is true,
                "the step must be flagged as port-recovered — step: " + JsonSerializer.Serialize(serverStep));
            var port = Assert.IsType<int>(serverStep.GetValueOrDefault("serverPort"));
            Assert.NotEqual(8765, port);
            Assert.Equal($"http://localhost:{port}/", serverStep.GetValueOrDefault("serverUrl")?.ToString());
            recoveredPort = port;
            // The resolved port was carried FORWARD in the discovery context: the final
            // planner prompt (round 4, the planComplete turn) names the running server URL.
            Assert.Contains(_clientFactory.PlannerUserPrompts, p =>
                p.Contains("### RUNNING SERVER ###", StringComparison.Ordinal) &&
                p.Contains($"http://localhost:{port}/", StringComparison.Ordinal));
            // No planner-round feedback happened — the step never errored.
            Assert.DoesNotContain(_clientFactory.PlannerUserPrompts, p =>
                p.Contains("The _command step", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(_clientFactory.Unmatched);
        }
        finally
        {
            holder?.Stop();
            // The recovered server runs inside the harness terminal (blocking that shell's
            // stdin, so the kill CANNOT go through the terminal) — kill it OUT-OF-BAND by the
            // exact port the recovery bound, never a broad process kill. Best-effort: on
            // non-Windows hosts this is a no-op and the CI runner is ephemeral anyway.
            if (recoveredPort.HasValue && OperatingSystem.IsWindows())
            {
                try
                {
                    using var killer = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "powershell",
                        $"-NoProfile -Command \"$c = Get-NetTCPConnection -LocalPort {recoveredPort.Value} -State Listen -ErrorAction SilentlyContinue; " +
                        $"if ($c) {{ Stop-Process -Id $c.OwningProcess -Force }}\"") { CreateNoWindow = true, UseShellExecute = false });
                    killer?.WaitForExit(15000);
                }
                catch { /* cleanup best-effort */ }
            }
        }
    }

    [Fact]
    public async Task WebTask_ServerStart_MissingPort_PatchedAndRecovered_NoPlannerRound()
    {
        // The benchmark-22 live failure AFTER the planner's recovery edit: the edit dropped
        // the `const PORT` line, so `node server.js` crashed with "ReferenceError: PORT is
        // not defined". The error message itself names the missing parameter — the command
        // pipeline must define it in the script (const PORT = process.env.PORT || 8765;),
        // re-run, and when the re-run hits the held default port, apply the free-port fix —
        // all deterministic, zero thinking, zero new plan steps, and the resolved URL must
        // ride forward in context so the browser test targets the port that answered.
        _clientFactory.Mode = PlannerMode.MissingPortRecovery;
        var controller = BuildController();
        var prompt = "Create a folder called 'benchmark_test_22' at the project root. Write a Node.js server " +
                     "in it that reads the PORT environment variable (default 8765) and serves any request, " +
                     "then start the server.";
        // The harness terminal inherits THIS session's environment, where PORT may be set
        // (e.g. PORT=0 on this host) — clear it so the server genuinely defaults to 8765 and
        // the failure sequence is real (missing PORT first, then EADDRINUSE on 8765).
        var setupTermField = typeof(AgentController).GetField("_terminal", BindingFlags.NonPublic | BindingFlags.Instance);
        if (setupTermField?.GetValue(controller) is TerminalService setupTerminal)
        {
            setupTerminal.Start();
            await setupTerminal.WriteStdinAsync("Remove-Item Env:PORT -Force -ErrorAction SilentlyContinue");
        }

        // Hold port 8765 EXACTLY like node binds (IPv6-any dual-mode) so the post-patch
        // re-run deterministically hits EADDRINUSE and the free-port fix must kick in.
        System.Net.Sockets.TcpListener? holder = null;
        int? recoveredPort = null;
        try
        {
            holder = new System.Net.Sockets.TcpListener(System.Net.IPAddress.IPv6Any, 8765);
            holder.Server.DualMode = true;
            holder.Start();
        }
        catch (System.Net.Sockets.SocketException) { /* already in use — even better */ }
        try
        {
            var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);
            Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
            // The server-start step did NOT error — the missing-PORT patch landed IN THE
            // SCRIPT, the re-run hit the held 8765, and the free-port recovery completed it.
            var serverStep = Assert.Single(allSteps.OfType<Dictionary<string, object?>>(),
                r => r.GetValueOrDefault("type")?.ToString() == "command" &&
                     (r.GetValueOrDefault("command")?.ToString() ?? "").Contains("node benchmark_test_22/server.js"));
            Assert.Equal("done", serverStep.GetValueOrDefault("status")?.ToString());
            Assert.True(serverStep.GetValueOrDefault("portRecovered") is true,
                "the step must be flagged as port-recovered — step: " + JsonSerializer.Serialize(serverStep));
            var port = Assert.IsType<int>(serverStep.GetValueOrDefault("serverPort"));
            Assert.NotEqual(8765, port);
            Assert.Equal($"http://localhost:{port}/", serverStep.GetValueOrDefault("serverUrl")?.ToString());
            recoveredPort = port;
            // The deterministic patch is IN the script — the planner never planned it.
            var serverPath = Path.Combine(_projectRoot, "benchmark_test_22", "server.js");
            Assert.True(File.Exists(serverPath), "server.js must exist after the create step");
            Assert.Contains("const PORT = process.env.PORT || 8765;", File.ReadAllText(serverPath, Encoding.UTF8));
            // The resolved port was carried FORWARD in the discovery context (the final
            // planComplete turn names the running server URL).
            Assert.Contains(_clientFactory.PlannerUserPrompts, p =>
                p.Contains("### RUNNING SERVER ###", StringComparison.Ordinal) &&
                p.Contains($"http://localhost:{port}/", StringComparison.Ordinal));
            // No planner-round feedback happened — the step never errored, no recovery step
            // was ever planned.
            Assert.DoesNotContain(_clientFactory.PlannerUserPrompts, p =>
                p.Contains("The _command step", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(_clientFactory.Unmatched);
        }
        finally
        {
            holder?.Stop();
            // The recovered server runs inside the harness terminal (blocking that shell's
            // stdin, so the kill CANNOT go through the terminal) — kill it OUT-OF-BAND by the
            // exact port the recovery bound, never a broad process kill.
            if (recoveredPort.HasValue && OperatingSystem.IsWindows())
            {
                try
                {
                    using var killer = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "powershell",
                        $"-NoProfile -Command \"$c = Get-NetTCPConnection -LocalPort {recoveredPort.Value} -State Listen -ErrorAction SilentlyContinue; " +
                        $"if ($c) {{ Stop-Process -Id $c.OwningProcess -Force }}\"") { CreateNoWindow = true, UseShellExecute = false });
                    killer?.WaitForExit(15000);
                }
                catch { /* cleanup best-effort */ }
            }
        }
    }

    [Fact]
    public async Task WebTask_CreateFileAsDirectory_RejectedWithCreateDirectorySteering()
    {
        // The same bounce from the other side: round 1 reaches for _create_file to make a
        // FOLDER. The missing-web-search gate must reject with TARGETED steering
        // ("FOLDER/DIRECTORY … _create_directory") instead of the generic web feedback, so
        // round 2 lands _create_directory instead of burning another mislabeled attempt.
        _clientFactory.Mode = PlannerMode.CreateFileAsDirectory;
        _clientFactory.WebAssessComplete = () => File.Exists(_csvPath);
        var controller = BuildController();
        var prompt = "Create a folder called 'benchmark_test_16' at the project root. Inside it, create a file called 'pokemon_data.csv'. " +
                     "Search the web to find the PokeAPI endpoint, then fetch the live Pokemon data (id numbers, stats and types) and write the data into pokemon_data.csv.";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        // The round-1 rejection's steering reached the round-2 planner turn (PlannerUserPrompts
        // records every planner user prompt, 0-based): it must name the FOLDER mislabel and
        // _create_directory — the generic web feedback would not have.
        Assert.True(_clientFactory.PlannerUserPrompts.Count >= 2,
            $"expected at least 2 planner turns — got {_clientFactory.PlannerUserPrompts.Count}; calls=[{string.Join(",", _clientFactory.Calls)}]");
        var round2Prompt = _clientFactory.PlannerUserPrompts[1];
        Assert.Contains("FOLDER/DIRECTORY", round2Prompt);
        Assert.Contains("_create_directory", round2Prompt);
        // Round 2 landed _create_directory at the head of the plan and the folder exists.
        Assert.NotNull(plan);
        Assert.Equal("_create_directory", plan!.Plan[0].File);
        Assert.Equal("benchmark_test_16", plan.Plan[0].Change);
        Assert.True(Directory.Exists(_folderPath), $"the demanded folder should exist at {_folderPath}");
        Assert.True(File.Exists(_csvPath), $"the demanded file should exist at {_csvPath}");
        Assert.Contains("### WEB RESULTS", File.ReadAllText(_csvPath, Encoding.UTF8));
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task WebTask_ScraperScriptRejected_SteeredToWebFetch_ScraperNeverLands()
    {
        // The "wrote a Python app to do a fetch" drift: round 1 plans a _create_file for a
        // scraper script (requests.get + open(...,"w")) on a web-needing task — the planner
        // forgot the _web_fetch step tool exists. The missing-web-search gate must reject it
        // with SCRAPER steering (naming _web_fetch), NOT the generic web feedback, and the
        // scraper file must never reach disk.
        _clientFactory.Mode = PlannerMode.ScraperScriptRejected;
        _clientFactory.WebAssessComplete = () => File.Exists(_csvPath);
        var controller = BuildController();
        var prompt = "Create a folder called 'benchmark_test_16' at the project root. Inside it, create a file called 'pokemon_data.csv'. " +
                     "Search the web to find the PokeAPI endpoint, then fetch the live Pokemon data (id numbers, stats and types) and write the data into pokemon_data.csv.";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        // The round-1 rejection's steering reached the round-2 planner turn: it must name the
        // scraper and _web_fetch — the generic web feedback would not have.
        Assert.True(_clientFactory.PlannerUserPrompts.Count >= 2,
            $"expected at least 2 planner turns — got {_clientFactory.PlannerUserPrompts.Count}; calls=[{string.Join(",", _clientFactory.Calls)}]");
        var round2Prompt = _clientFactory.PlannerUserPrompts[1];
        Assert.Contains("SCRAPER/FETCH script", round2Prompt);
        Assert.Contains("_web_fetch", round2Prompt);
        // Round 2 landed the web chain; the plan is the two web steps (the web step
        // pre-creates the demanded folder before its eager dump).
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_web_search", "_web_fetch" }, plan!.Plan.Select(s => s.File).ToArray());
        Assert.True(File.Exists(_csvPath), $"the demanded file should exist at {_csvPath}");
        Assert.Contains("### WEB RESULTS", File.ReadAllText(_csvPath, Encoding.UTF8));
        // The scraper script never landed on disk.
        Assert.False(File.Exists(Path.Combine(_projectRoot, "benchmark_test_16", "fetch_pokemon.py")),
            "the rejected scraper script must never be written");
        Assert.False(File.Exists(Path.Combine(_projectRoot, "fetch_pokemon.py")),
            "the rejected scraper script must never be written at the root either");
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task WebTask_ScraperAfterWebSteps_RejectedByScraperFileGuard_NeverLands()
    {
        // The exact benchmark-run hole the missing-web-search gate can't see: search → fetch
        // (the eager dump writes the demanded CSV), THEN the planner still proposes the
        // Python scraper _create_file. The gate is silent once a web step exists, so the
        // SCRAPER-FILE GUARD in ValidateIncrementalStepAsync must reject it — the file must
        // never land, and round 4 must complete from the already-dumped data instead of
        // burning a "run the scraper" step.
        _clientFactory.Mode = PlannerMode.ScraperAfterWebSteps;
        _clientFactory.WebAssessComplete = () => File.Exists(_csvPath);
        var controller = BuildController();
        var prompt = "Create a folder called 'benchmark_test_16' at the project root. Inside it, create a file called 'pokemon_data.csv'. " +
                     "Search the web to find the PokeAPI endpoint, then fetch the live Pokemon data (id numbers, stats and types) and write the data into pokemon_data.csv.";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        // The round-2 rejection's steering reached the round-3 planner turn (PlannerUserPrompts
        // is 0-based per turn): it must name the scraper and _web_fetch.
        Assert.True(_clientFactory.PlannerUserPrompts.Count >= 3,
            $"expected at least 3 planner turns — got {_clientFactory.PlannerUserPrompts.Count}; calls=[{string.Join(",", _clientFactory.Calls)}]");
        var round3Prompt = _clientFactory.PlannerUserPrompts[2];
        Assert.Contains("SCRAPER/FETCH script", round3Prompt);
        Assert.Contains("_web_fetch", round3Prompt);
        // The plan is just the web chain — the scraper never entered it.
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_web_search", "_web_fetch" }, plan!.Plan.Select(s => s.File).ToArray());
        // The demanded file was written by the web step's eager dump, and the scraper never
        // landed on disk.
        Assert.True(File.Exists(_csvPath), $"the demanded file should exist at {_csvPath}");
        Assert.Contains("### WEB RESULTS", File.ReadAllText(_csvPath, Encoding.UTF8));
        Assert.False(File.Exists(Path.Combine(_projectRoot, "benchmark_test_16", "fetch_pokemon.py")),
            "the post-web scraper script must never be written");
        Assert.False(File.Exists(Path.Combine(_projectRoot, "fetch_pokemon.py")),
            "the post-web scraper script must never be written at the root either");
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task WebTask_RunScraperCommand_Rejected_SteeredToWebFetch()
    {
        // The "and then it ran the scraper" half: search commits, then the planner proposes
        // `_command python fetch_poke_data.py`. No such script exists (the create was
        // rejected), so the RUN-A-SCRAPER GUARD must reject it — previously this command
        // executed (and died with an IndentationError). Round 3 must fetch the real URL.
        _clientFactory.Mode = PlannerMode.RunScraperCommandRejected;
        _clientFactory.WebAssessComplete = () => File.Exists(_csvPath);
        var controller = BuildController();
        var prompt = "Create a folder called 'benchmark_test_16' at the project root. Inside it, create a file called 'pokemon_data.csv'. " +
                     "Search the web to find the PokeAPI endpoint, then fetch the live Pokemon data (id numbers, stats and types) and write the data into pokemon_data.csv.";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        // The round-2 rejection's steering reached the round-3 planner turn: it must name the
        // run and _web_fetch (and the _scraper fallback).
        Assert.True(_clientFactory.PlannerUserPrompts.Count >= 3,
            $"expected at least 3 planner turns — got {_clientFactory.PlannerUserPrompts.Count}; calls=[{string.Join(",", _clientFactory.Calls)}]");
        var round3Prompt = _clientFactory.PlannerUserPrompts[2];
        Assert.Contains("RUNS a scraper/fetch script", round3Prompt);
        Assert.Contains("_web_fetch", round3Prompt);
        // The plan is just the web chain — the run command never entered it, and the CSV was
        // written by the fetch's eager dump.
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_web_search", "_web_fetch" }, plan!.Plan.Select(s => s.File).ToArray());
        Assert.True(File.Exists(_csvPath), $"the demanded file should exist at {_csvPath}");
        Assert.Contains("### WEB RESULTS", File.ReadAllText(_csvPath, Encoding.UTF8));
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task WebTask_ScraperFallbackStep_SystemBuildsAndRunsKnownGoodScraper()
    {
        // The sanctioned fallback: when web steps keep failing, the planner plans a "_scraper"
        // step with the URL. The SYSTEM (not the LLM) builds + runs a known-good scraper via
        // the injected fake service, which writes the demanded CSV — no scraper code ever
        // lands as a repo file.
        _clientFactory.Mode = PlannerMode.ScraperStepFallback;
        _clientFactory.WebAssessComplete = () => File.Exists(_csvPath);
        var fake = new FakeScraperService();
        var controller = BuildController();
        SetField(controller, "_scraperService", fake);
        var prompt = "Create a folder called 'benchmark_test_16' at the project root. Inside it, create a file called 'pokemon_data.csv'. " +
                     "Search the web to find the PokeAPI endpoint, then fetch the live Pokemon data (id numbers, stats and types) and write the data into pokemon_data.csv.";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_web_search", "_scraper" }, plan!.Plan.Select(s => s.File).ToArray());
        // The system-built scraper ran against the URL and wrote the demanded file.
        Assert.Equal(new[] { "https://example.com/alphafold3" }, fake.Urls.ToArray());
        Assert.True(File.Exists(_csvPath), $"the demanded file should exist at {_csvPath}");
        Assert.Contains("scraper", File.ReadAllText(_csvPath, Encoding.UTF8));
        // No freehand scraper file ever landed.
        Assert.False(File.Exists(Path.Combine(_projectRoot, "benchmark_test_16", "fetch_pokemon.py")),
            "no freehand scraper script may land — the _scraper step owns the fallback");
        // The run recorded the scraper step result.
        var scraperResults = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("type")?.ToString() == "scraper")
            .ToList();
        Assert.Single(scraperResults);
        Assert.Equal("done", scraperResults[0].GetValueOrDefault("status")?.ToString());
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task WebTask_Replay_RebuildsDiscoveryContextFromPersistedWebResults()
    {
        const string cardId = "replay-web-results";
        await _boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));
        _clientFactory.Mode = PlannerMode.WebChain;
        _clientFactory.WebAssessComplete = () => File.Exists(_dumpTarget);
        var controller = BuildController();
        var prompt = $"Search the web for recent AI breakthroughs and write the data into a text file at \"{_dumpTarget}\"";

        // PHASE 1 — the original run: search + fetch + eager dump, all persisted to the card.
        var (phase1Steps, phase1Plan, complete1) = await InvokeOrchestrate(controller, prompt, cardId);
        Assert.True(complete1, $"phase 1 should complete — plan summary: {phase1Plan?.Summary}");
        Assert.True(File.Exists(_dumpTarget));
        Assert.Equal(new[] { "_web_search", "_web_fetch" }, phase1Plan!.Plan.Select(s => s.File).ToArray());

        // The card now carries the harvested web results (search + fetch outputs).
        var rawAfterPhase1 = await _boardData.LoadRawAsync();
        Assert.Contains("\"_webResults\"", rawAfterPhase1!);
        Assert.Contains("AlphaFold 3 predicts protein structures", rawAfterPhase1);

        // A restarted run loads the plan + the persisted web results together.
        var (loadedPlan, loadedCompleted, _, loadedWeb) = LoadPlan(controller, cardId);
        Assert.NotNull(loadedPlan);
        Assert.NotNull(loadedCompleted);
        Assert.True(loadedCompleted!.Count >= 2, $"both web steps should be done — {string.Join(",", loadedCompleted)}");
        Assert.NotNull(loadedWeb);
        Assert.Equal(2, loadedWeb!.Count); // the search digest AND the fetched body
        var loadedSearch = Assert.Single(loadedWeb, w => w.GetValueOrDefault("type")?.ToString() == "_web_search");
        Assert.Contains("https://example.com/alphafold3", loadedSearch.GetValueOrDefault("output")?.ToString() ?? "");
        var loadedFetch = Assert.Single(loadedWeb, w => w.GetValueOrDefault("type")?.ToString() == "_web_fetch");
        Assert.Equal("https://example.com/alphafold3", loadedFetch.GetValueOrDefault("url")?.ToString());

        // PHASE 2 — the replay: both steps are skipped (already done), but the run must NOT
        // start with an empty context — the persisted web data is seeded back in as replayed
        // done results, so the remaining steps (and the edit-resolution injection) see it.
        var (replaySteps, _, complete2) = await InvokeOrchestrate(controller, prompt, cardId,
            existingPlan: loadedPlan, completedStepIndices: loadedCompleted, webResults: loadedWeb);
        Assert.True(complete2, $"replay should complete — calls=[{string.Join(",", _clientFactory.Calls)}]");

        var replayed = replaySteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("replayed") is true)
            .ToList();
        Assert.Equal(2, replayed.Count);
        var replayedSearch = Assert.Single(replayed, r => r.GetValueOrDefault("type")?.ToString() == "_web_search");
        Assert.Contains("AlphaFold 3 predicts protein structures", replayedSearch.GetValueOrDefault("output")?.ToString() ?? "");
        Assert.Contains("https://example.com/alphafold3", replayedSearch.GetValueOrDefault("output")?.ToString() ?? "");
        var replayedFetch = Assert.Single(replayed, r => r.GetValueOrDefault("type")?.ToString() == "_web_fetch");
        Assert.Equal("https://example.com/alphafold3", replayedFetch.GetValueOrDefault("url")?.ToString());

        // The replay made no new LLM calls the script didn't account for.
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── Cross-tool state carry (the multi-turn eval extended to web values) ──────────────
    // Turn 1's state is a VALUE from a web search result (the article URL). Turn 2's tool
    // call must consume that EXACT value — a _web_fetch of the same URL (fetch arm) or an
    // edit that embeds it (edit arm). The carry is judged at EXECUTION: the executed fetch
    // hit exactly the harvested URL (never an invented domain), and the landed edit embeds
    // exactly the harvested URL — with a content-aware ground-truth assessor keeping the
    // run incomplete until the carried value actually lands.

    [Fact]
    public async Task WebTask_CrossToolCarry_SearchResultUrlFeedsTurn2Fetch_ExecutesExactUrl()
    {
        _clientFactory.Mode = PlannerMode.WebChain;
        // Computed ground truth: the task is satisfied only once a fetch EXECUTED against
        // the exact URL harvested from the search results — not merely planned, executed.
        _clientFactory.WebAssessComplete = () =>
            _clientFactory.FetchedUrls.Any(u => u.Contains("https://example.com/alphafold3", StringComparison.Ordinal));
        var controller = BuildController();
        // No OS-output demand — a pure search→fetch chain, so the OS-output gate adds no
        // auto-dump step and the plan is exactly the two web steps.
        var prompt = "Search the web for an interesting AI article, then fetch the best article's URL from the results";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        // Turn-1 state (the harvested search results) reached the turn-2 planner prompt,
        // carrying the exact URL the fetch must use.
        var step2Prompt = Assert.Single(_clientFactory.Step2PlannerPrompts);
        Assert.Contains("### WEB RESULTS", step2Prompt);
        Assert.Contains("https://example.com/alphafold3", step2Prompt);
        // Turn 2's tool call consumed the EXACT value from turn 1: the planned step, the
        // executed step result, and the actual HTTP GET all carry the same URL.
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_web_search", "_web_fetch" }, plan!.Plan.Select(s => s.File).ToArray());
        Assert.Equal("https://example.com/alphafold3", plan.Plan[1].Change);
        var doneFetch = Assert.Single(allSteps.OfType<Dictionary<string, object?>>(),
            r => r.GetValueOrDefault("type")?.ToString() == "_web_fetch" &&
                 r.GetValueOrDefault("status")?.ToString() == "done");
        Assert.Equal("https://example.com/alphafold3", doneFetch.GetValueOrDefault("url")?.ToString());
        Assert.Contains(_clientFactory.FetchedUrls, u => u.Contains("https://example.com/alphafold3", StringComparison.Ordinal));
        // The invented example-domain pattern was never fetched — only the carried URL.
        Assert.DoesNotContain(_clientFactory.FetchedUrls, u => u.Contains("www.example.com", StringComparison.Ordinal));
        // ZERO repo edits — the fetch consumed the value; nothing invented.
        var editResults = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("type")?.ToString() is "edit" or "create" &&
                        r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
            .ToList();
        Assert.Empty(editResults);
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task WebTask_CrossToolCarry_SearchResultUrlFeedsTurn2Edit_EmbeddedExactUrl()
    {
        _clientFactory.Mode = PlannerMode.SearchThenEdit;
        var linksPath = Path.Combine(_projectRoot, LinksRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(linksPath)!);
        File.WriteAllText(linksPath, "export const links: string[] = [];", Encoding.UTF8);
        // Computed ground truth: complete only once the landed edit embeds the EXACT URL
        // from the search results — an invented/wrong URL must never satisfy it.
        _clientFactory.WebAssessComplete = () => File.Exists(linksPath) &&
            File.ReadAllText(linksPath, Encoding.UTF8).Contains("https://example.com/alphafold3", StringComparison.Ordinal);
        var controller = BuildController();
        // "find … online" is an EXPLICIT web command (TaskExplicitlyCommandsWeb), so the
        // web-step gate lets the search through without a classifier call — the search then
        // produces the value (URL) turn 2's edit must consume.
        var prompt = "Find a recent AI article online and add a link to the best article into src/links.ts";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        // Turn-1 state (the harvested search results) reached the turn-2 planner prompt.
        var step2Prompt = Assert.Single(_clientFactory.Step2PlannerPrompts);
        Assert.Contains("### WEB RESULTS", step2Prompt);
        Assert.Contains("https://example.com/alphafold3", step2Prompt);
        // Turn 2's tool call was an EDIT whose newString embeds the EXACT harvested URL —
        // cross-tool carry (search tool → edit tool) — and it landed on disk.
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_web_search", LinksRel }, plan!.Plan.Select(s => s.File).ToArray());
        Assert.Contains("https://example.com/alphafold3", plan.Plan[1].NewString);
        var landed = File.ReadAllText(linksPath, Encoding.UTF8);
        Assert.Contains("export const links: string[] = ['https://example.com/alphafold3'];", landed);
        // No fetch was needed — the edit consumed the search value directly (no _web_fetch
        // step executed, no example.com GET beyond the search).
        Assert.DoesNotContain(allSteps.OfType<Dictionary<string, object?>>(),
            r => r.GetValueOrDefault("type")?.ToString() == "_web_fetch");
        Assert.DoesNotContain(_clientFactory.FetchedUrls, u => u.Contains("example.com/alphafold3", StringComparison.Ordinal));
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── Card shows the VERIFIER's verdict, not the overridden run flag ──────────────────
    // The benchmark-22 symptom: the run ENDED (agent stopped) but the verifier's final verdict
    // was INCOMPLETE — a completion fallback (phantom-issue skip / circuit breaker / "replanner
    // proposed no further steps") flipped the RUN's taskComplete=true, and the card persisted
    // that flipped flag: green "✅ Verified complete" with the verifier's incomplete reason
    // underneath. The card must persist the VERIFIER's verdict (yellow "⚠️ Verified
    // incomplete") even when the run itself completed via a fallback.

    [Fact]
    public async Task WebTask_VerifierIncomplete_RunEndedByFallback_CardPersistsIncompleteVerdict()
    {
        const string cardId = "verify-verdict";
        await _boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));
        _clientFactory.Mode = PlannerMode.SearchThenEdit;
        // The verifier's FINAL verdict is INCOMPLETE (with a concrete absence claim that
        // survives verifier-issue triage), while the run itself still ends — the planner
        // declares planComplete and the repair replanner has nothing to add.
        _clientFactory.PostVerifyComplete = false;
        var linksPath = Path.Combine(_projectRoot, LinksRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(linksPath)!);
        File.WriteAllText(linksPath, "export const links: string[] = [];", Encoding.UTF8);
        var controller = BuildController();
        // "find … online" is an EXPLICIT web command, so the web-step gate lets the search
        // through without a classifier call (same shape as the CrossToolCarry test).
        var prompt = "Find a recent AI article online and add a link to the best article into src/links.ts";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt, cardId);

        // 1) The RUN ended: the planner declared planComplete and the repair replanner had
        //    nothing further to propose (empty plan), so the run-level flag flipped to
        //    complete — exactly the "agent stopped" the user described.
        Assert.True(complete,
            $"run must end via the completion fallback — calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        Assert.NotNull(plan);
        // 2) The repair loop RAN (the replanner was consulted and proposed nothing) — the
        //    fallback that completed the run is the exhausted-with-no-steps override.
        Assert.NotEmpty(_clientFactory.RepairUserPrompts);
        // 3) The CARD must show the verifier's verdict: incomplete (yellow "⚠️ Verified
        //    incomplete"), NOT the run's flipped flag (green "✅ Verified complete").
        var raw = await _boardData.LoadRawAsync();
        var (vComplete, vReason) = ReadCardVerification(raw, cardId);
        Assert.False(vComplete, $"card verification must persist complete=false (the verifier's verdict), not the run flag — reason: {vReason}");
        Assert.NotNull(vReason);
        Assert.Contains("scripted verifier", vReason, StringComparison.OrdinalIgnoreCase);
        // 4) Every LLM call the run made was accounted for by the script.
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── Web-only completion assessment (no OS-output demand) ─────────────────────────────
    // The between-steps whole-task assessment previously NEVER ran for web-only runs:
    // IsLastEditVerifiedComplete requires an edit result, so a run whose last step was a
    // successful _web_search/_web_fetch skipped the assessment entirely. Now a successful
    // web step opens the same gate — a pure "search the web" task completes THROUGH the
    // assessment right after the search, instead of waiting for the planner to declare
    // planComplete on its own.

    [Fact]
    public async Task WebTask_SearchOnly_NoOutputDemand_AssessmentCompletesAfterSearch()
    {
        _clientFactory.Mode = PlannerMode.SearchOnly;
        _clientFactory.WebAssessComplete = () => true; // the gathered results satisfy the task
        var controller = BuildController();
        var prompt = "Search the web for recent AI breakthroughs and report what you find";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        // The run completed — and it completed THROUGH the between-steps web assessment: the
        // planner produced exactly ONE step (the search) and never had to declare completion
        // itself (no second planner turn, no Step 2).
        Assert.True(complete, $"run should complete via the web assessment — calls=[{string.Join(",", _clientFactory.Calls)}]; plan={plan?.Summary}");
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_web_search" }, plan!.Plan.Select(s => s.File).ToArray());
        Assert.Equal(1, _clientFactory.Calls.Count(c => c == "planner-step"));
        Assert.Empty(_clientFactory.Step2PlannerPrompts);
        // The web-only assessment ran — the loop-time assessment after the search (the final
        // whole-task quality check is skipped because the pipeline already recorded a
        // verified_complete step for the planCompleteDeclared path).
        Assert.True(_clientFactory.Calls.Count(c => c == "web-assess") >= 1,
            $"the web-only completion assessment should have run after the search — calls=[{string.Join(",", _clientFactory.Calls)}]");
        var searchResult = allSteps.OfType<Dictionary<string, object?>>()
            .Single(r => r.GetValueOrDefault("type")?.ToString() == "_web_search");
        Assert.Equal("done", searchResult.GetValueOrDefault("status")?.ToString());
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task WebTask_SearchOnly_AssessmentSaysIncomplete_RejectsPlanCompleteWithFeedback()
    {
        // The incomplete arm of the web gate: the assessment says the search alone does NOT
        // satisfy the task. The planner then declares planComplete (the scripted default) —
        // the incomplete-assessment gate REJECTS it with the assessment's reason as feedback,
        // forcing further planner turns instead of silently completing with unmet
        // requirements. When the scripted planner keeps re-declaring completion, the regen
        // budget ends the loop and the pre-existing repair fallback ("replanner proposed no
        // further steps") closes the run — the NEW behavior being locked is the rejection +
        // feedback loop, not the repair fallback.
        _clientFactory.Mode = PlannerMode.SearchOnly;
        _clientFactory.WebAssessComplete = () => false;
        var controller = BuildController();
        var prompt = "Search the web for recent AI breakthroughs and verify each result is a real published paper";

        var (_, plan, complete) = await InvokeOrchestrate(controller, prompt);

        // The assessment ran repeatedly and the plan-complete claims were rejected: multiple
        // planner turns (1 search + ≥2 rejected completions) and ≥3 assessment calls (after
        // the search + each plan-complete recheck).
        Assert.True(_clientFactory.Calls.Count(c => c == "planner-step") >= 3,
            $"incomplete assessments should have forced repeated planner turns — calls=[{string.Join(",", _clientFactory.Calls)}]; plan={plan?.Summary}");
        Assert.True(_clientFactory.Calls.Count(c => c == "web-assess") >= 3,
            $"the assessment should have re-run at each plan-complete recheck — calls=[{string.Join(",", _clientFactory.Calls)}]");
        // The rejection feedback (the assessment's unmet-requirement reason) reached a LATER
        // planner turn — after the first planComplete was refused, the next prompt carries the
        // reason WHY the completion claim was rejected.
        Assert.True(_clientFactory.PlannerUserPrompts.Count >= 3,
            $"expected ≥3 planner turns (search + rejected completions) — got {_clientFactory.PlannerUserPrompts.Count}");
        Assert.True(_clientFactory.PlannerUserPrompts.Skip(1).Any(p => p.Contains("NOT complete") && p.Contains("completion assessment")),
            $"the rejection feedback should have reached a later planner prompt:\n{string.Join("\n---\n", _clientFactory.PlannerUserPrompts.Skip(1))}");
        // The run still ENDS (via the repair fallback: the replanner proposed no further
        // steps) — no hang, no churn.
        Assert.True(complete, $"run should close via the repair fallback — calls=[{string.Join(",", _clientFactory.Calls)}]");
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task WebTask_NoWebContent_PrematurePlanComplete_SteeredToCommandWrite()
    {
        _clientFactory.Mode = PlannerMode.SteeringWrite;
        var controller = BuildController();
        var prompt = $"write the data into a text file at \"{_steerTarget}\"";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        // The premature planComplete (no file, no content to auto-dump) was rejected and the
        // planner was steered to plan the _command write; the run completes with the file.
        Assert.True(complete, $"pipeline should complete after the steered write — plan summary: {plan?.Summary}");
        Assert.True(File.Exists(_steerTarget), $"the steered _command should have written {_steerTarget}");
        var actualContent = File.ReadAllText(_steerTarget, Encoding.UTF8);
        Assert.True(actualContent.Contains("repair data"),
            $"steered file should contain the Set-Content value — got: [{actualContent}]");

        // The plan is the single steered _command write.
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_command" }, plan!.Plan.Select(s => s.File).ToArray());
        // On Windows the steering teaches the Set-Content cmdlet; on Unix hosts the
        // same write is a bash echo redirect — both target the same absolute path.
        if (OperatingSystem.IsWindows())
            Assert.Contains("Set-Content", plan.Plan[0].Change);
        else
            Assert.Contains(_steerTarget, plan.Plan[0].Change);

        // The _command executed for real and its result landed in the run.
        var cmdResult = allSteps.OfType<Dictionary<string, object?>>()
            .Single(r => r.GetValueOrDefault("type")?.ToString() == "command");
        Assert.Equal("done", cmdResult.GetValueOrDefault("status")?.ToString());

        // Planner call 1 declared complete prematurely (rejected by the OS-output gate),
        // call 2 planned the write, call 3 completed.
        Assert.Equal(3, _clientFactory.Calls.Count(c => c == "planner-step"));

        // Every LLM call was accounted for.
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── Failed web fetch: retried with feedback, never a halting crash ───────────────────
    // The exact field failure: the planner invented "www.example.com/latest-ai-breakthrough"
    // (copying the example-domain pattern from the tool-use example) instead of a real URL
    // from the search results, the fetch failed, and the interleaved loop HALTED — dropping
    // the run into the repair loop, whose replanner (with no web results and no OS-write
    // guidance) invented a Node fs writeArticleToFile() in an Angular service. Now a failed
    // _web_fetch is a retryable step: the loop removes it, feeds the error back to the
    // planner, and lets it retry with a REAL URL from the harvested results.

    [Fact]
    public async Task WebTask_InventedUrlFetchFails_LoopRetriesWithRealUrlAndCompletes()
    {
        _clientFactory.Mode = PlannerMode.FetchRetry;
        // Incomplete until the demanded dump file exists — the between-steps web assessment
        // keeps the chain planning until the OS-output gate auto-dumps the fetched data.
        _clientFactory.WebAssessComplete = () => File.Exists(_dumpTarget);
        var controller = BuildController();
        // Non-news phrasing — rule C routes "interesting and relevant AI article … write the
        // data into a text file" to the news digest, which is not what this test exercises.
        var prompt = $"Search the web for recent AI breakthroughs and write the data into a text file at \"{_dumpTarget}\".";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        // The run completed AND the file was written (the search data auto-dumped).
        Assert.True(complete, $"pipeline should complete after the fetch retry — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        Assert.True(File.Exists(_dumpTarget), $"auto-dump should have written {_dumpTarget}");
        Assert.Contains("AlphaFold 3", File.ReadAllText(_dumpTarget, Encoding.UTF8));

        // The invented-URL fetch EXECUTED and FAILED (status error with the reason recorded).
        var failedFetch = allSteps.OfType<Dictionary<string, object?>>()
            .Single(r => r.GetValueOrDefault("type")?.ToString() == "_web_fetch" &&
                         r.GetValueOrDefault("status")?.ToString() == "error");
        Assert.Equal("https://www.example.com/latest-ai-breakthrough", failedFetch.GetValueOrDefault("url")?.ToString());
        Assert.Contains("invented URL", failedFetch.GetValueOrDefault("error")?.ToString() ?? "");

        // The successful retry used the REAL URL from the search results — and the FINAL plan
        // carries it (the failed step was removed from planSoFar, never repeated). The
        // demanded file was written INSIDE that successful fetch step by the eager auto-dump,
        // so no extra _command write step exists in the plan.
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_web_search", "_web_fetch" }, plan!.Plan.Select(s => s.File).ToArray());
        Assert.Equal("https://example.com/alphafold3", plan.Plan[1].Change);
        var successfulFetch = allSteps.OfType<Dictionary<string, object?>>()
            .Single(r => r.GetValueOrDefault("type")?.ToString() == "_web_fetch" &&
                         r.GetValueOrDefault("status")?.ToString() == "done");
        Assert.Equal("https://example.com/alphafold3", successfulFetch.GetValueOrDefault("url")?.ToString());

        // The failure feedback ("Do NOT retry the same URL… use a REAL URL… never invent")
        // reached a LATER planner turn — the planner saw why the fetch failed.
        Assert.True(_clientFactory.PlannerUserPrompts.Skip(1).Any(p =>
                p.Contains("failed", StringComparison.OrdinalIgnoreCase) &&
                p.Contains("www.example.com", StringComparison.Ordinal) &&
                p.Contains("REAL URL", StringComparison.Ordinal)),
            $"the fetch-failure feedback should have reached a later planner prompt:\n{string.Join("\n---\n", _clientFactory.PlannerUserPrompts.Skip(1))}");

        // ZERO repo edits — the run never invented application code to "write" the file
        // (the OS-file auto-dump result carries os=true and is not a repo edit).
        var editResults = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("os") is not true &&
                        r.GetValueOrDefault("type")?.ToString() is "edit" or "create" &&
                        r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
            .ToList();
        Assert.Empty(editResults);
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── The "loop of searching": the planner re-proposes the SAME query after a ───────────
    // successful search. The web re-run guard must reject the repeat before it executes so
    // the search runs EXACTLY once, and the planner must then complete the task from the
    // results already in context.

    [Fact]
    public async Task WebTask_RepeatedSearchQuery_IsRejected_SearchRunsOnce()
    {
        // After step 1's _web_search succeeds and its results are harvested, the planner
        // re-proposes the identical query — the deep-reasoning engine re-derived the research
        // need instead of using the results (the user-observed loop). The web re-run guard
        // must reject the duplicate with feedback BEFORE it executes, and the planner must
        // then write the demanded file from the results already in context.
        _clientFactory.Mode = PlannerMode.RepeatedSearch;
        _clientFactory.WebAssessComplete = () => File.Exists(_steerTarget);
        var controller = BuildController();
        // Non-news phrasing — the news-marked variant routes to the digest, not DuckDuckGo.
        var prompt = $"Search the web for recent AI breakthroughs and write the data into a text file at \"{_steerTarget}\".";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        // The run completed AND the commanded write created the demanded file.
        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        Assert.True(File.Exists(_steerTarget), $"the commanded write should have created {_steerTarget}");
        Assert.Contains("AlphaFold 3", File.ReadAllText(_steerTarget, Encoding.UTF8));

        // The search executed EXACTLY ONCE — the repeated proposal never ran. Before the
        // guard, the identical query slipped past planSoFar dedup as a NEW step and re-ran
        // the search (the loop).
        var searchGets = _clientFactory.FetchedUrls.Count(u =>
            u.Contains("duckduckgo", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, searchGets);
        Assert.Single(allSteps.OfType<Dictionary<string, object?>>(),
            r => r.GetValueOrDefault("type")?.ToString() == "_web_search");

        // The web re-run rejection reached a LATER planner turn (the rejected duplicate is
        // fed back), steering the planner to use the results instead of re-searching.
        Assert.True(_clientFactory.PlannerUserPrompts.Skip(1).Any(p =>
                p.Contains("already ran _web_search", StringComparison.Ordinal)),
            $"the web re-run rejection should have reached a later planner prompt; calls=[{string.Join(",", _clientFactory.Calls)}]; plannerCalls={_clientFactory.PlannerUserPrompts.Count}; got:\n{string.Join("\n---\n", _clientFactory.PlannerUserPrompts.Skip(1))}");

        // ZERO repo edits — the run never invented application code to "write" the file
        // (the OS-file auto-dump result carries os=true and is not a repo edit).
        var editResults = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("os") is not true &&
                        r.GetValueOrDefault("type")?.ToString() is "edit" or "create" &&
                        r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
            .ToList();
        Assert.Empty(editResults);
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── Repair loop: deterministic auto-dump before the replanner ─────────────────────────
    // When the fetch retry budget is exhausted (a persistently broken URL), the run halts
    // into post-execution verification, which reports the missing OS output file. The repair
    // loop must then dump the harvested search results deterministically — WITHOUT ever
    // calling the replanner, which (pre-fix) invented an Angular-service write.

    [Fact]
    public async Task WebTask_FetchAlwaysFails_RepairLoopAutoDumpsWithoutReplanner()
    {
        _clientFactory.Mode = PlannerMode.FetchAlwaysFails;
        _clientFactory.WebAssessComplete = () => File.Exists(_dumpTarget);
        var controller = BuildController();
        // The environment's scraper ALSO fails (consistent with "always fails") — so after the
        // fetch budget is exhausted the auto-injected _scraper runs, fails, and the run still
        // falls to the deterministic repair auto-dump. The replanner must never be called.
        SetField(controller, "_scraperService", new FakeScraperService(succeed: false));
        // Non-news phrasing — the news-marked variant routes to the digest, not DuckDuckGo.
        var prompt = $"Search the web for recent AI breakthroughs and write the data into a text file at \"{_dumpTarget}\".";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        // The run completed and the demanded file exists — the search results were dumped.
        Assert.True(complete, $"pipeline should complete via the repair-loop auto-dump — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        Assert.True(File.Exists(_dumpTarget), $"repair auto-dump should have written {_dumpTarget}");
        Assert.Contains("AlphaFold 3", File.ReadAllText(_dumpTarget, Encoding.UTF8));

        // The replanner was NEVER called — the deterministic dump closed the loop before the
        // LLM repair step could invent application code. This is the core regression: a
        // pre-fix run ended with an invented Angular-service edit, an INCOMPLETE verdict, and
        // a churn-breaker trip.
        Assert.Empty(_clientFactory.RepairUserPrompts);

        // ZERO repo edits.
        var editResults = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("type")?.ToString() is "edit" or "create" &&
                        r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
            .ToList();
        Assert.Empty(editResults);

        // The failed fetch attempts are recorded with their error reasons.
        var failedFetches = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("type")?.ToString() == "_web_fetch" &&
                        r.GetValueOrDefault("status")?.ToString() == "error")
            .ToList();
        Assert.True(failedFetches.Count >= 2, $"expected the retry budget to be exhausted with ≥2 failed fetches — got {failedFetches.Count}");

        // The fetch budget was exhausted BEFORE the halt, so the system auto-injected ONE
        // _scraper attempt at the failing URL (deterministic, no LLM call) — and it failed
        // too, which is what let the repair auto-dump close the run.
        var scraperResult = Assert.Single(allSteps.OfType<Dictionary<string, object?>>(),
            r => r.GetValueOrDefault("type")?.ToString() == "scraper");
        Assert.Equal("error", scraperResult.GetValueOrDefault("status")?.ToString());
        Assert.Equal("https://www.example.com/latest-ai-breakthrough", scraperResult.GetValueOrDefault("url")?.ToString());
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task WebTask_FetchExhausted_AutoInjectsScraper_AndCompletes()
    {
        // The positive arm of the auto-inject: the fetch budget is exhausted on the invented
        // URL, and the loop AUTO-INJECTS a _scraper step (no planner call — the system builds
        // and runs a known-good scraper for the failed URL). The fake scraper succeeds and
        // writes the demanded file, so the run completes without any repair round.
        _clientFactory.Mode = PlannerMode.FetchExhaustedScraperSucceeds;
        _clientFactory.WebAssessComplete = () => File.Exists(_dumpTarget);
        var fake = new FakeScraperService(succeed: true);
        var controller = BuildController();
        SetField(controller, "_scraperService", fake);
        var prompt = $"Search the web for recent AI breakthroughs and write the data into a text file at \"{_dumpTarget}\".";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        Assert.True(complete, $"pipeline should complete via the auto-injected scraper — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        Assert.True(File.Exists(_dumpTarget), $"the scraper should have written {_dumpTarget}");
        Assert.Contains("scraper", File.ReadAllText(_dumpTarget, Encoding.UTF8));

        // The auto-injected _scraper step landed in the plan and ran against the failed URL.
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_web_search", "_scraper" }, plan!.Plan.Select(s => s.File).ToArray());
        Assert.Equal("https://www.example.com/latest-ai-breakthrough", plan.Plan[1].Change);
        Assert.Equal(new[] { "https://www.example.com/latest-ai-breakthrough" }, fake.Urls.ToArray());
        var scraperResult = Assert.Single(allSteps.OfType<Dictionary<string, object?>>(),
            r => r.GetValueOrDefault("type")?.ToString() == "scraper");
        Assert.Equal("done", scraperResult.GetValueOrDefault("status")?.ToString());

        // Exactly the 3 planned fetch attempts (first fetch + the 2 retries) — the budget
        // was burned, then the scraper took over. No repair round was needed (the file
        // already exists).
        var failedFetches = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("type")?.ToString() == "_web_fetch" &&
                        r.GetValueOrDefault("status")?.ToString() == "error")
            .ToList();
        Assert.Equal(3, failedFetches.Count);
        Assert.Empty(_clientFactory.RepairUserPrompts);
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── Boarddata fixture: the demanded OS output is NEVER written ────────────────────────
    // Mirrors the RejectedPlanStepPersistenceTests harness (a card in the board fixture, a
    // scripted client, the controller driven through the real Orchestrate path) and locks the
    // NEGATIVE of the OS-output guarantee: when a run ends without creating the demanded file
    // and has nothing to auto-dump, the run must STAY INCOMPLETE — the repair loop may attempt
    // but can never falsely complete it (a repair that produces no write step cannot flip the
    // verdict, and the missing-file CONFIRMED issue is persisted on the card).

    [Fact]
    public async Task WebTask_OsFileNeverWritten_RepairCannotFalselyComplete_StaysIncomplete()
    {
        const string cardId = "os-never-written";
        await _boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));
        _clientFactory.Mode = PlannerMode.NeverWrites;
        var controller = BuildController();
        // Absolute temp path (quoted so it parses as a path) — the demanded file that NO run
        // may ever create. Nothing is ever written to the real desktop.
        var prompt = $"write the data into a text file at \"{_neverWrittenTarget}\"";
        Assert.True(AgentOsOutputVerifier.TryGetOsFileOutputDemand(prompt, out _),
            $"the test prompt must demand an OS output file: {prompt}");

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt, cardId);

        // 1) The run STAYS INCOMPLETE — the headline guarantee.
        Assert.False(complete,
            $"a run that never wrote the demanded OS file must not complete — calls=[{string.Join(",", _clientFactory.Calls)}]; plan={plan?.Summary}");
        // 2) The file was genuinely never created.
        Assert.False(File.Exists(_neverWrittenTarget), $"nothing may write {_neverWrittenTarget}");
        // 3) No repo edit was applied as a substitute (the "invented edit" failure mode).
        var editResults = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("type")?.ToString() is "edit" or "create" &&
                        r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
            .ToList();
        Assert.Empty(editResults);
        // 4) The repair loop RAN (the plan-fixer was invoked) and was steered at the missing
        //    file — yet its empty plan could not rescue the run into a false completion.
        Assert.NotEmpty(_clientFactory.RepairUserPrompts);
        Assert.Contains("text file", _clientFactory.RepairUserPrompts[0]);
        Assert.Contains(_neverWrittenTarget, _clientFactory.RepairUserPrompts[0]);
        // 5) The card persists the incompleteness: the CONFIRMED OS-output issue is on the
        //    card as ground truth, and the final verification verdict is complete=false.
        var raw = await _boardData.LoadRawAsync();
        var gt = ReadCardGroundTruth(raw, cardId);
        Assert.NotNull(gt);
        Assert.Contains(gt!, e => e.Contains("never created", StringComparison.Ordinal) &&
                                  e.Contains(_neverWrittenTarget, StringComparison.Ordinal));
        var (vComplete, vReason) = ReadCardVerification(raw, cardId);
        Assert.False(vComplete, $"card verification must record complete=false — reason: {vReason}");
        // 6) Every LLM call the run made was accounted for by the script.
        Assert.Empty(_clientFactory.Unmatched);
    }

    private static string BoardWithCard(string cardId, string column)
    {
        var board = new Dictionary<string, object?>
        {
            ["todo"] = new List<object>(),
            ["doing"] = new List<object>(),
            ["done"] = new List<object>(),
            ["archived"] = new List<object>(),
            ["selfImproving"] = new List<object>()
        };
        board[column] = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["id"] = cardId,
                ["text"] = "task",
                ["filePath"] = "C:/x"
            }
        };
        return JsonSerializer.Serialize(board);
    }

    private static string? ReadCardProperty(string? raw, string cardId, string property)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        using var doc = JsonDocument.Parse(raw);
        foreach (var col in new[] { "todo", "doing", "done", "selfImproving" })
        {
            if (!doc.RootElement.TryGetProperty(col, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var card in arr.EnumerateArray())
            {
                if (!card.TryGetProperty("id", out var id) || id.GetString() != cardId) continue;
                return card.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String
                    ? prop.GetString()
                    : null;
            }
        }
        return null;
    }

    private static List<string>? ReadCardGroundTruth(string? raw, string cardId)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        using var doc = JsonDocument.Parse(raw);
        foreach (var col in new[] { "todo", "doing", "done", "selfImproving" })
        {
            if (!doc.RootElement.TryGetProperty(col, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var card in arr.EnumerateArray())
            {
                if (!card.TryGetProperty("id", out var id) || id.GetString() != cardId) continue;
                if (card.TryGetProperty("_groundTruth", out var gt) && gt.ValueKind == JsonValueKind.Array)
                    return gt.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                return new List<string>(); // card found, no ground truth
            }
        }
        return null;
    }

    private static (bool complete, string? reason) ReadCardVerification(string? raw, string cardId)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (false, null);
        using var doc = JsonDocument.Parse(raw);
        foreach (var col in new[] { "todo", "doing", "done", "selfImproving" })
        {
            if (!doc.RootElement.TryGetProperty(col, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var card in arr.EnumerateArray())
            {
                if (!card.TryGetProperty("id", out var id) || id.GetString() != cardId) continue;
                if (!card.TryGetProperty("_verification", out var v)) return (false, null);
                return (v.TryGetProperty("complete", out var c) && c.GetBoolean(),
                        v.TryGetProperty("reason", out var r) ? r.GetString() : null);
            }
        }
        return (false, null);
    }

    private async Task<(List<object> allSteps, AgentPlan? plan, bool complete)> InvokeOrchestrate(
        AgentController controller, string prompt, string? cardId = null,
        AgentPlan? existingPlan = null, HashSet<int>? completedStepIndices = null,
        List<Dictionary<string, object?>>? webResults = null)
    {
        var method = typeof(AgentController).GetMethod("Orchestrate", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Orchestrate not found");
        var task = (Task<(List<object> allSteps, AgentPlan? plan, bool complete)>)method.Invoke(controller, new object?[]
        {
            prompt, _projectRoot, /*emitSse*/ false, CancellationToken.None,
            /*attachedFiles*/ new List<string>(),
            /*skipContextReview*/ false, /*steeringContext*/ null, /*skipQualityCheck*/ false,
            /*existingPlan*/ existingPlan, /*completedStepIndices*/ completedStepIndices, /*cardId*/ cardId,
            /*createTests*/ false, /*buildCommands*/ null, /*strictVerifier*/ null, /*webResults*/ webResults
        })!;
        var result = await task;
        return result;
    }

    private static (AgentPlan? plan, HashSet<int>? completed, bool benchmark, List<Dictionary<string, object?>>? webResults) LoadPlan(
        AgentController controller, string cardId)
    {
        var method = typeof(AgentController).GetMethod("LoadPlanFromBoardDataAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("LoadPlanFromBoardDataAsync not found");
        var task = (Task<(AgentPlan? plan, HashSet<int>? completedIndices, bool isBenchmark, List<Dictionary<string, object?>>? webResults)>)
            method.Invoke(controller, new object?[] { cardId })!;
        return task.GetAwaiter().GetResult();
    }

    private AgentController BuildController()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Editor:WorkspaceRoot"] = _base,
                ["Editor:DisableLLMRetries"] = "true"
            })
            .Build();
        var controller = (AgentController)RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        SetField(controller, "_clientFactory", _clientFactory);
        SetField(controller, "_config", config);
        SetField(controller, "_env", new FakeWebHostEnvironment(_projectRoot));
        SetField(controller, "_db", _db);
        SetField(controller, "_configFile", new ConfigFileService(_db));
        SetField(controller, "_terminal", new TerminalService(new ConfigFileService(_db)));
        SetField(controller, "_fileHints", new FileHintsManager(_db));
        SetField(controller, "_boardData", _boardData);
        SetField(controller, "_emailService", new EmailService(new ConfigFileService(_db)));
        SetField(controller, "_push", new PushNotificationService(_db));
        SetField(controller, "_editKnowledge", new EditKnowledgeService(_db));
        SetField(controller, "_scraperService", new ScraperEnvironmentService());
        // Hermetic runtime probe: nothing installed (no real process spawns in tests).
        SetField(controller, "_runtimeProbe", new RuntimeProbeService((_, _, _) => (-1, "", "")));
        // Hermetic browser test: the fake service PASSES by default, so a scripted
        // _browser_test step completes (status "done") instead of launching a real server
        // against the empty temp project and failing with a launch error (which the
        // interleaved loop would treat as a fatal step failure and REMOVE from the plan).
        SetField(controller, "_browserTestService", new FakeBrowserAutomationService());
        // Skip the real TCP/HTTP connectivity probe (the run must not depend on the host
        // network): cache the "reachable" verdict directly.
        SetStaticField("_nextConnectivityCheck", DateTime.UtcNow.AddMinutes(5));
        SetField(controller, "_lastConnectionCheckResult", true);
        return controller;
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field {name} not found");
        field.SetValue(target, value);
    }

    private static object GetField(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field {name} not found");
        return field.GetValue(target)
            ?? throw new InvalidOperationException($"Field {name} is null");
    }

    private static void SetStaticField(string name, object value)
    {
        var field = typeof(AgentController).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Static field {name} not found");
        field.SetValue(null, value);
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public FakeWebHostEnvironment(string contentRoot) => ContentRootPath = contentRoot;
        public string ApplicationName { get; set; } = "Weaver";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; }
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private enum PlannerMode { WebChain, SteeringWrite, NeverWrites, SearchOnly, FetchRetry, FetchAlwaysFails, SearchThenEdit, RepeatedSearch, NewsDigestWrite, CommandFetchSteer, RepoRelativeDump, FetchFirstDump, MkdirCommandPrep, CreateFileAsDirectory, ScraperScriptRejected, ScraperAfterWebSteps, RunScraperCommandRejected, ScraperStepFallback, FetchExhaustedScraperSucceeds, FetchThenCsvEdits, VisualVerifyBuild, VisualVerifyBrokenServer, VisualVerifyHalted, MkdirThenPathlessCreateFile, BrokenCommandRecovery, EaddrInUseRecovery, MissingPortRecovery }

    /// <summary>
    /// Fake system-built scraper: records the URLs it was asked to scrape and writes the
    /// demanded output file, so the _scraper fallback step is testable without spawning a
    /// real interpreter.
    /// </summary>
    private sealed class FakeScraperService : ScraperEnvironmentService
    {
        public readonly List<string> Urls = new();
        private readonly bool _succeed;

        public FakeScraperService(bool succeed = true) => _succeed = succeed;

        public override async Task<ScraperResult> TryRunScraperAsync(
            string url, string? outputPath, string workDir, string? metadataLine, CancellationToken ct)
        {
            Urls.Add(url);
            if (_succeed && outputPath != null)
            {
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(outputPath,
                    "### WEB RESULTS [scraper] ###\nAlphaFold 3 predicts protein structures with atom-level accuracy\n", ct);
            }
            return _succeed
                ? new ScraperResult(true, "import requests\n# system-built\n",
                    "WROTE " + (outputPath ?? "(none)") + " 79", null, outputPath)
                : new ScraperResult(false, "import requests\n# system-built\n", "",
                    "scraper failed: site blocked", null);
        }
    }

    /// <summary>
    /// An <see cref="IHttpClientFactory"/> whose handler answers every LLM request from a
    /// script routed on stable prompt markers, and answers the web-step GETs the run makes:
    /// DuckDuckGo searches return realistic result JSON (so step 1 produces harvestable
    /// output with article URLs), fetches return a small body. Unmatched LLM calls are
    /// recorded and fail the test.
    /// </summary>
    private sealed class WebTaskScriptedClientFactory : IHttpClientFactory, IDisposable
    {
        public readonly List<string> Calls = new();
        public readonly List<string> Unmatched = new();
        // User prompts of the repair-loop replanner (plan-fixer) — recorded so the test can
        // assert the repair loop RAN and was steered at the missing file even when it can't
        // complete the run.
        public readonly List<string> RepairUserPrompts = new();
        // The user prompts of the planner's SECOND call in WebChain mode — the step that
        // must pick the _web_fetch URL. Recorded so the test can assert the harvested
        // ### WEB RESULTS section actually reached the step-2 planner turn.
        public readonly List<string> Step2PlannerPrompts = new();
        public readonly List<string> PlannerUserPrompts = new();
        // Every GET the run makes (searches, fetches, probes) — recorded so a test can
        // assert the executed _web_fetch hit the EXACT URL harvested from the search
        // results (cross-tool carry judged at execution, not just in the plan).
        public readonly List<string> FetchedUrls = new();
        public readonly string SteerTarget;
        public PlannerMode Mode;
        /// <summary>Deterministic verdict for the between-steps whole-task completion
        /// assessment ("Evaluate the code changes against the ORIGINAL TASK ONLY"). The
        /// web-only assessment is a NEW LLM call the harness must script: content-aware
        /// (e.g. File.Exists on the demanded output) so a web chain stays incomplete until
        /// its output lands, exactly like the AssessComplete pattern in the synthetic suite.
        /// Null defaults to complete=true.</summary>
        public Func<bool>? WebAssessComplete { get; set; }
        /// <summary>Deterministic verdict for the post-execution verifier ("meticulous code
        /// reviewer verifying if a task is fully complete"). Defaults to complete=true; a test
        /// flips it to false to script a verifier that says INCOMPLETE while the run itself
        /// still ends (via a completion fallback) — locking what the CARD must then show.
        /// The issue text is bare prose with a concrete absence claim and no backticked/
        /// qualified/method-call symbols, so verifier-issue triage keeps it and the repair
        /// loop drives to the "replanner proposed no further steps" fallback.</summary>
        public bool PostVerifyComplete { get; set; } = true;
        private int _plannerCalls;

        public WebTaskScriptedClientFactory(PlannerMode mode, string steerTarget)
        {
            Mode = mode;
            SteerTarget = steerTarget;
        }

        public HttpClient CreateClient(string name) => new(new ScriptedHandler(this));
        public HttpClient CreateClient() => CreateClient("default");
        public void Dispose() { }

        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly WebTaskScriptedClientFactory _owner;
            public ScriptedHandler(WebTaskScriptedClientFactory owner) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var resp = BuildResponse(request);
                return Task.FromResult(resp);
            }

            private HttpResponseMessage BuildResponse(HttpRequestMessage request)
            {
                if (request.Method == HttpMethod.Get)
                {
                    lock (_owner.FetchedUrls) _owner.FetchedUrls.Add(request.RequestUri?.ToString() ?? "");
                    var host = request.RequestUri?.Host ?? "";
                    if (host.Contains("duckduckgo", StringComparison.OrdinalIgnoreCase))
                    {
                        // Realistic DuckDuckGo instant-answer JSON — long enough (well over
                        // 80 chars) to be harvested into ### WEB RESULTS.
                        return Json(new
                        {
                            AbstractText = "A survey of recent AI research breakthroughs covering large language models, multimodal systems and protein-folding advances published this quarter.",
                            AbstractURL = "https://example.com/ai-overview",
                            Answer = "",
                            RelatedTopics = new object[]
                            {
                                new { Text = "AlphaFold 3 predicts protein structures with atom-level accuracy", FirstURL = "https://example.com/alphafold3" },
                                new { Text = "A new open-weight LLM benchmarks above GPT-4 on reasoning tasks", FirstURL = "https://example.com/llm-benchmarks" }
                            }
                        });
                    }
                    if (host.Contains("www.example.com", StringComparison.OrdinalIgnoreCase))
                    {
                        // The invented example-domain URL must genuinely FAIL — the exact field
                        // failure ("www.example.com/latest-ai-breakthrough"). Throwing makes
                        // WebFetchAsync return an error so the web step result carries status
                        // "error" and the interleaved loop exercises its fetch-failure retry.
                        throw new HttpRequestException($"fetch failed: invented URL on {host}");
                    }
                    if (host.Contains("news.google.com", StringComparison.OrdinalIgnoreCase) ||
                        host.Contains("bing.com", StringComparison.OrdinalIgnoreCase))
                    {
                        // The fresh-news DIGEST (NewsService.FetchNewsAsync) fetches Google News /
                        // Bing News RSS — answer with a realistic feed so the digest is built from
                        // real items (title/link/pubDate/description) instead of an empty digest.
                        return Xml(RssFeed);
                    }
                    if (host.Contains("pokeapi.co", StringComparison.OrdinalIgnoreCase))
                    {
                        // PokeAPI list-endpoint shape — {"results":[{"name":…,"url":…}]} — so the
                        // eager dump's list-shaped-JSON → CSV path writes CLEAN id,name,url rows
                        // (not the sectioned dump), which is exactly what the tabular editor then
                        // operates on for the benchmark-21 fetch → add column → add rows → mass
                        // edit → edit-rows sequence.
                        return Json(new
                        {
                            count = 1351,
                            next = (string?)null,
                            previous = (string?)null,
                            results = new object[]
                            {
                                new { name = "bulbasaur", url = "https://pokeapi.co/api/v2/pokemon/1/" },
                                new { name = "pikachu", url = "https://pokeapi.co/api/v2/pokemon/25/" },
                                new { name = "lucario", url = "https://pokeapi.co/api/v2/pokemon/448/" }
                            }
                        });
                    }
                    // Connectivity probes (/api/tags, /slots) and _web_fetch targets. The body
                    // is deliberately long enough (> 80 chars after the HTTP header) that a
                    // successful fetch's output gets harvested and persisted — so replay tests
                    // can prove the previous run's web data survives a restart.
                    return Json(new
                    {
                        title = "AlphaFold 3 predicts protein structures with atom-level accuracy",
                        body = "A new open-weight model benchmarks above GPT-4 on reasoning tasks."
                    });
                }
                var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
                var system = new StringBuilder();
                var user = new StringBuilder();
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("messages", out var msgs))
                    {
                        foreach (var m in msgs.EnumerateArray())
                        {
                            var role = m.TryGetProperty("role", out var r) ? r.GetString() : "";
                            var msgContent = m.TryGetProperty("content", out var c) ? c.GetString() : "";
                            if (role == "system") system.Append(msgContent).Append('\n');
                            else if (role == "user") user.Append(msgContent).Append('\n');
                        }
                    }
                }
                catch { }
                var streaming = body.Contains("\"stream\":true", StringComparison.Ordinal) ||
                                body.Contains("\"stream\": true", StringComparison.Ordinal);
                var (content, kind) = Route(system.ToString(), user.ToString());
                lock (_owner.Calls) _owner.Calls.Add(kind);
                return streaming ? Sse(content) : Json(new { choices = new[] { new { message = new { content } } } });
            }

            private (string content, string kind) Route(string system, string user)
            {
                if (system.Contains("senior autonomous coding agent building a code-change plan", StringComparison.Ordinal))
                {
                    lock (_owner.PlannerUserPrompts) _owner.PlannerUserPrompts.Add(user);
                    var n = Interlocked.Increment(ref _owner._plannerCalls);
                    return PlannerContent(n, user);
                }
                if (system.Contains("You are a plan-fixer", StringComparison.Ordinal))
                {
                    // The repair loop must run but NEVER produce a write: an empty plan means
                    // "replanner returned no steps" and the loop breaks with the run incomplete.
                    lock (_owner.RepairUserPrompts) _owner.RepairUserPrompts.Add(user);
                    if (_owner.Mode == PlannerMode.VisualVerifyBrokenServer)
                    {
                        // The benchmark-22 live failure: the browser test could not start the
                        // server (node crashed with "Cannot find module 'express'"). Script the
                        // repair the real replanner should produce — a dependency-free
                        // plain-http server replacing the express one — so the repair loop's
                        // deterministic live web RE-RUN can pass and unblock completion.
                        var repairPlan = new Dictionary<string, object?>
                        {
                            ["plan"] = new object[]
                            {
                                new Dictionary<string, object?>
                                {
                                    ["file"] = "_create_file",
                                    ["change"] = "benchmark_test_22/server.js",
                                    ["newString"] =
                                        "const http = require('http');\n" +
                                        "const fs = require('fs');\n" +
                                        "const path = require('path');\n" +
                                        "const PORT = process.env.PORT || 8765;\n" +
                                        "http.createServer((req, res) => {\n" +
                                        "  if (req.url === '/api/health') { res.writeHead(200, { 'Content-Type': 'application/json' }); res.end(JSON.stringify({ status: 'ok' })); return; }\n" +
                                        "  res.writeHead(200, { 'Content-Type': 'text/html' });\n" +
                                        "  res.end(fs.readFileSync(path.join(__dirname, 'index.html')));\n" +
                                        "}).listen(PORT, () => console.log('listening on ' + PORT));\n",
                                    ["priority"] = 1,
                                    ["line"] = 1
                                }
                            }
                        };
                        return (JsonSerializer.Serialize(repairPlan), "replanner");
                    }
                    return ("{\"plan\": []}", "replanner");
                }
                if (system.Contains("You are the deep-reasoning engine of an autonomous coding agent", StringComparison.Ordinal))
                    return ("The next step is scripted by the test harness. Keep the task minimal: implement exactly the scripted step.", "deep-reason");
                if (system.Contains("You are a news research planner", StringComparison.Ordinal))
                    return ("{\"query\": \"AI research breakthroughs latest\", \"topics\": [\"ai\"], \"places\": [], \"region\": \"\"}", "news-plan");
                if (system.Contains("You are a news summarizer building a research digest", StringComparison.Ordinal))
                    return ("## Summary\nA roundup of the latest AI research and industry news.\nITEM 1: A new open-weight model surpasses prior benchmarks.\nITEM 2: Protein-folding advances published this quarter.\n", "news-summary");
                if (system.Contains("You are a strict plan-coherence validator", StringComparison.Ordinal))
                    return ("{\"valid\": true}", "plan-validator");
                if (system.Contains("You are a task complexity assessor", StringComparison.Ordinal))
                    return ("{\"score\": 20, \"atomicSteps\": 2}", "complexity");
                if (system.Contains("You extract file paths from instructions", StringComparison.Ordinal))
                {
                    // The _create_file arm extracts the target path with a tiny LLM call —
                    // script it to echo the description back (the changeDesc IS the path).
                    return (user.Trim().Trim('"', '\'', '`'), "extract-path");
                }
                if (system.Contains("You classify a single user request", StringComparison.Ordinal))
                {
                    // The visual-inspection classifier — consulted by BOTH the discovery
                    // short-circuit and the plan-complete visual gate. Scripted content-aware:
                    // demands to LOOK at the rendered page / check for visual bugs need a live
                    // browser test; a pure styling edit does not.
                    var needsVisual = user.Contains("visual bug", StringComparison.OrdinalIgnoreCase) ||
                                      user.Contains("look at the rendered", StringComparison.OrdinalIgnoreCase) ||
                                      user.Contains("visually confirm", StringComparison.OrdinalIgnoreCase);
                    return (needsVisual
                        ? "{\"needsVisual\": true, \"target\": \"the game\"}"
                        : "{\"needsVisual\": false, \"target\": \"\"}", "visual-classifier");
                }
                if (system.Contains("You extract a short checklist of literal, testable requirements", StringComparison.Ordinal))
                {
                    // The checklist is APPENDED to the task prompt (Pipeline.cs), so in the
                    // write-file modes it must not contain web-y phrasing — "Search the web…"
                    // would trip TaskExplicitlyCommandsWeb and hijack the run into the web-need
                    // gate before the OS-output gate gets a chance.
                    var req = _owner.Mode == PlannerMode.WebChain
                        ? "[\"Search the web for an interesting AI article\", \"Write the article data into a text file on the desktop\"]"
                        : _owner.Mode == PlannerMode.SearchThenEdit
                            ? "[\"Add the article link into src/links.ts\"]"
                            : "[\"Write the data into the file\", \"The file must exist after the run\"]";
                    return ($"{{\"requirements\": {req}}}", "checklist");
                }
                if (system.Contains("strict task classifier", StringComparison.Ordinal))
                {
                    // The web-need classifier is only consulted when the task does NOT
                    // explicitly command the web (explicit commands bypass it entirely).
                    // Script it content-aware like a real classifier: a task that says to
                    // fetch/search for news genuinely needs current external info; repo-local
                    // writes don't.
                    var needsWeb = user.Contains("news article", StringComparison.OrdinalIgnoreCase) ||
                                   user.Contains("Search the web", StringComparison.OrdinalIgnoreCase) ||
                                   user.Contains("fetch the", StringComparison.OrdinalIgnoreCase);
                    return (needsWeb
                        ? "{\"needsWeb\": true, \"reason\": \"task requires current external news\", \"query\": \"AI research breakthroughs latest\"}"
                        : "{\"needsWeb\": false, \"reason\": \"repo-local write\", \"query\": \"\"}", "web-need");
                }
                if (user.Contains("Evaluate the code changes against the ORIGINAL TASK ONLY", StringComparison.Ordinal))
                {
                    // The between-steps completion assessment for web-only runs. Scripted
                    // content-aware: incomplete until the demanded output exists, then complete.
                    var done = _owner.WebAssessComplete?.Invoke() ?? true;
                    return (done
                        ? "{\"complete\": true, \"reason\": \"scripted web assessment: demanded output exists\", \"issues\": []}"
                        : "{\"complete\": false, \"reason\": \"scripted web assessment: demanded output not yet written\", \"issues\": [\"the demanded OS output file was not written yet\"]}", "web-assess");
                }
                // The cross-tool EDIT arm applies a repo edit, so the per-step verifier,
                // cohesion check, and post-execution verifier can run — script them all
                // (the search/fetch/command modes never edit, so these stayed unscripted).
                if (user.Contains("Decide: keep or abandon", StringComparison.Ordinal))
                    return ("{\"decision\": \"keep\", \"reason\": \"scripted edit\", \"score\": 95, \"needsExtraStep\": false}", "verify");
                if (system.Contains("You detect code cohesion issues after an edit. Output ONLY JSON.", StringComparison.Ordinal))
                    return ("{\"issues\": []}", "cohesion");
                if (system.Contains("meticulous code reviewer verifying if a task is fully complete", StringComparison.Ordinal))
                    return (_owner.PostVerifyComplete
                        ? "{\"complete\": true, \"reason\": \"done\", \"issues\": []}"
                        : "{\"complete\": false, \"reason\": \"scripted verifier: the demanded change was not applied\", \"issues\": [\"the article link from the search results is missing from src/links.ts\"]}", "post-verify");
                // The discovery-phase architecture one-liner (fires only when the project
                // actually has files — the edit arm's fixture makes it run).
                if (system.Contains("You are a project architect. Given a project file tree", StringComparison.Ordinal))
                    return ("TypeScript fixture project", "architecture");
                lock (_owner.Unmatched) _owner.Unmatched.Add(system.Length > 80 ? system[..80] : system);
                return ("", "unknown");
            }

            private (string content, string kind) PlannerContent(int n, string user)
            {
                if (_owner.Mode == PlannerMode.SteeringWrite)
                {
                    // Call 1 declares complete prematurely (must be rejected by the
                    // OS-output gate); call 2 plans the write the feedback demanded;
                    // call 3 completes.
                    if (n == 1)
                        return ("{\"planComplete\": true, \"completionReason\": \"nothing to do\"}", "planner-step");
                    if (n == 2)
                    {
                        var writeCmd = OperatingSystem.IsWindows()
                            ? $"Set-Content -Path \"{_owner.SteerTarget}\" -Value \"repair data\" -Encoding UTF8"
                            : $"echo 'repair data' > \"{_owner.SteerTarget}\"";
                        return (PlannerStepJson("_command", writeCmd), "planner-step");
                    }
                    return ("{\"planComplete\": true, \"completionReason\": \"wrote the file\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.NeverWrites)
                {
                    // Every planner turn declares the plan complete without ever proposing a
                    // write and without any web step to auto-dump — the OS-output gate rejects
                    // each premature completion until the regen budget is exhausted.
                    return ("{\"planComplete\": true, \"completionReason\": \"nothing to do\"}", "planner-step");
                }
                if (_owner.Mode is PlannerMode.VisualVerifyBuild or PlannerMode.VisualVerifyBrokenServer)
                {
                    // The benchmark-22 shape (build-then-visually-verify): turn 1 builds the
                    // game page, turn 2 declares the plan complete WITHOUT any _browser_test
                    // step — the pre-fix failure the visual gate must DETERMINISTICALLY
                    // auto-inject the live browser test (the real weak-model failure was a
                    // planner that answered the steering by proposing _command steps forever,
                    // whose rejections reset the regen counter, so the 3-strike cap never
                    // fired and the browser test never ran). Turn 3 completes — the gate now
                    // passes because the injected _browser_test step already ran.
                    if (n == 1)
                        return (PlannerStepJson("_create_file", "benchmark_test_22/index.html"), "planner-step");
                    if (n == 2)
                        return ("{\"planComplete\": true, \"completionReason\": \"built the game\"}", "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"built the game and ran the live browser test\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.VisualVerifyHalted)
                {
                    // The OTHER benchmark-22 exit: turn 1 builds the game, then every later
                    // turn returns an UNPARSEABLE proposal (no step, no planComplete) — the
                    // interleaved loop rejects it ("returned neither planComplete, exploreFile,
                    // nor a step"), the regen budget (3) exhausts, and the loop BREAKS without
                    // EVER declaring planComplete (the observed live failure: the planner's
                    // oversized-anchor edit was rejected twice, then its response was
                    // unparseable). The planComplete visual gate never fires on this path — the
                    // halted-run visual gate must inject + execute the _browser_test anyway.
                    if (n == 1)
                        return (PlannerStepJson("_create_file", "benchmark_test_22/index.html"), "planner-step");
                    return ("{\"planComplete\": false, \"thinking\": \"no step\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.SearchOnly)
                {
                    // One search step, then planComplete — the run must end after the search
                    // via the between-steps WEB assessment, not by the planner completing.
                    if (n == 1)
                        return (PlannerStepJson("_web_search", SearchQuery), "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"gathered the results\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.SearchThenEdit)
                {
                    // Turn 1 searches (the value-producer), turn 2 EDITS a repo file
                    // embedding the EXACT URL harvested from the search results — the
                    // cross-tool carry arm: search tool → edit tool.
                    if (n == 1)
                        return (PlannerStepJson("_web_search", SearchQuery), "planner-step");
                    if (n == 2)
                    {
                        lock (_owner.Step2PlannerPrompts) _owner.Step2PlannerPrompts.Add(user);
                        return (PlannerEditStepJson(LinksRel,
                            "Add a link to the best AI article from the search results into the links array",
                            "export const links: string[] = [];",
                            "export const links: string[] = ['https://example.com/alphafold3'];"
                        ), "planner-step");
                    }
                    return ("{\"planComplete\": true, \"completionReason\": \"added the article link from the search results\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.RepeatedSearch)
                {
                    // The "loop of searching": after step 1's _web_search succeeds, the planner
                    // re-proposes a REWORDED variant of the same query ("AI research
                    // breakthroughs" vs "AI research breakthroughs latest") — its deep reasoning
                    // re-derived the research need instead of using the harvested results. The
                    // exact-match "STEP ALREADY DONE" guard cannot catch a reworded query
                    // (~0.75 Jaccard vs the 0.82 edit threshold), so the web re-run guard must
                    // reject it BEFORE it executes; step 3 must then write the demanded file
                    // from the results already in context — the search may run EXACTLY once.
                    if (n == 1)
                        return (PlannerStepJson("_web_search", SearchQuery), "planner-step");
                    if (n == 2)
                        return (PlannerStepJson("_web_search", "AI research breakthroughs"), "planner-step");
                    if (n == 3)
                    {
                        var writeCmd = OperatingSystem.IsWindows()
                            ? $"Set-Content -Path \"{_owner.SteerTarget}\" -Value \"AlphaFold 3 predicts protein structures with atom-level accuracy\" -Encoding UTF8"
                            : $"echo 'AlphaFold 3 predicts protein structures with atom-level accuracy' > \"{_owner.SteerTarget}\"";
                        return (PlannerStepJson("_command", writeCmd), "planner-step");
                    }
                    return ("{\"planComplete\": true, \"completionReason\": \"wrote the file from the results already in context\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.NewsDigestWrite)
                {
                    // ONE search step — news-marked phrasing, so it routes to the fresh-news
                    // digest — then the eager OS-dump writes the digest to the demanded file
                    // IN THE SEARCH STEP and the between-steps assessment completes the run.
                    // No fetch and no _command write are ever planned.
                    if (n == 1)
                        return (PlannerStepJson("_web_search", SearchQuery), "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"digest gathered and written\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.CommandFetchSteer)
                {
                    // Step 2 proposes a _command that pulls content from a HALLUCINATED URL
                    // (the ".../haha-im-in-danger/" failure mode) — the fetch-in-command guard
                    // must reject it with feedback that names the URL and demands a VERBATIM
                    // result URL; step 3 must then plan a _web_fetch of a REAL URL from the
                    // harvested results. The invented URL never executes as a command.
                    if (n == 1)
                        return (PlannerStepJson("_web_search", SearchQuery), "planner-step");
                    if (n == 2)
                        return (PlannerStepJson("_command",
                            "Invoke-RestMethod -Uri \"https://www.example.com/latest-ai-breakthrough\" | Select-Object title,summary,publishedDate"), "planner-step");
                    if (n == 3)
                        return (PlannerStepJson("_web_fetch", "https://example.com/alphafold3"), "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"fetched the real URL from the results\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.RepoRelativeDump)
                {
                    // The benchmark-task shape, scripted: the planner LEADS with the folder
                    // (filesystem prep — the missing-web-search guard must NOT bounce it on a
                    // web-needing task), then search → fetch. The n==4 _create_file turn is
                    // DEAD — the dump-task short-circuit completes the run the moment the
                    // fetch's eager dump writes the demanded file, so the planner never gets
                    // to propose the redundant file (that's the asserted behavior).
                    if (n == 1)
                        return (PlannerStepJson("_create_directory", "benchmark_test_16"), "planner-step");
                    if (n == 2)
                        return (PlannerStepJson("_web_search", SearchQuery), "planner-step");
                    if (n == 3)
                        return (PlannerStepJson("_web_fetch", "https://example.com/alphafold3"), "planner-step");
                    if (n == 4)
                        return (PlannerCreateFileStepJson("benchmark_test_16/pokemon_data.csv", "id,name,hp,attack,defense,speed,type_1,type_2"), "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"folder created, data fetched and written into the csv\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.FetchFirstDump)
                {
                    // Step 1 is a DIRECT _web_fetch (a web step, so the missing-web-search
                    // guard admits it without forcing a search first). The eager dump writes the
                    // demanded file in that same step, and the dump-task short-circuit completes
                    // the run with ZERO completion-assessment LLM calls — the regression for the
                    // "planner re-emits the whole dataset inline" failure. The n==2 turn is
                    // DEAD (the run completes before the planner can propose another step).
                    if (n == 1)
                        return (PlannerStepJson("_web_fetch", "https://example.com/alphafold3"), "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"fetched and dumped the data\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.MkdirCommandPrep)
                {
                    // The "failed over and over to create a directory" bounce: round 1
                    // reaches for a _command mkdir. The missing-web-search guard must treat
                    // mkdir-style _command steps as filesystem PREP (same as _create_directory)
                    // and let them EXECUTE — no rejection, no auto-injected search first.
                    // Then search → fetch → the eager dump fills the demanded csv.
                    if (n == 1)
                        return (PlannerStepJson("_command", "mkdir \"benchmark_test_16\""), "planner-step");
                    if (n == 2)
                        return (PlannerStepJson("_web_search", SearchQuery), "planner-step");
                    if (n == 3)
                        return (PlannerStepJson("_web_fetch", "https://example.com/alphafold3"), "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"folder created, data fetched and written into the csv\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.BrokenCommandRecovery)
                {
                    // The benchmark-22 deadlock, faithfully: round 1 mkdir's the folder, round
                    // 2 creates a server.js that is syntactically VALID (so the JS syntax
                    // check admits it) but throws immediately, round 3 runs
                    // `node benchmark_test_22/server.js`, which CRASHES with "Error: boom" —
                    // pre-fix that step was reported "done", so the planner never saw a
                    // failure and deadlocked re-proposing the same command. Now the failure
                    // is REPORTED (status=error) and fed back, round 4 plans a recovery EDIT
                    // of the crashing file, and round 5 completes.
                    if (n == 1)
                        return (PlannerStepJson("_command", "mkdir \"benchmark_test_22\""), "planner-step");
                    if (n == 2)
                        return (PlannerCreateFileStepJson("benchmark_test_22/server.js", "throw new Error(\"boom\");"), "planner-step");
                    if (n == 3)
                        return (PlannerStepJson("_command", "node benchmark_test_22/server.js"), "planner-step");
                    if (n == 4)
                        // The recovery edit replaces the crashing line with a REAL server — not
                        // a console.log stub (the deterministic placeholder-stub reject would
                        // abandon it).
                        return (PlannerEditStepJson("benchmark_test_22/server.js", "fix the crash in server.js",
                            "throw new Error(\"boom\");",
                            "const http = require('http');\nconst server = http.createServer((req, res) => res.end('ok'));\nserver.listen(process.env.PORT || 8765);"), "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"fixed the crashing server and re-verified\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.EaddrInUseRecovery)
                {
                    // The benchmark-22 live failure: round 1 mkdir's the folder, round 2 creates
                    // a REAL server reading process.env.PORT || 8765, round 3 runs
                    // `node benchmark_test_22/server.js` WHILE port 8765 is held by the test —
                    // the step must recover deterministically on a free port (status done, no
                    // feedback, no new plan step) and the resolved URL must ride into round 4's
                    // prompt via the ### RUNNING SERVER ### context note. Round 4 completes.
                    if (n == 1)
                        return (PlannerStepJson("_command", "mkdir \"benchmark_test_22\""), "planner-step");
                    if (n == 2)
                        return (PlannerCreateFileStepJson("benchmark_test_22/server.js",
                            "const http = require('http');\nconst server = http.createServer((req, res) => res.end('ok'));\nserver.listen(process.env.PORT || 8765);"), "planner-step");
                    if (n == 3)
                        return (PlannerStepJson("_command", "node benchmark_test_22/server.js"), "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"server running on a free port after the port-in-use recovery\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.MissingPortRecovery)
                {
                    // The benchmark-22 live failure AFTER the planner's recovery edit: the
                    // edit replaced the port-selection block but DROPPED the `const PORT`
                    // line, so `node server.js` crashed with "ReferenceError: PORT is not
                    // defined". The error message names the missing parameter, so the command
                    // pipeline must patch `const PORT = process.env.PORT || 8765;` into the
                    // script itself and re-run — the re-run then hits the held default port
                    // and gets the free-port fix — all WITHOUT a planner round (round 4
                    // completes with no recovery step ever planned).
                    if (n == 1)
                        return (PlannerStepJson("_command", "mkdir \"benchmark_test_22\""), "planner-step");
                    if (n == 2)
                        return (PlannerCreateFileStepJson("benchmark_test_22/server.js",
                            "const http = require('http');\nconst server = http.createServer((req, res) => res.end('ok'));\nserver.listen(PORT, () => {});"), "planner-step");
                    if (n == 3)
                        return (PlannerStepJson("_command", "node benchmark_test_22/server.js"), "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"server running on a free port after the missing-PORT recovery\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.MkdirThenPathlessCreateFile)
                {
                    // Round 1 mkdir's the folder with a _command (the benchmark-22 shape —
                    // interleaved route, so no mkdir→_create_directory conversion). Round 2
                    // plans a PATHLESS _create_file whose change is a prose description, not
                    // the full path — the deterministic scoping must land it inside the
                    // folder the mkdir just created. Round 3 completes.
                    if (n == 1)
                        return (PlannerStepJson("_command", "mkdir \"benchmark_test_22\""), "planner-step");
                    if (n == 2)
                        return (PlannerCreateFileStepJson(
                            "Create server.js backend implementation serving static html + api health check",
                            "const http = require('http');\nconst PORT = process.env.PORT || 8765;\nhttp.createServer((req, res) => {\n  if (req.url === '/api/health') { res.writeHead(200, {'Content-Type': 'application/json'}); res.end(JSON.stringify({ status: 'ok' })); }\n  else { res.writeHead(200, {'Content-Type': 'text/html'}); res.end('<h1>Benchmark 22</h1>'); }\n}).listen(PORT);\n"), "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"folder created and server.js written inside it\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.CreateFileAsDirectory)
                {
                    // Round 1 reaches for _create_file to make a FOLDER ("Create
                    // benchmark_test_16 directory"). The missing-web-search gate must reject
                    // with TARGETED steering toward _create_directory — not the generic web
                    // feedback (which would bounce it again) — so round 2 lands cleanly.
                    if (n == 1)
                        return (PlannerCreateFileStepJson("Create benchmark_test_16 directory", "placeholder"), "planner-step");
                    if (n == 2)
                        return (PlannerStepJson("_create_directory", "benchmark_test_16"), "planner-step");
                    if (n == 3)
                        return (PlannerStepJson("_web_search", SearchQuery), "planner-step");
                    if (n == 4)
                        return (PlannerStepJson("_web_fetch", "https://example.com/alphafold3"), "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"folder created, data fetched and written into the csv\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.ScraperScriptRejected)
                {
                    // Round 1 reaches for _create_file to write a PYTHON SCRAPER that does the
                    // HTTP fetch itself (requests.get + open(...,"w") — the "wrote a Python app
                    // to do a fetch" drift, the planner forgetting _web_fetch exists). The gate
                    // must reject with SCRAPER steering; round 2 must then plan the real web
                    // chain. The scraper file must NEVER land on disk.
                    if (n == 1)
                        return (PlannerCreateFileStepJson("benchmark_test_16/fetch_pokemon.py",
                            "import requests\n" +
                            "resp = requests.get(\"https://pokeapi.co/api/v2/pokemon?limit=1025\")\n" +
                            "with open(\"pokemon_data.csv\", \"w\") as f:\n" +
                            "    f.write(resp.text)\n"), "planner-step");
                    if (n == 2)
                        return (PlannerStepJson("_web_search", SearchQuery), "planner-step");
                    if (n == 3)
                        return (PlannerStepJson("_web_fetch", "https://example.com/alphafold3"), "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"folder created, data fetched and written into the csv\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.ScraperAfterWebSteps)
                {
                    // The exact benchmark-run shape: search commits, THEN the planner still
                    // proposes the Python scraper _create_file. The missing-web-search gate is
                    // silent here — a web step already ran — so the SCRAPER-FILE GUARD in
                    // ValidateIncrementalStepAsync must reject it (previously it sailed
                    // straight through, landed on disk, and then got "run"). The rejection
                    // steers round 3 to a _web_fetch whose eager dump writes the demanded CSV.
                    if (n == 1)
                        return (PlannerStepJson("_web_search", SearchQuery), "planner-step");
                    if (n == 2)
                        return (PlannerCreateFileStepJson("benchmark_test_16/fetch_pokemon.py",
                            "import requests\n" +
                            "resp = requests.get(\"https://pokeapi.co/api/v2/pokemon?limit=1025\")\n" +
                            "with open(\"pokemon_data.csv\", \"w\") as f:\n" +
                            "    f.write(resp.text)\n"), "planner-step");
                    if (n == 3)
                        return (PlannerStepJson("_web_fetch", "https://example.com/alphafold3"), "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"data fetched and auto-written by the web step\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.RunScraperCommandRejected)
                {
                    // search commits, then the planner tries to RUN the scraper
                    // (`python fetch_poke_data.py` — no such file exists, the create was
                    // rejected) — the RUN-A-SCRAPER GUARD in ValidateIncrementalStepAsync
                    // must reject it, so round 3 fetches the real URL and the eager dump
                    // writes the CSV. The run command never enters the plan.
                    if (n == 1)
                        return (PlannerStepJson("_web_search", SearchQuery), "planner-step");
                    if (n == 2)
                        return (PlannerStepJson("_command", "python fetch_poke_data.py"), "planner-step");
                    if (n == 3)
                        return (PlannerStepJson("_web_fetch", "https://example.com/alphafold3"), "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"data fetched and auto-written by the web step\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.ScraperStepFallback)
                {
                    // search commits, then the planner falls back to a "_scraper" step (web
                    // fetch keeps failing) — the system builds + runs a KNOWN-GOOD scraper
                    // via the injected fake service, writing the demanded CSV. No scraper
                    // code ever lands as a repo file.
                    if (n == 1)
                        return (PlannerStepJson("_web_search", SearchQuery), "planner-step");
                    if (n == 2)
                        return (PlannerStepJson("_scraper", "https://example.com/alphafold3"), "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"scraper wrote the demanded file\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.FetchThenCsvEdits)
                {
                    // The benchmark-21 shape: create the folder, search, fetch the PokeAPI list
                    // (whose eager dump writes the clean id,name,url CSV), then the tabular edit
                    // steps — add a column, add rows, MASS edit (fill the original rows' EMPTY
                    // type cells — the "where type is empty" token), and per-row edits. The
                    // dump-task short-circuit must NOT fire after the fetch (the task demands
                    // structured edits — IsPureDumpTask is false), so the run continues through
                    // the tabular fast-path and completes only after the last edit, with the
                    // planner's planComplete ending it (no corrective steps).
                    const string csv = "benchmark_test_21/pokemon_data.csv";
                    if (n == 1) return (PlannerStepJson("_create_directory", "benchmark_test_21"), "planner-step");
                    if (n == 2) return (PlannerStepJson("_web_search", SearchQuery), "planner-step");
                    if (n == 3) return (PlannerStepJson("_web_fetch", "https://pokeapi.co/api/v2/pokemon?limit=1025"), "planner-step");
                    if (n == 4) return (PlannerStepJson(csv, "add a type column"), "planner-step");
                    if (n == 5) return (PlannerStepJson(csv, "add a row with id=1026, name=weavmon, type=unknown"), "planner-step");
                    if (n == 6) return (PlannerStepJson(csv, "add a row with id=1027, name=kanbanite, type=unknown"), "planner-step");
                    if (n == 7) return (PlannerStepJson(csv, "add a row with id=1028, name=bugcatcher, type=unknown"), "planner-step");
                    if (n == 8) return (PlannerStepJson(csv, "set type to 'normal' where type is empty"), "planner-step");
                    if (n == 9) return (PlannerStepJson(csv, "set type to 'electric' where name is 'weavmon'"), "planner-step");
                    if (n == 10) return (PlannerStepJson(csv, "set type to 'ghost' where name is 'kanbanite'"), "planner-step");
                    if (n == 11) return (PlannerStepJson(csv, "set type to 'fairy' where name is 'bugcatcher'"), "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"fetched the data and applied all the CSV edits\"}", "planner-step");
                }
                if (n == 1)
                    return (PlannerStepJson("_web_search", SearchQuery), "planner-step");
                if (_owner.Mode == PlannerMode.FetchRetry)
                {
                    // Step 2 fetches an INVENTED URL (www.example.com — the exact failure from
                    // the field: the model copied the example-domain pattern instead of a real
                    // result URL). The scripted web handler makes that host throw, so the fetch
                    // FAILS. Step 3 must then retry with a REAL URL from the harvested results.
                    lock (_owner.Step2PlannerPrompts) _owner.Step2PlannerPrompts.Add(user);
                    if (n == 2)
                        return (PlannerStepJson("_web_fetch", "https://www.example.com/latest-ai-breakthrough"), "planner-step");
                    if (n == 3)
                        return (PlannerStepJson("_web_fetch", "https://example.com/alphafold3"), "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"fetched a real URL after the invented one failed\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.FetchAlwaysFails)
                {
                    // Every fetch uses the invented example-domain URL and fails. The retry cap
                    // (2) is exhausted, the run halts into post-execution verification, and the
                    // repair loop's deterministic auto-dump must complete the run from the
                    // harvested search results — the replanner must NEVER be called.
                    lock (_owner.Step2PlannerPrompts) _owner.Step2PlannerPrompts.Add(user);
                    return (PlannerStepJson("_web_fetch", "https://www.example.com/latest-ai-breakthrough"), "planner-step");
                }
                if (_owner.Mode == PlannerMode.FetchExhaustedScraperSucceeds)
                {
                    // The planner burns the fetch budget on the invented URL (turns 2-4: the
                    // first fetch plus the two retries all fail), then — with no planner call —
                    // the loop AUTO-INJECTS a _scraper step for the failed URL. The fake
                    // scraper succeeds and writes the demanded file; the next turn declares
                    // the plan complete.
                    if (n is >= 2 and <= 4)
                        return (PlannerStepJson("_web_fetch", "https://www.example.com/latest-ai-breakthrough"), "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"scraper wrote the demanded file after the fetch budget was exhausted\"}", "planner-step");
                }
                if (n == 2)
                {
                    // Step 2 must pick a CONCRETE URL from the harvested results.
                    lock (_owner.Step2PlannerPrompts) _owner.Step2PlannerPrompts.Add(user);
                    return (PlannerStepJson("_web_fetch", "https://example.com/alphafold3"), "planner-step");
                }
                return ("{\"planComplete\": true, \"completionReason\": \"fetched the article URL from the search results\"}", "planner-step");
            }

            private static string PlannerStepJson(string file, string change)
            {
                var payload = new Dictionary<string, object?>
                {
                    ["thinking"] = $"Step: {file} — {change}",
                    ["planComplete"] = false,
                    ["step"] = new Dictionary<string, object?>
                    {
                        ["file"] = file,
                        ["change"] = change
                    }
                };
                return JsonSerializer.Serialize(payload);
            }

            private static string PlannerEditStepJson(string file, string change, string oldString, string newString)
            {
                var payload = new Dictionary<string, object?>
                {
                    ["thinking"] = $"Edit: {file} — {change}",
                    ["planComplete"] = false,
                    ["step"] = new Dictionary<string, object?>
                    {
                        ["file"] = file,
                        ["change"] = change,
                        ["oldString"] = oldString,
                        ["newString"] = newString
                    }
                };
                return JsonSerializer.Serialize(payload);
            }

            private static string PlannerCreateFileStepJson(string file, string content)
            {
                var payload = new Dictionary<string, object?>
                {
                    ["thinking"] = $"Create file: {file}",
                    ["planComplete"] = false,
                    ["step"] = new Dictionary<string, object?>
                    {
                        ["file"] = "_create_file",
                        ["change"] = file,
                        ["newString"] = content
                    }
                };
                return JsonSerializer.Serialize(payload);
            }

            private static HttpResponseMessage Json(object obj)
                => new(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json")
                };

            private static HttpResponseMessage Xml(string xml)
                => new(HttpStatusCode.OK)
                {
                    Content = new StringContent(xml, Encoding.UTF8, "application/xml")
                };

            private const string RssFeed =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<rss version=\"2.0\"><channel><title>Google News</title>" +
                "<item><title>AlphaFold 3 predicts protein structures with atom-level accuracy</title>" +
                "<link>https://example.com/alphafold3</link>" +
                "<pubDate>Wed, 12 Aug 2026 10:00:00 GMT</pubDate>" +
                "<description>A new open-weight model benchmarks above GPT-4 on reasoning tasks.</description></item>" +
                "<item><title>A new open-weight LLM benchmarks above GPT-4 on reasoning tasks</title>" +
                "<link>https://example.com/llm-benchmarks</link>" +
                "<pubDate>Wed, 12 Aug 2026 09:00:00 GMT</pubDate>" +
                "<description>Benchmark results published today.</description></item>" +
                "</channel></rss>";

            private static HttpResponseMessage Sse(string content)
            {
                var data = JsonSerializer.Serialize(new
                {
                    choices = new[] { new { delta = new { content }, finish_reason = "stop" } }
                });
                var body = $"data: {data}\n\n\ndata: [DONE]\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
                };
            }
        }
    }
}
