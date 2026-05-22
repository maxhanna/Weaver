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

## File editing API

You can now instruct the backend to write files inside the repository workspace. The API is intentionally conservative and only allows writes under the configured workspace root (defaults to the parent folder of `backend`).

- `POST /api/editor/write` — JSON body:

```json
{
	"project": "backend",
	"path": "Services/TerminalService.cs",
	"content": "...new file contents...",
	"apply": true,
	"createIfMissing": true
}
```

If `apply` is `true`, the server writes the file (creating directories if needed). If `apply` is `false`, the server will only return the resolved absolute path and whether the file exists.

- `GET /api/editor/projects` — lists top-level folders under the workspace root so you can pick a `project` value.

Configuration: set the workspace root in [backend/appsettings.json](backend/appsettings.json#L1-L20) via `Editor:WorkspaceRoot`. Relative paths are resolved from the `backend` folder; the default is `..` which points to the repository root.

Security: the endpoint will reject any write that resolves outside the workspace root. Do NOT expose this API publicly without additional auth/ACL controls.

