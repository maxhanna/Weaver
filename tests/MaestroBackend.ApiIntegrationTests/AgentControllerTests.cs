using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MaestroBackend.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MaestroBackend.ApiIntegrationTests;

public class AgentControllerTests : IClassFixture<MaestroWebApplicationFactory>
{
    private readonly MaestroWebApplicationFactory _factory;

    public AgentControllerTests(MaestroWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private (HttpClient client, IAgentPendingStore store, WebApplicationFactory<Program> factory) CreateClientWithPendingStore()
    {
        ResetConnectivityCache();

        var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAgentPendingStore>();
                services.AddSingleton<IAgentPendingStore, AgentPendingStore>();
            });
        });

        customFactory.Services.GetRequiredService<TestHttpResponder>().Reset();
        var client = customFactory.CreateClient();
        var store = customFactory.Services.GetRequiredService<IAgentPendingStore>();
        return (client, store, customFactory);
    }

    private (HttpClient client, ScriptedTerminalService terminal, WebApplicationFactory<Program> factory) CreateClientWithAgentServices(
        Action<ScriptedTerminalService>? configureTerminal = null,
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        ResetConnectivityCache();

        var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAgentPendingStore>();
                services.AddSingleton<IAgentPendingStore, AgentPendingStore>();

                services.RemoveAll<ITerminalService>();
                var terminal = new ScriptedTerminalService();
                configureTerminal?.Invoke(terminal);
                services.AddSingleton<ITerminalService>(terminal);
            });
        });

        var httpResponder = customFactory.Services.GetRequiredService<TestHttpResponder>();
        httpResponder.Reset();
        if (responder != null)
            httpResponder.Responder = responder;

        var client = customFactory.CreateClient();
        var terminal = (ScriptedTerminalService)customFactory.Services.GetRequiredService<ITerminalService>();
        return (client, terminal, customFactory);
    }

    private (HttpClient client, ScriptedTerminalService terminal, IAgentPendingStore store, WebApplicationFactory<Program> factory) CreateClientWithAgentServicesAndPendingStore(
        Action<ScriptedTerminalService>? configureTerminal = null,
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        ResetConnectivityCache();

        var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAgentPendingStore>();
                services.AddSingleton<IAgentPendingStore, AgentPendingStore>();

                services.RemoveAll<ITerminalService>();
                var terminal = new ScriptedTerminalService();
                configureTerminal?.Invoke(terminal);
                services.AddSingleton<ITerminalService>(terminal);
            });
        });

        var httpResponder = customFactory.Services.GetRequiredService<TestHttpResponder>();
        httpResponder.Reset();
        if (responder != null)
            httpResponder.Responder = responder;

        var client = customFactory.CreateClient();
        var terminal = (ScriptedTerminalService)customFactory.Services.GetRequiredService<ITerminalService>();
        var store = customFactory.Services.GetRequiredService<IAgentPendingStore>();
        return (client, terminal, store, customFactory);
    }

    [Fact]
    public async Task Execute_returns_bad_request_when_prompt_missing()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/agent/execute", new { prompt = "", project = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Prompt is required", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ExecuteStream_emits_error_and_done_when_prompt_missing()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/agent/execute-stream", new { prompt = "", project = "" });
        response.EnsureSuccessStatusCode();

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        var events = ParseSseEvents(body);

        Assert.Equal(new[] { "error", "done" }, events.Select(e => e.Event).ToArray());
        Assert.Contains("Prompt is required", events[0].Data);
    }

    [Fact]
    public async Task Execute_runs_simple_git_intent_through_command_pipeline()
    {
        var (client, terminal, factory) = CreateClientWithAgentServices(responder: ClassificationResponse);
        using (factory)
        {
            var response = await client.PostAsJsonAsync("/api/agent/execute", new { prompt = "git pull latest", project = "" });
            response.EnsureSuccessStatusCode();

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(json.RootElement.GetProperty("complete").GetBoolean());

            var steps = json.RootElement.GetProperty("steps");
            Assert.True(steps.GetArrayLength() >= 1);
            Assert.Equal("command", steps[0].GetProperty("type").GetString());
            Assert.Equal("done", steps[0].GetProperty("status").GetString());
            Assert.Contains("git pull", steps[0].GetProperty("command").GetString());
            Assert.Contains("git pull", terminal.Commands);
        }
    }

    [Fact]
    public async Task Execute_handles_rename_delete_ping_and_install_fast_paths()
    {
        var renameSource = Path.Combine(_factory.WorkspaceRootPath, "sample", "rename-source.txt");
        var deleteTarget = Path.Combine(_factory.WorkspaceRootPath, "sample", "delete-me.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(renameSource)!);
        await File.WriteAllTextAsync(renameSource, "rename me", Encoding.UTF8);
        await File.WriteAllTextAsync(deleteTarget, "delete me", Encoding.UTF8);

        var (client, terminal, factory) = CreateClientWithAgentServices(
            configureTerminal: terminal =>
            {
                terminal.OutputFactory = (command, _) =>
                {
                    if (command.StartsWith("ping ", StringComparison.OrdinalIgnoreCase))
                        return "Reply from 127.0.0.1: bytes=32 time<1ms TTL=128" + Environment.NewLine;
                    if (command.StartsWith("npm install", StringComparison.OrdinalIgnoreCase))
                        return "added 1 package in 534ms" + Environment.NewLine;
                    return ScriptedTerminalService.DefaultOutput(command);
                };
            },
            responder: ClassificationResponse);

        using (factory)
        {
            var rename = await client.PostAsJsonAsync("/api/agent/execute", new { prompt = "rename sample/rename-source.txt to renamed.txt", project = "" });
            rename.EnsureSuccessStatusCode();
            using (var renameJson = JsonDocument.Parse(await rename.Content.ReadAsStringAsync()))
            {
                Assert.True(renameJson.RootElement.GetProperty("complete").GetBoolean());
                var renameSteps = renameJson.RootElement.GetProperty("steps");
                Assert.Equal("rename", renameSteps[0].GetProperty("type").GetString());
                Assert.Equal("done", renameSteps[0].GetProperty("status").GetString());
                Assert.Equal("sample/rename-source.txt", renameSteps[0].GetProperty("path").GetString());
                Assert.Equal("sample/renamed.txt", renameSteps[0].GetProperty("toPath").GetString());
            }
            Assert.False(File.Exists(renameSource));
            Assert.True(File.Exists(Path.Combine(_factory.WorkspaceRootPath, "sample", "renamed.txt")));

            var delete = await client.PostAsJsonAsync("/api/agent/execute", new { prompt = "delete file sample/delete-me.txt", project = "" });
            delete.EnsureSuccessStatusCode();
            using (var deleteJson = JsonDocument.Parse(await delete.Content.ReadAsStringAsync()))
            {
                Assert.True(deleteJson.RootElement.GetProperty("complete").GetBoolean());
                var deleteSteps = deleteJson.RootElement.GetProperty("steps");
                Assert.Equal("rename", deleteSteps[0].GetProperty("type").GetString());
                Assert.Equal("deleted", deleteSteps[0].GetProperty("editAction").GetString());
            }
            Assert.False(File.Exists(deleteTarget));

            var ping = await client.PostAsJsonAsync("/api/agent/execute", new { prompt = "ping localhost", project = "" });
            ping.EnsureSuccessStatusCode();
            using (var pingJson = JsonDocument.Parse(await ping.Content.ReadAsStringAsync()))
            {
                Assert.True(pingJson.RootElement.GetProperty("complete").GetBoolean());
                var pingSteps = pingJson.RootElement.GetProperty("steps");
                Assert.Equal("command", pingSteps[0].GetProperty("type").GetString());
                Assert.Contains("ping localhost", pingSteps[0].GetProperty("command").GetString());
                Assert.Contains("TTL=128", pingSteps[0].GetProperty("output").GetString());
                Assert.True(pingSteps[0].TryGetProperty("pingAnalysis", out _));
            }

            var install = await client.PostAsJsonAsync("/api/agent/execute", new { prompt = "npm install left-pad", project = "" });
            install.EnsureSuccessStatusCode();
            using (var installJson = JsonDocument.Parse(await install.Content.ReadAsStringAsync()))
            {
                Assert.True(installJson.RootElement.GetProperty("complete").GetBoolean());
                var installSteps = installJson.RootElement.GetProperty("steps");
                Assert.Equal("command", installSteps[0].GetProperty("type").GetString());
                Assert.Contains("npm install left-pad", installSteps[0].GetProperty("command").GetString());
                Assert.Contains("added 1 package", installSteps[0].GetProperty("output").GetString());
            }

            Assert.Contains(terminal.Commands, command => command.StartsWith("ping localhost", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(terminal.Commands, command => command.StartsWith("npm install left-pad", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Execute_handles_git_pull_commit_sync_and_revert_fast_paths()
    {
        var (client, terminal, factory) = CreateClientWithAgentServices(
            configureTerminal: terminal =>
            {
                terminal.OutputFactory = (command, _) =>
                {
                    if (command.Equals("git pull", StringComparison.OrdinalIgnoreCase))
                        return "Updating 1234567..89abcde" + Environment.NewLine + "Fast-forward" + Environment.NewLine;
                    if (command.StartsWith("git add -A && git commit -m ", StringComparison.OrdinalIgnoreCase))
                        return "[main abc1234] ship it" + Environment.NewLine + " 1 file changed, 1 insertion(+)" + Environment.NewLine;
                    if (command.Equals("git pull && git push", StringComparison.OrdinalIgnoreCase))
                        return "Already up to date." + Environment.NewLine + "Everything up-to-date" + Environment.NewLine;
                    if (command.Equals("git checkout -- .", StringComparison.OrdinalIgnoreCase))
                        return "Reverted working tree" + Environment.NewLine;
                    return ScriptedTerminalService.DefaultOutput(command);
                };
            },
            responder: ClassificationResponse);

        using (factory)
        {
            var pull = await client.PostAsJsonAsync("/api/agent/execute", new { prompt = "pull latest from git", project = "" });
            pull.EnsureSuccessStatusCode();
            using (var pullJson = JsonDocument.Parse(await pull.Content.ReadAsStringAsync()))
            {
                Assert.True(pullJson.RootElement.GetProperty("complete").GetBoolean());
                var pullSteps = pullJson.RootElement.GetProperty("steps");
                Assert.Equal(2, pullSteps.GetArrayLength());
                Assert.Equal("command", pullSteps[0].GetProperty("type").GetString());
                Assert.Equal("git pull", pullSteps[0].GetProperty("command").GetString());
                Assert.Equal("show", pullSteps[1].GetProperty("type").GetString());
                Assert.Equal("what was pulled from git", pullSteps[1].GetProperty("output").GetString());
            }

            var commit = await client.PostAsJsonAsync("/api/agent/execute", new { prompt = "git commit \"ship it\"", project = "" });
            commit.EnsureSuccessStatusCode();
            using (var commitJson = JsonDocument.Parse(await commit.Content.ReadAsStringAsync()))
            {
                Assert.True(commitJson.RootElement.GetProperty("complete").GetBoolean());
                var commitSteps = commitJson.RootElement.GetProperty("steps");
                Assert.Single(commitSteps.EnumerateArray());
                Assert.StartsWith("git add -A && git commit -m ", commitSteps[0].GetProperty("command").GetString(), StringComparison.OrdinalIgnoreCase);
                Assert.Contains("abc1234", commitSteps[0].GetProperty("output").GetString());
            }

            var sync = await client.PostAsJsonAsync("/api/agent/execute", new { prompt = "git push origin", project = "" });
            sync.EnsureSuccessStatusCode();
            using (var syncJson = JsonDocument.Parse(await sync.Content.ReadAsStringAsync()))
            {
                Assert.True(syncJson.RootElement.GetProperty("complete").GetBoolean());
                var syncSteps = syncJson.RootElement.GetProperty("steps");
                Assert.Single(syncSteps.EnumerateArray());
                Assert.Equal("git pull && git push", syncSteps[0].GetProperty("command").GetString());
            }

            var revert = await client.PostAsJsonAsync("/api/agent/execute", new { prompt = "revert all changes", project = "" });
            revert.EnsureSuccessStatusCode();
            using (var revertJson = JsonDocument.Parse(await revert.Content.ReadAsStringAsync()))
            {
                Assert.True(revertJson.RootElement.GetProperty("complete").GetBoolean());
                var revertSteps = revertJson.RootElement.GetProperty("steps");
                Assert.Single(revertSteps.EnumerateArray());
                Assert.Equal("git checkout -- .", revertSteps[0].GetProperty("command").GetString());
            }

            Assert.Contains("git pull", terminal.Commands);
            Assert.Contains(terminal.Commands, command => command.StartsWith("git add -A && git commit -m ", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("git pull && git push", terminal.Commands);
            Assert.Contains("git checkout -- .", terminal.Commands);
        }
    }

    [Fact]
    public async Task ExecuteStream_emits_plan_step_and_done_for_successful_rename()
    {
        var source = Path.Combine(_factory.WorkspaceRootPath, "sample", "stream-source.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "stream me", Encoding.UTF8);

        var (client, _, factory) = CreateClientWithAgentServices(responder: ClassificationResponse);
        using (factory)
        {
            var response = await client.PostAsJsonAsync("/api/agent/execute-stream", new
            {
                prompt = "rename sample/stream-source.txt to stream-renamed.txt",
                project = ""
            });
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync();
            var events = ParseSseEvents(body);

            Assert.Contains(events, e => e.Event == "phase");
            Assert.Contains(events, e => e.Event == "plan");
            Assert.Contains(events, e => e.Event == "step");
            var done = Assert.Single(events, e => e.Event == "done");
            using var doneJson = JsonDocument.Parse(done.Data);
            Assert.True(doneJson.RootElement.GetProperty("complete").GetBoolean());
            Assert.True(doneJson.RootElement.GetProperty("editsApplied").GetBoolean());
            Assert.False(File.Exists(source));
            Assert.True(File.Exists(Path.Combine(_factory.WorkspaceRootPath, "sample", "stream-renamed.txt")));
        }
    }

    [Fact]
    public async Task ExecuteStream_emits_show_event_for_git_pull_and_commit_hash_log()
    {
        var (client, terminal, factory) = CreateClientWithAgentServices(
            configureTerminal: terminal =>
            {
                terminal.OutputFactory = (command, _) =>
                {
                    if (command.Equals("git pull", StringComparison.OrdinalIgnoreCase))
                        return "Updating 1234567..89abcde" + Environment.NewLine + "Fast-forward" + Environment.NewLine;
                    if (command.StartsWith("git add -A && git commit -m ", StringComparison.OrdinalIgnoreCase))
                        return "[main abc1234] ship it" + Environment.NewLine + " 1 file changed, 1 insertion(+)" + Environment.NewLine;
                    return ScriptedTerminalService.DefaultOutput(command);
                };
            },
            responder: ClassificationResponse);

        using (factory)
        {
            var pullResponse = await client.PostAsJsonAsync("/api/agent/execute-stream", new { prompt = "pull latest from git", project = "" });
            pullResponse.EnsureSuccessStatusCode();
            var pullEvents = ParseSseEvents(await pullResponse.Content.ReadAsStringAsync());

            var showEvent = Assert.Single(pullEvents, e => e.Event == "show");
            using (var showJson = JsonDocument.Parse(showEvent.Data))
            {
                Assert.Equal("what was pulled from git", showJson.RootElement.GetProperty("text").GetString());
            }

            var commitResponse = await client.PostAsJsonAsync("/api/agent/execute-stream", new { prompt = "git commit \"ship it\"", project = "" });
            commitResponse.EnsureSuccessStatusCode();
            var commitEvents = ParseSseEvents(await commitResponse.Content.ReadAsStringAsync());

            var commitLogMessages = commitEvents
                .Where(e => e.Event == "log")
                .Select(e =>
                {
                    using var logJson = JsonDocument.Parse(e.Data);
                    return logJson.RootElement.GetProperty("message").GetString();
                })
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .ToList();

            Assert.Contains(commitLogMessages, message => message!.Contains("Git commit completed (abc1234)", StringComparison.Ordinal));
            Assert.Contains("git pull", terminal.Commands);
            Assert.Contains(terminal.Commands, command => command.StartsWith("git add -A && git commit -m ", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Execute_runs_code_edit_pipeline_with_cross_file_expansion_and_file_creation()
    {
        var project = "codeedit-execute";
        var projectRoot = Path.Combine(_factory.WorkspaceRootPath, project);
        var targetPath = Path.Combine(projectRoot, "Target.cs");
        var helperPath = Path.Combine(projectRoot, "HelperService.cs");
        var notePath = Path.Combine(projectRoot, "Notes", "GeneratedNote.txt");

        Directory.CreateDirectory(projectRoot);
        await File.WriteAllTextAsync(targetPath, """
namespace Sample;

public static class Target
{
    public static string BuildMessage()
    {
        return HelperService.DoWork();
    }
}
""", Encoding.UTF8);
        await File.WriteAllTextAsync(helperPath, """
namespace Sample;

class HelperService
{
    public static string DoWork()
    {
        return "old helper";
    }
}
""", Encoding.UTF8);
        await WriteConfigAsync(buildCommands: "");

        var planCalls = 0;
        var createFileCalls = 0;
        var (client, _, factory) = CreateClientWithAgentServices(
            responder: request => CrossFileCodeEditResponse(
                request,
                () => planCalls++,
                () => createFileCalls++));

        using (factory)
        {
            var response = await client.PostAsJsonAsync("/api/agent/execute", new
            {
                prompt = "Update the target message, update the helper text, and add a generated note file.",
                project
            });
            response.EnsureSuccessStatusCode();

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(json.RootElement.GetProperty("complete").GetBoolean());
            Assert.Equal(1, planCalls);
            Assert.Equal(1, createFileCalls);

            var steps = json.RootElement.GetProperty("steps");
            Assert.Contains(steps.EnumerateArray(), step => step.GetProperty("type").GetString() == "create");
            Assert.Contains(steps.EnumerateArray(), step => step.GetProperty("path").GetString() == "Target.cs");
            Assert.Contains(steps.EnumerateArray(), step => step.GetProperty("path").GetString() == "HelperService.cs");

            Assert.Contains("Target: ", await File.ReadAllTextAsync(targetPath));
            Assert.Contains("updated helper", await File.ReadAllTextAsync(helperPath));
            Assert.Equal("Generated note: helper output updated.", await File.ReadAllTextAsync(notePath));
        }
    }

    [Fact]
    public async Task ExecuteStream_runs_code_edit_pipeline_with_attached_files_after_context_confirmation()
    {
        var project = "codeedit-stream";
        var projectRoot = Path.Combine(_factory.WorkspaceRootPath, project);
        var targetPath = Path.Combine(projectRoot, "AttachedTarget.cs");

        Directory.CreateDirectory(projectRoot);
        await File.WriteAllTextAsync(targetPath, """
namespace Sample;

public static class AttachedTarget
{
    public static string ReadMessage()
    {
        return "before";
    }
}
""", Encoding.UTF8);
await WriteConfigAsync(buildCommands: "");

var (client, _, store, factory) = CreateClientWithAgentServicesAndPendingStore(
    responder: AttachedFileCodeEditResponse);

        using (factory)
        {
            var responseTask = client.PostAsJsonAsync("/api/agent/execute-stream", new
            {
                prompt = "Update the attached target message.",
                project,
                files = new[] { "AttachedTarget.cs" }
            });

            var reviewId = await WaitForPendingContextReviewIdAsync(store, responseTask, TimeSpan.FromSeconds(10));
            using var confirmClient = factory.CreateClient();
            var confirmResponse = await confirmClient.PostAsJsonAsync("/api/agent/context-review/confirm", new
            {
                id = reviewId,
                files = new[] { "AttachedTarget.cs" }
            });
            confirmResponse.EnsureSuccessStatusCode();

            var response = await responseTask;
            response.EnsureSuccessStatusCode();

            var events = ParseSseEvents(await response.Content.ReadAsStringAsync());
            Assert.Contains(events, e => e.Event == "context-review");
            Assert.Contains(events, e => e.Event == "plan");
            Assert.Contains(events, e => e.Event == "done");

            var done = Assert.Single(events, e => e.Event == "done");
            using var doneJson = JsonDocument.Parse(done.Data);
            Assert.True(doneJson.RootElement.GetProperty("complete").GetBoolean());
            Assert.True(doneJson.RootElement.GetProperty("editsApplied").GetBoolean());
            Assert.Contains("after", await File.ReadAllTextAsync(targetPath));
        }
    }

    [Fact]
    public async Task Apply_returns_bad_request_when_no_edits_are_provided()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/agent/apply", new { project = "", edits = Array.Empty<object>(), commands = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("No edits provided", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Apply_writes_existing_and_new_files_and_reports_command_results()
    {
        var existingPath = Path.Combine(_factory.WorkspaceRootPath, "sample", "existing.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(existingPath)!);
        await File.WriteAllTextAsync(existingPath, "hello world", Encoding.UTF8);

        var (client, terminal, factory) = CreateClientWithAgentServices(configureTerminal: terminal =>
        {
            terminal.OutputFactory = (command, _) => command.Equals("echo after", StringComparison.OrdinalIgnoreCase)
                ? "after ok" + Environment.NewLine
                : ScriptedTerminalService.DefaultOutput(command);
            terminal.ExceptionFactory = (command, _) => command.Equals("explode", StringComparison.OrdinalIgnoreCase)
                ? new InvalidOperationException("boom")
                : null;
        });

        using (factory)
        {
            var response = await client.PostAsJsonAsync("/api/agent/apply", new
            {
                project = "",
                edits = new object[]
                {
                    new { path = "sample/existing.txt", oldString = "hello", newString = "goodbye" },
                    new { path = "sample/new.txt", oldString = "", newString = "fresh file" }
                },
                commands = new object[]
                {
                    new { command = "echo after" },
                    new { command = "explode" }
                }
            });

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<ApplyResponse>();

            Assert.NotNull(body);
            Assert.Equal(new[] { "written", "written" }, body!.Edits.Select(e => e.Status).ToArray());
            Assert.Equal(new[] { "done", "error" }, body.Commands.Select(c => c.Status).ToArray());
            Assert.Contains("after ok", body.Commands[0].Output);
            Assert.Equal("boom", body.Commands[1].Error);
            Assert.Contains("echo after", terminal.Commands);
            Assert.Contains("explode", terminal.Commands);
            Assert.Equal("goodbye world", await File.ReadAllTextAsync(existingPath));
            Assert.Equal("fresh file", await File.ReadAllTextAsync(Path.Combine(_factory.WorkspaceRootPath, "sample", "new.txt")));
        }
    }

    [Fact]
    public async Task Apply_skips_outside_root_and_missing_file_edits()
    {
        var (client, _, factory) = CreateClientWithAgentServices();
        using (factory)
        {
            var response = await client.PostAsJsonAsync("/api/agent/apply", new
            {
                project = "",
                edits = new object[]
                {
                    new { path = "../outside.txt", oldString = "", newString = "x" },
                    new { path = "missing.txt", oldString = "before", newString = "after" }
                }
            });

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<ApplyResponse>();

            Assert.NotNull(body);
            Assert.Equal(new[] { "skipped", "skipped" }, body!.Edits.Select(e => e.Status).ToArray());
            Assert.Equal("Path outside project root", body.Edits[0].Error);
            Assert.Equal("File does not exist", body.Edits[1].Error);
        }
    }

    [Fact]
    public async Task Apply_returns_error_when_old_string_does_not_match_file()
    {
        var filePath = Path.Combine(_factory.WorkspaceRootPath, "sample", "mismatch.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "actual content", Encoding.UTF8);

        var (client, _, factory) = CreateClientWithAgentServices();
        using (factory)
        {
            var response = await client.PostAsJsonAsync("/api/agent/apply", new
            {
                project = "",
                edits = new object[]
                {
                    new { path = "sample/mismatch.txt", oldString = "missing content", newString = "replacement" }
                }
            });

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<ApplyResponse>();

            Assert.NotNull(body);
            Assert.Single(body!.Edits);
            Assert.Equal("error", body.Edits[0].Status);
            Assert.Equal("oldString not found in file", body.Edits[0].Error);
            Assert.Equal("actual content", await File.ReadAllTextAsync(filePath));
        }
    }

    [Fact]
    public async Task GetPendingQuestions_returns_ordered_questions()
    {
        var (client, store, factory) = CreateClientWithPendingStore();
        using (factory)
        {
            store.SetQuestion(new PendingQuestion
            {
                Id = "q-late",
                Question = "Later question",
                CreatedUtc = DateTime.UtcNow.AddMinutes(2),
                Fields = new List<QuestionField> { new() { Key = "name", Label = "Name" } }
            });
            store.SetQuestion(new PendingQuestion
            {
                Id = "q-early",
                Question = "Earlier question",
                CreatedUtc = DateTime.UtcNow.AddMinutes(1),
                Fields = new List<QuestionField> { new() { Key = "email", Label = "Email" } }
            });

            var response = await client.GetAsync("/api/agent/questions/pending");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<PendingQuestionsResponse>();
            Assert.NotNull(json);
            Assert.Equal(new[] { "q-early", "q-late" }, json!.Questions.Select(q => q.Id).ToArray());
        }
    }

    [Fact]
    public async Task AnswerQuestion_sets_answer_for_pending_question()
    {
        var (client, store, factory) = CreateClientWithPendingStore();
        using (factory)
        {
            var pending = new PendingQuestion
            {
                Id = "q-1",
                Question = "Which project?",
                CreatedUtc = DateTime.UtcNow
            };
            store.SetQuestion(pending);

            var response = await client.PostAsJsonAsync("/api/agent/questions/answer", new
            {
                id = "q-1",
                answers = new Dictionary<string, string> { ["project"] = "MaestroBackend" }
            });

            response.EnsureSuccessStatusCode();
            var answers = await pending.Answer.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("MaestroBackend", answers["project"]);
        }
    }

    [Fact]
    public async Task AnswerQuestion_returns_not_found_for_missing_id()
    {
        var (client, _, factory) = CreateClientWithPendingStore();
        using (factory)
        {
            var response = await client.PostAsJsonAsync("/api/agent/questions/answer", new
            {
                id = "missing",
                answers = new Dictionary<string, string>()
            });

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task ConfirmContextReview_uses_request_files_when_provided()
    {
        var (client, store, factory) = CreateClientWithPendingStore();
        using (factory)
        {
            var pending = new PendingContextReview
            {
                Id = "ctx-1",
                CreatedUtc = DateTime.UtcNow,
                Files = new List<string> { "Program.cs" }
            };
            store.SetContextReview(pending);

            var response = await client.PostAsJsonAsync("/api/agent/context-review/confirm", new
            {
                id = "ctx-1",
                files = new[] { "Controllers/AgentController.cs", "Services/AgentUtilities.cs" }
            });

            response.EnsureSuccessStatusCode();
            var selected = await pending.Answer.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, selected.Count);
            Assert.Contains("Controllers/AgentController.cs", selected);
        }
    }

    [Fact]
    public async Task ConfirmContextReview_returns_not_found_for_missing_id()
    {
        var (client, _, factory) = CreateClientWithPendingStore();
        using (factory)
        {
            var response = await client.PostAsJsonAsync("/api/agent/context-review/confirm", new
            {
                id = "missing",
                files = Array.Empty<string>()
            });

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    private sealed class PendingQuestionsResponse
    {
        public List<PendingQuestionItem> Questions { get; set; } = new();
    }

    private sealed class PendingQuestionItem
    {
        public string Id { get; set; } = "";
    }

    private sealed class ApplyResponse
    {
        public List<ApplyEditResult> Edits { get; set; } = new();
        public List<ApplyCommandResult> Commands { get; set; } = new();
    }

    private sealed class ApplyEditResult
    {
        public string Path { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Error { get; set; }
    }

    private sealed class ApplyCommandResult
    {
        public string Command { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Output { get; set; }
        public string? Error { get; set; }
    }

    private sealed class SseEvent
    {
        public string Event { get; set; } = "";
        public string Data { get; set; } = "";
    }

    private sealed class ScriptedTerminalService : ITerminalService
    {
        private readonly List<string> _commands = new();
        private readonly List<PendingTerminalApprovalDto> _pending = new();
        private readonly object _sync = new();

        public Func<string, string?, string>? OutputFactory { get; set; }
        public Func<string, string?, Exception?>? ExceptionFactory { get; set; }
        public bool IsRunning { get; private set; }

        public IReadOnlyList<string> Commands
        {
            get
            {
                lock (_sync)
                {
                    return _commands.ToList();
                }
            }
        }

        public string Output { get; private set; } = "";

        public void Start(string? shell = null, string args = "/K")
        {
            IsRunning = true;
        }

        public Task SendCommandAsync(string command, string? workingDirectory = null)
        {
            lock (_sync)
            {
                _commands.Add(command);
            }

            var error = ExceptionFactory?.Invoke(command, workingDirectory);
            if (error != null)
                throw error;

            var output = OutputFactory?.Invoke(command, workingDirectory) ?? DefaultOutput(command);
            lock (_sync)
            {
                Output += output.EndsWith(Environment.NewLine, StringComparison.Ordinal)
                    ? output
                    : output + Environment.NewLine;
            }

            return Task.CompletedTask;
        }

        public IReadOnlyList<PendingTerminalApprovalDto> GetPendingApprovals()
        {
            lock (_sync)
            {
                return _pending.ToList();
            }
        }

        public Task<bool> ApproveCommandAsync(string id, string scope = "once") => Task.FromResult(false);

        public bool RejectCommand(string id) => false;

        public string ReadAll()
        {
            lock (_sync)
            {
                return Output;
            }
        }

        public string ReadLastLines(int lines = 200)
        {
            lock (_sync)
            {
                var split = Output
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .TakeLast(Math.Max(0, lines));
                return string.Join(Environment.NewLine, split);
            }
        }

        public static string DefaultOutput(string command)
        {
            if (command.Contains("Test-NetConnection", StringComparison.OrdinalIgnoreCase))
                return "TcpTestSucceeded : True" + Environment.NewLine;
            if (command.StartsWith("git pull", StringComparison.OrdinalIgnoreCase))
                return "Already up to date." + Environment.NewLine;
            return command + Environment.NewLine + "done" + Environment.NewLine;
        }
    }

    private static void ResetConnectivityCache()
    {
        typeof(AgentController)
            .GetField("_nextConnectivityCheck", BindingFlags.NonPublic | BindingFlags.Static)
            ?.SetValue(null, DateTime.MinValue);
    }

    private static List<SseEvent> ParseSseEvents(string body)
    {
        return body.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block =>
            {
                var evt = new SseEvent();
                foreach (var line in block.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("event: ", StringComparison.Ordinal))
                        evt.Event = line["event: ".Length..];
                    else if (line.StartsWith("data: ", StringComparison.Ordinal))
                        evt.Data = line["data: ".Length..];
                }
                return evt;
            })
            .ToList();
    }

    private async Task WriteConfigAsync(string buildCommands)
    {
        var configPath = Path.Combine(_factory.ContentRootPath, "maestroconfig.json");
        Directory.CreateDirectory(_factory.ContentRootPath);
        var config = new FrontendConfig
        {
            buildCommands = buildCommands
        };
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(configPath, json, Encoding.UTF8);
    }

    private static async Task<string> WaitForPendingContextReviewIdAsync(
        IAgentPendingStore store,
        Task<HttpResponseMessage> responseTask,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var id = store.GetContextReviews()
                .Select(review => review.Id)
                .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
            if (!string.IsNullOrWhiteSpace(id))
                return id;

            if (responseTask.IsCompleted)
            {
                var response = await responseTask;
                var body = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    $"execute-stream completed before context review was available. Status={(int)response.StatusCode}. Body: {body}");
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Timed out waiting for pending context review.");
    }

    private static HttpResponseMessage CreateChatCompletionResponse(string content)
    {
        var payload = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content
                    }
                }
            }
        });

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage ClassificationResponse(HttpRequestMessage request)
    {
        return request.RequestUri!.AbsolutePath == "/v1/chat/completions"
            ? CreateChatCompletionResponse("""{"pipeline":"CommandExecution"}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage CrossFileCodeEditResponse(
        HttpRequestMessage request,
        Action? onPlan = null,
        Action? onCreateFile = null)
    {
        if (request.RequestUri?.AbsolutePath != "/v1/chat/completions")
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        var (system, _) = ReadLlmMessages(request);

        // Phase 2 — PLAN: one unified plan carrying inline edits + a file-creation step.
        // score=100 so the planner accepts it on the first attempt (no retry loop).
        if (system.StartsWith("You are a software-engineering agent.", StringComparison.Ordinal))
        {
            onPlan?.Invoke();
            return CreateChatCompletionResponse(
                """
                {"thinking":"Update the target and helper output and add a generated note file.","summary":"Edit Target.cs and HelperService.cs and create a note file.","score":100,"plan":[{"file":"_create_file","change":"Notes/GeneratedNote.txt: a generated note about the helper update","oldString":"","newString":"","priority":1},{"file":"Target.cs","change":"Prefix the BuildMessage result with the target label.","oldString":"        return HelperService.DoWork();","newString":"        return \"Target: \" + HelperService.DoWork();","priority":2},{"file":"HelperService.cs","change":"Update DoWork to return the new helper text.","oldString":"        return \"old helper\";","newString":"        return \"updated helper\";","priority":3}]}
                """);
        }

        // _create_file content generation
        if (system.Contains("You are a file creation assistant.", StringComparison.Ordinal))
        {
            onCreateFile?.Invoke();
            return CreateChatCompletionResponse("Generated note: helper output updated.");
        }

        return new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent($"Unhandled test LLM prompt: {system}")
        };
    }

    private static HttpResponseMessage AttachedFileCodeEditResponse(HttpRequestMessage request)
    {
        if (request.RequestUri?.AbsolutePath != "/v1/chat/completions")
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        var (system, _) = ReadLlmMessages(request);

        // Phase 2 — PLAN: a single edit to the attached file, with inline oldString/newString.
        if (system.StartsWith("You are a software-engineering agent.", StringComparison.Ordinal))
        {
            return CreateChatCompletionResponse(
                """
                {"thinking":"Update the attached file message.","summary":"Modify AttachedTarget.cs to return the new message.","score":100,"plan":[{"file":"AttachedTarget.cs","change":"Change ReadMessage so it returns the updated value.","oldString":"        return \"before\";","newString":"        return \"after\";","priority":1}]}
                """);
        }

        return new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent($"Unhandled test LLM prompt: {system}")
        };
    }

    private static (string system, string user) ReadLlmMessages(HttpRequestMessage request)
    {
        var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "{}";
        using var json = JsonDocument.Parse(body);
        var messages = json.RootElement.GetProperty("messages").EnumerateArray().ToList();

        var system = messages.FirstOrDefault(message =>
            string.Equals(message.GetProperty("role").GetString(), "system", StringComparison.Ordinal))
            .GetProperty("content").GetString() ?? "";
        var user = messages.FirstOrDefault(message =>
            string.Equals(message.GetProperty("role").GetString(), "user", StringComparison.Ordinal))
            .GetProperty("content").GetString() ?? "";

        return (system, user);
    }
}
