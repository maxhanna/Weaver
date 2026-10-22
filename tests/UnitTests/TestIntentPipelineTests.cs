using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Weaver.Controllers;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the strict-test-intent pipeline routing: a prompt that is STRICTLY a live
/// web-app test ("test the kanban board", "verify the calendar page loads") must be
/// handled by the deterministic live-test pipeline inside OrchestrateCore — spin up the
/// project's server, inspect the named section, verify — WITHOUT a single LLM call.
/// The controller is built exactly like the other pipeline tests (reflection, fake
/// services, hermetically "reachable" LLM) and the browser factory is nulled so the
/// run uses the HTTP fallback and never touches a real browser.
/// </summary>
public class TestIntentPipelineTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "weaver-pipe-" + Guid.NewGuid().ToString("N"));
    private readonly string _projectRoot;
    private readonly DatabaseService _db;
    private readonly FakeMessageHandler _clientFactory;

    public TestIntentPipelineTests()
    {
        Directory.CreateDirectory(_base);
        _projectRoot = Path.Combine(_base, "site");
        Directory.CreateDirectory(_projectRoot);
        File.WriteAllText(Path.Combine(_projectRoot, "index.html"), """
            <!DOCTYPE html>
            <html>
            <head><title>Fixture</title></head>
            <body>
              <nav><a href="/kanban.html">Kanban Board</a></nav>
              <h1>Fixture Home</h1>
              <p>Welcome.</p>
            </body>
            </html>
            """);
        File.WriteAllText(Path.Combine(_projectRoot, "kanban.html"), """
            <!DOCTYPE html>
            <html>
            <head><title>Kanban Board</title></head>
            <body><h1>Kanban Board</h1><p>Cards live here.</p></body>
            </html>
            """);
        _db = new DatabaseService(
            Path.Combine(_base, "data", "weaver.db"),
            Path.Combine(_base, "data"),
            Path.Combine(_base, "data", "weaverconfig.json"));
        // Any LLM call here fails the test loudly — the pipeline must not need one.
        _clientFactory = new FakeMessageHandler(shouldFail: true);
    }

    public void Dispose()
    {
        _clientFactory.Dispose();
        try { Directory.Delete(_base, true); } catch { }
    }

    [Fact]
    public async Task StrictTestIntentPrompt_RunsLiveTestPipeline_ZeroLlmCalls()
    {
        var controller = BuildController();
        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, "test the kanban board");

        Assert.True(complete, $"pipeline must complete — plan summary: {plan?.Summary}");
        Assert.NotNull(plan);
        Assert.Contains("Live web test passed", plan!.Summary);

        var steps = allSteps.OfType<Dictionary<string, object?>>().ToList();
        Assert.Equal(new[] { "server", "browse", "verify" }, steps.Select(s => s.GetValueOrDefault("type")?.ToString()).ToArray());
        Assert.All(steps, s => Assert.Equal("done", s.GetValueOrDefault("status")?.ToString()));
        Assert.Contains(steps, s => s.GetValueOrDefault("url")?.ToString()?.StartsWith("http://127.0.0.1:") == true);
        Assert.Contains(steps, s => s.GetValueOrDefault("section")?.ToString() == "Kanban Board");
        Assert.Contains(steps, s => s.GetValueOrDefault("type")?.ToString() == "verify");

        // No plan steps were produced (the pipeline returns its own plan — no LLM planning).
        Assert.Empty(plan!.Plan);
    }

    [Fact]
    public async Task StrictTestIntentPrompt_FailingVerdict_CompletesUnsuccessfully()
    {
        // Point at a site that has nothing matching "quantum flux capacitor".
        var controller = BuildController();
        var (_, plan, complete) = await InvokeOrchestrate(controller, "test the quantum flux capacitor");

        Assert.False(complete);
        Assert.Contains("Live web test failed", plan!.Summary);
    }

    [Fact]
    public async Task StrictTestIntentPrompt_ApiEndpoint_ApiPipeline()
    {
        var controller = BuildController();
        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, "test the /api/status endpoint");

        Assert.True(complete, plan?.Summary);
        Assert.Contains("Live web test passed", plan!.Summary);
        var steps = allSteps.OfType<Dictionary<string, object?>>().ToList();
        Assert.Contains(steps, s => s.GetValueOrDefault("type")?.ToString() == "verify" &&
                                    s.GetValueOrDefault("description")?.ToString()?.Contains("PASSED") == true);
    }

    [Fact]
    public async Task EditIntentPrompt_DoesNotShortCircuit()
    {
        // "fix the kanban board" is NOT strict test intent → the live-test pipeline must
        // not run; the prompt goes down the normal (LLM) path, which here has no LLM, so
        // the run either completes unsuccessfully or throws — never a server step.
        var controller = BuildController();
        var steps = new List<object>();
        AgentPlan? plan = null;
        try
        {
            var result = await InvokeOrchestrate(controller, "fix the kanban board");
            steps = result.allSteps;
            plan = result.plan;
        }
        catch (TargetInvocationException)
        {
            // Normal-path planning crashed (no LLM) — equally "not short-circuited".
        }

        Assert.DoesNotContain(steps.OfType<Dictionary<string, object?>>(),
            s => s.GetValueOrDefault("type")?.ToString() is "server" or "browser_test");
        Assert.DoesNotContain(steps.OfType<Dictionary<string, object?>>(),
            s => s.GetValueOrDefault("description")?.ToString()?.Contains("Live web test", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(plan?.Summary ?? "", "Live web test", StringComparison.OrdinalIgnoreCase);
    }

    // ── harness (mirrors InterleavedPipelineIntegrationTests) ────────────────

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
        SetField(controller, "_boardData", new BoardDataService(_db, NullLogger<BoardDataService>.Instance));
        SetField(controller, "_emailService", new EmailService(new ConfigFileService(_db)));
        SetField(controller, "_push", new PushNotificationService(_db));
        SetField(controller, "_editKnowledge", new EditKnowledgeService(_db));
        SetField(controller, "_runtimeProbe", new RuntimeProbeService((_, _, _) => (-1, "", "")));
        SetStaticField("_nextConnectivityCheck", DateTime.UtcNow.AddMinutes(5));
        SetField(controller, "_lastConnectionCheckResult", true);
        // Deterministic live-test service: HTTP fallback only, real launcher.
        SetField(controller, "_browserTestService", new BrowserAutomationService
        {
            Launcher = new ServerLauncherService(),
            BrowserFactory = null,
            ServerTimeout = TimeSpan.FromSeconds(60)
        });
        return controller;
    }

    private async Task<(List<object> allSteps, AgentPlan? plan, bool complete)> InvokeOrchestrate(
        AgentController controller, string prompt)
    {
        var method = typeof(AgentController).GetMethod("Orchestrate", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Orchestrate not found");
        var task = (Task<(List<object> allSteps, AgentPlan? plan, bool complete)>)method.Invoke(controller, new object?[]
        {
            prompt, _projectRoot, /*emitSse*/ false, CancellationToken.None,
            /*attachedFiles*/ new List<string>(), /*skipContextReview*/ false, /*steeringContext*/ null,
            /*skipQualityCheck*/ false, /*existingPlan*/ null, /*completedStepIndices*/ null, /*cardId*/ null,
            /*createTests*/ false, /*buildCommands*/ null, /*strictVerifier*/ null, /*webResults*/ null
        })!;
        return await task;
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

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public FakeWebHostEnvironment(string contentRoot) => ContentRootPath = contentRoot;
        public string ApplicationName { get; set; } = "Weaver";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; }
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    /// <summary>An <see cref="IHttpClientFactory"/> that answers GET probes like the real
    /// network (200) but fails every LLM POST loudly — the live-test pipeline never POSTs,
    /// so any POST proves a leak.</summary>
    private sealed class FakeMessageHandler : IHttpClientFactory, IDisposable
    {
        private readonly bool _shouldFail;
        public FakeMessageHandler(bool shouldFail) => _shouldFail = shouldFail;
        public HttpClient CreateClient(string name) => new(new Handler(_shouldFail));
        public HttpClient CreateClient() => CreateClient("default");
        public void Dispose() { }

        private sealed class Handler : HttpMessageHandler
        {
            private readonly bool _shouldFail;
            public Handler(bool shouldFail) => _shouldFail = shouldFail;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (request.Method == HttpMethod.Get)
                    return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent("{}")
                    });
                if (_shouldFail)
                    throw new InvalidOperationException("Test leaked an LLM call: " + request.RequestUri);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}")
                });
            }
        }
    }
}