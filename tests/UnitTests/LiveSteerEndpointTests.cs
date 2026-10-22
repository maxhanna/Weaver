using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Weaver;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Tests for LIVE steering: POST api/agent/steer while a card is RUNNING, as opposed to the
/// run-start steeringContext that is fixed when the run begins. The post's lesson — "context
/// failures only show up at turn N when the user references something from turn 2" — applies
/// to the operator too: by the time a human sees a wrong turn-2 proposal, the run is already
/// mid-flight, so the correction must be able to reach the planner on the NEXT turn.
///
/// Coverage locked in here:
///   • The endpoint stores the message and reports whether the card was actually executing
///   • Repeated steers overwrite (latest wins), missing cardId/message → BadRequest
///   • MID-RUN injection: a steer posted while turn 2 is being planned is drained before
///     turn 3 — it appears in the turn-3 planner prompt (and ONLY from turn 3 on), and the
///     override edit lands, undoing what turn 2 proposed
///   • The steer is consumed once (never re-applied or duplicated on later turns) and is
///     dropped when the run ends so it can't leak into the card's next run
/// </summary>
public class LiveSteerEndpointTests : IDisposable
{
    private const string DemoTsRel = "maxhanna.client/src/app/demo/demo.component.ts";

    private const string DemoComponentTs = """
        export class DemoComponent {
          title = 'demo';
          items: string[] = [];
          constructor() { }
        }
        """;

    private const string CtorLine = "  constructor() { }";
    private const string GetItemsPlain = "  getItems() { return this.items; }";
    private const string GetItemsWithCopy = "  getItems() { return this.items.slice(); }";
    private const string GetItemsCountLine = "  getItemsCount() { return this.items.length; }";

    private const string RunStartSteering = "Follow the user's task exactly.";
    private const string LiveSteerMessage =
        "IMPORTANT USER UPDATE: I only asked for getItems(). Remove the getItemsCount() helper " +
        "you just proposed — it is unwanted scope. getItems() must return a copy (this.items.slice()).";

    private readonly string _base;
    private readonly string _projectRoot;
    private readonly DatabaseService _db;
    private readonly BoardDataService _boardData;
    private readonly ScriptedClientFactory _clientFactory = new();

    public LiveSteerEndpointTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "weaver_livesteer_" + Guid.NewGuid().ToString("N"));
        _projectRoot = Path.Combine(_base, "proj");
        Directory.CreateDirectory(_projectRoot);
        Directory.CreateDirectory(Path.Combine(_base, "data"));

        var tsPath = Path.Combine(_projectRoot, DemoTsRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(tsPath)!);
        File.WriteAllText(tsPath, DemoComponentTs);

        _db = new DatabaseService(
            Path.Combine(_base, "data", "weaver.db"),
            Path.Combine(_base, "data"),
            Path.Combine(_base, "data", "weaverconfig.json"));
        _boardData = new BoardDataService(_db, NullLogger<BoardDataService>.Instance);
    }

    public void Dispose()
    {
        _clientFactory.Dispose();
        LiveSteer().Clear();
        ExecutingCards().Clear();
        try { Directory.Delete(_base, true); } catch { }
    }

    // ── The live steer endpoint (deterministic half — the LLM half can't be unit-tested) ──

    [Fact]
    public void Steer_StoresMessage_AndReportsActiveForExecutingCard()
    {
        var controller = MakeController();
        ExecutingCards()["card:steer-1"] = DateTime.UtcNow.Ticks; // run in flight

        var result = controller.SteerRun(new SteerRequest { CardId = "steer-1", Message = LiveSteerMessage });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("\"active\":true", JsonSerializer.Serialize(ok.Value));
        Assert.Equal(LiveSteerMessage, LiveSteer()["steer-1"]);
    }

    [Fact]
    public void Steer_UnknownCard_StoresMessageButReportsInactive()
    {
        // A steer that races the run start must not be lost even though the run hasn't
        // registered yet — the planner drains it on its first turn.
        var controller = MakeController();

        var result = controller.SteerRun(new SteerRequest { CardId = "steer-2", Message = "hold the first step" });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("\"active\":false", JsonSerializer.Serialize(ok.Value));
        Assert.Equal("hold the first step", LiveSteer()["steer-2"]);
    }

    [Fact]
    public void Steer_LatestMessageWins_OverwritesPrevious()
    {
        var controller = MakeController();

        controller.SteerRun(new SteerRequest { CardId = "steer-3", Message = "first message" });
        controller.SteerRun(new SteerRequest { CardId = "steer-3", Message = "second message" });

        Assert.Equal("second message", LiveSteer()["steer-3"]);
    }

    [Fact]
    public async Task StaleSteer_FromNonExecutingPost_IsDroppedAtRunEnd_AndNeverHitsNextRun()
    {
        // The stale-steer guard: a steer posted while the card is NOT executing (active:false)
        // sits in _liveSteer waiting to be drained. If the run ends WITHOUT draining it, the
        // stream's finally block must drop it — otherwise the stale message leaks into the
        // card's NEXT run and steers it with intent from a previous, unrelated execution.
        const string cardId = "steer-stale-1";
        var controller = BuildController();

        // 1) Post the steer while nothing is executing → stored, reported active:false.
        var result = controller.SteerRun(new SteerRequest { CardId = cardId, Message = LiveSteerMessage });
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("\"active\":false", JsonSerializer.Serialize(ok.Value));
        Assert.Equal(LiveSteerMessage, LiveSteer()[cardId]);

        // 2) The run ends without consuming it — the exact cleanup the stream's finally runs.
        InvokeClearRunState(controller, cardId);
        Assert.False(LiveSteer().ContainsKey(cardId));

        // 3) A fresh run on the same card must start clean: the stale steer never appears in
        //    any planner prompt, so it cannot steer the next execution.
        _clientFactory.PlannerUserPrompts.Clear();
        var (_, _, complete) = await InvokeOrchestrate(controller, cardId,
            "In the demo component, add a public getItems() method.", RunStartSteering);
        Assert.True(complete, $"run must complete — calls=[{string.Join(",", _clientFactory.Calls)}]");
        Assert.NotEmpty(_clientFactory.PlannerUserPrompts);
        foreach (var prompt in _clientFactory.PlannerUserPrompts)
        {
            Assert.DoesNotContain("### LIVE STEER ###", prompt);
            Assert.DoesNotContain(LiveSteerMessage, prompt);
        }
    }

    [Fact]
    public void Steer_MissingCardIdOrMessage_ReturnsBadRequest()
    {
        var controller = MakeController();
        Assert.IsType<BadRequestObjectResult>(controller.SteerRun(new SteerRequest { CardId = "", Message = "x" }));
        Assert.IsType<BadRequestObjectResult>(controller.SteerRun(new SteerRequest { CardId = "steer-4", Message = "" }));
        Assert.False(LiveSteer().ContainsKey("steer-4"));
    }

    // ── Mid-run injection through the live endpoint ──────────────────────────────────────

    [Fact]
    public async Task MidRunSteer_Turn3Message_OverridesTurn2Proposal()
    {
        const string cardId = "steer-run-1";
        var controller = BuildController();
        // Seed the card into boarddata so the delivered steer can be persisted as _steers
        // (the transcript the run appends to) — otherwise PublishDeliveredSteerAsync no-ops.
        await _boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));

        // The live steer is posted WHILE turn 2's planner request is in flight (simulating a
        // human who sees the turn-2 proposal and corrects course before it becomes the plan).
        _clientFactory.OnPlannerTurn = n =>
        {
            if (n == 2) LiveSteer()[cardId] = LiveSteerMessage;
        };

        var (_, plan, complete) = await InvokeOrchestrate(controller, cardId,
            "In the demo component, add a public getItems() method.", RunStartSteering);

        // The run really spanned three planner turns: turn 1 = the trivial getItems, turn 2 =
        // the unwanted helper (steer posted while this turn was in flight), turn 3 = the
        // override that undoes it. After turn 3's step, the whole-task assessment declares the
        // run complete (no fourth planner call needed).
        Assert.True(complete,
            $"run should complete — calls=[{string.Join(",", _clientFactory.Calls)}]; plan={plan?.Summary}");
        Assert.Equal(3, _clientFactory.PlannerUserPrompts.Count);

        // NOT run-start steering: turns 1 and 2 carry only the run-start context — the live
        // steer is absent (it hadn't been posted when those prompts were built).
        foreach (var prompt in _clientFactory.PlannerUserPrompts.Take(2))
        {
            Assert.Contains(RunStartSteering, prompt);
            Assert.DoesNotContain("### LIVE STEER ###", prompt);
            Assert.DoesNotContain("IMPORTANT USER UPDATE", prompt);
        }

        // The turn-3 prompt (built AFTER the steer was posted, i.e. the planner call whose
        // request was received after turn 2's reply) carries the live steer — the mid-run
        // instruction reached the planner on the next turn, so it can override what turn 2
        // proposed.
        var turn3 = _clientFactory.PlannerUserPrompts[2];
        Assert.Contains("### LIVE STEER ###", turn3);
        Assert.Contains(LiveSteerMessage, turn3);
        Assert.Contains(RunStartSteering, turn3); // run-start context stays visible too

        // The override landed: the unwanted turn-2 helper is gone, getItems() returns a copy.
        var ts = Read(DemoTsRel);
        Assert.Contains("this.items.slice()", ts);
        Assert.Contains("getItems()", ts);
        Assert.DoesNotContain("getItemsCount", ts);

        // Consumed exactly once: nothing remains in the registry, so no later turn re-applied it.
        Assert.False(LiveSteer().ContainsKey(cardId));
        Assert.Empty(_clientFactory.Unmatched);

        // The delivered steer survives on the card as a transcript: message + the turn it
        // became visible to (turn 3 — the planner call it reached), so a reload shows what
        // was injected and when, like the ground-truth section.
        var steers = ReadCardSteers(await _boardData.LoadRawAsync(), cardId);
        Assert.NotNull(steers);
        Assert.Single(steers!);
        Assert.Equal(3, steers![0].turn);
        Assert.Equal(LiveSteerMessage, steers[0].message);
        Assert.False(string.IsNullOrWhiteSpace(steers[0].at));
    }

    // ── Harness (mirrors AdversarialUserScenarioTests / InterleavedPipelineIntegrationTests) ─

    private async Task<(List<object> allSteps, AgentPlan? plan, bool complete)> InvokeOrchestrate(
        AgentController controller, string cardId, string prompt, string steeringContext)
    {
        var method = typeof(AgentController).GetMethod("Orchestrate", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Orchestrate not found");
        var task = (Task<(List<object> allSteps, AgentPlan? plan, bool complete)>)method.Invoke(controller, new object?[]
        {
            prompt, _projectRoot, /*emitSse*/ false, CancellationToken.None,
            /*attachedFiles*/ new List<string> { DemoTsRel },
            /*skipContextReview*/ false, /*steeringContext*/ steeringContext, /*skipQualityCheck*/ false,
            /*existingPlan*/ null, /*completedStepIndices*/ null, /*cardId*/ cardId,
            /*createTests*/ false, /*buildCommands*/ null, /*strictVerifier*/ null, /*webResults*/ null
        })!;
        return await task;
    }

    private string Read(string relPath) =>
        File.ReadAllText(Path.Combine(_projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));

    private static string BoardWithCard(string cardId, string column)
    {
        var board = new Dictionary<string, object?>
        {
            ["todo"] = new List<object>(),
            ["doing"] = new List<object>(),
            ["done"] = new List<object>(),
            ["archived"] = new List<object>(),
            ["selfImproving"] = new List<object>()
        };
        board[column] = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["id"] = cardId,
                ["text"] = "task",
                ["filePath"] = "C:/x"
            }
        };
        return JsonSerializer.Serialize(board);
    }

    private static List<(int turn, string message, string at)>? ReadCardSteers(string? raw, string cardId)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        using var doc = JsonDocument.Parse(raw);
        foreach (var col in new[] { "todo", "doing", "done", "selfImproving" })
        {
            if (!doc.RootElement.TryGetProperty(col, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var card in arr.EnumerateArray())
            {
                if (!card.TryGetProperty("id", out var id) || id.GetString() != cardId) continue;
                if (!card.TryGetProperty("_steers", out var steers) || steers.ValueKind != JsonValueKind.Array)
                    return new List<(int, string, string)>();
                return steers.EnumerateArray().Select(s => (
                    s.TryGetProperty("turn", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0,
                    s.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "",
                    s.TryGetProperty("at", out var a) ? a.GetString() ?? "" : "")).ToList();
            }
        }
        return null;
    }

    private static void InvokeClearRunState(AgentController controller, string cardId)
    {
        var method = typeof(AgentController).GetMethod("ClearRunStateForCard", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ClearRunStateForCard not found");
        method.Invoke(controller, new object?[] { cardId });
    }

    private static AgentController MakeController()
        => (AgentController)RuntimeHelpers.GetUninitializedObject(typeof(AgentController));

    private static System.Collections.Concurrent.ConcurrentDictionary<string, string> LiveSteer()
        => (System.Collections.Concurrent.ConcurrentDictionary<string, string>)typeof(AgentController)
            .GetField("_liveSteer", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static System.Collections.Concurrent.ConcurrentDictionary<string, long> ExecutingCards()
        => (System.Collections.Concurrent.ConcurrentDictionary<string, long>)typeof(AgentController)
            .GetField("_executingCards", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

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
        // Hermetic runtime probe: nothing installed (no real process spawns in tests).
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

    /// <summary>
    /// Scripted fake LLM. The planner returns a scripted 3-step trajectory (trivial getItems →
    /// unwanted getItemsCount helper → the live-steer override that removes it), then
    /// planComplete. <see cref="OnPlannerTurn"/> fires before each planner reply so the test
    /// can post a live steer mid-run at a chosen turn. Every other route is fixed; any call
    /// no marker matches lands in <see cref="Unmatched"/> and is answered empty.
    /// </summary>
    private sealed class ScriptedClientFactory : IHttpClientFactory, IDisposable
    {
        public Action<int>? OnPlannerTurn { get; set; }
        public readonly List<string> Calls = new();
        public readonly List<string> Unmatched = new();
        public readonly List<string> PlannerUserPrompts = new();
        private int _plannerCalls;
        private int _assessCalls;

        public HttpClient CreateClient(string name) => new(new ScriptedHandler(this));
        public HttpClient CreateClient() => CreateClient("default");
        public void Dispose() { }

        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly ScriptedClientFactory _owner;
            public ScriptedHandler(ScriptedClientFactory owner) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => Task.FromResult(BuildResponse(request));

            private HttpResponseMessage BuildResponse(HttpRequestMessage request)
            {
                if (request.Method == HttpMethod.Get)
                    return Json(new { });
                var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
                var system = new StringBuilder();
                var user = new StringBuilder();
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("messages", out var msgs))
                    {
                        foreach (var m in msgs.EnumerateArray())
                        {
                            var role = m.TryGetProperty("role", out var r) ? r.GetString() : "";
                            var msgContent = m.TryGetProperty("content", out var c) ? c.GetString() : "";
                            if (role == "system") system.Append(msgContent).Append('\n');
                            else if (role == "user") user.Append(msgContent).Append('\n');
                        }
                    }
                }
                catch { }
                var streaming = body.Contains("\"stream\":true", StringComparison.Ordinal) ||
                                body.Contains("\"stream\": true", StringComparison.Ordinal);
                var (content, kind) = Route(system.ToString(), user.ToString());
                lock (_owner.Calls) _owner.Calls.Add(kind);
                return streaming ? Sse(content) : Json(new { choices = new[] { new { message = new { content } } } });
            }

            private (string content, string kind) Route(string system, string user)
            {
                if (system.Contains("building a code-change plan ONE STEP AT A TIME", StringComparison.Ordinal))
                {
                    lock (_owner.PlannerUserPrompts) _owner.PlannerUserPrompts.Add(user);
                    var n = Interlocked.Increment(ref _owner._plannerCalls);
                    _owner.OnPlannerTurn?.Invoke(n);
                    return n switch
                    {
                        1 => (StepJson(DemoTsRel, "Add getItems() returning the items array",
                            CtorLine, CtorLine + "\n" + GetItemsPlain), "planner-step"),
                        2 => (StepJson(DemoTsRel, "Also expose a getItemsCount() helper",
                            GetItemsPlain, GetItemsPlain + "\n" + GetItemsCountLine), "planner-step"),
                        3 => (StepJson(DemoTsRel, "Apply the live steer: drop getItemsCount(), make getItems() return a copy",
                            GetItemsPlain + "\n" + GetItemsCountLine, GetItemsWithCopy), "planner-step"),
                        _ => ("{\"planComplete\": true, \"completionReason\": \"plan complete\"}", "planner-complete")
                    };
                }
                // Pre-plan deep reasoning fires before each planner turn when a cardId is
                // present (steering is keyed by cardId, so this test needs one) — answer with
                // benign prose that cannot interfere with the scripted steps.
                if (system.Contains("You are the deep-reasoning engine of an autonomous coding agent", StringComparison.Ordinal))
                    return ("The next step is scripted by the test harness. Keep the task minimal: exactly one public getItems() method, nothing more.", "deep-reason");
                if (system.Contains("Plan the complete minimum set of steps", StringComparison.Ordinal))
                    return ("{\"plan\": []}", "planner-classic");
                if (system.Contains("You extract a short checklist of literal, testable requirements", StringComparison.Ordinal))
                    return ("{\"requirements\": [\"Add a public getItems() method\"]}", "checklist");
                if (system.Contains("You are a strict plan-coherence validator", StringComparison.Ordinal))
                    return ("{\"valid\": true}", "plan-validator");
                if (user.Contains("Decide: keep or abandon", StringComparison.Ordinal))
                    return ("{\"decision\": \"keep\", \"reason\": \"verified\", \"score\": 95, \"needsExtraStep\": false}", "verify");
                if (user.Contains("Evaluate the code changes against the ORIGINAL TASK ONLY", StringComparison.Ordinal))
                {
                    // Steps 1 and 2 are "not complete yet" so the loop keeps planning (and the
                    // live steer gets a chance to land before turn 3); the override step 3
                    // finally satisfies the task and the run terminates cleanly.
                    var n = Interlocked.Increment(ref _owner._assessCalls);
                    return n <= 2
                        ? ("{\"complete\": false, \"reason\": \"still working\", \"issues\": []}", "assess")
                        : ("{\"complete\": true, \"reason\": \"task satisfied\", \"issues\": []}", "assess");
                }
                if (system.Contains("meticulous code reviewer verifying if a task is fully complete", StringComparison.Ordinal))
                    return ("{\"complete\": true, \"reason\": \"done\", \"issues\": []}", "post-verify");
                if (system.Contains("You detect code cohesion issues after an edit. Output ONLY JSON.", StringComparison.Ordinal))
                    return ("{\"issues\": []}", "cohesion");
                lock (_owner.Unmatched) _owner.Unmatched.Add(system.Length > 80 ? system[..80] : system);
                return ("", "unknown");
            }

            private static string StepJson(string file, string change, string oldString, string newString)
            {
                var payload = new Dictionary<string, object?>
                {
                    ["thinking"] = "Single atomic step.",
                    ["planComplete"] = false,
                    ["step"] = new Dictionary<string, object?>
                    {
                        ["file"] = file,
                        ["change"] = change,
                        ["oldString"] = oldString,
                        ["newString"] = newString
                    }
                };
                return JsonSerializer.Serialize(payload);
            }

            private static HttpResponseMessage Json(object obj)
                => new(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json")
                };

            private static HttpResponseMessage Sse(string content)
            {
                var data = JsonSerializer.Serialize(new
                {
                    choices = new[] { new { delta = new { content }, finish_reason = "stop" } }
                });
                var body = $"data: {data}\n\n\ndata: [DONE]\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
                };
            }
        }
    }
}
