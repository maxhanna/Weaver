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

    // This static lock ensures only ONE save request processes at a time across the whole server.
    private static readonly SemaphoreSlim _saveLock = new SemaphoreSlim(1, 1);

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

        // 1. Serialize JSON before locking (optimization)
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

        // 2. Wait for the Queue (Semaphore)
        await _saveLock.WaitAsync();

        try
        {
            // 3. Execute the Critical Save (with retries)
            await SaveWithRetryAsync(json);
            return Ok();
        }
        finally
        {
            // 4. Release the Queue
            _saveLock.Release();
        }
    }

    private async Task SaveWithRetryAsync(string json)
    {
        int retryCount = 0;
        int maxRetries = 3;
        int delay = 100;

        while (retryCount < maxRetries)
        {
            try
            {
                await _svc.SaveRawAsync(json);
                return; // Success
            }
            catch (Exception ex)
            {
                retryCount++;
                _logger.LogWarning(ex, "Save attempt {RetryCount} failed.", retryCount);

                if (retryCount >= maxRetries)
                {
                    // We give up. The service has already rolled back the file internally if needed.
                    throw;
                }

                await Task.Delay(delay);
                delay *= 2; // Exponential backoff
            }
        }
    }
}