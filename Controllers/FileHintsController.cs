using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using System.IO;

namespace MaestroBackend.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FileHintsController : ControllerBase
    {
        private readonly string _filePath = "filehints.json";

        [HttpGet]
        public IActionResult GetFileHints()
        {
            if (!System.IO.File.Exists(_filePath))
            {
                return NotFound("File hints file not found.");
            }

            var fileContent = System.IO.File.ReadAllText(_filePath);
            return Ok(fileContent);
        }

        [HttpPut]
        public IActionResult UpdateFileHints([FromBody] object content)
        {
            try
            {
                var json = JsonSerializer.Serialize(content);
                JsonDocument.Parse(json);
                System.IO.File.WriteAllText(_filePath, json);
                return Ok("File hints updated successfully.");
            }
            catch (JsonException)
            {
                return BadRequest("Invalid JSON format.");
            }
            catch (Exception)
            {
                return StatusCode(500, "An error occurred while updating file hints.");
            }
        }
    }
}