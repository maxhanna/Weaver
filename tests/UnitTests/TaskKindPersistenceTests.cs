using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the up-front dump-vs-build classification surfacing on the kanban card:
/// <c>card._taskKind</c> is written to boarddata (\"dump\" for a fetch-and-write task,
/// \"build\" for a script/program request) so a run that short-circuits deterministically is
/// visibly distinguishable from a build run. Mirrors VerificationPersistenceTests:
///   • PublishTaskKindAsync writes/clears _taskKind on the card
///   • A later PersistBoardDataPlanAsync rebuild must NOT wipe it (lives at card level)
///   • Unknown cardId is a silent no-op
///   • ClassifyTaskKind yields dump / build / null
/// </summary>
public class TaskKindPersistenceTests
{
    private static (AgentController controller, BoardDataService boardData) BuildHarness()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-taskkind-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var db = new DatabaseService(Path.Combine(dir, "weaver.db"), dir, Path.Combine(dir, "weaverconfig.json"));
        var boardData = new BoardDataService(db, NullLogger<BoardDataService>.Instance);
        var controller = (AgentController)RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        var field = typeof(AgentController).GetField("_boardData", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("_boardData field not found");
        field.SetValue(controller, boardData);
        return (controller, boardData);
    }

    private static Task InvokePublishTaskKind(AgentController controller, string cardId, string? taskKind)
    {
        var method = typeof(AgentController).GetMethod("PublishTaskKindAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("PublishTaskKindAsync not found");
        return (Task)method.Invoke(controller, new object?[] { cardId, taskKind, false, CancellationToken.None })!;
    }

    private static Task InvokePersistPlan(AgentController controller, string cardId, List<PlanStep> steps)
    {
        var method = typeof(AgentController).GetMethod("PersistBoardDataPlanAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("PersistBoardDataPlanAsync not found");
        return (Task)method.Invoke(controller, new object?[] { cardId, steps, false, CancellationToken.None, "test summary", 1, false })!;
    }

    private static string? InvokeClassifyTaskKind(string prompt, string projectRoot)
        => AgentPlanningPolicy.ClassifyTaskKind(prompt, projectRoot);

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
        var card = new Dictionary<string, object?>
        {
            ["id"] = cardId,
            ["text"] = "task",
            ["filePath"] = "C:/x"
        };
        board[column] = new List<object> { card };
        return JsonSerializer.Serialize(board);
    }

    private static string? ReadCardProperty(string raw, string cardId, string property)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        using var doc = JsonDocument.Parse(raw);
        foreach (var col in new[] { "todo", "doing", "done", "selfImproving" })
        {
            if (!doc.RootElement.TryGetProperty(col, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var card in arr.EnumerateArray())
            {
                if (!card.TryGetProperty("id", out var id) || id.GetString() != cardId) continue;
                return card.TryGetProperty(property, out var prop) ? prop.GetString() : null;
            }
        }
        return null;
    }

    [Fact]
    public async Task PublishTaskKind_WritesDumpOntoCard()
    {
        var (controller, boardData) = BuildHarness();
        const string cardId = "taskkind-card-1";
        await boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));

        await InvokePublishTaskKind(controller, cardId, "dump");

        var raw = await boardData.LoadRawAsync();
        Assert.Equal("dump", ReadCardProperty(raw!, cardId, "_taskKind"));
    }

    [Fact]
    public async Task PublishTaskKind_WritesBuildOntoCard()
    {
        var (controller, boardData) = BuildHarness();
        const string cardId = "taskkind-card-2";
        await boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));

        await InvokePublishTaskKind(controller, cardId, "build");

        var raw = await boardData.LoadRawAsync();
        Assert.Equal("build", ReadCardProperty(raw!, cardId, "_taskKind"));
    }

    [Fact]
    public async Task PublishTaskKind_Null_ClearsTheBadge()
    {
        var (controller, boardData) = BuildHarness();
        const string cardId = "taskkind-card-3";
        await boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));
        await InvokePublishTaskKind(controller, cardId, "dump");

        await InvokePublishTaskKind(controller, cardId, null);

        var raw = await boardData.LoadRawAsync();
        Assert.Null(ReadCardProperty(raw!, cardId, "_taskKind"));
    }

    [Fact]
    public async Task PublishTaskKind_SurvivesPlanRebuild()
    {
        var (controller, boardData) = BuildHarness();
        const string cardId = "taskkind-card-4";
        await boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));
        await InvokePublishTaskKind(controller, cardId, "dump");

        await InvokePersistPlan(controller, cardId,
            new List<PlanStep> { new() { File = "_web_fetch", Change = "https://pokeapi.co" } });

        var raw = await boardData.LoadRawAsync();
        Assert.Equal("dump", ReadCardProperty(raw!, cardId, "_taskKind"));
    }

    [Fact]
    public async Task PublishTaskKind_UnknownCardId_IsNoop()
    {
        var (controller, boardData) = BuildHarness();
        await boardData.SaveRawAsync(BoardWithCard("other-card", "doing"));

        await InvokePublishTaskKind(controller, "missing-card", "dump");

        var raw = await boardData.LoadRawAsync();
        Assert.Null(ReadCardProperty(raw!, "missing-card", "_taskKind"));
        Assert.Null(ReadCardProperty(raw!, "other-card", "_taskKind"));
    }

    [Fact]
    public void ClassifyTaskKind_WebFileDemand_Dump()
    {
        var root = Path.Combine(Path.GetTempPath(), "weaver-taskkind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal("dump", InvokeClassifyTaskKind(
                "Fetch the live Pokemon data from PokeAPI and write the data into benchmark_test_16/pokemon_data.csv.", root));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void ClassifyTaskKind_ScriptRequest_Build()
    {
        var root = Path.Combine(Path.GetTempPath(), "weaver-taskkind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal("build", InvokeClassifyTaskKind(
                "Write a python script that fetches the live Pokemon data and writes benchmark_test_16/pokemon_data.csv.", root));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void ClassifyTaskKind_BuildAndVerifyWebApp_NotDump()
    {
        // The benchmark-23 regression: a BUILD + browser-verify web-app task whose "write what
        // you saw to legs_report.txt" line is a REPORTING artifact — the "current leg count"
        // phrase trips the broad web-need hint and the legs_report.txt target trips the dump
        // file-output pattern, so this used to classify as "dump" and the deterministic dump
        // short-circuit completed the run the instant the report file existed (the 6-leg fix
        // and the browser tests never ran). A build-and-verify task must NEVER be a dump.
        var root = Path.Combine(Path.GetTempPath(), "weaver-taskkind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var prompt =
                "Create a folder called 'benchmark_test_23' at the project root. Inside it, build a small web app that draws an ANIMATED spider on a <canvas>, then FIX the animation and VISUALLY VERIFY the fix with the live browser test suite. " +
                "Serve 'index.html' at / with a heading, a <canvas> that ANIMATES a spider using requestAnimationFrame, and expose the current leg count as window.legCount. " +
                "Use the live browser test to confirm the spider has EXACTLY 4 legs and write what you saw to benchmark_test_23/legs_report.txt with a line in this exact format: LEGS: 4. " +
                "Then edit the animation to add 2 more legs so window.legCount equals 6, reload the server, run the live browser test again, and append a line: LEGS: 6.";
            Assert.NotEqual("dump", InvokeClassifyTaskKind(prompt, root));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void ClassifyTaskKind_OrdinaryEdit_Null()
    {
        Assert.Null(InvokeClassifyTaskKind("Add a method to the service.", Path.GetTempPath()));
    }
}
