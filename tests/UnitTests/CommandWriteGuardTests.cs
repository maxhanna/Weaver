using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Weaver;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the two _command write guards that came out of the "Search the web … write the
/// data into a text file on my desktop" run: (1) Windows PowerShell 5.1 does not support
/// `&&` as a statement separator — the planner's chained echo command failed with a parser
/// error instead of writing the file; (2) after a completed _command already wrote the
/// demanded OS output, further writes to the same location are stacked re-work (the run
/// proposed Set-Content, then an echo && chain, then an Invoke-RestMethod redirect — all
/// to the same desktop file).
/// </summary>
public class CommandWriteGuardTests
{
    // ── WindowsPowerShellAndChainReason (OS-independent detection) ────────────

    [Fact]
    public void AndChain_Unquoted_Rejected()
    {
        var reason = AgentPlanningPolicy.WindowsPowerShellAndChainReason("echo a > f && echo b >> f");
        Assert.NotNull(reason);
        Assert.Contains("&&", reason);
        Assert.Contains("PowerShell", reason);
    }

    [Fact]
    public void AndChain_InsideQuotes_Allowed()
    {
        // A literal "a && b" inside a quoted string is valid PowerShell 5.1.
        Assert.Null(AgentPlanningPolicy.WindowsPowerShellAndChainReason("Set-Content -Path \"f\" -Value \"a && b\""));
        Assert.Null(AgentPlanningPolicy.WindowsPowerShellAndChainReason("echo 'a && b'"));
    }

    [Fact]
    public void AndChain_NoAndOrBlank_Null()
    {
        Assert.Null(AgentPlanningPolicy.WindowsPowerShellAndChainReason("Set-Content -Path \"C:\\x\\y.txt\" -Value hello"));
        Assert.Null(AgentPlanningPolicy.WindowsPowerShellAndChainReason(null));
        Assert.Null(AgentPlanningPolicy.WindowsPowerShellAndChainReason(""));
    }

    // ── RedundantOsWriteReason (prompt-pinned absolute path ⇒ OS-independent) ─

    /// <summary>The exact prompt class from the failing run, but with an explicit absolute
    /// path so the demand resolves identically on Windows and headless-Linux CI.</summary>
    private const string PinnedPrompt =
        "Search the web for an interesting and relevant AI article and write the data into a text file at C:\\Users\\Test\\Desktop\\results.txt";

    private static readonly PlanStep FirstWrite = new()
    {
        File = "_command",
        Change = "Set-Content -Path \"C:\\Users\\Test\\Desktop\\results.txt\" -Value \"v2.1\""
    };

    [Fact]
    public void RedundantWrite_NoDemand_Null()
    {
        Assert.Null(AgentPlanningPolicy.RedundantOsWriteReason(
            "Fix the login bug", "echo x > out.txt", new List<PlanStep>()));
    }

    [Fact]
    public void RedundantWrite_FirstWriteToDemand_Allowed()
    {
        // Nothing committed yet — the FIRST write to the demanded location is the legit one.
        Assert.Null(AgentPlanningPolicy.RedundantOsWriteReason(
            PinnedPrompt, "Set-Content -Path \"C:\\Users\\Test\\Desktop\\results.txt\" -Value \"v2.1\"", new List<PlanStep>()));
    }

    [Fact]
    public void RedundantWrite_SameFileAgain_Rejected()
    {
        var reason = AgentPlanningPolicy.RedundantOsWriteReason(
            PinnedPrompt, "Set-Content -Path \"C:\\Users\\Test\\Desktop\\results.txt\" -Value \"more\"", new List<PlanStep> { FirstWrite });
        Assert.NotNull(reason);
        Assert.Contains("ALREADY written", reason);
    }

    [Fact]
    public void RedundantWrite_RedirectSameFile_Rejected()
    {
        var reason = AgentPlanningPolicy.RedundantOsWriteReason(
            PinnedPrompt, "echo more > \"C:\\Users\\Test\\Desktop\\results.txt\"", new List<PlanStep> { FirstWrite });
        Assert.NotNull(reason);
        Assert.Contains("ALREADY written", reason);
    }

    [Fact]
    public void RedundantWrite_DifferentLocation_Allowed()
    {
        Assert.Null(AgentPlanningPolicy.RedundantOsWriteReason(
            PinnedPrompt, "Set-Content -Path \"C:\\Users\\Test\\Desktop\\other.txt\" -Value \"more\"", new List<PlanStep> { FirstWrite }));
    }

    [Fact]
    public void RedundantWrite_DemandNotYetSatisfied_Allowed()
    {
        // The committed step wrote somewhere ELSE — the demand is not satisfied, so a genuine
        // write to the demanded file must stay allowed (the repair path can never be blocked).
        var committedElsewhere = new PlanStep { File = "_command", Change = "Set-Content -Path \"C:\\temp\\scratch.txt\" -Value \"x\"" };
        Assert.Null(AgentPlanningPolicy.RedundantOsWriteReason(
            PinnedPrompt, "Set-Content -Path \"C:\\Users\\Test\\Desktop\\results.txt\" -Value \"v2.1\"", new List<PlanStep> { committedElsewhere }));
    }

    // ── ValidateIncrementalStepAsync end-to-end (real private validator) ─────

    private static readonly MethodInfo ValidateMethod = typeof(AgentController).GetMethod(
        "ValidateIncrementalStepAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static (bool valid, string? reason) Validate(PlanStep step, string prompt, List<PlanStep> planSoFar)
    {
        var controller = RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        var task = (Task<(bool valid, string? reason)>)ValidateMethod.Invoke(controller, new object?[]
        {
            step, prompt, /*discoveryContext*/ "", planSoFar,
            /*projectRoot*/ ".", /*emitSse*/ false, CancellationToken.None, /*skipLlm*/ false,
            /*lastStepCompletionNote*/ null, /*attachedFiles*/ null
        })!;
        var result = task.GetAwaiter().GetResult();
        return (result.valid, result.reason);
    }

    [Fact]
    public void AndChainCommand_OnWindows_Rejected()
    {
        var (valid, reason) = Validate(
            new PlanStep
            {
                File = "_command",
                Change = "echo AI Research > \"C:\\Users\\Test\\Desktop\\results.txt\" && echo more >> \"C:\\Users\\Test\\Desktop\\results.txt\""
            },
            PinnedPrompt, new List<PlanStep>());
        if (OperatingSystem.IsWindows())
        {
            Assert.False(valid);
            Assert.Contains("&&", reason ?? "");
        }
        else
        {
            // bash/PS7 allow && — the guard is Windows-only, so a non-Windows host passes it.
            Assert.True(valid, reason);
        }
    }

    [Fact]
    public void RedundantWriteCommand_Rejected()
    {
        var (valid, reason) = Validate(
            new PlanStep { File = "_command", Change = "Set-Content -Path \"C:\\Users\\Test\\Desktop\\results.txt\" -Value \"more content\"" },
            PinnedPrompt, new List<PlanStep> { FirstWrite });
        Assert.False(valid);
        Assert.NotNull(reason);
        Assert.Contains("ALREADY written", reason);
    }

    [Fact]
    public void FirstWriteCommand_Allowed()
    {
        var (valid, reason) = Validate(
            new PlanStep { File = "_command", Change = "Set-Content -Path \"C:\\Users\\Test\\Desktop\\results.txt\" -Value \"v2.1\"" },
            PinnedPrompt, new List<PlanStep>());
        Assert.True(valid, reason);
    }

    [Fact]
    public void GenericDesktopDemand_SecondWrite_OnWindows_Rejected()
    {
        // The EXACT run shape: prompt names no file ("a text file on my desktop"), the first
        // committed Set-Content lands on the real Desktop, and a second write to the same
        // desktop must be rejected. Windows-only — headless-Linux CI resolves no Desktop
        // folder, so the demand directory is empty and the guard stays silent there.
        if (!OperatingSystem.IsWindows()) return;
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (string.IsNullOrWhiteSpace(desktop)) return;
        const string prompt = "Search the web for an interesting and relevant AI article and write the data into a text file on my desktop";
        var first = new PlanStep { File = "_command", Change = $"Set-Content -Path \"{desktop}\ai_article_summary.txt\" -Value \"content\"" };
        var reason = AgentPlanningPolicy.RedundantOsWriteReason(
            prompt, $"echo more > \"{desktop}\\ai_article_summary.txt\"", new List<PlanStep> { first });
        Assert.NotNull(reason);
        Assert.Contains("ALREADY written", reason);
    }

    // ── Classic-route whole-plan gate (FindPlanCommandViolations) ────────────

    private static List<(PlanStep Step, string Reason)> FindViolations(AgentPlan plan, string prompt)
        => AgentPlanningPolicy.FindPlanCommandViolations(plan, prompt);

    [Fact]
    public void ClassicPlan_RedundantWrite_Flagged()
    {
        var plan = new AgentPlan
        {
            Plan = new List<PlanStep>
            {
                new() { File = "_command", Change = "Set-Content -Path \"C:\\Users\\Test\\Desktop\\results.txt\" -Value \"v2.1\"" },
                new() { File = "_command", Change = "echo more > \"C:\\Users\\Test\\Desktop\\results.txt\"" }
            }
        };
        var violations = FindViolations(plan, PinnedPrompt);
        Assert.Single(violations);
        Assert.Contains("ALREADY written", violations[0].Reason);
    }

    [Fact]
    public void ClassicPlan_SingleWrite_NotFlagged()
    {
        var plan = new AgentPlan
        {
            Plan = new List<PlanStep>
            {
                new() { File = "_command", Change = "Set-Content -Path \"C:\\Users\\Test\\Desktop\\results.txt\" -Value \"v2.1\"" }
            }
        };
        Assert.Empty(FindViolations(plan, PinnedPrompt));
    }

    [Fact]
    public void ClassicPlan_AndChain_OnWindows_Flagged()
    {
        var plan = new AgentPlan
        {
            Plan = new List<PlanStep>
            {
                new() { File = "_command", Change = "echo a > \"C:\\Users\\Test\\Desktop\\results.txt\" && echo b >> \"C:\\Users\\Test\\Desktop\\results.txt\"" }
            }
        };
        var violations = FindViolations(plan, PinnedPrompt);
        if (OperatingSystem.IsWindows())
        {
            Assert.Single(violations);
            Assert.Contains("&&", violations[0].Reason);
        }
        else
        {
            // Non-Windows: && is legal and the redundant-write guard needs a PRIOR write.
            Assert.Empty(violations);
        }
    }
}
