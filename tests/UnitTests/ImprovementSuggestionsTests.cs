using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Tests for the improvement-suggestions persist/read helpers used by the
/// POST api/agent/suggest-improvements endpoint. These helpers are the
/// deterministic half of the feature (the LLM half can't be unit-tested):
/// a generated suggestion set must round-trip onto the card in board data and
/// be read back without regenerating.
///
/// Coverage locked in here:
///   • Persist → Read returns the same suggestions (idempotent round-trip)
///   • A card that already has suggestions returns them WITHOUT re-running
///   • A card that legitimately got 0 suggestions (empty array) is still
///     treated as "done" — the reader returns the empty list, not null, so
///     the LLM is never re-invoked for it.
/// </summary>
public class ImprovementSuggestionsTests
{
    private static (AgentController controller, DatabaseService db, string dbPath) BuildHarness()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-sugg-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "weaver.db");
        var db = new DatabaseService(dbPath, dir, Path.Combine(dir, "weaverconfig.json"));
        var boardData = new BoardDataService(db, NullLogger<BoardDataService>.Instance);
        var controller = (AgentController)RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        var field = typeof(AgentController).GetField("_boardData", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("_boardData field not found");
        field.SetValue(controller, boardData);
        var dbField = typeof(AgentController).GetField("_db", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("_db field not found");
        dbField.SetValue(controller, db);
        return (controller, db, dbPath);
    }

    private static void SeedBoard(DatabaseService db, string cardId, string column)
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
            new Dictionary<string, object?> { ["id"] = cardId, ["text"] = "task", ["filePath"] = "C:/x" }
        };
        var json = System.Text.Json.JsonSerializer.Serialize(board);
        db.SetBoardData(json);
    }

    private static async Task<List<object>?> InvokeRead(AgentController controller, string cardId)
    {
        var method = typeof(AgentController).GetMethod("ReadCardSuggestionsAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ReadCardSuggestionsAsync not found");
        var task = (Task<List<object>?>)method.Invoke(controller, new object?[] { cardId })!;
        return await task;
    }

    private static Task InvokePersist(AgentController controller, string cardId, List<object> suggestions)
    {
        var method = typeof(AgentController).GetMethod("PersistCardSuggestionsAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("PersistCardSuggestionsAsync not found");
        return (Task)method.Invoke(controller, new object?[] { cardId, suggestions })!;
    }

    private static Task InvokePersistContext(AgentController controller, string cardId, object context)
    {
        var method = typeof(AgentController).GetMethod("PersistCardSuggestionsContextAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("PersistCardSuggestionsContextAsync not found");
        return (Task)method.Invoke(controller, new object?[] { cardId, context })!;
    }

    [Fact]
    public async Task Persist_Then_Read_RoundTrips_Suggestions()
    {
        var (controller, db, dbPath) = BuildHarness();
        try
        {
            SeedBoard(db, "card-1", "done");
            var suggestions = new List<object>
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "abc12345",
                    ["description"] = "Add error handling",
                    ["files"] = new List<string> { "src/app/globe.ts" },
                    ["createdAt"] = "2026-08-04T00:00:00Z"
                },
                new Dictionary<string, object?>
                {
                    ["id"] = "def67890",
                    ["description"] = "Extract a service",
                    ["files"] = new List<string>(),
                    ["createdAt"] = "2026-08-04T00:00:00Z"
                }
            };

            await InvokePersist(controller, "card-1", suggestions);
            var read = await InvokeRead(controller, "card-1");

            Assert.NotNull(read);
            Assert.Equal(2, read!.Count);
            var first = read[0] as System.Text.Json.JsonElement? ?? default;
            Assert.Contains("Add error handling", read[0].ToString());
            Assert.Contains("Extract a service", read[1].ToString());
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, true); } catch { }
        }
    }

    [Fact]
    public async Task Read_On_Empty_Array_Returns_Empty_Not_Null()
    {
        var (controller, db, dbPath) = BuildHarness();
        try
        {
            SeedBoard(db, "card-2", "todo");
            // A card that legitimately earned 0 suggestions stores an empty array.
            await InvokePersist(controller, "card-2", new List<object>());
            var read = await InvokeRead(controller, "card-2");
            // Must NOT be null: null would make the endpoint re-run the LLM.
            Assert.NotNull(read);
            Assert.Empty(read!);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, true); } catch { }
        }
    }

    [Fact]
    public async Task Read_Returns_Null_When_No_Suggestions_Persisted()
    {
        var (controller, db, dbPath) = BuildHarness();
        try
        {
            SeedBoard(db, "card-3", "selfImproving");
            var read = await InvokeRead(controller, "card-3");
            Assert.Null(read);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, true); } catch { }
        }
    }

    // ── "More like this" top-up dedupe ────────────────────────────────────
    // The top-up endpoint feeds the existing suggestions back to the LLM and
    // then filters the NEW ones through IsDuplicateSuggestion so the set is
    // extended rather than repeated. These helpers are pure/static, so they're
    // unit-tested directly (the endpoint's LLM half can't be).

    private static string InvokeNormalize(string s)
    {
        var method = typeof(AgentController).GetMethod("NormalizeSuggestionText",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("NormalizeSuggestionText not found");
        return (string)method.Invoke(null, new object?[] { s })!;
    }

    private static bool InvokeIsDuplicate(string desc, IEnumerable<string> known)
    {
        var method = typeof(AgentController).GetMethod("IsDuplicateSuggestion",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("IsDuplicateSuggestion not found");
        return (bool)method.Invoke(null, new object?[] { desc, known })!;
    }

    [Fact]
    public async Task Persist_Context_RoundTrips_Alongside_Suggestions()
    {
        var (controller, db, dbPath) = BuildHarness();
        try
        {
            SeedBoard(db, "card-ctx", "done");
            var suggestions = new List<object>
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "abc12345",
                    ["description"] = "Add error handling",
                    ["files"] = new List<string> { "src/app/globe.ts" },
                    ["createdAt"] = "2026-08-06T00:00:00Z"
                }
            };
            await InvokePersist(controller, "card-ctx", suggestions);
            await InvokePersistContext(controller, "card-ctx", new Dictionary<string, object?>
            {
                ["summary"] = "Refactored the upload flow",
                ["thinking"] = "The upload flow retried too aggressively, so I capped retries at 3.",
                // NOTE: keep these ASCII — System.Text.Json escapes non-ASCII
                // (e.g. "✓") as \uXXXX in board data, so a literal assert on the
                // raw string would fail even though the data round-trips fine.
                ["steps"] = new List<string> { "src/upload.ts - cap retries" },
                ["planItems"] = new List<string> { "Refactor upload retries" },
                ["filesEdited"] = new List<string> { "src/upload.ts" },
                ["generatedAt"] = "2026-08-06T00:00:00Z"
            });

            var raw = db.GetBoardData();
            Assert.NotNull(raw);
            var boardJson = raw!;
            Assert.Contains("_suggestions", boardJson);
            Assert.Contains("_suggestionsContext", boardJson);
            Assert.Contains("Refactored the upload flow", boardJson);
            Assert.Contains("capped retries at 3", boardJson);
            Assert.Contains("Refactor upload retries", boardJson);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, true); } catch { }
        }
    }

    [Fact]
    public void Normalize_Suggestion_Text_Is_Case_And_Punctuation_Insensitive()
    {
        Assert.Equal("add error handling", InvokeNormalize("Add  error handling!"));
        Assert.Equal("extract a service", InvokeNormalize("Extract a service."));
    }

    [Fact]
    public void TopUp_Dedupe_Drops_Exact_And_Contained_Repeats()
    {
        // Exact normalized match → duplicate.
        Assert.True(InvokeIsDuplicate("Add error handling.", new[] { "add error handling" }));
        // A more specific suggestion that embeds the existing phrase → duplicate.
        Assert.True(InvokeIsDuplicate("Add error handling for the upload flow", new[] { "add error handling" }));
    }

    [Fact]
    public void TopUp_Dedupe_Allows_Genuinely_Distinct_Suggestions()
    {
        // Different topic entirely → not a duplicate.
        Assert.False(InvokeIsDuplicate("Add retry logic to the upload service", new[] { "add error handling" }));
        // Same general area but a distinct ask → not a duplicate.
        Assert.False(InvokeIsDuplicate("Write tests for the upload flow", new[] { "add error handling" }));
    }

    [Fact]
    public void TopUp_Dedupe_Drops_Repeats_Within_The_Same_Batch()
    {
        var known = new[] { "add error handling" };
        // Simulate the parse loop: the first new suggestion is distinct from the
        // known set and gets kept; the second is a containment rephrase of the
        // first and must be dropped.
        var batch = new List<string>();
        var first = "Add retry logic to the upload service";
        if (!InvokeIsDuplicate(first, known.Concat(batch))) batch.Add(first);
        var second = "Add retry logic to the upload service with exponential backoff";
        if (!InvokeIsDuplicate(second, known.Concat(batch))) batch.Add(second);
        Assert.Single(batch);
        Assert.Equal("Add retry logic to the upload service", batch[0]);
    }

    // ── Board-wide project context helpers ────────────────────────────────
    // The suggestion endpoint now feeds the LLM a broader application view (other
    // kanban cards, project skeleton, git history) so it can propose cross-feature
    // integrations. These pure helpers are deterministic and unit-tested directly.

    private static string InvokeStripContext(string s)
    {
        var method = typeof(AgentController).GetMethod("StripSuggestionContext",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("StripSuggestionContext not found");
        return (string)method.Invoke(null, new object?[] { s })!;
    }

    private static string InvokeTruncate(string s, int max)
    {
        var method = typeof(AgentController).GetMethod("TruncateForContext",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("TruncateForContext not found");
        return (string)method.Invoke(null, new object?[] { s, max })!;
    }

    [Fact]
    public void StripSuggestionContext_Removes_Prefixed_Context_Block()
    {
        // Suggestion-derived cards prepend [CONTEXT — source ref — summary][/CONTEXT].
        var text = "[CONTEXT — #abc123 — completion summary of the source task]\n" +
                   "Built the admin ban flow.\n[/CONTEXT]\n\nMake banning notify the banned user";
        Assert.Equal("Make banning notify the banned user", InvokeStripContext(text));
    }

    [Fact]
    public void StripSuggestionContext_Leaves_Plain_Task_Text_Untouched()
    {
        var plain = "Build the admin ban flow with role checks";
        Assert.Equal(plain, InvokeStripContext(plain));
        // Case-insensitive marker must still be stripped.
        var upper = "[CONTEXT][/CONTEXT] Follow-up task";
        Assert.Equal("Follow-up task", InvokeStripContext(upper));
    }

    [Fact]
    public void TruncateForContext_Caps_Long_Text_And_Keeps_Short_Text()
    {
        var longText = new string('a', 500);
        var capped = InvokeTruncate(longText, 100);
        Assert.Equal(101, capped.Length); // 100 chars + ellipsis
        Assert.StartsWith(new string('a', 100), capped);
        Assert.EndsWith("…", capped);
        Assert.Equal("short task", InvokeTruncate("  short task  ", 100));
    }

    // ── Per-project suggestion context depth ──────────────────────────────
    // The per-project setting controls how much whole-app context the suggestion
    // endpoint sends (skeleton only / + board history / + git). The pure normalizer
    // is unit-tested directly; anything unrecognized must fall back to "full".

    private static string InvokeNormalizeDepth(string? s)
    {
        var method = typeof(AgentController).GetMethod("NormalizeSuggestionDepth",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("NormalizeSuggestionDepth not found");
        return (string)method.Invoke(null, new object?[] { s })!;
    }

    [Fact]
    public void SuggestionDepth_Normalizes_Known_Values()
    {
        Assert.Equal("full", InvokeNormalizeDepth("full"));
        Assert.Equal("full", InvokeNormalizeDepth("  Full ")); // case/whitespace tolerant
        Assert.Equal("board", InvokeNormalizeDepth("board"));
        Assert.Equal("board", InvokeNormalizeDepth("board-history"));
        Assert.Equal("board", InvokeNormalizeDepth("board_history"));
        Assert.Equal("skeleton", InvokeNormalizeDepth("skeleton"));
        Assert.Equal("skeleton", InvokeNormalizeDepth("Skeleton"));
    }

    [Fact]
    public void SuggestionDepth_Unknown_And_Empty_Fall_Back_To_Full()
    {
        Assert.Equal("full", InvokeNormalizeDepth(""));
        Assert.Equal("full", InvokeNormalizeDepth(null));
        Assert.Equal("full", InvokeNormalizeDepth("garbage"));
        Assert.Equal("full", InvokeNormalizeDepth("everything"));
    }

    // ── Idle-only guard: suggestions must never run while a card executes ────
    // The suggestion system only works while the board is completely idle. The pure
    // guard: allowed when nothing is executing, or when the executing card IS the one
    // the suggestions are for (it just finished — the completion-triggered flow, whose
    // run is winding down). Blocked whenever a DIFFERENT card is executing.

    [Fact]
    public void SuggestionGuard_NoCardExecuting_Allowed()
    {
        Assert.True(AgentController.SuggestionAllowedWhileExecuting(false, false, "card-1"));
        Assert.True(AgentController.SuggestionAllowedWhileExecuting(false, false, null));
    }

    [Fact]
    public void SuggestionGuard_OtherCardExecuting_Blocked()
    {
        // An idle-loop request for card-1 while card-2 is running must be refused.
        Assert.False(AgentController.SuggestionAllowedWhileExecuting(true, false, "card-1"));
        // Unknown card (empty id) while something executes → refused.
        Assert.False(AgentController.SuggestionAllowedWhileExecuting(true, false, null));
        Assert.False(AgentController.SuggestionAllowedWhileExecuting(true, false, ""));
    }

    [Fact]
    public void SuggestionGuard_SameCardExecuting_Allowed()
    {
        // The completion-triggered request for the card that JUST finished: its run entry
        // may still exist while the run winds down — suggestions for THIS card are the point.
        Assert.True(AgentController.SuggestionAllowedWhileExecuting(true, true, "card-1"));
        // An empty/unidentifiable card id while something executes is refused — the guard
        // cannot verify the executing card IS this one, so it errs on the side of blocking.
        Assert.False(AgentController.SuggestionAllowedWhileExecuting(true, true, ""));
    }

    private static async Task<bool> InvokeIsCardBenchmark(AgentController controller, string cardId)
    {
        var method = typeof(AgentController).GetMethod("IsCardBenchmarkAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("IsCardBenchmarkAsync not found");
        var task = (Task<bool>)method.Invoke(controller, new object?[] { cardId })!;
        return await task;
    }

    private static void SeedBoardCard(DatabaseService db, string cardId, string column, bool benchmarkFlag, string filePath)
    {
        var board = new Dictionary<string, object?>
        {
            ["todo"] = new List<object>(),
            ["doing"] = new List<object>(),
            ["done"] = new List<object>(),
            ["archived"] = new List<object>(),
            ["selfImproving"] = new List<object>()
        };
        var card = new Dictionary<string, object?> { ["id"] = cardId, ["text"] = "task", ["filePath"] = filePath };
        if (benchmarkFlag) card["_benchmark"] = true;
        board[column] = new List<object> { card };
        db.SetBoardData(System.Text.Json.JsonSerializer.Serialize(board));
    }

    [Fact]
    public async Task IsCardBenchmark_FlaggedCard_True()
    {
        var (controller, db, dbPath) = BuildHarness();
        try
        {
            SeedBoardCard(db, "bench-1", "done", benchmarkFlag: true, filePath: "C:/unrelated");
            Assert.True(await InvokeIsCardBenchmark(controller, "bench-1"));
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, true); } catch { }
        }
    }

    [Fact]
    public async Task IsCardBenchmark_CardInBenchmarkProject_WithoutFlag_True()
    {
        // A benchmark prompt pasted into a normal card in the "Weaver Benchmarks" project
        // has no _benchmark flag — its filePath is the benchmark root, which must still
        // mark it as a benchmark card so suggestions never attach.
        var (controller, db, dbPath) = BuildHarness();
        try
        {
            var benchRoot = AgentProjectUtilities.GetBenchmarkSandboxPath();
            SeedBoardCard(db, "bench-2", "done", benchmarkFlag: false, filePath: benchRoot);
            Assert.True(await InvokeIsCardBenchmark(controller, "bench-2"));
            // Case / trailing-separator variants still match (path normalized).
            SeedBoardCard(db, "bench-2b", "done", benchmarkFlag: false, filePath: benchRoot.ToUpperInvariant() + Path.DirectorySeparatorChar);
            Assert.True(await InvokeIsCardBenchmark(controller, "bench-2b"));
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, true); } catch { }
        }
    }

    [Fact]
    public async Task IsCardBenchmark_NormalProjectCard_False()
    {
        var (controller, db, dbPath) = BuildHarness();
        try
        {
            SeedBoardCard(db, "normal-1", "done", benchmarkFlag: false, filePath: "C:/Users/someone/code/maxhanna");
            Assert.False(await InvokeIsCardBenchmark(controller, "normal-1"));
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, true); } catch { }
        }
    }

    [Fact]
    public async Task SuggestionEndpoint_WhileAnotherCardExecuting_ReturnsCancelled()
    {
        var (controller, db, dbPath) = BuildHarness();
        try
        {
            // Simulate card-2 executing. The run registry lives in this controller's own
            // card-session store, so the entry cannot leak into other tests.
            var dict = CardStoreAccess.Of(controller).ExecutingCards;
            dict["card:card-2"] = DateTime.UtcNow.Ticks;
            try
            {
                var payload = System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    project = "C:/some/project",
                    cardId = "card-1",
                    cardText = "task"
                });
                var method = typeof(AgentController).GetMethod("SuggestImprovements",
                    BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new InvalidOperationException("SuggestImprovements not found");
                var task = (Task<Microsoft.AspNetCore.Mvc.IActionResult>)method.Invoke(controller, new object?[] { payload })!;
                var result = await task as Microsoft.AspNetCore.Mvc.OkObjectResult;
                Assert.NotNull(result);
                var json = System.Text.Json.JsonSerializer.Serialize(result!.Value);
                Assert.Contains("\"cancelled\":true", json);
            }
            finally
            {
                dict.TryRemove("card:card-2", out _);
            }
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, true); } catch { }
        }
    }
}
