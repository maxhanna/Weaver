using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Weaver;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the marker-level guards added for OS-filesystem tasks. These came out of the
/// "Search the web for an interesting and relevant AI article…" run, which failed three ways:
/// 1) the model's correct _web_search step was rejected by the research-verb guard
///    ("'search' is not an actionable edit") even though the web-need gate had confirmed web is
///    required; 2) the model invented a Linux path (/home/user/search_results) because it was
///    never told the OS or desktop path; 3) that repo-relative _create_directory then silently
///    created the folder INSIDE the project root.
/// </summary>
public class OsMarkerGuardTests
{
    private static readonly MethodInfo ValidateMethod = typeof(AgentController).GetMethod(
        "ValidateIncrementalStepAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>Runs the REAL private validator on an uninitialized controller — the early
    /// guard paths are pure (static helpers only), so no DI/state is needed.</summary>
    private static (bool valid, string? reason) Validate(PlanStep step, string prompt)
    {
        var controller = RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        var task = (Task<(bool valid, string? reason)>)ValidateMethod.Invoke(controller, new object?[]
        {
            step, prompt, /*discoveryContext*/ "", /*planSoFar*/ new List<PlanStep>(),
            /*projectRoot*/ ".", /*emitSse*/ false, CancellationToken.None, /*skipLlm*/ false, /*lastStepCompletionNote*/ null, /*attachedFiles*/ null
        })!;
        var result = task.GetAwaiter().GetResult();
        return (result.valid, result.reason);
    }

    private static PlanStep WebSearch(string query) => new() { File = "_web_search", Change = query };

    // ── _web_search / _web_fetch are actionable markers ──────────────────────

    [Fact]
    public void WebSearchStep_WithSearchVerbChange_IsNotRejectedByResearchGuard()
    {
        // The exact shape from the failing run: change starts with "search" but the step is a
        // _web_search marker — the web-need gate governs it, not the research-verb guard.
        var (valid, reason) = Validate(WebSearch("Search for recent AI articles about machine learning advancements"), "Search the web for an interesting and relevant AI article and write the data into a text file on my desktop.");
        Assert.True(valid, reason);
    }

    [Fact]
    public void WebFetchStep_WithReadVerbChange_IsNotRejectedByResearchGuard()
    {
        var (valid, _) = Validate(new PlanStep { File = "_web_fetch", Change = "Read the page at https://example.com/article" },
            "Fetch a page from the web");
        Assert.True(valid);
    }

    [Fact]
    public void NormalEditStep_WithSearchVerbChange_StillRejected()
    {
        // The research guard stays intact for actual repo-edit steps.
        var (valid, reason) = Validate(new PlanStep { File = "src/Login.ts", Change = "Search for the login component" },
            "Fix the login component");
        Assert.False(valid);
        Assert.Contains("Research step rejected", reason);
    }

    [Fact]
    public void WebSearchStep_WithWhitespaceFileVariant_IsNotRejectedByResearchGuard()
    {
        // End-to-end: the parser (which normalizes the file field) must produce a step the validator
        // accepts. An LLM-emitted file name like " _web_search \n" used to dodge IsWebStep and the
        // web-step exclusion, so the research-verb guard bounced a genuinely web-needing task right
        // after the web-need gate approved it (the interleaved deadlock). Feeding the whitespace-y
        // field through ParseStepFromJson then ValidateIncrementalStepAsync is the true runtime path.
        var parsed = AgentPlanningPolicy.ParseStepFromJson(
            /*file*/ " _web_search \n", /*change*/ "Search for recent AI articles about machine learning advancements",
            /*targetSymbol*/ null, /*line*/ 0, /*oldString*/ null, /*newString*/ null,
            /*refFiles*/ new List<string>(), /*edits*/ new List<EditPair>(), /*targetType*/ null,
            /*targetName*/ null, /*insertAfter*/ null, /*newCode*/ null, /*fullFile*/ null);
        Assert.Equal("_web_search", parsed.File);
        var (valid, reason) = Validate(parsed,
            "Search the web for an interesting and relevant AI article and write the data into a text file on my desktop.");
        Assert.True(valid, reason);
    }

    [Fact]
    public void WebSearchStep_AfterWebNeedGateConfirmation_IsNotRejected()
    {
        // The exact reported sequence: the web-need gate runs (task hints at needing current external
        // info), the LLM confirms web is required, and THEN the model proposes the _web_search step.
        // The validator must accept it — "Research step rejected — 'search' is not an actionable edit"
        // must never fire for a web marker whose change field IS the query.
        var (valid, reason) = Validate(WebSearch("Search the web for the latest AI research breakthroughs"),
            "Search the web for an interesting and relevant AI article and write the data into a text file on my desktop.");
        Assert.True(valid, reason);
    }

    [Fact]
    public void WebSearchStep_AutoInjectedAfterRefusals_IsNotRejected()
    {
        // Auto-inject path: the planner refused _web_search MAX_STEP_REGEN_ATTEMPTS times, so the loop
        // injects a bare _web_search step (File + Change only, no oldString/newString/symbol). That
        // shape must pass validation exactly like a model-proposed one.
        var (valid, reason) = Validate(new PlanStep { File = "_web_search", Change = "latest AI research breakthroughs" },
            "Search the web for the latest AI research breakthroughs and write them to a file.");
        Assert.True(valid, reason);
    }

    [Fact]
    public void ParseStepFromJson_NormalizesWhitespaceInFileField()
    {
        // Root cause: a stray newline/space in the LLM's "file" field used to dodge IsWebStep and
        // the web-step exclusion, so the research-verb guard fired "'search' is not an actionable edit"
        // on a legitimate _web_search step. ParseStepFromJson must collapse it to the clean marker.
        var step = AgentPlanningPolicy.ParseStepFromJson(
            /*file*/ " _web_search \n", /*change*/ "Search for AI articles", /*targetSymbol*/ null,
            /*line*/ 0, /*oldString*/ null, /*newString*/ null, /*refFiles*/ new List<string>(),
            /*edits*/ new List<EditPair>(), /*targetType*/ null, /*targetName*/ null, /*insertAfter*/ null,
            /*newCode*/ null, /*fullFile*/ null);
        Assert.Equal("_web_search", step.File);
    }

    // ── _create_directory / _create_file cannot reach the OS filesystem ─────

    [Fact]
    public void CreateDirectory_WithOsPath_RejectedForOsTask()
    {
        // The exact failure: repo-relative _create_directory with an invented absolute path.
        var (valid, reason) = Validate(new PlanStep { File = "_create_directory", Change = "/home/user/search_results" },
            "Search the web for an AI article and write it to a text file on my desktop.");
        Assert.False(valid);
        Assert.Contains("writes RELATIVE TO THE PROJECT ROOT", reason);
        Assert.Contains("_command", reason);
    }

    [Fact]
    public void CreateFile_WithWindowsPath_RejectedForOsTask()
    {
        // Content is provided (so the pre-existing content check passes) — the OS-path guard
        // must still reject it before the repo-relative path would land inside the project.
        var (valid, reason) = Validate(new PlanStep { File = "_create_file", Change = "C:\\Users\\me\\Desktop\\out.txt", NewString = "# AI Article\n\nSummary" },
            "Search the web for an AI article and write it to a text file on my desktop");
        Assert.False(valid);
        Assert.Contains("writes RELATIVE TO THE PROJECT ROOT", reason);
    }

    [Fact]
    public void CreateDirectory_WithTildeHomePath_RejectedForOsTask()
    {
        var (valid, reason) = Validate(new PlanStep { File = "_create_directory", Change = "~/Desktop/search_results" },
            "Search the web for an AI article and write it to a text file on my desktop");
        Assert.False(valid);
        Assert.Contains("writes RELATIVE TO THE PROJECT ROOT", reason);
    }

    [Fact]
    public void CreateDirectory_WithUncPath_RejectedForOsTask()
    {
        var (valid, reason) = Validate(new PlanStep { File = "_create_directory", Change = "\\\\server\\share\\out" },
            "Search the web for an AI article and write it to a text file on my desktop");
        Assert.False(valid);
        Assert.Contains("writes RELATIVE TO THE PROJECT ROOT", reason);
    }

    [Fact]
    public void CreateDirectory_RepoRelative_AllowedEvenForOsTask()
    {
        var (valid, reason) = Validate(new PlanStep { File = "_create_directory", Change = "benchmark_test_6" },
            "Create a folder called benchmark_test_6");
        Assert.True(valid, reason);
    }

    [Fact]
    public void CreateDirectory_WithOsPath_AllowedForRepoTask()
    {
        // Repo tasks keep full marker freedom — the guard only fires for OS tasks.
        var (valid, reason) = Validate(new PlanStep { File = "_create_directory", Change = "src/desktop/components" },
            "Create a desktop components folder in the repo");
        Assert.True(valid, reason);
    }

    // ── _command prose gets desktop-aware feedback on OS tasks ───────────────

    [Fact]
    public void CommandStep_ProseChange_OsFeedbackMentionsDesktopPath()
    {
        var (valid, reason) = Validate(new PlanStep { File = "_command", Change = "Create temporary script to fetch latest AI article data" },
            "Search the web for an AI article and write it to a text file on my desktop");
        Assert.False(valid);
        // The OS-task feedback teaches the host's shell: New-Item on Windows, mkdir -p on Unix.
        if (OperatingSystem.IsWindows())
            Assert.Contains("New-Item -ItemType Directory", reason);
        else
            Assert.Contains("mkdir -p", reason);
    }

    [Fact]
    public void CommandStep_RealCommand_Allowed()
    {
        var (valid, reason) = Validate(new PlanStep { File = "_command", Change = "New-Item -ItemType Directory -Path \"C:\\Users\\me\\Desktop\\search_results\" -Force" },
            "Search the web for an AI article and save it to my desktop.");
        Assert.True(valid, reason);
    }
}
