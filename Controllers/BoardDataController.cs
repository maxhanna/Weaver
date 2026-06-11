using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Weaver.Services;

namespace Weaver.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class BoardDataController : ControllerBase
    {
        private readonly BoardDataService _svc;
        private readonly ILogger<BoardDataController> _logger;

        // Using SemaphoreSlim ensures thread-safe queuing without deadlocks.
        // Initial count 1 means only 1 thread can enter at a time (acts as a mutex/lock).
        private static readonly SemaphoreSlim _saveLock = new SemaphoreSlim(1, 1);

        public BoardDataController(BoardDataService svc, ILogger<BoardDataController> logger)
        {
            _svc = svc;
            _logger = logger;
        }

        [HttpGet("load")]
        public async Task<IActionResult> Load()
        {
            var raw = await _svc.LoadRawAsync();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Ok(new { todo = Array.Empty<object>(), doing = Array.Empty<object>(), done = Array.Empty<object>(), archived = Array.Empty<object>() });
            }
            try
            {
                return new ContentResult
                {
                    Content = raw,
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse saved boarddata, returning default state");
                return Ok(new { todo = Array.Empty<object>(), doing = Array.Empty<object>(), done = Array.Empty<object>(), archived = Array.Empty<object>() });
            }
        }

        [HttpPost("save")] // Typically Save is a POST/PUT, not a GET
        public async Task<IActionResult> Save([FromBody] object data)
        {
            if (data == null)
            {
                return BadRequest("Data cannot be null");
            }

            string json;
            try
            {
                // 1. Validate and Serialize upfront (before locking)
                json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });

                if (string.IsNullOrWhiteSpace(json))
                {
                    _logger.LogWarning("Attempted to save empty JSON data.");
                    return BadRequest("Data is empty");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to serialize data.");
                return BadRequest("Invalid data format");
            }

            // 2. Enter the Queue (Wait for our turn)
            // This blocks the request until the previous save is fully done.
            await _saveLock.WaitAsync();

            try
            {
                // 3. Perform the Critical Save with Retries
                await SaveWithRetryAsync(json);
                return Ok();
            }
            finally
            {
                // 4. Release the lock so the next queued request can proceed
                _saveLock.Release();
            }
        }

        private async Task SaveWithRetryAsync(string json)
        {
            var retryCount = 0;
            var maxRetries = 3;
            var delay = 100; // ms

            while (retryCount < maxRetries)
            {
                try
                {
                    // Assuming BoardDataService handles the atomic file writing
                    await _svc.SaveRawAsync(json);

                    // If successful, log and break
                    _logger.LogInformation("Board data saved successfully.");
                    return;
                }
                catch (Exception ex)
                {
                    retryCount++;
                    _logger.LogWarning(ex, "Failed to save board data. Attempt {RetryCount}/{MaxRetries}", retryCount, maxRetries);

                    if (retryCount >= maxRetries)
                    {
                        _logger.LogError(ex, "CRITICAL: Failed to save board data after {RetryCount} attempts.", retryCount);
                        throw; // Throw to be caught by the outer try/catch block in Save()
                    }

                    // Exponential backoff
                    await Task.Delay(delay);
                    delay *= 2;
                }
            }
        }
    }
}