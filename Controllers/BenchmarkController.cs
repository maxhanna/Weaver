using Microsoft.AspNetCore.Mvc;
using Weaver.Services;
using System.Text.Json;
using System.Text;

namespace Weaver.Controllers;

[ApiController]
[Route("api/benchmark")]
public class BenchmarkController : ControllerBase
{
    private readonly BenchmarkService _benchmark;
    private readonly IWebHostEnvironment _env;

    public BenchmarkController(IWebHostEnvironment env)
    {
        _env = env;
        var weaverDataDir = Path.Combine(_env.ContentRootPath, "data");
        _benchmark = new BenchmarkService(weaverDataDir);
    }

    [HttpGet("scores")]
    public IActionResult GetScores()
    {
        var scores = _benchmark.LoadScores();
        return Ok(scores.OrderByDescending(s => s.Timestamp).ToList());
    }

    [HttpGet("info")]
    public IActionResult GetSystemInfo()
    {
        var info = BenchmarkService.DetectSystemInfo();
        return Ok(info);
    }

    [HttpGet("plans")]
    public IActionResult GetPlans()
    {
        var plans = BenchmarkService.GetBenchmarkPlans();
        return Ok(plans);
    }

    [HttpPost("save-score")]
    public IActionResult SaveScore([FromBody] BenchmarkScore score)
    {
        if (score == null)
            return BadRequest("Invalid score data");
        score.Timestamp = DateTime.UtcNow;
        var overrides = _benchmark.LoadCustomSystemInfo();
        score.SystemInfo = _benchmark.ResolveSystemInfo(overrides);
        _benchmark.SaveScore(score);
        return Ok(new { message = "Score saved", id = score.Id });
    }

    [HttpGet("summary")]
    public IActionResult GetSummary([FromQuery] int? level = null, [FromQuery] string? model = null)
    {
        return Ok(_benchmark.BuildHistorySummary(level, model));
    }

    [HttpGet("compare/{currentId}/{baselineId}")]
    public IActionResult Compare(string currentId, string baselineId)
    {
        var scores = _benchmark.LoadScores();
        var current = scores.SingleOrDefault(s => s.Id == currentId);
        var baseline = scores.SingleOrDefault(s => s.Id == baselineId);
        if (current == null || baseline == null) return NotFound(new { message = "Benchmark score not found" });
        if (current.Level != baseline.Level) return BadRequest(new { message = "Baselines must use the same benchmark level" });
        return Ok(BenchmarkService.Compare(current, baseline));
    }

    [HttpGet("export/{id}")]
    public IActionResult Export(string id)
    {
        var score = _benchmark.LoadScores().SingleOrDefault(s => s.Id == id);
        if (score == null) return NotFound(new { message = "Benchmark score not found" });
        var json = JsonSerializer.Serialize(score, new JsonSerializerOptions { WriteIndented = true });
        return File(Encoding.UTF8.GetBytes(json), "application/json", $"weaver-benchmark-{id}.json");
    }

    [HttpGet("routing-calibration")]
    public IActionResult GetRoutingCalibration()
    {
        return Ok(PlannerRoutingCalibrationService.Run());
    }

    [HttpPost("evaluate")]
    public async Task<IActionResult> Evaluate([FromBody] BenchmarkEvaluationRequest request, CancellationToken ct)
    {
        if (request == null || request.Level < 1)
            return BadRequest("Invalid benchmark evaluation request");
        var custom = _benchmark.LoadCustomSystemInfo();
        var root = !string.IsNullOrWhiteSpace(request.BenchmarkProjectRoot)
            ? request.BenchmarkProjectRoot
            : !string.IsNullOrWhiteSpace(custom?.BenchmarkProjectRoot)
                ? custom.BenchmarkProjectRoot
                : AgentUtilities.GetBenchmarkSandboxPath();
        try
        {
            var score = await _benchmark.EvaluateAsync(request.Level, root!, request.ModelUsed ?? "", request.DurationMs, request.ActualStrategies, ct);
            return Ok(score);
        }
        catch (ArgumentOutOfRangeException ex) { return BadRequest(ex.Message); }
        catch (Exception ex) { return StatusCode(500, new { error = "Benchmark evaluation failed", detail = ex.Message }); }
    }

    [HttpPost("prepare/{level:int}")]
    public async Task<IActionResult> Prepare(int level, [FromBody] BenchmarkPrepareRequest? request, CancellationToken ct)
    {
        var custom = _benchmark.LoadCustomSystemInfo();
        var root = !string.IsNullOrWhiteSpace(request?.BenchmarkProjectRoot)
            ? request.BenchmarkProjectRoot
            : !string.IsNullOrWhiteSpace(custom?.BenchmarkProjectRoot)
                ? custom.BenchmarkProjectRoot
                : AgentUtilities.GetBenchmarkSandboxPath();
        try
        {
            var prepared = await _benchmark.PrepareAsync(level, root!, ct);
            return Ok(new { benchmarkProjectRoot = prepared.RunRoot, runId = prepared.RunId });
        }
        catch (ArgumentOutOfRangeException ex) { return BadRequest(ex.Message); }
        catch (Exception ex) { return StatusCode(500, new { error = "Benchmark preparation failed", detail = ex.Message }); }
    }

    [HttpGet("system-info")]
    public IActionResult GetSystemInfoConfig()
    {
        var custom = _benchmark.LoadCustomSystemInfo();
        var detected = BenchmarkService.DetectSystemInfo();
        var defaultRoot = AgentUtilities.GetBenchmarkSandboxPath();
        return Ok(new { detected, custom, defaultBenchmarkRoot = defaultRoot });
    }

    [HttpPost("system-info")]
    public IActionResult SaveSystemInfoConfig([FromBody] CustomSystemInfo info)
    {
        if (info == null)
            return BadRequest("Invalid system info data");
        _benchmark.SaveCustomSystemInfo(info);
        return Ok(new { message = "System info saved" });
    }

    [HttpDelete("scores/{id}")]
    public IActionResult DeleteScore(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest("Missing score id");
        var deleted = _benchmark.DeleteScore(id);
        if (!deleted)
            return NotFound(new { message = "Score not found" });
        return Ok(new { message = "Score deleted" });
    }
}

public class BenchmarkEvaluationRequest
{
    public int Level { get; set; }
    public string? BenchmarkProjectRoot { get; set; }
    public string? ModelUsed { get; set; }
    public double DurationMs { get; set; }
    public List<string> ActualStrategies { get; set; } = new();
}

public class BenchmarkPrepareRequest
{
    public string? BenchmarkProjectRoot { get; set; }
}
