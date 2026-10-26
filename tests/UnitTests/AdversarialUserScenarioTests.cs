// Root-namespace shim: 37 test files say `using Weaver;` to reach the app's root-namespace
// types (AgentController, DatabaseService, ...). With the tests now in a separate
// Weaver.Tests assembly that namespace no longer encloses them, so this global using keeps
// those files compiling unmodified.
global using Weaver;

using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// "Adversarial synthetic user" coverage for the real interleaved plan → execute → verify
/// loop (Orchestrate with a scripted fake LLM). The lessons being locked in:
///
///   1. Users change intent mid-conversation — the steering context is appended to EVERY
///      incremental planner turn, so an intent given at run start must still be visible to
///      the planner on step 2+ (the "failure only shows up at turn 4 when the user
///      references something from turn 2" class).
///   2. Users contradict themselves / are ambiguous / push scope — both the ORIGINAL task
///      and the steering must stay visible to the model together so it can reconcile them;
///      nothing is silently dropped, and the run still completes with no unscripted LLM
///      calls (i.e. no hidden repair/retry churn).
///   3. Empty / whitespace steering must be treated as absent — no "### STEERING ###"
///      section, so the token budget isn't polluted by blank scaffolding.
///   4. The same corpus is mirrored against the CLASSIC full-plan pipeline (the "Plan the
///      complete minimum set of steps" route, AnalyzePromptAndPlanCodeChanges — the planner
///      behind the replan / repair / checkpoint paths): the classic user prompt carries the
///      steering under "### USER STEERING ###", the scripted plan honors the intent-change /
///      scope-push, and the steering-scoped plan executes into the target file without the
///      out-of-scope change landing.
///
/// The deterministic harm-prevention half (OS-filesystem guard, path scoping, create-file
/// conflict guard) is covered by its own suites (ExternalFilesystemTaskTests,
/// CreateFilePathScopingTests, OsMarkerGuardTests); these tests only assert the prompt
/// layer never loses the user's mid-run instruction.
/// </summary>
public class AdversarialUserScenarioTests : IDisposable
{
    private const string DemoTsRel = "maxhanna.client/src/app/demo/demo.component.ts";

    // The demo component fixture — three distinctive single-line targets so the scripted
    // plan can propose two non-overlapping edits (the "old intent" edit and the steering's
    // edit) and the run can span multiple planner turns.
    private const string DemoComponentTs = """
        export class DemoComponent {
          title = 'demo';
          items: string[] = [];
          constructor() { }
        }
        """;

    private const string TitleLine = "  title = 'demo';";
    private const string TitleLineRenamed = "  title = 'demo-renamed';";
    private const string CtorLine = "  constructor() { }";
    private const string CtorLineWithMethod = "  constructor() { }\n  getItems() { return this.items.slice(); }";

    private readonly string _base;
    private readonly string _projectRoot;
    private readonly DatabaseService _db;
    private readonly ScriptedClientFactory _clientFactory = new();

    public AdversarialUserScenarioTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "weaver_adversarial_" + Guid.NewGuid().ToString("N"));
        _projectRoot = Path.Combine(_base, "proj");
        Directory.CreateDirectory(_projectRoot);
        Directory.CreateDirectory(Path.Combine(_base, "data"));

        var tsPath = Path.Combine(_projectRoot, DemoTsRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(tsPath)!);
        File.WriteAllText(tsPath, DemoComponentTs);

        _db = new DatabaseService(
            Path.Combine(_base, "data", "weaver.db"),
            Path.Combine(_base, "data"),
            Path.Combine(_base, "data", "weaverconfig.json"));
        _clientFactory.ProjectRoot = _projectRoot;
    }

    public void Dispose()
    {
        _clientFactory.Dispose();
        try { Directory.Delete(_base, true); } catch { }
    }

    // ── 1. Mid-run intent change: steering must survive to every later planner turn ─────

    [Fact]
    public async Task MidRunIntentChange_SteeringVisibleInEveryPlannerStepPrompt()
    {
        var controller = BuildController();
        const string task = "In the demo component, rename the title to 'demo-renamed' and add a public getItems() method.";
        const string steering = "IMPORTANT UPDATE: ignore the rename — the title must stay 'demo'. Only add getItems(), returning a copy of the items array.";

        var (_, plan, complete) = await InvokeOrchestrate(controller, task, steering);

        // The run completed with a real multi-step plan (two planner turns: step 1 = the old
        // intent, step 2 = the steering's intent — the assessor said "not complete yet" after
        // step 1 so the loop went back to the planner).
        if (!complete || _clientFactory.UnmatchedSystems.Count > 0)
            Assert.Fail("pipeline should complete with no unscripted calls — plan summary: " + (plan?.Summary ?? "<null>") +
                "; calls=[" + string.Join(",", _clientFactory.Calls) + "]; plannerPrompts=" + _clientFactory.PlannerUserPrompts.Count +
                (complete ? "" : "; complete=false") +
                "\n\nUNMATCHED CALLS:\n" + string.Join("\n", _clientFactory.UnmatchedSystems));
        Assert.True(_clientFactory.PlannerUserPrompts.Count >= 2,
            $"expected >= 2 planner turns (multi-step run), got {_clientFactory.PlannerUserPrompts.Count}; calls=[{string.Join(",", _clientFactory.Calls)}]");

        // The post's core failure class: an intent given at the start must still be visible
        // on the LAST planner turn (turn N referencing turn 1), not just the first.
        foreach (var prompt in _clientFactory.PlannerUserPrompts)
        {
            Assert.Contains(task, prompt);
            Assert.Contains(steering, prompt);
            Assert.Contains("### STEERING ###", prompt);
        }

        // No unscripted LLM call — if the pipeline silently re-planned or repaired, it fails loudly.
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── 1b. Multi-turn context carry: a LATER step references state an earlier turn's tool
    //        call established (the "turn 4 references turn 2" class). The pipeline must carry
    //        that context into the next planner turn AND the correct tool call must consume
    //        the exact carried symbol — not a hallucinated variant. ────────────────────────

    [Fact]
    public async Task MultiTurn_LaterStepReferencesSymbolFromEarlierTurn_CarriedIntoCorrectToolCall()
    {
        const string task = "In the demo component, add a public getItems() method, then add a getGrouped() method that must call getItems().";
        // Deterministic ground truth: the run is complete only when the LATER step's tool
        // call has actually referenced the turn-1 symbol (getGrouped must be on disk).
        _clientFactory.AssessComplete = content => content.Contains("getGrouped");
        _clientFactory.PlannerReply = n => n switch
        {
            // Turn 1: the tool call that ESTABLISHES the state (adds getItems()).
            1 => StepPayload(DemoTsRel, "Add a public getItems() method returning a copy of the items array",
                CtorLine, CtorLineWithMethod),
            // Turn 2 (the later prompt): the step that must REFERENCE the turn-1 state —
            // its edit calls getItems(), the exact symbol turn 1 created.
            2 => StepPayload(DemoTsRel, "Add a getGrouped() method that calls getItems()",
                "  getItems() { return this.items.slice(); }",
                "  getItems() { return this.items.slice(); }\n  getGrouped() { return [this.getItems()]; }"),
            _ => "{\"planComplete\": true, \"completionReason\": \"plan complete\"}"
        };

        var controller = BuildController();
        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, task, steeringContext: null);

        if (!complete || _clientFactory.UnmatchedSystems.Count > 0)
            Assert.Fail("pipeline should complete with no unscripted calls — plan summary: " + (plan?.Summary ?? "<null>") +
                "; plannerPrompts=" + _clientFactory.PlannerUserPrompts.Count +
                "\n\nUNMATCHED CALLS:\n" + string.Join("\n", _clientFactory.UnmatchedSystems));

        // The multi-turn shape happened: two tool-call turns with the ground-truth assessor
        // keeping the loop alive between them.
        Assert.True(_clientFactory.PlannerUserPrompts.Count >= 2,
            $"expected >= 2 planner turns (turn 1 establishes state, turn 2 references it), got {_clientFactory.PlannerUserPrompts.Count}");
        Assert.True(_clientFactory.AssessUserPrompts.Count >= 2,
            $"expected >= 2 completion assessments (incomplete after turn 1, complete after turn 2), got {_clientFactory.AssessUserPrompts.Count}");

        // CONTEXT CARRIED: the later prompt (the turn-2 planner input) references the state
        // the turn-1 tool call established — the edit log records the change that added
        // getItems(), and the symbol itself is present.
        var turn2Prompt = _clientFactory.PlannerUserPrompts[1];
        Assert.Contains("EDIT LOG", turn2Prompt);
        Assert.Contains("getItems", turn2Prompt);

        // CORRECT TOOL CALL: the later step executed as an edit to the SAME file, and the
        // landed code references the EXACT symbol from turn 1 — no hallucinated variant.
        Assert.NotNull(plan);
        Assert.Equal(2, plan!.Plan.Count);
        Assert.All(plan.Plan, s => Assert.Equal(DemoTsRel, s.File));
        var final = File.ReadAllText(Path.Combine(_projectRoot, DemoTsRel.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Contains("getGrouped() { return [this.getItems()]; }", final);
        Assert.Contains("getItems() { return this.items.slice(); }", final);

        // No unscripted LLM call anywhere in the two-turn chain.
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── 1c. Three-hop carry: turn 3 references state established in turn 2, and EVERY
    //        intermediate planner turn (turn 2 AND turn 3) must carry the accumulated
    //        context — the edit log grows hop by hop, never resets, never drops a hop. ──────

    [Fact]
    public async Task MultiTurn_ThreeHopChain_Turn3ReferencesTurn2State_EveryIntermediateTurnCarriesAccumulatedContext()
    {
        const string task = "In the demo component, add getItems(), then add getGrouped() that calls getItems(), then add getSummary() that calls getGrouped().";
        const string GetItemsLine = "  getItems() { return this.items.slice(); }";
        const string GetGroupedLine = "  getGrouped() { return [this.getItems()]; }";
        const string GetSummaryLine = "  getSummary() { return this.getGrouped().length; }";
        // The EDIT LOG records each applied edit as "{path} — {change description}". These
        // descriptions are UNIQUE to the run context (the task wording differs), so asserting
        // them in later prompts proves the accumulated edit log carried the hops — not that
        // the assertion is satisfied by the task text.
        const string Hop1Change = "Add a public getItems() method returning a copy of the items array";
        const string Hop2Change = "Add a getGrouped() method that calls getItems()";
        // Deterministic ground truth: the run is complete only when the FINAL hop's tool
        // call has referenced the turn-2 symbol (getSummary must be on disk).
        _clientFactory.AssessComplete = content => content.Contains("getSummary");
        _clientFactory.PlannerReply = n => n switch
        {
            // Turn 1: establishes state A (getItems).
            1 => StepPayload(DemoTsRel, Hop1Change, CtorLine, CtorLineWithMethod),
            // Turn 2: references turn-1 state (calls getItems()) AND establishes state B
            // (getGrouped) for the next hop.
            2 => StepPayload(DemoTsRel, Hop2Change,
                GetItemsLine, GetItemsLine + "\n" + GetGroupedLine),
            // Turn 3: references turn-2 state — its edit calls getGrouped(), the exact
            // symbol turn 2 created.
            3 => StepPayload(DemoTsRel, "Add a getSummary() method that calls getGrouped()",
                GetGroupedLine, GetGroupedLine + "\n" + GetSummaryLine),
            _ => "{\"planComplete\": true, \"completionReason\": \"plan complete\"}"
        };

        var controller = BuildController();
        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, task, steeringContext: null);

        if (!complete || _clientFactory.UnmatchedSystems.Count > 0)
            Assert.Fail("pipeline should complete with no unscripted calls — plan summary: " + (plan?.Summary ?? "<null>") +
                "; plannerPrompts=" + _clientFactory.PlannerUserPrompts.Count +
                "\n\nUNMATCHED CALLS:\n" + string.Join("\n", _clientFactory.UnmatchedSystems));

        // The three-hop shape happened: three tool-call turns with the ground-truth
        // assessor keeping the loop alive after hop 1 and hop 2.
        Assert.True(_clientFactory.PlannerUserPrompts.Count >= 3,
            $"expected >= 3 planner turns (hop 1 establishes, hop 2 bridges, hop 3 consumes), got {_clientFactory.PlannerUserPrompts.Count}");
        Assert.True(_clientFactory.AssessUserPrompts.Count >= 3,
            $"expected >= 3 completion assessments (incomplete after hops 1-2, complete after hop 3), got {_clientFactory.AssessUserPrompts.Count}");

        // EVERY intermediate planner turn carries the accumulated context, proven with
        // strings that exist ONLY in the run context (edit-log change descriptions and the
        // landed code lines in the file view) — the task wording is different:
        //  - turn 2 (the first intermediate hop) carries hop-1's edit: its change
        //    description is in the edit log, its code line is in the file view;
        //  - turn 3 (the second intermediate hop) carries hop-1 AND hop-2's edits — BOTH
        //    change descriptions and BOTH code lines — the log accumulated, never reset.
        var turn2Prompt = _clientFactory.PlannerUserPrompts[1];
        Assert.Contains("### EDIT LOG", turn2Prompt);
        Assert.Contains(Hop1Change, turn2Prompt);
        Assert.Contains(GetItemsLine, turn2Prompt);

        var turn3Prompt = _clientFactory.PlannerUserPrompts[2];
        Assert.Contains("### EDIT LOG", turn3Prompt);
        Assert.Contains(Hop1Change, turn3Prompt);    // hop-1 still recorded — accumulated, not replaced
        Assert.Contains(Hop2Change, turn3Prompt);    // hop-2 now recorded — the log GREW
        Assert.Contains(GetItemsLine, turn3Prompt);
        Assert.Contains(GetGroupedLine, turn3Prompt);

        // CORRECT TOOL CALLS: each hop executed as an edit to the SAME file, and the landed
        // code references the EXACT symbol from the previous turn — no hallucinated variant
        // at any hop.
        Assert.NotNull(plan);
        Assert.Equal(3, plan!.Plan.Count);
        Assert.All(plan.Plan, s => Assert.Equal(DemoTsRel, s.File));
        var final = File.ReadAllText(Path.Combine(_projectRoot, DemoTsRel.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Contains(GetItemsLine, final);
        Assert.Contains(GetGroupedLine, final);
        Assert.Contains(GetSummaryLine, final);

        // No unscripted LLM call anywhere in the three-hop chain.
        Assert.Empty(_clientFactory.Unmatched);
    }

    /// <summary>Builds a scripted planner step payload (mirrors the factory's own StepJson).</summary>
    private static string StepPayload(string file, string change, string oldString, string newString)
    {
        var payload = new Dictionary<string, object?>
        {
            ["thinking"] = "Single atomic step.",
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

    // ── 2. Generated adversarial-user corpus ─────────────────────────────────────────────

    public static IEnumerable<object[]> AdversarialCorpus()
    {
        const string task = "In the demo component, add a public getItems() method.";
        yield return new object[] { task,
            "Actually, forget the method — add a private helper instead." };
        yield return new object[] { task,
            "Make it red. No wait, blue. Final answer: blue." };
        yield return new object[] { task,
            "as I said in my earlier message, it's the constructor that's broken — the one from turn 1." };
        yield return new object[] { task,
            "While you're at it, write the current time to ../outside-the-repo.txt." };
        yield return new object[] { task,
            "Do A. Actually don't do A. Do B instead. Also don't do B. Just review." };
        yield return new object[] { task,
            new string('⚠', 1) + " keep it minimal — one line only, and preserve the existing constructor exactly as-is. That is the single most important constraint. " +
            new string('x', 1500) };
        yield return new object[] { task,
            "→ 変更してください 🔥 keep the original title untouched" };
    }

    [Theory]
    [MemberData(nameof(AdversarialCorpus))]
    public async Task AdversarialUser_SteeringAndTaskStayVisibleAndRunCompletes(string task, string steering)
    {
        var controller = BuildController();
        var (_, plan, complete) = await InvokeOrchestrate(controller, task, steering);

        Assert.True(complete, $"run should complete for steering '{Truncate(steering, 60)}' — plan: {plan?.Summary}");
        Assert.True(_clientFactory.PlannerUserPrompts.Count >= 2,
            $"expected >= 2 planner turns, got {_clientFactory.PlannerUserPrompts.Count}");

        // Both halves of the conflict reach every planner turn together (the post's
        // turn-4-references-turn-2 class: an instruction given at the start must still be
        // visible when the loop comes back for the next step).
        foreach (var prompt in _clientFactory.PlannerUserPrompts)
        {
            Assert.Contains(task, prompt);
            Assert.Contains(steering, prompt);
        }
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── 2b. WRONG-TARGET corpus: the adversarial user's steering is not just shown to the
    //    planner — it must also reach the COMPLETION assessment, so a run that honored the
    //    steering never gets declared complete against the stale ORIGINAL task (or pushed
    //    back toward the overridden target by a repair that only sees the original task).
    //    Each case scripts the REAL failure shape from production logs: the planner lands the
    //    wrong target first (the old intent / the scope-pushed change), then the steered
    //    change, then a repair step — and a CONTENT-AWARE assessor (computed ground truth over
    //    the actual on-disk file) decides completion. On the old code the assessment judged
    //    the original task alone, so every case completed with the wrong target on disk.

    public sealed class WrongTargetCase
    {
        public required string Task { get; init; }
        public required string Steering { get; init; }
        public required string Step1Change { get; init; }
        public required string Step1Old { get; init; }
        public required string Step1New { get; init; }
        public required string Step2Change { get; init; }
        public required string Step2Old { get; init; }
        public required string Step2New { get; init; }
        public string? Step3Change { get; init; }
        public string? Step3Old { get; init; }
        public string? Step3New { get; init; }
        public required Func<string, bool> Truth { get; init; }
    }

    public static IEnumerable<object[]> WrongTargetCorpus()
    {
        // A) Intent change: the steering cancels half the original task. The wrong target is
        //    the cancelled rename landing anyway; completion must wait until it is reverted.
        yield return new object[] { new WrongTargetCase
        {
            Task = "In the demo component, rename the title to 'demo-renamed' and add a public getItems() method.",
            Steering = "IMPORTANT UPDATE: ignore the rename — the title must stay 'demo'. Only add getItems(), returning a copy of the items array.",
            Step1Change = "Rename the title field (old intent — the steering cancels it)",
            Step1Old = TitleLine, Step1New = TitleLineRenamed,
            Step2Change = "Add getItems() returning a copy of items (per steering)",
            Step2Old = CtorLine, Step2New = CtorLineWithMethod,
            Step3Change = "Revert the title to 'demo' — the steering cancelled the rename",
            Step3Old = TitleLineRenamed, Step3New = TitleLine,
            Truth = c => c.Contains("getItems()") && c.Contains("title = 'demo';") && !c.Contains("demo-renamed")
        } };
        // B) Contradiction: the user's final answer overrides the earlier instruction. The
        //    wrong target is returning the array directly; completion must wait for the copy.
        yield return new object[] { new WrongTargetCase
        {
            Task = "In the demo component, add a public getItems() method that returns the items array directly.",
            Steering = "Actually, no — do not return the array itself. Final answer: getItems() must return a copy via this.items.slice().",
            Step1Change = "Add getItems() returning this.items directly (contradicted by the final answer)",
            Step1Old = CtorLine, Step1New = "  constructor() { }\n  getItems() { return this.items; }",
            Step2Change = "Switch getItems() to return this.items.slice() (per the final answer)",
            Step2Old = "  getItems() { return this.items; }",
            Step2New = "  getItems() { return this.items.slice(); }",
            Truth = c => c.Contains("getItems() { return this.items.slice(); }") && !c.Contains("return this.items; }")
        } };
        // C) Scope-push: the steering forbids touching anything beyond the ask. The wrong
        //    target is the scope-pushed field removal; completion must wait until it is back.
        yield return new object[] { new WrongTargetCase
        {
            Task = "In the demo component, add a public getItems() method and remove the unused 'items' field.",
            Steering = "Scope correction: keep the 'items' field — it IS used. Only add getItems(). Do not remove anything.",
            Step1Change = "Remove the unused items field (scope-pushed beyond the steering)",
            Step1Old = "  title = 'demo';\n  items: string[] = [];", Step1New = "  title = 'demo';",
            Step2Change = "Add getItems() returning a copy (per steering)",
            Step2Old = CtorLine, Step2New = CtorLineWithMethod,
            Step3Change = "Restore the items field — the steering forbade removing it",
            Step3Old = "  title = 'demo';\n  constructor() { }",
            Step3New = "  title = 'demo';\n  items: string[] = [];\n  constructor() { }",
            Truth = c => c.Contains("items: string[] = []") && c.Contains("getItems()")
        } };
    }

    [Theory]
    [MemberData(nameof(WrongTargetCorpus))]
    public async Task AdversarialWrongTarget_NeverCompletesWithWrongTarget(WrongTargetCase c)
    {
        var controller = BuildController();
        // The scripted planner: wrong target first (the old intent / the pushed scope), then
        // the steered change, then a repair that removes the wrong target — so completion can
        // only be declared once the assessment has reconciled against the steering.
        _clientFactory.PlannerReply = n => n switch
        {
            1 => StepJson(DemoTsRel, c.Step1Change, c.Step1Old, c.Step1New),
            2 => StepJson(DemoTsRel, c.Step2Change, c.Step2Old, c.Step2New),
            3 when c.Step3Old != null => StepJson(DemoTsRel, c.Step3Change!, c.Step3Old, c.Step3New!),
            _ => "{\"planComplete\": true, \"completionReason\": \"plan complete\"}"
        };
        // Computed ground truth: we know the correct final state before the run starts.
        _clientFactory.AssessComplete = c.Truth;

        var (_, plan, complete) = await InvokeOrchestrate(controller, c.Task, c.Steering);

        // THE assertion: the run must never report success while the wrong target is on disk.
        // On the old code the assessment judged the ORIGINAL task only, so each case completed
        // with the wrong target present; with the steering threaded into the assessment, the
        // run repairs the wrong target away before it can complete.
        var ts = File.ReadAllText(Path.Combine(_projectRoot, DemoTsRel.Replace('/', Path.DirectorySeparatorChar)));
        Assert.True(c.Truth(ts),
            $"run completed but the steered ground truth is NOT satisfied on disk — plan: {plan?.Summary}; " +
            $"calls=[{string.Join(",", _clientFactory.Calls)}]; file:\n{ts}");
        Assert.True(complete,
            $"run should complete only once the steered target is on disk — calls=[{string.Join(",", _clientFactory.Calls)}]; plan: {plan?.Summary}");
        Assert.True(_clientFactory.PlannerUserPrompts.Count >= 2,
            $"expected a multi-turn run, got {_clientFactory.PlannerUserPrompts.Count} planner turns");

        // The steering must reach EVERY completion assessment — this is the fix under test.
        // Without it the assessor cannot tell that a cancelled rename / contradicted return /
        // scope-pushed removal is the wrong target.
        Assert.NotEmpty(_clientFactory.AssessUserPrompts);
        foreach (var prompt in _clientFactory.AssessUserPrompts)
        {
            Assert.Contains("## User steering", prompt);
            Assert.Contains(c.Steering, prompt);
            Assert.Contains("OVERRIDES the original task", prompt);
        }
        // Any post-execution verification prompt must also carry the steering, so a repair
        // driven from the verifier side cannot churn back toward the original target either.
        foreach (var prompt in _clientFactory.PostVerifyUserPrompts)
            Assert.Contains(c.Steering, prompt);
        Assert.Empty(_clientFactory.Unmatched);
    }

    private static string StepJson(string file, string change, string oldString, string newString)
    {
        var payload = new Dictionary<string, object?>
        {
            ["thinking"] = "Single atomic step.",
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

    // ── 3. Empty / whitespace steering is treated as absent ─────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   \n  \r\n  ")]
    public async Task EmptySteering_ProducesNoSteeringSection(string steering)
    {
        const string task = "In the demo component, add a public getItems() method.";
        var controller = BuildController();
        var (_, plan, complete) = await InvokeOrchestrate(controller, task, steering);

        Assert.True(complete, $"run should complete — plan: {plan?.Summary}");
        Assert.True(_clientFactory.PlannerUserPrompts.Count >= 2,
            $"expected >= 2 planner turns, got {_clientFactory.PlannerUserPrompts.Count}");
        foreach (var prompt in _clientFactory.PlannerUserPrompts)
        {
            // Empty/whitespace user steering must add NOTHING: exactly one steering section
            // (the attached-files instruction the pipeline injects itself), and no classic-
            // planner "USER STEERING" marker.
            Assert.Single(Regex.Matches(prompt, "### STEERING ###"));
            Assert.Contains("The user has explicitly attached one or more files", prompt);
            Assert.DoesNotContain("### USER STEERING ###", prompt);
        }
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── 4. Classic full-plan pipeline (the 'Plan the complete minimum set of steps' ──────
    //    route — AnalyzePromptAndPlanCodeChanges, behind the replan/repair/checkpoint
    //    paths). The adversarial corpus must hold there too: the classic user prompt gets
    //    the steering under "### USER STEERING ###", the scripted plan reflects the
    //    intent-change / scope-push, and the steering-scoped plan executes into the target
    //    file without the out-of-scope change landing.

    [Fact]
    public async Task ClassicPlanning_IntentChange_SteeringVisibleAndPlanHonorsIt()
    {
        var controller = BuildController();
        const string task = "In the demo component, rename the title to 'demo-renamed' and add a public getItems() method.";
        const string steering = "IMPORTANT UPDATE: ignore the rename — the title must stay 'demo'. Only add getItems(), returning a copy of the items array.";
        // The classic planner's reply honors the steering: it does NOT rename the title.
        _clientFactory.ClassicPlannerReply = n => ClassicPlanJson(
            "Add getItems() returning a copy (per steering — title untouched)", CtorLine, CtorLineWithMethod);

        var (plan, userPrompt) = await InvokeClassicPlanner(controller, task, steering);

        // Both halves of the conflict reach the classic planner together (its marker differs
        // from the incremental route: "### USER STEERING ###").
        Assert.Contains(task, userPrompt);
        Assert.Contains("### USER STEERING ###", userPrompt);
        Assert.Contains(steering, userPrompt);
        Assert.NotNull(plan);
        var step = Assert.Single(plan!.Plan);
        Assert.Contains("getItems", step.Change);
        Assert.DoesNotContain("rename", step.Change, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Theory]
    [MemberData(nameof(AdversarialCorpus))]
    public async Task ClassicPlanning_AdversarialCorpus_SteeringAndTaskStayVisible(string task, string steering)
    {
        var controller = BuildController();
        _clientFactory.ClassicPlannerReply = n => ClassicPlanJson(
            "Add getItems() per steering", CtorLine, CtorLineWithMethod);

        var (plan, userPrompt) = await InvokeClassicPlanner(controller, task, steering);

        Assert.Contains(task, userPrompt);
        Assert.Contains("### USER STEERING ###", userPrompt);
        Assert.Contains(steering, userPrompt);
        Assert.NotNull(plan);
        Assert.NotEmpty(plan!.Plan);
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  \r\n  ")]
    public async Task ClassicPlanning_EmptySteering_NoUserSteeringSection(string steering)
    {
        const string task = "In the demo component, add a public getItems() method.";
        var controller = BuildController();
        _clientFactory.ClassicPlannerReply = n => ClassicPlanJson("Add getItems()", CtorLine, CtorLineWithMethod);

        var (plan, userPrompt) = await InvokeClassicPlanner(controller, task, steering);

        Assert.DoesNotContain("### USER STEERING ###", userPrompt);
        Assert.NotNull(plan);
        Assert.Single(plan!.Plan);
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task ClassicPlanning_ScopePush_SteeringScopedPlanExecutesIntoTargetFile()
    {
        var controller = BuildController();
        const string task = "In the demo component, add a public getItems() method and rename the title.";
        const string steering = "Scope-push correction: only add getItems(). Do NOT touch the title or anything else. One minimal edit.";
        _clientFactory.ClassicPlannerReply = n => ClassicPlanJson(
            "Add getItems() returning a copy of items (title untouched)", CtorLine, CtorLineWithMethod);

        var (plan, _) = await InvokeClassicPlanner(controller, task, steering);
        Assert.NotNull(plan);

        // Execute the classic plan end-to-end: the steering-scoped step must land, and the
        // out-of-scope title change (the original task's other half) must NOT.
        var results = new List<object>();
        var execMethod = typeof(AgentController).GetMethod("ExecutePlan", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ExecutePlan not found");
        var execTask = (Task)execMethod.Invoke(controller, new object?[]
        {
            task, _projectRoot, /*emitSse*/ false, /*discoveryContext*/ "", plan,
            CancellationToken.None, results, /*steeringContext*/ steering,
            /*attachedFiles*/ new List<string> { DemoTsRel }, /*completedStepIndices*/ null,
            /*cardId*/ null, /*replanBudget*/ new[] { 0 }, /*onActivity*/ null, /*skipLlmPreResolution*/ true
        })!;
        await execTask;

        var ts = File.ReadAllText(Path.Combine(_projectRoot, DemoTsRel.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Contains("getItems()", ts);
        Assert.Contains("title = 'demo';", ts); // out-of-scope title change never landed
        Assert.DoesNotContain("demo-renamed", ts);
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── 4b. Classic-route mirror of the multi-turn context-carry eval: a CREATE step must
    //        carry its file path into the REFERENCE step of the SAME plan. Unlike the
    //        incremental route there are no separate planner turns — the whole plan is one
    //        call — so the carry must already exist BETWEEN PLAN ITEMS, and it must be
    //        assertable on the plan itself BEFORE any execution. Then the chain must
    //        actually land (create file + reference wired in). ───────────────────────────

    [Fact]
    public async Task ClassicPlanning_CreateThenReference_CarriesFilePathAcrossPlanItems_BeforeExecution()
    {
        const string task = "Create a new items-store.ts with an ItemsStore class, then wire it into the demo component so it imports and uses the store.";
        const string ItemsStoreRel = "maxhanna.client/src/app/demo/items-store.ts";
        const string StoreContent = "export class ItemsStore {\n  items: string[] = [];\n}\n";
        const string ImportLine = "import { ItemsStore } from './items-store';\n\n";
        const string AnchorOld = "export class DemoComponent {";
        const string AnchorNew = ImportLine + "export class DemoComponent {\n  store = new ItemsStore();";

        // The classic planner emits BOTH plan items in one call — item 1 creates the new
        // file (path in "file", complete content in "newString" — the deterministic create
        // shape TryCreateFileAsync applies with zero LLM), item 2 references it (anchored
        // edit into the demo component). The reference item's payload names the path AND
        // the symbol that item 1 creates — the carry across plan items.
        _clientFactory.ClassicPlannerReply = n => ClassicPlanJsonTwoItems(
            "Create items-store.ts with an ItemsStore class", ItemsStoreRel, StoreContent,
            "Wire the new items store into the demo component (imports ./items-store and instantiates ItemsStore)",
            DemoTsRel, AnchorOld, AnchorNew);

        var controller = BuildController();
        var (plan, userPrompt) = await InvokeClassicPlanner(controller, task, steering: "");

        // Both halves of the create-then-reference chain reached the classic planner together.
        Assert.Contains(task, userPrompt);
        Assert.NotNull(plan);

        // PHASE A — the carry, asserted BEFORE ANY EXECUTION (only the planner ran):
        //  - ordered chain: item 1 is the create (new path), item 2 is the reference (existing file);
        //  - item 2's payload references item 1's path AND symbol — the carry across plan items;
        //  - nothing has executed yet: the created file must NOT exist and the demo component
        //    must still be pristine, proving the carry lives in the plan, not in landed state.
        Assert.Equal(2, plan!.Plan.Count);
        var createStep = plan.Plan[0];
        var referenceStep = plan.Plan[1];
        Assert.Equal(ItemsStoreRel, createStep.File);
        Assert.Equal(StoreContent.Trim(), createStep.NewString?.Trim());
        Assert.Equal(DemoTsRel, referenceStep.File);
        Assert.Contains("./items-store", referenceStep.NewString);
        Assert.Contains("ItemsStore", referenceStep.NewString);
        Assert.False(File.Exists(Path.Combine(_projectRoot, ItemsStoreRel.Replace('/', Path.DirectorySeparatorChar))),
            "the create step must not have executed yet — the path carry must be assertable on the plan alone");
        Assert.Equal(DemoComponentTs,
            File.ReadAllText(Path.Combine(_projectRoot, DemoTsRel.Replace('/', Path.DirectorySeparatorChar))));

        // PHASE B — execute the classic plan end-to-end: the create lands and the reference
        // step consumes the exact carried path (no hallucinated variant).
        var results = new List<object>();
        var execMethod = typeof(AgentController).GetMethod("ExecutePlan", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ExecutePlan not found");
        var execTask = (Task)execMethod.Invoke(controller, new object?[]
        {
            task, _projectRoot, /*emitSse*/ false, /*discoveryContext*/ "", plan,
            CancellationToken.None, results, /*steeringContext*/ "",
            /*attachedFiles*/ new List<string> { DemoTsRel }, /*completedStepIndices*/ null,
            /*cardId*/ null, /*replanBudget*/ new[] { 0 }, /*onActivity*/ null, /*skipLlmPreResolution*/ true
        })!;
        await execTask;

        var store = File.ReadAllText(Path.Combine(_projectRoot, ItemsStoreRel.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Contains("export class ItemsStore", store);
        var ts = File.ReadAllText(Path.Combine(_projectRoot, DemoTsRel.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Contains("import { ItemsStore } from './items-store';", ts);
        Assert.Contains("store = new ItemsStore();", ts);
        // Only the scripted planner + per-step verifier calls happened — no unscripted LLM.
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── 4c. Classic-route mirror of the invented-DIRECTORY guard: the classic full-plan
    //        route has no per-step validation, so a _create_file under a nonsense directory
    //        prefix must be rejected BEFORE EXECUTION — the whole plan is gated up front in
    //        ExecutePlan with the same closest-real-directory steer as the incremental guard.
    //        The exemption (an earlier _create_directory step in the same plan makes the
    //        directory real) must still let the plan through. ────────────────────────────

    [Fact]
    public async Task ClassicPlanning_InventedCreateDirectory_RejectedBeforeExecution()
    {
        // The task must NOT name the path: the invented-directory guard exempts user-named
        // paths (legit "write docs/README.md" into a new folder), so the planner-invention
        // case is a generic task where the PLAN invents the nonsense directory.
        const string task = "Create a reusable string-utilities helper and import it into the demo component.";
        const string InventedRel = "maxhanna.client/src/helpers/weaver/util.ts";
        const string UtilContent = "export class Util {}\n";

        // Two-item classic plan: item 1 _create_file under a directory that does not exist
        // anywhere in the fixture (maxhanna.client/src exists — the deepest real ancestor —
        // but helpers/weaver does not), item 2 an anchored edit into the demo that references
        // the invented path. Both must be rejected BEFORE anything executes.
        var plan = new AgentPlan
        {
            Thinking = "Full plan: create the helper, wire it in.",
            Summary = "Create util.ts then import it.",
            Plan = new List<PlanStep>
            {
                new() { File = "_create_file", Change = InventedRel, NewString = UtilContent },
                new()
                {
                    File = DemoTsRel,
                    Change = "Import Util from the new helper into the demo component",
                    OldString = "export class DemoComponent {",
                    NewString = "import { Util } from './helpers/weaver/util';\n\nexport class DemoComponent {"
                }
            }
        };

        var controller = BuildController();
        var results = new List<object>();
        var execMethod = typeof(AgentController).GetMethod("ExecutePlan", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ExecutePlan not found");
        var execTask = (Task)execMethod.Invoke(controller, new object?[]
        {
            task, _projectRoot, /*emitSse*/ false, /*discoveryContext*/ "", plan,
            CancellationToken.None, results, /*steeringContext*/ "",
            /*attachedFiles*/ new List<string> { DemoTsRel }, /*completedStepIndices*/ null,
            /*cardId*/ null, /*replanBudget*/ new[] { 0 }, /*onActivity*/ null, /*skipLlmPreResolution*/ true
        })!;
        await execTask;

        // REJECTED BEFORE EXECUTION: nothing landed — neither the invented file nor the
        // reference edit nor any part of the invented directory tree.
        Assert.False(File.Exists(Path.Combine(_projectRoot, InventedRel.Replace('/', Path.DirectorySeparatorChar))),
            "the invented create must never materialize on disk");
        Assert.False(Directory.Exists(Path.Combine(_projectRoot, "maxhanna.client", "src", "helpers")),
            "no part of the invented directory tree may be created");
        // The reference edit must never land either — the whole plan is rejected up front.
        Assert.Equal(DemoComponentTs,
            File.ReadAllText(Path.Combine(_projectRoot, DemoTsRel.Replace('/', Path.DirectorySeparatorChar))));

        // The rejection is recorded as a failed step carrying the closest-real-directory steer.
        var rejected = results.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("type")?.ToString() == "rejected_step")
            .ToList();
        Assert.Single(rejected);
        Assert.Equal(InventedRel, rejected[0].GetValueOrDefault("path"));
        var reason = rejected[0].GetValueOrDefault("error")?.ToString() ?? "";
        Assert.Contains("does not exist anywhere in the project", reason);
        Assert.Contains("maxhanna.client/src", reason);   // the closest real directory steer
        Assert.Contains("NEVER invent directory paths", reason);
        // Zero steps executed → zero LLM calls (the gate is fully deterministic).
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task ClassicPlanning_InventedCreateDirectory_PrecededByCreateDirectory_IsAllowed()
    {
        const string task = "Create a helpers/weaver directory with a util.ts under it.";
        const string NewDirRel = "maxhanna.client/src/helpers/weaver";
        const string UtilRel = "maxhanna.client/src/helpers/weaver/util.ts";
        const string UtilContent = "export class Util {}\n";

        // Same invented-looking directory — but an EARLIER _create_directory step in the SAME
        // plan makes it real, so the gate must NOT fire (the exemption mirrors the incremental
        // guard's planSoFar check).
        var plan = new AgentPlan
        {
            Thinking = "Create the directory, then the file.",
            Summary = "New helpers/weaver with util.ts.",
            Plan = new List<PlanStep>
            {
                new() { File = "_create_directory", Change = NewDirRel },
                new() { File = UtilRel, Change = "Create util.ts under helpers/weaver", NewString = UtilContent }
            }
        };

        var controller = BuildController();
        var results = new List<object>();
        var execMethod = typeof(AgentController).GetMethod("ExecutePlan", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ExecutePlan not found");
        var execTask = (Task)execMethod.Invoke(controller, new object?[]
        {
            task, _projectRoot, /*emitSse*/ false, /*discoveryContext*/ "", plan,
            CancellationToken.None, results, /*steeringContext*/ "",
            /*attachedFiles*/ new List<string> { DemoTsRel }, /*completedStepIndices*/ null,
            /*cardId*/ null, /*replanBudget*/ new[] { 0 }, /*onActivity*/ null, /*skipLlmPreResolution*/ true
        })!;
        await execTask;

        // NOT rejected: the plan executed and both steps landed.
        Assert.DoesNotContain(results.OfType<Dictionary<string, object?>>(),
            r => r.GetValueOrDefault("type")?.ToString() == "rejected_step");
        var utilFull = Path.Combine(_projectRoot, UtilRel.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(utilFull), "the create after an earlier _create_directory must land");
        Assert.Contains("export class Util", File.ReadAllText(utilFull));
        Assert.True(Directory.Exists(Path.Combine(_projectRoot, NewDirRel.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Empty(_clientFactory.Unmatched);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    // ── Harness (mirrors InterleavedPipelineIntegrationTests) ───────────────────────────

    private async Task<(List<object> allSteps, AgentPlan? plan, bool complete)> InvokeOrchestrate(
        AgentController controller, string prompt, string? steeringContext)
    {
        // Hermetic runs: the scripted edits must land verbatim — an external formatter
        // (prettier via npx) rewriting quote style would corrupt the scripted plan's later
        // steps (their oldString anchors no longer match the re-quoted file) and flip the
        // ground-truth assertions, and only on machines where npx exists.
        CodeFormatterService.SuppressExternalFormattersForTests.Value = true;
        var method = typeof(AgentController).GetMethod("Orchestrate", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Orchestrate not found");
        var task = (Task<(List<object> allSteps, AgentPlan? plan, bool complete)>)method.Invoke(controller, new object?[]
        {
            prompt, _projectRoot, /*emitSse*/ false, CancellationToken.None,
            /*attachedFiles*/ new List<string> { DemoTsRel },
            /*skipContextReview*/ false, /*steeringContext*/ steeringContext, /*skipQualityCheck*/ false,
            /*existingPlan*/ null, /*completedStepIndices*/ null, /*cardId*/ null,
            /*createTests*/ false, /*buildCommands*/ null, /*strictVerifier*/ null, /*webResults*/ null
        })!;
        return await task;
    }

    /// <summary>Drives the CLASSIC full-plan planner directly (the "Plan the complete minimum
    /// set of steps" route) and returns the parsed plan plus the user prompt that was sent, so
    /// tests can assert the task + steering reached the classic mode exactly like the
    /// incremental mode.</summary>
    private async Task<(AgentPlan? plan, string userPrompt)> InvokeClassicPlanner(
        AgentController controller, string prompt, string steering)
    {
        var method = typeof(AgentController).GetMethod("AnalyzePromptAndPlanCodeChanges", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("AnalyzePromptAndPlanCodeChanges not found");
        var task = (Task<AgentPlan?>)method.Invoke(controller, new object?[]
        {
            prompt,
            /*discoveryContext*/ "### read maxhanna.client/src/app/demo/demo.component.ts\n```\n" + DemoComponentTs + "\n```\n",
            _projectRoot, /*emitSse*/ false, CancellationToken.None, steering
        })!;
        var plan = await task;
        return (plan, _clientFactory.ClassicPlannerUserPrompts[^1]);
    }

    private static string ClassicPlanJson(string change, string oldString, string newString)
    {
        var payload = new Dictionary<string, object?>
        {
            ["thinking"] = "Full plan honoring the user's steering.",
            ["plan"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["file"] = DemoTsRel,
                    ["change"] = change,
                    ["oldString"] = oldString,
                    ["newString"] = newString
                }
            }
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>A two-item classic plan: item 1 CREATES a new file (the deterministic
    /// create shape: path in "file", complete content in "newString", no oldString),
    /// item 2 REFERENCES it (anchored edit into an existing file). Used to assert the
    /// create-then-reference path-carry across plan items.</summary>
    private static string ClassicPlanJsonTwoItems(
        string createChange, string createFile, string createContent,
        string refChange, string refFile, string refOldString, string refNewString)
    {
        var payload = new Dictionary<string, object?>
        {
            ["thinking"] = "Full two-step plan: create the store, then wire it into the component.",
            ["plan"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["file"] = createFile,
                    ["change"] = createChange,
                    ["newString"] = createContent
                },
                new Dictionary<string, object?>
                {
                    ["file"] = refFile,
                    ["change"] = refChange,
                    ["oldString"] = refOldString,
                    ["newString"] = refNewString
                }
            }
        };
        return JsonSerializer.Serialize(payload);
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
        SetField(controller, "_boardData", new BoardDataService(_db, NullLogger<BoardDataService>.Instance));
        SetField(controller, "_emailService", new EmailService(new ConfigFileService(_db)));
        SetField(controller, "_push", new PushNotificationService(_db));
        SetField(controller, "_editKnowledge", new EditKnowledgeService(_db));
        // Hermetic runtime probe: nothing installed (no real process spawns in tests).
        SetField(controller, "_runtimeProbe", new RuntimeProbeService((_, _, _) => (-1, "", "")));
        SetStaticField("_nextConnectivityCheck", DateTime.UtcNow.AddMinutes(5));
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
    /// Scripted fake LLM that records the user prompt of EVERY planner call (incremental
    /// and classic) so tests can assert the task + steering survive to every turn. Routes
    /// on stable prompt markers; any request no marker matches lands in <see cref="Unmatched"/>
    /// and is answered empty — the tests then fail on the Unmatched assertion.
    /// </summary>
    private sealed class ScriptedClientFactory : IHttpClientFactory, IDisposable
    {
        public readonly List<string> Calls = new();
        public readonly List<string> Unmatched = new();
        public readonly List<string> UnmatchedSystems = new();
        public readonly List<string> PlannerUserPrompts = new();
        public readonly List<string> ClassicPlannerUserPrompts = new();
        public readonly List<string> AssessUserPrompts = new();
        public readonly List<string> PostVerifyUserPrompts = new();
        public Func<int, string>? ClassicPlannerReply { get; set; }
        /// <summary>Overrides the fixed turn sequence for the INCREMENTAL planner — lets a test
        /// script a wrong-target step, a steered-correct step, then a repair step.</summary>
        public Func<int, string>? PlannerReply { get; set; }
        /// <summary>Deterministic ground truth for the completion assessment: given the CURRENT
        /// on-disk content of the demo component, is the task (as modified by the steering)
        /// complete? When set, the scripted assessor reads the real file — mirroring the
        /// synthetic-ground-truth suite's "we know the correct answer before we ask" idea.</summary>
        public Func<string, bool>? AssessComplete { get; set; }
        /// <summary>Path of the temp project root so the scripted assessor can read real files.</summary>
        public string ProjectRoot { get; set; } = "";
        private int _plannerCalls;
        private int _classicPlannerCalls;
        private int _assessCalls;

        public HttpClient CreateClient(string name) => new(new ScriptedHandler(this));
        public HttpClient CreateClient() => CreateClient("default");
        public void Dispose() { }

        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly ScriptedClientFactory _owner;
            public ScriptedHandler(ScriptedClientFactory owner) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => Task.FromResult(BuildResponse(request));

            private HttpResponseMessage BuildResponse(HttpRequestMessage request)
            {
                if (request.Method == HttpMethod.Get)
                    return Json(new { });
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
                // ORDER MATTERS: the incremental planner first (its system text is the most
                // distinctive), then the classic full-plan planner, then the fixed markers.
                if (system.Contains("building a code-change plan ONE STEP AT A TIME", StringComparison.Ordinal))
                {
                    lock (_owner.PlannerUserPrompts) _owner.PlannerUserPrompts.Add(user);
                    var n = Interlocked.Increment(ref _owner._plannerCalls);
                    if (_owner.PlannerReply != null) return (_owner.PlannerReply(n), "planner-step");
                    return n switch
                    {
                        1 => (StepJson(DemoTsRel, "Rename the title field (old intent — scripted to be corrected by steering)",
                            TitleLine, TitleLineRenamed), "planner-step"),
                        2 => (StepJson(DemoTsRel, "Add getItems() returning a copy of items (per mid-run steering)",
                            CtorLine, CtorLineWithMethod), "planner-step"),
                        _ => ("{\"planComplete\": true, \"completionReason\": \"plan complete\"}", "planner-complete")
                    };
                }
                if (system.Contains("Plan the complete minimum set of steps", StringComparison.Ordinal))
                {
                    // NOTE: recorded SEPARATELY from PlannerUserPrompts (the incremental route)
                    // so classic-planner prompts can never pollute incremental assertions.
                    lock (_owner.ClassicPlannerUserPrompts) _owner.ClassicPlannerUserPrompts.Add(user);
                    var n = Interlocked.Increment(ref _owner._classicPlannerCalls);
                    if (_owner.ClassicPlannerReply != null) return (_owner.ClassicPlannerReply(n), "planner-classic");
                    return ("{\"plan\": [{\"file\": \"" + DemoTsRel + "\", \"change\": \"Add getItems() per steering\", " +
                            "\"oldString\": \"" + CtorLine + "\", \"newString\": \"" + CtorLineWithMethod + "\"}]}", "planner-classic");
                }
                if (system.Contains("You extract a short checklist of literal, testable requirements", StringComparison.Ordinal))
                    return ("{\"requirements\": [\"Add a public getItems() method\"]}", "checklist");
                if (system.Contains("You are a strict plan-coherence validator", StringComparison.Ordinal))
                    return ("{\"valid\": true}", "plan-validator");
                if (user.Contains("Decide: keep or abandon", StringComparison.Ordinal))
                    return ("{\"decision\": \"keep\", \"reason\": \"verified\", \"score\": 95, \"needsExtraStep\": false}", "verify");
                if (user.Contains("Evaluate the code changes against the ORIGINAL TASK ONLY", StringComparison.Ordinal))
                {
                    lock (_owner.AssessUserPrompts) _owner.AssessUserPrompts.Add(user);
                    var n = Interlocked.Increment(ref _owner._assessCalls);
                    if (_owner.AssessComplete != null)
                    {
                        // Computed ground truth: read the REAL current file, decide completion
                        // deterministically. If the wrong target is on disk the assessment must
                        // say incomplete so the loop repairs instead of completing wrong.
                        var rel = DemoTsRel.Replace('/', Path.DirectorySeparatorChar);
                        var path = Path.Combine(_owner.ProjectRoot, rel);
                        var content = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "";
                        var done = _owner.AssessComplete(content);
                        return done
                            ? ("{\"complete\": true, \"reason\": \"steered ground truth satisfied\", \"issues\": []}", "assess")
                            : ("{\"complete\": false, \"reason\": \"the steered target is not yet on disk\", \"issues\": []}", "assess");
                    }
                    // Default (no ground-truth hook): first assessment says "not complete yet"
                    // so the interleaved loop goes back to the planner for a second step — the
                    // multi-turn shape that makes these tests exercise steering persistence
                    // across turns. Subsequent assessments say complete so the run terminates
                    // cleanly.
                    return n == 1
                        ? ("{\"complete\": false, \"reason\": \"the rename landed but getItems() has not been added yet\", \"issues\": []}", "assess")
                        : ("{\"complete\": true, \"reason\": \"task satisfied\", \"issues\": []}", "assess");
                }
                if (system.Contains("meticulous code reviewer verifying if a task is fully complete", StringComparison.Ordinal))
                {
                    lock (_owner.PostVerifyUserPrompts) _owner.PostVerifyUserPrompts.Add(user);
                    return ("{\"complete\": true, \"reason\": \"done\", \"issues\": []}", "post-verify");
                }
                if (system.Contains("You detect code cohesion issues after an edit. Output ONLY JSON.", StringComparison.Ordinal))
                    return ("{\"issues\": []}", "cohesion");
                lock (_owner.Unmatched) _owner.Unmatched.Add(system.Length > 80 ? system[..80] : system);
                lock (_owner.UnmatchedSystems) _owner.UnmatchedSystems.Add("SYSTEM: " + system + "\nUSER: " + user + "\n---");
                return ("", "unknown");
            }

            private static string StepJson(string file, string change, string oldString, string newString)
            {
                var payload = new Dictionary<string, object?>
                {
                    ["thinking"] = "Single atomic step.",
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
