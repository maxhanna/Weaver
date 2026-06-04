using WeaverBackend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace WeaverBackend.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CalendarController : ControllerBase
    {
        private readonly CalendarService _svc;
        private readonly ILogger<CalendarController> _logger;

        public CalendarController(CalendarService svc, ILogger<CalendarController> logger)
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
                return Ok(Array.Empty<object>());
            }
            try
            {
                return new ContentResult { Content = raw, ContentType = "application/json", StatusCode = 200 };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse saved calendar data, returning empty");
                return Ok(Array.Empty<object>());
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
                _logger.LogError(ex, "Failed to save calendar data");
                return StatusCode(500, "Failed to save calendar data");
            }
        }
    }
}
