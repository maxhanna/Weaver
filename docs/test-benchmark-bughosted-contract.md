# Weaver Test-Benchmark ⇄ BugHosted Contract

Status: **draft** — Weaver client side (Phases 1–2) implemented; server side is BTC's to build.

This is the data contract for the agent-benchmark framework discussed 2026-06-24.
A "test card" is run through the orchestrator; the orchestrator scores **how far
through the card's steps the agent got before breaking** and emits a `TestRunResult`.
Results are uploaded to BugHosted so model / Weaver-version / hardware combinations
can be compared on a shared leaderboard.

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
  "runAt": "2026-06-28T12:34:56Z"
}
```

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

### `GET {bughosted}/weaver/leaderboard?testName=&limit=`
Fetch best/recent runs for comparison. Response:

```jsonc
{
  "testName": "starter-create-tests-md",
  "entries": [
    { "score": 100, "passed": true, "weaverVersion": "6",
      "os": "Windows 10.0.26220", "cpuCores": 16, "ramGb": 32.0,
      "machineName": "DESKTOP-XYZ", "runAt": "2026-06-28T12:34:56Z" }
  ]
}
```

## Open questions for BTC
- Ranking: best score per (machine, weaverVersion), or every run?
- Tie-break when score == 100 (e.g. wall-clock duration — not captured yet)?
- Do we key the leaderboard by `testName` string, or a registered test id?

## Remaining Weaver-side phases
- **Phase 3** — Frontend Test Score Card (AngularJS, bind to `vm.agentResult.testResult`).
- **Phase 4** — client `POST test-score` / `GET leaderboard` in `BughostedController`,
  plus a "BugHosted Integration" settings affordance to view scores.
- **Phase 5** — seed benchmark cards: the starter ("create `tests.md`, then
  progressively harder steps") and the advanced "scaffold a project from README".
