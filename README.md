# Weaver

Weaver is an advanced agentic system that enables AI-driven automation through intelligent orchestration of multiple tools and pipelines. It is a tool for building AI-powered workflows (via Kanban board, Calendar, Cron jobs, etc) that can execute complex tasks autonomously. Create/Prioritize tasks and let Weaver take care of the rest. Weaver also acts as a remote connection which enables you to work with your agent remotely, share workspace, it features a built-in IDE for co-editing files, etc.


Control your agent remotely on Bughosted.com/Weaver

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

## FAQ

### Where to Start

1. Open the options popup (top right of screen)
2. Click on both 'projects' and 'settings' and configure the application
3. Start by adding a card in the kanban, adding files for context and then pressing start

`llamaUrl` specifies the address of your OpenAI API compatible LLM endpoint. This can be any compatible server such as Ollama, llama.cpp, or other local/remote LLM services. By default, Weaver looks for the server at `http://localhost:8080`. If your server runs on a different port or address, update this setting by opening the settings panel or in `weaverconfig.json` to ensure Weaver can connect to your LLM backend.

### What if the backend application won't close?

If the backend application won't close, try closing the website first. Sometimes the backend process remains active even after closing the browser tab, so explicitly closing the website interface can help terminate the backend application properly.

### Force File Edits
In cases where the edit pipeline isn't being chosen,
Employ 'Fix the' keywords to force the agent to make file edits. 
For example, phrase your request as 'Fix the README.md file to add a new section about forced edits' 
or 'Fix the configuration section to clarify the llamaUrl setting'. 
This mechanism ensures that the standard edit pipeline is automatically selected.

## Configuration

`weaverconfig.json` contains `llamaUrl` (defaults to `http://localhost:8080`). Edit if your llama server is at a different address


Publish command : dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true

![Agentic loop](https://venturebeat.com/_next/image?url=https%3A%2F%2Fimages.ctfassets.net%2Fjdtwqhzvc2n1%2F5gWXRttHvteZMEGgygXVuz%2F3fa3112800b8d8f6e153fa0957a78f22%2Fautonomous_optimization.png%3Fw%3D1000%26q%3D100&w=3840&q=75)
