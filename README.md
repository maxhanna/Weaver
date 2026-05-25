# Maestro Backend

This is a lightweight ASP.NET Core backend that:

- Spawns a shell-like terminal and exposes simple HTTP APIs to start it, run commands, and read output.
- Proxies AI requests to a llama.cpp HTTP server (ie: `http://localhost:8080`).
- Serves a small AngularJS-based Kanban frontend from `wwwroot/` that interacts with the above APIs.

## Requirements

- .NET 10 SDK (or compatible runtime)

## Run

From the `backend` folder:

```bash
dotnet restore
dotnet run
```

The app serves static files and APIs. Open the URL shown in the console (usually `http://localhost:5000` or `http://localhost:5001`).

## Configuration

`maestroconfig.json` contains `llamaUrl` (defaults to `http://localhost:8080`). Edit if your llama server is at a different address.

## Agentic Orchestration Router
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

Security: the endpoint will reject any write that resolves outside the workspace root. Do NOT expose this API publicly without additional auth/ACL controls.

