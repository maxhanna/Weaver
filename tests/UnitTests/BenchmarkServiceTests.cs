using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

public sealed class BenchmarkServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "weaver-benchmark-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EvaluateAsync_UsesArtifactChecksInsteadOfReportedPlanProgress()
    {
        var sandbox = Path.Combine(_root, "sandbox");
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(Path.Combine(sandbox, "benchmark_test_1"));
        await File.WriteAllTextAsync(Path.Combine(sandbox, "benchmark_test_1", "test.md"),
            "Hello world\nThe capital of France is Paris");

        var result = await new BenchmarkService(data).EvaluateAsync(1, sandbox, "test-model", 1234);

        Assert.Equal("completed", result.Status);
        Assert.Equal(100, result.ScorePercent);
        Assert.Equal(100, result.CorrectnessPercent);
        Assert.Equal(4, result.StepsCompleted);
        Assert.False(string.IsNullOrWhiteSpace(result.PlannerRoute));
        Assert.NotNull(result.PlannerGateScore);
        Assert.All(result.Checks, check => Assert.True(check.Passed, check.Message));
    }

    [Fact]
    public async Task EvaluateAsync_ReportsIndependentFailedAssertions()
    {
        var sandbox = Path.Combine(_root, "sandbox");
        Directory.CreateDirectory(Path.Combine(sandbox, "benchmark_test_1"));
        await File.WriteAllTextAsync(Path.Combine(sandbox, "benchmark_test_1", "test.md"), "Hello world");

        var result = await new BenchmarkService(Path.Combine(_root, "data"))
            .EvaluateAsync(1, sandbox, "test-model", 50);

        Assert.Equal("partial", result.Status);
        Assert.Equal(85, result.ScorePercent);
        Assert.Contains(result.Checks, check => check.Name == "Contains Paris fact" && !check.Passed);
        Assert.Contains("Contains Paris fact", result.ErrorReason);
        var failed = Assert.Single(result.Checks, check => !check.Passed);
        Assert.Equal("EDIT_APPLICATION_FAILED", failed.FailureCode);
        Assert.Equal(nameof(FailureCategory.EditApplication), failed.FailureCategory);
        Assert.Equal(1, result.FailureCounts[nameof(FailureCategory.EditApplication)]);
    }

    [Fact]
    public void EveryBenchmarkHasAcceptanceChecks()
    {
        Assert.All(BenchmarkService.GetBenchmarkPlans(), plan => Assert.NotEmpty(plan.AcceptanceChecks));
    }

    [Fact]
    public void EditStrategyBenchmarksHaveFixturesAndStrategyLabels()
    {
        var scenarios = BenchmarkService.GetBenchmarkPlans().Where(plan => plan.Level >= 6).ToList();
        Assert.Equal(10, scenarios.Count);
        Assert.All(scenarios, plan =>
        {
            Assert.NotEmpty(plan.SetupFiles);
            Assert.False(string.IsNullOrWhiteSpace(plan.WorkspacePath));
            Assert.False(string.IsNullOrWhiteSpace(plan.ExpectedStrategy));
            Assert.Contains(plan.AcceptanceChecks, check => check.Category == "preservation");
        });
    }

    [Fact]
    public async Task Evaluation_RecordsVersionStrategyAndCommandTelemetry()
    {
        var sandbox = Path.Combine(_root, "sandbox");
        var service = new BenchmarkService(Path.Combine(_root, "data"));
        var prepared = await service.PrepareAsync(15, sandbox);
        var file = Path.Combine(prepared.RunRoot, "edit_strategy", "json", "settings.json");
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("\"pageSize\": 20", "\"pageSize\": 50"));

        var result = await service.EvaluateAsync(15, prepared.RunRoot, "test-model", 25,
            ["old-string-replacement"]);

        Assert.Equal(BenchmarkService.BenchmarkSchemaVersion, result.RunMetadata!.SchemaVersion);
        Assert.Contains("old-string-replacement", result.ActualStrategies);
        var command = Assert.Single(result.Checks, c => c.Type == BenchmarkCheckType.CommandSucceeds);
        Assert.True(command.Passed, command.StandardError);
        Assert.Equal(0, command.ExitCode);
        Assert.False(command.TimedOut);
        Assert.NotNull(command.DurationMs);
    }

    [Fact]
    public void Compare_IdentifiesNewFailuresAndRecoveries()
    {
        var baseline = new BenchmarkScore { Id = "base", ScorePercent = 80, DurationMs = 100, PlannerRoute = "incremental", Checks = [new() { Name = "A", Passed = true }, new() { Name = "B", Passed = false }] };
        var current = new BenchmarkScore { ScorePercent = 70, DurationMs = 120, PlannerRoute = "meta-plan", Checks = [new() { Name = "A", Passed = false }, new() { Name = "B", Passed = true }] };

        var comparison = BenchmarkService.Compare(current, baseline);

        Assert.True(comparison.HasRegression);
        Assert.Contains("A", comparison.NewlyFailingChecks);
        Assert.Contains("B", comparison.RecoveredChecks);
        Assert.True(comparison.RouteChanged);
    }

    [Fact]
    public void DifficultyLookup_IsOneBasedAndStable()
    {
        Assert.Equal(1, BenchmarkService.GetPlanForDifficulty(1).Level);
        Assert.Equal(15, BenchmarkService.GetPlanForDifficulty(15).Level);
    }

    [Fact]
    public async Task PrepareAsync_RejectsFilesystemRoot()
    {
        var service = new BenchmarkService(Path.Combine(_root, "data"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareAsync(1, Path.GetPathRoot(_root)!));
    }

    [Fact]
    public async Task HistorySummary_AggregatesPersistedRuns()
    {
        var sandbox = Path.Combine(_root, "sandbox");
        Directory.CreateDirectory(Path.Combine(sandbox, "benchmark_test_1"));
        await File.WriteAllTextAsync(Path.Combine(sandbox, "benchmark_test_1", "test.md"),
            "Hello world\nThe capital of France is Paris");
        var service = new BenchmarkService(Path.Combine(_root, "data"));
        await service.EvaluateAsync(1, sandbox, "model-a", 100);
        await service.EvaluateAsync(1, sandbox, "model-a", 200);

        var summary = service.BuildHistorySummary(1, "model-a");

        Assert.Equal(2, summary.RunCount);
        Assert.Equal(100, summary.AverageScore);
        Assert.True(summary.ByLevel.ContainsKey(1));
        Assert.True(summary.ByModel.ContainsKey("model-a"));
    }

    [Fact]
    public async Task PrepareAsync_CreatesDeterministicEditFixture()
    {
        var sandbox = Path.Combine(_root, "sandbox");
        var service = new BenchmarkService(Path.Combine(_root, "data"));

        var prepared = await service.PrepareAsync(8, sandbox);
        var fixture = Path.Combine(prepared.RunRoot, "edit_strategy", "property-update", "CacheOptions.cs");
        var fixtureContent = await File.ReadAllTextAsync(fixture);
        Assert.Contains("MaxEntries { get; set; } = 100;", fixtureContent);
        Assert.DoesNotContain("\\n", fixtureContent);
        Assert.Contains("\n", fixtureContent);

        await File.AppendAllTextAsync(fixture, "\nBROKEN");
        var preparedAgain = await service.PrepareAsync(8, sandbox);
        var freshFixture = Path.Combine(preparedAgain.RunRoot, "edit_strategy", "property-update", "CacheOptions.cs");
        Assert.DoesNotContain("BROKEN", await File.ReadAllTextAsync(freshFixture));
        Assert.NotEqual(prepared.RunId, preparedAgain.RunId);
    }

    [Fact]
    public async Task OccurrenceCheck_DetectsDuplicateProperty()
    {
        var sandbox = Path.Combine(_root, "sandbox");
        var service = new BenchmarkService(Path.Combine(_root, "data"));
        var prepared = await service.PrepareAsync(8, sandbox);
        var fixture = Path.Combine(prepared.RunRoot, "edit_strategy", "property-update", "CacheOptions.cs");
        var content = await File.ReadAllTextAsync(fixture);
        content = content.Replace("= 100;", "= 250;\n    public int MaxEntries { get; set; } = 250;");
        await File.WriteAllTextAsync(fixture, content);

        var result = await service.EvaluateAsync(8, prepared.RunRoot, "test-model", 10);

        Assert.Contains(result.Checks, check => check.Name == "Property is not duplicated" && !check.Passed);
        Assert.True(result.PreservationPercent < 100);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
