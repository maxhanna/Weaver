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
/// "Tool calls are the real attack surface" eval suite. The agent can produce a
/// plausible-looking plan while quietly calling the WRONG tool — an edit instead of
/// a command, a curl in a _command step instead of _web_search, a _create_file dodge
/// instead of editing the attached file. So every test here replays a trace against
/// a REAL sandbox (temp project + scripted fake LLM + interceptable HTTP) and asserts
/// BOTH sides:
///
///   • OUTPUT — the accepted plan and the final state (files edited/created/deleted
///     on disk, what the run claims to have done).
///   • SIDE EFFECTS — which tool ACTUALLY executed: the web requests the fake HTTP
///     client recorded (or did NOT record), the files that appeared/disappeared in
///     the sandbox, the commands issued through the terminal.
///
/// Suite A (ToolCorpus): a large prompt corpus, each with the golden tool, asserting
/// the right tool is planned AND its side effect lands (and nothing else happens).
/// Suite B (adversarial traces): the planner first proposes the WRONG tool; the
/// deterministic guard must reject it with the auditable reason, the wrong side
/// effect must NEVER happen (no invented URL fetched, no dodge file created, no
/// command run), and the corrected tool must then execute.
/// </summary>
public class ToolSelectionEvalTests : IDisposable
{
    private const string DemoTsRel = "maxhanna.client/src/app/demo/demo.component.ts";
    private const string DemoComponentTs = """
        export class DemoComponent {
          title = 'demo';
          items: string[] = [];
          constructor() { }
        }
        """;

    private const string CtorLine = "  constructor() { }";
    private const string CtorLineWithMethod = "  constructor() { }\n  getItems() { return this.items.slice(); }";

    private const string NotesRel = "NOTES.md";
    private const string NotesContent = "# Release notes\n\n- v1.0: initial release\n";
    private const string NotesUpdated = "- v1.0: initial release\n- v2.0: agent version bump\n";

    private readonly string _base;
    private readonly string _projectRoot;
    private readonly DatabaseService _db;

    public ToolSelectionEvalTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "weaver_toolsel_" + Guid.NewGuid().ToString("N"));
        _projectRoot = Path.Combine(_base, "proj");
        Directory.CreateDirectory(_projectRoot);
        Directory.CreateDirectory(Path.Combine(_base, "data"));
        Directory.CreateDirectory(Path.Combine(_projectRoot, "src"));

        var tsPath = Path.Combine(_projectRoot, DemoTsRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(tsPath)!);
        File.WriteAllText(tsPath, DemoComponentTs);
        File.WriteAllText(Path.Combine(_projectRoot, NotesRel), NotesContent);
        File.WriteAllText(Path.Combine(_projectRoot, "src", "obsolete.txt"), "stale content\n");
        File.WriteAllText(Path.Combine(_projectRoot, "src", "old.txt"), "old content\n");

        _db = new DatabaseService(
            Path.Combine(_base, "data", "weaver.db"),
            Path.Combine(_base, "data"),
            Path.Combine(_base, "data", "weaverconfig.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, true); } catch { }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SUITE A — the golden-tool corpus: the planner proposes the RIGHT tool; we
    // assert it is planned AND its sandbox side effect actually lands.
    // ═══════════════════════════════════════════════════════════════════════════

    public sealed class StepSpec
    {
        public StepSpec(string file, string change, string? oldString = null, string? newString = null, string[]? attached = null, string? assertToken = null)
        {
            File = file; Change = change; OldString = oldString; NewString = newString;
            Attached = attached ?? Array.Empty<string>(); AssertToken = assertToken;
        }
        public string File { get; }
        public string Change { get; }
        public string? OldString { get; }
        public string? NewString { get; }
        public string[] Attached { get; }
        /// <summary>Token that must appear in the edited file after the run (edit cases).</summary>
        public string? AssertToken { get; }
    }

    /// <summary>JSON shape of one corpus case (tests/tool-selection-corpus.json). Settable
    /// properties for System.Text.Json; mapped onto the immutable <see cref="StepSpec"/> the
    /// harness drives with. Property names are camelCase in the file and read
    /// case-insensitively.</summary>
    public sealed class ToolCorpusCase
    {
        public string Name { get; set; } = "";
        public string Prompt { get; set; } = "";
        public string File { get; set; } = "";
        public string Change { get; set; } = "";
        public string? OldString { get; set; }
        public string? NewString { get; set; }
        public List<string> Attached { get; set; } = new();
        public string? AssertToken { get; set; }

        public StepSpec ToStepSpec() =>
            new(File, Change, OldString, NewString, Attached.ToArray(), AssertToken);

        /// <summary>Loads the corpus from the test output (the csproj copies it there with
        /// CopyToOutputDirectory). Fails loudly when missing or empty so a broken deployment
        /// can never silently run an empty corpus.</summary>
        public static List<ToolCorpusCase> LoadAll()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "tool-selection-corpus.json");
            if (!System.IO.File.Exists(path))
                throw new InvalidOperationException(
                    $"Tool-selection corpus not found at '{path}' — the JSON corpus must be copied to the test output.");
            var json = System.IO.File.ReadAllText(path);
            var cases = JsonSerializer.Deserialize<List<ToolCorpusCase>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            if (cases == null || cases.Count == 0)
                throw new InvalidOperationException(
                    $"Tool-selection corpus at '{path}' is empty or unreadable.");
            return cases;
        }
    }

    /// <summary>
    /// The golden-tool corpus is DATA-DRIVEN: it loads <c>tests/tool-selection-corpus.json</c>
    /// (copied to the test output as <c>tool-selection-corpus.json</c>), so adding a new
    /// prompt + expected-tool/side-effect case is a pure JSON edit — no C# change. Each case
    /// carries the prompt, the golden step (tool name or file path, change text, old/new
    /// strings for edits), the files to attach, and the token that must land on disk. A
    /// missing or empty corpus fails LOUDLY at discovery rather than silently running zero
    /// cases.
    /// </summary>
    public static IEnumerable<object[]> ToolCorpus()
    {
        foreach (var c in ToolCorpusCase.LoadAll())
            yield return new object[] { c.Name, c.Prompt, c.ToStepSpec() };
    }

    [Theory]
    [MemberData(nameof(ToolCorpus))]
    public async Task Corpus_RightToolPlannedAndSideEffectLands(string name, string prompt, StepSpec spec)
    {
        var factory = new ToolSelectionScriptedClientFactory();
        factory.Proposals.Add(spec);
        var controller = BuildController(factory);
        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt, spec.Attached);

        // OUTPUT: the run completed and the accepted plan is exactly the golden tool.
        Assert.True(complete, $"[{name}] run should complete — plan: {plan?.Summary}; unmatched={factory.Unmatched.Count}\n{string.Join("\n", factory.UnmatchedSystems)}");
        Assert.NotNull(plan);
        Assert.True(plan!.Plan.Count >= 1, $"[{name}] plan should contain the expected step; got {plan.Plan.Count}");
        Assert.Equal(spec.File, plan.Plan[0].File);
        if (spec.File is not ("_sql_migration")) // change is a free-form description for migrations
            Assert.Equal(spec.Change, plan.Plan[0].Change);

        // No hidden LLM calls (no silent replan/repair/retry churn).
        Assert.Empty(factory.Unmatched);

        // SIDE EFFECTS: the tool really executed — not just planned.
        AssertToolSideEffects(name, spec, allSteps, factory);
    }

    private void AssertToolSideEffects(string name, StepSpec spec, List<object> allSteps, ToolSelectionScriptedClientFactory factory)
    {
        var results = allSteps.OfType<Dictionary<string, object?>>().ToList();
        string Rel(string p) => Path.Combine(_projectRoot, p.Replace('/', Path.DirectorySeparatorChar));
        switch (spec.File)
        {
            case "_web_search":
            {
                var r = results.Single(x => x.GetValueOrDefault("type")?.ToString() == "_web_search");
                Assert.Equal("done", r.GetValueOrDefault("status")?.ToString());
                // The search engine was really queried (side-effect trace).
                Assert.True(factory.Gets.Any(u => u.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase)),
                    $"[{name}] _web_search must issue a GET to the search engine; gets=[{string.Join("; ", factory.Gets)}]");
                // ...and its output was harvested (the point of the tool).
                var output = r.GetValueOrDefault("output")?.ToString() ?? "";
                Assert.Contains("https://example.com/alphafold3", output);
                break;
            }
            case "_web_fetch":
            {
                var r = results.Single(x => x.GetValueOrDefault("type")?.ToString() == "_web_fetch");
                Assert.Equal("done", r.GetValueOrDefault("status")?.ToString());
                // The exact URL was fetched (side-effect trace) — nothing invented.
                Assert.Contains(spec.Change, factory.Gets);
                break;
            }
            case "_create_file":
            {
                Assert.True(File.Exists(Rel(spec.Change)), $"[{name}] _create_file must create '{spec.Change}' on disk");
                Assert.Equal(spec.NewString, File.ReadAllText(Rel(spec.Change)));
                break;
            }
            case "_create_directory":
                Assert.True(Directory.Exists(Rel(spec.Change)), $"[{name}] _create_directory must create '{spec.Change}' on disk");
                break;
            case "_delete_file":
                Assert.False(File.Exists(Rel(spec.Change)), $"[{name}] _delete_file must remove '{spec.Change}' from disk");
                break;
            case "_rename_file":
            {
                var (src, dst) = ParseRename(spec.Change);
                Assert.False(File.Exists(Rel(src)), $"[{name}] old path '{src}' must be gone after rename");
                Assert.True(File.Exists(Rel(dst)), $"[{name}] new path '{dst}' must exist after rename");
                break;
            }
            case "_command":
            {
                var r = results.Single(x => x.GetValueOrDefault("type")?.ToString() == "command");
                Assert.Equal("done", r.GetValueOrDefault("status")?.ToString());
                Assert.Equal(spec.Change, r.GetValueOrDefault("command")?.ToString());
                var output = r.GetValueOrDefault("output")?.ToString() ?? "";
                if (spec.Change.Contains("hello-from-command.txt"))
                {
                    // The command really ran in the sandbox and its file side effect landed.
                    var written = Rel("hello-from-command.txt");
                    Assert.True(File.Exists(written), $"[{name}] command must create hello-from-command.txt");
                    Assert.Contains("hello", File.ReadAllText(written));
                }
                else
                {
                    // pwd — output must name the sandbox project dir (command ran in the right cwd).
                    Assert.Contains("weaver_toolsel_", output);
                }
                break;
            }
            case "_sql_migration":
            {
                var migFile = Rel("migrations/schema_changes.md");
                Assert.True(File.Exists(migFile), $"[{name}] _sql_migration must write migrations/schema_changes.md");
                var content = File.ReadAllText(migFile);
                Assert.Contains("CREATE TABLE IF NOT EXISTS user_preferences", content);
                break;
            }
            default:
            {
                // A plain file edit: the edit result is recorded AND the file changed.
                var r = results.Single(x => x.GetValueOrDefault("type")?.ToString() == "edit");
                Assert.Equal("done", r.GetValueOrDefault("status")?.ToString());
                Assert.NotNull(spec.AssertToken);
                Assert.Contains(spec.AssertToken!, File.ReadAllText(Rel(spec.File)),
                    StringComparison.Ordinal);
                break;
            }
        }
    }

    private static (string src, string dst) ParseRename(string change)
    {
        var i = change.IndexOf('\u2192');
        if (i <= 0) throw new InvalidOperationException("bad rename spec: " + change);
        return (change[..i].Trim(), change[(i + 1)..].Trim());
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SUITE B — adversarial wrong-tool traces. The planner's FIRST proposal is the
    // wrong tool; a deterministic guard must reject it with an auditable reason, the
    // wrong side effect must never land, and the corrected tool must then execute.
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fetch-in-command guard: the planner answers a web-hinting task with a
    /// `_command` step that curls an invented API (the "api.current.ai" failure
    /// mode). The guard rejects it with FetchCommandFeedback; the follow-up must be
    /// a repo edit, the command must NEVER run (no notes.json, no URL fetched).
    /// </summary>
    [Fact]
    public async Task Trace_FetchCommandRejected_CommandNeverRuns()
    {
        const string prompt = "Check the latest weaver release notes online and note the version in NOTES.md.";
        var factory = new ToolSelectionScriptedClientFactory
        {
            ClassifierNeedsWeb = false,
            ClassifierReason = "the release notes are already tracked in the repo"
        };
        // Proposal 1: the wrong tool — an invented-API fetch via _command.
        factory.Proposals.Add(new StepSpec("_command",
            "Invoke-RestMethod https://api.current.ai/releases | ConvertTo-Json | Set-Content notes.json"));
        // Proposal 2 (retry): the right tool — a repo edit.
        factory.Proposals.Add(new StepSpec(NotesRel, "Add the version note to the release notes",
            "- v1.0: initial release", NotesUpdated, assertToken: "v2.0: agent version bump"));

        var controller = BuildController(factory);
        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt, Array.Empty<string>());

        Assert.True(complete, $"run should complete — plan: {plan?.Summary}; unmatched={factory.Unmatched.Count}\n{string.Join("\n", factory.UnmatchedSystems)}");

        // The rejection feedback reached the next planner turn, with the guard's reason.
        Assert.True(factory.PlannerUserPrompts.Count >= 2,
            $"expected >= 2 planner turns (rejection + retry), got {factory.PlannerUserPrompts.Count}");
        Assert.Contains("REJECTED", factory.PlannerUserPrompts[1]);
        Assert.Contains("Fetching web content is NOT a shell command", factory.PlannerUserPrompts[1]);

        // The corrected tool is what got planned and executed.
        Assert.NotNull(plan);
        Assert.Equal(NotesRel, plan!.Plan.Single().File);
        Assert.Contains("edit", allSteps.OfType<Dictionary<string, object?>>().Select(r => r.GetValueOrDefault("type")?.ToString()));
        Assert.Contains("v2.0: agent version bump", File.ReadAllText(Path.Combine(_projectRoot, NotesRel)));

        // SIDE-EFFECT ABSENCE: the wrong _command never ran — no notes.json, no web fetch at all.
        Assert.False(File.Exists(Path.Combine(_projectRoot, "notes.json")));
        Assert.Empty(factory.Gets);
        Assert.Empty(factory.Unmatched);
    }

    /// <summary>
    /// Web-need veto: on a repo-only task the planner proposes _web_search; the
    /// classifier vetoes with an auditable reason, the web step is rejected, and the
    /// vetoed search never actually queries anything. The follow-up edit still lands.
    /// </summary>
    [Fact]
    public async Task Trace_WebStepVetoed_NoSearchSideEffect_EditionStillLands()
    {
        const string prompt = "Refactor the demo component so getItems() returns a copy of the items array.";
        var factory = new ToolSelectionScriptedClientFactory
        {
            ClassifierNeedsWeb = false,
            ClassifierReason = "repo-only refactoring task, no current external info required"
        };
        factory.Proposals.Add(new StepSpec("_web_search", "refactoring patterns for getters"));
        factory.Proposals.Add(new StepSpec(DemoTsRel, "Add getItems() returning a copy",
            CtorLine, CtorLineWithMethod, assertToken: "getItems()"));

        var controller = BuildController(factory);
        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt, new[] { DemoTsRel });

        Assert.True(complete, $"run should complete — plan: {plan?.Summary}; unmatched={factory.Unmatched.Count}\n{string.Join("\n", factory.UnmatchedSystems)}");

        // The veto + classifier reason reached the next planner turn.
        Assert.True(factory.PlannerUserPrompts.Count >= 2);
        Assert.Contains("does NOT need CURRENT EXTERNAL information", factory.PlannerUserPrompts[1]);
        Assert.Contains("repo-only refactoring task", factory.PlannerUserPrompts[1]);

        // OUTPUT: the corrected edit is what executed and changed the file.
        Assert.NotNull(plan);
        Assert.Equal(DemoTsRel, plan!.Plan.Single().File);
        Assert.Contains("getItems()", File.ReadAllText(Path.Combine(_projectRoot, DemoTsRel.Replace('/', Path.DirectorySeparatorChar))));

        // SIDE-EFFECT ABSENCE: the vetoed web step NEVER queried the search engine.
        Assert.Empty(factory.Gets);
        Assert.Empty(factory.Unmatched);
    }

    /// <summary>
    /// Attached-files guard: with a file attached, the planner dodges the real edit
    /// with a _create_file step. The guard rejects it ("do NOT create…"), the dodge
    /// file never appears, and the edit to the attached file still lands.
    /// </summary>
    [Fact]
    public async Task Trace_CreateFileDodgeRejected_NoDodgeFile_RealEditLands()
    {
        const string prompt = "In the demo component, add a public getItems() method that returns a copy of the items array.";
        var factory = new ToolSelectionScriptedClientFactory();
        factory.Proposals.Add(new StepSpec("_create_file", "demo-helper.ts",
            newString: "export class DemoHelper { }\n"));
        factory.Proposals.Add(new StepSpec(DemoTsRel, "Add getItems() returning a copy",
            CtorLine, CtorLineWithMethod, assertToken: "getItems()"));

        var controller = BuildController(factory);
        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt, new[] { DemoTsRel });

        Assert.True(complete, $"run should complete — plan: {plan?.Summary}; unmatched={factory.Unmatched.Count}\n{string.Join("\n", factory.UnmatchedSystems)}");

        Assert.True(factory.PlannerUserPrompts.Count >= 2);
        Assert.Contains("REJECTED", factory.PlannerUserPrompts[1]);
        Assert.Contains("do NOT create", factory.PlannerUserPrompts[1]);

        // OUTPUT: the attached file got the real edit.
        Assert.NotNull(plan);
        Assert.Equal(DemoTsRel, plan!.Plan.Single().File);
        Assert.Contains("getItems()", File.ReadAllText(Path.Combine(_projectRoot, DemoTsRel.Replace('/', Path.DirectorySeparatorChar))));

        // SIDE-EFFECT ABSENCE: the dodge file was never created.
        Assert.False(File.Exists(Path.Combine(_projectRoot, "demo-helper.ts")));
        Assert.Empty(factory.Unmatched);
    }

    /// <summary>
    /// Invented-symbol guard (hallucinated-removal): the planner "fixes" a method that was
    /// never in the file — removeLegacyCache is a fiction. PreEditValidation reports the
    /// removal target absent, and the guard rejects the edit BEFORE it lands, naming the
    /// file's REAL member inventory; the retry is the genuine edit, and the invented symbol
    /// never appears anywhere.
    /// </summary>
    [Fact]
    public async Task Trace_InventedSymbolRejected_MembersNamed_SymbolNeverAppears()
    {
        const string prompt = "In the demo component, add a public getItems() method that returns a copy of the items array.";
        var factory = new ToolSelectionScriptedClientFactory();
        // Proposal 1: the WRONG edit — "deletes" a method that does not exist in the file.
        factory.Proposals.Add(new StepSpec(DemoTsRel, "Delete the removeLegacyCache method from the demo component",
            "  removeLegacyCache() { }\n"));
        // Proposal 2 (retry): the right edit.
        factory.Proposals.Add(new StepSpec(DemoTsRel, "Add getItems() to the demo component",
            CtorLine, CtorLineWithMethod, assertToken: "getItems()"));

        var controller = BuildController(factory);
        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt, new[] { DemoTsRel });

        Assert.True(complete, $"run should complete — plan: {plan?.Summary}; unmatched={factory.Unmatched.Count}\n{string.Join("\n", factory.UnmatchedSystems)}");

        // The invented symbol was rejected BEFORE landing, with the file's real members named.
        Assert.True(factory.PlannerUserPrompts.Count >= 2,
            $"expected >= 2 planner turns (rejection + retry), got {factory.PlannerUserPrompts.Count}");
        Assert.Contains("REJECTED", factory.PlannerUserPrompts[1]);
        Assert.Contains("does NOT exist in the file", factory.PlannerUserPrompts[1]);
        Assert.Contains("constructor", factory.PlannerUserPrompts[1]); // the real member inventory

        // OUTPUT: the corrected edit is what executed and changed the file.
        Assert.NotNull(plan);
        Assert.Equal(DemoTsRel, plan!.Plan.Single().File);
        var final = File.ReadAllText(Path.Combine(_projectRoot, DemoTsRel.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Contains("getItems()", final);

        // SIDE-EFFECT ABSENCE: the invented symbol never appeared anywhere in the file.
        Assert.DoesNotContain("removeLegacyCache", final);
        Assert.Empty(factory.Gets);
        Assert.Empty(factory.Unmatched);
    }

    /// <summary>
    /// Invented-file guard: the planner "fixes" a file that does not exist (src/ghost.ts — a
    /// fiction). ValidateIncrementalStepAsync rejects the edit deterministically BEFORE it
    /// lands (not create-eligible, no similar file, nothing creates it earlier in the plan);
    /// the retry targets the REAL file, and the invented path never appears on disk.
    /// </summary>
    [Fact]
    public async Task Trace_InventedFileRejected_PathNeverAppears_RealEditLands()
    {
        const string prompt = "Fix the demo component so the title field reads 'demo-renamed' instead of 'demo'.";
        var factory = new ToolSelectionScriptedClientFactory();
        // Proposal 1: the WRONG target — an edit to an invented file that does not exist.
        factory.Proposals.Add(new StepSpec("src/ghost.ts", "Fix the title to demo-renamed",
            "  title = 'demo';", "  title = 'demo-renamed';"));
        // Proposal 2 (retry): the right tool — the edit on the REAL file.
        factory.Proposals.Add(new StepSpec(DemoTsRel, "Rename the title to demo-renamed",
            "  title = 'demo';", "  title = 'demo-renamed';", assertToken: "title = 'demo-renamed';"));

        var controller = BuildController(factory);
        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt, new[] { DemoTsRel });

        Assert.True(complete, $"run should complete — plan: {plan?.Summary}; unmatched={factory.Unmatched.Count}\n{string.Join("\n", factory.UnmatchedSystems)}");

        // The invented file was rejected BEFORE landing, with corrective guidance.
        Assert.True(factory.PlannerUserPrompts.Count >= 2,
            $"expected >= 2 planner turns (rejection + retry), got {factory.PlannerUserPrompts.Count}");
        Assert.Contains("REJECTED", factory.PlannerUserPrompts[1]);
        Assert.Contains("Cannot edit 'src/ghost.ts'", factory.PlannerUserPrompts[1]);
        Assert.Contains("does not exist", factory.PlannerUserPrompts[1]);

        // OUTPUT: the corrected edit on the real file executed.
        Assert.NotNull(plan);
        Assert.Equal(DemoTsRel, plan!.Plan.Single().File);
        var final = File.ReadAllText(Path.Combine(_projectRoot, DemoTsRel.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Contains("title = 'demo-renamed';", final);

        // SIDE-EFFECT ABSENCE: the invented file was never created.
        Assert.False(File.Exists(Path.Combine(_projectRoot, "src", "ghost.ts")));
        Assert.Empty(factory.Gets);
        Assert.Empty(factory.Unmatched);
    }

    /// <summary>
    /// Missing-web-search auto-inject: on an explicitly web task the planner refuses
    /// the web tools 3× (MAX_STEP_REGEN_ATTEMPTS); the pipeline auto-injects the
    /// _web_search step, the injected search ACTUALLY runs (side-effect trace: the
    /// search engine GET happens), its results reach the replan turn, and the
    /// follow-up edit lands.
    /// </summary>
    [Fact]
    public async Task Trace_MissingWebSearchAutoInjects_AndSearchActuallyRuns()
    {
        // Deliberately NOT a news-y prompt ("latest AI news" would route the search to the
        // NewsService digest) — this trace tests the missing-web-search auto-inject guard, so
        // the query stays on the plain DuckDuckGo path the scripted factory serves.
        const string prompt = "Search the web for the latest release notes for weaver and add a summary line to NOTES.md.";
        var factory = new ToolSelectionScriptedClientFactory();
        // 3× refusals (non-web steps) → missing-web-search guard → auto-inject.
        factory.Proposals.Add(new StepSpec("_command", "echo nothing"));
        factory.Proposals.Add(new StepSpec("_command", "echo nothing"));
        factory.Proposals.Add(new StepSpec("_command", "echo nothing"));
        // The follow-up edit after the injected search.
        factory.Proposals.Add(new StepSpec(NotesRel, "Add a summary line per the web results",
            "- v1.0: initial release", NotesUpdated, assertToken: "v2.0: agent version bump"));

        var controller = BuildController(factory);
        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt, Array.Empty<string>());

        Assert.True(complete, $"run should complete — plan: {plan?.Summary}; unmatched={factory.Unmatched.Count}\n{string.Join("\n", factory.UnmatchedSystems)}");

        // The three refusals were rejected with the web-need feedback.
        Assert.True(factory.PlannerUserPrompts.Count >= 4,
            $"expected >= 4 planner turns (3 rejections + edit + complete), got {factory.PlannerUserPrompts.Count}");
        foreach (var idx in new[] { 1, 2 })
            Assert.Contains("Use a \"_web_search\" step", factory.PlannerUserPrompts[idx]);

        // OUTPUT: the plan is exactly [injected _web_search, edit NOTES.md].
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_web_search", NotesRel }, plan!.Plan.Select(s => s.File).ToArray());
        Assert.Contains("v2.0: agent version bump", File.ReadAllText(Path.Combine(_projectRoot, NotesRel)));

        // SIDE EFFECTS: the injected search REALLY ran (search-engine GET happened — the
        // "quietly refuses the web tool" trace would show NO GET), its output was harvested
        // into ### WEB RESULTS, and the follow-up planner turn (the edit proposal) SAW it.
        Assert.True(factory.Gets.Any(u => u.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase)),
            $"auto-injected search must execute; gets=[{string.Join("; ", factory.Gets)}]");
        var searchResult = allSteps.OfType<Dictionary<string, object?>>()
            .Single(r => r.GetValueOrDefault("type")?.ToString() == "_web_search");
        Assert.Equal("done", searchResult.GetValueOrDefault("status")?.ToString());
        Assert.Contains("https://example.com/alphafold3", searchResult.GetValueOrDefault("output")?.ToString() ?? "");
        // The planner turn AFTER the injected search executed must see the harvested results.
        Assert.True(factory.PlannerUserPrompts.Count >= 4,
            $"expected the post-search planner turn; calls=[{string.Join(",", factory.Calls)}]");
        var postSearchPrompt = factory.PlannerUserPrompts[3];
        Assert.Contains("### WEB RESULTS", postSearchPrompt);
        Assert.Contains("https://example.com/alphafold3", postSearchPrompt);
        Assert.Empty(factory.Unmatched);
    }

    /// <summary>
    /// FETCH-COMMAND AUTO-INJECT LOOP-GUARD: the planner keeps answering a web task
    /// with URL-fetching _command steps (the "api.current.ai" failure mode) instead
    /// of a web tool. Each one is rejected by the fetch-in-command guard; on the
    /// regen cap (MAX_STEP_REGEN_ATTEMPTS rejections) with at least two consecutive
    /// fetch-command rejections, the planner auto-injects a _web_search step — and
    /// it must inject EXACTLY ONE, never re-inject per rejection. The injected search
    /// really runs, its results land in the follow-up planner turn, and the web task
    /// completes with the edit.
    /// </summary>
    [Fact]
    public async Task Trace_FetchCommandLoopGuard_AutoInjectsExactlyOneSearch()
    {
        const string prompt = "Check the latest weaver release notes online and note the version in NOTES.md.";
        // ClassifierNeedsWeb=false keeps the web-need gate out of the way (it would
        // reject the commands with its own feedback first); the fetch-in-command
        // guard is keyword+shape deterministic and fires regardless.
        var factory = new ToolSelectionScriptedClientFactory { ClassifierNeedsWeb = false };
        // 3× URL-fetching _command proposals — each rejected by the fetch-in-command guard.
        factory.Proposals.Add(new StepSpec("_command",
            "Invoke-RestMethod https://api.current.ai/releases | ConvertTo-Json | Set-Content notes.json"));
        factory.Proposals.Add(new StepSpec("_command",
            "curl -s https://api.current.ai/releases -o notes.json"));
        factory.Proposals.Add(new StepSpec("_command",
            "Invoke-WebRequest https://api.current.ai/releases -OutFile notes.json"));
        // The follow-up edit after the auto-injected search.
        factory.Proposals.Add(new StepSpec(NotesRel, "Add the version note per the web results",
            "- v1.0: initial release", NotesUpdated, assertToken: "v2.0: agent version bump"));

        var controller = BuildController(factory);
        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt, Array.Empty<string>());

        Assert.True(complete, $"run should complete — plan: {plan?.Summary}; unmatched={factory.Unmatched.Count}\n{string.Join("\n", factory.UnmatchedSystems)}");

        // Every refusal carried the fetch-command veto text to the next planner turn.
        Assert.True(factory.PlannerUserPrompts.Count >= 4,
            $"expected >= 4 planner turns (3 rejections + injected search + edit), got {factory.PlannerUserPrompts.Count}");
        Assert.Contains("Fetching web content is NOT a shell command", factory.PlannerUserPrompts[1]);
        Assert.Contains("Fetching web content is NOT a shell command", factory.PlannerUserPrompts[2]);

        // OUTPUT: exactly ONE _web_search step (the auto-inject must not re-fire per
        // rejection), followed by the repo edit — no _command step ever made the plan.
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_web_search", NotesRel }, plan!.Plan.Select(s => s.File).ToArray());
        Assert.Single(plan.Plan, s => s.File == "_web_search");

        // SIDE EFFECTS: the injected search REALLY ran (duckduckgo GET — the wrong
        // path would show ZERO web GETs since the commands were rejected), its output
        // was harvested into ### WEB RESULTS for the follow-up planner turn, and the
        // wrong commands never ran (no notes.json, and Gets contains no invented API).
        Assert.True(factory.Gets.Any(u => u.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase)),
            $"auto-injected search must execute; gets=[{string.Join("; ", factory.Gets)}]");
        Assert.DoesNotContain(factory.Gets, u => u.Contains("api.current.ai", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(_projectRoot, "notes.json")));
        var postSearchPrompt = factory.PlannerUserPrompts[3];
        Assert.Contains("### WEB RESULTS", postSearchPrompt);
        Assert.Contains("https://example.com/alphafold3", postSearchPrompt);
        Assert.Contains("v2.0: agent version bump", File.ReadAllText(Path.Combine(_projectRoot, NotesRel)));
        Assert.Empty(factory.Unmatched);
    }

    /// <summary>
    /// GUARD-INTERACTION trace (the audit's §5.2 gap): a web-needing OS task trips
    /// BOTH deterministic guards on one run. Proposal 1 (a URL-fetching _command) is
    /// rejected by the fetch-in-command veto; proposal 2 (a repo edit) is rejected by
    /// the OS-task veto (pure OS task — repo source files are NOT the target); the
    /// corrected _command (a real write, no URL) then executes and completes the run.
    /// Asserts each veto reached the next planner turn with its own auditable reason.
    /// </summary>
    [Fact]
    public async Task Trace_GuardInteraction_FetchVetoThenOsVeto_RealCommandLands()
    {
        // "check … online" is a web HINT but not an explicit web command ("fetch …
        // online" would be — the web-need gate then mandates the web tools and
        // preempts BOTH guards). ClassifierNeedsWeb=false keeps the gate quiet so the
        // fetch-in-command guard (keyword+shape deterministic) rejects proposal 1 and
        // the OS-task veto (pure OS task — no repo hints) rejects proposal 2.
        // The demanded OS output pins an ABSOLUTE path under this test's temp root (the
        // file the corrected command writes into the project dir). Pinning the real Desktop
        // would make the deterministic OS-output check depend on real-world state — the run
        // only completes when Desktop\ai_article_data.txt (DefaultDumpFileName) happens to
        // exist on the machine, which made this test pass/fail on desktop contents.
        var osTarget = Path.Combine(_projectRoot, "release-version.txt");
        var prompt = $"Check the latest weaver release version online and save the version to a file at \"{osTarget}\".";
        var factory = new ToolSelectionScriptedClientFactory { ClassifierNeedsWeb = false };
        // 1) Wrong tool #1: URL-fetching _command → fetch-in-command veto.
        factory.Proposals.Add(new StepSpec("_command",
            "Invoke-RestMethod https://api.current.ai/releases | ConvertTo-Json | Set-Content out.json"));
        // 2) Wrong tool #2: repo edit → OS-task veto (Desktop target, not the repo).
        factory.Proposals.Add(new StepSpec(NotesRel, "Record the version in the release notes",
            "- v1.0: initial release", NotesUpdated));
        // 3) The right tool: a real OS write.
        factory.Proposals.Add(new StepSpec("_command", "echo v2.1 > release-version.txt"));

        var controller = BuildController(factory);
        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt, Array.Empty<string>());

        Assert.True(complete, $"run should complete — plan: {plan?.Summary}; unmatched={factory.Unmatched.Count}\n{string.Join("\n", factory.UnmatchedSystems)}");

        // Both vetoes reached the next planner turn, each with its own reason.
        Assert.True(factory.PlannerUserPrompts.Count >= 3,
            $"expected >= 3 planner turns (2 rejections + corrected command), got {factory.PlannerUserPrompts.Count}");
        Assert.Contains("Fetching web content is NOT a shell command", factory.PlannerUserPrompts[1]);
        // Prompt 2 carries BOTH vetoes (the rejection list is cumulative): the OS-task
        // veto for the repo edit, plus the earlier fetch veto still visible.
        Assert.Contains("operates on the OS filesystem", factory.PlannerUserPrompts[2]);

        // The corrected tool is what got planned and executed — exactly one _command.
        Assert.NotNull(plan);
        var cmd = Assert.Single(plan!.Plan, s => s.File == "_command");
        Assert.Equal("echo v2.1 > release-version.txt", cmd.Change);
        Assert.False(File.Exists(Path.Combine(_projectRoot, "out.json")), "the URL-fetch command must never run");
        Assert.Contains("release-version.txt", Directory.GetFiles(_projectRoot).Select(Path.GetFileName));
        Assert.Contains("v2.1", File.ReadAllText(Path.Combine(_projectRoot, "release-version.txt")));
        Assert.Empty(factory.Unmatched);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Harness (mirrors AdversarialUserScenarioTests / WebTaskInterleavedPipelineIntegrationTests)
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task<(List<object> allSteps, AgentPlan? plan, bool complete)> InvokeOrchestrate(
        AgentController controller, string prompt, string[] attachedFiles)
    {
        // Hermetic runs: the scripted edits must land verbatim — an external formatter
        // (prettier via npx) rewriting quote style would break the exact-token assertions
        // and the follow-up edits' anchors, and only on machines where npx exists.
        CodeFormatterService.SuppressExternalFormattersForTests.Value = true;
        var method = typeof(AgentController).GetMethod("Orchestrate", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Orchestrate not found");
        var task = (Task<(List<object> allSteps, AgentPlan? plan, bool complete)>)method.Invoke(controller, new object?[]
        {
            prompt, _projectRoot, /*emitSse*/ false, CancellationToken.None,
            /*attachedFiles*/ attachedFiles.ToList(),
            /*skipContextReview*/ false, /*steeringContext*/ null, /*skipQualityCheck*/ false,
            /*existingPlan*/ null, /*completedStepIndices*/ null, /*cardId*/ null,
            /*createTests*/ false, /*buildCommands*/ null, /*webResults*/ null
        })!;
        return await task;
    }

    private AgentController BuildController(ToolSelectionScriptedClientFactory factory)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Editor:WorkspaceRoot"] = _base,
                ["Editor:DisableLLMRetries"] = "true"
            })
            .Build();
        var controller = (AgentController)RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        SetField(controller, "_clientFactory", factory);
        SetField(controller, "_config", config);
        SetField(controller, "_env", new FakeWebHostEnvironment(_projectRoot));
        SetField(controller, "_db", _db);
        SetField(controller, "_configFile", new ConfigFileService(_db));
        SetField(controller, "_terminal", new TerminalService(new ConfigFileService(_db)));
        SetField(controller, "_fileHints", new FileHintsManager(_db));
        SetField(controller, "_boardData", new BoardDataService(_db, NullLogger<BoardDataService>.Instance));
        SetField(controller, "_emailService", new EmailService(new ConfigFileService(_db)));
        SetField(controller, "_push", new PushNotificationService(_db));
        SetField(controller, "_editKnowledge", new EditKnowledgeService(_db));
        // Hermetic runtime probe: nothing installed (no real process spawns in tests).
        SetField(controller, "_runtimeProbe", new RuntimeProbeService((_, _, _) => (-1, "", "")));
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

    /// <summary>
    /// Scripted fake LLM + fake web stack. The planner is driven by a per-test queue
    /// of step proposals (when the queue is empty the planner says planComplete). Web
    /// GETs are intercepted: searches return realistic DuckDuckGo-shaped results (so
    /// outputs are harvestable), fetches return a small body, and every web-relevant
    /// GET is recorded into <see cref="Gets"/> — the side-effect trace used to prove
    /// which web tool (if any) actually ran. Any LLM call no route matches lands in
    /// <see cref="Unmatched"/> and fails the test.
    /// </summary>
    internal sealed class ToolSelectionScriptedClientFactory : IHttpClientFactory, IDisposable
    {
        public readonly List<string> Calls = new();
        public readonly List<string> Unmatched = new();
        public readonly List<string> UnmatchedSystems = new();
        public readonly List<string> PlannerUserPrompts = new();
        public readonly List<string> ReplanPrompts = new();
        public readonly List<string> Gets = new();

        /// <summary>Queue of planner step proposals; exhausted → planComplete.</summary>
        public readonly List<StepSpec> Proposals = new();
        /// <summary>Steps the web-results replan should return (only used when a web step is followed by a file step).</summary>
        public readonly List<StepSpec> ReplanSpecs = new();

        /// <summary>Answer the web-need classifier returns; true by default.</summary>
        public bool ClassifierNeedsWeb = true;
        /// <summary>Reason the classifier reports (surfaced as the veto reason).</summary>
        public string ClassifierReason = "";

        private int _plannerCalls;
        private int _replanCalls;

        public HttpClient CreateClient(string name) => new(new ScriptedHandler(this));
        public HttpClient CreateClient() => CreateClient("default");
        public void Dispose() { }

        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly ToolSelectionScriptedClientFactory _owner;
            public ScriptedHandler(ToolSelectionScriptedClientFactory owner) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var resp = BuildResponse(request);
                return Task.FromResult(resp);
            }

            private HttpResponseMessage BuildResponse(HttpRequestMessage request)
            {
                if (request.Method == HttpMethod.Get)
                {
                    var url = request.RequestUri?.ToString() ?? "";
                    var host = request.RequestUri?.Host ?? "";
                    // Record only the web tools' requests — skip the connectivity probes
                    // (/api/tags, /slots, ...) so Gets is a clean side-effect trace.
                    if (host.Contains("duckduckgo", StringComparison.OrdinalIgnoreCase) ||
                        host.Contains("example.com", StringComparison.OrdinalIgnoreCase))
                    {
                        lock (_owner.Gets) _owner.Gets.Add(url);
                    }
                    if (host.Contains("duckduckgo", StringComparison.OrdinalIgnoreCase))
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
                    return Json(new { });
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
                // ORDER MATTERS: the incremental planner first, then the classic/replan
                // planners, then the fixed classifiers/verifiers.
                if (system.Contains("building a code-change plan ONE STEP AT A TIME", StringComparison.Ordinal) ||
                    system.Contains("senior autonomous coding agent building a code-change plan", StringComparison.Ordinal))
                {
                    lock (_owner.PlannerUserPrompts) _owner.PlannerUserPrompts.Add(user);
                    var n = Interlocked.Increment(ref _owner._plannerCalls);
                    var idx = n - 1;
                    lock (_owner.Proposals)
                    {
                        if (idx < _owner.Proposals.Count)
                            return (StepJson(_owner.Proposals[idx]), "planner-step");
                    }
                    return ("{\"planComplete\": true, \"completionReason\": \"plan complete\"}", "planner-complete");
                }
                if (system.Contains("Revise remaining execution steps. NEVER remove existing steps.", StringComparison.Ordinal))
                {
                    lock (_owner.ReplanPrompts) _owner.ReplanPrompts.Add(user);
                    var i = Interlocked.Increment(ref _owner._replanCalls) - 1;
                    lock (_owner.ReplanSpecs)
                    {
                        if (i < _owner.ReplanSpecs.Count)
                            return (PlanJson(_owner.ReplanSpecs[i]), "replan");
                    }
                    return ("{\"plan\":[]}", "replan-empty");
                }
                if (system.Contains("Plan the complete minimum set of steps", StringComparison.Ordinal))
                    return (PlanJson(new StepSpec(NotesRel, "fallback classic planner step", "- v1.0: initial release", NotesUpdated)), "planner-classic");
                if (system.Contains("You are a project architect reviewing a file tree for a coding agent", StringComparison.Ordinal))
                    return ("{\"files\": [\"" + DemoTsRel + "\", \"" + NotesRel + "\"], \"architectureNote\": \"A TypeScript/Angular demo app with notes.\"}", "architect-select");
                if (system.Contains("You are a project architect. Given a project file tree and the user's task, write ONE short sentence", StringComparison.Ordinal))
                    return ("A TypeScript/Angular demo app with notes.", "architect-note");
                if (system.Contains("You extract a short checklist of literal, testable requirements", StringComparison.Ordinal))
                    return ("{\"requirements\": [\"satisfy the prompt\"]}", "checklist");
                if (system.Contains("You are a strict plan-coherence validator", StringComparison.Ordinal))
                    return ("{\"valid\": true}", "plan-validator");
                if (system.Contains("You are a strict task classifier. Output ONLY the requested JSON.", StringComparison.Ordinal))
                {
                    var reason = string.IsNullOrWhiteSpace(_owner.ClassifierReason) ? "classifier verdict" : _owner.ClassifierReason;
                    return _owner.ClassifierNeedsWeb
                        ? ("{\"needsWeb\": true, \"reason\": \"" + reason + "\", \"query\": \"web task query\"}", "web-classifier")
                        : ("{\"needsWeb\": false, \"reason\": \"" + reason + "\", \"query\": \"\"}", "web-classifier");
                }
                if (user.Contains("Decide: keep or abandon", StringComparison.Ordinal))
                    return ("{\"decision\": \"keep\", \"reason\": \"verified\", \"score\": 95, \"needsExtraStep\": false}", "verify");
                if (user.Contains("Evaluate the code changes against the ORIGINAL TASK ONLY", StringComparison.Ordinal))
                    return ("{\"complete\": true, \"reason\": \"task satisfied\", \"issues\": []}", "assess");
                if (system.Contains("meticulous code reviewer verifying if a task is fully complete", StringComparison.Ordinal))
                    return ("{\"complete\": true, \"reason\": \"done\", \"issues\": []}", "post-verify");
                if (system.Contains("You detect code cohesion issues after an edit. Output ONLY JSON.", StringComparison.Ordinal))
                    return ("{\"issues\": []}", "cohesion");
                if (system.Contains("You extract file paths from instructions", StringComparison.Ordinal))
                    return (user.Trim().Trim('"', '\'', '`', ' '), "path-extract");
                lock (_owner.Unmatched) _owner.Unmatched.Add(system.Length > 80 ? system[..80] : system);
                lock (_owner.UnmatchedSystems) _owner.UnmatchedSystems.Add("SYSTEM: " + system + "\nUSER: " + user + "\n---");
                return ("", "unknown");
            }

            private static string StepJson(StepSpec spec)
            {
                var payload = new Dictionary<string, object?>
                {
                    ["thinking"] = "Single atomic step.",
                    ["planComplete"] = false,
                    ["step"] = new Dictionary<string, object?>
                    {
                        ["file"] = spec.File,
                        ["change"] = spec.Change,
                        ["oldString"] = spec.OldString,
                        ["newString"] = spec.NewString
                    }
                };
                return JsonSerializer.Serialize(payload);
            }

            private static string PlanJson(StepSpec spec)
            {
                var payload = new Dictionary<string, object?>
                {
                    ["plan"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["file"] = spec.File,
                            ["change"] = spec.Change,
                            ["oldString"] = spec.OldString,
                            ["newString"] = spec.NewString
                        }
                    }
                };
                return JsonSerializer.Serialize(payload);
            }

            private static HttpResponseMessage Json(object obj)
                => new(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json")
                };

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
