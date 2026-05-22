# Maestro Backend

This is a lightweight ASP.NET Core backend that:

- Spawns a shell-like terminal and exposes simple HTTP APIs to start it, run commands, and read output.
- Proxies AI requests to a llama.cpp HTTP server (default at `http://192.168.2.58:8080`).
- Serves a small AngularJS-based Kanban frontend from `wwwroot/` that interacts with the above APIs.

## Requirements

- .NET 7 SDK (or compatible runtime)

## Run

From the `backend` folder:

```bash
dotnet restore
dotnet run
```

The app serves static files and APIs. Open the URL shown in the console (usually `http://localhost:5000` or `http://localhost:5001`).

## Configuration

`appsettings.json` contains `LlamaUrl` (defaults to `http://192.168.2.58:8080`). Edit if your llama server is at a different address.

## API Endpoints

- `POST /api/terminal/start` — start the terminal process
- `POST /api/terminal/exec` — execute a command: JSON body `{ "command": "echo hi" }`
- `GET /api/terminal/output` — returns `{ "output": "..." }` with recent terminal output
- `POST /api/ai/generate` — proxy to llama server; send JSON like `{ "prompt": "Write a short todo list" }`
- `POST /api/ai/proxy?path=...` — proxy arbitrary POST to the llama server path

## Frontend

The frontend is a small AngularJS (1.x) single-file app in `wwwroot/` that provides a Kanban board, AI assistant, and basic terminal UI.

## Notes

- The frontend currently uses AngularJS for quick scaffolding. If you want a full Angular (2+) project, I can scaffold an `ng`-based project and integrate its build output into `wwwroot`.
- The terminal executes commands on the server — be cautious when exposing this over the network.

