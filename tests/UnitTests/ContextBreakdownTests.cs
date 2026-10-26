using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Tests for <c>AgentController.BuildContextBreakdownForRun</c> — the per-category rows
/// behind the agent-panel token counter. Regression: the old "headers / skeleton /
/// steering" row was derived as a token RESIDUAL (whole-context estimate minus per-file
/// estimates). The token estimator is non-additive, so the residual routinely collapsed
/// to 0 tokens even when the scaffolding text was non-trivial — the counter showed a
/// starting token count and every non-file category read 0, and nothing updated during
/// execution. The rewrite estimates non-file rows at the UI's documented ~chars/4 rate
/// and splits the skeleton into its own row, so no row shows 0 while it has content.
///
/// The tests construct the <see cref="AgentRunContext"/> directly and pass it as an
/// argument — the same way the run-time callers do — instead of seeding controller
/// fields. (The old test seeded legacy reflection adapter fields that fed a dead
/// 2-arg BuildContextBreakdown overload; both are gone.)
/// </summary>
public class ContextBreakdownTests
{
    private static object UninitializedController() =>
        RuntimeHelpers.GetUninitializedObject(typeof(AgentController));

    private static dynamic InvokeBuildContextBreakdownForRun(
        AgentRunContext runContext, List<object> ds, string discoveryContext)
    {
        var method = typeof(AgentController).GetMethod(
            "BuildContextBreakdownForRun", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("BuildContextBreakdownForRun not found");
        return method.Invoke(UninitializedController(),
            new object?[] { runContext, ds, discoveryContext })!;
    }

    [Fact]
    public void SkeletonAndScaffoldingRows_ShowRealTokenEstimates_NeverZero()
    {
        // 400 chars of skeleton in the context.
        var runContext = new AgentRunContext { SkeletonContextChars = 400 };
        // One discovery read: 800 chars of file output.
        var ds = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "read", ["path"] = "src/app.ts", ["output"] = new string('x', 800)
            }
        };
        // Context = headers (400, not part of any file output) + file output (800) + extra (100).
        var discoveryContext = new string('H', 400) + new string('x', 800) + new string('O', 100);

        var rows = (IEnumerable<dynamic>)InvokeBuildContextBreakdownForRun(runContext, ds, discoveryContext);
        var rowList = rows.Cast<dynamic>().ToList();

        // File row: chars from the read output, tokens estimated.
        var fileRow = rowList.Single(r => (string)r.kind == "file");
        Assert.Equal(800, (int)fileRow.chars);
        Assert.True((int)fileRow.tokens > 0, "file row must show its token estimate");

        // Skeleton gets its own row with a real (non-zero) estimate.
        var skeletonRow = rowList.Single(r => (string)r.kind == "skeleton");
        Assert.Equal(400, (int)skeletonRow.chars);
        Assert.Equal(100, (int)skeletonRow.tokens); // ceil(400/4)
        Assert.True((int)skeletonRow.tokens > 0);

        // Scaffolding row: the residual after skeleton, estimated directly — never 0.
        var scaffoldingRow = rowList.Single(r => (string)r.kind == "scaffolding");
        Assert.Equal(100, (int)scaffoldingRow.chars);
        Assert.Equal(25, (int)scaffoldingRow.tokens); // ceil(100/4)
        Assert.True((int)scaffoldingRow.tokens > 0,
            "scaffolding tokens must not collapse to 0 when the scaffolding has content");
    }

    [Fact]
    public void NoSkeleton_NoSkeletonRow_ScaffoldingStillShowsRealEstimate()
    {
        var runContext = new AgentRunContext(); // SkeletonContextChars stays 0
        var ds = new List<object>();
        var discoveryContext = new string('H', 60); // headers only, no reads, no skeleton

        var rows = (IEnumerable<dynamic>)InvokeBuildContextBreakdownForRun(runContext, ds, discoveryContext);
        var rowList = rows.Cast<dynamic>().ToList();

        Assert.DoesNotContain(rowList, r => (string)r.kind == "skeleton");
        var scaffoldingRow = Assert.Single(rowList);
        Assert.Equal("scaffolding", (string)scaffoldingRow.kind);
        Assert.Equal(60, (int)scaffoldingRow.chars);
        Assert.Equal(15, (int)scaffoldingRow.tokens);
    }

    [Fact]
    public void SkeletonLargerThanContextRemainder_CappedToRemainder()
    {
        var runContext = new AgentRunContext { SkeletonContextChars = 1000 }; // larger than what remains
        var ds = new List<object>();
        var discoveryContext = new string('H', 300); // 300 chars total, no file reads

        var rows = (IEnumerable<dynamic>)InvokeBuildContextBreakdownForRun(runContext, ds, discoveryContext);
        var rowList = rows.Cast<dynamic>().ToList();

        var skeletonRow = rowList.Single(r => (string)r.kind == "skeleton");
        Assert.Equal(300, (int)skeletonRow.chars); // capped, never exceeds the context
        Assert.DoesNotContain(rowList, r => (string)r.kind == "scaffolding");
    }

    [Fact]
    public void EmptyContext_NoRows()
    {
        var rows = (IEnumerable<dynamic>)InvokeBuildContextBreakdownForRun(
            new AgentRunContext(), new List<object>(), "");
        Assert.Empty(rows.Cast<dynamic>().ToList());
    }

    /// <summary>
    /// The task input (raw prompt + requirement checklist) is its own breakdown row — the
    /// categories cover every part of what the LLM sees, and the scaffolding residual stops
    /// silently swallowing the task. The row is NOT subtracted from the discovery-context
    /// residual: it lives outside the discovery context entirely.
    /// </summary>
    [Fact]
    public void TaskPromptRow_IncludesChecklistShare_ShowsRealEstimate()
    {
        // Prompt (300 chars) + requirement checklist (100 chars) = 400 chars of task input.
        var runContext = new AgentRunContext
        {
            TaskPromptContextChars = 400,
            RequirementChecklist = "### EXPLICIT REQUIREMENTS CHECKLIST ###\n 1. …"
        };
        var ds = new List<object>();
        var discoveryContext = new string('H', 60); // headers only

        var rows = (IEnumerable<dynamic>)InvokeBuildContextBreakdownForRun(runContext, ds, discoveryContext);
        var rowList = rows.Cast<dynamic>().ToList();

        var taskRow = rowList.Single(r => (string)r.kind == "task");
        Assert.Equal("task prompt + requirements checklist", (string)taskRow.name);
        Assert.Equal(400, (int)taskRow.chars);
        Assert.Equal(100, (int)taskRow.tokens); // ceil(400/4)
        Assert.True((int)taskRow.tokens > 0, "task row must show its token estimate");
        // The scaffolding residual is unchanged — the task is NOT part of the discovery context.
        var scaffoldingRow = rowList.Single(r => (string)r.kind == "scaffolding");
        Assert.Equal(60, (int)scaffoldingRow.chars);
        Assert.Equal(15, (int)scaffoldingRow.tokens);
    }

    [Fact]
    public void TaskPromptRow_NoChecklist_NamesJustTaskPrompt_AndZeroTaskCharsOmitsRow()
    {
        // Without a checklist the row is still shown, named plainly.
        var runContext = new AgentRunContext { TaskPromptContextChars = 300, RequirementChecklist = null };
        var rows = (IEnumerable<dynamic>)InvokeBuildContextBreakdownForRun(runContext, new List<object>(), "HHH");
        var taskRow = rows.Cast<dynamic>().Single(r => (string)r.kind == "task");
        Assert.Equal("task prompt", (string)taskRow.name);
        Assert.Equal(300, (int)taskRow.chars);

        // A fresh context (TaskPromptContextChars 0) → no task row (mirrors the old behaviour).
        var freshRows = (IEnumerable<dynamic>)InvokeBuildContextBreakdownForRun(
            new AgentRunContext(), new List<object>(), "");
        Assert.DoesNotContain(freshRows.Cast<dynamic>(), r => (string)r.kind == "task");
    }
}
