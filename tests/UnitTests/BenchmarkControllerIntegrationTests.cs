using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Weaver.Controllers;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

public class BenchmarkControllerIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "weaver-benchmark-api-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PrepareEvaluatePersistReload_EndToEnd()
    {
        Directory.CreateDirectory(_root);
        var controller = new BenchmarkController(new FakeEnvironment(_root));
        var prepare = Assert.IsType<OkObjectResult>(await controller.Prepare(1,
            new BenchmarkPrepareRequest { BenchmarkProjectRoot = Path.Combine(_root, "sandbox") }, CancellationToken.None));
        using var preparedJson = JsonDocument.Parse(JsonSerializer.Serialize(prepare.Value));
        var runRoot = preparedJson.RootElement.GetProperty("benchmarkProjectRoot").GetString()!;
        Directory.CreateDirectory(Path.Combine(runRoot, "benchmark_test_1"));
        await File.WriteAllTextAsync(Path.Combine(runRoot, "benchmark_test_1", "test.md"),
            "Hello world\nThe capital of France is Paris");

        var evaluated = Assert.IsType<OkObjectResult>(await controller.Evaluate(new BenchmarkEvaluationRequest
        {
            Level = 1,
            BenchmarkProjectRoot = runRoot,
            ModelUsed = "integration-model",
            DurationMs = 20,
            ActualStrategies = ["whole-file-create"]
        }, CancellationToken.None));
        var score = Assert.IsType<BenchmarkScore>(evaluated.Value);

        Assert.Equal(100, score.ScorePercent);
        Assert.Contains("whole-file-create", score.ActualStrategies);
        var scores = Assert.IsType<OkObjectResult>(controller.GetScores());
        Assert.Single(Assert.IsType<List<BenchmarkScore>>(scores.Value));
    }

    [Fact]
    public async Task Prepare_RejectsUnknownLevel()
    {
        Directory.CreateDirectory(_root);
        var controller = new BenchmarkController(new FakeEnvironment(_root));
        var result = await controller.Prepare(999,
            new BenchmarkPrepareRequest { BenchmarkProjectRoot = Path.Combine(_root, "sandbox") }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Weaver.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(root);
    }
}
