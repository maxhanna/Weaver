# Maestro Agent Architecture: Visual Guide

## Current System (Phased Pipeline - Single Path)

```
┌─────────────────────────────────────────────────────────────┐
│                   User Prompt                               │
│      "pull all changes and show what was pulled"            │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ↓
        ┌────────────────────────────────────┐
        │     PHASE 1: DISCOVER              │
        │  (read files, list dirs, grep)     │
        └────────────────────┬───────────────┘
                             │
                             ↓
        ┌────────────────────────────────────┐
        │     PHASE 2: PLAN                  │
        │  (LLM: which files to change?)     │
        │  Output: [_git, _show]             │
        └────────────────────┬───────────────┘
                             │
                             ↓
        ┌────────────────────────────────────┐
        │     PHASE 3: EDIT                  │
        │  (LLM: generate patches)           │
        │  → Executes: git pull [#1]         │
        └────────────────────┬───────────────┘
                             │
                             ↓
        ┌────────────────────────────────────┐
        │  PHASE 4: REVIEW LOOP (3x)         │
        │  HasSuccessfulEdits() → false      │
        │  → RE-PLAN [#2-3]                  │
        │  → git pull [#4-5]                 │
        │  → RE-PLAN [#6-7]                  │
        │  → git pull [#8-9]                 │
        │  → RE-PLAN [#10-11]                │
        │  → git pull [#12-13]               │
        └────────────────────┬───────────────┘
                             │
                             ↓
                    ┌────────────────┐
                    │  RESULT        │
                    │  • 7 git pulls │
                    │  • 30 seconds  │
                    └────────────────┘

⚠️  Problem: Git operation treated as "no edits" → review loop triggered
```

---

## New System (Multi-Pipeline - Smart Routing)

```
┌─────────────────────────────────────────────────────────────┐
│                   User Prompt                               │
│      "pull all changes and show what was pulled"            │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ↓
        ┌─────────────────────────────────────────────────┐
        │    TASK ORCHESTRATION ROUTER                    │
        │  ✓ Detects "_git" marker                        │
        │  ✓ Detects "git pull" command                   │
        │  ✓ Routes to CommandExecutionPipeline           │
        └────────────┬───────────────────────────────────┘
                     │
                     ↓
        ┌───────────────────────────────────────────────────┐
        │  COMMAND EXECUTION PIPELINE                       │
        │  └─ Single-pass, no retry loop                    │
        │     ├─ Parse: ["git pull"]                        │
        │     ├─ Execute: git pull [#1]                     │
        │     ├─ Check: exitCode == 0 ✓                     │
        │     └─ Return immediately                         │
        └────────────┬───────────────────────────────────────┘
                     │
                     ↓
                    ┌────────────────┐
                    │  RESULT        │
                    │  • 1 git pull  │
                    │  • 3 seconds   │
                    └────────────────┘

✓ Benefit: Git detected early → direct execution → no review loop
```

---

## Router Decision Tree

```
                          User Input
                              │
                              ↓
                    ┌─────────────────────┐
                    │  Check for Special  │
                    │  Markers (_git, etc)│
                    └────────┬─────────┬──┘
                     YES     │         │ NO
                      ┌──────┘         └──────┐
                      ↓                       ↓
        ┌──────────────────────┐  ┌──────────────────────────┐
        │  COMMAND PIPELINE    │  │  Check for Mixed Task    │
        │  • Git pull          │  │  (edits + commands)      │
        │  • npm install       │  └────────┬────────┬────────┘
        │  • dotnet build      │   YES     │        │ NO
        │                      │    ┌──────┘        └──────┐
        │  Characteristics:    │    ↓                      ↓
        │  • Execute once      │  ┌────────────────┐  ┌──────────────┐
        │  • No retry          │  │COMPOUND        │  │ Check for    │
        │  • Fast return       │  │PIPELINE        │  │ Code Edit    │
        └──────────────────────┘  │ • Decompose    │  │ Verbs        │
                                  │ • Sequence     │  └────┬──────┬──┘
                                  │ • Exec each    │   YES │      │ NO
                                  │   pipeline     │    ┌──┘      └──┐
                                  └────────────────┘    ↓             ↓
                                                   ┌─────────┐    ┌──────────┐
                                                   │CODE EDIT│    │QUICK     │
                                                   │PIPELINE │    │CHECK     │
                                                   │         │    │PIPELINE  │
                                                   │ • DIS   │    │ • Ping   │
                                                   │ • PLAN  │    │ • Health │
                                                   │ • EDIT  │    │ • Status │
                                                   │ • REV   │    └──────────┘
                                                   └─────────┘
```

---

## Execution Flows

### Flow 1: Pure Code Edit Task
```
Input: "add styling to the header and fix button colors"
          │
          ↓
      [Router]
      ✓ Code-edit verbs: add, fix
      ✓ No commands detected
      ↓ Routes to CodeEditPipeline
          │
          ↓
    ┌─────────────────┐
    │ DISCOVER        │ → Find .css/.html/.js files
    └────────┬────────┘
             ↓
    ┌─────────────────┐
    │ PLAN            │ → LLM decides which files to modify
    └────────┬────────┘
             ↓
    ┌─────────────────┐
    │ EDIT            │ → LLM generates patches
    └────────┬────────┘
             ↓
    ┌─────────────────┐
    │ REVIEW          │ → Check if done; retry if not
    └────────┬────────┘
             ↓
          RESULT
          Files modified
```

### Flow 2: Pure Command Task
```
Input: "pull latest changes"
          │
          ↓
      [Router]
      ✓ Contains "pull"
      ✓ Contains "_git" marker
      ✓ No code-edit verbs
      ↓ Routes to CommandExecutionPipeline
          │
          ↓
    ┌─────────────────┐
    │ PARSE           │ → Extract "git pull"
    └────────┬────────┘
             ↓
    ┌─────────────────┐
    │ EXECUTE         │ → Run git pull [#1 ONLY]
    └────────┬────────┘
             ↓
    ┌─────────────────┐
    │ CHECK           │ → exitCode == 0?
    └────────┬────────┘
             ↓
    ┌─────────────────┐
    │ RETURN          │ → No retry, done
    └────────┬────────┘
             ↓
          RESULT
          Git pull output
```

### Flow 3: Compound Task
```
Input: "pull latest code then add dark mode styling"
          │
          ↓
      [Router]
      ✓ Contains "pull" (command verb)
      ✓ Contains "add" (edit verb)
      ↓ Routes to CompoundPipeline
          │
          ↓
    ┌─────────────────────────┐
    │ DECOMPOSE               │
    │ • "pull latest code"    │
    │ • "add dark mode"       │
    └────────┬────────────────┘
             ↓
    ┌─────────────────────────┐
    │ ORDER                   │
    │ 1. Commands first       │
    │ 2. Edits second         │
    └────────┬────────────────┘
             ↓
    ┌─────────────────────────────────────────┐
    │ EXECUTE SUB-TASK 1: "pull latest code"  │
    │   ├─ Route: CommandExecutionPipeline    │
    │   ├─ Execute: git pull [#1]             │
    │   └─ Return: success ✓                  │
    └────────┬────────────────────────────────┘
             ↓
    ┌─────────────────────────────────────────┐
    │ EXECUTE SUB-TASK 2: "add dark mode"     │
    │   ├─ Route: CodeEditPipeline            │
    │   ├─ Discover → Plan → Edit → Review    │
    │   └─ Return: files modified ✓           │
    └────────┬────────────────────────────────┘
             ↓
    ┌─────────────────────────┐
    │ AGGREGATE RESULTS       │
    │ • 1 git pull executed   │
    │ • 2 files modified      │
    └────────┬────────────────┘
             ↓
          RESULT
          Combined execution log
```

### Flow 4: Diagnostic Task
```
Input: "check if the API server is running"
          │
          ↓
      [Router]
      ✓ Contains "check" (diagnostic verb)
      ✓ No edit or command verbs
      ↓ Routes to QuickCheckPipeline
          │
          ↓
    ┌─────────────────┐
    │ PARSE           │ → Extract "ping API"
    └────────┬────────┘
             ↓
    ┌─────────────────┐
    │ EXECUTE         │ → Run health check
    └────────┬────────┘
             ↓
    ┌─────────────────┐
    │ ANALYZE         │ → Interpret results
    └────────┬────────┘
             ↓
    ┌─────────────────┐
    │ RETURN          │ → Health status
    └────────┬────────┘
             ↓
          RESULT
          Server: Running ✓
```

---

## Component Interaction Diagram

```
                         ┌──────────────────────┐
                         │   AgentController    │
                         │  (Entry Point)       │
                         └──────────┬───────────┘
                                    │
                                    ↓
                    ┌───────────────────────────────┐
                    │  TaskOrchestrationRouter      │
                    │  • DetectSpecialMarkers()     │
                    │  • DetectCodeEditVerbs()      │
                    │  • DetectCommandOperations()  │
                    │  • RoutePrompt()              │
                    └───────────────┬───────────────┘
                                    │
                 ┌──────────────────┼──────────────────┬─────────────────┐
                 ↓                  ↓                  ↓                 ↓
          ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐
          │CodeEdit      │  │Command       │  │Compound      │  │QuickCheck    │
          │Pipeline      │  │Execution     │  │Pipeline      │  │Pipeline      │
          │              │  │Pipeline      │  │              │  │              │
          │• Discover    │  │• Parse       │  │• Decompose   │  │• Analyze     │
          │• Plan        │  │• Execute     │  │• Order       │  │• Execute     │
          │• Edit        │  │• Capture     │  │• Route each  │  │• Format      │
          │• Review      │  │• Return      │  │• Aggregate   │  │• Return      │
          └──────┬───────┘  └──────┬───────┘  └──────┬───────┘  └──────┬───────┘
                 │                 │                  │                 │
                 └─────────────┬────┴──────┬──────────┴─────────────────┘
                               ↓          ↓
                        ┌──────────────────────────┐
                        │ Services Used:           │
                        │ • TerminalService        │
                        │ • FileHintsManager       │
                        │ • ILlmClient             │
                        │ • ConfigFileService      │
                        └──────────────────────────┘
```

---

## State Transitions: Task Classification

```
┌─────────────────────────────────────────────────────────────────┐
│                                                                 │
│                    TASK CLASSIFICATION STATE                    │
│                                                                 │
│   ┌────────────────┐              ┌────────────────┐            │
│   │ SPECIAL MARKER │ ─YES─────→   │ COMMAND        │            │
│   │  _git / _show? │              │ PIPELINE       │            │
│   └────────────────┘              └────────────────┘            │
│           │                                                     │
│          NO                                                     │
│           │                                                     │
│           ↓                                                     │
│   ┌────────────────┐              ┌────────────────┐            │
│   │ CODE EDIT VERB │ ─YES─┬──→    │ COMMAND VERB?  │            │
│   │  add/fix/etc?  │      │       └────────────────┘            │
│   └────────────────┘      │              │                      │
│           │               │            YES                      │
│          NO              YES            │                       │
│           │               │             ↓                       │
│           │               │      ┌──────────────┐               │
│           │               │      │ COMPOUND     │               │
│           │               └─────→│ PIPELINE     │               │
│           │                      └──────────────┘               │
│           ↓                                                     │
│   ┌────────────────┐              ┌────────────────┐            │
│   │ COMMAND VERB   │ ─YES─────→   │ COMMAND        │            │
│   │ pull/push/etc? │              │ PIPELINE       │            │
│   └────────────────┘              └────────────────┘            │
│           │                                                     │
│          NO                                                     │
│           │                                                     │
│           ↓                                                     │
│   ┌────────────────┐              ┌────────────────┐            │
│   │ DIAGNOSTIC     │ ─YES─────→   │ QUICK CHECK    │            │
│   │ ping/check?    │              │ PIPELINE       │            │
│   └────────────────┘              └────────────────┘            │
│           │                                                     │
│          NO                                                     │
│           │                                                     │
│           ↓                                                     │
│   ┌────────────────┐              ┌────────────────┐            │
│   │ DEFAULT        │ ─────────→   │ CODE EDIT      │            │
│   │ FALLBACK       │              │ PIPELINE       │            │
│   └────────────────┘              └────────────────┘            │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

---

## Execution Timeline Comparison

### Before (Current System)
```
Time:     0s    5s    10s   15s   20s   25s   30s
          |-----|-----|-----|-----|-----|-----|
Phase 1   |████|
Phase 2   |     ████|
Phase 3   |         ██| git#1
Review 1  |           |████|
Phase 2'  |               ████|
Phase 3'  |                   ██| git#2
Review 2  |                     |████|
Phase 2'' |                         ████|
Phase 3'' |                             ██| git#3
...continues...                           Done at 30s

⚠️  7 git executions + 4 planning cycles = 30 seconds
```

### After (New System)
```
Time:     0s    1s    2s    3s
          |-----|-----|-----|
Router    |██|
Cmd Exec  |  ██| git#1
Return    |    ██|
                  Done at 3s

✓ 1 git execution + 0 planning = 3 seconds
```

---

## Handler System: Extensibility Example

```
                  ┌──────────────────────────────┐
                  │ ISpecialMarkerHandler         │
                  │ Interface                    │
                  └───────────────┬───────────────┘
                                  │
        ┌─────────────────────────┼─────────────────────────┐
        ↓                         ↓                         ↓
  ┌──────────────┐         ┌──────────────┐         ┌──────────────┐
  │GitHandler    │         │PackageHandler│         │PingHandler   │
  │ _git marker  │         │ _package     │         │ _ping        │
  │              │         │              │         │              │
  │ • git pull   │         │ • npm install│         │ • TCP test   │
  │ • git push   │         │ • dotnet add │         │ • HTTP check │
  │ • git commit │         │ • pip install│         │ • Diagnostics│
  └──────────────┘         └──────────────┘         └──────────────┘

        Future handlers (same pattern):
  ┌──────────────┐         ┌──────────────┐         ┌──────────────┐
  │BuildHandler  │         │ConfigHandler │         │DatabaseHandler
  │ _build       │         │ _config      │         │ _database    │
  │              │         │              │         │              │
  │ • dotnet     │         │ • read config│         │ • migrate    │
  │   build      │         │ • update cfg │         │ • seed       │
  │ • npm run    │         │ • validate   │         │ • backup     │
  └──────────────┘         └──────────────┘         └──────────────┘
```

All handlers implement same interface → Router can register/invoke any handler  
New handler = 1 class implementing ISpecialMarkerHandler, registered in DI

---

**This visual guide complements the three detailed documents. Use alongside them for implementation!**
