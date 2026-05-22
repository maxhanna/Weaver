using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using System.IO;
using System.Text;
using System;
using System.Linq;
using System.Threading.Tasks;

[ApiController]
[Route("api/editor")]
public class FileEditController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;

    public FileEditController(IConfiguration config, IWebHostEnvironment env)
    {
        _config = config;
        _env = env;
    }

    public class EditRequest
    {
        public string Project { get; set; } = "";
        public string Path { get; set; } = "";
        public string Content { get; set; } = "";
        public bool Apply { get; set; } = true;
        public bool CreateIfMissing { get; set; } = true;
    }

    [HttpPost("write")]
    public async Task<IActionResult> Write([FromBody] EditRequest req)
    {
        if (req == null) return BadRequest("Missing request");

        var configuredRoot = _config.GetValue<string>("Editor:WorkspaceRoot");
        string workspaceRoot;
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            workspaceRoot = Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.GetFullPath(Path.Combine(_env.ContentRootPath, configuredRoot));
        }
        else
        {
            workspaceRoot = Path.GetFullPath(Path.Combine(_env.ContentRootPath, ".."));
        }

        var projectSegment = string.IsNullOrWhiteSpace(req.Project) ? "" : req.Project.Trim().TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relativePath = req.Path?.Trim() ?? "";
        var targetFull = Path.GetFullPath(Path.Combine(workspaceRoot, projectSegment, relativePath));

        if (!targetFull.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Path outside workspace root is not allowed.");
        }

        if (!req.Apply)
        {
            return Ok(new { path = targetFull, exists = System.IO.File.Exists(targetFull) });
        }

        var dir = Path.GetDirectoryName(targetFull);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        if (!System.IO.File.Exists(targetFull) && !req.CreateIfMissing)
        {
            return NotFound("File does not exist.");
        }

        try
        {
            await System.IO.File.WriteAllTextAsync(targetFull, req.Content ?? string.Empty, Encoding.UTF8);
            return Ok(new { path = targetFull, written = true });
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }

    [HttpGet("projects")]
    public IActionResult Projects()
    {
        var configuredRoot = _config.GetValue<string>("Editor:WorkspaceRoot");
        string workspaceRoot = !string.IsNullOrWhiteSpace(configuredRoot)
            ? (Path.IsPathRooted(configuredRoot) ? configuredRoot : Path.GetFullPath(Path.Combine(_env.ContentRootPath, configuredRoot)))
            : Path.GetFullPath(Path.Combine(_env.ContentRootPath, ".."));
        try
        {
            var dirs = Directory.GetDirectories(workspaceRoot, "*", SearchOption.TopDirectoryOnly)
                        .Select(d => new { name = Path.GetFileName(d), path = Path.GetRelativePath(workspaceRoot, d) });
            return Ok(dirs);
        }
        catch (Exception ex) { return StatusCode(500, ex.Message); }
    }

    [HttpGet("list")]
    public IActionResult List([FromQuery] string project = "", [FromQuery] string path = "")
    {
        var configuredRoot = _config.GetValue<string>("Editor:WorkspaceRoot");
        string workspaceRoot = !string.IsNullOrWhiteSpace(configuredRoot)
            ? (Path.IsPathRooted(configuredRoot) ? configuredRoot : Path.GetFullPath(Path.Combine(_env.ContentRootPath, configuredRoot)))
            : Path.GetFullPath(Path.Combine(_env.ContentRootPath, ".."));

        var projectSegment = string.IsNullOrWhiteSpace(project) ? "" : project.Trim().TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var projectRoot = Path.GetFullPath(Path.Combine(workspaceRoot, projectSegment));
        var relativePath = (path ?? "").Trim();
        var targetFull = Path.GetFullPath(Path.Combine(projectRoot, relativePath));

        if (!targetFull.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Path outside project root is not allowed.");
        }

        if (System.IO.File.Exists(targetFull))
        {
            return Ok(new
            {
                path = Path.GetRelativePath(projectRoot, targetFull).Replace("\\", "/"),
                name = Path.GetFileName(targetFull),
                isDirectory = false
            });
        }

        if (!Directory.Exists(targetFull))
        {
            return NotFound("Directory not found.");
        }

        try
        {
            var dirs = Directory.GetDirectories(targetFull).Select(d => new
            {
                name = Path.GetFileName(d),
                path = Path.GetRelativePath(projectRoot, d).Replace("\\", "/"),
                isDirectory = true
            });

            var files = Directory.GetFiles(targetFull).Select(f => new
            {
                name = Path.GetFileName(f),
                path = Path.GetRelativePath(projectRoot, f).Replace("\\", "/"),
                isDirectory = false
            });

            var entries = dirs.Concat(files).OrderByDescending(x => x.isDirectory).ThenBy(x => x.name);

            return Ok(new { path = Path.GetRelativePath(projectRoot, targetFull).Replace("\\", "/"), entries });
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }
}
