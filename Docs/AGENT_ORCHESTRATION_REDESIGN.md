# Maestro Agent Orchestration: Redesigned Multi-Pipeline Architecture

## Executive Summary

The current Phased Pipeline (DISCOVER→PLAN→EDIT→VERIFY) works well for code editing but treats all tasks uniformly, causing:
- **7+ duplicate git operations** in single execution
- **Unnecessary review loops** for non-editing tasks  
- **Poor task classification** leading to failed operations being retried

This document proposes a **Multi-Pipeline Architecture** where each task type gets a specialized, optimized orchestration flow.

---

## Part 1: Current State Analysis

### Current Flow (All Tasks)
```
User Input
    ↓
RunPhasedPipeline()
    ├─ Phase 1: DISCOVER (read files, grep, list)
    ├─ Phase 2: PLAN (LLM: which files to change?)
    ├─ Phase 3: EDIT (LLM: generate patches per file)
    ├─ Phase 4: VERIFY & REVIEW LOOP (up to 3x)
    │   └─ If incomplete: re-plan + re-edit
    └─ Return results
```

### Identified Issues

**Issue 1: Task Type Confusion**
- Git operations (no file edits) → treated as edit failures → retried 3x  
- Package installs (command execution) → same treatment
- Network pings (diagnostics) → same treatment
- Real edits (code changes) → need the full pipeline

Example from logs:
```
Phase 2 — PLAN: asking model which files need to change…
→ Plan: 2 file(s) — _git, _show
Phase 3 — EDIT: applying edits to 2 planned file(s)…
→ Git pull: git pull (executed)
Review attempt 1: no successful edits (WRONG! Git succeeded)
→ Phase 2 — PLAN: asking model which files need to change… (RE-PLAN!)
→ Git pull: git pull (DUPLICATE EXECUTION)
(Repeats 7+ times total)
```

**Issue 2: Scattered Special Marker Handling**
- `_git`, `_show`, `_package_install`, `_ping` defined in planning prompt
- But actual execution logic is buried in `RunEditPhase()` 
- Command building/execution is duplicated
- No dedicated handler infrastructure

**Issue 3: Output/SSE Formatting**
- Garbled text in logs: "ing git pull output: Pulling changes from remote repository..."
- Suggests incomplete string formatting in phase transitions
- No specialized output formatters per operation type

**Issue 4: Review Loop Over-Used**
- Designed for incomplete edits (legitimate retry case)
- Used for non-editing operations (wrong use case)
- Causes infinite re-planning when task isn't file-editing

---

## Part 2: Proposed Multi-Pipeline Architecture

### High-Level Design

```
┌─────────────────────────────────────────────────────────────┐
│                    ORCHESTRATION ROUTER                     │
│  Analyzes prompt, classifies task type, routes to pipeline  │
└─────────────────────────────────────────────────────────────┘
                          │
        ┌─────────────────┼─────────────────┬─────────────┐
        ↓                 ↓                 ↓             ↓
   ┌─────────────┐  ┌──────────────┐  ┌──────────────┐ ┌──────────┐
   │CODE EDIT    │  │COMMAND       │  │COMPOUND      │ │QUICK     │
   │PIPELINE     │  │EXECUTION     │  │PIPELINE      │ │CHECK     │
   │             │  │PIPELINE      │  │              │ │PIPELINE  │
   │• Discovery  │  │• Parse cmd   │  │• Route ops   │ │• Status  │
   │• Planning   │  │• Execute     │  │• Sequence    │ │• Ping    │
   │• Editing    │  │• Capture out │  │• Combine     │ │• Health  │
   │• Review     │  │• Format      │  │• Report      │ │          │
   └─────────────┘  └──────────────┘  └──────────────┘ └──────────┘
        │                 │                 │             │
        └─────────────────┼─────────────────┴─────────────┘
                          ↓
                ┌─────────────────────┐
                │ VERIFICATION        │
                │ PIPELINE            │
                │• Confirm task done  │
                │• Build check        │
                │• Return results     │
                └─────────────────────┘
```

### Pipeline Types

#### 1. **CODE EDIT PIPELINE** (for `.js`, `.html`, `.css` changes)
```
Trigger: prompt contains code-edit verbs (add, fix, modify, etc.) 
         AND no special markers
         AND no shell commands detected

Flow:
  1. DISCOVER: List & read candidate files (deterministic)
  2. PLAN: LLM decides which files to edit (scoped to code)
  3. EDIT: LLM generates patches per file
  4. REVIEW: Check if task complete; loop if needed (up to 3x)
  5. BUILD: Verify compilation
  6. Return: List of modified files

Success Criteria: Files edited AND (if specified) build passes
```

#### 2. **COMMAND EXECUTION PIPELINE** (for git, npm, dotnet, etc.)
```
Trigger: prompt contains special markers (_git, _package_install, etc.)
         OR explicit shell commands detected
         OR infrastructure verbs (pull, push, install, run, etc.)

Flow:
  1. PARSE: Extract command(s) from prompt or LLM
  2. EXECUTE: Run command(s) in sequence
  3. CAPTURE: Get output, status code, error details
  4. FORMAT: Structured result {command, output, status, exitCode}
  5. CLASSIFY: Was execution successful? (exit code, output patterns)
  6. Return: {success: bool, output: string, steps: []}

Success Criteria: Exit code 0 OR output matches success pattern
                  (no retry loop — single attempt only)
```

#### 3. **COMPOUND PIPELINE** (for mixed prompts like "pull changes then style logo")
```
Trigger: prompt contains BOTH code-edit verbs AND infrastructure verbs

Flow:
  1. DECOMPOSE: Break into sub-tasks (LLM or regex-based)
  2. SEQUENCE: Order tasks (commands first, then edits? or vice versa?)
  3. Execute each via appropriate sub-pipeline
  4. AGGREGATE: Combine results and metadata
  5. Return: {steps: [cmd_results, edit_results, ...]}

Success Criteria: All sub-tasks successful
```

#### 4. **QUICK CHECK PIPELINE** (for status, ping, health checks)
```
Trigger: prompt is diagnostic (ping, check, status, verify, etc.)

Flow:
  1. PARSE: Extract check type
  2. EXECUTE: Run diagnostic command(s)
  3. ANALYZE: Interpret results (LLM optional)
  4. FORMAT: Return findings
  5. Return: {healthy: bool, details: string}

Success Criteria: Diagnostic complete (not retry-based)
```

---

## Part 3: Implementation Structure

### Core Abstraction: IPipeline Interface

```csharp
/// <summary>
/// Represents a task execution pipeline with a specific orchestration strategy.
/// </summary>
public interface IOrchestrationPipeline
{
    /// <summary>
    /// Executes the pipeline for the given task context.
    /// </summary>
    Task<PipelineResult> ExecuteAsync(
        OrchestrationContext context, 
        CancellationToken ct = default);
}

public class OrchestrationContext
{
    public string Prompt { get; set; }
    public string ProjectRoot { get; set; }
    public bool EmitSse { get; set; }
    public HttpResponse Response { get; set; }
    public List<string> AttachedFiles { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();
}

public class PipelineResult
{
    public bool Success { get; set; }
    public string Summary { get; set; }
    public List<object> Steps { get; set; } = new();
    public List<string> FilesModified { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();
}
```

### Task Router: Classify & Route

```csharp
/// <summary>
/// Analyzes a prompt and routes it to the appropriate pipeline.
/// </summary>
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
    /// Determines the best-fit pipeline for the given prompt.
    /// </summary>
    public (IOrchestrationPipeline pipeline, TaskType type) RoutePrompt(string prompt)
    {
        // Check for special markers first (highest priority)
        if (DetectSpecialMarkers(prompt, out var markers))
        {
            _logger.LogInformation($"Detected special markers: {string.Join(", ", markers)}");
            return (_serviceProvider.GetRequiredService<ICommandExecutionPipeline>(), TaskType.Command);
        }

        // Check for compound tasks (multiple operation types)
        var hasCodeEdits = DetectCodeEditVerbs(prompt);
        var hasCommands = DetectCommandOperations(prompt);
        if (hasCodeEdits && hasCommands)
        {
            _logger.LogInformation("Detected compound task (code edits + commands)");
            return (_serviceProvider.GetRequiredService<ICompoundPipeline>(), TaskType.Compound);
        }

        // Check for pure command/infrastructure operations
        if (hasCommands)
        {
            _logger.LogInformation("Detected command/infrastructure task");
            return (_serviceProvider.GetRequiredService<ICommandExecutionPipeline>(), TaskType.Command);
        }

        // Check for quick diagnostics
        if (DetectDiagnosticOperation(prompt))
        {
            _logger.LogInformation("Detected diagnostic/health-check task");
            return (_serviceProvider.GetRequiredService<IQuickCheckPipeline>(), TaskType.QuickCheck);
        }

        // Default: code edit pipeline
        _logger.LogInformation("Routing to default Code Edit pipeline");
        return (_serviceProvider.GetRequiredService<ICodeEditPipeline>(), TaskType.CodeEdit);
    }

    private bool DetectSpecialMarkers(string prompt, out List<string> markers)
    {
        markers = new();
        foreach (var marker in new[] { "_git", "_show", "_package_install", "_ping", "_create_file" })
        {
            if (prompt.Contains(marker, StringComparison.OrdinalIgnoreCase))
                markers.Add(marker);
        }
        return markers.Count > 0;
    }

    private bool DetectCodeEditVerbs(string prompt)
    {
        var verbs = new[] 
        { 
            "add", "implement", "fix", "update", "change", "modify", "create", "edit", 
            "refactor", "style", "color", "position", "align", "layout", "format", "enhance"
        };
        var lower = prompt.ToLowerInvariant();
        return verbs.Any(v => lower.Contains(v));
    }

    private bool DetectCommandOperations(string prompt)
    {
        var verbs = new[] 
        { 
            "install", "npm install", "dotnet add package", "git pull", "git push", "git commit",
            "build", "run", "start", "stop", "deploy", "push", "pull", "clone", "branch"
        };
        var lower = prompt.ToLowerInvariant();
        return verbs.Any(v => lower.Contains(v));
    }

    private bool DetectDiagnosticOperation(string prompt)
    {
        var verbs = new[] { "ping", "check", "status", "verify", "health", "diagnose" };
        var lower = prompt.ToLowerInvariant();
        return verbs.Any(v => lower.Contains(v));
    }
}

public enum TaskType
{
    CodeEdit,
    Command,
    Compound,
    QuickCheck
}
```

### Command Execution Pipeline: Single-Pass Execution

```csharp
public class CommandExecutionPipeline : IOrchestrationPipeline
{
    private readonly TerminalService _terminal;
    private readonly ILlmClient _llm;
    private readonly ILogger<CommandExecutionPipeline> _logger;

    public CommandExecutionPipeline(
        TerminalService terminal, 
        ILlmClient llm, 
        ILogger<CommandExecutionPipeline> logger)
    {
        _terminal = terminal;
        _llm = llm;
        _logger = logger;
    }

    public async Task<PipelineResult> ExecuteAsync(OrchestrationContext ctx, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting Command Execution pipeline");
        var result = new PipelineResult { Steps = new List<object>() };

        try
        {
            // Step 1: Parse command(s) from prompt (with LLM if needed)
            var commands = await ExtractCommands(ctx.Prompt, ct);
            if (commands.Count == 0)
            {
                result.Success = false;
                result.Summary = "No commands parsed from prompt";
                return result;
            }

            // Step 2: Execute each command sequentially (NO RETRY LOOP)
            var allSucceeded = true;
            foreach (var cmd in commands)
            {
                _logger.LogInformation($"Executing: {cmd}");
                var (output, exitCode) = await _terminal.ExecuteAndCaptureAsync(cmd, ctx.ProjectRoot, ct);
                
                var success = exitCode == 0;
                allSucceeded = allSucceeded && success;

                var step = new Dictionary<string, object?>
                {
                    { "type", "command" },
                    { "command", cmd },
                    { "output", output },
                    { "exitCode", exitCode },
                    { "status", success ? "done" : "error" }
                };
                result.Steps.Add(step);

                // Format output for SSE
                if (ctx.EmitSse)
                    await EmitStepSse(ctx.Response, step, ct);
            }

            result.Success = allSucceeded;
            result.Summary = allSucceeded 
                ? $"Executed {commands.Count} command(s) successfully"
                : $"Some commands failed (see steps)";

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command execution pipeline failed");
            result.Success = false;
            result.Summary = $"Pipeline error: {ex.Message}";
            return result;
        }
    }

    private async Task<List<string>> ExtractCommands(string prompt, CancellationToken ct)
    {
        // TODO: Implement command extraction logic
        // Could use regex patterns for common commands, or LLM if needed
        throw new NotImplementedException();
    }

    private async Task EmitStepSse(HttpResponse response, Dictionary<string, object?> step, CancellationToken ct)
    {
        // TODO: Emit SSE step event
    }
}
```

### Code Edit Pipeline: Optimized for Real Edits

```csharp
public class CodeEditPipeline : IOrchestrationPipeline
{
    private readonly AgentController _agent;  // Reuse existing DISCOVER/PLAN/EDIT logic
    private readonly ILogger<CodeEditPipeline> _logger;

    public CodeEditPipeline(AgentController agent, ILogger<CodeEditPipeline> logger)
    {
        _agent = agent;
        _logger = logger;
    }

    public async Task<PipelineResult> ExecuteAsync(OrchestrationContext ctx, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting Code Edit pipeline");
        
        // Reuse existing phased pipeline but with corrected review logic
        var (steps, summary, complete) = await _agent.RunPhasedPipelineAsync(
            ctx.Prompt, 
            ctx.ProjectRoot, 
            emitSse: ctx.EmitSse, 
            ct: ct);

        return new PipelineResult
        {
            Success = complete,
            Summary = summary,
            Steps = steps,
            FilesModified = ExtractFilesModified(steps)
        };
    }

    private List<string> ExtractFilesModified(List<object> steps)
    {
        // TODO: Extract file paths from edit steps
        return new();
    }
}
```

### Compound Pipeline: Decompose & Sequence

```csharp
public class CompoundPipeline : IOrchestrationPipeline
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
        _logger.LogInformation("Starting Compound pipeline");
        var result = new PipelineResult { Steps = new List<object>() };

        try
        {
            // Step 1: Decompose into sub-tasks
            var subTasks = await DecomposePrompt(ctx.Prompt, ct);
            if (subTasks.Count == 0)
            {
                result.Success = false;
                result.Summary = "Failed to decompose compound task";
                return result;
            }

            // Step 2: Determine execution order (commands first, then edits)
            var ordered = OrderSubTasks(subTasks);

            // Step 3: Execute each sub-task
            var allSucceeded = true;
            foreach (var subTask in ordered)
            {
                var (pipeline, _) = _router.RoutePrompt(subTask.Prompt);
                var subCtx = new OrchestrationContext
                {
                    Prompt = subTask.Prompt,
                    ProjectRoot = ctx.ProjectRoot,
                    EmitSse = ctx.EmitSse,
                    Response = ctx.Response
                };
                
                var subResult = await pipeline.ExecuteAsync(subCtx, ct);
                allSucceeded = allSucceeded && subResult.Success;
                
                result.Steps.AddRange(subResult.Steps);
                result.FilesModified.AddRange(subResult.FilesModified);
            }

            result.Success = allSucceeded;
            result.Summary = $"Completed {subTasks.Count} sub-task(s): {string.Join(", ", subTasks.Select(t => t.Type))}";
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Compound pipeline failed");
            result.Success = false;
            result.Summary = $"Pipeline error: {ex.Message}";
            return result;
        }
    }

    private async Task<List<SubTask>> DecomposePrompt(string prompt, CancellationToken ct)
    {
        // TODO: Use LLM or heuristics to split compound task into components
        // Example: "pull changes and add styling"
        //   → ["pull all changes", "add styling to the logo"]
        throw new NotImplementedException();
    }

    private List<SubTask> OrderSubTasks(List<SubTask> tasks)
    {
        // TODO: Order tasks logically (commands before edits is usually good)
        throw new NotImplementedException();
    }

    private class SubTask
    {
        public string Type { get; set; }  // "code_edit" or "command"
        public string Prompt { get; set; }
    }
}
```

---

## Part 4: Special Marker Handler System

Create dedicated handlers for each special marker, removing scattered logic:

```csharp
public interface ISpecialMarkerHandler
{
    string MarkerName { get; }
    Task<PipelineResult> HandleAsync(string changeDescription, string projectRoot, 
        OrchestrationContext ctx, CancellationToken ct = default);
}

// Example: Git handler
public class GitMarkerHandler : ISpecialMarkerHandler
{
    public string MarkerName => "_git";
    private readonly TerminalService _terminal;

    public GitMarkerHandler(TerminalService terminal) => _terminal = terminal;

    public async Task<PipelineResult> HandleAsync(
        string changeDescription, string projectRoot, OrchestrationContext ctx, CancellationToken ct = default)
    {
        // Parse git command from changeDescription
        var gitCmd = changeDescription.Trim().Trim('`', '"', '\'');
        if (string.IsNullOrWhiteSpace(gitCmd))
            gitCmd = "git pull";  // default

        // Execute once (no retries)
        var (output, exitCode) = await _terminal.ExecuteAndCaptureAsync(gitCmd, projectRoot, ct);
        
        return new PipelineResult
        {
            Success = exitCode == 0,
            Summary = $"Git: {gitCmd}",
            Steps = new List<object>
            {
                new Dictionary<string, object?>
                {
                    { "type", "git" },
                    { "command", gitCmd },
                    { "output", output },
                    { "exitCode", exitCode },
                    { "status", exitCode == 0 ? "done" : "error" }
                }
            }
        };
    }
}

// Registering handlers (in Startup/DI)
services.AddSingleton<ISpecialMarkerHandler, GitMarkerHandler>();
services.AddSingleton<ISpecialMarkerHandler, ShowMarkerHandler>();
services.AddSingleton<ISpecialMarkerHandler, PackageInstallMarkerHandler>();
services.AddSingleton<ISpecialMarkerHandler, PingMarkerHandler>();
services.AddSingleton<ISpecialMarkerHandler, CreateFileMarkerHandler>();
```

---

## Part 5: Integration Checklist

### Phase 1: Foundation (Week 1)
- [ ] Create `IOrchestrationPipeline` interface
- [ ] Implement `TaskOrchestrationRouter`
- [ ] Implement `CommandExecutionPipeline`
- [ ] Create `ISpecialMarkerHandler` interface
- [ ] Implement Git, Package, Ping handlers
- [ ] Wire into DI container

### Phase 2: Refactor Existing (Week 2)
- [ ] Extract code-edit logic into `CodeEditPipeline`
- [ ] Move special marker logic from `RunEditPhase` to handlers
- [ ] Implement `CompoundPipeline`
- [ ] Remove duplicate command execution code

### Phase 3: Testing & Refinement (Week 3)
- [ ] Test each pipeline in isolation
- [ ] Test router classification accuracy
- [ ] Fix git duplication issue (now eliminated by single-pass execution)
- [ ] Verify no unnecessary review loops

### Phase 4: Expansion (Ongoing)
- [ ] Add `QuickCheckPipeline` for diagnostics
- [ ] Create handlers for future operations (config, build, etc.)
- [ ] Implement advanced decomposition in `CompoundPipeline`

---

## Part 6: Migration Strategy

### Old Code
```csharp
// Single endpoint, does everything
[HttpPost("execute-stream")]
public async Task ExecuteStream([FromBody] AgentRequest req)
{
    // Was: always run full phased pipeline
    var (steps, summary, complete) = await RunPhasedPipeline(...);
}
```

### New Code
```csharp
// Single endpoint, smart routing
[HttpPost("execute-stream")]
public async Task ExecuteStream([FromBody] AgentRequest req)
{
    var router = _serviceProvider.GetRequiredService<TaskOrchestrationRouter>();
    var (pipeline, taskType) = router.RoutePrompt(req.Prompt);
    
    var ctx = new OrchestrationContext
    {
        Prompt = req.Prompt,
        ProjectRoot = GetProjectRoot(req.Project),
        EmitSse = true,
        Response = Response,
        AttachedFiles = req.Files
    };
    
    var result = await pipeline.ExecuteAsync(ctx, CancellationToken.None);
    
    // Emit SSE results
    await SendSse(Response, "done", result);
}
```

---

## Part 7: Future-Proofing

### New Operation Types (Easy to Add)
Want to support build verification, config updates, database migrations?

```csharp
// Create a handler interface with same pattern
public interface IBuildVerificationHandler { }
public class DotnetBuildHandler : IBuildVerificationHandler { }

// Add to router
public bool DetectBuildOperation(string prompt) { }

// Route to new pipeline
public IOrchestrationPipeline RouteBuildTask() => 
    _serviceProvider.GetRequiredService<IBuildVerificationPipeline>();
```

### New Command Sources
Want to read tasks from files, APIs, config?

```csharp
// Create a task parser interface
public interface ITaskParser
{
    Task<List<OrchestrationTask>> ParseAsync(object source);
}

public class PromptTaskParser : ITaskParser { }
public class FileTaskParser : ITaskParser { }
public class ApiTaskParser : ITaskParser { }

// Use in ExecuteStream
var parser = _serviceProvider.GetRequiredService<ITaskParser>();
var tasks = await parser.ParseAsync(req.Prompt);
```

### Monitoring & Observability
Add structured logging hooks:

```csharp
public interface IPipelineObserver
{
    Task OnPipelineStartedAsync(string pipelineType, OrchestrationContext ctx);
    Task OnTaskRoutedAsync(string fromPrompt, string toRoute);
    Task OnPipelineCompletedAsync(PipelineResult result);
}
```

---

## Summary: Key Wins

| Issue | Current | New |
|-------|---------|-----|
| Git operations | 7+ duplicate runs | 1 single execution |
| Review loops | Applies to all tasks | Only for code edits |
| Task classification | All tasks → same path | Smart routing by type |
| Command handling | Scattered in EditPhase | Dedicated pipeline |
| Special markers | Buried in EditPhase | Modular handlers |
| Maintenance | Monolithic controller | Pluggable components |
| Future features | Modify core pipeline | Add handler + router logic |

---

## Appendix: File Structure

```
Controllers/
  ├── AgentController.cs           (entry point, delegates to router)
  
Pipelines/
  ├── IOrchestrationPipeline.cs    (interface)
  ├── CodeEditPipeline.cs
  ├── CommandExecutionPipeline.cs
  ├── CompoundPipeline.cs
  └── QuickCheckPipeline.cs

Routing/
  ├── TaskOrchestrationRouter.cs
  └── TaskType.cs (enum)

Handlers/
  ├── ISpecialMarkerHandler.cs
  ├── GitMarkerHandler.cs
  ├── PackageInstallMarkerHandler.cs
  ├── PingMarkerHandler.cs
  ├── ShowMarkerHandler.cs
  └── CreateFileMarkerHandler.cs

Models/
  ├── OrchestrationContext.cs
  ├── PipelineResult.cs
  └── SubTask.cs
```

---

**Document Version**: 1.0  
**Last Updated**: 2026-05-25  
**Status**: Ready for Implementation
