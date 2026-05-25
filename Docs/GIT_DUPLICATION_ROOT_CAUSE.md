# Git Duplication Issue: Root Cause & Solution

## The Problem: 7 Git Commands in One Execution

From your logs (9:00:35 AM - 9:01:02 AM):
```
9:00:35 — Git pull: git pull
9:00:37 — ✓ command finished
9:00:43 — Git pull: git pull (DUPLICATE #2)
9:00:46 — ✓ command finished
9:00:52 — Git pull: git pull (DUPLICATE #3) 
9:00:57 — ✓ command finished
9:01:02 — Git pull: git pull (DUPLICATE #4)
```

**Plus 3 more planning attempts = 7 total executions of the same command.**

---

## Root Cause Analysis

### The Current Flow (Phased Pipeline Bug)

```
INPUT: "pull all changes and show what was pulled"
    ↓
Phase 1: DISCOVER (reads files, greps for keywords)
    ↓
Phase 2: PLAN (LLM decides which files to edit)
    Output: [{ "file": "_git", "change": "pull all changes" }]
    ↓
Phase 3: EDIT (LLM generates patches)
    → Detects "_git" marker → Executes: git pull [EXECUTION #1]
    → Execution succeeds: output "Already up to date"
    → Result stored in allSteps
    ↓
Phase 4: VERIFY LOOP (Iteration 1/3)
    → HasSuccessfulEdits(allSteps) → FALSE ❌
    Why? Because HasSuccessfulEdits() only counts "edit" and "rename" step types
    Git execution returns type "command", not "edit" → Treated as FAILURE
    ↓
    → Logs: "Review attempt 1: no successful edits"
    → REPLANS with same prompt [EXECUTION #2 - Duplicate planning]
    → REEDITS and calls git pull [EXECUTION #3 - Duplicate execution]
    ↓
REPEAT Review Iterations 2 & 3
    → Same logic → 4 more duplicate executions
```

### The Bug in Code

From `AgentController.cs` line ~1370:

```csharp
private static bool HasSuccessfulEdits(IEnumerable<object> steps) =>
    steps.OfType<Dictionary<string, object?>>().Any(s =>
        s.TryGetValue("type", out var t) &&
        (string.Equals(t?.ToString(), "edit", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(t?.ToString(), "rename", StringComparison.OrdinalIgnoreCase)) &&  // ← ONLY checks "edit" and "rename"
        s.TryGetValue("status", out var st) && st?.ToString() == "done");
```

**This function returns `false` when the only successful steps are "command" type (git pull).**

Therefore:
- Git pull executes successfully ✓
- But `HasSuccessfulEdits()` returns `false`
- System enters review loop ❌
- Review loop retries planning + execution ❌
- Result: Duplicate execution

---

## Solution 1: Quick Fix (Patch Current System)

### Update HasSuccessfulEdits() Logic

```csharp
private static bool HasSuccessfulEdits(IEnumerable<object> steps) =>
    steps.OfType<Dictionary<string, object?>>().Any(s =>
        s.TryGetValue("type", out var t) &&
        (string.Equals(t?.ToString(), "edit", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(t?.ToString(), "rename", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(t?.ToString(), "command", StringComparison.OrdinalIgnoreCase)) &&  // ← ADD THIS
        s.TryGetValue("status", out var st) && st?.ToString() == "done");
```

**Problem:** This partially fixes it, but now command operations trigger the review loop unnecessarily, which is still wasteful.

---

## Solution 2: Smart Task Classification (Recommended)

The new multi-pipeline architecture recognizes that git operations and code edits are **fundamentally different task types** that need different orchestration logic:

### For Git/Command Tasks: Single-Pass Execution

```csharp
// CommandExecutionPipeline.cs
public async Task<PipelineResult> ExecuteAsync(OrchestrationContext ctx, CancellationToken ct = default)
{
    // Step 1: Parse commands
    var commands = ExtractCommands(ctx.Prompt);  // ["git pull"]
    
    // Step 2: Execute ONCE (no review loop)
    foreach (var cmd in commands)
    {
        await _terminal.SendCommandAsync(cmd);
        var output = _terminal.ReadOutput();
        
        var succeeded = exitCode == 0;  // Simple success criteria
        result.Steps.Add(new { type = "command", command = cmd, status = succeeded ? "done" : "error" });
    }
    
    // Step 3: Return immediately (NO RETRY)
    result.Success = allSucceeded;
    return result;  // ← Exit here, no review loop
}
```

**Flow with new architecture:**
```
INPUT: "pull all changes and show what was pulled"
    ↓
TaskOrchestrationRouter.RoutePrompt()
    → Detects "_git" marker
    → Routes to CommandExecutionPipeline
    ↓
CommandExecutionPipeline.ExecuteAsync()
    → Parse: ["git pull"]
    → Execute: git pull [EXECUTION #1 - ONLY ONCE]
    → Check: exitCode == 0 → success
    → Return PipelineResult { Success: true, Steps: [...] }
    ↓
ExecuteStream() receives result
    → Emits done event with results
    → NO review loop, NO replanning, NO retry
    ↓
COMPLETE ✓
```

**Comparison:**

| Aspect | Current | New |
|--------|---------|-----|
| Git executions | 7x | 1x |
| Planning phases | 4x | 0x |
| Review loops | 3x | 0x |
| Total time | ~30s | ~3s |
| Code path | 4 phases, review logic | Direct to command handler |

---

## Implementation Steps

### Step 1: Add Task-Aware Success Check

Modify `RunPhasedPipeline()` to distinguish task types:

```csharp
private async Task<(List<object> allSteps, string summary, bool complete)> RunPhasedPipeline(
    string prompt, string projectRoot, bool emitSse, CancellationToken ct = default)
{
    // ... existing DISCOVER, PLAN, EDIT phases ...

    // NEW: Task-aware verification logic
    if (IsCommandOnlyTask(prompt))
    {
        // Command tasks: don't enter review loop
        var hasCommandSuccess = allSteps.Any(s =>
            s is Dictionary<string, object?> d &&
            d.TryGetValue("type", out var t) && t?.ToString() == "command" &&
            d.TryGetValue("status", out var st) && st?.ToString() == "done");
        
        return (allSteps, "Command executed", hasCommandSuccess);
    }
    
    // Code edit tasks: keep existing review loop logic
    // ... existing review loop code ...
}

private bool IsCommandOnlyTask(string prompt)
{
    var lower = prompt.ToLowerInvariant();
    var commandPatterns = new[] { "_git", "_show", "_package", "git pull", "npm install" };
    return commandPatterns.Any(p => lower.Contains(p));
}
```

### Step 2: Implement Full Router (Recommended Path Forward)

Instead of patching, implement the full router that eliminates the review loop for command tasks:

```csharp
// In ExecuteStream endpoint
if (req.Prompt.Contains("_git", StringComparison.OrdinalIgnoreCase) ||
    req.Prompt.Contains("git pull", StringComparison.OrdinalIgnoreCase))
{
    // Use command pipeline (single execution, no review)
    var pipeline = _serviceProvider.GetRequiredService<ICommandExecutionPipeline>();
    var result = await pipeline.ExecuteAsync(ctx, ct);
    // Done — no review loop
}
else
{
    // Use code edit pipeline (with review loop if needed)
    var pipeline = _serviceProvider.GetRequiredService<ICodeEditPipeline>();
    var result = await pipeline.ExecuteAsync(ctx, ct);
}
```

---

## Verification

After implementing the fix, verify with this test case:

### Test Input
```json
{
  "prompt": "pull all changes and show what was pulled",
  "project": ""
}
```

### Expected Output (New Architecture)
```json
{
  "summary": "Executed 1 command(s) successfully",
  "success": true,
  "steps": [
    {
      "index": 0,
      "type": "command",
      "command": "git pull",
      "output": "Already up to date.",
      "status": "done"
    }
  ]
}
```

### Key Success Metrics
- ✓ Only 1 git execution (not 7)
- ✓ Total execution time < 5 seconds
- ✓ No review loop (no replanning)
- ✓ Clean output (no garbled text)
- ✓ Correct success status

---

## Migration Path

### Short Term (1-2 hours)
Implement the task-aware check in `RunPhasedPipeline()`:
```csharp
if (IsCommandOnlyTask(prompt)) {
    return (allSteps, summary, hasCommandSuccess);  // Skip review loop
}
```

### Medium Term (1-2 days)
Implement `CommandExecutionPipeline` interface and route command prompts to it.

### Long Term (1 week)
Full multi-pipeline architecture with router, all pipeline types, and handlers.

---

## Key Insights

1. **Task Type Matters**: Code edits need retry/review loops; commands don't.
2. **Router Solves Duplicates**: One router decision prevents wasted execution.
3. **Single Responsibility**: Each pipeline handles only its task type well.
4. **Future Extensibility**: New task types easily added via new pipelines.

---

**Next Action**: Implement Solution 2 (full architecture) for long-term maintainability, or Solution 1 (quick patch) for immediate relief.
