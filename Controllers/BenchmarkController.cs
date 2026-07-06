using Microsoft.AspNetCore.Mvc;
using Weaver.Services;

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

    [HttpGet("system-info")]
    public IActionResult GetSystemInfoConfig()
    {
        var custom = _benchmark.LoadCustomSystemInfo();
        var detected = BenchmarkService.DetectSystemInfo();
        return Ok(new { detected, custom });
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
