# Weaver

Weaver is an advanced agentic system that enables AI-driven automation through intelligent orchestration of multiple tools and pipelines. It is a tool for building AI-powered workflows (via Kanban board, Calendar, Cron jobs, etc) that can execute complex tasks autonomously. Create/Prioritize tasks and let Weaver take care of the rest. Weaver also acts as a remote connection which enables you to work with your agent remotely, share workspace, it features a built-in IDE for co-editing files, etc.


Control your agent remotely on Bughosted.com

## Requirements

- .NET 10 SDK (or compatible runtime)
- llama.cpp server / Ollama 

## Run

From the `Weaver` folder:

```bash
dotnet restore
dotnet run
```

The app serves static files and APIs. Open the URL shown in the console (usually `http://localhost:5000`).

## Configuration

`weaverconfig.json` contains `llamaUrl` (defaults to `http://localhost:8080`). Edit if your llama server is at a different address

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
Publish command : dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
