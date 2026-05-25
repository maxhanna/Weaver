# Maestro Agent Architecture Redesign: Executive Summary

## Problem Statement

Your agent orchestration pipeline executes the same git command 7 times when asked to "pull changes":

- Phased pipeline treats ALL tasks the same way
- Review loop designed for code edits gets applied to git operations
- Result: Unnecessary replanning, duplicate executions, wasted time

**Current behavior:** 7 git pulls in 30 seconds  
**Target behavior:** 1 git pull in 3 seconds

---

## Solution: Multi-Pipeline Architecture

Replace the monolithic phased pipeline with specialized task pipelines:

```
User Input
    ↓
[TaskRouter: Classify Task Type]
    ├─ Code Edit → CodeEditPipeline (DISCOVER→PLAN→EDIT→REVIEW)
    ├─ Git/Commands → CommandExecutionPipeline (EXECUTE ONCE, no retry)
    ├─ Mixed Tasks → CompoundPipeline (sequence multiple pipelines)
    └─ Diagnostics → QuickCheckPipeline (health checks, no retry)
    ↓
[Selected Pipeline Executes]
    ↓
[Results Aggregated & Returned]
```

**Key Change:** Commands execute once (not retried), avoiding the review-loop trap.

---

## What You Get

### Before
```
"pull all changes" 
  → DISCOVER (read files)
  → PLAN (ask LLM)
  → EDIT (run git pull) [#1]
  → REVIEW LOOP:
    → RE-PLAN [#2]
    → RE-EDIT (run git pull) [#3]
    → RE-PLAN [#4]
    → RE-EDIT (run git pull) [#5]
    → RE-PLAN [#6]
    → RE-EDIT (run git pull) [#7]
  = 7 executions, 30 seconds
```

### After
```
"pull all changes"
  → [Router: Detects "_git" or "git pull"]
  → [Routes to CommandExecutionPipeline]
  → EXECUTE (run git pull) [#1]
  → RETURN
  = 1 execution, 3 seconds
```

---

## Architecture Components

### 1. TaskOrchestrationRouter
Analyzes prompt, classifies task type, returns appropriate pipeline.

```csharp
var (pipeline, taskType) = router.RoutePrompt("pull all changes");
// Returns: (CommandExecutionPipeline, TaskType.Command)
```

**Detection Logic:**
- Special markers (`_git`, `_show`, etc.) → Command pipeline
- Code verbs (add, fix, style) + command verbs → Compound pipeline  
- Code verbs only → Code edit pipeline
- Diagnostics (ping, check, verify) → Quick check pipeline

### 2. Pipeline Interface
```csharp
public interface IOrchestrationPipeline
{
    Task<PipelineResult> ExecuteAsync(OrchestrationContext ctx, CancellationToken ct);
}
```

All pipelines implement this interface with their own strategies.

### 3. CommandExecutionPipeline
Single-pass execution for git, npm, dotnet operations:
- Parse command(s)
- Execute once
- Return result immediately (no retry)

### 4. CodeEditPipeline
Refactored existing DISCOVER→PLAN→EDIT logic:
- Uses phased pipeline for code changes
- Includes review loop (only for edits)
- Handles `.js`, `.html`, `.css` modifications

### 5. CompoundPipeline
Handles mixed tasks by decomposing and sequencing:
- Splits "pull then style" into ["pull all changes", "add styling"]
- Routes each sub-task to appropriate pipeline
- Executes commands before edits
- Aggregates results

---

## Three Implementation Paths

### 🟢 **Quick Fix** (1-2 hours)
Add task-aware logic to existing `RunPhasedPipeline()`:

```csharp
if (IsCommandOnlyTask(prompt)) {
    // Skip review loop for command tasks
    return (allSteps, summary, hasCommandSuccess);
}
```

**Pros:** Fast, minimal changes  
**Cons:** Doesn't scale, review logic still present but disabled

### 🟡 **Medium Refactor** (1 day)
Extract CommandExecutionPipeline, use router for basic routing:

```csharp
if (IsCommandTask(prompt)) {
    var pipeline = _serviceProvider.GetRequiredService<ICommandExecutionPipeline>();
    return await pipeline.ExecuteAsync(ctx);
}
```

**Pros:** Eliminates git duplicates, cleaner separation  
**Cons:** Review loop logic still exists

### 🔴 **Full Architecture** (1 week)
Implement complete multi-pipeline system:

- Router for all task types
- All 4 pipeline types implemented
- Special marker handlers
- Clean interfaces for future extensibility

**Pros:** Scalable, maintainable, extensible  
**Cons:** Largest effort, requires refactoring existing code

---

## What to Implement First

**Recommended:** Full Architecture (despite 1-week timeline)

**Why?** 
- Small models always choose full pipeline when unclear
- Router removes all ambiguity → better reproducibility
- Future operations (build verification, config, migrations) leverage same architecture
- Phased pipeline refactored cleanly, not just patched

**Phase Breakdown:**
1. **Core Interfaces** (2 hours): Router, IPipeline, DTOs
2. **CommandExecutionPipeline** (2 hours): Git, npm, dotnet handling
3. **DI & Router** (2 hours): Wire up, implement classification logic
4. **CodeEditPipeline** (2 hours): Refactor existing logic
5. **Testing** (4 hours): Verify no regressions, test all paths
6. **CompoundPipeline** (2 hours): Nice-to-have, extends later

---

## Expected Outcomes

### Git Operations
- Executions reduced: 7 → 1
- Time reduced: 30s → 3s
- Duplicate prevention: ✓

### Code Edits
- Behavior: Unchanged (same phased pipeline)
- Review loop: Still works for incomplete edits
- Quality: ✓

### Future Operations
- Build verification: Easily added as new handler
- Config management: New pipeline type if needed
- Database operations: New handler infrastructure ready

---

## Documentation Provided

1. **AGENT_ORCHESTRATION_REDESIGN.md** (30 KB)
   - Full architecture specification
   - Phased pipeline issues
   - Multi-pipeline design
   - File structure, integration checklist

2. **AGENT_IMPLEMENTATION_QUICKSTART.md** (20 KB)
   - Step-by-step implementation guide
   - Code templates for all components
   - DI configuration
   - Testing checklist

3. **GIT_DUPLICATION_ROOT_CAUSE.md** (10 KB)
   - Root cause analysis of git 7x duplication
   - Bug location in existing code
   - Three solution approaches with pros/cons
   - Verification steps

---

## Next Steps

1. **Review** these three documents
2. **Choose** implementation path (recommended: Full Architecture)
3. **Implement** starting with Core Interfaces & CommandExecutionPipeline
4. **Test** with "pull all changes" prompt
5. **Verify** single git execution (not 7x)
6. **Extend** with other pipeline types

---

## Key Principle

> **One Router, Many Pipelines**
> 
> The router is the single decision point that prevents wasted work. Once classified, each task type gets its optimal orchestration strategy.

---

**Timeline to Production:** 1 week for full architecture, 1 day for quick fix  
**Risk Level:** Medium (requires refactoring, but interfaces are clean)  
**Payoff:** High (scales to all future operations, eliminates entire classes of bugs)
