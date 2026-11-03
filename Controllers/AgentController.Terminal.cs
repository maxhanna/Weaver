using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Weaver.Services;
using static Weaver.Services.AgentTokenMetrics;
using static Weaver.Services.AgentEditHeuristics;
using static Weaver.Services.AgentPlanParsing;
using static Weaver.Services.AgentMethodInventory;
using static Weaver.Services.AgentProjectUtilities;
using static Weaver.Services.AgentDiscovery;
using static Weaver.Services.AgentTextUtilities;
using static Weaver.Services.AgentCodeFormatting;
using static Weaver.Services.AgentSkeleton;
using static Weaver.Services.AgentDiffUtilities;
using static Weaver.Services.AgentJsonUtilities;
using Weaver;
namespace Weaver.Controllers;

partial class AgentController
{
    /// Sends a command to the terminal and waits for its output to stabilize, returning only
    /// the fresh output produced since the command started. Retries ONCE when the output looks
    /// like a transient file-lock / network blip (unless Editor:DisableLLMRetries is set), so
    /// a build that failed because a file was briefly locked or a feed momentarily dropped gets
    /// a second chance before being reported as a real failure. Emits recovering + metric logs.
    /// </summary>
    private async Task<string> RunTerminalCommandWithRetryAsync(string command, string projectRoot, bool emitSse, CancellationToken ct)
    {
        var fresh = await CaptureTerminalOutputAsync(command, projectRoot, ct);
        if (LlmRetriesDisabled() || !TransientFailureDetector.IsTransientCommandFailure(fresh)) return fresh;
        await EmitLog(emitSse, "recovering",
            "⚠ Terminal command hit a transient blip (file lock / network) — retrying once.",
            detail: fresh.Length > 600 ? fresh[^600..] : fresh, ct: ct);
        try { await Task.Delay(1000, ct); } catch { return fresh; }
        var retry = await CaptureTerminalOutputAsync(command, projectRoot, ct);
        // 'recovered' means the retry is no longer transient AND shows no hard failure — a
        // retry that now surfaces a genuine compile/test error is still a failure, not a win.
        var recovered = !TransientFailureDetector.IsTransientCommandFailure(retry) && !TransientFailureDetector.LooksLikeCommandFailure(retry);
        var cmdForLog = command.Length > 120 ? command[..120] + "…" : command;
        await EmitLog(emitSse, "metric",
            $"📊 Recovery (terminal): {(recovered ? "recovered ✓" : "still failed ✗")} — {cmdForLog}", ct: ct);
        return retry;
    }

    /// <summary>
    /// Marker-based command capture for the agentic CommandExecutionPipeline: sends a command,
    /// echoes a unique __DONE_ marker, and waits for it, returning the fresh output (marker
    /// lines stripped). Retries ONCE when the output looks like a transient file-lock / network
    /// blip (unless Editor:DisableLLMRetries is set) — the pipeline's commands are often builds
    /// or installs that hit the same momentary hiccups the main executor guards against.
    /// </summary>
    private async Task<string> RunMarkerCommandWithRetryAsync(string command, string projectRoot, bool emitSse, CancellationToken ct, int waitMinutes = 10)
    {
        async Task<string> RunOnce()
        {
            var beforeLen = _terminal.ReadAll().Length;
            await _terminal.SendCommandAsync(command, projectRoot);
            var marker = "__DONE_" + Guid.NewGuid().ToString("N") + "__";
            await _terminal.WriteStdinAsync("echo '" + marker + "'");
            var timeout2 = DateTime.UtcNow.AddMinutes(waitMinutes);
            while (!ct.IsCancellationRequested && DateTime.UtcNow < timeout2)
            { await Task.Delay(500); if (_terminal.ReadAll().Contains(marker)) break; }
            var fullOut = _terminal.ReadAll();
            var freshOut = beforeLen < fullOut.Length ? fullOut[beforeLen..] : "";
            return string.Join("\n", (freshOut ?? "").Split('\n').Where(l => !l.Contains("__DONE_")));
        }

        var fresh = await RunOnce();
        if (LlmRetriesDisabled() || !TransientFailureDetector.IsTransientCommandFailure(fresh)) return fresh;
        await EmitLog(emitSse, "recovering",
            "⚠ Terminal command hit a transient blip (file lock / network) — retrying once.",
            detail: fresh.Length > 600 ? fresh[^600..] : fresh, ct: ct);
        try { await Task.Delay(1000, ct); } catch { return fresh; }
        var retry = await RunOnce();
        var recovered = !TransientFailureDetector.IsTransientCommandFailure(retry) && !TransientFailureDetector.LooksLikeCommandFailure(retry);
        var cmdForLog = command.Length > 120 ? command[..120] + "…" : command;
        await EmitLog(emitSse, "metric",
            $"📊 Recovery (terminal): {(recovered ? "recovered ✓" : "still failed ✗")} — {cmdForLog}", ct: ct);
        return retry;
    }

    private async Task<string> CaptureTerminalOutputAsync(string command, string projectRoot, CancellationToken ct)
    {
        _terminal.Start();
        var beforeLen = _terminal.ReadAll().Length;
        await _terminal.SendCommandAsync(command, projectRoot);
        var prevLen = beforeLen; var stableMs = 0;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(500, ct);
            var curLen = _terminal.ReadAll().Length;
            if (curLen == prevLen) { stableMs += 500; if (stableMs >= 3000) break; }
            else { stableMs = 0; prevLen = curLen; }
        }
        var fullOutput = _terminal.ReadAll();
        return beforeLen >= 0 && beforeLen < fullOutput.Length ? fullOutput[beforeLen..] : "";
    }

    private async Task ExecuteCommandStep(AgentStep step, string projectRoot, Dictionary<string, object?> result,
        bool emitSse, CancellationToken ct)
    {
        var command = step.Command ?? "";
        if (string.IsNullOrWhiteSpace(command)) { result["status"] = "error"; result["error"] = "No command"; return; }
        var output = await RunTerminalCommandWithRetryAsync(command, projectRoot, emitSse, ct);
        // A _command whose output shows a HARD failure (a crash, compile error, or failed
        // test run) is a FAILED step, not done — the benchmark-22 failure: `node server.js`
        // crashed with a SyntaxError but was reported "done", so the planner never planned a
        // recovery edit and deadlocked re-running the same broken command. Benign summaries
        // ("Passed!", "Failed: 0", "0 Error(s)") are NOT failures — the detector's bare
        // 'failed'/'error' substrings would otherwise flag a passing build/test run.
        if (OutputShowsHardFailure(output))
        {
            // Deterministic command self-heal: a _command whose output shows a HARD failure
            // (crash / compile error / failed test run) gets a bounded chain of MECHANICAL
            // fixes derived straight from the failure output BEFORE it is ever handed to the
            // planner — zero thinking rounds, zero new plan steps. Each attempt derives a fix
            // from the CURRENT output (fixes chain: patch the missing PORT parameter, then
            // the re-run's EADDRINUSE gets the free-port fix), re-runs, and re-checks; when a
            // fix is not applicable or the budget is exhausted, the failure falls through to
            // the planner for a real recovery edit (a crash is still reported status=error,
            // never done — the benchmark-22 deadlock fix). Benign summaries ("Passed!",
            // "Failed: 0", "0 Error(s)") never reach this path.
            const int MaxDeterministicRetries = 3;
            var recoveredPort = (int?)null;
            var recoveredBy = "";
            for (var attempt = 0; attempt < MaxDeterministicRetries; attempt++)
            {
                var fix = BuildCommandRecoveryFix(command, output, projectRoot, out var fixPort);
                if (fix == null) break; // no deterministic fix applies — fall through to the planner
                if (fixPort.HasValue) { recoveredPort = fixPort; recoveredBy = "on a free port"; }
                var retryOutput = await RunTerminalCommandWithRetryAsync(fix, projectRoot, emitSse, ct);
                if (!OutputShowsHardFailure(retryOutput))
                {
                    result["status"] = "done";
                    result["command"] = fix;
                    result["output"] = retryOutput;
                    result["snippet"] = retryOutput;
                    if (recoveredPort.HasValue)
                    {
                        result["serverPort"] = recoveredPort.Value;
                        result["serverUrl"] = $"http://localhost:{recoveredPort.Value}/";
                        result["portRecovered"] = true;
                        recoveredBy = $"on free port {recoveredPort.Value} (EADDRINUSE recovery)";
                    }
                    else
                    {
                        recoveredBy = "by deterministically patching the failing source from the error output";
                    }
                    await EmitLog(emitSse, "recovering",
                        $"🔄 Command failed ({ExtractCommandFailureExcerpt(output)}) — recovered {recoveredBy} after {attempt + 1} deterministic fix(es)" +
                        (recoveredPort.HasValue ? $"; resolved URL: http://localhost:{recoveredPort.Value}/" : ""),
                        new { recovered = true, serverPort = recoveredPort }, ct: ct);
                    return;
                }
                output = retryOutput; // derive the next fix from the new failure
            }
            result["status"] = "error";
            result["command"] = command;
            result["output"] = output;
            // Carry the diagnostic excerpt in `error` so the interleaved loop's failed-command
            // feedback hands the planner the actual error ("Error: boom"/SyntaxError), not a
            // generic "see output" — the planner needs the real reason to plan the fix.
            var excerpt = ExtractCommandFailureExcerpt(output);
            result["error"] = "Command failed: " + excerpt;
            await EmitLog(emitSse, "error", $"⛔ Command failed: {excerpt}", ct: ct);
            return;
        }
        result["status"] = "done"; result["command"] = command;
        result["output"] = output;
        result["snippet"] = output;
    }

    /// <summary>
    /// Derives ONE deterministic fix from a failed command's output, or null when no mechanical
    /// fix applies (the failure then goes to the planner). Fixes are ordered so they CHAIN:
    /// (1) a port conflict (EADDRINUSE) re-runs the command with a free port injected (PORT
    /// env + literal busy-port swap); (2) a node script referencing an UNDEFINED PORT — the
    /// benchmark-22 shape where a recovery edit dropped the `const PORT` line — gets the
    /// missing parameter defined in the script itself (the error message names it), so the
    /// re-run either binds successfully or surfaces EADDRINUSE, which fix (1) then resolves.
    /// </summary>
    private string? BuildCommandRecoveryFix(string command, string output, string projectRoot, out int? freePort)
    {
        freePort = null;
        var busyPort = ExtractBusyPort(output);
        if (busyPort.HasValue)
        {
            var p = ServerLauncherService.FindFreePort();
            freePort = p;
            return InjectPortIntoCommand(command, p, busyPort.Value, _terminal.ShellName);
        }
        var missing = ExtractMissingIdentifier(output);
        if (string.Equals(missing, "PORT", StringComparison.Ordinal) &&
            TryResolveNodeScriptPath(command, projectRoot, out var scriptPath) &&
            System.IO.File.Exists(scriptPath) &&
            PatchMissingPortIntoScript(scriptPath))
        {
            return command; // same command — the file now defines PORT
        }
        // AGILE DEPENDENCY RECOVERY: a node command failed with "Cannot find module 'X'"
        // (e.g. `node server.js` requiring 'express' before any `npm install` — the exact
        // benchmark-22 failure). Deterministically install the project's declared deps in
        // the SCRIPT's directory and re-run the original command — no planner round, no new
        // plan step. The re-run then either serves (done) or surfaces the NEXT failure
        // (EADDRINUSE / missing PORT), which the fixes above chain-resolve.
        var missingModule = ExtractMissingModule(output);
        if (!string.IsNullOrWhiteSpace(missingModule) &&
            !IsNodeBuiltinModule(missingModule) &&
            TryResolveNodeScriptPath(command, projectRoot, out var nodeScript) &&
            System.IO.File.Exists(nodeScript))
        {
            var scriptDir = Path.GetDirectoryName(nodeScript);
            if (!string.IsNullOrWhiteSpace(scriptDir) &&
                System.IO.File.Exists(Path.Combine(scriptDir, "package.json")))
            {
                var shell = _terminal.ShellName.ToLowerInvariant();
                var isPs = shell is "powershell" or "pwsh" or "cmd" or "cmd.exe";
                var cdPart = isPs
                    ? $"Set-Location -LiteralPath \"{scriptDir}\"; "
                    : $"cd \"{scriptDir}\" && ";
                var installPart = isPs
                    ? "npm install --no-audit --no-fund 2>$null | Out-Null; "
                    : "npm install --no-audit --no-fund >/dev/null 2>&1; ";
                return cdPart + installPart + command.TrimStart();
            }
        }
        // PYTHON mirror of the dependency recovery: `python app.py` failed with
        // "ModuleNotFoundError: No module named 'flask'" — the agent wrote the import without
        // ever running `pip install`. Deterministically install the missing dependency and
        // re-run the original command — no planner round. When the SCRIPT's directory
        // declares requirements.txt, install it (covers every dependency in one shot,
        // mirroring `npm install` pulling package.json); otherwise install the exact module
        // the error named — the agile form: a lone `import flask` with no manifest still
        // self-heals. The re-run then either serves (done) or surfaces the NEXT failure
        // (EADDRINUSE / missing PORT), which the fixes above chain-resolve.
        if (!string.IsNullOrWhiteSpace(missingModule) &&
            !IsPythonStdlibModule(missingModule) &&
            TryResolvePythonScriptPath(command, projectRoot, out var pyScript) &&
            System.IO.File.Exists(pyScript))
        {
            var scriptDir = Path.GetDirectoryName(pyScript);
            if (!string.IsNullOrWhiteSpace(scriptDir))
            {
                var shell = _terminal.ShellName.ToLowerInvariant();
                var isPs = shell is "powershell" or "pwsh" or "cmd" or "cmd.exe";
                var cdPart = isPs
                    ? $"Set-Location -LiteralPath \"{scriptDir}\"; "
                    : $"cd \"{scriptDir}\" && ";
                var hasReq = System.IO.File.Exists(Path.Combine(scriptDir, "requirements.txt"));
                var target = hasReq ? "-r requirements.txt" : missingModule;
                var installPart = isPs
                    ? $"python -m pip install --no-input --no-cache-dir {target} 2>$null | Out-Null; "
                    : $"python -m pip install --no-input --no-cache-dir {target} >/dev/null 2>&1; ";
                return cdPart + installPart + command.TrimStart();
            }
        }
        return null;
    }

    /// <summary>
    /// Extracts the module name from a `Cannot find module 'X'` (node) or
    /// `ModuleNotFoundError: No module named 'X'` (python) failure. Returns null for
    /// non-missing-module failures.
    /// </summary>
    internal static string? ExtractMissingModule(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var m = Regex.Match(output, @"Cannot find module\s*'([^']+)'", RegexOptions.IgnoreCase);
        if (!m.Success)
            m = Regex.Match(output, @"No module named\s*'([^']+)'", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>True when the module name is a python STDLIB module (os, sys, json, …) — a
    /// stdlib module can never be pip-installed, so a missing-stdlib claim is a different
    /// bug (a stripped/corrupt interpreter) and must go to the planner instead. Mirrors
    /// IsNodeBuiltinModule for the node side of the same recovery.</summary>
    internal static bool IsPythonStdlibModule(string module)
    {
        return module.ToLowerInvariant() switch
        {
            "abc" or "argparse" or "array" or "asyncio" or "base64" or "bisect" or "builtins"
            or "collections" or "contextlib" or "copy" or "csv" or "ctypes" or "dataclasses"
            or "datetime" or "decimal" or "enum" or "errno" or "fnmatch" or "fractions"
            or "functools" or "gc" or "getopt" or "getpass" or "glob" or "hashlib" or "heapq"
            or "html" or "http" or "importlib" or "inspect" or "io" or "itertools" or "json"
            or "logging" or "math" or "mimetypes" or "multiprocessing" or "os" or "pathlib"
            or "pdb" or "pickle" or "platform" or "queue" or "random" or "re" or "select"
            or "shlex" or "shutil" or "signal" or "socket" or "sqlite3" or "ssl" or "statistics"
            or "string" or "struct" or "subprocess" or "sys" or "sysconfig" or "tempfile"
            or "threading" or "time" or "traceback" or "types" or "typing" or "unittest"
            or "urllib" or "uuid" or "warnings" or "weakref" or "xml" or "zipfile" => true,
            _ => false
        };
    }

    /// <summary>True when the module name is a node BUILT-IN (fs, http, path, …) — a
    /// builtin can never be npm-installed, so a missing-builtin claim is a different bug
    /// (a stripped/corrupt runtime) and must go to the planner instead.</summary>
    internal static bool IsNodeBuiltinModule(string module)
    {
        if (module.StartsWith("node:", StringComparison.Ordinal)) return true;
        return module.ToLowerInvariant() switch
        {
            "assert" or "async_hooks" or "buffer" or "child_process" or "cluster" or "console"
            or "constants" or "crypto" or "dgram" or "diagnostics_channel" or "dns" or "domain"
            or "events" or "fs" or "http" or "http2" or "https" or "inspector" or "module"
            or "net" or "os" or "path" or "perf_hooks" or "process" or "punycode" or "querystring"
            or "readline" or "repl" or "stream" or "string_decoder" or "sys" or "timers" or "tls"
            or "trace_events" or "tty" or "url" or "util" or "v8" or "vm" or "wasi" or "worker_threads"
            or "zlib" => true,
            _ => false
        };
    }

    /// <summary>
    /// Extracts the identifier from a `ReferenceError: X is not defined` (node) or
    /// `NameError: name 'X' is not defined` (python) style failure — the missing parameter the
    /// command's error message itself names. Returns null for non-missing-identifier failures.
    /// </summary>
    internal static string? ExtractMissingIdentifier(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var m = Regex.Match(output, @"ReferenceError:\s*([A-Za-z_$][A-Za-z0-9_$]*)\s+is not defined", RegexOptions.IgnoreCase);
        if (!m.Success)
            m = Regex.Match(output, @"NameError:\s*name\s+'([A-Za-z_$][A-Za-z0-9_$]*)'\s+is not defined", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Resolves the script a `python &lt;script&gt;` command runs, or returns false when the
    /// command is not a plain script invocation (python -c, flags, other runtimes). Handles
    /// quoted absolute paths (`python "C:\path with spaces\app.py"`) and bare relative
    /// paths (`python app.py` resolved against projectRoot).
    /// </summary>
    internal static bool TryResolvePythonScriptPath(string command, string projectRoot, out string scriptPath)
    {
        scriptPath = "";
        if (string.IsNullOrWhiteSpace(command)) return false;
        var m = Regex.Match(command, @"\bpython(?:3(?:\.\d+)?)?(?:\.exe)?\s+(?:""([^""]+)""|([^\s""-][^\s""]*))", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        var arg = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith("-", StringComparison.Ordinal)) return false;
        if (!arg.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) return false;
        scriptPath = Path.IsPathRooted(arg)
            ? Path.GetFullPath(arg)
            : Path.GetFullPath(Path.Combine(projectRoot, arg));
        return true;
    }

    /// <summary>
    /// Resolves the script a `node &lt;script&gt;` command runs, or returns false when the
    /// command is not a plain script invocation (node -e, flags, other runtimes). Handles
    /// quoted absolute paths (`node "C:\path with spaces\server.js"`) and bare relative
    /// paths (`node server.js` resolved against projectRoot).
    /// </summary>
    internal static bool TryResolveNodeScriptPath(string command, string projectRoot, out string scriptPath)
    {
        scriptPath = "";
        if (string.IsNullOrWhiteSpace(command)) return false;
        var m = Regex.Match(command, @"\bnode(?:\.exe)?\s+(?:""([^""]+)""|([^\s""-][^\s""]*))", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        var arg = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith("-", StringComparison.Ordinal)) return false;
        if (!arg.EndsWith(".js", StringComparison.OrdinalIgnoreCase) &&
            !arg.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) &&
            !arg.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase)) return false;
        scriptPath = Path.IsPathRooted(arg)
            ? Path.GetFullPath(arg)
            : Path.GetFullPath(Path.Combine(projectRoot, arg));
        return true;
    }

    /// <summary>
    /// Deterministically defines the missing PORT parameter in a node script: injects
    /// `const PORT = process.env.PORT || 8765;` (the benchmark contract — read PORT env,
    /// default 8765) right after the leading 'use strict' directive, so the re-run has the
    /// parameter the error said was missing. Refuses to patch when PORT is already declared
    /// (a duplicate const would swap the ReferenceError for a SyntaxError) or the file is
    /// unreadable. Returns true when the patch was applied.
    /// </summary>
    internal static bool PatchMissingPortIntoScript(string scriptPath)
    {
        try
        {
            var content = System.IO.File.ReadAllText(scriptPath);
            if (Regex.IsMatch(content, @"\b(?:const|let|var)\s+PORT\b\s*=", RegexOptions.IgnoreCase)) return false;
            var insertAt = content.Length > 0 && content[0] == '\uFEFF' ? 1 : 0;
            var strict = Regex.Match(content, @"^\s*['""]use strict['""]\s*;", RegexOptions.IgnoreCase);
            if (strict.Success) insertAt = strict.Index + strict.Length;
            // A leading newline would make the file start with a blank line — only needed
            // when landing AFTER a directive; at the very top the declaration leads.
            var patch = insertAt == 0
                ? "const PORT = process.env.PORT || 8765;\n"
                : "\nconst PORT = process.env.PORT || 8765;\n";
            System.IO.File.WriteAllText(scriptPath, content.Insert(insertAt, patch));
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// True when the command output is a PORT-IN-USE failure (EADDRINUSE / "address already
    /// in use") — the one command failure class that is recoverable DETERMINISTICALLY by
    /// re-running on a free port, with no planner round. The user-facing benchmark contract
    /// demands exactly this: "If the preconfigured port is busy, start the server on a
    /// different free port."
    /// </summary>
    internal static bool IsPortInUseFailure(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        var o = output.ToLowerInvariant();
        return o.Contains("eaddrinuse", StringComparison.Ordinal) ||
               o.Contains("address already in use", StringComparison.Ordinal) ||
               o.Contains("port already in use", StringComparison.Ordinal) ||
               o.Contains("permission denied to bind", StringComparison.Ordinal);
    }

    /// <summary>
    /// Parses the port number from a port-in-use failure, or null when the output is not a
    /// port-conflict (or no port is reported). Node prints `{ code: 'EADDRINUSE', …, port:
    /// 8765 }`; Python prints `[Errno 98] Address already in use` (no port); common CLIs
    /// print `listen EADDRINUSE: address already in use :::8765`. Gated on the conflict
    /// signature so an unrelated "port: 8765" in passing output can never misfire.
    /// </summary>
    internal static int? ExtractBusyPort(string? output)
    {
        if (!IsPortInUseFailure(output)) return null;
        var o = output!;
        Match m = Regex.Match(o, @"\bport[:""']?\s*[:=]\s*(\d{1,5})", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var p) && p > 0 && p < 65536) return p;
        m = Regex.Match(o, @"in use\s*:+\s*(\d{1,5})", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out p) && p > 0 && p < 65536) return p;
        // Node's EADDRINUSE object literal carries `port: 8765` after the message.
        m = Regex.Match(o, @"port:\s*(\d{1,5})", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out p) && p > 0 && p < 65536) return p;
        return null;
    }

    /// <summary>
    /// Rebuilds a server-start command to use a free port: (1) any literal occurrence of the
    /// busy port in the command is swapped for the free port (covers `--port 8765`, `:8765`,
    /// `python -m http.server 8765`, …) and (2) a shell-native `PORT` env injection is
    /// prepended so servers that read `process.env.PORT` (the benchmark-22 contract) bind the
    /// free port. The env set is scoped to the single command via the shell's one-shot
    /// assignment (bash) or a same-line reset so it does not leak into later commands.
    /// </summary>
    internal static string InjectPortIntoCommand(string command, int freePort, int busyPort, string shellName)
    {
        var cmd = Regex.Replace(command ?? "", $@"\b{busyPort}\b", freePort.ToString());
        var prefix = shellName.ToLowerInvariant() switch
        {
            "powershell" or "pwsh" => $"$env:PORT={freePort}; ",
            "cmd" or "cmd.exe" => $"set PORT={freePort}&& ",
            _ => $"PORT={freePort} " // bash/sh — one-shot env assignment for this command only
        };
        return prefix + cmd.TrimStart();
    }

    /// <summary>Builds the failure excerpt fed back to the planner. The PS terminal prefixes
    /// every command with a prompt/echo line ("PS …&gt; Set-Location …; node server.js") that is
    /// noise, not diagnostic — drop it so the actual error line (SyntaxError / Error: boom)
    /// fits inside the truncation budget instead of being cut off by the head-truncate.</summary>
    internal static string ExtractCommandFailureExcerpt(string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n');
        var start = 0;
        if (lines.Length > 0 && lines[0].TrimStart().StartsWith("PS ", StringComparison.OrdinalIgnoreCase))
            start = 1; // PowerShell prompt + echoed command line — noise
        var rest = string.Join("\n", lines.Skip(start));
        return TruncateForLlm(rest, 400);
    }

    /// <summary>True when command output shows a HARD failure, with benign success summaries
    /// exempted so a passing build/test run is never mistaken for a crash. "Passed!" (with
    /// the bang) only appears on xUnit's SUCCESS line — the failure line is "Failed! …".</summary>
    internal static bool OutputShowsHardFailure(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        var o = output.ToLowerInvariant();
        if (o.Contains("passed!", StringComparison.Ordinal) ||
            o.Contains("build succeeded", StringComparison.Ordinal) ||
            Regex.IsMatch(o, @"\bfailed\s*[:=]?\s*0\b") ||   // "Failed: 0" / "failed = 0"
            Regex.IsMatch(o, @"\b0\s+failed\b") ||           // "0 failed, 10 passed"
            Regex.IsMatch(o, @"\b0\s+(?:errors?|warnings?)\b")) // "0 Error(s)", "0 warnings"
            return false;
        return TransientFailureDetector.LooksLikeCommandFailure(o);
    }
    private async Task<(string output, string? error)> WebSearchAsync(string query, CancellationToken ct)
    {
        try
        {
            var client = _clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(1);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            var apiUrl = "https://api.duckduckgo.com/?q=" + Uri.EscapeDataString(query) + "&format=json&no_html=1&skip_disambig=1";
            var resp = await client.GetAsync(apiUrl, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var sb = new StringBuilder();
            if (root.TryGetProperty("AbstractText", out var abs) && abs.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(abs.GetString()))
            { sb.AppendLine("## Summary"); sb.AppendLine(abs.GetString()); if (root.TryGetProperty("AbstractURL", out var url)) sb.AppendLine($"Source: {url.GetString()}"); sb.AppendLine(); }
            if (root.TryGetProperty("Answer", out var ans) && ans.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(ans.GetString()))
                sb.AppendLine($"Answer: {ans.GetString()}");
            if (root.TryGetProperty("RelatedTopics", out var topics) && topics.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine("## Results"); var count = 0;
                foreach (var topic in topics.EnumerateArray())
                {
                    if (count >= 10) break;
                    if (topic.TryGetProperty("Text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        var u = topic.TryGetProperty("FirstURL", out var fu) ? fu.GetString() : "";
                        sb.AppendLine($"  - {text.GetString()}{(string.IsNullOrWhiteSpace(u) ? "" : $" ({u})")}"); count++;
                    }
                }
            }
            return (sb.Length > 0 ? sb.ToString() : "(no results)", null);
        }
        catch (Exception ex) { return ("", ex.Message); }
    }
    private async Task<(string output, string? error)> WebFetchAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = _clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            var resp = await client.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "text/plain";
            if (contentType.Contains("html")) body = Regex.Replace(body, "<[^>]+>", " ");
            return ($"HTTP {(int)resp.StatusCode}\n{body.Trim()}", null);
        }
        catch (Exception ex) { return ("", ex.Message); }
    }
    private async Task ExecuteReadStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var relPath = (step.Path ?? "").Replace('/', Path.DirectorySeparatorChar);
        var targetPath = Path.GetFullPath(Path.Combine(projectRoot, relPath));
        if (!AgentProjectUtilities.IsPathUnderRoot(targetPath, projectRoot))
        {
            result["status"] = "error";
            result["error"] = "Path outside root";
            return;
        }
        if (!System.IO.File.Exists(targetPath))
        {
            result["status"] = "error";
            result["error"] = "File not found";
            return;
        }
        result["path"] = step.Path;
        result["output"] = await System.IO.File.ReadAllTextAsync(targetPath, Encoding.UTF8);
        result["status"] = "done";
    }
    private Task ExecuteListStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var relPath = string.IsNullOrWhiteSpace(step.Path) ? "" : step.Path.Replace('/', Path.DirectorySeparatorChar);
        var targetPath = Path.GetFullPath(Path.Combine(projectRoot, relPath));
        if (!AgentProjectUtilities.IsPathUnderRoot(targetPath, projectRoot)) { result["status"] = "error"; result["error"] = "Path outside root"; return Task.CompletedTask; }
        if (!Directory.Exists(targetPath)) { result["status"] = "error"; result["error"] = "Directory not found"; return Task.CompletedTask; }
        var entries = Directory.GetFileSystemEntries(targetPath)
            .Select(e => (Directory.Exists(e) ? "[dir]  " : "[file] ") + Path.GetFileName(e))
            .OrderBy(x => x).Take(200);
        result["status"] = "done"; result["path"] = step.Path ?? ".";
        result["output"] = string.Join("\n", entries);
        return Task.CompletedTask;
    }
    private Task ExecuteGlobStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var pattern = (step.Pattern ?? step.Path ?? "*").Replace('\\', '/');
        result["path"] = pattern;
        try
        {
            IEnumerable<string> files;
            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                var parts = pattern.Split('/'); var filePattern = parts[^1];
                var dirParts = parts.Length > 1 ? parts[..^1] : Array.Empty<string>();
                var hasRec = dirParts.Any(p => p == "**");
                var dirClean = dirParts.Where(p => p != "**").ToList();
                if (dirClean.Count == 0 || hasRec)
                    files = Directory.EnumerateFiles(projectRoot, filePattern == "**" ? "*" : filePattern, SearchOption.AllDirectories);
                else
                {
                    var searchRoot = Path.GetFullPath(Path.Combine(projectRoot, string.Join(Path.DirectorySeparatorChar, dirClean)));
                    if (!AgentProjectUtilities.IsPathUnderRoot(searchRoot, projectRoot)) throw new InvalidOperationException("Pattern outside root");
                    files = Directory.EnumerateFiles(searchRoot, filePattern, SearchOption.AllDirectories);
                }
            }
            else
            {
                var single = Path.GetFullPath(Path.Combine(projectRoot, pattern));
                files = System.IO.File.Exists(single) ? new[] { single } : Array.Empty<string>();
            }
            var list = files.Where(f => AgentProjectUtilities.IsPathUnderRoot(f, projectRoot)).Take(100)
                .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/')).ToList();
            result["status"] = "done"; result["output"] = list.Count == 0 ? "(no matches)" : string.Join("\n", list);
        }
        catch (Exception ex) { result["status"] = "error"; result["error"] = ex.Message; }
        return Task.CompletedTask;
    }
    private async Task<(List<object> allSteps, AgentPlan? plan, bool complete)> RepairBuildPipeline(string prompt, string projectRoot, bool emitSse, string buildCommands, CancellationToken ct)
    {
        await EmitLog(emitSse, "info", "Build repair prompt detected — running repair pipeline.", ct: ct);
        var cmds = ParseBuildCommands(buildCommands);
        string? buildOutput = null;
        if (cmds.Count > 0)
        {
            _terminal.Start();
            foreach (var cmd in cmds)
            {
                await _terminal.SendCommandAsync(cmd, projectRoot);
                await Task.Delay(3000);
            }
            buildOutput = _terminal.ReadAll();
        }
        var resultSteps = new List<object>();
        await RunRepairPlan(projectRoot, emitSse, ct, prompt, buildOutput ?? "", resultSteps);
        return (resultSteps, null, true);
    }
    private Task ExecuteGrepStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var query = step.Query ?? step.Pattern ?? "";
        result["path"] = step.Path ?? ""; result["query"] = query;
        if (string.IsNullOrWhiteSpace(query)) { result["status"] = "error"; result["error"] = "grep requires query"; return Task.CompletedTask; }
        var searchRoot = projectRoot;
        if (!string.IsNullOrWhiteSpace(step.Path))
        {
            searchRoot = Path.GetFullPath(Path.Combine(projectRoot, step.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!AgentProjectUtilities.IsPathUnderRoot(searchRoot, projectRoot)) { result["status"] = "error"; result["error"] = "Path outside root"; return Task.CompletedTask; }
        }
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules", ".git", "bin", "obj", "dist", ".angular" };
        var matches = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories))
            {
                if (!AgentProjectUtilities.IsPathUnderRoot(file, projectRoot)) continue;
                if (skipDirs.Any(d => file.Contains(Path.DirectorySeparatorChar + d + Path.DirectorySeparatorChar))) continue;
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > 500_000) continue;
                    var lines = System.IO.File.ReadAllLines(file);
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (!lines[i].Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                        matches.Add($"{Path.GetRelativePath(projectRoot, file).Replace('\\', '/')}:{i + 1}: {lines[i].Trim()}");
                        if (matches.Count >= 50) break;
                    }
                }
                catch { }
                if (matches.Count >= 50) break;
            }
            result["status"] = "done";
            result["output"] = matches.Count == 0 ? "(no matches)" : string.Join("\n", matches);
        }
        catch (Exception ex) { result["status"] = "error"; result["error"] = ex.Message; }
        return Task.CompletedTask;
    }
    private async Task ExecuteWebStep(AgentStep step, Dictionary<string, object?> result)
    {
        var isFetch = step.Type is "web_fetch";
        var target = step.Url ?? step.Path ?? "";
        var query = step.Query ?? "";
        try
        {
            var client = _clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            if (isFetch || (!string.IsNullOrWhiteSpace(target) && Uri.TryCreate(target, UriKind.Absolute, out _)))
            {
                var url = Uri.TryCreate(target, UriKind.Absolute, out var pu) ? pu : new Uri(target);
                var resp = await client.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();
                var ct2 = resp.Content.Headers.ContentType?.MediaType ?? "text/plain";
                if (ct2.Contains("html")) body = Regex.Replace(body, "<[^>]+>", " ");
                result["status"] = "done"; result["url"] = url.ToString();
                result["output"] = $"HTTP {(int)resp.StatusCode}\n{body.Trim()}";
            }
            else
            {
                var search = !string.IsNullOrWhiteSpace(query) ? query : target;
                if (string.IsNullOrWhiteSpace(search)) { result["status"] = "error"; result["error"] = "web_search requires query"; return; }
                var (searchOut, _) = await ExecuteWebSearchAsync(search, null, CancellationToken.None);
                result["status"] = "done"; result["query"] = search; result["output"] = searchOut;
            }
        }
        catch (Exception ex) { result["status"] = "error"; result["error"] = ex.Message; }
    }
    private async Task<List<EditResult>> ApplyEditsDirect(List<EditAction> edits, string projectRoot)
    {
        var results = new List<EditResult>();
        var fileGroups = new Dictionary<string, List<EditAction>>(StringComparer.OrdinalIgnoreCase);
        var fileOrder = new List<string>();
        foreach (var edit in edits)
        {
            if (!fileGroups.ContainsKey(edit.Path)) { fileGroups[edit.Path] = new(); fileOrder.Add(edit.Path); }
            fileGroups[edit.Path].Add(edit);
        }
        foreach (var filePath in fileOrder)
        {
            var fileEdits = fileGroups[filePath];
            var targetPath = Path.GetFullPath(Path.Combine(projectRoot, filePath));
            if (!AgentProjectUtilities.IsPathUnderRoot(targetPath, projectRoot))
            { foreach (var _ in fileEdits) results.Add(new EditResult { Path = filePath, Status = "skipped", Error = "Path outside root" }); continue; }
            string content = "";
            var fileExists = System.IO.File.Exists(targetPath);
            if (fileExists) content = await System.IO.File.ReadAllTextAsync(targetPath, Encoding.UTF8);
            else if (fileEdits.Any(e => !string.IsNullOrEmpty(e.OldString)))
            { foreach (var e in fileEdits) results.Add(new EditResult { Path = filePath, Status = "skipped", Error = "File does not exist" }); continue; }
            var hasError = false;
            foreach (var edit in fileEdits)
            {
                var ur = GetUnsafeEditPayloadReason(edit.OldString, edit.NewString ?? "");
                if (ur != null) { results.Add(new EditResult { Path = filePath, Status = "error", Error = ur }); hasError = true; break; }
                if (!fileExists && string.IsNullOrEmpty(edit.OldString)) { content = edit.NewString ?? ""; continue; }
                if (string.IsNullOrEmpty(edit.OldString)) { content += edit.NewString ?? ""; continue; }
                var (ok, newContent, err, snippet) = AnchorEditHeuristics.TryReplaceSafe(content, edit.OldString, edit.NewString ?? "");
                if (!ok)
                {
                    var fullErr = err;
                    if (!string.IsNullOrEmpty(snippet)) fullErr += $". Nearby: {snippet}";
                    results.Add(new EditResult { Path = filePath, Status = "error", Error = fullErr });
                    hasError = true; break;
                }
                content = newContent;
            }
            if (!hasError)
            {
                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                await System.IO.File.WriteAllTextAsync(targetPath, content, Encoding.UTF8);
                results.Add(new EditResult { Path = filePath, Status = "written" });
            }
        }
        return results;
    }
    private async Task<bool> RunSmartBuildCheck(string projectRoot, string buildCmd, bool emitSse, CancellationToken ct)
    {
        const string systemPrompt = @"You are a build checker. Analyze the build output.
Output ONLY valid JSON (no markdown):
{""decision"": ""done""|""command""|""ask_user"", ""summary"": ""brief"", ""command"": ""cmd if needed"", ""userQuestion"": ""question if needed""}
done = build OK; command = run this to fix; ask_user = need input";
        _terminal.Start();
        await EmitLog(emitSse, "info", $"Build check: {buildCmd}", ct: ct);
        var iteration = 0; const int maxIter = 5;
        while (iteration < maxIter)
        {
            iteration++;
            // The terminal/build executor fails on transient blips too (a stale build daemon
            // holding obj/bin, a NuGet feed that dropped for a second) — retry those ONCE
            // before asking the LLM to diagnose what is usually a self-healing hiccup.
            var fresh = await RunTerminalCommandWithRetryAsync(buildCmd, projectRoot, emitSse, ct);
            var userPrompt = $"Build command: {buildCmd}\nOutput:\n```\n{fresh}\n```\nIteration: {iteration}/{maxIter}";
            var (raw, err) = await CallLlmRawText(systemPrompt, userPrompt, emitSse, ct);
            if (string.IsNullOrWhiteSpace(raw)) { await EmitLog(emitSse, "warn", $"Build check LLM failed: {err}", new { raw }, ct: ct); break; }
            var decision = ParseBuildCheckResponse(raw);
            if (decision == null) { await EmitLog(emitSse, "warn", "Could not parse build check response", new { raw, decision }, ct: ct); break; }
            switch (decision.Decision)
            {
                case "done": await EmitLog(emitSse, "success", $"Build OK: {decision.Summary}", new { raw, decision }, ct: ct); return true;
                case "command":
                    if (!string.IsNullOrWhiteSpace(decision.Command))
                    {
                        await EmitLog(emitSse, "info", $"Build fix: {decision.Command}", ct: ct);
                        await _terminal.SendCommandAsync(decision.Command, projectRoot);
                        await Task.Delay(2000);
                    }
                    continue;
                case "ask_user":
                    await EmitLog(emitSse, "info", $"Build needs user input: {decision.Summary}", ct: ct);
                    var userQuestion = !string.IsNullOrWhiteSpace(decision.UserQuestion)
                        ? decision.UserQuestion
                        : $"Build needs input: {decision.Summary}\n\nProvide the required input or type 'skip' to skip this build check:";
                    var answer = await AskUserAsync(userQuestion, new List<QuestionField>
                    {
                        new() { Key = "buildResponse", Label = decision.Summary ?? "", Type = "text", DefaultValue = "" }
                    }, ct, new { raw, decision });
                    var userResponse = answer.GetValueOrDefault("buildResponse", "").Trim();
                    if (!string.IsNullOrWhiteSpace(userResponse))
                    {
                        if (userResponse.Equals("skip", StringComparison.OrdinalIgnoreCase))
                        {
                            await EmitLog(emitSse, "warn", "User skipped build check.", ct: ct);
                            return true;
                        }
                        await _terminal.WriteStdinAsync(userResponse);
                        await Task.Delay(1000);
                        continue;
                    }
                    return false;
                default: return false;
            }
        }
        await EmitLog(emitSse, "warn", $"Build check inconclusive after {maxIter} iterations", ct: ct);
        return false;
    }
    private static BuildCheckDecision? ParseBuildCheckResponse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var json = raw.Trim();
        if (json.StartsWith("```")) { var m = Regex.Match(json, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) json = m.Groups[1].Value.Trim(); }
        try { return JsonSerializer.Deserialize<BuildCheckDecision>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch
        {
            var rep = AgentJsonUtilities.RepairJsonString(json);
            if (rep != null)
                try { return JsonSerializer.Deserialize<BuildCheckDecision>(rep, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
                catch { }
        }
        return null;
    }
    private static (bool skipMetaPlan, int score) DeterministicMetaPlanGate(string prompt)
    {
        return (true, 0);
    }
}
