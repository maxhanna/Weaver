using Xunit;
using Weaver;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Direct locks on the extracted <see cref="AgentPlanningPolicy"/> service (formerly
/// private statics of the AgentController.Planning/Formatting partials): the
/// creation-signal detector, the create-candidate extractor, the PATH probe, the
/// JS/TS syntax gate, and the change-dedup normalization/similarity pair the planning
/// loops' duplicate guards share. The policy is pure static logic over the plan model —
/// these tests call it directly instead of via reflection.
/// </summary>
public class AgentPlanningPolicyTests
{
    // ── PromptSignalsFileCreation ─────────────────────────────────────────────

    [Theory]
    [InlineData("create a new file called notes.txt and paste the summary")]
    [InlineData("We need a new service for rate limiting")]
    [InlineData("write a test for the login flow")]
    [InlineData("create a folder called benchmark_test_16")]
    [InlineData("make a directory for the exports")]
    [InlineData("scaffold a component for the settings page")]
    public void PromptSignalsFileCreation_CreationPhrasing_True(string prompt)
    {
        Assert.True(AgentPlanningPolicy.PromptSignalsFileCreation(prompt));
    }

    [Theory]
    [InlineData("add a method to the component")]
    [InlineData("fix the bug in the login service")]
    [InlineData("update the DTO to add a property")]
    [InlineData("refactor the worker and add tests")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PromptSignalsFileCreation_EditPhrasingOrBlank_False(string? prompt)
    {
        Assert.False(AgentPlanningPolicy.PromptSignalsFileCreation(prompt));
    }

    // ── CreateCandidatePath ───────────────────────────────────────────────────

    [Fact]
    public void CreateCandidatePath_MarkerStep_ExtractsPathFromChange()
    {
        var step = new PlanStep { File = "_create_file", Change = "benchmark_test_16/pokemon_data.csv" };
        Assert.Equal("benchmark_test_16/pokemon_data.csv", AgentPlanningPolicy.CreateCandidatePath(step));
    }

    [Fact]
    public void CreateCandidatePath_MarkerStep_DottedPrefixCapturedWhole()
    {
        // The extraction regex must not stop at the first dot of a dotted prefix.
        var step = new PlanStep { File = "_create_file", Change = "maxhanna.client/src/helpers/util.ts" };
        Assert.Equal("maxhanna.client/src/helpers/util.ts", AgentPlanningPolicy.CreateCandidatePath(step));
    }

    [Fact]
    public void CreateCandidatePath_CreateEligibleShape_ReturnsRelativeFile()
    {
        var step = new PlanStep { File = "docs/readme.md", NewString = "# Title", OldString = null };
        Assert.Equal("docs/readme.md", AgentPlanningPolicy.CreateCandidatePath(step));
    }

    [Fact]
    public void CreateCandidatePath_CreateEligibleShape_NormalizesBackslashes()
    {
        var step = new PlanStep { File = "src\\app\\util.ts", NewString = "export {};", OldString = null };
        Assert.Equal("src/app/util.ts", AgentPlanningPolicy.CreateCandidatePath(step));
    }

    [Fact]
    public void CreateCandidatePath_NonCreateSteps_Null()
    {
        // An EDIT shape (oldString anchor present) is never a create.
        Assert.Null(AgentPlanningPolicy.CreateCandidatePath(
            new PlanStep { File = "src/app.ts", Change = "fix", NewString = "b", OldString = "a" }));
        // A plain repo edit with no content is never a create.
        Assert.Null(AgentPlanningPolicy.CreateCandidatePath(
            new PlanStep { File = "src/app.ts", Change = "fix the bug" }));
        // Special markers other than _create_file are never creates.
        Assert.Null(AgentPlanningPolicy.CreateCandidatePath(
            new PlanStep { File = "_command", Change = "mkdir x" }));
    }

    // ── FindExecutableOnPath ──────────────────────────────────────────────────

    [Fact]
    public void FindExecutableOnPath_MissingExecutable_Null()
    {
        Assert.Null(AgentPlanningPolicy.FindExecutableOnPath("definitely-not-a-real-weaver-exe-xyz"));
    }

    [Fact]
    public void FindExecutableOnPath_Found_ReturnsExistingPath()
    {
        var path = AgentPlanningPolicy.FindExecutableOnPath(OperatingSystem.IsWindows() ? "cmd" : "sh");
        Assert.NotNull(path);
        Assert.True(File.Exists(path), $"probe should return an existing path, got '{path}'");
    }

    // ── ValidateJavaScriptLikeCreateFile (node syntax gate) ───────────────────

    [Fact]
    public void ValidateJavaScriptLikeCreateFile_NonJsOrBlankContent_Null()
    {
        // Only .js/.jsx/.mjs/.cjs/.ts/.tsx files are gated.
        Assert.Null(AgentPlanningPolicy.ValidateJavaScriptLikeCreateFile("src/app.py", "def f(:\n  pass"));
        Assert.Null(AgentPlanningPolicy.ValidateJavaScriptLikeCreateFile(null, "x"));
        Assert.Null(AgentPlanningPolicy.ValidateJavaScriptLikeCreateFile("a.js", null));
        Assert.Null(AgentPlanningPolicy.ValidateJavaScriptLikeCreateFile("a.js", "   "));
    }

    [Fact]
    public void ValidateJavaScriptLikeCreateFile_ValidScript_Null()
    {
        if (AgentPlanningPolicy.FindExecutableOnPath("node") == null) return; // node absent — gate stands down
        Assert.Null(AgentPlanningPolicy.ValidateJavaScriptLikeCreateFile(
            "src/util.js", "export function add(a, b) { return a + b; }\n"));
    }

    [Fact]
    public void ValidateJavaScriptLikeCreateFile_BrokenScript_RejectedWithReason()
    {
        if (AgentPlanningPolicy.FindExecutableOnPath("node") == null) return; // node absent — gate stands down
        var reason = AgentPlanningPolicy.ValidateJavaScriptLikeCreateFile(
            "src/broken.js", "function ( {\n  const = = 1;\n");
        Assert.NotNull(reason);
        Assert.Contains("syntax check failed", reason);
        Assert.Contains("src/broken.js", reason);
    }

    // ── NormalizeChangeForDedup / CalculateChangeSimilarity ───────────────────

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("  Fix the BUG!!  ", "fix the bug")]
    [InlineData("Add\t\ttabs\nand\nnewlines", "add tabs and newlines")]
    [InlineData("Rename MAX_RETRIES to MAX_ATTEMPTS.", "rename max_retries to max_attempts")]
    public void NormalizeChangeForDedup_LowercasesStripsPunctuationCollapsesWhitespace(string? change, string expected)
    {
        Assert.Equal(expected, AgentPlanningPolicy.NormalizeChangeForDedup(change));
    }

    [Fact]
    public void CalculateChangeSimilarity_IdenticalChanges_One()
    {
        Assert.Equal(1.0, AgentPlanningPolicy.CalculateChangeSimilarity("add the login helper", "add the login helper"));
    }

    [Fact]
    public void CalculateChangeSimilarity_DisjointChanges_Zero()
    {
        Assert.Equal(0.0, AgentPlanningPolicy.CalculateChangeSimilarity("alpha beta gamma", "delta epsilon zeta"));
    }

    [Fact]
    public void CalculateChangeSimilarity_PartialOverlap_Jaccard()
    {
        // Jaccard over word sets: {add, helper} ∩ {add, exporter} = 1 of 3 union words.
        Assert.Equal(1.0 / 3.0, AgentPlanningPolicy.CalculateChangeSimilarity("add helper", "add exporter"), 5);
    }

    [Fact]
    public void CalculateChangeSimilarity_BlankInput_Zero()
    {
        Assert.Equal(0.0, AgentPlanningPolicy.CalculateChangeSimilarity("", "add helper"));
        Assert.Equal(0.0, AgentPlanningPolicy.CalculateChangeSimilarity("add helper", null!));
        Assert.Equal(0.0, AgentPlanningPolicy.CalculateChangeSimilarity("", "   "));
    }
}
