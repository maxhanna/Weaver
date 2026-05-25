# Agent Orchestration Implementation: Quick Start

This guide provides step-by-step implementation instructions and code templates to start building the new multi-pipeline architecture.

---

## Step 1: Create Core Interfaces

### File: `Pipelines/IOrchestrationPipeline.cs`

```csharp
namespace MaestroBackend.Pipelines;

/// <summary>
/// Represents a specialized task execution pipeline with its own orchestration strategy.
/// Each pipeline type (CodeEdit, Command, Compound, QuickCheck) implements this interface.
/// </summary>
public interface IOrchestrationPipeline
{
    /// <summary>
    /// Executes the pipeline logic for the given context.
    /// </summary>
    /// <param name="context">Task execution context (prompt, paths, output config)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Result containing success status, summary, and execution steps</returns>
    Task<PipelineResult> ExecuteAsync(OrchestrationContext context, CancellationToken ct = default);
}

/// <summary>
/// Context passed to each pipeline execution.
/// </summary>
public class OrchestrationContext
{
    public string Prompt { get; set; } = "";
    public string ProjectRoot { get; set; } = "";
    public bool EmitSse { get; set; } = false;
    public HttpResponse? Response { get; set; }
    public List<string> AttachedFiles { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Result returned by any pipeline execution.
/// </summary>
public class PipelineResult
{
    public bool Success { get; set; }
    public string Summary { get; set; } = "";
    public List<object> Steps { get; set; } = new();
    public List<string> FilesModified { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Enum for task classification.
/// </summary>
public enum PipelineType
{
    CodeEdit,
    Command,
    Compound,
    QuickCheck
}
```

### File: `Routing/TaskOrchestrationRouter.cs`

```csharp
namespace MaestroBackend.Routing;

using MaestroBackend.Pipelines;
using Microsoft.Extensions.Logging;

public class TaskOrchestrationRouter
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TaskOrchestrationRouter> _logger;

    public TaskOrchestrationRouter(IServiceProvider sp, ILogger<TaskOrchestrationRouter> logger)
    {
        _serviceProvider = sp;
        _logger = logger;
    }

    /// <summary>
    /// Analyzes the prompt and determines the best pipeline to use.
    /// Returns the pipeline instance and the detected task type.
    /// </summary>
    public (IOrchestrationPipeline pipeline, PipelineType type) RoutePrompt(string prompt)
    {
        // Priority 1: Check for special markers (_git, _show, etc.)
        if (DetectSpecialMarkers(prompt, out var markers))
        {
            _logger.LogInformation("Detected special markers: {markers}", string.Join(", ", markers));
            return (_serviceProvider.GetRequiredService<ICommandExecutionPipeline>(), PipelineType.Command);
        }

        // Priority 2: Check for compound tasks (both code edits AND commands)
        var hasCodeEdits = DetectCodeEditVerbs(prompt);
        var hasCommands = DetectCommandOperations(prompt);
        
        if (hasCodeEdits && hasCommands)
        {
            _logger.LogInformation("Detected compound task (code edits + commands)");
            return (_serviceProvider.GetRequiredService<ICompoundPipeline>(), PipelineType.Compound);
        }

        // Priority 3: Pure command/infrastructure tasks
        if (hasCommands)
        {
            _logger.LogInformation("Detected command/infrastructure task");
            return (_serviceProvider.GetRequiredService<ICommandExecutionPipeline>(), PipelineType.Command);
        }

        // Priority 4: Diagnostic/health check tasks
        if (DetectDiagnosticOperation(prompt))
        {
            _logger.LogInformation("Detected diagnostic/health-check task");
            return (_serviceProvider.GetRequiredService<IQuickCheckPipeline>(), PipelineType.QuickCheck);
        }

        // Default: Code edit pipeline
        _logger.LogInformation("Routing to default Code Edit pipeline");
        return (_serviceProvider.GetRequiredService<ICodeEditPipeline>(), PipelineType.CodeEdit);
    }

    /// <summary>
    /// Detects special markers used by the system (_git, _package_install, etc.).
    /// </summary>
    private bool DetectSpecialMarkers(string prompt, out List<string> markers)
    {
        markers = new();
        var specialMarkers = new[] { "_git", "_show", "_package_install", "_ping", "_create_file" };
        
        foreach (var marker in specialMarkers)
        {
            if (prompt.Contains(marker, StringComparison.OrdinalIgnoreCase))
                markers.Add(marker);
        }
        
        return markers.Count > 0;
    }

    /// <summary>
    /// Detects code editing operations (modify, add, fix, style, etc.).
    /// </summary>
    private bool DetectCodeEditVerbs(string prompt)
    {
        var verbs = new[]
        {
            "add", "implement", "fix", "update", "change", "modify", "create", "delete",
            "refactor", "edit", "write", "remove", "style", "color", "position", "align",
            "enhance", "improve", "toggle", "enable", "disable", "insert", "set"
        };
        
        var lower = prompt.ToLowerInvariant();
        return verbs.Any(v => Regex.IsMatch(lower, @"\b" + Regex.Escape(v) + @"\b"));
    }

    /// <summary>
    /// Detects command/infrastructure operations (install, pull, push, build, etc.).
    /// </summary>
    private bool DetectCommandOperations(string prompt)
    {
        var verbs = new[]
        {
            "install", "npm install", "dotnet add package", "git pull", "git push", "git commit",
            "git clone", "build", "run", "start", "stop", "deploy", "publish", "restore",
            "migrate", "seed"
        };
        
        var lower = prompt.ToLowerInvariant();
        return verbs.Any(v => lower.Contains(v));
    }

    /// <summary>
    /// Detects diagnostic/status check operations (ping, health, verify, etc.).
    /// </summary>
    private bool DetectDiagnosticOperation(string prompt)
    {
        var verbs = new[] { "ping", "check", "status", "verify", "health", "diagnose", "test" };
        
        var lower = prompt.ToLowerInvariant();
        return verbs.Any(v => Regex.IsMatch(lower, @"\b" + Regex.Escape(v) + @"\b"));
    }
}
```

---

## Step 2: Implement Core Pipelines

### File: `Pipelines/CommandExecutionPipeline.cs`

```csharp
namespace MaestroBackend.Pipelines;

using MaestroBackend.Services;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

public interface ICommandExecutionPipeline : IOrchestrationPipeline { }

/// <summary>
/// Pipeline for executing shell commands, git operations, package installations, etc.
/// Single-pass execution (no retry loops) — either succeeds or fails once.
/// </summary>
public class CommandExecutionPipeline : ICommandExecutionPipeline
{
    private readonly TerminalService _terminal;
    private readonly ILogger<CommandExecutionPipeline> _logger;

    public CommandExecutionPipeline(TerminalService terminal, ILogger<CommandExecutionPipeline> logger)
    {
        _terminal = terminal;
        _logger = logger;
    }

    public async Task<PipelineResult> ExecuteAsync(OrchestrationContext ctx, CancellationToken ct = default)
    {
        _logger.LogInformation("CommandExecutionPipeline starting for prompt: {prompt}", ctx.Prompt);
        
        var result = new PipelineResult { Steps = new() };
        
        try
        {
            // Step 1: Parse commands from prompt
            var commands = ExtractCommands(ctx.Prompt);
            
            if (commands.Count == 0)
            {
                result.Success = false;
                result.Summary = "No commands detected in prompt";
                _logger.LogWarning("No commands found in prompt");
                return result;
            }

            _logger.LogInformation("Extracted {count} command(s)", commands.Count);

            // Step 2: Execute each command sequentially (NO RETRIES)
            var allSucceeded = true;
            var stepIndex = 0;

            foreach (var cmd in commands)
            {
                _logger.LogInformation("Executing command {index}: {cmd}", stepIndex, cmd);
                
                try
                {
                    _terminal.Start();
                    await _terminal.SendCommandAsync(cmd, ctx.ProjectRoot);
                    
                    // Wait for command completion
                    await Task.Delay(500);
                    var output = _terminal.ReadLastLines(100);
                    
                    var succeeded = !output.Contains("error", StringComparison.OrdinalIgnoreCase)
                                 && !output.Contains("failed", StringComparison.OrdinalIgnoreCase);
                    
                    allSucceeded = allSucceeded && succeeded;

                    var step = new Dictionary<string, object?>
                    {
                        { "index", stepIndex },
                        { "type", "command" },
                        { "command", cmd },
                        { "output", output },
                        { "status", succeeded ? "done" : "error" }
                    };
                    
                    result.Steps.Add(step);
                    _logger.LogInformation("Command {index} completed with status: {status}", stepIndex, succeeded ? "done" : "error");
                    
                    stepIndex++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error executing command: {cmd}", cmd);
                    allSucceeded = false;
                    
                    var errorStep = new Dictionary<string, object?>
                    {
                        { "index", stepIndex },
                        { "type", "command" },
                        { "command", cmd },
                        { "output", ex.Message },
                        { "status", "error" }
                    };
                    
                    result.Steps.Add(errorStep);
                    stepIndex++;
                }
            }

            // Step 3: Return result
            result.Success = allSucceeded;
            result.Summary = allSucceeded
                ? $"Executed {commands.Count} command(s) successfully"
                : $"Executed {commands.Count} command(s); some failed (see steps)";

            _logger.LogInformation("CommandExecutionPipeline completed: {success}", result.Success);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CommandExecutionPipeline failed with exception");
            result.Success = false;
            result.Summary = $"Pipeline error: {ex.Message}";
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    /// <summary>
    /// Extracts individual commands from the prompt.
    /// Handles git, npm, dotnet, and generic shell commands.
    /// </summary>
    private List<string> ExtractCommands(string prompt)
    {
        var commands = new List<string>();
        var lower = prompt.ToLowerInvariant();

        // Git operations
        if (lower.Contains("git pull"))
            commands.Add("git pull");
        if (lower.Contains("git push"))
            commands.Add("git push");
        if (lower.Contains("git commit"))
            commands.Add("git commit -m \"auto-commit\"");
        if (lower.Contains("git clone"))
        {
            // Try to extract URL
            var match = Regex.Match(prompt, @"git clone\s+([^\s]+)");
            commands.Add(match.Success ? $"git clone {match.Groups[1].Value}" : "git clone");
        }

        // Package managers
        if (lower.Contains("npm install"))
        {
            var match = Regex.Match(prompt, @"npm install\s+([^\s]+)", RegexOptions.IgnoreCase);
            commands.Add(match.Success ? $"npm install {match.Groups[1].Value}" : "npm install");
        }
        
        if (lower.Contains("dotnet add package"))
        {
            var match = Regex.Match(prompt, @"dotnet add package\s+([^\s]+)", RegexOptions.IgnoreCase);
            commands.Add(match.Success ? $"dotnet add package {match.Groups[1].Value}" : "dotnet add package");
        }

        // Build commands
        if (lower.Contains("build"))
        {
            if (lower.Contains("dotnet"))
                commands.Add("dotnet build");
            else if (lower.Contains("npm"))
                commands.Add("npm run build");
        }

        return commands;
    }
}
```

### File: `Pipelines/CodeEditPipeline.cs`

```csharp
namespace MaestroBackend.Pipelines;

using Microsoft.Extensions.Logging;

public interface ICodeEditPipeline : IOrchestrationPipeline { }

/// <summary>
/// Pipeline for making code changes (modifying .js, .html, .css files).
/// Uses the existing phased pipeline (DISCOVER → PLAN → EDIT → REVIEW).
/// </summary>
public class CodeEditPipeline : ICodeEditPipeline
{
    private readonly ILogger<CodeEditPipeline> _logger;
    // TODO: Inject reference to existing agent logic (refactored from AgentController)

    public CodeEditPipeline(ILogger<CodeEditPipeline> logger)
    {
        _logger = logger;
    }

    public async Task<PipelineResult> ExecuteAsync(OrchestrationContext ctx, CancellationToken ct = default)
    {
        _logger.LogInformation("CodeEditPipeline starting for prompt: {prompt}", ctx.Prompt);
        
        try
        {
            // TODO: Call the refactored phased pipeline orchestrator
            // This will run: DISCOVER → PLAN → EDIT → REVIEW (with proper logic)
            
            return new PipelineResult
            {
                Success = false,
                Summary = "CodeEditPipeline not yet implemented"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CodeEditPipeline failed");
            return new PipelineResult
            {
                Success = false,
                Summary = $"Pipeline error: {ex.Message}",
                ErrorMessage = ex.Message
            };
        }
    }
}
```

### File: `Pipelines/CompoundPipeline.cs`

```csharp
namespace MaestroBackend.Pipelines;

using MaestroBackend.Routing;
using Microsoft.Extensions.Logging;

public interface ICompoundPipeline : IOrchestrationPipeline { }

/// <summary>
/// Pipeline for tasks that combine multiple operation types
/// (e.g., "pull changes then add styling" = command + code edit).
/// Decomposes the prompt into sub-tasks, routes each to appropriate pipeline,
/// and sequences them logically.
/// </summary>
public class CompoundPipeline : ICompoundPipeline
{
    private readonly TaskOrchestrationRouter _router;
    private readonly ILogger<CompoundPipeline> _logger;

    public CompoundPipeline(TaskOrchestrationRouter router, ILogger<CompoundPipeline> logger)
    {
        _router = router;
        _logger = logger;
    }

    public async Task<PipelineResult> ExecuteAsync(OrchestrationContext ctx, CancellationToken ct = default)
    {
        _logger.LogInformation("CompoundPipeline starting for prompt: {prompt}", ctx.Prompt);
        
        var result = new PipelineResult { Steps = new() };
        
        try
        {
            // Step 1: Decompose into sub-tasks
            var subTasks = DecomposePrompt(ctx.Prompt);
            
            if (subTasks.Count == 0)
            {
                result.Success = false;
                result.Summary = "Failed to decompose compound task";
                return result;
            }

            _logger.LogInformation("Decomposed into {count} sub-tasks", subTasks.Count);

            // Step 2: Order sub-tasks (commands before edits usually)
            var ordered = OrderSubTasks(subTasks);

            // Step 3: Execute each sub-task
            var allSucceeded = true;
            var subTaskIndex = 0;

            foreach (var subTask in ordered)
            {
                var (pipeline, pipelineType) = _router.RoutePrompt(subTask.Prompt);
                
                _logger.LogInformation("Executing sub-task {index} as {type}: {prompt}", 
                    subTaskIndex, pipelineType, subTask.Prompt);

                var subCtx = new OrchestrationContext
                {
                    Prompt = subTask.Prompt,
                    ProjectRoot = ctx.ProjectRoot,
                    EmitSse = ctx.EmitSse,
                    Response = ctx.Response,
                    Metadata = ctx.Metadata
                };

                var subResult = await pipeline.ExecuteAsync(subCtx, ct);
                allSucceeded = allSucceeded && subResult.Success;
                
                result.Steps.AddRange(subResult.Steps);
                result.FilesModified.AddRange(subResult.FilesModified);
                
                _logger.LogInformation("Sub-task {index} completed with success={success}", 
                    subTaskIndex, subResult.Success);
                
                subTaskIndex++;
            }

            result.Success = allSucceeded;
            result.Summary = $"Completed {subTasks.Count} sub-task(s): {string.Join(", ", subTasks.Select(t => t.Description))}";
            
            _logger.LogInformation("CompoundPipeline completed: {success}", result.Success);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CompoundPipeline failed");
            result.Success = false;
            result.Summary = $"Pipeline error: {ex.Message}";
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    /// <summary>
    /// Decomposes a compound prompt into logical sub-tasks.
    /// Example: "pull changes and add styling" → ["pull changes", "add styling"]
    /// </summary>
    private List<SubTask> DecomposePrompt(string prompt)
    {
        var subTasks = new List<SubTask>();
        var lower = prompt.ToLowerInvariant();

        // Split on conjunctions (and, then, also, etc.)
        var parts = Regex.Split(prompt, @"\s+(?:and|then|also|after)\s+", RegexOptions.IgnoreCase);
        
        foreach (var part in parts)
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                subTasks.Add(new SubTask
                {
                    Prompt = part.Trim(),
                    Description = part.Length > 50 ? part.Substring(0, 47) + "..." : part.Trim()
                });
            }
        }

        return subTasks;
    }

    /// <summary>
    /// Orders sub-tasks logically (commands before edits is usually good).
    /// </summary>
    private List<SubTask> OrderSubTasks(List<SubTask> tasks)
    {
        // Commands first, then edits
        var commands = tasks.Where(t => IsCommandTask(t.Prompt)).ToList();
        var edits = tasks.Where(t => !IsCommandTask(t.Prompt)).ToList();
        
        var result = new List<SubTask>();
        result.AddRange(commands);
        result.AddRange(edits);
        
        return result;
    }

    private bool IsCommandTask(string prompt)
    {
        var lower = prompt.ToLowerInvariant();
        var commandVerbs = new[] { "install", "pull", "push", "build", "run", "start", "ping" };
        return commandVerbs.Any(v => lower.Contains(v));
    }

    private class SubTask
    {
        public string Prompt { get; set; } = "";
        public string Description { get; set; } = "";
    }
}
```

---

## Step 3: Wire Into Dependency Injection

### File: `Program.cs` (Updated)

```csharp
using MaestroBackend.Pipelines;
using MaestroBackend.Routing;
using MaestroBackend.Services;

var builder = WebApplication.CreateBuilder(args);

// Register existing services
builder.Services.AddSingleton<TerminalService>();
builder.Services.AddSingleton<ConfigFileService>();
var basePath = builder.Environment.ContentRootPath;
builder.Services.AddSingleton(new FileHintsManager(basePath));

// Register new orchestration infrastructure
builder.Services.AddSingleton<TaskOrchestrationRouter>();

// Register pipelines
builder.Services.AddSingleton<ICommandExecutionPipeline, CommandExecutionPipeline>();
builder.Services.AddSingleton<ICodeEditPipeline, CodeEditPipeline>();
builder.Services.AddSingleton<ICompoundPipeline, CompoundPipeline>();
// TODO: Add QuickCheckPipeline when implemented

// Existing HTTP and controllers config
builder.Services.AddHttpClient("llama", client =>
{
    client.Timeout = TimeSpan.FromMinutes(30);
});
builder.Services.AddControllers();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
}));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseCors();

app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();
```

---

## Step 4: Update AgentController Entry Point

### File: `Controllers/AgentController.cs` (ExecuteStream Updated)

```csharp
[HttpPost("execute-stream")]
public async Task ExecuteStream([FromBody] AgentRequest req)
{
    Response.ContentType = "text/event-stream";
    Response.Headers["Cache-Control"] = "no-cache";
    Response.Headers["X-Accel-Buffering"] = "no";

    if (string.IsNullOrWhiteSpace(req.Prompt))
    {
        await SendSse(Response, "error", new { message = "Prompt is required" });
        await SendSse(Response, "done", new { });
        return;
    }

    try
    {
        var projectRoot = GetProjectRoot(req.Project);
        
        // NEW: Use task router to select appropriate pipeline
        var router = HttpContext.RequestServices.GetRequiredService<TaskOrchestrationRouter>();
        var (pipeline, taskType) = router.RoutePrompt(req.Prompt);
        
        await SendSse(Response, "phase", new { phase = "start", taskType = taskType.ToString() });
        await EmitLog(true, "info", $"Agent run started (routing to {taskType} pipeline)",
            new { projectRoot, task = req.Prompt });

        // Create orchestration context
        var context = new OrchestrationContext
        {
            Prompt = req.Prompt,
            ProjectRoot = projectRoot,
            EmitSse = true,
            Response = Response,
            AttachedFiles = req.Files ?? new()
        };

        // Execute via selected pipeline
        var pipelineResult = await pipeline.ExecuteAsync(context, Response.HttpContext.RequestAborted);

        // Emit final result
        await SendSse(Response, "done", new
        {
            summary = pipelineResult.Summary,
            success = pipelineResult.Success,
            steps = pipelineResult.Steps,
            filesModified = pipelineResult.FilesModified,
            errorMessage = pipelineResult.ErrorMessage
        });
    }
    catch (Exception ex)
    {
        await SendSse(Response, "error", new { message = ex.Message });
        await SendSse(Response, "done", new { });
    }
}
```

---

## Testing Checklist

- [ ] Test CommandExecutionPipeline with `git pull` command
- [ ] Test CodeEditPipeline with code change prompt
- [ ] Test CompoundPipeline with "pull then style" prompt
- [ ] Verify no duplicate command executions (git should run once)
- [ ] Verify proper routing classification for each task type
- [ ] Check SSE output formatting for clarity
- [ ] Verify review loop only applies to code edits

---

**Next Steps:**
1. Implement each pipeline with proper error handling
2. Create special marker handlers (refactor from RunEditPhase)
3. Implement QuickCheckPipeline for diagnostics
4. Build comprehensive test suite
5. Performance profiling to ensure no regressions
