using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Weaver;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Integration coverage for the REAL interleaved plan → execute → verify pipeline
/// (Orchestrate → StepResolutionPipeline → RunInterleavedPlanExecutionLoop →
/// PostExecuteVerify), driven end-to-end against a real temp Angular project with a
/// SCRIPTED fake LLM. This is the regression test for the crawler-component churn:
///
/// The task is a pipe-only template edit ("add comma separators to the index count").
/// The pre-edit HTML carries pre-existing bindings that the OLD whole-template binding
/// check false-positived on (#keywordsInput template ref, searchResults.length array
/// chain, parentRef / noFavourites / onMobile members), which spawned garbage repair
/// steps (isLoading = false, a .catch(), keywordsInput: string). With the per-step
/// pre-edit snapshot threading + hardened extraction, the run must:
///
///   1. propose ONE step (the pipe edit), apply it, and verify it complete;
///   2. run post-execution verification with ZERO template-binding issues and
///      complete=true → NO repair loop, NO repair plan steps, NO edits to the .ts;
///   3. finish complete with exactly one edit result.
///
/// The fake LLM routes on stable system/user prompt markers and FAILS the test if any
/// LLM call arrives that the script does not account for (Unmatched) — so if the
/// pipeline starts making unexpected LLM calls (e.g. the repair replanner firing), the
/// test fails loudly instead of silently degrading.
/// </summary>
public class InterleavedPipelineIntegrationTests : IDisposable
{
    private const string HtmlRel = "maxhanna.client/src/app/crawler/crawler.component.html";
    private const string TsRel = "maxhanna.client/src/app/crawler/crawler.component.ts";

    // The exact crawler-component shape that false-positived before the fixes: a method
    // containing a "}" inside a backtick string, then members declared with
    // definite-assignment assertions, plain assignments, and an inline @ViewChild.
    // NOTE: `isLoading` in the template is deliberately NOT declared in the component — a
    // GENUINE pre-existing missing binding (like the real crawler component). Only the
    // pre-edit snapshot threading can exclude it from post-execution validation; the
    // whole-template fallback flags it and drives a repair, so this fixture is a true
    // canary for the snapshot fix, not just for the extraction hardening.
    private const string ComponentTs = """
        import { Component, ViewChild, ElementRef } from '@angular/core';

        @Component({ selector: 'app-crawler' })
        export class CrawlerComponent {
          load() {
            const css = `padding: ${this.indexCount}px }`;
            return 1;
          }
          parentRef!: ElementRef<HTMLDivElement> | null;
          onMobile = window.innerWidth < 768;
          noFavourites = false;
          @ViewChild('keywordsInput') keywordsInput!: ElementRef<HTMLInputElement>;
          searchResults: any[] = [];
          indexCount = 0;
        }
        """;

    private const string IndexCountLine =
        "<div *ngIf=\"!searchResults.length && indexCount\" class=\"nbDiv\">Total indexes: {{ indexCount }}</div>";
    private const string IndexCountLinePiped =
        "<div *ngIf=\"!searchResults.length && indexCount\" class=\"nbDiv\">Total indexes: {{ indexCount | number:'1.0-0' }}</div>";

    private static string HtmlBefore() => """
        <div class="notificationContainer">
          <input #keywordsInput />
          <div *ngIf="isLoading" class="loadingSpinner">Loading…</div>
          <div *ngIf="!searchResults.length && indexCount" class="nbDiv">Total indexes: {{ indexCount }}</div>
          <div *ngIf="parentRef">{{ parentRef.nativeElement }}</div>
          <div *ngIf="noFavourites">None</div>
          <div *ngIf="onMobile">mobile</div>
        </div>
        """;

    private readonly string _base;
    private readonly string _projectRoot;
    private readonly DatabaseService _db;
    private readonly ScriptedClientFactory _clientFactory = new();

    public InterleavedPipelineIntegrationTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "weaver_interleaved_" + Guid.NewGuid().ToString("N"));
        _projectRoot = Path.Combine(_base, "proj");
        Directory.CreateDirectory(_projectRoot);
        Directory.CreateDirectory(Path.Combine(_base, "data"));

        var tsPath = Path.Combine(_projectRoot, TsRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(tsPath)!);
        File.WriteAllText(tsPath, ComponentTs);
        File.WriteAllText(Path.Combine(_projectRoot, HtmlRel.Replace('/', Path.DirectorySeparatorChar)), HtmlBefore());

        _db = new DatabaseService(
            Path.Combine(_base, "data", "weaver.db"),
            Path.Combine(_base, "data"),
            Path.Combine(_base, "data", "weaverconfig.json"));
    }

    public void Dispose()
    {
        _clientFactory.Dispose();
        try { Directory.Delete(_base, true); } catch { }
    }

    [Fact]
    public async Task PipeOnlyTemplateEdit_FullInterleavedPipeline_CompletesWithZeroRepairSteps()
    {
        var controller = BuildController();
        var prompt = "Format the indexCount display with comma separators in the crawler component";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        // The run finished complete.
        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}");

        // The pipe landed in the file.
        var htmlPath = Path.Combine(_projectRoot, HtmlRel.Replace('/', Path.DirectorySeparatorChar));
        var html = await File.ReadAllTextAsync(htmlPath);
        Assert.Contains("{{ indexCount | number:'1.0-0' }}", html);
        Assert.DoesNotContain("{{ indexCount }}</div>", html);

        // EXACTLY ONE edit result, and it targets the HTML — never the .ts. A repair pass
        // would have edited the component (adding invented members) and/or produced extra
        // steps; both are asserted absent.
        var editResults = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("type")?.ToString() is "edit" or "create" &&
                        r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
            .ToList();
        var edit = Assert.Single(editResults);
        Assert.Equal(HtmlRel, edit.GetValueOrDefault("path")?.ToString());

        // No repair steps were planned: the plan holds exactly the one pipe edit, and no
        // step description is a "Repair:" summary.
        Assert.NotNull(plan);
        Assert.Single(plan!.Plan);
        Assert.DoesNotContain(plan.Plan, s => (s.Change ?? "").StartsWith("Repair", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allSteps.OfType<Dictionary<string, object?>>(),
            r => r.GetValueOrDefault("description")?.ToString()?.Contains("Repair:", StringComparison.OrdinalIgnoreCase) == true);

        // The .ts was never touched (no repair edits adding members).
        Assert.DoesNotContain(editResults, r => r.GetValueOrDefault("path")?.ToString() == TsRel);
        var tsOnDisk = await File.ReadAllTextAsync(Path.Combine(_projectRoot, TsRel.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal(ComponentTs, tsOnDisk);

        // Every LLM call the pipeline made was one the script accounted for — an unexpected
        // call (e.g. the repair replanner firing) fails the test loudly.
        Assert.Empty(_clientFactory.Unmatched);

        // Regression: the between-steps assessor must have SEEN the actual OLD→NEW diff of
        // the applied edit (the "doesn't see that the last step satisfied the prompt" bug:
        // without the diff, the assessor cannot tell what this run added vs pre-existing
        // CSS/markup and plans a redundant follow-up step). The assess prompt must carry the
        // diff marker and the pipe change itself.
        var assessPrompt = Assert.Single(_clientFactory.AssessPrompts);
        Assert.Contains("OLD→NEW", assessPrompt);
        Assert.Contains("| number:'1.0-0'", assessPrompt);

        // Sanity: the scripted LLM saw the expected call kinds.
        Assert.Contains("checklist", _clientFactory.Calls);
        Assert.Contains("planner-step", _clientFactory.Calls);
        Assert.Contains("verify", _clientFactory.Calls);
        Assert.Contains("assess", _clientFactory.Calls);
        Assert.Contains("post-verify", _clientFactory.Calls);
        Assert.Contains("cohesion", _clientFactory.Calls);
    }

    private async Task<(List<object> allSteps, AgentPlan? plan, bool complete)> InvokeOrchestrate(
        AgentController controller, string prompt)
    {
        var method = typeof(AgentController).GetMethod("Orchestrate", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Orchestrate not found");
        var task = (Task<(List<object> allSteps, AgentPlan? plan, bool complete)>)method.Invoke(controller, new object?[]
        {
            prompt, _projectRoot, /*emitSse*/ false, CancellationToken.None,
            /*attachedFiles*/ new List<string> { HtmlRel, TsRel },
            /*skipContextReview*/ false, /*steeringContext*/ null, /*skipQualityCheck*/ false,
            /*existingPlan*/ null, /*completedStepIndices*/ null, /*cardId*/ null,
            /*createTests*/ false, /*buildCommands*/ null, /*strictVerifier*/ null, /*webResults*/ null
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
        SetField(controller, "_boardData", new BoardDataService(_db, NullLogger<BoardDataService>.Instance));
        SetField(controller, "_emailService", new EmailService(new ConfigFileService(_db)));
        SetField(controller, "_push", new PushNotificationService(_db));
        SetField(controller, "_editKnowledge", new EditKnowledgeService(_db));
        // Hermetic runtime probe: nothing installed (no real process spawns in tests).
        SetField(controller, "_runtimeProbe", new RuntimeProbeService((_, _, _) => (-1, "", "")));
        // Skip the real TCP/HTTP connectivity probe: the run must not depend on the host
        // network. Cache the "reachable" verdict directly (_nextConnectivityCheck is static;
        // _lastConnectionCheckResult is an instance field on the controller).
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
    /// An <see cref="IHttpClientFactory"/> whose handler answers every LLM request from a
    /// script routed on stable system/user prompt markers. Responses use the OpenAI chat
    /// shape the controller parses: streaming calls get SSE `data:` chunks, non-streaming
    /// calls get a `{"choices":[{"message":{"content":...}}]}` body. Any request that no
    /// marker matches is recorded in <see cref="Unmatched"/> and answered with an empty
    /// content so the pipeline degrades — the test then fails on the Unmatched assertion.
    /// </summary>
    private sealed class ScriptedClientFactory : IHttpClientFactory, IDisposable
    {
        public readonly List<string> Calls = new();
        public readonly List<string> Unmatched = new();
        // User prompts of the between-steps AssessCompletion calls — the regression hook for
        // the redundant-step fix: the assessor must see the OLD→NEW diff of each applied
        // edit (otherwise it cannot tell what this run added and plans a redundant follow-up
        // step, e.g. adding an HTML class the CSS already targets).
        public readonly List<string> AssessPrompts = new();
        private int _plannerCalls;

        public HttpClient CreateClient(string name) => new(new ScriptedHandler(this));
        public HttpClient CreateClient() => CreateClient("default");
        public void Dispose() { }

        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly ScriptedClientFactory _owner;
            public ScriptedHandler(ScriptedClientFactory owner) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var resp = BuildResponse(request);
                return Task.FromResult(resp);
            }

            private HttpResponseMessage BuildResponse(HttpRequestMessage request)
            {
                if (request.Method == HttpMethod.Get)
                {
                    // Connectivity probes (/api/tags, /slots): answer 200 so reachability checks pass.
                    return Json(new { });
                }
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
                // ORDER MATTERS: the planner prompt is checked first (its system text is the
                // most distinctive), then the other stable markers.
                if (system.Contains("senior autonomous coding agent building a code-change plan", StringComparison.Ordinal))
                {
                    var n = Interlocked.Increment(ref _owner._plannerCalls);
                    return n == 1 ? (PlannerStepJson(), "planner-step")
                                  : ("{\"planComplete\": true, \"completionReason\": \"plan complete\"}", "planner-complete");
                }
                if (system.Contains("You extract a short checklist of literal, testable requirements", StringComparison.Ordinal))
                    return ("{\"requirements\": [\"Format the index count display with comma separators\"]}", "checklist");
                if (user.Contains("Decide: keep or abandon", StringComparison.Ordinal))
                    return ("{\"decision\": \"keep\", \"reason\": \"verified\", \"score\": 95, \"needsExtraStep\": false}", "verify");
                if (user.Contains("Evaluate the code changes against the ORIGINAL TASK ONLY", StringComparison.Ordinal))
                {
                    _owner.AssessPrompts.Add(user);
                    return ("{\"complete\": true, \"reason\": \"task satisfied\", \"issues\": []}", "assess");
                }
                if (system.Contains("meticulous code reviewer verifying if a task is fully complete", StringComparison.Ordinal))
                    return ("{\"complete\": true, \"reason\": \"done\", \"issues\": []}", "post-verify");
                if (system.Contains("You detect code cohesion issues after an edit. Output ONLY JSON.", StringComparison.Ordinal))
                    return ("{\"issues\": []}", "cohesion");
                lock (_owner.Unmatched) _owner.Unmatched.Add(system.Length > 80 ? system[..80] : system);
                return ("", "unknown");
            }

            private static string PlannerStepJson()
            {
                var payload = new Dictionary<string, object?>
                {
                    ["thinking"] = "Single atomic step: add the number pipe to the existing indexCount interpolation.",
                    ["planComplete"] = false,
                    ["step"] = new Dictionary<string, object?>
                    {
                        ["file"] = HtmlRel,
                        ["change"] = "Format indexCount with comma separators in Total indexes display",
                        ["oldString"] = IndexCountLine,
                        ["newString"] = IndexCountLinePiped
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
