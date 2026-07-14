using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

public class FailureTaxonomyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "weaver-failure-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("oldString not found verbatim in file", "ANCHOR_RESOLUTION_FAILED", FailureCategory.AnchorResolution)]
    [InlineData("dotnet build failed with exit code 1", "BUILD_TEST_FAILED", FailureCategory.BuildOrTest)]
    [InlineData("newString too short — possible content deletion", "STRUCTURAL_VALIDATION_FAILED", FailureCategory.StructuralValidation)]
    [InlineData("failed after 4 attempts; giving up", "RECOVERY_EXHAUSTED", FailureCategory.RecoveryExhausted)]
    [InlineData("discovery context does not contain the required symbol", "CONTEXT_INSUFFICIENT", FailureCategory.Context)]
    public void Classify_ReturnsStableCodeAndCategory(string reason, string code, FailureCategory category)
    {
        var result = FailureTaxonomy.Classify(reason, "failure");
        Assert.Equal(code, result.Code);
        Assert.Equal(category, result.Category);
        Assert.True(result.ReusableLesson);
    }

    [Fact]
    public async Task RecordOutcome_PersistsNormalizedFailureAndDeduplicatesRetries()
    {
        var project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);
        var service = new EditKnowledgeService(Path.Combine(_root, "data"));

        await service.RecordOutcomeAsync(project, "Service.cs", "change method", "prompt", "old", "new",
            "failure", "oldString not found verbatim in file\nlarge noisy attempt dump");
        await service.RecordOutcomeAsync(project, "Service.cs", "change method", "prompt", "old", "new",
            "failure", "oldString not found after another retry with different noise");

        var knowledge = await service.LoadAsync(project);
        var failure = Assert.Single(knowledge!.RecentFailures);
        Assert.Equal("ANCHOR_RESOLUTION_FAILED", failure.Code);
        Assert.Equal(nameof(FailureCategory.AnchorResolution), failure.Category);
        Assert.Equal("The edit anchor was missing, ambiguous, or resolved to the wrong location.", failure.Reason);
        Assert.DoesNotContain("attempt dump", failure.Reason);
    }

    [Fact]
    public void UnknownFailure_IsConciseAndNotPromotedAsReusable()
    {
        var result = FailureTaxonomy.Classify(new string('x', 500));
        Assert.Equal("UNKNOWN_FAILURE", result.Code);
        Assert.False(result.ReusableLesson);
        Assert.True(result.Summary.Length <= 160);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
