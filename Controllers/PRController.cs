using Weaver.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Weaver.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PRController : ControllerBase
    {
        private readonly GitService _git;
        private readonly ILogger<PRController> _logger;

        public PRController(GitService git, ILogger<PRController> logger)
        {
            _git = git;
            _logger = logger;
        }

        [HttpPost("start")]
        public async Task<IActionResult> Start([FromBody] PrStartRequest req)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.ProjectPath))
                    return BadRequest(new { success = false, error = "ProjectPath required" });

                var originalBranch = await _git.GetCurrentBranchAsync(req.ProjectPath);

                var sanitized = Regex.Replace(req.CardId ?? "task", @"[^a-zA-Z0-9_-]", "");
                var branchName = $"weaver/{sanitized}";

                var hasChanges = await _git.HasUncommittedChangesAsync(req.ProjectPath);
                if (hasChanges)
                {
                    // Stash any existing uncommitted changes so they don't leak into the PR branch
                    await _git.RunGitAsync(req.ProjectPath, "stash push -m \"weaver-auto-stash\"");
                }

                var branchResult = await _git.CreateBranchAsync(req.ProjectPath, branchName);
                if (!branchResult.Success)
                {
                    // Branch may already exist — try with timestamp suffix
                    branchName = $"weaver/{sanitized}-{DateTime.UtcNow:yyyyMMddHHmmss}";
                    branchResult = await _git.CreateBranchAsync(req.ProjectPath, branchName);
                }

                return Ok(new
                {
                    success = branchResult.Success,
                    branchName = branchResult.Success ? branchName : null,
                    originalBranch = branchResult.Success ? originalBranch : null,
                    output = branchResult.Output,
                    error = branchResult.Error
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PR start failed");
                return Ok(new { success = false, error = ex.Message });
            }
        }

        [HttpPost("finish")]
        public async Task<IActionResult> Finish([FromBody] PrFinishRequest req)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.ProjectPath))
                    return BadRequest(new { success = false, error = "ProjectPath required" });

                var branchName = req.BranchName ?? "weaver/" + Regex.Replace(req.CardId ?? "task", @"[^a-zA-Z0-9_-]", "");

                // Commit all changes
                var commitResult = await _git.CommitAllAsync(req.ProjectPath, req.CardText ?? "Weaver agent changes");
                if (!commitResult.Success && !commitResult.Output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase) && !commitResult.Error.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Commit warning: {Output} {Error}", commitResult.Output, commitResult.Error);
                }

                // Push
                var pushResult = await _git.PushAsync(req.ProjectPath, branchName);
                if (!pushResult.Success)
                {
                    return Ok(new { success = false, error = pushResult.Error, branchName, commitHash = ExtractCommitHash(commitResult.Output) });
                }

                // Create PR via gh CLI
                var prBody = $"Automated PR by Weaver agent.\n\n{req.Summary ?? req.CardText ?? ""}";
                var prResult = await _git.CreatePullRequestAsync(req.ProjectPath, req.CardText ?? "Weaver agent changes", prBody, branchName);

                string? prUrl = null;
                if (prResult.Success)
                {
                    prUrl = prResult.Output?.Trim();
                    // gh returns the PR URL on success
                    var urlMatch = Regex.Match(prUrl ?? "", @"https?://[^\s]+");
                    if (urlMatch.Success) prUrl = urlMatch.Value;
                }

                // Restore original branch
                string? restoreError = null;
                if (!string.IsNullOrWhiteSpace(req.OriginalBranch))
                {
                    var checkoutResult = await _git.RunGitAsync(req.ProjectPath, $"checkout \"{req.OriginalBranch}\"");
                    if (checkoutResult.Success)
                    {
                        await _git.RunGitAsync(req.ProjectPath, "stash pop");
                    }
                    else
                    {
                        restoreError = checkoutResult.Error;
                    }
                }

                return Ok(new
                {
                    success = prResult.Success,
                    prUrl = prUrl,
                    branchName = branchName,
                    originalBranch = req.OriginalBranch,
                    commitHash = ExtractCommitHash(commitResult.Output),
                    commitOutput = commitResult.Output,
                    pushOutput = pushResult.Output,
                    prOutput = prResult.Output,
                    prError = prResult.Error,
                    pushError = pushResult.Error,
                    restoreError = restoreError
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PR finish failed");
                return Ok(new { success = false, error = ex.Message });
            }
        }
        private static string? ExtractCommitHash(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            var match = Regex.Match(output, @"\[[^\]]+ ([a-f0-9]{7,40})\]");
            return match.Success ? match.Groups[1].Value : null;
        }
    }

    public class PrStartRequest
    {
        public string? ProjectPath { get; set; }
        public string? CardId { get; set; }
        public string? CardText { get; set; }
    }

    public class PrFinishRequest
    {
        public string? ProjectPath { get; set; }
        public string? CardId { get; set; }
        public string? CardText { get; set; }
        public string? BranchName { get; set; }
        public string? Summary { get; set; }
        public string? OriginalBranch { get; set; }
    }
}
