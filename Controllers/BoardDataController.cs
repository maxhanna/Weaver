using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Weaver.Services;

namespace Weaver.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BoardDataController : ControllerBase
{
    private readonly BoardDataService _svc;
    private readonly ILogger<BoardDataController> _logger;

    public BoardDataController(BoardDataService svc, ILogger<BoardDataController> logger)
    {
        _svc = svc;
        _logger = logger;
    }

    [HttpGet("load")]
    public async Task<IActionResult> Load()
    {
        try
        {
            var raw = await _svc.LoadRawAsync();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Ok(new { todo = Array.Empty<object>(), doing = Array.Empty<object>(), done = Array.Empty<object>(), archived = Array.Empty<object>() });
            }

            return new ContentResult
            {
                Content = raw,
                ContentType = "application/json",
                StatusCode = 200
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load board data.");
            return StatusCode(500, "Error loading data");
        }
    }

    [HttpPost("save")]
    public async Task<IActionResult> Save([FromBody] object data)
    {
        if (data == null) return BadRequest("Data cannot be null");

        string json;
        try
        {
            json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            if (string.IsNullOrWhiteSpace(json)) return BadRequest("Empty data");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Serialization failed");
            return BadRequest("Invalid data format");
        }

        try
        {
            // BoardDataService now handles its own locking and retries internally
            await _svc.SaveRawAsync(json);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save failed permanently after retries.");
            return StatusCode(500, "Error saving data");
        }
    }
}