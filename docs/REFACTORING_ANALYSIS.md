# Weaver Refactoring Analysis: God Classes & Code Smells

*Analysed: 2026-08-25 — branch `docs/refactoring-analysis` off `main` (`0ae23d6`)*

This report identifies the codebase's worst maintainability hotspots using **measured**
metrics (line counts, dependency counts, field counts, static-state counts), not opinion.
It mirrors the format of `docs/CODING_PIPELINE_REPORT.md` and proposes a sequenced,
low-risk decomposition roadmap. No code is changed here — this is the analysis a
refactor will be planned against.

---

## 1. Methodology

Every figure below was gathered directly from the source on `main`:

- Aggregate line counts across `AgentController` partial files (`Get-Content | Measure-Object -Line`).
- Method-signature counts per partial (`regex` against `^\s*(public|private|…)\s*…\w+\s*\(`).
- Injected-dependency and mutable-field counts from the constructor/field declarations in `AgentController.cs`.
- Static-state counts (`private static`/`ConcurrentDictionary` fields) across all partials.
- Front-end file sizes (`wwwroot/*.js`) and largest-service public-method counts.

God-class thresholds cited are the commonly-cited heuristics (e.g. LCOM, number of
collaborators > 7, methods > ~20–40, file > ~1000 lines). Weaver exceeds all of these
on its primary controller by an order of magnitude.

---

## 2. Findings (worst-first)

### 🟥 God class #1 — `AgentController` (the dominant problem)

The controller is sharded across **20 `partial` files** that share one class, one
`private` scope, and one set of instance fields. Sharding is not decomposition.

| Metric | Measured | Typical god-class threshold |
| --- | --- | --- |
| Aggregate lines (20 partials) | **29,305** | > ~1,000 |
| Partial files | 20 | (cosmetic — same class) |
| Injected + field collaborators | **25** | > ~7 |
| Mutable instance fields | **46** | > ~10 |
| Static mutable fields | **13** | > 0 in a per-request controller |
| Largest partial | `AgentController.Planning.cs` — 5,333 lines | single file > ~1,000 |

Per-partial line/method distribution (top offenders):

```text
5333  AgentController.Planning.cs        (~1,071 sig matches)
2554  AgentController.EditResolution.cs  (~553)
2441  AgentController.Pipeline.cs        (~448)
2386  AgentController.ApplyEdit.cs        (~485)
2054  AgentController.Discovery.cs       (~384)
2002  AgentController.Execution.cs       (~391)
1928  AgentController.Formatting.cs      (~453)
1806  AgentController.cs                 (~357)
1736  Services/AgentEditHeuristics.cs    (23 public methods)
1475  AgentController.StepExploration.cs (~287)
1427  AgentController.Prompts.cs         (~122)
1252  AgentController.Steps.cs           (~250)
1020  AgentController.Llm.cs             (~192)
```

**The specific code smells, with evidence:**

1. **Shredded god class, not decomposition.** The 20 `partial` files are cosmetic:
   `AgentController.Planning.cs` reads `private` fields declared in
   `AgentController.Pipeline.cs`. A real decomposition would be separate classes with
   explicit interfaces; partials here only silence the compiler's file-size warning.

2. **Violates Single Responsibility (~22 responsibilities in one type).** The class
   performs: HTTP/SSE hosting, LLM orchestration, prompt construction, plan parsing,
   edit resolution, AST/text editing, formatting, terminal commands, browser testing,
   web scraping, runtime probing, OS-output verification, repair loop, token accounting,
   complexity scoring, context-review gating, steering, changelog, and benchmark wiring.

3. **Excessive collaborators (25).** `IHttpClientFactory`, `IConfiguration`,
   `IWebHostEnvironment`, `TerminalService`, `FileHintsManager`, `ConfigFileService`,
   `EmailService`, `BoardDataService`, `EditKnowledgeService`, `PushNotificationService`,
   `DatabaseService`, `AiServerDiscoveryService`, plus `_scraperService`,
   `_runtimeProbe`, `_cfgCache`, etc. Tests must build a 12-field harness via
   `RuntimeHelpers.GetUninitializedObject` — observed firsthand while wiring
   `StrictVerifierTests`.

4. **Mutable static state in a controller (13 static fields).** `_pendingQuestions`,
   `_pendingContextReviews`, `_cancelledSteps`, `_liveSteer`, `_stepThinkingStore`,
   `_complexityScores`, `_atomicStepEstimates`, `_infiniteTimeout`,
   `_nextConnectivityCheck`. Static/`ConcurrentDictionary` state in an ASP.NET controller
   is a cross-request correctness hazard and a testability hazard (every test resets
   statics via reflection — e.g. `SetStaticField("_nextConnectivityCheck", …)`).

5. **Run-scoped state living on a singleton-shaped controller.** `_gracefulStop`,
   `_requirementChecklist`, `_stepLlmPromptTokens`, `_runLlmPromptTokens`,
   `_skeletonContextChars`, `_taskPromptContextChars` are per-run state sitting as
   instance fields. The code comments "reset at run start in StepResolutionPipeline" —
   that reset is the smell: the state should be a `RunContext` object passed by
   reference, not fields wiped each run.

**Secondary smells evidenced by the god class:**

- **Reflection-based test coupling.** 7 test files reach into `AgentController` via
  `GetMethod("Orchestrate"/"PostExecuteVerify")`. Adding one parameter required updating
  all 7 (a `TargetParameterCountException` regression hit during the StrictVerifier work).
  This coupling exists *because* the god class can't be constructed normally.
- **Controller performs I/O directly.** Reads files, runs processes, launches browsers,
  scrapes the web — inline. A controller should orchestrate, not perform.
- **Hand-assembled config save drops fields.** While wiring StrictVerifier UI I found
  `prByDefault` was never explicitly written in `saveSettings` — a toggle persisted only
  incidentally. Smell: the settings save path assembles a giant cfg object by hand and
  forgets fields.

### 🟧 God class #2 — `wwwroot/meeting.js` (7,756 lines)

The single largest file in the repo. For contrast `agent.js` (next largest) is 3,165.
A 7.7k-line vanilla-JS file bundling the entire meeting/ticker/spider feature surface
with no module boundary is a god module by size alone. Front-end, no compile risk, but
highest DX-pain-per-line (no navigability, no encapsulation). Candidate split:
`meeting-ui.js`, `meeting-ticker.js`, `meeting-spider.js`, `meeting-state.js`.

### 🟧 God class #3 — `Services/AgentEditHeuristics.cs` (1,736 lines, 23 public methods)

Below the controller but over threshold. A "heuristics" bag with 23 public entry points
strongly suggests multiple heuristic families (rename, CSS, template-binding,
hallucinated-property, brace-matching…) lumped into one service. Likely cohesive *per
family* but not cohesive *as a whole*. Candidate: one class per family behind a common
`IEditHeuristic` interface.

---

## 3. Partial classes vs composition: an empirical comparison

The 20 `AgentController.*.cs` files use `partial` to split one class across files. This
section answers the direct question: **is that decomposition, or just file-shredding?**
The answer matters because it determines whether the roadmap (Section 4) should keep
the partials or replace them with composed services.

### 3.1 What `partial` actually guarantees

`partial` is a *compiler* feature, not an *architecture* feature. All `partial`
definitions of the same type merge into **one CLR type** before code generation. That
means:

- Every `private` member declared in any partial is visible to **all** other partials
  with **no declaration, no interface, and no import**. The compiler does not enforce a
  boundary.
- The merged type has **one set of fields**, **one constructor**, and **one `this`**.
  A field declared in `AgentController.cs` is mutated by code in `AgentController.Planning.cs`
  as if it were local.
- There is **no way to test, mock, or substitute** one partial in isolation — you get
  all of them or none. This is why the tests use `RuntimeHelpers.GetUninitializedObject`
  and reflect into privates.

### 3.2 The measured coupling

I traced every private member (method or field) declared in each partial and counted
how many **other** partials reference it. This cross-reference count is the precise
metric that distinguishes "decomposition" (low coupling, clear seams) from
"shredding" (high coupling, no seam).

```text
Private members declared across all partials:           371
Private members referenced from a DIFFERENT partial:   192
Cross-partial coupling rate:                            51.8%
```

**Over half of all private members are used outside the partial that declares them.**
That is the opposite of decomposition — it is a single class whose source happens to
live in 20 files.

Per-partial export/import (the hub-and-spoke pattern):

| Partial | Exports (its privates used elsewhere) | Imports (uses others' privates) |
| --- | ---: | ---: |
| cs (the core) | **42** | 10 |
| Formatting | 23 | 8 |
| Prompts | 19 | 5 |
| Steps | 15 | 18 |
| Planning | 13 | **47** |
| Terminal | 10 | 8 |
| Llm | 8 | 10 |
| Pipeline | 8 | **49** |
| Execution | 7 | **35** |
| Repair | 7 | 13 |
| Constants | 7 | 0 |
| StepExploration | 6 | 8 |
| EditResolution | 6 | 8 |
| BrowserTest | 5 | 5 |
| Discovery | 5 | **45** |
| EditPhases | 4 | 6 |
| Complexity | 3 | 11 |
| EditPipeline | 3 | **21** |
| ApplyEdit | 1 | **28** |
| CommandPipeline | 0 | 10 |

The top cross-referenced members (what composition would force through an interface):

| Member | Declared in | Referenced from N other partials |
| --- | --- | ---: |
| `EmitLog` | cs | **17** |
| `SendSse` | cs | **16** |
| `_infiniteTimeout` | cs | **16** |
| `LoadConfigAsync` | cs | 10 |
| `PersistBoardDataPlanStepAsync` | Formatting | 7 |
| `ExecutePlan` | Execution | 6 |
| `TruncateForLlm` | Formatting | 5 |
| `_terminal` | cs | 5 |
| `NormalizeChangeForDedup` | Formatting | 5 |
| `_editKnowledge` | cs | 5 |

### 3.3 What the numbers say

Two structural facts fall out of the measurement:

1. **There is a clear hub (the `cs` core), and clear leaves that only consume.**
   `cs` exports 42 privates the rest depend on (`EmitLog`, `SendSse`, `LoadConfigAsync`,
   the `_terminal`/`_editKnowledge`/`_boardData` collaborators). `Constants` exports 7
   and imports 0 — a pure leaf. `CommandPipeline` exports 0 and imports 10 — a pure
   consumer. **This is already a dependency graph in disguise**, but with no edges
   declared: every partial silently reaches across the whole merged type.

2. **The "pipeline" partials are sinks, not modules.** `Pipeline` imports 49, `Planning`
   imports 47, `Discovery` imports 45, `Execution` imports 35. These partials are the
   *orchestration spine* of the agent, and they touch nearly every private in the class.
   Under partials, that coupling is invisible; under composition, each such import
   becomes an explicit constructor-injected dependency — making the spine's real
   collaborator count (and thus its true complexity) legible.

### 3.4 Side-by-side: partials vs composition for this codebase

| Property | `partial` (current) | Composition (proposed) |
| --- | --- | --- |
| Boundary enforcement | None — shared `private` scope | Compiler-enforced `public`/`internal` surface |
| Coupling visibility | Hidden (51.8% cross-ref, silent) | Explicit (constructor params, interfaces) |
| Unit testability | Must instantiate the whole 29k-line class (reflection) | Instantiate one service with fakes |
| Mockable seams | None (tests reflect into privates) | One interface per service |
| Adding a parameter | Breaks every reflection caller (7 test files) | Breaks only direct callers |
| Independent evolution | Any partial can mutate any field | A service owns its state; others go through API |
| Static state hazard | 13 statics shared across the merged type | Statics scoped per service or eliminated |
| Compile-time size signal | Lost (shredded across files) | Preserved per class |
| Refactor risk to introduce | None (already here) | Higher (but contained per-extract) |

### 3.5 Why composition wins here (the deciding factor)

The partial-class approach has **one legitimate use case**: generated + hand-written
code merging into one type (e.g. `Designer.cs` + `Form.cs` in WinForms, EF model
snapshots). Weaver's usage is the *opposite*: 20 hand-written files that all want to
share state. That is exactly the case the C# design guidelines say to avoid, because
"partial types [used to split implementation] allow all parts to access all private
members of all other parts," defeating encapsulation.

The empirical clincher: **51.8% cross-partial coupling + 13 static fields + 25
collaborators** means the partials give the *appearance* of decomposition without any
of its benefits — no enforced boundaries, no isolated tests, no mockable seams, no
compile-time size signal. The 7 reflection-test callers and the `TargetParameterCountException`
regression I hit during the StrictVerifier work are direct downstream symptoms.

### 3.6 The composition target shape (concrete)

The export/import table already reveals the natural service boundaries — the hub and
leaves are *already* separated in all but the `partial` keyword:

- **Infrastructure services (extract from `cs`):** `EmitLog`/`SendSse` →
  `IRunEventSink`; `LoadConfigAsync` + `_cfgCache` → `ConfigProvider`;
  `_infiniteTimeout` → part of `LlmClient`; the 13 statics → a scoped `RunRegistry`.
  This alone removes the 42-export hub.
- **Leaf services (pure extract, low risk):** `Constants`, `Complexity`,
  `BrowserTest`, `StepExploration`, `EditPhases`, `Prompts`, `Formatting`,
  `Terminal` — each becomes a class with an `internal`/`public` surface; the few
  cross-refs become explicit calls.
- **Orchestration services (the spine, higher risk):** `Pipeline`, `Planning`,
  `Discovery`, `Execution`, `EditPipeline`, `ApplyEdit` — these become services that
  *compose* the leaf services via constructor injection. Their high import counts (47–49)
  become a visible dependency list that today is invisible — which is the *value* of
  composition, not a cost.

### 3.7 Conclusion

For `AgentController`, **partial classes are the wrong tool**: they provide file-shredding
without the encapsulation, testability, or coupling-control that the size of this class
demands. The measured 51.8% silent cross-coupling, the reflection-based test harness,
and the cross-request static state are all consequences of choosing `partial` where
composition was needed. The roadmap in Section 4 therefore replaces partials with
composed services, not merely better-organized partials.

---

## 4. Sequenced Decomposition Roadmap

Ordered lowest-risk-first. Each step keeps `dotnet test` + `node tests/js/run-all.js`
green and is one reviewable commit (the standard held for the StrictVerifier branch).
**No behavior change** — pure extract/move.

### Phase 0 — Safe quick wins (≈1–2 days each)

- [ ] **Extract `AgentRunContext`.** Move the 12 run-scoped mutable fields
  (`_gracefulStop`, `_requirementChecklist`, `_stepLlm*`, `_runLlm*`,
  `_skeletonContextChars`, `_taskPromptContextChars`, `_discoverySteps`) into a
  `RunContext` record passed through `StepResolutionPipeline`/`PostExecuteVerify`.
  Kills the "reset on run start" smell; makes runs trivially testable. No behavior change.
- [ ] **Split `AgentEditHeuristics` by family** behind an `IEditHeuristic` interface
  (one class per family). Small, safe, mechanical.

### Phase 1 — Extract services from the controller partials (the real fix)

Each partial's private methods become a service the controller delegates to. The
`private` cross-partial access is replaced with explicit service interfaces — which
is exactly what retires the reflection-test coupling.

- [ ] `AgentController.Llm.cs` → `LlmClient` (natural seam; already wraps `IHttpClientFactory`)
- [ ] `AgentController.Terminal.cs` → `CommandRunner`
- [ ] `AgentController.BrowserTest.cs` → `BrowserTestRunner`
- [ ] `AgentController.Formatting.cs` → `CodeFormatter`
- [ ] `AgentController.Pipeline.cs` → `AgentPipeline` (the verify/repair loop)
- [ ] `AgentController.EditResolution.cs` → `EditResolver`
- [ ] `AgentController.Planning.cs` → `AgentPlanner` (the 5,333-line monster — last)

After Phase 1, `AgentController` shrinks to ~HTTP + SSE wiring that delegates; the
controller's dependency count drops from 25 to the handful of services it orchestrates.

### Phase 2 — Kill the static state (correctness)

- [ ] Move the 13 `static`/`ConcurrentDictionary` fields into a scoped `RunRegistry`
  (per-run) or `CardSessionStore` (per-card), injected. Fixes the cross-request hazard
  and removes the test `SetStaticField` dance.

### Phase 3 — Front-end

- [ ] **Split `meeting.js`** into `meeting-ui.js`, `meeting-ticker.js`,
  `meeting-spider.js`, `meeting-state.js`. No compile risk, big DX win.

---

## 5. Non-goals (deliberately out of scope for this analysis)

- No behavior changes, no API changes, no config-schema changes.
- No new dependencies.
- No changes to the public HTTP surface (`api/agent/*`, `api/config/*`, etc.).
- Does not touch `feature/test-benchmark-gates` (in flight).

## 6. Next steps

1. Pick Phase 0's `AgentRunContext` extract as the first concrete commit — it's the
   safest, highest-leverage move and unblocks cleaner tests for everything that follows.
2. Re-measure the controller aggregate after each phase to confirm the line/dependency
   counts actually drop (the metric is the proof the refactor worked).
3. Keep the "one extract per commit, tests green" discipline that held for the
   StrictVerifier + CI work.
