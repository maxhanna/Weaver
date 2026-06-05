using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Weaver.Controllers
{
    [ApiController]
    [Route("api/improvementdata")]
    public class ImprovementDataController : ControllerBase
    {
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config;

        public ImprovementDataController(IWebHostEnvironment env, IConfiguration config)
        {
            _env = env;
            _config = config;
        }

        private string ResolveWorkspaceRoot()
        {
            var configuredRoot = _config.GetValue<string>("Editor:WorkspaceRoot");
            if (!string.IsNullOrWhiteSpace(configuredRoot))
                return Path.IsPathRooted(configuredRoot)
                    ? configuredRoot
                    : Path.GetFullPath(Path.Combine(_env.ContentRootPath, configuredRoot));
            return Path.GetFullPath(Path.Combine(_env.ContentRootPath, ".."));
        }

        private string GetProjectRoot(string project)
        {
            var workspaceRoot = ResolveWorkspaceRoot();
            var projectSegment = string.IsNullOrWhiteSpace(project) ? "" :
                project.Trim().TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(workspaceRoot, projectSegment));
        }

        private string GetFilePath(string project)
        {
            return Path.Combine(GetProjectRoot(project), "improvementdata.json");
        }

        [HttpGet]
        public async Task<IActionResult> Get([FromQuery] string project)
        {
            var filePath = GetFilePath(project);
            if (!System.IO.File.Exists(filePath))
                return Ok(new { features = Array.Empty<object>() });

            try
            {
                var text = await System.IO.File.ReadAllTextAsync(filePath);
                return new ContentResult { Content = text, ContentType = "application/json", StatusCode = 200 };
            }
            catch
            {
                return Ok(new { features = Array.Empty<object>() });
            }
        }

        [HttpPut]
        public async Task<IActionResult> Put([FromBody] JsonElement data)
        {
            var project = "";
            if (data.TryGetProperty("project", out var projEl))
                project = projEl.GetString() ?? "";

            if (string.IsNullOrWhiteSpace(project))
                return BadRequest(new { error = "project is required" });

            var filePath = GetFilePath(project);
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(filePath, json);
            return Ok();
        }
    }
}
