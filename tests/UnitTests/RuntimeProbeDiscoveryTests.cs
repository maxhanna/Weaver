using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Xunit;
using Weaver;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the discovery-phase runtime probe: RunBootstrapDiscovery (the real method) must
/// surface a "RUNTIME AVAILABILITY" section into the discovery context so the planner sees
/// which runtimes exist on the machine before choosing a language (the benchmark-4 "write a
/// server" case), and the probe result must be cached PER PROJECT in the DB (24h TTL) so a
/// second discovery on the same project reuses the cache instead of re-probing — measured by
/// counting calls into the fake probe service.
/// </summary>
public class RuntimeProbeDiscoveryTests : IDisposable
{
    private readonly string _base;
    private readonly string _projectRoot;
    private readonly DatabaseService _db;

    public RuntimeProbeDiscoveryTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "weaver_runtime_probe_" + Guid.NewGuid().ToString("N"));
        _projectRoot = Path.Combine(_base, "proj");
        Directory.CreateDirectory(_projectRoot);
        File.WriteAllText(Path.Combine(_projectRoot, "readme.md"), "# project\n");
        _db = new DatabaseService(
            Path.Combine(_base, "data", "weaver.db"),
            Path.Combine(_base, "data"),
            Path.Combine(_base, "data", "weaverconfig.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, true); } catch { }
    }

    private sealed class CountingProbeService : RuntimeProbeService
    {
        public int ProbeCalls;

        public CountingProbeService()
            : base((_, _, _) => (-1, "", ""))
        {
        }

        public override List<RuntimeInfo> ProbeAll()
        {
            ProbeCalls++;
            return new List<RuntimeInfo>
            {
                new("python", "Python 3.12.4"),
                new("node", "v22.5.1"),
                new("go", null)
            };
        }
    }

    private AgentController BuildController(CountingProbeService probe)
    {
        var controller = (AgentController)RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        SetField(controller, "_clientFactory", new ThrowingClientFactory());
        SetField(controller, "_config", new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Editor:WorkspaceRoot"] = _base,
                ["Editor:DisableLLMRetries"] = "true"
            })
            .Build());
        SetField(controller, "_db", _db);
        SetField(controller, "_configFile", new ConfigFileService(_db));
        SetField(controller, "_fileHints", new FileHintsManager(_db));
        SetField(controller, "_editKnowledge", new EditKnowledgeService(_db));
        SetField(controller, "_runtimeProbe", probe);
        return controller;
    }

    private static async Task<(string discoveryText, List<object> steps)> InvokeRunBootstrapDiscovery(
        AgentController controller, string prompt, string projectRoot)
    {
        var method = typeof(AgentController).GetMethod(
            "RunBootstrapDiscovery", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("RunBootstrapDiscovery not found");
        var task = (Task<(string, List<object>)>)method.Invoke(controller, new object?[]
        {
            prompt, projectRoot, /*emitSse*/ false, /*attachedFiles*/ null, CancellationToken.None
        })!;
        return await task;
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field {name} not found");
        field.SetValue(target, value);
    }

    private sealed class ThrowingClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("no LLM calls allowed in discovery test");
        public HttpClient CreateClient() => CreateClient("default");
    }

    [Fact]
    public async Task Discovery_EmitsRuntimeAvailability_WithFakeProbeResults()
    {
        var probe = new CountingProbeService();
        var controller = BuildController(probe);
        var prompt = "Create a simple HTTP server on port 9969 serving index.html at / with basic content plus a /api/hello endpoint that returns JSON {\"message\": \"Hello\"}.";

        var (discoveryText, _) = await InvokeRunBootstrapDiscovery(controller, prompt, _projectRoot);

        Assert.Contains("RUNTIME AVAILABILITY", discoveryText);
        Assert.Contains("python (Python 3.12.4)", discoveryText);
        Assert.Contains("node (v22.5.1)", discoveryText);
        Assert.Contains("NOT available: go", discoveryText);
    }

    [Fact]
    public async Task Discovery_ProbesOnceThenReusesPerProjectDbCache()
    {
        var probe = new CountingProbeService();
        var controller = BuildController(probe);
        var prompt = "Create a simple HTTP server on port 9969.";

        // First run: no cache → probe runs (once) and the result is stored per project.
        var (first, _) = await InvokeRunBootstrapDiscovery(controller, prompt, _projectRoot);
        Assert.Equal(1, probe.ProbeCalls);
        Assert.Contains("RUNTIME AVAILABILITY", first);
        Assert.NotNull(_db.GetRuntimeProbe("proj"));

        // Second run on the SAME project: the DB cache is fresh (< 24h) → no new probe.
        var (second, _) = await InvokeRunBootstrapDiscovery(controller, prompt, _projectRoot);
        Assert.Equal(1, probe.ProbeCalls);
        Assert.Contains("RUNTIME AVAILABILITY", second);
        Assert.Contains("python (Python 3.12.4)", second);
    }

    [Fact]
    public async Task Discovery_StaleVersionlessCache_IsReProbedFresh()
    {
        var probe = new CountingProbeService();
        var controller = BuildController(probe);
        var prompt = "Create a simple HTTP server on port 9969.";

        // Simulate a cache written BEFORE the probe-format versioning — the pre-fix Windows
        // npm/npx .cmd shim bug wrote blobs with NO version field claiming npm/npx missing,
        // and the 24h TTL would otherwise serve that lie for a full day. A version-less (or
        // wrong-version) blob must be treated as stale and re-probed.
        _db.SetRuntimeProbe("proj", "{\"probedAtUtc\":\"" + DateTime.UtcNow.AddMinutes(-5).ToString("o") +
            "\",\"probes\":[{\"name\":\"python\",\"version\":\"Python 3.12.4\"},{\"name\":\"node\",\"version\":\"v22.5.1\"},{\"name\":\"npm\"},{\"name\":\"npx\"}]}");

        var (discovery, _) = await InvokeRunBootstrapDiscovery(controller, prompt, _projectRoot);

        // The stale blob was discarded → a fresh probe ran, and the corrected availability
        // (npm/npx present, per the fake probe's node/python results) surfaces.
        Assert.Equal(1, probe.ProbeCalls);
        Assert.Contains("RUNTIME AVAILABILITY", discovery);
        Assert.Contains("python (Python 3.12.4)", discovery);

        // The re-probe wrote a versioned blob back — the next run reuses it (no new probe).
        var cached = _db.GetRuntimeProbe("proj");
        Assert.NotNull(cached);
        Assert.Contains("\"version\":2", cached);
        var (second, _) = await InvokeRunBootstrapDiscovery(controller, prompt, _projectRoot);
        Assert.Equal(1, probe.ProbeCalls);
        Assert.Contains("RUNTIME AVAILABILITY", second);
    }
}
