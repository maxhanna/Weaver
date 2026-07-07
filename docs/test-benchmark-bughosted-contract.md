# Weaver Test-Benchmark ⇄ BugHosted Contract

Status: **draft** — Weaver client side (Phases 1–3) implemented; server side is BTC's
to build. Fields marked **(Phase 4a — planned)** are specced here ahead of the Weaver
implementation so BTC can design the schema once.

This is the data contract for the agent-benchmark framework discussed 2026-06-24.
A "test card" is run through the orchestrator; the orchestrator scores **how far
through the card's steps the agent got before breaking** and emits a `TestRunResult`.
Results are uploaded to BugHosted so model / Weaver-version / hardware combinations
can be compared on a shared leaderboard.

Scoring is two-tier (decided 2026-07-06):

1. **Progress score** (0–100) — how far through the steps the agent got. Useful for
   comparing runs that all fail; a small model that reaches step 3 of 4 beats one
   that dies on step 1.
2. **Gates** — binary disqualifiers. Any gate failure means the run is not a
   **perfect pass**, regardless of score. `perfectPass = passed && all gates true`.
   The leaderboard's headline stat is perfect-pass rate; the gates explain *why* a
   run wasn't perfect.

## Flow

```
User loads test card → Frontend (card.isTest = true)
  → POST /api/agent/execute-stream { ..., isTest: true, testName }
  → Orchestrator runs steps one at a time, halts on first failure (existing behavior)
  → TestScorer computes TestRunResult (+ machine metadata + .weaver-version)
  → SSE event "test_result" → Frontend Test Score Card
  → POST {bughosted}/weaver/test-score   (upload — Phase 4, client TODO)
  → GET  {bughosted}/weaver/leaderboard  (compare — Phase 4, client TODO)
```

## TestRunResult (the wire object)

Emitted as the `test_result` SSE event and used as the upload body. Source of truth:
`DataContracts/Agent/TestRunResult.cs`.

```jsonc
{
  "testName": "starter-create-tests-md",
  "cardId": "card-uuid-or-null",
  "stepsPassed": 2,
  "totalSteps": 4,
  "score": 50,                 // 0–100, how far it got
  "passed": false,             // true only when ALL steps completed
  "failedStep": "src/hard.cs",
  "failureReason": "Fatal step failure: could not apply edit",
  "codeFile": "tests.md",      // primary file produced
  "writtenTests": ["tests/CalcTests.cs"],
  "machine": {
    "os": "Microsoft Windows 10.0.26220",
    "osArchitecture": "X64",
    "cpuCores": 16,
    "ramGb": 32.0,
    "machineName": "DESKTOP-XYZ",
    "runtime": ".NET 10.0.0"
  },
  "weaverVersion": "6",
  "runAt": "2026-06-28T12:34:56Z",

  // ---- Phase 4a — planned, not yet emitted by Weaver ----
  "expectedSteps": 3,          // from the card manifest; null if card doesn't pin it
  "plannedSteps": 3,           // how many steps the model's plan actually contained
  "gates": {
    "formattingClean": true,     // every edited file passes the formatting oracle
    "structurePreserved": true,  // no writes outside the card's allowedPaths
    "permissionsRespected": true,// every command went through the approval flow
    "exactStepCount": true,      // plannedSteps == expectedSteps == stepsPassed
    "noReplan": true             // no replan- or repair-originated steps executed
  },
  "perfectPass": true,         // passed && every gate true
  "model": {
    "name": "medgemma:4b",     // model identifier as configured
    "backend": "llama.cpp",    // inference backend, best-effort
    "temperature": 0.0,
    "seed": 42                 // null when the backend doesn't honor seeds
  }
}
```

Gate semantics (client-side, enforced by `TestScorer` + orchestrator telemetry):

| Gate | Fails when |
| --- | --- |
| `formattingClean` | Any file in `writtenTests`/`codeFile`/edited set fails the card's formatting oracle (canonical formatter in check mode, or golden-file diff). Files already dirty before the run don't count against it. |
| `structurePreserved` | Any file created/modified/deleted outside the card manifest's `allowedPaths`. |
| `permissionsRespected` | Any command executed without passing the terminal approval flow, or any blocked operation attempted. |
| `exactStepCount` | The generated plan's step count differs from the card's `expectedSteps`, or extra steps executed beyond the plan. "3 planned → 3 complete, no more, no less." |
| `noReplan` | Any executed step originated from a replan (`GenerateReplanStepsAsync`) or repair pipeline rather than the original plan. A step that only succeeded via internal repair fails this gate. |

A gate that cannot be evaluated (e.g. card has no `expectedSteps`) is reported as
`null`, counts as *not perfect*, and the leaderboard shows it as "unmeasured".

## Card manifest (Phase 4a — planned)

Test cards gain a `benchmark` block that makes the gates decidable:

```jsonc
{
  "isTest": true,
  "testName": "starter-create-tests-md",
  "benchmark": {
    "expectedSteps": 3,
    "allowedPaths": ["tests.md", "tests/**"],       // globs, project-root-relative
    "formatting": {
      "mode": "formatter",                          // "formatter" | "golden" | "none"
      "commands": { "cs": "dotnet format --verify-no-changes --include {file}" }
    },
    "runs": 1                                       // repeat count; see determinism note
  }
}
```

**Determinism note:** identical output across models/hardware can't be guaranteed
(quantization, backend, scheduling all perturb sampling even at temperature 0), so
determinism is *measured*, not assumed: a card may request `runs: N`, each run
uploads individually, and the leaderboard aggregates a perfect-pass **rate** per
(model, weaverVersion, machine). 10/10 and 6/10 are different results even when the
best single run of each is identical.

## Server endpoints (BTC to implement, SQL-mirrored)

### `POST {bughosted}/weaver/test-score`
Upload one run. Auth via existing Weaver session token (same scheme as
`/weaver/heartbeat`). Body = the `TestRunResult` above plus the session fields
already sent on heartbeat:

```jsonc
{ "token": "...", "clientId": "...", "result": { /* TestRunResult */ } }
```

Response: `{ "ok": true, "id": "<server-row-id>" }`

Suggested SQL columns: `id, client_id, test_name, card_id, steps_passed,
total_steps, score, passed, failed_step, failure_reason, code_file,
written_tests (json), os, os_arch, cpu_cores, ram_gb, machine_name, runtime,
weaver_version, run_at, created_at`.

Phase 4a additions: `expected_steps, planned_steps, perfect_pass,
gate_formatting_clean, gate_structure_preserved, gate_permissions_respected,
gate_exact_step_count, gate_no_replan` (nullable booleans — `NULL` = unmeasured),
`model_name, model_backend, model_temperature, model_seed`. Rows uploaded by
pre-4a clients simply leave these `NULL`.

### `GET {bughosted}/weaver/leaderboard?testName=&limit=`
Fetch best/recent runs for comparison. Response:

```jsonc
{
  "testName": "starter-create-tests-md",
  "entries": [
    { "score": 100, "passed": true, "weaverVersion": "6",
      "os": "Windows 10.0.26220", "cpuCores": 16, "ramGb": 32.0,
      "machineName": "DESKTOP-XYZ", "runAt": "2026-06-28T12:34:56Z",
      // Phase 4a
      "perfectPass": true, "modelName": "medgemma:4b",
      "perfectPassRate": { "perfect": 9, "runs": 10 }   // aggregated per
                                                        // (model, weaverVersion, machine)
    }
  ]
}
```

Ranking (Phase 4a): entries ordered by perfect-pass **rate** first, then best
score, then most recent. A single lucky run doesn't outrank a consistently
perfect (model, hardware) pair.

## Open questions for BTC

- Tie-break when perfect-pass rates are equal (e.g. wall-clock duration — not
  captured yet)?
- Do we key the leaderboard by `testName` string, or a registered test id?
- Should the leaderboard expose per-gate failure counts (e.g. "formatting is the
  most-failed gate for model X"), or only `perfectPass`?

## Remaining Weaver-side phases

- **Phase 4a — gates & telemetry** (prerequisite for upload):
  - Card manifest (`benchmark` block) on test cards; `expectedSteps`, `allowedPaths`,
    formatting oracle config.
  - Orchestrator telemetry: tag step dicts with `origin` (plan | replan | repair),
    record file touches against `allowedPaths`, record command approval provenance.
  - Formatting gate: post-run formatter check (or golden diff) over edited files.
  - `TestScorer` computes the five gates + `perfectPass`; extend `TestRunResult`
    and `EnvironmentMetadata` (model name/backend/temperature/seed).
- **Phase 4b** — client `POST test-score` / `GET leaderboard` in `BughostedController`,
  plus a "BugHosted Integration" settings affordance to view scores.
- **Phase 5** — seed benchmark cards: the starter ("create `tests.md`, then
  progressively harder steps") and the advanced "scaffold a project from README".
  Each seed card ships with a `benchmark` manifest so all five gates are measurable.
