using System.Reflection;
using Xunit;
using Weaver;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// The extracted EXPLICIT REQUIREMENTS CHECKLIST must ride in the PLANNER prompts as its own
/// section — it must NEVER be appended to the task `prompt`, because the task text feeds the
/// web-need detectors (TaskHintsWebNeed / ConfirmWebNeedAsync), the OS-task classifier and the
/// fetch-in-command guard. A checklist item that happens to contain "search" / "fetch" /
/// "current" / "latest" would trip the deliberately-broad WebNeedHints and hijack a plain code
/// run into a web task. These tests lock the separation at the prompt-builder level and pin the
/// exact hazard the separation prevents.
/// </summary>
public class RequirementChecklistSeparationTests
{
    private static readonly MethodInfo BuildUserPromptMethod = typeof(AgentController).GetMethod(
        "BuildIncrementalStepUserPrompt", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo BuildReplanPromptMethod = typeof(AgentController).GetMethod(
        "BuildReplanPrompt", BindingFlags.NonPublic | BindingFlags.Static)!;

    private const string Checklist =
        "### EXPLICIT REQUIREMENTS CHECKLIST ###\n" +
        "Verify EACH item individually against the actual code/content.\n" +
        " 1. The rename must reflect the latest computed configuration\n" +
        " 2. Verify the current file on disk is updated";

    private static string BuildIncrementalUserPrompt(string? requirementChecklist)
        => (string)BuildUserPromptMethod.Invoke(null, new object?[]
        {
            "Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS in the worker config",
            "### FILE: maxhanna.client/src/app/benchmarks/benchmark-config.ts ###\nexport const WORKER_CONFIGS = [];",
            new List<PlanStep>(),            /* planSoFar */
            null,                            /* steeringContext */
            new List<string>(),              /* rejectionFeedback */
            null,                            /* extendedReasoning */
            null,                            /* atomicStepEstimate */
            requirementChecklist,
            null                             /* projectRoot */
        })!;

    private static string BuildReplan(string? requirementChecklist)
        => (string)BuildReplanPromptMethod.Invoke(null, new object?[]
        {
            "Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS in the worker config",
            new List<string>(),              /* history */
            null,                            /* steeringContext */
            null,                            /* existingPlan */
            null,                            /* executedSteps */
            "",                              /* qualityCheckReason */
            "",                              /* fileContents */
            requirementChecklist
        })!;

    private static bool TaskHintsWebNeed(string? prompt)
        => AgentPlanningPolicy.TaskHintsWebNeed(prompt);

    [Fact]
    public void IncrementalPlannerPrompt_ChecklistRidesAsOwnSection_NotInsideTask()
    {
        var prompt = BuildIncrementalUserPrompt(Checklist);

        // The checklist section is present, threaded in as its own block…
        Assert.Contains("### EXPLICIT REQUIREMENTS CHECKLIST", prompt);
        Assert.Contains("latest computed configuration", prompt);
        // …and the TASK section still shows only the raw user task (the checklist is not
        // merged into the task text that heuristics scan).
        var taskStart = prompt.IndexOf("### TASK ###", StringComparison.Ordinal);
        var checklistStart = prompt.IndexOf("### EXPLICIT REQUIREMENTS CHECKLIST", StringComparison.Ordinal);
        Assert.True(taskStart >= 0, "planner prompt must have a TASK section");
        Assert.True(checklistStart > taskStart, "checklist must come after the TASK section");
        var taskSection = prompt[taskStart..checklistStart];
        Assert.Contains("Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS", taskSection);
        Assert.DoesNotContain("EXPLICIT REQUIREMENTS", taskSection);
    }

    [Fact]
    public void IncrementalPlannerPrompt_NoChecklist_OmitsSection()
    {
        Assert.DoesNotContain("### EXPLICIT REQUIREMENTS CHECKLIST", BuildIncrementalUserPrompt(null));
        Assert.DoesNotContain("### EXPLICIT REQUIREMENTS CHECKLIST", BuildIncrementalUserPrompt("   "));
    }

    [Fact]
    public void ReplanPrompt_ChecklistRidesAsOwnSection()
    {
        var prompt = BuildReplan(Checklist);
        Assert.Contains("## Requirements", prompt);
        Assert.Contains("### EXPLICIT REQUIREMENTS CHECKLIST", prompt);
        Assert.Contains("latest computed configuration", prompt);

        Assert.DoesNotContain("### EXPLICIT REQUIREMENTS CHECKLIST", BuildReplan(null));
    }

    [Fact]
    public void TaskHintsWebNeed_ChecklistAppendedToTask_WouldTripTheHint()
    {
        // The hijack the separation prevents: the raw task is a plain repo rename (no hint
        // words), but a checklist item carrying "latest"/"current" WOULD trip the broad
        // web-hint list if it were ever appended to the task prompt. Locking this proves the
        // appended form is exactly what must never reach the task text.
        var plainTask = "Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS in the worker config";
        Assert.False(TaskHintsWebNeed(plainTask), "the plain task must not hint at web need");

        var appended = plainTask + "\n\n" + Checklist;
        Assert.True(TaskHintsWebNeed(appended),
            "appending the checklist must trip the web hint — this is the hazard the separation prevents");
    }

    // ── Replan OS-output guidance ─────────────────────────────────────────────────────────
    // The repair replanner previously had NO OS-write guidance and NO web-results context: a
    // run whose demanded desktop file was never written (e.g. its _web_fetch failed and the
    // loop halted) fell to the replanner, which invented a Node fs writeArticleToFile() in an
    // Angular service — a repo edit that cannot touch the desktop. The replan prompt must
    // (a) tell the replanner the ONLY step that can create the demanded OS file is a
    // _command with an absolute path, and (b) surface the harvested web results so the
    // repair can write REAL gathered content instead of inventing it.

    private const string OsTask =
        "Search the web for an interesting and relevant AI article and write the data into a text file at \"C:\\Users\\Test\\Desktop\\ai_article.txt\"";

    private static string BuildReplanWithSteps(string task, List<object>? executedSteps, string? qualityCheckReason)
        => (string)BuildReplanPromptMethod.Invoke(null, new object?[]
        {
            task,
            new List<string>(),              /* history */
            null,                            /* steeringContext */
            null,                            /* existingPlan */
            executedSteps,                   /* executedSteps */
            qualityCheckReason ?? "",        /* qualityCheckReason */
            "",                              /* fileContents */
            null                             /* requirementChecklist */
        })!;

    [Fact]
    public void ReplanPrompt_OsOutputDemand_SteersToCommandWrite_NeverAppCode()
    {
        var prompt = BuildReplanWithSteps(OsTask, null, null);

        // The OS-OUTPUT DEMAND section names the demanded file and the ONLY tool that can
        // create it — a _command with an absolute path.
        Assert.Contains("## OS-OUTPUT DEMAND", prompt);
        if (OperatingSystem.IsWindows())
            Assert.Contains("Set-Content -Path", prompt);
        else
            Assert.Contains("echo \"<content>\" >", prompt);
        Assert.Contains("Only step type that can create it is a _command", prompt, StringComparison.OrdinalIgnoreCase);
        // The explicit anti-pattern from the field failure: never write app code to "write"
        // the file.
        Assert.Contains("Do NOT create or edit application code", prompt);

        // A plain repo task gets no OS-output section at all.
        var plain = BuildReplanWithSteps(
            "Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS in the worker config",
            null, null);
        Assert.DoesNotContain("## OS-OUTPUT DEMAND", plain);
    }

    [Fact]
    public void ReplanPrompt_HarvestedWebResults_AreInjectedForTheRepair()
    {
        // A failed _web_fetch leaves the successful _web_search output in the executed
        // results. The replan prompt must include those results (real titles/URLs) so the
        // repair can write actual content to the demanded file instead of inventing it.
        var executed = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "_web_search",
                ["status"] = "done",
                ["query"] = "AI research breakthroughs latest",
                ["output"] = "## Results\n  - AlphaFold 3 predicts protein structures (https://example.com/alphafold3)\n"
            },
            new Dictionary<string, object?>
            {
                ["type"] = "_web_fetch",
                ["status"] = "error",
                ["url"] = "https://www.example.com/latest-ai-breakthrough",
                ["error"] = "fetch failed: invented URL on www.example.com",
                ["output"] = ""
            }
        };
        var prompt = BuildReplanWithSteps(OsTask, executed, null);

        Assert.Contains("## Web results gathered so far", prompt);
        Assert.Contains("AlphaFold 3", prompt);
        Assert.Contains("https://example.com/alphafold3", prompt);
        Assert.Contains("never invent URLs or content", prompt);
    }

    [Fact]
    public void ReplanPrompt_NoExecutedSteps_NoWebResultsSection()
    {
        var prompt = BuildReplanWithSteps(
            "Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS in the worker config",
            new List<object>(), null);
        Assert.DoesNotContain("## Web results gathered so far", prompt);
    }
}
