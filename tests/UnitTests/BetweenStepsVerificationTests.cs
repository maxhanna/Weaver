using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Regression tests for the between-steps whole-task verification in the
/// interleaved execution loop (AgentController).
///
/// BUG (fixed): when the per-step verifier returned keep + needsExtraStep=false,
/// the loop ran a between-steps AssessCompletion with a hardcoded 30s LLM
/// timeout. On slow local models the assessment timed out, and the timeout was
/// treated as "task NOT complete" — forcing the agent to plan a redundant
/// step 2 that re-did the same edit.
///
/// FIX: the assessment uses the configurable timeout + one retry, and when the
/// assessment LLM is STILL unavailable (timed out / unparseable / empty), the
/// loop declares the plan complete after a needsExtraStep=false edit instead of
/// planning a redundant follow-up step.
///
/// These tests drive the two extracted decision helpers (private static, via
/// reflection) and AssessCompletion's early-return paths that need no LLM.
/// </summary>
public class BetweenStepsVerificationTests
{
    private static Dictionary<string, object?> EditResult(
        string status, bool needsExtraStep, string path = "src/app/foo.ts", bool isEdit = true)
    {
        return new Dictionary<string, object?>
        {
            ["type"] = isEdit ? "edit" : "create",
            ["status"] = status,
            ["path"] = path,
            ["needsExtraStep"] = needsExtraStep
        };
    }

    private static bool InvokeIsLastEditVerifiedComplete(List<object> results)
    {
        var method = typeof(AgentController).GetMethod(
            "IsLastEditVerifiedComplete", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("IsLastEditVerifiedComplete not found");
        var typed = results.OfType<Dictionary<string, object?>>().ToList();
        return (bool)(method.Invoke(null, new object?[] { typed }) ?? false);
    }

    private static (bool declare, string reason, bool failed) InvokeShouldDeclare(
        bool isComplete, string? assessReason, bool requireAssessment = false, bool visualInspectionPending = false)
    {
        var method = typeof(AgentController).GetMethod(
            "ShouldDeclarePlanCompleteAfterAssessment", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ShouldDeclarePlanCompleteAfterAssessment not found");
        var args = new object?[] { isComplete, assessReason, requireAssessment, visualInspectionPending, null, null };
        var result = (bool)method.Invoke(null, args)!;
        return (result, (string)args[4]!, (bool)args[5]!);
    }

    private static bool InvokeIsLastWebStepComplete(List<object> results)
    {
        var method = typeof(AgentController).GetMethod(
            "IsLastWebStepComplete", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("IsLastWebStepComplete not found");
        var typed = results.OfType<Dictionary<string, object?>>().ToList();
        return (bool)(method.Invoke(null, new object?[] { typed }) ?? false);
    }

    private static Dictionary<string, object?> WebResult(string status, string type = "_web_search")
        => new() { ["type"] = type, ["status"] = status, ["query"] = "AI research breakthroughs latest", ["output"] = "results" };

    /// <summary>
    /// The gate: a needsExtraStep=false edit/create result must trigger
    /// between-steps verification.
    /// </summary>
    [Fact]
    public void IsLastEditVerifiedComplete_NeedsExtraStepFalseEdit_ReturnsTrue()
    {
        var results = new List<object> { EditResult("done", needsExtraStep: false) };
        Assert.True(InvokeIsLastEditVerifiedComplete(results));
    }

    /// <summary>
    /// The gate must NOT fire when the result carries no needsExtraStep flag
    /// (not verified), when the status is not done/modified/created, or when the
    /// flag is explicitly true (a follow-up step is genuinely needed).
    /// </summary>
    [Theory]
    [InlineData("done", true)]   // needsExtraStep=true → still needs step 2
    [InlineData("failed", false)] // failed edit → not a verified-complete gate
    [InlineData("skipped", false)]
    public void IsLastEditVerifiedComplete_NotVerified_ReturnsFalse(string status, bool needsExtraStep)
    {
        var results = new List<object> { EditResult(status, needsExtraStep) };
        Assert.False(InvokeIsLastEditVerifiedComplete(results));
    }

    [Fact]
    public void IsLastEditVerifiedComplete_MissingFlag_ReturnsFalse()
    {
        var results = new List<object>
        {
            new Dictionary<string, object?> { ["type"] = "edit", ["status"] = "done", ["path"] = "a.ts" }
        };
        Assert.False(InvokeIsLastEditVerifiedComplete(results));
    }

    /// <summary>
    /// The web-only gate: a successful _web_search/_web_fetch result must trigger
    /// between-steps whole-task verification even though it is NOT an edit — the
    /// exact gap this feature closes (IsLastEditVerifiedComplete requires an edit).
    /// </summary>
    [Fact]
    public void IsLastWebStepComplete_SuccessfulWebStep_ReturnsTrue()
    {
        Assert.True(InvokeIsLastWebStepComplete(new List<object> { WebResult("done") }));
        Assert.True(InvokeIsLastWebStepComplete(new List<object> { WebResult("done", "_web_fetch") }));
        Assert.True(InvokeIsLastWebStepComplete(new List<object> { WebResult("done", "web_search") }));
    }

    /// <summary>
    /// The web-only gate must NOT fire when the web step failed, or when the last
    /// result is not a web step at all (a plain command, a list step).
    /// </summary>
    [Theory]
    [InlineData("error")]
    [InlineData("skipped")]
    public void IsLastWebStepComplete_FailedOrNonWeb_ReturnsFalse(string status)
    {
        Assert.False(InvokeIsLastWebStepComplete(new List<object> { WebResult(status) }));
        Assert.False(InvokeIsLastWebStepComplete(new List<object>
        {
            new Dictionary<string, object?> { ["type"] = "command", ["status"] = "done", ["command"] = "dir" }
        }));
    }

    /// <summary>
    /// The web-only mirror of the core regression: assessment LLM unavailable after a
    /// SUCCESSFUL WEB STEP must NOT declare the plan complete. A web step has no
    /// per-step verifier confirmation (unlike a needsExtraStep=false edit), so
    /// "assessment unavailable" must keep planning — otherwise a multi-step web
    /// chain (search → fetch → write) would prematurely end after step 1.
    /// </summary>
    [Fact]
    public void AssessmentTimedOut_AfterWebStep_KeepsPlanning()
    {
        var gate = InvokeIsLastWebStepComplete(new List<object> { WebResult("done") });
        Assert.True(gate);

        var (declare, reason, failed) = InvokeShouldDeclare(
            isComplete: true, assessReason: "Assessment timed out", requireAssessment: true);
        Assert.False(declare);
        Assert.True(failed);
        Assert.Contains("continuing instead of declaring complete", reason);
    }

    [Fact]
    public void AssessmentComplete_AfterWebStep_DeclaresComplete()
    {
        var (declare, reason, failed) = InvokeShouldDeclare(
            isComplete: true, assessReason: "task satisfied by the gathered results", requireAssessment: true);
        Assert.True(declare);
        Assert.False(failed);
        Assert.Equal("task satisfied by the gathered results", reason);
    }

    /// <summary>
    /// The core regression: assessment LLM unavailable ("Assessment timed out")
    /// after a needsExtraStep=false edit → declare the plan complete, do NOT
    /// plan a redundant step 2.
    /// </summary>
    [Fact]
    public void AssessmentTimedOut_AfterNeedsExtraStepFalseEdit_DeclaresComplete()
    {
        var gate = InvokeIsLastEditVerifiedComplete(new List<object> { EditResult("done", false) });
        Assert.True(gate);

        var (declare, reason, failed) = InvokeShouldDeclare(
            isComplete: false, assessReason: "Assessment timed out");
        Assert.True(declare);
        Assert.True(failed);
        Assert.Contains("stopping instead of planning a redundant step", reason);
    }

    /// <summary>Unparseable assessment JSON is treated the same as a timeout.</summary>
    [Fact]
    public void AssessmentUnparseable_AfterNeedsExtraStepFalseEdit_DeclaresComplete()
    {
        var (declare, reason, failed) = InvokeShouldDeclare(
            isComplete: false, assessReason: "Could not parse assessment");
        Assert.True(declare);
        Assert.True(failed);
        Assert.Contains("assessment LLM unavailable", reason);
    }

    /// <summary>Empty assessment response (LLM down) also declares complete.</summary>
    [Fact]
    public void AssessmentEmpty_AfterNeedsExtraStepFalseEdit_DeclaresComplete()
    {
        var (declare, reason, failed) = InvokeShouldDeclare(isComplete: false, assessReason: null);
        Assert.True(declare);
        Assert.True(failed);
    }

    /// <summary>
    /// A REAL assessment verdict of "not complete" (LLM actually responded and
    /// found remaining work) must still keep planning — the fallback only covers
    /// assessment unavailability, never a genuine not-complete verdict.
    /// </summary>
    [Fact]
    public void AssessmentSaysNotComplete_KeepsPlanning()
    {
        var (declare, reason, failed) = InvokeShouldDeclare(
            isComplete: false, assessReason: "Step 2 still needs the button handler");
        Assert.False(declare);
        Assert.False(failed);
        Assert.Equal("Step 2 still needs the button handler", reason);
    }

    /// <summary>A healthy complete assessment declares complete with the LLM's reason.</summary>
    [Fact]
    public void AssessmentSaysComplete_DeclaresCompleteWithLlmReason()
    {
        var (declare, reason, failed) = InvokeShouldDeclare(
            isComplete: true, assessReason: "All requested changes applied");
        Assert.True(declare);
        Assert.False(failed);
        Assert.Equal("All requested changes applied", reason);
    }

    /// <summary>
    /// The visual-inspection regression (benchmark 22): a task that demands LOOKING
    /// at the rendered page must NEVER be declared complete while no _browser_test
    /// step has run — even when the assessment LLM is unavailable (the observed
    /// failure declared planComplete after the build steps with the assessment down,
    /// so the live web test and the Test Browser tab never happened).
    /// </summary>
    [Fact]
    public void AssessmentTimedOut_VisualInspectionPending_KeepsPlanning()
    {
        var (declare, reason, failed) = InvokeShouldDeclare(
            isComplete: false, assessReason: "Assessment timed out", visualInspectionPending: true);
        Assert.False(declare);
        Assert.True(failed);
        Assert.Contains("live browser test", reason);
    }

    /// <summary>
    /// The visual gate is DETERMINISTIC: even a healthy "complete" assessment verdict
    /// cannot end a visual-inspection task before the _browser_test step ran.
    /// </summary>
    [Fact]
    public void AssessmentSaysComplete_VisualInspectionPending_StillKeepsPlanning()
    {
        var (declare, reason, failed) = InvokeShouldDeclare(
            isComplete: true, assessReason: "All requested changes applied", visualInspectionPending: true);
        Assert.False(declare);
        Assert.False(failed);
        Assert.Contains("live browser test", reason);
    }

    /// <summary>
    /// Once the _browser_test step HAS run (visualInspectionPending=false), the
    /// normal assessment rules apply again — a complete verdict declares complete.
    /// </summary>
    [Fact]
    public void AssessmentSaysComplete_BrowserTestRan_DeclaresComplete()
    {
        var (declare, reason, failed) = InvokeShouldDeclare(
            isComplete: true, assessReason: "Live web test passed and all changes applied", visualInspectionPending: false);
        Assert.True(declare);
        Assert.False(failed);
    }

    /// <summary>
    /// An assessment "not complete" verdict on a visual task still keeps planning even
    /// after the browser test ran (unavailable-vs-genuine distinction is preserved).
    /// </summary>
    [Fact]
    public void AssessmentSaysNotComplete_AfterBrowserTest_KeepsPlanning()
    {
        var (declare, reason, failed) = InvokeShouldDeclare(
            isComplete: false, assessReason: "The button handler is still missing", visualInspectionPending: false);
        Assert.False(declare);
        Assert.False(failed);
        Assert.Equal("The button handler is still missing", reason);
    }

    /// <summary>
    /// AssessCompletion's no-LLM early return: zero edit steps → command-only
    /// task is complete, no LLM call made.
    /// </summary>
    [Fact]
    public async Task AssessCompletion_NoEditSteps_CommandOnlyComplete_NoLlm()
    {
        var (isComplete, reason) = await InvokeAssessCompletion(
            new List<object>(), projectRoot: Path.GetTempPath());
        Assert.True(isComplete);
        Assert.Contains("No edit steps", reason);
    }

    /// <summary>
    /// AssessCompletion's no-LLM early return: a failed edit step → not complete,
    /// no LLM call made.
    /// </summary>
    [Fact]
    public async Task AssessCompletion_FailedEditStep_NotComplete_NoLlm()
    {
        var results = new List<object>
        {
            EditResult("done", false, "src/app/a.ts"),
            EditResult("failed", false, "src/app/b.ts")
        };
        var (isComplete, reason) = await InvokeAssessCompletion(results, projectRoot: Path.GetTempPath());
        Assert.False(isComplete);
        Assert.Contains("failed", reason);
    }

    /// <summary>
    /// The dump-task short-circuit: a task that demands fetched web data be written into a
    /// file (not a script) whose demanded file ALREADY exists with real content on disk is
    /// complete DETERMINISTICALLY — no LLM assessment, so the fetched data never round-trips
    /// through the completion LLM (the "needs a python parser to generate CSV rows" drift).
    /// </summary>
    [Fact]
    public async Task AssessCompletion_DumpTaskFileAlreadyWritten_CompletesDeterministically()
    {
        var root = Path.Combine(Path.GetTempPath(), "weaver_dump_" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "benchmark_test_16");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "pokemon_data.csv"), "id,name\n1,bulbasaur\n");
            var prompt = "Create a folder called 'benchmark_test_16' at the project root. Inside it, create a file called 'pokemon_data.csv'. Fetch the live Pokemon data from PokeAPI and write the data into benchmark_test_16/pokemon_data.csv.";
            var executedSteps = new List<object>
            {
                new Dictionary<string, object?> { ["type"] = "_web_fetch", ["status"] = "done", ["url"] = "https://pokeapi.co/api/v2/pokemon" }
            };
            var (isComplete, reason) = await InvokeAssessCompletionWithPrompt(executedSteps, root, prompt);
            Assert.True(isComplete);
            Assert.Contains("dump task", reason, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    /// <summary>IsDumpTask: web need + a demanded output file + no script request = a dump task.</summary>
    [Fact]
    public void IsDumpTask_WebFileDemandNoScript_True()
    {
        var root = Path.Combine(Path.GetTempPath(), "weaver_dumptask_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.True(InvokeIsDumpTask(
                "Fetch the live Pokemon data from PokeAPI and write the data into benchmark_test_16/pokemon_data.csv.", root));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    /// <summary>
    /// IsDumpTask: the news-article dump prompt ("fetch a recent AI news article … to a desktop
    /// text file") carries none of the literal WebNeedHints words (no "latest"/"current"/
    /// "fetch the"), so it must still classify as a dump via the news-shaped-prompt detection.
    /// This is the field failure where the task did web_search → web_fetch of a bot-walled site
    /// instead of the RSS digest + straight-to-file dump.
    /// </summary>
    [Fact]
    public void IsDumpTask_NewsArticleDesktopDump_True()
    {
        var root = Path.Combine(Path.GetTempPath(), "weaver_dumptask_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.True(InvokeIsDumpTask(
                "Fetch a recent AI news article and create a text file on the desktop and dump article the data in there.", root));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    /// <summary>IsDumpTask: a script/program request is a BUILD task, not a dump — normal steps continue.</summary>
    [Fact]
    public void IsDumpTask_ScriptRequest_False()
    {
        var root = Path.Combine(Path.GetTempPath(), "weaver_dumptask_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.False(InvokeIsDumpTask(
                "Write a python script that fetches the live Pokemon data and writes benchmark_test_16/pokemon_data.csv.", root));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    /// <summary>IsDumpTask: no demanded output file (pure search) is not a dump task.</summary>
    [Fact]
    public void IsDumpTask_NoFileDemand_False()
    {
        Assert.False(InvokeIsDumpTask("Search the web for recent AI breakthroughs.", Path.GetTempPath()));
    }

    private static bool InvokeIsDumpTask(string prompt, string projectRoot)
        => AgentPlanningPolicy.IsDumpTask(prompt, projectRoot);

    private static async Task<(bool isComplete, string reason)> InvokeAssessCompletion(
        List<object> executedSteps, string projectRoot)
        => await InvokeAssessCompletionWithPrompt(executedSteps, projectRoot, "Make the button work");

    private static async Task<(bool isComplete, string reason)> InvokeAssessCompletionWithPrompt(
        List<object> executedSteps, string projectRoot, string prompt)
    {
        var controller = RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        var method = typeof(AgentController).GetMethod(
            "AssessCompletion",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("AssessCompletion not found");
        var task = (Task<(bool, string)>)method.Invoke(controller,
            new object?[]
            {
                prompt, executedSteps, projectRoot,
                CancellationToken.None, new AgentPlan(), new List<string>(),
                /* atomicStepEstimate */ null,
                /* steeringContext */ null
            })!;
        return await task;
    }
}
