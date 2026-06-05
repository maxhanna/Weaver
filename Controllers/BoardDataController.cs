using Weaver.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace Weaver.Controllers
{
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
            var raw = await _svc.LoadRawAsync();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Ok(new { todo = Array.Empty<object>(), doing = Array.Empty<object>(), done = Array.Empty<object>(), archived = Array.Empty<object>() });
            }

            try
            {
                // Return raw JSON as-is
                return new ContentResult { Content = raw, ContentType = "application/json", StatusCode = 200 };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse saved boarddata, returning default state");
                return Ok(new { todo = Array.Empty<object>(), doing = Array.Empty<object>(), done = Array.Empty<object>(), archived = Array.Empty<object>() });
            }
        }

        [HttpPost("save")]
        public async Task<IActionResult> Save([FromBody] object data)
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await _svc.SaveRawAsync(json);
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save board data");
                return StatusCode(500, "Failed to save board data");
            }
        }
    }
}
