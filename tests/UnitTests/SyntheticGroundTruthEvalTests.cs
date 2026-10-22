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
using Weaver;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Synthetic datasets with COMPUTED ground truth, per the post's core lesson: "the hardest
/// hallucinations to catch are the confident wrong answers — the response sounds right, reads
/// well, and the data is just incorrect. For data agents specifically, generate synthetic
/// datasets with computed ground truth so we know the correct answer BEFORE we ask the question."
///
/// Every corpus entry is a synthetic task whose correct answer is a PURE FUNCTION of the
/// fixture (a CSS rule must gain specific declarations; a getter must exist; a hallucinated
/// property typo must never land). No LLM is consulted for the answer — the test computes it.
///
/// The corpus is anchored on a real failure: a "put max-height and overflow auto on the
/// schedules" task produced a planner step that hallucinated `s.departure.ested` (dropping
/// letters from the real `s.departure.estimated`) and targeted the HTML file instead of CSS.
/// Beyond property typos, the corpus now covers DATA-CORRECTNESS hallucinations with
/// counting/aggregation/arithmetic ground truth: "rename every occurrence of X to Y" (the
/// answer is a computed occurrence COUNT — an ambiguous single-line anchor must never
/// silently rename one of N), "group N benchmarks by name" (the answer is a computed group
/// STRUCTURE — a grouping that duplicates an entry is rejected by counting the entry keys
/// before it lands), and arithmetic transforms over GENERATED datasets ("increase every rate
/// limit by N" — the answer is each known input + N, so a wrong-number edit that reads fine
/// and lands cleanly is still caught by the computed answer).
/// Three behaviors are locked in per shape:
///   1. A correct planner reproduces the computed ground truth exactly (proving the harness
///      can reach the known answer with zero unscripted LLM calls).
///   2. A confident-wrong edit (hallucinated property) is rejected by the deterministic
///      hallucinated-property guard and NEVER lands — the run fails loudly instead of
///      completing "successfully" with wrong data.
///   3. Non-JSON planner output is rejected with feedback, retried, and the corrected step
///      still reaches ground truth.
/// </summary>
public class SyntheticGroundTruthEvalTests : IDisposable
{
    private const string GlobeCssRel = "maxhanna.client/src/app/globe/globe.component.css";
    private const string GlobeHtmlRel = "maxhanna.client/src/app/globe/globe.component.html";
    private const string AppHtmlRel = "maxhanna.client/src/app/app.component.html";
    private const string FlightTsRel = "maxhanna.client/src/app/flights/flight-schedule.component.ts";
    private const string BenchConfigTsRel = "maxhanna.client/src/app/benchmarks/benchmark-config.ts";
    private const string BenchmarkDataTsRel = "maxhanna.client/src/app/benchmarks/benchmark-data.ts";
    private const string RateLimitsTsRel = "maxhanna.client/src/app/benchmarks/rate-limits.ts";
    private const string MetricsTsRel = "maxhanna.client/src/app/metrics/metrics.service.ts";

    // ── Multi-turn chain fixture ──────────────────────────────────────────────────────────
    // Turn 1 establishes the RETRY_LIMIT constant; turn 2 establishes the loadWithRetries()
    // wrapper (its NAME is the value later turns must reference); turn 3 wires it into the
    // constructor. The correct final state is COMPUTED from this fixture — a context failure
    // at turn 3 (referencing loadWithRetry() instead) is detectable before the run starts.

    private const string MetricsServiceFixture = """
        export class MetricsService {
          constructor() { }

          async fetch(): Promise<string> {
            return this.load();
          }

          private load(): Promise<string> {
            return Promise.resolve('ok');
          }
        }
        """;

    private const string MultiTurnChainPrompt =
        "In the MetricsService, 1) add a RETRY_LIMIT constant set to 5, " +
        "2) add a loadWithRetries() method that calls this.load() up to RETRY_LIMIT times, " +
        "3) update the constructor to call loadWithRetries().";

    private const string AddRetryLimitChange = "Add a RETRY_LIMIT = 5 constant";
    private const string AddRetryLimitOld = "  constructor() { }";
    private const string AddRetryLimitNew = "  private readonly RETRY_LIMIT = 5;\n\n  constructor() { }";

    private const string AddWrapperChange = "Add loadWithRetries() retrying up to RETRY_LIMIT";
    private const string AddWrapperOld = "  private load(): Promise<string> {";
    private const string AddWrapperNew =
        "  private loadWithRetries(): Promise<string> {\n    return this.load();\n  }\n\n  private load(): Promise<string> {";

    private const string WireCtorChange = "Call loadWithRetries() from the constructor";
    private const string WireCtorOld = "  constructor() { }";
    private const string WireCtorNewCorrect = "  constructor() {\n    void this.loadWithRetries();\n  }";
    // The context-failure variant: turn 3 forgot the name established in turn 2.
    private const string WireCtorNewWrong = "  constructor() {\n    void this.loadWithRetry();\n  }";
    private const string WireCtorWrongCallOld = "    void this.loadWithRetry();";
    private const string WireCtorWrongCallNew = "    void this.loadWithRetries();";

    // ── (click) handler fixture ───────────────────────────────────────────────────────────
    // The template ALREADY wires a working `vm.openCard()` Details button, so the real method
    // name is present in the edited file — the apply-time hallucinated-property guard can then
    // reject a typo'd handler (vm.opnCard) against it, exactly like the `s.departure.ested`
    // case. The task's ground truth: an Open button whose handler references the REAL method.

    private const string CardTsRel = "maxhanna.client/src/app/cards/card.component.ts";
    private const string CardHtmlRel = "maxhanna.client/src/app/cards/card.component.html";

    private const string CardTsFixture = """
        export class CardComponent {
          vm = {
            items: [
              { id: 'a', title: 'Alpha' },
              { id: 'b', title: 'Beta' }
            ],
            hideOpen: false,
            openCard(id: string): void { }
          };
        }
        """;

    private const string CardHtmlFixture = """
        <div class="card-list">
          <div *ngFor="let card of vm.items" class="card-item">
            <span>{{ card.title }}</span>
            <button (click)="vm.openCard(card.id)">Details</button>
          </div>
        </div>
        """;

    private const string ClickHandlerTaskPrompt =
        "In CardComponent, add an Open button next to each card's Details button " +
        "that also calls vm.openCard() with the card's id.";

    private const string DetailsButtonOld = "<button (click)=\"vm.openCard(card.id)\">Details</button>";
    private const string DetailsPlusOpenCorrect =
        "<button (click)=\"vm.openCard(card.id)\">Details</button> " +
        "<button (click)=\"vm.openCard(card.id)\">Open</button>";
    // The hallucination: the handler method name is typo'd (dropped 'e') instead of referencing
    // the real `vm.openCard` — reads fine, calls a method that doesn't exist.
    private const string DetailsPlusOpenWrong =
        "<button (click)=\"vm.openCard(card.id)\">Details</button> " +
        "<button (click)=\"vm.opnCard(card.id)\">Open</button>";

    // ── Clean-pass ground-truth fixture ──────────────────────────────────────────────────
    // A card component with a sibling stylesheet so a fully clean run (template binding +
    // CSS wiring, nothing failing) still has deterministic checks that RAN — and the card
    // must show each pass with its verified expectation, not silently hide the section.

    private const string CardCssRel = "maxhanna.client/src/app/cards/card.component.css";

    private const string CardCssFixture = """
        .card-item {
          padding: 8px;
        }
        """;

    // The Open button: introduces a NEW binding (vm.hideOpen — declared in the component .ts,
    // so the cross-file resolution from the sibling-TS work must let it through) and references
    // the .card-open-btn class that step 2 defines in the stylesheet.
    private const string DetailsPlusOpenClean =
        "<button (click)=\"vm.openCard(card.id)\">Details</button> " +
        "<button (click)=\"vm.openCard(card.id)\" [disabled]=\"vm.hideOpen\" class=\"card-open-btn\">Open</button>";

    private const string CardItemRule = """
        .card-item {
          padding: 8px;
        }
        """;

    private const string CardItemRulePlusOpenBtn = """
        .card-item {
          padding: 8px;
        }
        .card-open-btn {
          margin-left: 4px;
        }
        """;

    private const string GlobeCssFixture = """
        .flight-detail-panel {
          padding: 12px;
        }
        .flight-schedule-container {
          padding: 4px;
        }
        """;

    private const string GlobeHtmlFixture = """
        <div class="flight-detail-panel">
          <div class="flight-schedule-container" *ngIf="schedules.length">
            <div *ngFor="let s of schedules" class="flight-schedule-entry">
              <span>Estimated: {{ s.departure.estimated | date:'short' }}</span>
            </div>
          </div>
        </div>
        """;

    private const string FlightTsFixture = """
        export class FlightScheduleComponent {
          departure = { estimated: '' };
          getEstimated(): string {
            return this.departure.estimated;
          }
        }
        """;

    private const string ScheduleContainerRule = """
        .flight-schedule-container {
          padding: 4px;
        }
        """;

    private const string ScheduleContainerRuleFixed = """
        .flight-schedule-container {
          padding: 4px;
          max-height: 300px;
          overflow: auto;
        }
        """;

    // ── Counting fixture: the constant appears EXACTLY 5 times (computed by the tests) ──

    private const string BenchConfigFixture = """
        export const WORKER_CONFIGS = [
          { id: 'alpha', retries: MAX_RETRIES },
          { id: 'beta', retries: MAX_RETRIES },
          { id: 'gamma', retries: MAX_RETRIES },
          { id: 'delta', retries: MAX_RETRIES },
          { id: 'epsilon', retries: MAX_RETRIES },
        ];
        """;

    // ── Aggregation fixture: 6 flat benchmarks — bm_a × 2, bm_b × 1, bm_c × 3. The
    //    expected group structure ({"bm_a":[3,7],"bm_b":[5],"bm_c":[2,6,9]}) is COMPUTED
    //    by the tests from this fixture, never hardcoded in the assertions. The fixture stays
    //    under the targeted-anchor guard's 10-line/400-char whole-file anchor limit (a data
    //    rewrite is a full-file edit, and the guard rejects oversized oldStrings). Group keys
    //    sit at line start — DetectDuplicatePropertyAddition counts them, so a DUPLICATED
    //    group key (the merge-artifact hallucination) is caught before landing. ──

    private const string BenchmarkDataFixture = """
        export const BENCHMARKS = [
          { name: 'bm_a', metric: 7 },
          { name: 'bm_a', metric: 3 },
          { name: 'bm_b', metric: 5 },
          { name: 'bm_c', metric: 9 },
          { name: 'bm_c', metric: 2 },
          { name: 'bm_c', metric: 6 }
        ];
        """;

    private const string GroupedBenchmarksCorrect = """
        export const BENCHMARK_GROUPS = {
          bm_a: [{ name: 'bm_a', metric: 7 }, { name: 'bm_a', metric: 3 }],
          bm_b: [{ name: 'bm_b', metric: 5 }],
          bm_c: [{ name: 'bm_c', metric: 9 }, { name: 'bm_c', metric: 2 }, { name: 'bm_c', metric: 6 }]
        };
        """;

    /// <summary>The confident-wrong grouping: the model "merges" bm_a's entries into TWO
    /// bm_a blocks — the output reads like a plausible group structure, every benchmark is
    /// present, and the data is incorrect (a duplicated group key). Counting the line-anchored
    /// group keys (2 vs 0 in the flat input) is what makes it detectable before landing.</summary>
    private const string GroupedBenchmarksWithDuplicate = """
        export const BENCHMARK_GROUPS = {
          bm_a: [{ name: 'bm_a', metric: 7 }, { name: 'bm_a', metric: 3 }],
          bm_b: [{ name: 'bm_b', metric: 5 }],
          bm_a: [{ name: 'bm_c', metric: 9 }, { name: 'bm_c', metric: 2 }, { name: 'bm_c', metric: 6 }]
        };
        """;

    /// <summary>The drop hallucination: every group key is present and the output reads like a
    /// valid grouping, but bm_a's SECOND entry (metric: 3) was silently dropped — 5 entries in,
    /// 6 in the flat input. The drop mirror of the duplicate-key guard counts object-literal
    /// entries across the flat→grouped transform (6 vs 5) and rejects it before it lands.</summary>
    private const string GroupedBenchmarksWithDroppedEntry = """
        export const BENCHMARK_GROUPS = {
          bm_a: [{ name: 'bm_a', metric: 7 }],
          bm_b: [{ name: 'bm_b', metric: 5 }],
          bm_c: [{ name: 'bm_c', metric: 9 }, { name: 'bm_c', metric: 2 }, { name: 'bm_c', metric: 6 }]
        };
        """;

    private readonly string _base;
    private readonly string _projectRoot;
    private readonly DatabaseService _db;
    private readonly BoardDataService _boardData;
    private readonly ScriptedClientFactory _clientFactory;

    public SyntheticGroundTruthEvalTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "weaver_groundtruth_" + Guid.NewGuid().ToString("N"));
        _projectRoot = Path.Combine(_base, "proj");
        Directory.CreateDirectory(_projectRoot);
        Directory.CreateDirectory(Path.Combine(_base, "data"));
        _clientFactory = new ScriptedClientFactory();
        _db = new DatabaseService(
            Path.Combine(_base, "data", "weaver.db"),
            Path.Combine(_base, "data"),
            Path.Combine(_base, "data", "weaverconfig.json"));
        _boardData = new BoardDataService(_db, NullLogger<BoardDataService>.Instance);

        Write(GlobeCssRel, GlobeCssFixture);
        Write(GlobeHtmlRel, GlobeHtmlFixture);
        Write(FlightTsRel, FlightTsFixture);
        Write(BenchConfigTsRel, BenchConfigFixture);
        Write(BenchmarkDataTsRel, BenchmarkDataFixture);
        Write(MetricsTsRel, MetricsServiceFixture);
        Write(CardTsRel, CardTsFixture);
        Write(CardHtmlRel, CardHtmlFixture);
        Write(CardCssRel, CardCssFixture);
        _clientFactory.ProjectRoot = _projectRoot;
    }

    public void Dispose()
    {
        _clientFactory.Dispose();
        try { Directory.Delete(_base, true); } catch { }
    }

    private void Write(string relPath, string content)
    {
        var p = Path.Combine(_projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private string Read(string relPath) =>
        File.ReadAllText(Path.Combine(_projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));

    // ── 1. The schedules CSS task (the real run's task) ──────────────────────────────────

    [Fact]
    public async Task CssSchedulesTask_CorrectPlanner_ReproducesComputedGroundTruth()
    {
        _clientFactory.PlannerReply = n => StepJson(GlobeCssRel,
            "Add max-height and overflow auto to the flight schedule container",
            ScheduleContainerRule, ScheduleContainerRuleFixed);

        var (_, plan, complete) = await Run("Put a max height and overflow auto on the schedules displayed in the flight information popup panel");

        // Ground truth is COMPUTED: the schedule container rule must carry both declarations.
        var (ok, why) = CssSelectorHasProps(Read(GlobeCssRel), ".flight-schedule-container", "max-height", "overflow: auto");
        Assert.True(ok, $"{why} — plan: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]");
        Assert.True(complete, $"run should complete — calls=[{string.Join(",", _clientFactory.Calls)}]");
        Assert.Empty(_clientFactory.Unmatched);
        Assert.Single(_clientFactory.PlannerUserPrompts);
    }

    [Fact]
    public async Task CssSchedulesTask_HallucinatedHtmlBinding_NeverLandsSilently()
    {
        // The real run's hallucination: the planner "fixed" the CSS task by editing the HTML and
        // typo'd the real property `s.departure.estimated` into `s.departure.ested`.
        _clientFactory.PlannerReply = n => StepJson(GlobeHtmlRel,
            "Add maxHeight and overflow auto to schedules container",
            "<span>Estimated: {{ s.departure.estimated | date:'short' }}</span>",
            "<span>Estimated: {{ s.departure.ested | date:'short' }}</span>");

        var (_, plan, complete) = await Run("Put a max height and overflow auto on the schedules displayed in the flight information popup panel");

        // The hallucination must be caught BEFORE it lands — the guard rejects the step, the
        // resolver then fails, and the run stops incomplete. It must NEVER report success with
        // the typo written into the file (a silent confident-wrong completion).
        var html = Read(GlobeHtmlRel);
        var css = Read(GlobeCssRel);
        Assert.DoesNotContain("ested", html);
        Assert.DoesNotContain("ested", css);
        Assert.False(complete,
            $"a hallucinated edit must fail loudly, not complete — calls=[{string.Join(",", _clientFactory.Calls)}]; plan={plan?.Summary}");
        // The CSS ground truth is knowingly unsatisfied — that is exactly what the computed
        // answer exists to catch; the point is the run did not pretend otherwise.
        var (_, why) = CssSelectorHasProps(css, ".flight-schedule-container", "max-height");
        Assert.False(why.Length == 0, "ground truth must be able to detect the wrong answer");
    }

    // ── 2. TS getter task with a hallucinated property typo ──────────────────────────────

    [Fact]
    public async Task TsGetterTask_CorrectPlanner_ReproducesComputedGroundTruth()
    {
        var oldStr = """
            getEstimated(): string {
              return this.departure.estimated;
            }
            """;
        var newStr = """
            getEstimated(): string {
              return this.departure.estimated;
            }

            getEstimatedDeparture(): string {
              return this.departure.estimated;
            }
            """;
        _clientFactory.PlannerReply = n => StepJson(FlightTsRel,
            "Add a getEstimatedDeparture() getter returning the estimated departure",
            oldStr, newStr);

        var (_, plan, complete) = await Run("In FlightScheduleComponent, add a getter that returns the estimated departure time");

        var ts = Read(FlightTsRel);
        Assert.Contains("getEstimatedDeparture(): string", ts);
        Assert.DoesNotContain("ested", ts);
        Assert.True(complete, $"run should complete — calls=[{string.Join(",", _clientFactory.Calls)}]");
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task TsGetterTask_HallucinatedProperty_GuardRejects_NeverLands()
    {
        var oldStr = """
            getEstimated(): string {
              return this.departure.estimated;
            }
            """;
        var newStr = """
            getEstimated(): string {
              return this.departure.ested;
            }
            """;
        _clientFactory.PlannerReply = n => StepJson(FlightTsRel,
            "Fix the estimated departure getter",
            oldStr, newStr);

        var (_, plan, complete) = await Run("In FlightScheduleComponent, fix the getter that returns the estimated departure time");

        var ts = Read(FlightTsRel);
        Assert.DoesNotContain("ested", ts);
        Assert.Contains("this.departure.estimated", ts);
        Assert.False(complete,
            $"a hallucinated edit must fail loudly, not complete — calls=[{string.Join(",", _clientFactory.Calls)}]; plan={plan?.Summary}");
    }

    // ── 3. Non-JSON planner output must be rejected, retried, and corrected ──────────────

    [Fact]
    public async Task CssSchedulesTask_NonJsonPlannerOutput_RejectedThenCorrected()
    {
        var correctStep = StepJson(GlobeCssRel,
            "Add max-height and overflow auto to the flight schedule container",
            ScheduleContainerRule, ScheduleContainerRuleFixed);
        // Turn 1 = the real model's confident-wrong prose ("I need to think about this…");
        // turn 2 = the corrected concrete step.
        _clientFactory.PlannerReply = n => n == 1
            ? "I need to think about this carefully. Let me analyze the best approach for the flight panel first."
            : correctStep;

        var (_, plan, complete) = await Run("Put a max height and overflow auto on the schedules displayed in the flight information popup panel");

        // The garbage proposal was rejected with parse feedback, then the corrected step landed
        // and still reached the computed ground truth.
        Assert.True(complete, $"run should complete — calls=[{string.Join(",", _clientFactory.Calls)}]");
        Assert.Equal(2, _clientFactory.PlannerUserPrompts.Count);
        Assert.True(
            _clientFactory.PlannerUserPrompts[1].Contains("REJECTED ATTEMPTS") ||
            _clientFactory.PlannerUserPrompts[1].Contains("could not be parsed"),
            $"second planner prompt should carry the rejection feedback:\n{_clientFactory.PlannerUserPrompts[1]}");
        var (ok, why) = CssSelectorHasProps(Read(GlobeCssRel), ".flight-schedule-container", "max-height", "overflow: auto");
        Assert.True(ok, $"{why} — plan: {plan?.Summary}");
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── 4. A CSS class defined by the run must be wired up before the job completes ──────

    [Fact]
    public async Task CssSchedulesTask_NewClassCreatedButNeverWired_CannotCompleteUntilRepaired()
    {
        // The real failure mode from the log: the agent created a rule ('.flight-detail-body')
        // whose class nothing in the template uses — dead CSS. The run must NOT be allowed to
        // declare the job complete while the class is unwired; verification fails and the
        // repair loop must wire it into the template.
        var cssOld = """
            .flight-schedule-container {
              padding: 4px;
            }
            """;
        var cssNew = """
            .flight-schedule-container {
              padding: 4px;
            }
            .flight-detail-body {
              max-height: 300px;
              overflow: auto;
            }
            """;
        var htmlOld = "<div *ngFor=\"let s of schedules\" class=\"flight-schedule-entry\">";
        var htmlNew = "<div *ngFor=\"let s of schedules\" class=\"flight-schedule-entry flight-detail-body\">";
        _clientFactory.PlannerReply = n => StepJson(GlobeCssRel,
            "Add a .flight-detail-body rule for the flight detail panel body", cssOld, cssNew);
        _clientFactory.RepairReply = n => ReplanJson(GlobeHtmlRel,
            "Wire .flight-detail-body into the schedule entry markup", htmlOld, htmlNew);

        var (_, plan, complete) = await Run("In the flight information popup, add a .flight-detail-body class with a max height");

        // The deterministic unwired-CSS check must have blocked completion after the initial
        // step and driven a repair pass — had it not fired, the run would have completed with
        // dead CSS and no repair call.
        Assert.True(complete, $"run must complete only after the class is wired — calls=[{string.Join(",", _clientFactory.Calls)}]");
        Assert.Contains("flight-detail-body", Read(GlobeCssRel));
        Assert.Contains("flight-detail-body", Read(GlobeHtmlRel));
        Assert.Single(_clientFactory.PlannerUserPrompts);
        Assert.NotEmpty(_clientFactory.RepairUserPrompts);
        // The replanner prompt carries the deterministic unwired-CSS issue — direct proof the
        // check (not the LLM verifier) blocked completion and drove the repair.
        var repairPrompt = _clientFactory.RepairUserPrompts[0];
        Assert.Contains("flight-detail-body", repairPrompt);
        Assert.Contains("unwired", repairPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── 4b. A CSS class REMOVED by the run must be cleaned out of the template too ────────
    //    The mirror of the unwired check: the run deletes a rule ('.flight-schedule-container')
    //    but leaves the template referencing the class — the element now points at a class that
    //    no longer exists, so its styling silently breaks. Verification must fail
    //    deterministically and the repair loop must strip the reference from the template;
    //    only then can the run complete.

    [Fact]
    public async Task CssSchedulesTask_RemovedClassStillReferenced_CannotCompleteUntilTemplateCleaned()
    {
        var cssNew = """
            .flight-detail-panel {
              padding: 12px;
            }
            """;
        var htmlOld = "<div class=\"flight-schedule-container\" *ngIf=\"schedules.length\">";
        var htmlNew = "<div *ngIf=\"schedules.length\">";
        _clientFactory.PlannerReply = n => StepJson(GlobeCssRel,
            "Remove the .flight-schedule-container rule from the stylesheet",
            GlobeCssFixture, cssNew);
        _clientFactory.RepairReply = n => ReplanJson(GlobeHtmlRel,
            "Remove the .flight-schedule-container class from the schedule container div", htmlOld, htmlNew);

        var (_, plan, complete) = await Run(
            "In the flight information popup, remove the .flight-schedule-container rule from the stylesheet");

        // The deterministic orphaned-template-reference check must have blocked completion after
        // the CSS-only step and driven a repair pass — had it not fired, the run would have
        // completed with the template still pointing at a class that no longer exists.
        Assert.True(complete, $"run must complete only after the template reference is cleaned — calls=[{string.Join(",", _clientFactory.Calls)}]");
        Assert.DoesNotContain("flight-schedule-container", Read(GlobeCssRel));
        Assert.DoesNotContain("flight-schedule-container", Read(GlobeHtmlRel));
        Assert.Single(_clientFactory.PlannerUserPrompts);
        Assert.NotEmpty(_clientFactory.RepairUserPrompts);
        // The replanner prompt carries the deterministic orphaned-reference issue — direct proof
        // the check (not the LLM verifier) blocked completion and drove the repair.
        var repairPrompt = _clientFactory.RepairUserPrompts[0];
        Assert.Contains("flight-schedule-container", repairPrompt);
        Assert.Contains("orphaned", repairPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── 5. Counting: "rename every occurrence of X to Y" (computed occurrence count) ─────
    //    The ground truth is a NUMBER: the fixture holds MAX_RETRIES exactly N times, so the
    //    correct answer is count(new)==N && count(old)==0. A "confident" single-line anchor
    //    would silently rename 1 of N — the ambiguous-anchor rejection refuses to guess.

    [Fact]
    public async Task RenameTask_CorrectPlanner_RenamesEveryComputedOccurrence()
    {
        // Computed BEFORE the run: how many occurrences must be renamed?
        var expectedCount = CountOccurrences(BenchConfigFixture, "MAX_RETRIES");
        Assert.True(expectedCount >= 3, "fixture must exercise a real counting task");
        _clientFactory.PlannerReply = n => StepJson(BenchConfigTsRel,
            "Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS",
            BenchConfigFixture, BenchConfigFixture.Replace("MAX_RETRIES", "MAX_ATTEMPTS"));

        var (_, plan, complete) = await Run(
            "Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS in the worker config",
            new List<string> { BenchConfigTsRel });

        var ts = Read(BenchConfigTsRel);
        Assert.Equal(0, CountOccurrences(ts, "MAX_RETRIES"));
        Assert.Equal(expectedCount, CountOccurrences(ts, "MAX_ATTEMPTS"));
        Assert.True(complete, $"run should complete — calls=[{string.Join(",", _clientFactory.Calls)}]");
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── 5c. Per-step LLM token spend (computed: each step's own planning + verification rounds) ──
    //    The step result must carry the ESTIMATED prompt+response token spend of ITS OWN labeled
    //    LLM rounds. The count is deterministic: a correct single-step run fires exactly 2 labeled
    //    rounds before the edit result is emitted (1 planner + 1 verify — the scripted verifier
    //    keeps at 95/100, which ends the rounds after round 1), and the step's prompt share must
    //    at least cover the recorded planner prompt, since the verify prompt only adds to it.

    [Fact]
    public async Task RenameTask_EditStepCarriesComputedLlmTokenSpend()
    {
        var expectedCount = CountOccurrences(BenchConfigFixture, "MAX_RETRIES");
        var fullyRenamed = BenchConfigFixture.Replace("MAX_RETRIES", "MAX_ATTEMPTS");
        _clientFactory.PlannerReply = n => StepJson(BenchConfigTsRel,
            "Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS",
            BenchConfigFixture, fullyRenamed);

        var (allSteps, plan, complete) = await Run(
            "Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS in the worker config",
            new List<string> { BenchConfigTsRel });

        Assert.True(complete, $"run should complete — calls=[{string.Join(",", _clientFactory.Calls)}]");
        Assert.Equal(expectedCount, CountOccurrences(Read(BenchConfigTsRel), "MAX_ATTEMPTS"));
        var editStep = allSteps.OfType<Dictionary<string, object?>>()
            .First(s => s.GetValueOrDefault("type")?.ToString() == "edit" &&
                        s.GetValueOrDefault("status")?.ToString() == "done");
        dynamic llm = editStep["llmTokens"]
            ?? throw new Exception("edit step result must carry llmTokens — per-step LLM spend");
        // Exactly the step's own rounds: 1 planner + 1 verification round (scripted keep/95
        // ends the rounds after round 1). Assessments/replans run AFTER the result is emitted
        // and must not leak into it.
        Assert.Equal(2, (int)llm.calls);
        Assert.True((int)llm.promptTokens > 0, "prompt share must be positive");
        Assert.True((int)llm.responseTokens > 0, "response share must be positive");
        Assert.Equal((int)llm.promptTokens + (int)llm.responseTokens, (int)llm.totalTokens);
        // Computed floor: the step's prompt share must at least cover the recorded planner
        // prompt (the verify prompt only adds to it) — proves the numbers are real estimates,
        // not placeholders.
        var plannerPromptTokens = AgentTokenMetrics.EstimateTokens(_clientFactory.PlannerUserPrompts[0]);
        Assert.True((int)llm.promptTokens >= plannerPromptTokens,
            $"step prompt share ({llm.promptTokens}) must cover the planner prompt ({plannerPromptTokens})");
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task LlmRoundMetrics_AccumulateExactEstimates_AndResetOnTake()
    {
        // Direct contract of the accumulator the wrappers feed: labeled rounds add exact
        // EstimateTokens(prompt)+EstimateTokens(response) each, TakeStepLlmMetrics snapshots
        // and RESETS (a second take is null), and unlabeled rounds record nothing.
        var controller = BuildController();
        var record = typeof(AgentController).GetMethod("RecordLlmRoundMetricsAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("RecordLlmRoundMetricsAsync not found");
        var take = typeof(AgentController).GetMethod("TakeStepLlmMetrics",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("TakeStepLlmMetrics not found");

        // No labeled rounds yet → nothing to attach.
        Assert.Null(take.Invoke(controller, null));

        const string sys = "You are a planner.";
        const string user = "plan the next atomic step";
        const string resp = "{\"planComplete\":false,\"step\":{\"file\":\"a.ts\",\"change\":\"x\"}}";
        await (Task)record.Invoke(controller, new object?[] { "planner step 1", sys, user, resp, false, CancellationToken.None })!;
        await (Task)record.Invoke(controller, new object?[] { "verify step 1 round 1/3", sys, user, resp, false, CancellationToken.None })!;

        dynamic m = take.Invoke(controller, null)!;
        Assert.NotNull(m);
        Assert.Equal(2, (int)m.calls);
        var expectedPrompt = 2 * (AgentTokenMetrics.EstimateTokens(sys) + AgentTokenMetrics.EstimateTokens(user));
        var expectedResponse = 2 * AgentTokenMetrics.EstimateTokens(resp);
        Assert.Equal(expectedPrompt, (int)m.promptTokens);
        Assert.Equal(expectedResponse, (int)m.responseTokens);
        Assert.Equal(expectedPrompt + expectedResponse, (int)m.totalTokens);

        // Take resets — a second take is null again.
        Assert.Null(take.Invoke(controller, null));

        // Unlabeled rounds record nothing.
        await (Task)record.Invoke(controller, new object?[] { null, "ignored sys", "ignored user", "x", false, CancellationToken.None })!;
        Assert.Null(take.Invoke(controller, null));
    }

    [Fact]
    public async Task RunLlmSpend_CumulativeAcrossSteps_NeverResetByTake()
    {
        // The live "tokens used" counter: unlike TakeStepLlmMetrics (per-step snapshot that
        // resets), RunLlmSpend keeps the CUMULATIVE run spend so the header can show the
        // total even when the discovery context is tiny (OS/benchmark runs with an empty
        // sandbox) — the numbers the per-step badges and the 📊 logs report, summed.
        var controller = BuildController();
        var record = typeof(AgentController).GetMethod("RecordLlmRoundMetricsAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("RecordLlmRoundMetricsAsync not found");
        var take = typeof(AgentController).GetMethod("TakeStepLlmMetrics",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("TakeStepLlmMetrics not found");
        var spend = typeof(AgentController).GetMethod("RunLlmSpend",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("RunLlmSpend not found");

        Assert.Null(spend.Invoke(controller, null));

        const string sys = "You are a planner.";
        const string user = "plan the next atomic step";
        const string resp = "{\"planComplete\":false,\"step\":{\"file\":\"a.ts\",\"change\":\"x\"}}";
        await (Task)record.Invoke(controller, new object?[] { "planner step 1", sys, user, resp, false, CancellationToken.None })!;
        await (Task)record.Invoke(controller, new object?[] { "verify step 1", sys, user, resp, false, CancellationToken.None })!;

        dynamic run1 = spend.Invoke(controller, null)!;
        Assert.NotNull(run1);
        Assert.Equal(2, (int)run1.calls);
        var expectedPrompt = 2 * (AgentTokenMetrics.EstimateTokens(sys) + AgentTokenMetrics.EstimateTokens(user));
        var expectedResponse = 2 * AgentTokenMetrics.EstimateTokens(resp);
        Assert.Equal(expectedPrompt, (int)run1.promptTokens);
        Assert.Equal(expectedResponse, (int)run1.responseTokens);
        Assert.Equal(expectedPrompt + expectedResponse, (int)run1.totalTokens);

        // A per-step TAKE snapshots+resets the step counters but must NOT touch the run total.
        dynamic taken = take.Invoke(controller, null)!;
        Assert.Equal(2, (int)taken.calls);
        Assert.Null(take.Invoke(controller, null)); // step counters reset

        // More rounds accumulate on top of the run total — still 4 calls, doubled values.
        await (Task)record.Invoke(controller, new object?[] { "planner step 2", sys, user, resp, false, CancellationToken.None })!;
        await (Task)record.Invoke(controller, new object?[] { "web assess", sys, user, resp, false, CancellationToken.None })!;
        dynamic run2 = spend.Invoke(controller, null)!;
        Assert.Equal(4, (int)run2.calls);
        Assert.Equal(2 * expectedPrompt, (int)run2.promptTokens);
        Assert.Equal(2 * expectedResponse, (int)run2.responseTokens);
        Assert.Equal(2 * (expectedPrompt + expectedResponse), (int)run2.totalTokens);
        // The run total is never reset by a take.
        dynamic run3 = spend.Invoke(controller, null)!;
        Assert.Equal(4, (int)run3.calls);
    }

    [Fact]
    public async Task RenameTask_AmbiguousSingleLineAnchor_NeverLandsPartialRename()
    {
        // The counting lesson applied: the model "confidently" anchors ONE occurrence line
        // ("rename every occurrence" with a single-line edit). The constant appears 5 times,
        // so the all-occurrence counting guard deterministically refuses to pick a victim —
        // a partial rename (1 of 5) would be corrupt data. The run must fail loudly, file
        // untouched. The description is the REALISTIC one (names the symbol) — the guard must
        // fire even though "MAX_RETRIES" appears in the file and would otherwise disambiguate
        // the anchor to one occurrence.
        _clientFactory.PlannerReply = n => StepJson(BenchConfigTsRel,
            "Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS",
            "retries: MAX_RETRIES", "retries: MAX_ATTEMPTS");

        var (_, plan, complete) = await Run(
            "Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS in the worker config",
            new List<string> { BenchConfigTsRel });

        var ts = Read(BenchConfigTsRel);
        Assert.Equal(CountOccurrences(BenchConfigFixture, "MAX_RETRIES"), CountOccurrences(ts, "MAX_RETRIES"));
        Assert.Equal(0, CountOccurrences(ts, "MAX_ATTEMPTS"));
        Assert.False(complete,
            $"an ambiguous partial rename must fail loudly, not complete — calls=[{string.Join(",", _clientFactory.Calls)}]; plan={plan?.Summary}");
    }

    // ── 5b. Post-execution rename-all completeness (deterministic, drives the repair loop) ──
    //    The deterministic rename check scans the CURRENT edited files for the old name. A
    //    "confident" PARTIAL rename — one unique occurrence replaced, four left — fails
    //    verification deterministically: the LLM verifier can claim completion, but the old
    //    name provably still occurs, so the repair loop replaces the rest and only then does
    //    the run complete. A correct full rename in one step publishes the positive pass entry
    //    instead (the check ran and passed, visible on the card).

    [Fact]
    public async Task RenameAllTask_PartialRename_DeterministicCheckDrivesRepairToCompletion()
    {
        // The planner lands ONE of the five occurrences — the alpha line is a unique anchor
        // (each id line differs, so this is not the ambiguous multi-match the counting guard
        // rejects) — and stops, declaring the plan complete.
        var afterPartial = BenchConfigFixture.Replace("{ id: 'alpha', retries: MAX_RETRIES }",
            "{ id: 'alpha', retries: MAX_ATTEMPTS }");
        Assert.Equal(4, CountOccurrences(afterPartial, "MAX_RETRIES"));
        _clientFactory.PlannerReply = n => n == 1
            ? StepJson(BenchConfigTsRel, "Rename the alpha entry's MAX_RETRIES to MAX_ATTEMPTS",
                "{ id: 'alpha', retries: MAX_RETRIES },", "{ id: 'alpha', retries: MAX_ATTEMPTS },")
            : "{\"planComplete\": true, \"completionReason\": \"plan complete\"}";
        // Repair: the deterministic check's CONFIRMED issue reaches the replanner, which fixes
        // the remaining four occurrences with a whole-file replacement.
        var fullyRenamed = BenchConfigFixture.Replace("MAX_RETRIES", "MAX_ATTEMPTS");
        _clientFactory.RepairReply = n => ReplanJson(BenchConfigTsRel,
            "Rename the remaining MAX_RETRIES occurrences to MAX_ATTEMPTS",
            afterPartial, fullyRenamed);

        var (_, plan, complete) = await Run(
            "Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS in the worker config",
            new List<string> { BenchConfigTsRel });

        var ts = Read(BenchConfigTsRel);
        var remaining = CountOccurrences(ts, "MAX_RETRIES");
        Assert.True(remaining == 0,
            $"the partial rename must not survive to a completed run ({remaining} occurrence(s) left) — file:\n{ts}\ncalls=[{string.Join(",", _clientFactory.Calls)}]");
        Assert.Equal(5, CountOccurrences(ts, "MAX_ATTEMPTS"));
        Assert.True(complete,
            $"run should complete only after the repair — calls=[{string.Join(",", _clientFactory.Calls)}]; plan={plan?.Summary}");
        // Evidence the repair loop — not the planner — finished the rename: the repair
        // replanner ran, and its prompt carried the deterministic rename issue.
        Assert.NotEmpty(_clientFactory.RepairUserPrompts);
        Assert.Contains("MAX_RETRIES", _clientFactory.RepairUserPrompts[0]);
        Assert.Contains("still occurs", _clientFactory.RepairUserPrompts[0]);
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task RenameAllTask_CleanPass_PublishesRenameAllPassEntry()
    {
        // A correct single-step full rename: the deterministic check RAN (a rename-all request
        // was detected) and found zero remaining occurrences — the ground-truth section on the
        // card must list the positive pass entry, not hide the check.
        const string cardId = "gt-card-rename";
        await _boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));
        var fullyRenamed = BenchConfigFixture.Replace("MAX_RETRIES", "MAX_ATTEMPTS");
        _clientFactory.PlannerReply = n => n == 1
            ? StepJson(BenchConfigTsRel, "Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS",
                BenchConfigFixture, fullyRenamed)
            : "{\"planComplete\": true, \"completionReason\": \"plan complete\"}";

        var (_, plan, complete) = await Run(
            "Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS in the worker config",
            new List<string> { BenchConfigTsRel }, cardId);

        Assert.True(complete,
            $"run should complete — calls=[{string.Join(",", _clientFactory.Calls)}]; plan={plan?.Summary}");
        Assert.Empty(_clientFactory.RepairUserPrompts);
        var gt = ReadGroundTruth(await _boardData.LoadRawAsync(), cardId);
        Assert.NotNull(gt);
        Assert.Contains(gt!, e => e.Contains("✓ Rename-all:", StringComparison.Ordinal));
        Assert.Contains(gt!, e => e.Contains("MAX_RETRIES", StringComparison.Ordinal) && e.Contains("MAX_ATTEMPTS", StringComparison.Ordinal));
        Assert.DoesNotContain(gt!, e => e.Contains("RENAME-ALL INCOMPLETE", StringComparison.Ordinal));
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task RenameTask_NonJsonPlannerOutput_RejectedThenCorrected()
    {
        // Third behavior of the discipline, rename shape: turn 1 is the model's confident-wrong
        // prose (no JSON at all); turn 2 is the corrected full rename. The garbage proposal is
        // rejected with parse feedback, then the corrected step lands and still reaches the
        // computed occurrence ground truth (0 old, N new).
        var fullyRenamed = BenchConfigFixture.Replace("MAX_RETRIES", "MAX_ATTEMPTS");
        _clientFactory.PlannerReply = n => n == 1
            ? "I need to think about this carefully. Let me analyze how the worker config is structured before renaming the constant."
            : StepJson(BenchConfigTsRel, "Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS",
                BenchConfigFixture, fullyRenamed);

        var (_, plan, complete) = await Run(
            "Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS in the worker config",
            new List<string> { BenchConfigTsRel });

        Assert.True(complete, $"run should complete — calls=[{string.Join(",", _clientFactory.Calls)}]");
        Assert.Equal(2, _clientFactory.PlannerUserPrompts.Count);
        Assert.True(
            _clientFactory.PlannerUserPrompts[1].Contains("REJECTED ATTEMPTS") ||
            _clientFactory.PlannerUserPrompts[1].Contains("could not be parsed"),
            $"second planner prompt should carry the rejection feedback:\n{_clientFactory.PlannerUserPrompts[1]}");
        var ts = Read(BenchConfigTsRel);
        Assert.Equal(0, CountOccurrences(ts, "MAX_RETRIES"));
        Assert.Equal(CountOccurrences(BenchConfigFixture, "MAX_RETRIES"), CountOccurrences(ts, "MAX_ATTEMPTS"));
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── 5c. Requirement-checklist separation (never hijack a run) ────────────────────────
    //    The extracted EXPLICIT REQUIREMENTS CHECKLIST is threaded into the PLANNER prompt as
    //    its own section — it is NEVER appended to the task `prompt`, because the task text
    //    feeds the web-need detectors (TaskHintsWebNeed / ConfirmWebNeedAsync) and the OS-task
    //    classifier. A checklist item that happens to contain "search" / "fetch" / "current" /
    //    "latest" would trip the deliberately-broad web hints and hijack a plain code run into
    //    a web task. This test scripts exactly that: a plain rename task, a checklist whose
    //    items carry hint words, and asserts the planner still sees the checklist section while
    //    the web-need classifier is never even called (no unmatched LLM calls, no web steps).

    [Fact]
    public async Task ChecklistWithWebHintWords_DoesNotHijackPlainRun_PlannerStillSeesChecklist()
    {
        var fullyRenamed = BenchConfigFixture.Replace("MAX_RETRIES", "MAX_ATTEMPTS");
        _clientFactory.PlannerReply = n => n == 1
            ? StepJson(BenchConfigTsRel, "Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS",
                BenchConfigFixture, fullyRenamed)
            : "{\"planComplete\": true, \"completionReason\": \"plan complete\"}";
        // The extracted checklist carries the exact words that would trip TaskHintsWebNeed if
        // they ever reached the task prompt ("latest" and "current" are both WebNeedHints).
        _clientFactory.ChecklistReply = () => "{\"requirements\": [" +
            "\"the rename must reflect the latest computed configuration\", " +
            "\"verify the current file on disk is updated\"]}";

        var (allSteps, plan, complete) = await Run(
            "Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS in the worker config",
            new List<string> { BenchConfigTsRel });

        // The run completed with zero unscripted LLM calls — had the checklist been appended to
        // the task prompt, TaskHintsWebNeed would have fired at the planComplete gate and the
        // web-need classifier would have been invoked (an unmatched call).
        Assert.Empty(_clientFactory.Unmatched);
        Assert.DoesNotContain(_clientFactory.Calls, c => c == "unknown");
        // The planner still got the checklist — as its own section, not inside the task text.
        Assert.NotEmpty(_clientFactory.PlannerUserPrompts);
        var firstPlannerPrompt = _clientFactory.PlannerUserPrompts[0];
        Assert.Contains("### EXPLICIT REQUIREMENTS CHECKLIST", firstPlannerPrompt);
        Assert.Contains("latest computed configuration", firstPlannerPrompt);
        // And no web step was ever proposed or executed.
        var webSteps = allSteps.OfType<Dictionary<string, object?>>().Count(s =>
            s.GetValueOrDefault("path")?.ToString() is "_web_search" or "_web_fetch");
        Assert.Equal(0, webSteps);
        var ts = Read(BenchConfigTsRel);
        Assert.Equal(0, CountOccurrences(ts, "MAX_RETRIES"));
        Assert.Equal(CountOccurrences(BenchConfigFixture, "MAX_RETRIES"), CountOccurrences(ts, "MAX_ATTEMPTS"));
        Assert.True(complete,
            $"run should complete — calls=[{string.Join(",", _clientFactory.Calls)}]; plan={plan?.Summary}");
    }

    // ── 6. Aggregation: "group N benchmarks by name" (computed group structure) ─────────
    //    The ground truth is a STRUCTURE computed from the fixture ({"bm_a":[3,7],"bm_b":[5],
    //    "bm_c":[2,6,9]}). A grouping that reads well but duplicates an entry is caught by
    //    counting the line-anchored entry keys (7 vs 6) before it lands.

    [Fact]
    public async Task GroupTask_CorrectPlanner_ReproducesComputedGroupStructure()
    {
        // Expected structure is COMPUTED from the flat fixture, never hardcoded.
        var expected = ExpectedGroupedBenchmarks(BenchmarkDataFixture);
        _clientFactory.PlannerReply = n => StepJson(BenchmarkDataTsRel,
            "Group the benchmark data by name",
            BenchmarkDataFixture, GroupedBenchmarksCorrect);

        var (_, plan, complete) = await Run(
            "Group the 6 benchmarks by name in the benchmark data file",
            new List<string> { BenchmarkDataTsRel });

        Assert.Equal(expected, CanonicalGroupedBenchmarks(Read(BenchmarkDataTsRel)));
        Assert.True(complete, $"run should complete — calls=[{string.Join(",", _clientFactory.Calls)}]");
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task GroupTask_DuplicateGroupKey_GuardRejects_NeverLands()
    {
        // The confident-wrong grouping: bm_a is declared TWICE as a group key (a merge
        // artifact that reads plausibly — every benchmark is present, the totals add up).
        // DetectDuplicatePropertyAddition counts the line-anchored group keys (2 vs 0 in the
        // flat input) and rejects the step before it lands — the file must stay flat.
        _clientFactory.PlannerReply = n => StepJson(BenchmarkDataTsRel,
            "Group the benchmark data by name",
            BenchmarkDataFixture, GroupedBenchmarksWithDuplicate);

        var (_, plan, complete) = await Run(
            "Group the 6 benchmarks by name in the benchmark data file",
            new List<string> { BenchmarkDataTsRel });

        Assert.Equal(BenchmarkDataFixture, Read(BenchmarkDataTsRel));
        Assert.False(complete,
            $"a duplicated-entry grouping must fail loudly, not complete — calls=[{string.Join(",", _clientFactory.Calls)}]; plan={plan?.Summary}");
    }

    [Fact]
    public async Task GroupTask_DroppedEntry_GuardRejects_NeverLands()
    {
        // The drop mirror of the duplicate-key guard: every group key present, the output reads
        // like a valid grouping, but bm_a's second entry (metric: 3) was silently dropped — 5
        // entries in the grouped output vs 6 in the flat input. The deterministic entry-count
        // guard rejects the step before it lands — the file must stay flat and the run must
        // fail loudly instead of completing "successfully" with missing data.
        _clientFactory.PlannerReply = n => StepJson(BenchmarkDataTsRel,
            "Group the benchmark data by name",
            BenchmarkDataFixture, GroupedBenchmarksWithDroppedEntry);

        var (_, plan, complete) = await Run(
            "Group the 6 benchmarks by name in the benchmark data file",
            new List<string> { BenchmarkDataTsRel });

        Assert.Equal(BenchmarkDataFixture, Read(BenchmarkDataTsRel));
        Assert.False(complete,
            $"a dropped-entry grouping must fail loudly, not complete — calls=[{string.Join(",", _clientFactory.Calls)}]; plan={plan?.Summary}");
    }

    [Fact]
    public async Task GroupTask_NonJsonPlannerOutput_RejectedThenCorrected()
    {
        // Third behavior of the discipline, group shape: turn 1 is the model's confident-wrong
        // prose (no JSON at all); turn 2 is the corrected grouping. The garbage proposal is
        // rejected with parse feedback, then the corrected step lands and still reproduces the
        // computed group structure (every entry preserved, each under its own group key).
        var expected = ExpectedGroupedBenchmarks(BenchmarkDataFixture);
        _clientFactory.PlannerReply = n => n == 1
            ? "I need to think about this carefully. Let me first understand how the benchmarks are laid out before grouping them."
            : StepJson(BenchmarkDataTsRel, "Group the benchmark data by name",
                BenchmarkDataFixture, GroupedBenchmarksCorrect);

        var (_, plan, complete) = await Run(
            "Group the 6 benchmarks by name in the benchmark data file",
            new List<string> { BenchmarkDataTsRel });

        Assert.True(complete, $"run should complete — calls=[{string.Join(",", _clientFactory.Calls)}]");
        Assert.Equal(2, _clientFactory.PlannerUserPrompts.Count);
        Assert.True(
            _clientFactory.PlannerUserPrompts[1].Contains("REJECTED ATTEMPTS") ||
            _clientFactory.PlannerUserPrompts[1].Contains("could not be parsed"),
            $"second planner prompt should carry the rejection feedback:\n{_clientFactory.PlannerUserPrompts[1]}");
        Assert.Equal(expected, CanonicalGroupedBenchmarks(Read(BenchmarkDataTsRel)));
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── 7. Arithmetic transform: "increase every rate limit by N" (computed numeric GT) ──
    //    The dataset is GENERATED (seeded, so byte-identical across runs) and the ground truth
    //    is a NUMBER computed from it BEFORE the agent runs: each known input limit + the known
    //    delta. This is the post's data-correctness case in its purest form — a wrong-number
    //    edit is structurally perfect (same shape, valid numbers, no guard smells it) and
    //    lands cleanly. ONLY the computed answer catches it.

    [Fact]
    public async Task ArithmeticTask_CorrectPlanner_ReproducesComputedNumericGroundTruth()
    {
        var (fixture, expected, delta, prompt) = GenerateArithmeticCase(0x4A12);
        Write(RateLimitsTsRel, fixture);
        _clientFactory.PlannerReply = n => StepJson(RateLimitsTsRel,
            $"Increase every rate limit by {delta}", fixture, TransformLimits(fixture, delta));

        var (_, plan, complete) = await Run(prompt, new List<string> { RateLimitsTsRel });

        // Computed BEFORE the run: every known input + the known delta, positionally.
        Assert.Equal(expected, ParseLimits(Read(RateLimitsTsRel)));
        Assert.True(complete, $"run should complete — calls=[{string.Join(",", _clientFactory.Calls)}]");
        Assert.Empty(_clientFactory.Unmatched);
        Assert.Single(_clientFactory.PlannerUserPrompts);
    }

    [Fact]
    public async Task ArithmeticTask_WrongDelta_ReadsFineButDataIncorrect_ComputedGroundTruthCatchesIt()
    {
        var (fixture, expected, delta, prompt) = GenerateArithmeticCase(0xBEEF);
        Write(RateLimitsTsRel, fixture);
        // The confident-wrong edit: the model "increases" by delta+5 instead of delta. The
        // output reads perfectly — same structure, valid numbers — and every deterministic
        // guard passes, so the run completes looking successful. This is the post's hardest
        // case: no edit-layer rule can smell a wrong number. Only the computed ground truth
        // (limits == fixture + delta) detects the incorrect data.
        var wrongDelta = delta + 5;
        _clientFactory.PlannerReply = n => StepJson(RateLimitsTsRel,
            $"Increase every rate limit by {delta}", fixture, TransformLimits(fixture, wrongDelta));

        var (_, plan, complete) = await Run(prompt, new List<string> { RateLimitsTsRel });

        var baseLimits = ParseLimits(fixture);
        var landed = ParseLimits(Read(RateLimitsTsRel));
        // The wrong data landed and the run reported success — that is exactly the danger.
        Assert.True(complete,
            $"the wrong-number edit must look like a successful run (that's the point) — calls=[{string.Join(",", _clientFactory.Calls)}]; plan={plan?.Summary}");
        Assert.Equal(baseLimits.Select(l => l + wrongDelta).ToArray(), landed);
        // ...and the computed answer is the ONLY thing that proves the data is incorrect.
        Assert.NotEqual(expected, landed);
    }

    [Fact]
    public async Task ArithmeticTask_PartialEdit_OnlyOneLineTransformed_ComputedGroundTruthCatchesPartialTransform()
    {
        var (fixture, expected, delta, prompt) = GenerateArithmeticCase(0x51F3);
        Write(RateLimitsTsRel, fixture);
        // The confident-wrong partial: the model anchors ONE unique region line and transforms
        // only that limit, leaving the rest untouched — "increase every rate limit" becomes
        // "increase one". The anchor is unambiguous (regions are distinct), so no guard fires;
        // the run looks done. Only the computed answer catches the partial transform.
        var firstLine = fixture.Split('\n')[1];
        var firstLimit = ParseLimits(fixture)[0];
        var partialLine = firstLine.Replace($"limit: {firstLimit}", $"limit: {firstLimit + delta}");
        _clientFactory.PlannerReply = n => StepJson(RateLimitsTsRel,
            $"Increase every rate limit by {delta}", firstLine, partialLine);

        var (_, plan, complete) = await Run(prompt, new List<string> { RateLimitsTsRel });

        var baseLimits = ParseLimits(fixture);
        var landed = ParseLimits(Read(RateLimitsTsRel));
        Assert.Equal(baseLimits[0] + delta, landed[0]);                    // the one line it touched
        Assert.Equal(baseLimits.Skip(1).ToArray(), landed.Skip(1).ToArray()); // everything else untouched
        Assert.NotEqual(expected, landed); // only the computed answer catches the partial transform
        Assert.True(complete,
            $"the partial edit looks like a successful run — calls=[{string.Join(",", _clientFactory.Calls)}]; plan={plan?.Summary}");
    }

    [Fact]
    public void ArithmeticGuardSweep_SeededDatasets_CorrectTransformMatchesComputedAnswer_WrongDeltaNeverMatches()
    {
        // The generator sweep (no pipeline, milliseconds): for every seeded dataset the
        // whole-file transform must land and reproduce the COMPUTED answer exactly, and a
        // wrong-delta transform must never coincide with it. Byte-identical across runs.
        var checkedDocs = 0;
        for (var seed = 0; seed < 12; seed++)
        {
            var (fixture, expected, delta, _) = GenerateArithmeticCase(seed);
            var (replaced, newContent, err, _) =
                AgentEditHeuristics.TryReplaceSafe(fixture, fixture, TransformLimits(fixture, delta));
            Assert.True(replaced, $"doc #{seed}: whole-file anchor must apply — {err}");
            Assert.Equal(expected, ParseLimits(newContent));
            var wrong = ParseLimits(TransformLimits(fixture, delta + 5));
            Assert.NotEqual(expected, wrong);
            checkedDocs++;
        }
        Assert.Equal(12, checkedDocs);
    }

    // ── Multi-turn chain: the correct answer depends on a value established in turn 2 ────
    // The task spans three planner turns: turn 1 establishes the RETRY_LIMIT constant,
    // turn 2 establishes the loadWithRetries() wrapper (its NAME is the value later turns
    // must reference), and turn 3 must call EXACTLY that name. This is the Reddit-post
    // "context failures only show up at turn 4 when the user references something from
    // turn 2" class: a context failure at turn 3 references the chain inconsistently
    // (loadWithRetry() — reads fine, data wrong). Ground truth is COMPUTED from the
    // fixture before the run, so the wrong variant fails deterministically.

    /// <summary>Computed ground truth for the multi-turn chain: the constant from turn 1,
    /// the wrapper defined in turn 2, and the constructor referencing EXACTLY that name.
    /// The wrong variant's `loadWithRetry(` is never a substring of the correct
    /// `loadWithRetries()` — so the inconsistency is detectable, not a wording artifact.</summary>
    private static bool MultiTurnChainTruth(string ts) =>
        ts.Contains("RETRY_LIMIT = 5") &&
        ts.Contains("loadWithRetries()") &&
        ts.Contains("this.loadWithRetries()") &&
        !ts.Contains("this.loadWithRetry(");

    [Fact]
    public async Task MultiTurnChainTask_CorrectPlanner_ReproducesComputedGroundTruth()
    {
        _clientFactory.PlannerReply = n => n switch
        {
            1 => StepJson(MetricsTsRel, AddRetryLimitChange, AddRetryLimitOld, AddRetryLimitNew),
            2 => StepJson(MetricsTsRel, AddWrapperChange, AddWrapperOld, AddWrapperNew),
            3 => StepJson(MetricsTsRel, WireCtorChange, WireCtorOld, WireCtorNewCorrect),
            _ => "{\"planComplete\": true, \"completionReason\": \"plan complete\"}"
        };
        _clientFactory.AssessComplete = MultiTurnChainTruth;
        _clientFactory.AssessTargetRel = MetricsTsRel;

        var (_, plan, complete) = await Run(MultiTurnChainPrompt, new List<string> { MetricsTsRel });

        var ts = Read(MetricsTsRel);
        Assert.True(MultiTurnChainTruth(ts),
            $"computed ground truth NOT reproduced — plan: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; file:\n{ts}");
        Assert.True(complete,
            $"run should complete — calls=[{string.Join(",", _clientFactory.Calls)}]");
        // Multi-turn by construction: three distinct executed steps, each gated on the
        // previous one by the between-steps assessment (which only says complete once the
        // full chain is on disk).
        Assert.True(_clientFactory.PlannerUserPrompts.Count >= 3,
            $"expected >= 3 planner turns (turn 3 depends on turn 2's name), got {_clientFactory.PlannerUserPrompts.Count}");
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task MultiTurnChainTask_WrongVariant_InconsistentReference_FailsDeterministically()
    {
        // Context-failure trajectory: turn 3 forgot the name established in turn 2 and wired
        // `loadWithRetry()` — the wrong variant that reads fine but references the chain
        // inconsistently, then declares the plan complete. The computed ground truth must
        // fail it deterministically: the between-steps assessment keeps it incomplete, the
        // planComplete claim right after is RE-ASSESSED and rejected (never trusted at face
        // value), and the post-execution verifier flags the inconsistency as a CONFIRMED
        // issue so the repair loop fixes the wiring — the inconsistent reference can never
        // survive to a completed run.
        _clientFactory.PlannerReply = n => n switch
        {
            1 => StepJson(MetricsTsRel, AddRetryLimitChange, AddRetryLimitOld, AddRetryLimitNew),
            2 => StepJson(MetricsTsRel, AddWrapperChange, AddWrapperOld, AddWrapperNew),
            3 => StepJson(MetricsTsRel, "Wire the retry wrapper into the constructor", WireCtorOld, WireCtorNewWrong),
            _ => "{\"planComplete\": true, \"completionReason\": \"nothing left to do\"}"
        };
        _clientFactory.AssessComplete = MultiTurnChainTruth;
        _clientFactory.AssessTargetRel = MetricsTsRel;
        // Content-aware verifier: while the inconsistent reference is on disk it reports a
        // CONFIRMED issue (non-empty issues — so the "all steps done, no confirmed issues →
        // trust step truth" override cannot rescue the run), and once repaired it passes.
        _clientFactory.VerifierReply = () =>
            MultiTurnChainTruth(Read(MetricsTsRel))
                ? "{\"complete\": true, \"reason\": \"chain consistent\", \"issues\": []}"
                : "{\"complete\": false, \"reason\": \"ctor calls loadWithRetry() but loadWithRetries() is the method established in turn 2\", " +
                  "\"issues\": [\"constructor calls loadWithRetry() but loadWithRetries() is the defined method — inconsistent reference\"]}";
        _clientFactory.RepairReply = n => ReplanJson(MetricsTsRel,
            "Fix the constructor to call loadWithRetries() (the name established in turn 2)",
            WireCtorWrongCallOld, WireCtorWrongCallNew);

        var (allSteps, plan, complete) = await Run(MultiTurnChainPrompt, new List<string> { MetricsTsRel });

        var ts = Read(MetricsTsRel);
        // The inconsistent reference must NEVER survive to a completed run.
        var stepTruth = string.Join("\n", allSteps.OfType<Dictionary<string, object?>>()
            .Where(s => s.TryGetValue("type", out var t) && t?.ToString() is "edit" or "create")
            .Select(s => $"{s.GetValueOrDefault("path")} :: {s.GetValueOrDefault("status")} :: {s.GetValueOrDefault("error")} :: {s.GetValueOrDefault("reason")}"));
        var planSteps = plan?.Plan == null ? "<null>" : string.Join("\n", plan.Plan.Select(p => $"{p.File} :: {p.Change}"));
        Assert.False(ts.Contains("this.loadWithRetry(", StringComparison.Ordinal),
            $"the context-failure variant survived — plan: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; file:\n{ts}\nSTEP STATUSES:\n{stepTruth}\nPLAN:\n{planSteps}");
        Assert.True(MultiTurnChainTruth(ts),
            $"computed ground truth NOT satisfied after repair — calls=[{string.Join(",", _clientFactory.Calls)}]; file:\n{ts}\nSTEP STATUSES:\n{stepTruth}");
        Assert.True(complete,
            $"run should complete only after the repair — calls=[{string.Join(",", _clientFactory.Calls)}]");
        // Evidence the pipeline did NOT trust the wrong variant at face value: the
        // planComplete claim after the context-failure step was rejected (>= 4 planner
        // prompts = 3 executed steps + at least one rejected completion claim), and the
        // repair planner ran with the inconsistency visible in its prompt.
        Assert.True(_clientFactory.PlannerUserPrompts.Count >= 4,
            $"expected the planComplete claim to be rejected (>= 4 planner turns), got {_clientFactory.PlannerUserPrompts.Count}");
        Assert.NotEmpty(_clientFactory.RepairUserPrompts);
        Assert.Contains("loadWithRetry", _clientFactory.RepairUserPrompts[0]);
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── (click) handler ground-truth task ────────────────────────────────────────────────
    // The correct answer REQUIRES a (click) handler that references the REAL method: the
    // template must gain an Open button wired to `vm.openCard(...)` — never a typo'd variant
    // (`vm.opnCard`) that reads fine but calls a method that doesn't exist. The apply-time
    // hallucinated-property guard rejects the typo against the real `vm.openCard` already
    // present in the template; the ground truth is the exact expected handler string.

    /// <summary>Computed ground truth for the click-handler task: an Open button whose
    /// (click) handler references the REAL `vm.openCard` method, and no typo'd variant.
    /// Deliberately anchored on the handler VALUE + label ("(click)=\"vm.openCard(card.id)\">Open"),
    /// not on "button (click)" — the apply pipeline's self-heal normalizes `word (` → `word(`
    /// in HTML attribute lists, so the space before `(` must not be part of the contract.</summary>
    private static bool ClickHandlerTruth(string html) =>
        html.Contains("(click)=\"vm.openCard(card.id)\">Open</button>", StringComparison.Ordinal) &&
        !html.Contains("vm.opnCard(", StringComparison.Ordinal);

    [Fact]
    public async Task ClickHandlerTask_CorrectPlanner_ReferencesRealMethod()
    {
        _clientFactory.PlannerReply = n => n == 1
            ? StepJson(CardHtmlRel, "Add an Open button calling vm.openCard() with the card id",
                DetailsButtonOld, DetailsPlusOpenCorrect)
            : "{\"planComplete\": true, \"completionReason\": \"plan complete\"}";

        var (_, plan, complete) = await Run(ClickHandlerTaskPrompt, new List<string> { CardHtmlRel, CardTsRel });

        var html = Read(CardHtmlRel);
        Assert.True(ClickHandlerTruth(html),
            $"computed ground truth NOT reproduced — plan: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; file:\n{html}");
        Assert.True(complete,
            $"run should complete — calls=[{string.Join(",", _clientFactory.Calls)}]");
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task ClickHandlerTask_HallucinatedHandlerTypo_NeverLands()
    {
        // The hallucination from real runs: the planner "wires" the handler but typo's the
        // method name (`vm.opnCard` — dropped 'e'). It must be REJECTED before it lands, and
        // the run must NOT report success with the typo on disk — the fix must reference the
        // real `vm.openCard`.
        _clientFactory.PlannerReply = n => n == 1
            ? StepJson(CardHtmlRel, "Add an Open button calling vm.openCard() with the card id",
                DetailsButtonOld, DetailsPlusOpenWrong)
            : "{\"planComplete\": true, \"completionReason\": \"plan complete\"}";

        var (_, plan, complete) = await Run(ClickHandlerTaskPrompt, new List<string> { CardHtmlRel, CardTsRel });

        var html = Read(CardHtmlRel);
        // The typo must never land — the guard rejects the step against the real `vm.openCard`
        // already present in the template (the same file the guard scans).
        Assert.DoesNotContain("opnCard", html);
        Assert.DoesNotContain("vm.opnCard(", html);
        Assert.False(complete,
            $"a hallucinated handler typo must fail loudly, not complete — calls=[{string.Join(",", _clientFactory.Calls)}]; plan={plan?.Summary}");
        // The ground truth is knowingly unsatisfied — that is what the computed answer exists
        // to catch; the point is the run did not pretend otherwise. (No Unmatched assertion
        // here, mirroring the `ested` test: after the guard rejects the step the resolver is
        // legitimately re-invoked — and in the harness that scripted-but-unrouted call fails
        // the edit, which is exactly the loud-failure path being asserted.)
        Assert.False(ClickHandlerTruth(html), "ground truth must be able to detect the wrong answer");
    }

    [Fact]
    public async Task CssSchedulesTask_UnwiredClass_PublishesComputedGroundTruthToCard()
    {
        // The computed ground truth ("the new CSS class must be wired into the template")
        // must land on the CARD as _groundTruth — the human watching the run sees the
        // known-correct answer it is being checked against, live and after reload.
        const string cardId = "gt-card-unwired";
        await _boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));

        var cssOld = """
            .flight-schedule-container {
              padding: 4px;
            }
            """;
        var cssNew = """
            .flight-schedule-container {
              padding: 4px;
            }
            .flight-detail-body {
              max-height: 300px;
              overflow: auto;
            }
            """;
        var htmlOld = "<div *ngFor=\"let s of schedules\" class=\"flight-schedule-entry\">";
        var htmlNew = "<div *ngFor=\"let s of schedules\" class=\"flight-schedule-entry flight-detail-body\">";
        _clientFactory.PlannerReply = n => StepJson(GlobeCssRel,
            "Add a .flight-detail-body rule for the flight detail panel body", cssOld, cssNew);
        _clientFactory.RepairReply = n => ReplanJson(GlobeHtmlRel,
            "Wire .flight-detail-body into the schedule entry markup", htmlOld, htmlNew);

        var (_, plan, complete) = await Run(
            "In the flight information popup, add a .flight-detail-body class with a max height",
            new List<string> { GlobeCssRel, GlobeHtmlRel }, cardId);

        Assert.True(complete, $"run must complete after the class is wired — calls=[{string.Join(",", _clientFactory.Calls)}]");
        // The card must carry the computed expectation, persisted to boarddata.
        var gt = ReadGroundTruth(await _boardData.LoadRawAsync(), cardId);
        Assert.NotNull(gt);
        Assert.NotEmpty(gt);
        Assert.Contains("flight-detail-body", string.Join("\n", gt));
    }

    [Fact]
    public async Task VerifierClaimsEditNotMade_LandedEditIsConfirmedOnDisk_InPromptAndGroundTruth()
    {
        // THE regression from the popupUserTagUser?.username log: the title-line edit applied
        // ("✓ Edited", log shows old→new), yet the post-execution verifier claimed
        // "this change was not made in app.component.html" and a repair pass re-attempted it.
        // The verifier is an LLM and can hallucinate a landed edit as missing. The fix is
        // deterministic ground truth: every applied edit's newString is verified present on
        // disk BEFORE the verifier runs, injected into the verifier prompt as non-negotiable
        // facts, and surfaced on the card so the user can SEE the edit provably landed.
        const string cardId = "gt-card-not-made";
        await _boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));

        var oldStr = """
            <span class="cursorPointer userTagProfileLink" (click)="openUserTagProfile($event)">
             [title]="'Open profile of ' + popupUserTagUser?.username + ' in a new tab'">
            """;
        var newStr = """
            <span class="cursorPointer userTagProfileLink" (click)="openUserTagProfile($event)">
             [title]="'Open profile of ' + popupUserTagUser.username + ' in a new tab'">
            """;
        Write(AppHtmlRel, "<div>\n" + oldStr + "\n</div>\n" + new string('x', 20000)); // large file: windowing is exercised
        _clientFactory.PlannerReply = n => StepJson(AppHtmlRel,
            "Replace optional chaining with direct property access for username field", oldStr, newStr);
        // The scripted verifier reproduces the real log's hallucination: it claims the change
        // was NOT made even though the new text is provably on disk.
        _clientFactory.VerifierReply = () =>
            "{\"complete\": false, \"reason\": \"The task requires replacing '?' with '.' operator in " +
            "popupUserTagUser.username access, but this change was not made in app.component.html.\", " +
            "\"issues\": [{\"type\": \"CONFIRMED\", \"text\": \"optional chaining '?.' still exists instead " +
            "of direct property access '.'\"}]}";

        var (_, plan, _) = await Run(
            "Replace '?' with '.' in popupUserTagUser.username access in app.component.html",
            new List<string> { AppHtmlRel }, cardId);

        // The verifier prompt MUST carry the deterministic counter-fact before the verifier
        // answers — the whole point is that the LLM can no longer claim a provably-landed
        // edit is missing without the prompt contradicting it.
        var verifierPrompt = Assert.Single(_clientFactory.VerifierUserPrompts);
        Assert.Contains("CONFIRMED APPLIED EDITS", verifierPrompt);
        Assert.Contains("popupUserTagUser.username + ' in a new tab'", verifierPrompt);
        Assert.Contains("do NOT report these as missing", verifierPrompt);

        // The card carries the confirmed edit as ground truth — the user sees the edit
        // provably landed, independent of what the verifier LLM claims.
        var gt = ReadGroundTruth(await _boardData.LoadRawAsync(), cardId);
        Assert.NotNull(gt);
        Assert.Contains(gt!, g => g.Contains("Applied edit confirmed on disk", StringComparison.Ordinal) &&
                                   g.Contains("popupUserTagUser.username", StringComparison.Ordinal));
    }

    // ── Clean-pass ground truth (the section renders with passes, not just failures) ────
    // A fully clean run has nothing to FAIL, but the deterministic checks still RAN — the
    // card must show each one as a verified pass with its expectation instead of hiding the
    // 🎯 Ground truth section the moment nothing is wrong.

    [Fact]
    public async Task CleanPass_PublishesEachDeterministicCheckThatRanAndPassed()
    {
        // Two steps: the HTML edit introduces a NEW binding (vm.hideOpen — declared in the
        // component .ts, so the cross-file sibling resolution must let it through) and
        // references the .card-open-btn class; the CSS edit defines that class. Every
        // deterministic check runs and passes: template bindings resolve against the .ts,
        // the bare-selector scan finds nothing, the new class is wired into the connected
        // template. The content-aware assessor (computed ground truth over the real CSS)
        // keeps the loop planning until the class exists, then the run completes clean.
        const string cardId = "gt-card-clean";
        await _boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));
        _clientFactory.AssessComplete = content => content.Contains(".card-open-btn", StringComparison.Ordinal);
        _clientFactory.AssessTargetRel = CardCssRel;
        _clientFactory.PlannerReply = n => n switch
        {
            1 => StepJson(CardHtmlRel, "Add an Open button calling vm.openCard() with the card id",
                DetailsButtonOld, DetailsPlusOpenClean),
            2 => StepJson(CardCssRel, "Add the .card-open-btn rule for the Open button",
                CardItemRule, CardItemRulePlusOpenBtn),
            _ => "{\"planComplete\": true, \"completionReason\": \"plan complete\"}"
        };

        var (_, plan, complete) = await Run(
            "In the CardComponent template, add an Open button next to each card's Details button " +
            "that also calls vm.openCard() with the card's id, and style it with a new .card-open-btn class.",
            new List<string> { CardHtmlRel, CardTsRel, CardCssRel }, cardId);

        Assert.True(complete, $"clean run must complete — calls=[{string.Join(",", _clientFactory.Calls)}]");
        var gt = ReadGroundTruth(await _boardData.LoadRawAsync(), cardId);
        Assert.NotNull(gt);
        Assert.NotEmpty(gt);
        var all = string.Join("\n", gt);
        // Every deterministic check that ran and passed is listed with its verified expectation.
        Assert.Contains("✓ Template bindings:", all);
        Assert.Contains("✓ CSS selector scan:", all);
        Assert.Contains("✓ CSS wiring:", all);
        Assert.Contains("✓ Applied edit confirmed on disk:", all);
        // Nothing failed — no deterministic issue text on the card.
        Assert.DoesNotContain("CONFIRMED", all);
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task CleanPass_OsOutputDemandSatisfied_PublishesOsOutputPass()
    {
        // The web-task clean pass ("search the web and write the data into a text file"):
        // zero repo edits, but the OS-output demand was satisfied — the demanded file REALLY
        // exists with content on disk (the written-check requires the actual file with
        // meaningful content, not just a command that mentions the folder). Pinned to an
        // absolute TEMP path so the deterministic check never depends on real-world state
        // (Desktop\ai_article_data.txt existing on the machine) — the old "on my desktop"
        // wording made this test pass/fail on desktop contents. The ground-truth section must
        // render with the OS check's pass — a clean pass is still a verified pass.
        const string cardId = "gt-card-os";
        var dir = Path.Combine(Path.GetTempPath(), "weaver_osv_gt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "ai_article_data.txt");
            File.WriteAllText(target, "data");
            await _boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));
            var prompt = $"search the web for an interesting and relevant AI article and write the data into a text file at \"{target}\"";
            var hasDemand = AgentOsOutputVerifier.TryGetOsFileOutputDemand(prompt, out var demand);
            Assert.True(hasDemand);
            Assert.Equal(dir, demand.DirectoryPath);
            Assert.Equal("ai_article_data.txt", demand.FileNameHint);
            var results = new List<object>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "_command", ["status"] = "done",
                    ["command"] = $"Set-Content -Path \"{target}\" -Value 'data'"
                }
            };

            var (complete, _, _, _, _) = await InvokePostExecuteVerify(prompt, results, cardId);

            Assert.True(complete);
            var gt = ReadGroundTruth(await _boardData.LoadRawAsync(), cardId);
            Assert.NotNull(gt);
            var pass = Assert.Single(gt!);
            Assert.Contains("✓ OS output:", pass);
            Assert.Contains("ai_article_data.txt", pass);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task PostExecuteVerify_ExtraCsvColumn_CompletesWithNoConfirmedIssue()
    {
        // Regression for the benchmark-16 "extra url column" guard: the simple dump task asks
        // for id + name, but the eager dump ALSO writes a 'url' column (the source of the
        // derived id). The verifier used to flag that superset column as a defect and drive a
        // corrective step to strip it. Now the verifier prompt explicitly declares extra data a
        // non-issue — so feeding a CSV with the extra column through PostExecuteVerify must
        // complete cleanly (no CONFIRMED issue). The scripted verifier follows the guidance;
        // the prompt-content assertions prove the guidance (and the CSV with the extra column)
        // actually reached the verifier, so this is a real lock and not a vacuous pass.
        const string cardId = "gt-card-csv-extra";
        await _boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));

        const string csvRel = "benchmark_test_16/pokemon_data.csv";
        var csv = "FETCHED_AT: 2026-08-13\n" +
                  "id,name,url\n" +
                  "1,bulbasaur,https://pokeapi.co/api/v2/pokemon/1/\n" +
                  "25,pikachu,https://pokeapi.co/api/v2/pokemon/25/\n";
        Write(csvRel, csv);

        var results = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "create", ["status"] = "created",
                ["path"] = csvRel, ["newStringPreview"] = csv
            }
        };

        var prompt = "Create a folder called 'benchmark_test_16' at the project root. " +
                     "Inside it, create a file called 'pokemon_data.csv' containing each Pokemon's " +
                     "id number and its name.";

        // The scripted verifier behaves as the guidance instructs: the extra url column is fine.
        _clientFactory.VerifierReply = () =>
            "{\"complete\": true, \"reason\": \"id and name are present\", \"issues\": []}";

        var (complete, _, confirmed, speculative, _) = await InvokePostExecuteVerify(prompt, results, cardId);

        Assert.True(complete,
            $"extra url column must not fail verification — calls=[{string.Join(",", _clientFactory.Calls)}]");
        Assert.Empty(confirmed);
        Assert.Empty(speculative);

        // The guidance that makes this trustworthy reached the verifier, and the CSV — extra
        // url column included — was actually shown in the prompt.
        var verifierPrompt = Assert.Single(_clientFactory.VerifierUserPrompts);
        Assert.Contains("EXTRA DATA IS NOT A DEFECT", verifierPrompt);
        Assert.Contains("id,name,url", verifierPrompt);
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task PostExecuteVerify_PureDumpWithExtraUrlColumn_CompletesWithoutVerifierLlm()
    {
        // The live benchmark-16 failure: a PURE DUMP task (fetch → demanded file, no
        // structured edits) whose eager dump wrote the file WITH an extra 'url' column. The
        // between-steps assessor already short-circuits pure dumps, but PostExecuteVerify
        // used to hand the deterministically-produced CSV to the verifier LLM anyway — and
        // the real verifier HALLUCINATED an invented requirement ("NO url column per
        // requirements" — text that never appears in the task) plus "Missing header row" for
        // a file that has one, driving a corrective step that would STRIP good data. The
        // prompt-guidance approach (EXTRA DATA IS NOT A DEFECT) is not enough — an LLM can
        // ignore it. PostExecuteVerify now short-circuits pure dumps DETERMINISTICALLY the
        // moment the demanded file is written: complete with ZERO verifier LLM calls.
        //
        // This test uses the REAL benchmark-16 description (web-hinted via the FRESHNESS
        // "current date" line, no structured-edit demands → IsPureDumpTask=true) and feeds the
        // exact extra-column CSV shape the eager dump produces. It asserts the verifier LLM
        // was NEVER consulted — VerifierUserPrompts stays empty — so the hallucination class
        // cannot happen at all, not merely that a scripted verifier chose to comply.
        const string cardId = "gt-card-csv-pure-dump";
        await _boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));

        const string csvRel = "benchmark_test_16/pokemon_data.csv";
        var csv = "FETCHED_AT: 2026-08-13\n" +
                  "id,name,url\n" +
                  "1,bulbasaur,https://pokeapi.co/api/v2/pokemon/1/\n" +
                  "25,pikachu,https://pokeapi.co/api/v2/pokemon/25/\n";
        Write(csvRel, csv);

        var results = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "create", ["status"] = "created",
                ["path"] = csvRel, ["newStringPreview"] = csv
            }
        };

        var prompt = BenchmarkService.GetBenchmarkPlans().First(p => p.Level == 16).Description;
        // The real description must classify as a PURE dump for this regression to exercise
        // the deterministic short-circuit (and for the pre-fix bug to have fired the verifier).
        Assert.True(WebNeedClassifier.IsWebNeed(prompt),
            "benchmark-16 must classify as a web need (the FRESHNESS 'current date' line)");

        // Deliberately leave VerifierReply unscripted — if the verifier LLM were consulted,
        // the harness would record the call and the test fails.
        var (complete, details, confirmed, speculative, _) = await InvokePostExecuteVerify(prompt, results, cardId);

        Assert.True(complete, $"pure dump must complete deterministically — details={details}");
        Assert.Empty(confirmed);
        Assert.Empty(speculative);
        // THE regression: the verifier LLM was never called. The deterministic short-circuit
        // (not the scripted guidance-compliance) completed the run.
        Assert.Empty(_clientFactory.VerifierUserPrompts);
        Assert.Empty(_clientFactory.Calls);
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task CleanPass_NoOsDemandAndNoEdits_PublishesNothing()
    {
        // When NO deterministic check ran (no edits, no OS demand), the ground-truth section
        // must stay hidden — nothing was verified, so there is nothing to list.
        const string cardId = "gt-card-none";
        await _boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));

        var (complete, _, _, _, _) = await InvokePostExecuteVerify(
            "Add a comment to the MetricsService constructor.", new List<object>(), cardId);

        Assert.True(complete);
        var gt = ReadGroundTruth(await _boardData.LoadRawAsync(), cardId);
        Assert.NotNull(gt);
        Assert.Empty(gt!);
    }

    [Fact]
    public async Task CleanPass_ComponentAndTemplateEdited_PublishesComponentWiringPass()
    {
        // The unrendered-component-logic check's positive side: a UI task whose component .ts
        // AND sibling template were both edited (nothing left unrendered) records a
        // "component wiring" pass alongside the template-binding pass.
        const string cardId = "gt-card-wiring";
        await _boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));
        var results = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "edit", ["status"] = "done", ["path"] = CardHtmlRel,
                ["newStringPreview"] = "<button (click)=\"vm.openCard(card.id)\">Details</button>"
            },
            new Dictionary<string, object?>
            {
                ["type"] = "edit", ["status"] = "done", ["path"] = CardTsRel,
                ["newStringPreview"] = "openCard(id: string): void { }"
            }
        };

        var (complete, _, _, _, _) = await InvokePostExecuteVerify(
            "In the CardComponent template, add an Open button wired to vm.openCard().", results, cardId);

        Assert.True(complete);
        var gt = ReadGroundTruth(await _boardData.LoadRawAsync(), cardId);
        Assert.NotNull(gt);
        var all = string.Join("\n", gt!);
        Assert.Contains("✓ Template bindings:", all);
        Assert.Contains("✓ Component wiring:", all);
        Assert.Contains("✓ Applied edit confirmed on disk:", all);
        Assert.DoesNotContain("CONFIRMED", all);
    }

    [Fact]
    public async Task PerStepGroundTruth_AttachedToPlanItem_AndVerifiedOnDisk()
    {
        // Each plan item carries ITS OWN deterministic expected outcome (per-step computed
        // ground truth): the click-handler step's expectation is the new Open-button line
        // present in the file — and once the step completes, the entry is marked verified
        // against disk (the edit landed), so the plan view shows ✓ per step.
        const string cardId = "gt-card-per-step";
        await _boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));
        _clientFactory.PlannerReply = n => n == 1
            ? StepJson(CardHtmlRel, "Add an Open button calling vm.openCard() with the card id",
                DetailsButtonOld, DetailsPlusOpenCorrect)
            : "{\"planComplete\": true, \"completionReason\": \"plan complete\"}";

        var (_, plan, complete) = await Run(ClickHandlerTaskPrompt, new List<string> { CardHtmlRel, CardTsRel }, cardId);

        Assert.True(complete, $"run must complete — calls=[{string.Join(",", _clientFactory.Calls)}]");
        var gt = ReadPlanItemGroundTruth(await _boardData.LoadRawAsync(), cardId, index: 0);
        Assert.NotNull(gt);
        Assert.NotEmpty(gt);
        var all = string.Join("\n", gt!.Select(g => g.text ?? ""));
        Assert.Contains("Expected: \"", all);
        Assert.Contains("present in " + CardHtmlRel, all);
        // The expectation is the known-correct answer for THIS step and it provably held:
        // the anchor (the new Open-button line, paren-spacing-normalized like the apply
        // self-heal) is on disk after the step completed.
        Assert.All(gt, g => Assert.True(g.verified, $"expected outcome must be verified on disk — {g.text}"));
    }

    [Fact]
    public async Task FinalVerificationReason_PersistedOnCard_AfterCompleteRun()
    {
        // The final verification verdict must land on the card as _verification — the reason
        // the run was verified complete, visible after the run instead of only in the log.
        const string cardId = "gt-card-verdict";
        await _boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));
        _clientFactory.PlannerReply = n => n == 1
            ? StepJson(CardHtmlRel, "Add an Open button calling vm.openCard() with the card id",
                DetailsButtonOld, DetailsPlusOpenCorrect)
            : "{\"planComplete\": true, \"completionReason\": \"plan complete\"}";

        var (_, plan, complete) = await Run(ClickHandlerTaskPrompt, new List<string> { CardHtmlRel, CardTsRel }, cardId);

        Assert.True(complete, $"run must complete — calls=[{string.Join(",", _clientFactory.Calls)}]");
        var verdict = ReadVerification(await _boardData.LoadRawAsync(), cardId);
        Assert.NotNull(verdict);
        Assert.True(verdict!.Value.complete);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Value.reason),
            "the final verification reason must be persisted on the card");
    }

    // ── Direct guard tests (the deterministic layer the ground truth relies on) ──────────

    [Fact]
    public void Guard_DetectsEstedTypo_InHtmlTemplateBinding()
    {
        var oldStr = "<span>Estimated: {{ s.departure.estimated | date:'short' }}</span>";
        var newStr = "<span>Estimated: {{ s.departure.ested | date:'short' }}</span>";
        var file = "<div *ngFor=\"let s of schedules\">" +
                   "<span>Estimated: {{ s.departure.estimated | date:'short' }}</span></div>";
        var result = AgentEditHeuristics.DetectHallucinatedProperties(oldStr, newStr, file, "globe.component.html");
        Assert.NotNull(result);
        Assert.Contains("ested", result);
        Assert.Contains("estimated", result); // "did you mean" names the real property
    }

    [Fact]
    public void Guard_DetectsEstedTypo_InTs()
    {
        var oldStr = "return this.departure.estimated;";
        var newStr = "return this.departure.ested;";
        var file = "export class FlightScheduleComponent {\n  departure = { estimated: '' };\n}";
        var result = AgentEditHeuristics.DetectHallucinatedProperties(oldStr, newStr, file, "flight-schedule.component.ts");
        Assert.NotNull(result);
        Assert.Contains("ested", result);
    }

    [Fact]
    public void Guard_Passes_PropertyDeclaredInlineInSameEdit()
    {
        var oldStr = "constructor() { }";
        var newStr = "constructor() { }\nitems: string[] = [];";
        var file = "export class C {\n  constructor() { }\n}";
        // `items` is declared with `:` in the SAME edit — the `\bprop\s*[:=]` check exempts it.
        Assert.Null(AgentEditHeuristics.DetectHallucinatedProperties(oldStr, newStr, file, "c.ts"));
    }

    [Fact]
    public void Guard_Passes_RealMethodOnExistingArray()
    {
        var oldStr = "constructor() { }";
        var newStr = "constructor() { }\ngetItems() { return this.items.slice(); }";
        var file = "export class C {\n  title = 'x';\n  items: string[] = [];\n  constructor() { }\n}";
        // `items` exists in the file (the ':' tokenizer fix) and `slice` has no similar sibling —
        // this exact edit used to be false-positive flagged as "did you mean 'items:'?"
        Assert.Null(AgentEditHeuristics.DetectHallucinatedProperties(oldStr, newStr, file, "c.ts"));
    }

    [Fact]
    public void Guard_Passes_NewWordLongerThanExisting()
    {
        var oldStr = "x = 1;";
        var newStr = "x = 1;\nthis.deleted = true;";
        var file = "export class C {\n  delete(): void { }\n}";
        // 'deleted' is LONGER than 'delete' — the typo heuristic must not flag a genuine new name.
        Assert.Null(AgentEditHeuristics.DetectHallucinatedProperties(oldStr, newStr, file, "c.ts"));
    }

    // ── HTML [property] binding values and (event) handler bodies ──────────────────────
    // The guard scans the EXPRESSION TEXT Angular evaluates: {{ }} bodies, [prop]="…" values,
    // and (event)="…" handler bodies. The binding TARGET ([class.foo], [style.width], (click))
    // names a class/style/event, not a property access — it must never be scanned, or every
    // `.foo`-style class token next to a real property would false-positive.

    [Fact]
    public void Guard_DetectsEstedTypo_InPropertyBindingValue()
    {
        var oldStr = "<div [ngClass]=\"vm.isActive ? 'on' : 'off'\"></div>";
        var newStr = "<div [ngClass]=\"vm.isActiv ? 'on' : 'off'\"></div>"; // typo: dropped the 'e'
        var file = "export class C {\n  vm = { isActive: true };\n}\n";
        var result = AgentEditHeuristics.DetectHallucinatedProperties(oldStr, newStr, file, "card.component.html");
        Assert.NotNull(result);
        Assert.Contains("isActiv", result);
        Assert.Contains("isActive", result); // "did you mean" names the real property
    }

    [Fact]
    public void Guard_DetectsEstedTypo_InEventHandlerBody()
    {
        var oldStr = "<button (click)=\"vm.onSubmit()\">Save</button>";
        var newStr = "<button (click)=\"vm.onSubmt()\">Save</button>"; // typo: dropped the 'i'
        var file = "export class C {\n  vm = { onSubmit(): void { } };\n}\n";
        var result = AgentEditHeuristics.DetectHallucinatedProperties(oldStr, newStr, file, "card.component.html");
        Assert.NotNull(result);
        Assert.Contains("onSubmt", result);
        Assert.Contains("onSubmit", result);
    }

    [Fact]
    public void Guard_DetectsTypo_InTwoWayBindingValue()
    {
        var oldStr = "<input [(ngModel)]=\"vm.filterText\">";
        var newStr = "<input [(ngModel)]=\"vm.fiterText\">"; // typo: dropped the 'l'
        var file = "export class C {\n  vm = { filterText: '' };\n}\n";
        var result = AgentEditHeuristics.DetectHallucinatedProperties(oldStr, newStr, file, "search.component.html");
        Assert.NotNull(result);
        Assert.Contains("fiterText", result);
        Assert.Contains("filterText", result);
    }

    // ── Structural directives (*ngIf / *ngFor / *ngSwitchCase) ─────────────────────────
    // The `*directive="…"` attribute carries an EVALUATED expression just like {{ }} / [binding] /
    // (event) — a typo inside it (vm.isActiv, vm.card, s.departure.ested) must get the same
    // hallucinated-property treatment. The directive NAME (*ngIf) is never scanned.

    [Fact]
    public void Guard_DetectsEstedTypo_InNgIfExpression()
    {
        var oldStr = "<div *ngIf=\"vm.isActive\">Ready</div>";
        var newStr = "<div *ngIf=\"vm.isActiv\">Ready</div>"; // typo: dropped the 'e'
        var file = "export class C {\n  vm = { isActive: true };\n}\n";
        var result = AgentEditHeuristics.DetectHallucinatedProperties(oldStr, newStr, file, "card.component.html");
        Assert.NotNull(result);
        Assert.Contains("isActiv", result);
        Assert.Contains("isActive", result); // "did you mean" names the real property
    }

    [Fact]
    public void Guard_DetectsEstedTypo_InNgSwitchCaseExpression()
    {
        var oldStr = "<div [ngSwitch]=\"vm.mode\"><span *ngSwitchCase=\"vm.active\">On</span></div>";
        var newStr = "<div [ngSwitch]=\"vm.mode\"><span *ngSwitchCase=\"vm.activ\">On</span></div>"; // typo: dropped the 'e'
        var file = "export class C {\n  vm = { mode: 0, active: true };\n}\n";
        var result = AgentEditHeuristics.DetectHallucinatedProperties(oldStr, newStr, file, "card.component.html");
        Assert.NotNull(result);
        Assert.Contains("activ", result);
        Assert.Contains("active", result);
    }

    [Fact]
    public void Guard_DetectsTypo_InNgForCollectionExpression()
    {
        var oldStr = "<ul><li *ngFor=\"let c of vm.cards\">{{ c.name }}</li></ul>";
        var newStr = "<ul><li *ngFor=\"let c of vm.card\">{{ c.name }}</li></ul>"; // typo: dropped the 's'
        var file = "export class C {\n  vm = { cards: [] };\n}\n";
        var result = AgentEditHeuristics.DetectHallucinatedProperties(oldStr, newStr, file, "card.component.html");
        Assert.NotNull(result);
        Assert.Contains("card", result);
        Assert.Contains("cards", result); // "did you mean 'cards'?"
    }

    [Fact]
    public void Guard_Passes_NgForLoopVariable_IsNotTreatedAsProperty()
    {
        // The ngFor microsyntax declares a LOCAL (`let c`) and reads a dotted collection
        // (vm.cards) — the loop variable is NOT a property access and must never be flagged,
        // even when its name resembles an existing property (here 'card' vs the real 'cards').
        var oldStr = "<ul><li>{{ c.title }}</li></ul>";
        var newStr = "<ul><li *ngFor=\"let c of vm.cards\">{{ c.title }}</li></ul>";
        var file = "export class C {\n  vm = { cards: [] };\n}\n";
        Assert.Null(AgentEditHeuristics.DetectHallucinatedProperties(oldStr, newStr, file, "card.component.html"));
    }

    // ── Resolving bound properties against the component .ts ────────────────────────────
    // A template references members declared in the sibling .component.ts, which the HTML
    // never contains. The guard now merges the sibling's tokens into its known-word set: a
    // binding referencing a GENUINELY DECLARED member is exempt (no false positive against a
    // similar template token), while a typo of a real TS member is caught even when the real
    // name appears NOWHERE in the template.

    [Fact]
    public void Guard_Passes_TsDeclaredMember_AgainstSimilarTemplateToken()
    {
        // The false-positive class: `vm.item` is genuinely declared in the component TS, but
        // the template happens to contain the word 'items' (a different collection) — before
        // the sibling-resolution fix the guard flagged it as "did you mean 'items'?".
        var tsContent = "export class C {\n  vm = { item: { id: 1 } };\n}\n";
        var oldHtml = "<ul><li *ngFor=\"let i of items\">{{ i.id }}</li></ul>";
        var newHtml = "<ul><li *ngFor=\"let i of items\" [hidden]=\"vm.item\">{{ i.id }}</li></ul>";
        var result = AgentEditHeuristics.DetectHallucinatedProperties(
            oldHtml, newHtml, oldHtml, "card.component.html", tsContent);
        Assert.Null(result);
    }

    [Fact]
    public void Guard_DetectsTsMemberTypo_WhenRealNameAbsentFromTemplate()
    {
        // The cross-file typo: `vm.opnCard` (dropped 'e') references a method that exists only
        // in the component TS — the template itself never mentions 'openCard'. Before the fix
        // the guard had no way to know 'openCard' is real and the typo sailed through.
        var tsContent = "export class C {\n  vm = { openCard(id: string): void { } };\n}\n";
        var oldHtml = "<div>Loading…</div>";
        var newHtml = "<div><button (click)=\"vm.opnCard(1)\">Go</button></div>";
        var result = AgentEditHeuristics.DetectHallucinatedProperties(
            oldHtml, newHtml, oldHtml, "card.component.html", tsContent);
        Assert.NotNull(result);
        Assert.Contains("opnCard", result);
        Assert.Contains("openCard", result); // "did you mean" names the real TS member
    }

    [Fact]
    public void Guard_Passes_TsDeclaredMember_ExactReference()
    {
        // The happy path of the same setup: the exact declared member (vm.openCard) must pass.
        var tsContent = "export class C {\n  vm = { openCard(id: string): void { } };\n}\n";
        var oldHtml = "<div>Loading…</div>";
        var newHtml = "<div><button (click)=\"vm.openCard(1)\">Go</button></div>";
        Assert.Null(AgentEditHeuristics.DetectHallucinatedProperties(
            oldHtml, newHtml, oldHtml, "card.component.html", tsContent));
    }

    [Fact]
    public void Guard_Passes_BindingTargetClassToken_IsNeverScanned()
    {
        // `[class.activ]` is a binding TARGET naming the CSS class 'activ' — NOT a property
        // access. The class name is a dropped-letter variant of 'active' (a real class in the
        // file), so if the target were scanned the guard would false-positive. Only the value
        // (`vm.isActive`, which exists) may be scanned → must pass.
        var oldStr = "<div class=\"active\"></div>";
        var newStr = "<div class=\"active\" [class.activ]=\"vm.isActive\"></div>";
        var file = "export class C {\n  vm = { isActive: true };\n}\n.active { color: red; }\n";
        Assert.Null(AgentEditHeuristics.DetectHallucinatedProperties(oldStr, newStr, file, "card.component.html"));
    }

    [Fact]
    public void Guard_Passes_EventHandlerCallingExistingMethod()
    {
        // A genuine handler body calling an existing method must not be flagged — same edit in
        // TS passes today, so the HTML event path must behave identically. The `item` loop
        // variable is declared by the file's own `*ngFor`, exactly as in a real template.
        var oldStr = "<button>Save</button>";
        var newStr = """
            <ul><li *ngFor="let item of vm.items"><button (click)="vm.select(item)">Save</button></li></ul>
            """;
        var file = """
            <ul><li *ngFor="let item of vm.items"><button (click)="vm.select(item)">Save</button></li></ul>
            """;
        Assert.Null(AgentEditHeuristics.DetectHallucinatedProperties(oldStr, newStr, file, "card.component.html"));
    }

    [Fact]
    public void Guard_Passes_InterpolationAndBindings_AllExisting()
    {
        // Mixed template features, every referenced property genuinely present in the template
        // itself (ngFor-declared `card`, `vm.*` members): the scan must not invent a phantom
        // from `*ngIf`, `| async`, structural directives, event names, or class targets.
        var oldStr = "<div>Loading…</div>";
        var newStr = """
            <ul *ngIf="vm.isLoading | async">
              <li *ngFor="let card of vm.cards" [class.ready]="vm.isReady" (mouseenter)="vm.preview(card)">
                {{ vm.title }} <button (click)="vm.open(card.id)">Open</button>
              </li>
            </ul>
            """;
        var file = newStr; // the template IS the file being edited — its own tokens are the ground truth
        Assert.Null(AgentEditHeuristics.DetectHallucinatedProperties(oldStr, newStr, file, "card.component.html"));
    }

    // ── Direct guard tests for the counting / aggregation shapes ────────────────────────

    [Fact]
    public void Guard_RejectsSingleLineAnchor_WhenOccurrenceIsRepeatedFiveTimes()
    {
        // "Rename every occurrence" with a single-line anchor: the line appears 5 times, so
        // the apply layer must refuse to guess which occurrence instead of renaming just one.
        var (replaced, _, matchError, _) = AgentEditHeuristics.TryReplaceSafe(
            BenchConfigFixture, "retries: MAX_RETRIES", "retries: MAX_ATTEMPTS");
        Assert.False(replaced);
        Assert.NotNull(matchError);
        Assert.Contains("5 times", matchError);
    }

    [Fact]
    public void Guard_Passes_WholeFileAnchor_ForRenameEveryOccurrence()
    {
        var (replaced, newContent, matchError, _) = AgentEditHeuristics.TryReplaceSafe(
            BenchConfigFixture, BenchConfigFixture, BenchConfigFixture.Replace("MAX_RETRIES", "MAX_ATTEMPTS"));
        Assert.True(replaced);
        Assert.Null(matchError);
        Assert.Equal(0, CountOccurrences(newContent, "MAX_RETRIES"));
        Assert.Equal(CountOccurrences(BenchConfigFixture, "MAX_RETRIES"), CountOccurrences(newContent, "MAX_ATTEMPTS"));
    }

    [Fact]
    public void Guard_DetectsDuplicateGroupKey_InGroupedBenchmarkOutput()
    {
        // The merge-artifact hallucination: 'bm_a' declared twice as a group key (2 vs 0 in
        // the flat input) — counting the line-anchored keys catches it before it lands.
        var reason = AgentEditHeuristics.DetectDuplicatePropertyAddition(BenchmarkDataFixture, GroupedBenchmarksWithDuplicate);
        Assert.NotNull(reason);
        Assert.Contains("DUPLICATE PROPERTY ADDITION", reason);
        Assert.Contains("bm_a", reason);
    }

    [Fact]
    public void Guard_Passes_WellFormedGroupedBenchmarkOutput()
    {
        // The correct grouping declares each group key exactly once (0→1, never >1) and the
        // compact one-line entries keep 'name'/'metric' off line start, so nothing is flagged.
        Assert.Null(AgentEditHeuristics.DetectDuplicatePropertyAddition(BenchmarkDataFixture, GroupedBenchmarksCorrect));
    }

    [Fact]
    public void Guard_DetectsDroppedEntry_InGroupedBenchmarkOutput()
    {
        // The drop mirror: every group key present, but bm_a lost its second entry (5 vs 6) —
        // counting object-literal entries across the flat→grouped transform catches it.
        var reason = AgentEditHeuristics.DetectDroppedEntriesInGroupedOutput(
            BenchmarkDataFixture, GroupedBenchmarksWithDroppedEntry, "Group the benchmark data by name");
        Assert.NotNull(reason);
        Assert.Contains("GROUPED OUTPUT DROPS ENTRIES", reason);
        Assert.Contains("5 entries", reason);
        Assert.Contains("had 6", reason);
    }

    [Fact]
    public void Guard_Passes_WellFormedGroupedOutput_EntriesPreserved()
    {
        // The correct grouping preserves every entry (6 in, 6 out) — nothing to flag, even
        // though the flat→grouped transform itself matches the guard's shape.
        Assert.Null(AgentEditHeuristics.DetectDroppedEntriesInGroupedOutput(
            BenchmarkDataFixture, GroupedBenchmarksCorrect, "Group the benchmark data by name"));
    }

    [Fact]
    public void Guard_Passes_NonGroupingEdit_EvenWithFewerObjects()
    {
        // A delete-style edit legitimately reduces the object count — the change is not a
        // grouping, so the drop guard must stay out of its way.
        var fixture = BenchmarkDataFixture.Replace("\r\n", "\n");
        var afterDelete = fixture.Replace("  { name: 'bm_c', metric: 9 },\n", "");
        Assert.NotEqual(fixture, afterDelete);
        Assert.Null(AgentEditHeuristics.DetectDroppedEntriesInGroupedOutput(
            fixture, afterDelete, "Remove the bm_c:9 entry"));
    }

    [Fact]
    public void Guard_Passes_DropShapeWithoutGroupingChangeDescription()
    {
        // The shape alone (grouped output, fewer entries) must not fire — the guard is scoped
        // to edits whose change description calls the task aggregation/grouping, so a rewrite
        // described differently is not blocked.
        Assert.Null(AgentEditHeuristics.DetectDroppedEntriesInGroupedOutput(
            BenchmarkDataFixture, GroupedBenchmarksWithDroppedEntry, "Reformat the benchmark data"));
    }

    [Fact]
    public void Guard_Passes_RegroupingOfAlreadyGroupedData()
    {
        // Re-grouping an ALREADY-grouped file (fewer entries in a legitimate restructure) is
        // out of scope — the guard targets the flat→grouped transform only.
        Assert.Null(AgentEditHeuristics.DetectDroppedEntriesInGroupedOutput(
            GroupedBenchmarksCorrect, GroupedBenchmarksWithDroppedEntry, "Group the benchmark data by name"));
    }

    [Fact]
    public void Guard_Fires_WithEmptyChangeDescription_FallsBackToShapeOnly()
    {
        // An absent change description falls back to shape-only (old flat → new grouped with
        // fewer entries) so direct callers without a description still get the protection.
        Assert.NotNull(AgentEditHeuristics.DetectDroppedEntriesInGroupedOutput(
            BenchmarkDataFixture, GroupedBenchmarksWithDroppedEntry));
    }

    // ── Counting / aggregation ground-truth helpers (all answers are pure functions of
    //    the fixtures — computed BEFORE the agent runs, never hardcoded in assertions) ──

    private static int CountOccurrences(string text, string token) =>
        text.Split(token, StringSplitOptions.None).Length - 1;

    /// <summary>Canonical form of a GROUPED benchmark module: group key → sorted metric list.
    /// Parses each `key: [ … ]` block and — the structure check — asserts every entry inside
    /// carries its group's name, so a wrong-nested or duplicated entry can never match.
    /// Returns the canonical string so the test compares structurally against the expected.</summary>
    private static string CanonicalGroupedBenchmarks(string groupedTs)
    {
        var map = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (Match g in Regex.Matches(groupedTs, @"^\s*([A-Za-z_]\w*):\s*\[", RegexOptions.Multiline))
        {
            var key = g.Groups[1].Value;
            var openIdx = g.Index + g.Length - 1;
            var closeIdx = FindMatchingSquareBracket(groupedTs, openIdx);
            var block = groupedTs[openIdx..(closeIdx + 1)];
            var names = Regex.Matches(block, @"name:\s*'([^']+)'")
                .Select(m => m.Groups[1].Value).ToList();
            var metrics = Regex.Matches(block, @"metric:\s*(\d+)")
                .Select(m => int.Parse(m.Groups[1].Value)).OrderBy(x => x).ToList();
            foreach (var n in names)
                Assert.Equal(key, n); // entries must sit under their own name
            map[key] = metrics;
        }
        return JsonSerializer.Serialize(map);
    }

    private static int FindMatchingSquareBracket(string s, int openIdx)
    {
        var depth = 0;
        for (var i = openIdx; i < s.Length; i++)
        {
            if (s[i] == '[') depth++;
            else if (s[i] == ']') { depth--; if (depth == 0) return i; }
        }
        return s.Length - 1;
    }

    /// <summary>Computes the EXPECTED grouped structure from the flat fixture: group every
    /// entry by name, sort the metric lists — the answer is known before the agent runs.</summary>
    private static string ExpectedGroupedBenchmarks(string flatTs)
    {
        var names = Regex.Matches(flatTs, @"name:\s*'([^']+)'")
            .Select(m => m.Groups[1].Value).ToList();
        var metrics = Regex.Matches(flatTs, @"metric:\s*(\d+)")
            .Select(m => int.Parse(m.Groups[1].Value)).ToList();
        var map = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++)
        {
            if (!map.TryGetValue(names[i], out var list))
            {
                list = new List<int>();
                map[names[i]] = list;
            }
            list.Add(metrics[i]);
        }
        foreach (var list in map.Values) list.Sort();
        return JsonSerializer.Serialize(map);
    }

    // ── Arithmetic dataset generator (all answers are pure functions of the generated
    //    fixture — computed BEFORE the agent runs) ────────────────────────────────────────

    /// <summary>Generates a rate-limits dataset with a KNOWN delta: the ground truth (each
    /// input + delta) is computed here, before the question is ever asked. Seeded, so the
    /// dataset is byte-identical across runs and machines. The fixture stays compact (≤ 8
    /// lines, well under the targeted-anchor guard's whole-file limit) so the correct edit is
    /// a single whole-file replacement.</summary>
    private static (string fixture, int[] expected, int delta, string prompt) GenerateArithmeticCase(int seed)
    {
        var rng = new Random(seed);
        var regions = new[] { "us-east", "eu-west", "ap-south", "sa-east", "ca-central", "me-north" };
        var k = 3 + rng.Next(4); // 3-6 regions
        var limits = new int[k];
        for (var i = 0; i < k; i++) limits[i] = 10 + rng.Next(90); // 10-99
        var delta = 5 * (1 + rng.Next(20));                         // 5..100, never 0
        var sb = new StringBuilder("export const RATE_LIMITS = [\n");
        for (var i = 0; i < k; i++)
            sb.Append("  { region: '").Append(regions[i]).Append("', limit: ").Append(limits[i]).Append(" },\n");
        sb.Append("];\n");
        return (sb.ToString(), limits.Select(l => l + delta).ToArray(), delta,
            $"Increase every rate limit in RATE_LIMITS by {delta}");
    }

    /// <summary>Applies a numeric transform to every `limit:` in the fixture, in order.</summary>
    private static string TransformLimits(string fixture, int by) =>
        Regex.Replace(fixture, @"limit:\s*(\d+)",
            m => "limit: " + (int.Parse(m.Groups[1].Value) + by));

    /// <summary>Parses every `limit:` value, positionally, as the canonical file answer.</summary>
    private static int[] ParseLimits(string ts) =>
        Regex.Matches(ts, @"limit:\s*(\d+)").Select(m => int.Parse(m.Groups[1].Value)).ToArray();

    // ── Computed ground-truth helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Ground truth is COMPUTED here, never guessed: given the actual final CSS, does the
    /// selector's rule block contain every required declaration? The answer is a pure function
    /// of the file contents — we know it before the agent runs.
    /// </summary>
    private static (bool ok, string why) CssSelectorHasProps(string css, string selector, params string[] required)
    {
        var idx = css.IndexOf(selector, StringComparison.Ordinal);
        if (idx < 0) return (false, $"selector '{selector}' not found in CSS");
        var open = css.IndexOf('{', idx);
        if (open < 0) return (false, $"no rule block after '{selector}'");
        var close = css.IndexOf('}', open);
        if (close < 0) return (false, $"unterminated rule block for '{selector}'");
        var block = css[(open + 1)..close];
        var missing = required.Where(r => !block.Contains(r, StringComparison.Ordinal)).ToList();
        return missing.Count == 0
            ? (true, "")
            : (false, $"selector '{selector}' missing [{string.Join(", ", missing)}]; block was: {block}");
    }

    // ── Harness (mirrors AdversarialUserScenarioTests / InterleavedPipelineIntegrationTests) ─

    private async Task<(List<object> allSteps, AgentPlan? plan, bool complete)> Run(
        string prompt, IReadOnlyList<string>? attachedFiles = null, string? cardId = null)
    {
        var controller = BuildController();
        var method = typeof(AgentController).GetMethod("Orchestrate", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Orchestrate not found");
        var task = (Task<(List<object> allSteps, AgentPlan? plan, bool complete)>)method.Invoke(controller, new object?[]
        {
            prompt, _projectRoot, /*emitSse*/ false, CancellationToken.None,
            /*attachedFiles*/ attachedFiles ?? new List<string> { GlobeCssRel, GlobeHtmlRel },
            /*skipContextReview*/ false, /*steeringContext*/ null, /*skipQualityCheck*/ false,
            /*existingPlan*/ null, /*completedStepIndices*/ null, /*cardId*/ cardId,
            /*createTests*/ false, /*buildCommands*/ null, /*strictVerifier*/ null, /*webResults*/ null
        })!;
        return await task;
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

    private static List<string>? ReadGroundTruth(string? raw, string cardId)
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

    /// <summary>Reads the card's final verification verdict ({ complete, reason, at }).</summary>
    private static (bool complete, string? reason)? ReadVerification(string? raw, string cardId)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        using var doc = JsonDocument.Parse(raw);
        foreach (var col in new[] { "todo", "doing", "done", "selfImproving" })
        {
            if (!doc.RootElement.TryGetProperty(col, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var card in arr.EnumerateArray())
            {
                if (!card.TryGetProperty("id", out var id) || id.GetString() != cardId) continue;
                if (!card.TryGetProperty("_verification", out var v)) return null;
                return (v.TryGetProperty("complete", out var c) && c.GetBoolean(),
                        v.TryGetProperty("reason", out var r) ? r.GetString() : null);
            }
        }
        return null;
    }

    /// <summary>Reads the per-step ground truth attached to one plan item: the deterministic
    /// expected outcomes for THAT step, each with its on-disk verified flag.</summary>
    private static List<(string? text, bool? verified)>? ReadPlanItemGroundTruth(string? raw, string cardId, int index)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        using var doc = JsonDocument.Parse(raw);
        foreach (var col in new[] { "todo", "doing", "done", "selfImproving" })
        {
            if (!doc.RootElement.TryGetProperty(col, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var card in arr.EnumerateArray())
            {
                if (!card.TryGetProperty("id", out var id) || id.GetString() != cardId) continue;
                if (!card.TryGetProperty("_plan", out var plan) ||
                    !plan.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    return null;
                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("index", out var idx) || idx.GetInt32() != index) continue;
                    if (!item.TryGetProperty("groundTruth", out var gt) || gt.ValueKind != JsonValueKind.Array)
                        return new List<(string?, bool?)>(); // item found, no per-step ground truth
                    return gt.EnumerateArray().Select(e =>
                    {
                        string? text = e.TryGetProperty("text", out var t) ? t.GetString() : null;
                        bool? verified = null;
                        if (e.TryGetProperty("verified", out var v) &&
                            v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            verified = v.GetBoolean();
                        return (text, verified);
                    }).ToList();
                }
            }
        }
        return null;
    }

    /// <summary>Invokes the post-execution verifier directly (no full run) so a test can drive
    /// the deterministic checks with a precise result set — used by the clean-pass ground-truth
    /// tests (OS-output pass, component-wiring pass, nothing-published negative).</summary>
    private async Task<(bool complete, string details, List<string> confirmedIssues, List<string> speculativeIssues, List<string> groundTruth)> InvokePostExecuteVerify(
        string prompt, List<object> allResults, string? cardId)
    {
        var controller = BuildController();
        var method = typeof(AgentController).GetMethod("PostExecuteVerify", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("PostExecuteVerify not found");
        var task = (Task<(bool complete, string details, List<string> confirmedIssues, List<string> speculativeIssues, List<string> groundTruth)>)method.Invoke(
            controller, new object?[]
            {
                prompt, _projectRoot, /*emitSse*/ false, allResults, CancellationToken.None,
                /*discoveryContext*/ null, /*atomicStepEstimate*/ null, /*preEditSnapshots*/ null,
                /*cardId*/ cardId, /*steeringContext*/ null,
                /*strictVerifier*/ null // legacy behavior — these tests predate the hard-gate toggle
            })!;
        return await task;
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

    /// <summary>The replanner (plan-fixer) response shape: a "plan" array whose items may carry
    /// concrete oldString/newString edits, applied directly without an edit-resolution call.</summary>
    private static string ReplanJson(string file, string change, string oldString, string newString)
    {
        var payload = new Dictionary<string, object?>
        {
            ["plan"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["file"] = file,
                    ["change"] = change,
                    ["oldString"] = oldString,
                    ["newString"] = newString
                }
            }
        };
        return JsonSerializer.Serialize(payload);
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
    /// Scripted fake LLM. The test supplies <see cref="PlannerReply"/> — a function of the
    /// planner-call ordinal returning the raw JSON (or garbage) for that turn. Every other
    /// route is fixed; any call no marker matches lands in <see cref="Unmatched"/> and is
    /// answered empty (so resolver/repair churn fails the "no unscripted calls" assertions).
    /// </summary>
    private sealed class ScriptedClientFactory : IHttpClientFactory, IDisposable
    {
        public Func<int, string>? PlannerReply { get; set; }
        public Func<int, string>? RepairReply { get; set; }
        public readonly List<string> Calls = new();
        public readonly List<string> Unmatched = new();
        public readonly List<string> PlannerUserPrompts = new();
        public readonly List<string> RepairUserPrompts = new();
        public readonly List<string> VerifierUserPrompts = new();
        public Func<string>? VerifierReply { get; set; }
        /// <summary>Raw "{\"requirements\": [...]}" JSON for the requirement-checklist extractor
        /// call (defaults to a benign single item). A test can script hint-word items ("latest",
        /// "current") to prove the checklist never leaks into the task prompt's web-need scan.</summary>
        public Func<string>? ChecklistReply { get; set; }
        /// <summary>Computed ground truth for the completion assessment: given the CURRENT
        /// on-disk content of the target file, is the task complete? When set, the scripted
        /// assessor reads the real file and decides deterministically — we know the correct
        /// answer before the run starts.</summary>
        public Func<string, bool>? AssessComplete { get; set; }
        /// <summary>Relative path of the file the content-aware assessor reads.</summary>
        public string AssessTargetRel { get; set; } = "";
        /// <summary>Path of the temp project root so the assessor can read real files.</summary>
        public string ProjectRoot { get; set; } = "";
        private int _plannerCalls;
        private int _repairCalls;

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
                // Pre-plan deep reasoning fires before each planner turn when a cardId is
                // present (ground-truth publishing is keyed by cardId, so these tests need
                // one) — answer with benign prose that cannot interfere with the scripted
                // steps, mirroring LiveSteerEndpointTests.
                if (system.Contains("You are the deep-reasoning engine of an autonomous coding agent", StringComparison.Ordinal))
                    return ("The next step is scripted by the test harness. Keep the task minimal: implement exactly the scripted edit.", "deep-reason");
                if (system.Contains("building a code-change plan ONE STEP AT A TIME", StringComparison.Ordinal))
                {
                    lock (_owner.PlannerUserPrompts) _owner.PlannerUserPrompts.Add(user);
                    var n = Interlocked.Increment(ref _owner._plannerCalls);
                    if (_owner.PlannerReply != null) return (_owner.PlannerReply(n), "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"nothing to do\"}", "planner-complete");
                }
                if (system.Contains("You are a plan-fixer", StringComparison.Ordinal))
                {
                    lock (_owner.RepairUserPrompts) _owner.RepairUserPrompts.Add(user);
                    var n = Interlocked.Increment(ref _owner._repairCalls);
                    if (_owner.RepairReply != null) return (_owner.RepairReply(n), "replanner");
                    return ("{\"plan\": []}", "replanner");
                }
                if (system.Contains("Plan the complete minimum set of steps", StringComparison.Ordinal))
                    return ("{\"plan\": []}", "planner-classic");
                if (system.Contains("You extract a short checklist of literal, testable requirements", StringComparison.Ordinal))
                    return (_owner.ChecklistReply?.Invoke() ?? "{\"requirements\": [\"satisfy the computed ground truth\"]}", "checklist");
                if (system.Contains("You are a strict plan-coherence validator", StringComparison.Ordinal))
                    return ("{\"valid\": true}", "plan-validator");
                if (user.Contains("Decide: keep or abandon", StringComparison.Ordinal))
                    return ("{\"decision\": \"keep\", \"reason\": \"verified\", \"score\": 95, \"needsExtraStep\": false}", "verify");
                if (user.Contains("Evaluate the code changes against the ORIGINAL TASK ONLY", StringComparison.Ordinal))
                {
                    if (_owner.AssessComplete != null && !string.IsNullOrEmpty(_owner.AssessTargetRel))
                    {
                        // Computed ground truth: read the REAL current file and decide
                        // deterministically — a multi-turn context failure (inconsistent
                        // reference) must fail the assessment, never pass it.
                        var path = Path.Combine(_owner.ProjectRoot,
                            _owner.AssessTargetRel.Replace('/', Path.DirectorySeparatorChar));
                        var content = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "";
                        return _owner.AssessComplete(content)
                            ? ("{\"complete\": true, \"reason\": \"computed ground truth satisfied\", \"issues\": []}", "assess")
                            : ("{\"complete\": false, \"reason\": \"computed ground truth NOT satisfied — the multi-turn chain is inconsistent\", \"issues\": []}", "assess");
                    }
                    return ("{\"complete\": true, \"reason\": \"task satisfied\", \"issues\": []}", "assess");
                }
                if (system.Contains("meticulous code reviewer verifying if a task is fully complete", StringComparison.Ordinal))
                {
                    lock (_owner.VerifierUserPrompts) _owner.VerifierUserPrompts.Add(user);
                    return (_owner.VerifierReply?.Invoke() ?? "{\"complete\": true, \"reason\": \"done\", \"issues\": []}", "post-verify");
                }
                if (system.Contains("You detect code cohesion issues after an edit. Output ONLY JSON.", StringComparison.Ordinal))
                    return ("{\"issues\": []}", "cohesion");
                lock (_owner.Unmatched) _owner.Unmatched.Add(system.Length > 80 ? system[..80] : system);
                return ("", "unknown");
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
