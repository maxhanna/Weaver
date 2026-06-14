using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using System.IO;
using System.Text;
using System;
using System.Diagnostics;
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
        var projectRootPrefix = projectRoot.EndsWith(Path.DirectorySeparatorChar.ToString())
            ? projectRoot : projectRoot + Path.DirectorySeparatorChar;

        try
        {
            // Recursive search when a search term is provided
            if (!string.IsNullOrWhiteSpace(search))
            {
                var searchTerm = search.Trim();
                // Determine the search root based on whether a path is specified
                var searchRoot = string.IsNullOrWhiteSpace(path) ? projectRoot : Path.GetFullPath(Path.Combine(projectRoot, path.Trim()));
                
                // Validate that the search root is within the project root
                if (!string.Equals(searchRoot, projectRoot, StringComparison.OrdinalIgnoreCase) &&
                    !searchRoot.StartsWith(projectRootPrefix, StringComparison.OrdinalIgnoreCase))
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
            var relativePath = (path ?? "").Trim().TrimStart('/', '\\');
            var targetFull = Path.GetFullPath(Path.Combine(projectRoot, relativePath));

            // Ensure the target path is within the project root
            if (!string.Equals(targetFull, projectRoot, StringComparison.OrdinalIgnoreCase) &&
                !targetFull.StartsWith(projectRootPrefix, StringComparison.OrdinalIgnoreCase))
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

            // If the path doesn't exist as a file or directory, return not found
            if (!Directory.Exists(targetFull))
            {
                return NotFound("Path not found.");
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

    [HttpGet("git-diff")]
    public async Task<IActionResult> GitDiff([FromQuery] string project = "")
    {
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
        var projectRoot = Path.GetFullPath(Path.Combine(workspaceRoot, projectSegment));

        if (!Directory.Exists(projectRoot))
        {
            return NotFound("Project directory not found.");
        }

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "diff --no-color",
                WorkingDirectory = projectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            // Also get untracked files
            using var untracked = new Process();
            untracked.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "ls-files --others --exclude-standard",
                WorkingDirectory = projectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            untracked.Start();
            var untrackedOutput = await untracked.StandardOutput.ReadToEndAsync();
            await untracked.WaitForExitAsync();

            // Parse diff into structured file-level entries
            var files = new List<object>();
            var currentFile = "";
            var currentLines = new List<string>();

            void FlushFile()
            {
                if (!string.IsNullOrWhiteSpace(currentFile) && currentLines.Count > 0)
                {
                    var headerCount = currentLines.Count(l => l.StartsWith("---") || l.StartsWith("+++") || l.StartsWith("@@") || l.StartsWith("diff --git") || l.StartsWith("index "));
                    files.Add(new
                    {
                        path = currentFile,
                        header = string.Join("\n", currentLines.Take(headerCount)),
                        body = string.Join("\n", currentLines.Skip(headerCount))
                    });
                }
            }

            foreach (var line in output.Split('\n'))
            {
                if (line.StartsWith("diff --git "))
                {
                    FlushFile();
                    var parts = line.Split(' ');
                    currentFile = parts.Length >= 4 && parts[3].Length > 2 ? parts[3][2..] : "";
                }
                currentLines.Add(line);
            }
            FlushFile();

            var untrackedFiles = untrackedOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .ToList();

            return Ok(new
            {
                diff = output,
                files,
                untracked = untrackedFiles,
                hasChanges = !string.IsNullOrWhiteSpace(output) || untrackedFiles.Count > 0
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("git-diff-file")]
    public async Task<IActionResult> GitDiffFile([FromQuery] string project = "", [FromQuery] string path = "")
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "Path is required" });

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
        var projectRoot = Path.GetFullPath(Path.Combine(workspaceRoot, projectSegment));

        if (!Directory.Exists(projectRoot))
            return NotFound(new { error = "Project directory not found." });

        // Read new content from disk
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, path.Trim().Replace('/', Path.DirectorySeparatorChar)));
        string newContent = "";
        if (System.IO.File.Exists(fullPath))
            newContent = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8);

        // Read old content from git HEAD
        string oldContent = "";
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"show HEAD:\"{path.Trim().Replace('\\', '/')}\"",
                WorkingDirectory = projectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            process.Start();
            var gitOutput = await process.StandardOutput.ReadToEndAsync();
            var gitError = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
                oldContent = gitOutput;
        }
        catch { }

        return Ok(new
        {
            path,
            oldContent,
            newContent,
            isNewFile = string.IsNullOrWhiteSpace(oldContent)
        });
    }
}
