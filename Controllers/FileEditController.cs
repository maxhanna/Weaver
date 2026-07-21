using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Diagnostics;
namespace Weaver.Controllers;

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
            var lastModified = System.IO.File.GetLastWriteTimeUtc(targetFull).ToString("O");
            return Ok(new { content, lastModified });
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }
    [HttpGet("check-modified")]
    public IActionResult CheckModified([FromQuery] string project = "", [FromQuery] string path = "", [FromQuery] string? since = null)
    {
        if (string.IsNullOrEmpty(path))
            return BadRequest("Path is required");
        var configuredRoot = _config.GetValue<string>("Editor:WorkspaceRoot");
        string workspaceRoot;
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            workspaceRoot = Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.GetFullPath(Path.Combine(_env.ContentRootPath, configuredRoot));
        else
            workspaceRoot = Path.GetFullPath(Path.Combine(_env.ContentRootPath, ".."));
        var projectSegment = string.IsNullOrWhiteSpace(project) ? "" : project.Trim().TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relativePath = path.Trim();
        var targetFull = Path.GetFullPath(Path.Combine(workspaceRoot, projectSegment, relativePath));
        if (!targetFull.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
            return BadRequest("Path outside workspace root is not allowed.");
        if (!System.IO.File.Exists(targetFull))
            return Ok(new { exists = false, modified = false, lastModified = (string?)null });
        var lastModified = System.IO.File.GetLastWriteTimeUtc(targetFull);
        var modified = true;
        if (!string.IsNullOrWhiteSpace(since) && DateTime.TryParse(since, null, System.Globalization.DateTimeStyles.RoundtripKind, out var sinceDt))
        {
            modified = lastModified > sinceDt;
        }
        return Ok(new { exists = true, modified, lastModified = lastModified.ToString("O") });
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
            // Staged diff
            using var stagedProc = new Process();
            stagedProc.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "diff --staged --no-color",
                WorkingDirectory = projectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            stagedProc.Start();
            var stagedOutput = await stagedProc.StandardOutput.ReadToEndAsync();
            await stagedProc.WaitForExitAsync();
            // Untracked files
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
            // Current branch name
            using var branchProc = new Process();
            branchProc.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse --abbrev-ref HEAD",
                WorkingDirectory = projectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            branchProc.Start();
            var branchOutput = await branchProc.StandardOutput.ReadToEndAsync();
            await branchProc.WaitForExitAsync();
            // Parse working-dir diff into structured file-level entries
            var files = new List<object>();
            var currentFile = "";
            var currentLines = new List<string>();
            void FlushFile(List<object> target)
            {
                if (!string.IsNullOrWhiteSpace(currentFile) && currentLines.Count > 0)
                {
                    target.Add(new
                    {
                        path = currentFile,
                        body = string.Join("\n", currentLines)
                    });
                }
            }
            foreach (var line in output.Split('\n'))
            {
                if (line.StartsWith("diff --git "))
                {
                    FlushFile(files);
                    var parts = line.Split(' ');
                    currentFile = parts.Length >= 4 && parts[3].Length > 2 ? parts[3][2..] : "";
                }
                currentLines.Add(line);
            }
            FlushFile(files);
            // Parse staged diff
            var stagedFiles = new List<object>();
            currentFile = "";
            currentLines.Clear();
            foreach (var line in stagedOutput.Split('\n'))
            {
                if (line.StartsWith("diff --git "))
                {
                    FlushFile(stagedFiles);
                    var parts = line.Split(' ');
                    currentFile = parts.Length >= 4 && parts[3].Length > 2 ? parts[3][2..] : "";
                }
                currentLines.Add(line);
            }
            FlushFile(stagedFiles);
            var untrackedFiles = untrackedOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .ToList();
            var branchName = branchOutput?.Trim() ?? "unknown";
            var hasUnpushed = false;
            try
            {
                using var unpushedProc = new Process();
                unpushedProc.StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "rev-list --count @{u}..HEAD",
                    WorkingDirectory = projectRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                unpushedProc.Start();
                var unpushedOut = (await unpushedProc.StandardOutput.ReadToEndAsync()).Trim();
                await unpushedProc.WaitForExitAsync();
                if (int.TryParse(unpushedOut, out var count))
                    hasUnpushed = count > 0;
            }
            catch { }
            return Ok(new
            {
                diff = output,
                files,
                staged = stagedFiles,
                untracked = untrackedFiles,
                branch = branchName,
                hasChanges = !string.IsNullOrWhiteSpace(output) || stagedFiles.Count > 0 || untrackedFiles.Count > 0,
                hasStaged = stagedFiles.Count > 0,
                hasUnpushed
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
    public class GitCommitRequest
    {
        public string Project { get; set; } = "";
        public string Message { get; set; } = "";
    }
    public class GitPrRequest
    {
        public string Project { get; set; } = "";
        public string Message { get; set; } = "";
        public string? Summary { get; set; }
    }
    private async Task<string> RunGitAsync(string args, string workingDir)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        proc.Start();
        var output = await proc.StandardOutput.ReadToEndAsync();
        var error = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return proc.ExitCode == 0 ? output : error;
    }
    [HttpPost("git-commit")]
    public async Task<IActionResult> GitCommit([FromBody] GitCommitRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Project))
            return BadRequest(new { error = "Project required" });
        var projectRoot = ResolveProjectRoot(req.Project);
        if (!Directory.Exists(projectRoot))
            return NotFound(new { error = "Project directory not found." });
        try
        {
            var addOut = await RunGitAsync("add -A", projectRoot);
            var escaped = (req.Message ?? "commit").Replace("\"", "\\\"");
            var commitOut = await RunGitAsync($"commit -m \"{escaped}\"", projectRoot);
            var isNoOp = commitOut.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase);
            return Ok(new { success = !isNoOp, addOutput = addOut, commitOutput = commitOut, nothingToCommit = isNoOp });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }
    [HttpPost("git-push")]
    public async Task<IActionResult> GitPush([FromBody] GitCommitRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Project))
            return BadRequest(new { error = "Project required" });
        var projectRoot = ResolveProjectRoot(req.Project);
        if (!Directory.Exists(projectRoot))
            return NotFound(new { error = "Project directory not found." });
        try
        {
            var branch = (await RunGitAsync("rev-parse --abbrev-ref HEAD", projectRoot)).Trim();
            var pushOut = await RunGitAsync($"push origin \"{branch}\"", projectRoot);
            return Ok(new { success = true, branch, output = pushOut });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }
    [HttpPost("git-pr")]
    public async Task<IActionResult> GitPr([FromBody] GitPrRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Project))
            return BadRequest(new { error = "Project required" });
        var projectRoot = ResolveProjectRoot(req.Project);
        if (!Directory.Exists(projectRoot))
            return NotFound(new { error = "Project directory not found." });
        try
        {
            // Stage + commit
            await RunGitAsync("add -A", projectRoot);
            var escapedMsg = (req.Message ?? "commit").Replace("\"", "\\\"");
            await RunGitAsync($"commit -m \"{escapedMsg}\"", projectRoot);
            // Push to current branch
            var branch = (await RunGitAsync("rev-parse --abbrev-ref HEAD", projectRoot)).Trim();
            var pushOut = await RunGitAsync($"push -u origin \"{branch}\"", projectRoot);
            // Create PR via gh
            var prBody = req.Summary ?? req.Message ?? "";
            var escapedBody = prBody.Replace("\"", "\\\"").Replace("\n", "\\n");
            var escapedTitle = (req.Message ?? "Weaver changes").Replace("\"", "\\\"");
            using var ghProc = new Process();
            ghProc.StartInfo = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = $"pr create --title \"{escapedTitle}\" --body \"{escapedBody}\" --head \"{branch}\"",
                WorkingDirectory = projectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            ghProc.Start();
            var ghOut = await ghProc.StandardOutput.ReadToEndAsync();
            var ghErr = await ghProc.StandardError.ReadToEndAsync();
            await ghProc.WaitForExitAsync();
            return Ok(new
            {
                success = ghProc.ExitCode == 0,
                branch,
                pushOutput = pushOut,
                prUrl = ghProc.ExitCode == 0 ? ghOut?.Trim() : ghErr
            });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }
    private string ResolveProjectRoot(string project)
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
        return Path.GetFullPath(Path.Combine(workspaceRoot, projectSegment));
    }
}
