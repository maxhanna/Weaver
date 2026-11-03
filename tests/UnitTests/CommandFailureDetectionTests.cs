using System.Text.RegularExpressions;
using Weaver.Controllers;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the _command hard-failure detector (AgentController.OutputShowsHardFailure). The
/// benchmark-22 failure: `node server.js` crashed with a SyntaxError but the step was
/// reported "done", so the planner never planned a recovery edit and deadlocked re-running
/// the same broken command. The detector marks a _command whose output shows a HARD failure
/// as status=error — while exempting benign success summaries ("Passed!", "Failed: 0",
/// "0 Error(s)") that contain the bare words 'failed'/'error' but mean the run PASSED.
/// </summary>
public class CommandFailureDetectionTests
{
    [Fact]
    public void NodeSyntaxErrorCrash_IsAFailure()
    {
        // The exact crash from the benchmark-22 log — node's stack-trace output.
        const string output =
            "C:\\...\\server.js:41\n" +
            "});\n" +
            "^\n" +
            "SyntaxError: Unexpected token '}'\n" +
            "    at wrapSafe (node:internal/modules/cjs/loader:1662:18)\n" +
            "    at Module._compile (node:internal/modules/cjs/loader:1704:20)";
        Assert.True(AgentController.OutputShowsHardFailure(output));
    }

    [Fact]
    public void CommandNotFound_IsAFailure()
    {
        // PowerShell "not recognized" (Windows) and bash "command not found" (Unix) — the
        // runtime-free deterministic failure the integration test uses.
        Assert.True(AgentController.OutputShowsHardFailure("notacommand12345xyz : The term 'notacommand12345xyz' is not recognized as the name of a cmdlet, function, script file, or operable program."));
        Assert.True(AgentController.OutputShowsHardFailure("bash: notacommand12345xyz: command not found"));
    }

    [Fact]
    public void XunitSuccessLine_IsNotAFailure()
    {
        const string output =
            "Test run for C:\\...\\Weaver.dll (.NETCoreApp,Version=v10.0)\n" +
            "A total of 1 test files matched the specified pattern.\n" +
            "Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 307 ms - Weaver.dll (net10.0)";
        Assert.False(AgentController.OutputShowsHardFailure(output));
    }

    [Fact]
    public void XunitFailureLine_IsAFailure()
    {
        // The FAILURE line starts "Failed!" and contains a NON-zero failed count — it must
        // NOT be exempted just because the word 'passed' appears ("Passed: 2026").
        const string output =
            "  Failed Weaver.UnitTests.SomeTest.Runs [3 ms]\n" +
            "Failed!  - Failed:     2, Passed:  2026, Skipped:     0, Total:  2028, Duration: 25 s - Weaver.dll (net10.0)";
        Assert.True(AgentController.OutputShowsHardFailure(output));
    }

    [Fact]
    public void DotnetBuildSuccess_IsNotAFailure()
    {
        Assert.False(AgentController.OutputShowsHardFailure("Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n\nTime Elapsed 00:00:02.01"));
    }

    [Fact]
    public void DotnetBuildCompileError_IsAFailure()
    {
        Assert.True(AgentController.OutputShowsHardFailure("Program.cs(12,9): error CS1002: ; expected"));
        Assert.True(AgentController.OutputShowsHardFailure("error CS0103: The name 'x' does not exist in the current context"));
    }

    [Fact]
    public void TestSummaryZeroFailed_IsNotAFailure()
    {
        Assert.False(AgentController.OutputShowsHardFailure("0 failed, 10 passed"));
        Assert.False(AgentController.OutputShowsHardFailure("Build succeeded"));
    }

    [Fact]
    public void EmptyOrNullOutput_IsNeverAFailure()
    {
        Assert.False(AgentController.OutputShowsHardFailure(null));
        Assert.False(AgentController.OutputShowsHardFailure(""));
        Assert.False(AgentController.OutputShowsHardFailure("   "));
    }

    [Fact]
    public void Excerpt_StripsPowerShellPromptLineBeforeTruncating()
    {
        // The PS terminal prefixes every command with a prompt/echo line. If it were kept,
        // the head-truncate would cut the diagnostic off before "Error: boom" (the
        // benchmark-22 node crash shape). The excerpt must drop that first line.
        const string output =
            "PS C:\\Users\\Saint\\AppData\\Local\\Temp\\proj> Set-Location -LiteralPath \"C:\\Users\\Saint\\AppData\\Local\\Temp\\proj\"; node benchmark_test_22/server.js\n" +
            "C:\\Users\\Saint\\AppData\\Local\\Temp\\proj\\benchmark_test_22\\server.js:1\n" +
            "throw new Error(\"boom\");\n" +
            "^\n" +
            "Error: boom\n";
        var excerpt = AgentController.ExtractCommandFailureExcerpt(output);
        Assert.DoesNotContain("PS ", excerpt, StringComparison.Ordinal);
        Assert.Contains("Error: boom", excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void Excerpt_NoPromptLine_PassesOutputThrough()
    {
        const string output = "server.js:1\nthrow new Error(\"boom\");\nError: boom\n";
        var excerpt = AgentController.ExtractCommandFailureExcerpt(output);
        Assert.Contains("Error: boom", excerpt, StringComparison.Ordinal);
    }

    // ── Deterministic EADDRINUSE recovery ────────────────────────────────────
    // A server-start _command whose port is already in use (the benchmark-22 shape —
    // `node server.js` defaulting to 8765 while another process holds it) must be
    // recovered DETERMINISTICALLY on a free port with zero planning rounds: the step
    // never errors, and the resolved URL is carried forward for the browser test.

    [Fact]
    public void PortInUseFailure_IsDetected()
    {
        // The exact node shapes from the benchmark-22 failure log.
        Assert.True(AgentController.IsPortInUseFailure("Failed to start HTTP listener due to: Error: listen EADDRINUSE: address already in use :::8765"));
        Assert.True(AgentController.IsPortInUseFailure("{ code: 'EADDRINUSE', errno: -4091, syscall: 'listen', address: '::', port: 8765 }"));
        Assert.True(AgentController.IsPortInUseFailure("OSError: [Errno 98] Address already in use"));
        Assert.True(AgentController.IsPortInUseFailure("port already in use"));
        // A NON-port failure is never confused with one.
        Assert.False(AgentController.IsPortInUseFailure("SyntaxError: Unexpected token '}'"));
        Assert.False(AgentController.IsPortInUseFailure("throw new Error(\"boom\")"));
        Assert.False(AgentController.IsPortInUseFailure(null));
    }

    [Fact]
    public void BusyPort_ExtractedFromCommonErrorShapes()
    {
        // node's EADDRINUSE object literal carries `port: 8765`.
        Assert.Equal(8765, AgentController.ExtractBusyPort("... { code: 'EADDRINUSE', errno: -4091, syscall: 'listen', address: '::', port: 8765 }"));
        // "address already in use :::PORT" (node's message line).
        Assert.Equal(8765, AgentController.ExtractBusyPort("listen EADDRINUSE: address already in use :::8765"));
        Assert.Equal(8080, AgentController.ExtractBusyPort("Error: listen EADDRINUSE: address already in use :::8080"));
        // `port=1234` style.
        Assert.Equal(1234, AgentController.ExtractBusyPort("Error: address already in use port=1234"));
        // Non-conflict output (or missing port) never extracts one.
        Assert.Null(AgentController.ExtractBusyPort("SyntaxError: Unexpected token '}'"));
        Assert.Null(AgentController.ExtractBusyPort("OSError: [Errno 98] Address already in use"));
        Assert.Null(AgentController.ExtractBusyPort(null));
    }

    [Fact]
    public void InjectPort_SwapsLiteralBusyPort_AndPrependsShellEnv()
    {
        // The benchmark-22 shape: a PORT-env server with no literal port in the command —
        // the PowerShell prefix injects the free port so process.env.PORT picks it up.
        Assert.Equal("$env:PORT=3123; node benchmark_test_22/server.js",
            AgentController.InjectPortIntoCommand("node benchmark_test_22/server.js", 3123, 8765, "powershell"));
        Assert.Equal("$env:PORT=3123; node server.js",
            AgentController.InjectPortIntoCommand("node server.js", 3123, 8765, "pwsh"));
        // A literal busy port in the command is swapped too (python -m http.server PORT).
        Assert.Equal("PORT=8080 python -m http.server 8080",
            AgentController.InjectPortIntoCommand("python -m http.server 8765", 8080, 8765, "bash"));
        Assert.Equal("PORT=8080 node app.js --port 8080",
            AgentController.InjectPortIntoCommand("node app.js --port 8765", 8080, 8765, "bash"));
        // cmd.exe gets the set-var form.
        Assert.Equal("set PORT=8080&& node server.js",
            AgentController.InjectPortIntoCommand("node server.js", 8080, 8765, "cmd"));
        // Word-boundary: the busy port inside a larger number is NOT swapped.
        Assert.Equal("PORT=8080 node app.js --port 18000",
            AgentController.InjectPortIntoCommand("node app.js --port 18000", 8080, 8765, "bash"));
        // Multiple occurrences all swap.
        Assert.Equal("PORT=7777 python app.py 7777",
            AgentController.InjectPortIntoCommand("python app.py 8765", 7777, 8765, "bash"));
    }

    // ── Deterministic missing-parameter recovery ─────────────────────────────
    // A node server that references an undefined PORT (a recovery edit dropped the
    // `const PORT` line) fails with "ReferenceError: PORT is not defined". The error message
    // itself names the missing parameter, so the command pipeline can define it in the
    // script and re-run — no planner round, no new plan step.

    [Fact]
    public void MissingIdentifier_ExtractedFromReferenceError()
    {
        // The exact benchmark-22 shape — PORT referenced but never defined.
        Assert.Equal("PORT", AgentController.ExtractMissingIdentifier(
            "C:\\...\\server.js:65\nserver.listen(PORT, () => {\n              ^\nReferenceError: PORT is not defined\n    at Object.<anonymous>"));
        Assert.Equal("getOpenPort", AgentController.ExtractMissingIdentifier(
            "ReferenceError: getOpenPort is not defined"));
        Assert.Equal("port", AgentController.ExtractMissingIdentifier(
            "ReferenceError: port is not defined"));
        // Python's NameError shape is recognized too.
        Assert.Equal("PORT", AgentController.ExtractMissingIdentifier(
            "NameError: name 'PORT' is not defined"));
        // Non-missing-identifier failures extract nothing.
        Assert.Null(AgentController.ExtractMissingIdentifier(
            "SyntaxError: Unexpected token '}'"));
        Assert.Null(AgentController.ExtractMissingIdentifier(
            "Error: listen EADDRINUSE: address already in use :::8765"));
        Assert.Null(AgentController.ExtractMissingIdentifier(null));
        Assert.Null(AgentController.ExtractMissingIdentifier(""));
    }

    [Fact]
    public void NodeScriptPath_ResolvedFromCommand()
    {
        // CI runs on Linux; dev runs on Windows. Use the OS-native temp path + Path.Combine
        // for expected values so the assertions hold on both (the old tests hardcoded
        // "C:\Users\Saint\Desktop\benchmark_sandbox\..." which only matched a Windows dev box).
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var sandbox = Path.Combine(root, "benchmark_sandbox");
        var absDir = Path.Combine(sandbox, "benchmark_test_22");
        var absScript = Path.Combine(absDir, "server.js");
        var proj = Path.Combine(root, "proj");
        // Quoted absolute path (the benchmark-22 command shape) — native separators so
        // Path.IsPathRooted/GetFullPath treat it as rooted on the current OS.
        Assert.True(AgentController.TryResolveNodeScriptPath(
            "node \"" + absScript + "\"",
            proj, out var abs));
        Assert.Equal(absScript, abs);
        // Bare relative path resolves against the project root.
        Assert.True(AgentController.TryResolveNodeScriptPath("node server.js", proj, out var rel));
        Assert.Equal(Path.Combine(proj, "server.js"), rel);
        Assert.True(AgentController.TryResolveNodeScriptPath("node benchmark_test_22/server.js", proj, out var rel2));
        Assert.Equal(Path.Combine(proj, "benchmark_test_22", "server.js"), rel2);
        // node -e / flags / non-JS targets are NOT plain script invocations.
        Assert.False(AgentController.TryResolveNodeScriptPath("node -e \"console.log(1)\"", proj, out _));
        Assert.False(AgentController.TryResolveNodeScriptPath("node --watch server.js", proj, out _));
        Assert.False(AgentController.TryResolveNodeScriptPath("python app.py", proj, out _));
        Assert.False(AgentController.TryResolveNodeScriptPath("node --version", proj, out _));
        Assert.False(AgentController.TryResolveNodeScriptPath("", proj, out _));
    }

    [Fact]
    public void PythonScriptPath_ResolvedFromCommand()
    {
        // See NodeScriptPath_ResolvedFromCommand — platform-native paths so CI (Linux) and
        // dev (Windows) both pass. The old hardcoded "C:\Users\Saint\..." only ran on Windows.
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var sandbox = Path.Combine(root, "benchmark_sandbox");
        var absDir = Path.Combine(sandbox, "benchmark_test_22");
        var absScript = Path.Combine(absDir, "server.py");
        var proj = Path.Combine(root, "proj");
        // Quoted absolute path (the benchmark-22 command shape for a python server).
        Assert.True(AgentController.TryResolvePythonScriptPath(
            "python \"" + absScript + "\"",
            proj, out var abs));
        Assert.Equal(absScript, abs);
        // Bare relative path resolves against the project root.
        Assert.True(AgentController.TryResolvePythonScriptPath("python app.py", proj, out var rel));
        Assert.Equal(Path.Combine(proj, "app.py"), rel);
        Assert.True(AgentController.TryResolvePythonScriptPath("python3 benchmark_test_22/server.py", proj, out var rel2));
        Assert.Equal(Path.Combine(proj, "benchmark_test_22", "server.py"), rel2);
        // python -c / flags / non-PY targets are NOT plain script invocations.
        Assert.False(AgentController.TryResolvePythonScriptPath("python -c \"print(1)\"", proj, out _));
        Assert.False(AgentController.TryResolvePythonScriptPath("python -m flask run", proj, out _));
        Assert.False(AgentController.TryResolvePythonScriptPath("node server.js", proj, out _));
        Assert.False(AgentController.TryResolvePythonScriptPath("python --version", proj, out _));
        Assert.False(AgentController.TryResolvePythonScriptPath("", proj, out _));
    }

    [Fact]
    public void MissingPort_PatchedIntoScript_Once()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver_portpatch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // With 'use strict': the patch lands right after the directive.
            var withStrict = Path.Combine(dir, "strict.js");
            File.WriteAllText(withStrict, "\"use strict\";\nconst http = require('http');\nserver.listen(PORT, () => {});\n");
            Assert.True(AgentController.PatchMissingPortIntoScript(withStrict));
            var patched = File.ReadAllText(withStrict);
            Assert.Contains("const PORT = process.env.PORT || 8765;", patched);
            Assert.StartsWith("\"use strict\";\nconst PORT =", patched);
            // Patching again is a NO-OP (PORT is already declared — never double-inject).
            Assert.False(AgentController.PatchMissingPortIntoScript(withStrict));
            Assert.Single(Regex.Matches(File.ReadAllText(withStrict), "const PORT ="));

            // No 'use strict': the patch lands at the top.
            var noStrict = Path.Combine(dir, "plain.js");
            File.WriteAllText(noStrict, "const http = require('http');\nserver.listen(PORT, () => {});\n");
            Assert.True(AgentController.PatchMissingPortIntoScript(noStrict));
            Assert.StartsWith("const PORT = process.env.PORT || 8765;", File.ReadAllText(noStrict));

            // Missing/unreadable file never patches.
            Assert.False(AgentController.PatchMissingPortIntoScript(Path.Combine(dir, "nope.js")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── Deterministic missing-dependency recovery ─────────────────────────────
    // `node server.js` fails with "Cannot find module 'express'" (the benchmark-22 live
    // failure — the agent wrote express code without ever running `npm install`). The
    // command pipeline must extract the module name, confirm it is not a node builtin
    // (builtins can never be npm-installed), and deterministically run `npm install`
    // before re-running — no planner round.

    [Fact]
    public void MissingModule_ExtractsNodeAndPythonModuleNames()
    {
        Assert.Equal("express",
            AgentController.ExtractMissingModule("Error: Cannot find module 'express' Require stack: - C:\\app.js"));
        Assert.Equal("flask",
            AgentController.ExtractMissingModule("ModuleNotFoundError: No module named 'flask'"));
        // Single-quoted module names are matched; double quotes and other failures are not.
        Assert.Equal("morgan",
            AgentController.ExtractMissingModule("node:internal/modules/cjs/loader:1401 Cannot find module 'morgan'"));
        Assert.Null(AgentController.ExtractMissingModule("SyntaxError: Unexpected token '}'"));
        Assert.Null(AgentController.ExtractMissingModule("ReferenceError: PORT is not defined"));
        Assert.Null(AgentController.ExtractMissingModule(null));
        Assert.Null(AgentController.ExtractMissingModule(""));
    }

    [Fact]
    public void MissingModule_NodeBuiltins_AreNotInstallable()
    {
        // Builtins (and node: prefixed) can never come from npm — a missing-builtin claim is
        // a different bug and must reach the planner, not an npm install.
        Assert.True(AgentController.IsNodeBuiltinModule("fs"));
        Assert.True(AgentController.IsNodeBuiltinModule("http"));
        Assert.True(AgentController.IsNodeBuiltinModule("path"));
        Assert.True(AgentController.IsNodeBuiltinModule("node:test"));
        Assert.True(AgentController.IsNodeBuiltinModule("zlib"));
        // Third-party modules ARE installable.
        Assert.False(AgentController.IsNodeBuiltinModule("express"));
        Assert.False(AgentController.IsNodeBuiltinModule("morgan"));
        Assert.False(AgentController.IsNodeBuiltinModule(""));
    }

    [Fact]
    public void MissingModule_PythonStdlib_AreNotInstallable()
    {
        // Stdlib modules can never come from pip — a missing-stdlib claim is a different bug
        // and must reach the planner, not a pip install.
        Assert.True(AgentController.IsPythonStdlibModule("os"));
        Assert.True(AgentController.IsPythonStdlibModule("sys"));
        Assert.True(AgentController.IsPythonStdlibModule("json"));
        Assert.True(AgentController.IsPythonStdlibModule("http"));
        Assert.True(AgentController.IsPythonStdlibModule("dataclasses"));
        Assert.True(AgentController.IsPythonStdlibModule("pathlib"));
        // Third-party modules ARE installable.
        Assert.False(AgentController.IsPythonStdlibModule("flask"));
        Assert.False(AgentController.IsPythonStdlibModule("fastapi"));
        Assert.False(AgentController.IsPythonStdlibModule("requests"));
        Assert.False(AgentController.IsPythonStdlibModule(""));
    }
}
