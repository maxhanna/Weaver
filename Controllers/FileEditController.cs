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
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

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
    public IActionResult List([FromQuery] string project = "", [FromQuery] string path = "", [FromQuery] string search = "")
    {
        var configuredRoot = _config.GetValue<string>("Editor:WorkspaceRoot");
        string workspaceRoot = !string.IsNullOrWhiteSpace(configuredRoot)
            ? (Path.IsPathRooted(configuredRoot) ? configuredRoot : Path.GetFullPath(Path.Combine(_env.ContentRootPath, configuredRoot)))
            : Path.GetFullPath(Path.Combine(_env.ContentRootPath, ".."));

        var projectSegment = string.IsNullOrWhiteSpace(project) ? "" : project.Trim().TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var projectRoot = Path.GetFullPath(Path.Combine(workspaceRoot, projectSegment));

        try
        {
            // Recursive search when a search term is provided
            if (!string.IsNullOrWhiteSpace(search))
            {
                var searchTerm = search.Trim();
                // Determine the search root based on whether a path is specified
                var searchRoot = string.IsNullOrWhiteSpace(path) ? projectRoot : Path.GetFullPath(Path.Combine(projectRoot, path.Trim()));
                
                // Validate that the search root is within the project root
                if (!searchRoot.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest("Path outside project root is not allowed.");
                }
                
                var matchingDirs = Directory.EnumerateDirectories(searchRoot, "*", SearchOption.AllDirectories)
                    .Where(d => Path.GetFileName(d).IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(d => new
                    {
                        name = Path.GetFileName(d),
                        path = Path.GetRelativePath(projectRoot, d).Replace("\\", "/"),
                        isDirectory = true
                    });

                var matchingFiles = Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories)
                    .Where(f => Path.GetFileName(f).IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(f => new
                    {
                        name = Path.GetFileName(f),
                        path = Path.GetRelativePath(projectRoot, f).Replace("\\", "/"),
                        isDirectory = false
                    });

                var searchEntries = matchingDirs.Concat(matchingFiles).OrderByDescending(x => x.isDirectory).ThenBy(x => x.name);

                return Ok(new { path = "", entries = searchEntries, search = searchTerm });
            }

            // Normal directory listing when no search term
            var relativePath = (path ?? "").Trim();
            var targetFull = Path.GetFullPath(Path.Combine(projectRoot, relativePath));

            // Ensure the target path is within the project root
            if (!targetFull.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest("Path outside project root is not allowed.");
            }

            // If a specific file is requested, return its info
            if (System.IO.File.Exists(targetFull))
            {
                return Ok(new
                {
                    path = Path.GetRelativePath(projectRoot, targetFull).Replace("\\", "/"),
                    name = Path.GetFileName(targetFull),
                    isDirectory = false
                });
            }

            // If the path is a file but doesn't exist, return not found
            if (System.IO.File.Exists(targetFull))
            {
                return NotFound("File not found.");
            }

            // If the path is not a directory, return not found
            if (!Directory.Exists(targetFull))
            {
                return NotFound("Directory not found.");
            }

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

    [HttpGet("content")]
    public IActionResult GetContent([FromQuery] string project = "", [FromQuery] string path = "")
    {
        if (string.IsNullOrEmpty(path))
        {
            return BadRequest("Path is required");
        }

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

        var projectSegment = string.IsNullOrWhiteSpace(project) ? "" : project.Trim().TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relativePath = path.Trim();
        var targetFull = Path.GetFullPath(Path.Combine(workspaceRoot, projectSegment, relativePath));

        if (!targetFull.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Path outside workspace root is not allowed.");
        }

        if (!System.IO.File.Exists(targetFull))
        {
            return NotFound("File not found.");
        }

        try
        {
            var content = System.IO.File.ReadAllText(targetFull);
            return Ok(new { content });
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }

    [HttpPost("save")]
    public async Task<IActionResult> Save([FromBody] EditRequest req)
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
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

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
}
