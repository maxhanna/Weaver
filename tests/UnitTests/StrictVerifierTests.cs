using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Weaver;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the StrictVerifier (hard-gate) toggle added to <see cref="AgentRequest.StrictVerifier"/>
/// and threaded through to <see cref="AgentController"/>'s PostExecuteVerify. Three states:
///   • null  (legacy)   — deterministic issues still force complete=false, but the LLM
///                        verification round AND the post-verify repair loop still run.
///   • true  (strict)   — a deterministic finding is terminal: PostExecuteVerify returns
///                        complete=false IMMEDIATELY (no LLM round), and the outer repair
///                        loop is skipped (the "Strict verifier hard-gate:" details sentinel
///                        is what the outer guard keys on).
///   • false (relaxed)  — deterministic issues are published as ground truth but are
///                        NON-blocking: the LLM round runs and a clean LLM verdict completes
///                        the run even when a deterministic check fired.
///
/// Coverage locked in here:
///   1. Strict + deterministic issue → complete=false, LLM NEVER called, details carries
///      the "Strict verifier hard-gate:" sentinel (the outer repair-loop guard's key).
///   2. Strict + no deterministic issue → behaves like legacy (LLM round runs; a clean LLM
///      verdict completes the run).
///   3. Legacy (null) + deterministic issue → complete=false AND the LLM round WAS called
///      (the regression guard: strict must NOT silently change the legacy path).
///   4. Relaxed (false) + deterministic issue + clean LLM verdict → complete=true; the
///      deterministic issue is recorded as ground truth but does not fail the run.
///   5. AgentRequest round-trips StrictVerifier through JSON (backward-compat: a payload
///      omitting the field deserializes to null).
/// </summary>
public class StrictVerifierTests : IDisposable
{
    private readonly string _base;
    private readonly string _projectRoot;
    private readonly DatabaseService _db;
    private readonly BoardDataService _boardData;
    private readonly RecordingClientFactory _clientFactory;

    public StrictVerifierTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "weaver-strict-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_base);
        _projectRoot = Path.Combine(_base, "proj");
        Directory.CreateDirectory(_projectRoot);
        _db = new DatabaseService(Path.Combine(_base, "weaver.db"), _base, Path.Combine(_base, "weaverconfig.json"));
        _boardData = new BoardDataService(_db, NullLogger<BoardDataService>.Instance);
        _clientFactory = new RecordingClientFactory();
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, true); } catch { }
    }

    [Fact]
    public async Task StrictMode_DeterministicIssue_FailsImmediatelyWithoutLlmRound()
    {
        // A "rename every occurrence" task where the edit only replaced ONE of N occurrences
        // fires the deterministic rename-all check (old name still on disk). Strict mode must
        // return complete=false WITHOUT invoking the LLM verifier, and the details must carry
        // the "Strict verifier hard-gate:" sentinel the outer repair-loop guard keys on.
        const string cardId = "strict-fail";
        var file = Path.Combine(_projectRoot, "worker.ts");
        const string oldName = "MAX_RETRIES";
        const string newName = "MAX_ATTEMPTS";
        // Three occurrences on disk; the edit only touches the first → partial rename.
        File.WriteAllText(file, $"const {oldName} = 5;\nfunction a() {{ return {oldName}; }}\nfunction b() {{ return {oldName}; }}\n");

        var results = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "edit", ["status"] = "done", ["path"] = file,
                ["oldString"] = $"const {oldName} = 5;",
                ["newString"] = $"const {newName} = 5;"
            }
        };

        var (complete, details, confirmedIssues, speculative, groundTruth) =
            await InvokePostExecuteVerify($"Rename every occurrence of {oldName} to {newName}", results, cardId, strictVerifier: true);

        Assert.False(complete, "strict mode must fail the run on a deterministic issue");
        // The hard-gate sentinel — the outer repair-loop guard in ExecuteStreamCore keys on this prefix.
        Assert.StartsWith("Strict verifier hard-gate:", details, StringComparison.Ordinal);
        // The deterministic rename-all finding is in the confirmed issues (drives the verdict).
        Assert.NotEmpty(confirmedIssues);
        // No speculative issues from the hard-gate early-return.
        Assert.Empty(speculative);
        // Ground truth carries the deterministic issue so the card shows the exact defect.
        Assert.Contains(groundTruth, g => g.Contains(oldName) || g.Contains("Rename-all") || g.Contains("rename"));
        // CRITICAL: the LLM verifier round never ran (the whole point of the hard-gate).
        Assert.Empty(_clientFactory.VerifierCalls);
    }

    [Fact]
    public async Task StrictMode_NoDeterministicIssue_BehavesLikeLegacy()
    {
        // Strict mode with NO deterministic issue must fall through to the LLM verifier round
        // (the hard-gate only fires when deterministicIssues.Count > 0). A clean LLM verdict
        // completes the run — strict mode is not a blanket "always fail" switch.
        const string cardId = "strict-clean";
        var file = Path.Combine(_projectRoot, "svc.ts");
        File.WriteAllText(file, "export function hello() { return 'hi'; }\n");

        var results = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "edit", ["status"] = "done", ["path"] = file,
                ["oldString"] = "return 'hi';",
                ["newString"] = "return 'hello';"
            }
        };

        _clientFactory.VerifierReply = """{"complete": true, "reason": "clean", "issues": []}""";

        var (complete, details, _, _, _) =
            await InvokePostExecuteVerify("Change the greeting from hi to hello", results, cardId, strictVerifier: true);

        Assert.True(complete, "strict mode with no deterministic issue + clean LLM verdict must complete");
        // The LLM verifier round DID run (strict mode only short-circuits on a deterministic issue).
        Assert.NotEmpty(_clientFactory.VerifierCalls);
    }

    [Fact]
    public async Task LegacyMode_DeterministicIssue_StillInvokesLlmRound()
    {
        // Regression guard: legacy mode (null) must keep the historical behavior — a
        // deterministic issue forces complete=false, but the LLM verification round STILL
        // runs (strict mode must not silently change the legacy path). The LLM is scripted
        // to echo the deterministic failure; the verdict must be false and the LLM called.
        const string cardId = "legacy-fail";
        var file = Path.Combine(_projectRoot, "worker.ts");
        const string oldName = "MAX_RETRIES";
        const string newName = "MAX_ATTEMPTS";
        File.WriteAllText(file, $"const {oldName} = 5;\nfunction a() {{ return {oldName}; }}\nfunction b() {{ return {oldName}; }}\n");

        var results = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "edit", ["status"] = "done", ["path"] = file,
                ["oldString"] = $"const {oldName} = 5;",
                ["newString"] = $"const {newName} = 5;"
            }
        };

        _clientFactory.VerifierReply = """{"complete": false, "reason": "rename incomplete", "issues": [{"type": "CONFIRMED", "text": "partial rename"}]}""";

        var (complete, details, _, _, _) =
            await InvokePostExecuteVerify($"Rename every occurrence of {oldName} to {newName}", results, cardId, strictVerifier: null);

        Assert.False(complete, "legacy mode still fails on a deterministic issue");
        // Legacy mode does NOT use the hard-gate sentinel.
        Assert.False(details.StartsWith("Strict verifier hard-gate:", StringComparison.Ordinal),
            "legacy mode must not emit the hard-gate sentinel");
        // The LLM verifier round DID run (legacy behavior preserved).
        Assert.NotEmpty(_clientFactory.VerifierCalls);
    }

    [Fact]
    public async Task RelaxedMode_DeterministicIssue_CleanLlmVerdict_Completes()
    {
        // Relaxed mode (false): a deterministic issue is published as ground truth but is
        // NON-blocking. The LLM verifier round runs, and a clean LLM verdict completes the
        // run despite the deterministic finding. This is the explicit opt-out of the gate.
        const string cardId = "relaxed-clean";
        var file = Path.Combine(_projectRoot, "worker.ts");
        const string oldName = "MAX_RETRIES";
        const string newName = "MAX_ATTEMPTS";
        File.WriteAllText(file, $"const {oldName} = 5;\nfunction a() {{ return {oldName}; }}\nfunction b() {{ return {oldName}; }}\n");

        var results = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "edit", ["status"] = "done", ["path"] = file,
                ["oldString"] = $"const {oldName} = 5;",
                ["newString"] = $"const {newName} = 5;"
            }
        };

        _clientFactory.VerifierReply = """{"complete": true, "reason": "clean", "issues": []}""";

        var (complete, details, confirmedIssues, _, groundTruth) =
            await InvokePostExecuteVerify($"Rename every occurrence of {oldName} to {newName}", results, cardId, strictVerifier: false);

        Assert.True(complete, "relaxed mode + clean LLM verdict must complete despite the deterministic issue");
        // The LLM round ran (relaxed mode does not short-circuit).
        Assert.NotEmpty(_clientFactory.VerifierCalls);
        // The deterministic issue did NOT seed the confirmed issues (non-blocking).
        Assert.Empty(confirmedIssues);
        // The deterministic issue IS recorded as ground truth (informational).
        Assert.Contains(groundTruth, g => g.Contains(oldName) || g.Contains("Rename-all") || g.Contains("rename"));
    }

    [Fact]
    public void AgentRequest_StrictVerifier_OmittedFromJson_DeserializesToNull()
    {
        // Backward-compat: a payload that does not include StrictVerifier (every existing card
        // and every existing caller before this feature) must deserialize to null → legacy.
        var json = """{"Prompt":"x","Project":"y"}""";
        var req = JsonSerializer.Deserialize<AgentRequest>(json)!;
        Assert.Null(req.StrictVerifier);
    }

    [Fact]
    public void AgentRequest_StrictViewer_RoundTripsThroughJson()
    {
        var req = new AgentRequest { Prompt = "p", StrictVerifier = true };
        var json = JsonSerializer.Serialize(req);
        var back = JsonSerializer.Deserialize<AgentRequest>(json)!;
        Assert.True(back.StrictVerifier);
    }

    [Fact]
    public void FrontendConfig_DefaultStrictVerifier_OmittedFromJson_DeserializesToNull()
    {
        // Backward-compat: an existing weaver_config row without defaultStrictVerifier
        // deserializes to null → legacy default.
        var json = """{"defaultProject":"x"}""";
        var cfg = JsonSerializer.Deserialize<FrontendConfig>(json)!;
        Assert.Null(cfg.defaultStrictVerifier);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────

    private async Task<(bool complete, string details, List<string> confirmedIssues, List<string> speculativeIssues, List<string> groundTruth)> InvokePostExecuteVerify(
        string prompt, List<object> allResults, string? cardId, bool? strictVerifier)
    {
        var controller = BuildController();
        var method = typeof(AgentController).GetMethod("PostExecuteVerify", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("PostExecuteVerify not found");
        var task = (Task<(bool complete, string details, List<string> confirmedIssues, List<string> speculativeIssues, List<string> groundTruth)>)method.Invoke(
            controller, new object?[]
            {
                prompt, _projectRoot, /*emitSse*/ false, allResults, CancellationToken.None,
                /*discoveryContext*/ null, /*atomicStepEstimate*/ null, /*preEditSnapshots*/ null,
                /*cardId*/ cardId, /*steeringContext*/ null, /*strictVerifier*/ strictVerifier
            })!;
        return await task;
    }

    private AgentController BuildController()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Editor:WorkspaceRoot"] = _base,
                ["Editor:DisableLLMRetries"] = "true"
            })
            .Build();
        var controller = (AgentController)RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        SetField(controller, "_clientFactory", _clientFactory);
        SetField(controller, "_config", config);
        SetField(controller, "_env", new FakeWebHostEnvironment(_projectRoot));
        SetField(controller, "_db", _db);
        SetField(controller, "_configFile", new ConfigFileService(_db));
        SetField(controller, "_terminal", new TerminalService(new ConfigFileService(_db)));
        SetField(controller, "_fileHints", new FileHintsManager(_db));
        SetField(controller, "_boardData", _boardData);
        SetField(controller, "_emailService", new EmailService(new ConfigFileService(_db)));
        SetField(controller, "_push", new PushNotificationService(_db));
        SetField(controller, "_editKnowledge", new EditKnowledgeService(_db));
        SetField(controller, "_runtimeProbe", new RuntimeProbeService((_, _, _) => (-1, "", "")));
        SetStaticField("_nextConnectivityCheck", DateTime.UtcNow.AddMinutes(5));
        SetField(controller, "_lastConnectionCheckResult", true);
        return controller;
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field {name} not found");
        field.SetValue(target, value);
    }

    private static void SetStaticField(string name, object value)
    {
        var field = typeof(AgentController).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Static field {name} not found");
        field.SetValue(null, value);
    }

    /// <summary>
    /// Scripted fake LLM. Replies to the verifier round with <see cref="VerifierReply"/> (a clean
    /// complete=true/complete=false JSON string the test sets per scenario). Records every
    /// verifier call so the strict-mode test can assert the LLM round was NEVER invoked.
    /// Any other route (planner/repair) returns empty — these tests only exercise PostExecuteVerify.
    /// </summary>
    private sealed class RecordingClientFactory : IHttpClientFactory, IDisposable
    {
        public string? VerifierReply { get; set; }
        public readonly List<string> VerifierCalls = new();
        private readonly HttpMessageHandlerStub _handler;

        public RecordingClientFactory()
        {
            _handler = new HttpMessageHandlerStub(this);
        }

        public HttpClient CreateClient(string name = "") => new(_handler);

        public void Dispose() => _handler.Dispose();

        private sealed class HttpMessageHandlerStub : HttpMessageHandler
        {
            private readonly RecordingClientFactory _owner;
            public HttpMessageHandlerStub(RecordingClientFactory owner) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                // Record every call so the strict-mode test can assert the LLM round was never
                // invoked when the hard-gate tripped. The verifier system prompt is distinctive
                // ("meticulous code reviewer"); we don't need to parse it — any call here means
                // the LLM round ran.
                _owner.VerifierCalls.Add(request.RequestUri?.ToString() ?? "?");
                var reply = _owner.VerifierReply ?? """{"complete": true, "reason": "", "issues": []}""";
                // CallLlmRawStreaming expects an OpenAI-style streaming response (SSE): the
                // reply text is the delta.content of a single choice, terminated by [DONE].
                // A plain JSON body is NOT parsed — it would surface as an empty verifier reply.
                var data = JsonSerializer.Serialize(new
                {
                    choices = new[] { new { delta = new { content = reply }, finish_reason = "stop" } }
                });
                var body = $"data: {data}\n\n\ndata: [DONE]\n";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
                });
            }
        }
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public FakeWebHostEnvironment(string contentRoot) => ContentRootPath = contentRoot;
        public string ApplicationName { get; set; } = "Weaver";
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
