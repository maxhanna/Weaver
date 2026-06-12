using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using System.IO;

namespace Weaver.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FileHintsController : ControllerBase
    {
        private readonly string _filePath;

        public FileHintsController(IWebHostEnvironment env)
        {
            _filePath = Path.Combine(env.ContentRootPath, "data/filehints.json");
        }

        [HttpGet]
        public IActionResult GetFileHints()
        {
            // Check if file exists, if not create it with default structure
            if (!System.IO.File.Exists(_filePath))
            {
                // Ensure the directory exists (good practice)
                var directory = Path.GetDirectoryName(_filePath);
                if (directory != null && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Initialize with the default structure expected by the Angular client
                // Using a raw string ensures the PascalCase "Projects" key matches the JS expectations
                var defaultContent = "{\"Projects\": {}}";
                System.IO.File.WriteAllText(_filePath, defaultContent);
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