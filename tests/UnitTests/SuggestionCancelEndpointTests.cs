using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Tests for the server-side half of suggestion cancellation: the per-cardId abort
/// registry behind POST api/agent/suggest-improvements/cancel. This is the
/// deterministic half of the feature (the LLM call itself can't be unit-tested):
/// a cancel must abort the in-flight generation's token, remove its handle, and
/// tombstone cancels that arrive before the generation request registers.
///
/// Coverage locked in here:
///   • Cancel of a registered card → its CTS is cancelled and the handle removed
///   • Cancel of an unknown card → tombstoned so a late registration skips the LLM
///   • Repeated cancels are idempotent
///   • Missing cardId → BadRequest
/// </summary>
public class SuggestionCancelEndpointTests
{
    private static AgentController MakeController()
        => (AgentController)RuntimeHelpers.GetUninitializedObject(typeof(AgentController));

    private static JsonElement Payload(string cardId)
        => JsonDocument.Parse($"{{\"cardId\":\"{cardId}\"}}").RootElement.Clone();

    private static Dictionary<string, CancellationTokenSource> CtsRegistry(AgentController controller)
        => CardStoreAccess.Of(controller).SuggestionCts;

    private static HashSet<string> CancelledSet(AgentController controller)
        => CardStoreAccess.Of(controller).SuggestionCancelled;

    [Fact]
    public void Cancel_RegisteredCard_CancelsTokenAndRemovesHandle()
    {
        var controller = MakeController();
        using var cts = new CancellationTokenSource();
        CtsRegistry(controller)["sugg-cancel-1"] = cts;

        var result = controller.CancelSuggestImprovements(Payload("sugg-cancel-1"));

        Assert.IsType<OkObjectResult>(result);
        Assert.True(cts.IsCancellationRequested, "the in-flight generation's token must be cancelled");
        Assert.False(CtsRegistry(controller).ContainsKey("sugg-cancel-1"), "the handle must be removed after cancel");
        Assert.False(CancelledSet(controller).Contains("sugg-cancel-1"), "a registered cancel must not tombstone");
    }

    [Fact]
    public void Cancel_UnknownCard_TombstonesSoLateRegistrationSkipsTheLlm()
    {
        var controller = MakeController();

        var result = controller.CancelSuggestImprovements(Payload("sugg-cancel-2"));

        Assert.IsType<OkObjectResult>(result);
        // SuggestImprovements consumes this tombstone at registration and skips the LLM call.
        Assert.Contains("sugg-cancel-2", CancelledSet(controller));
    }

    [Fact]
    public void Cancel_Twice_IsIdempotent()
    {
        var controller = MakeController();

        Assert.IsType<OkObjectResult>(controller.CancelSuggestImprovements(Payload("sugg-cancel-3")));
        Assert.IsType<OkObjectResult>(controller.CancelSuggestImprovements(Payload("sugg-cancel-3")));
        Assert.False(CtsRegistry(controller).ContainsKey("sugg-cancel-3"));
    }

    [Fact]
    public void Cancel_ThenRegenerate_SameCardRegistersAFreshUntouchedHandle()
    {
        var controller = MakeController();
        using var first = new CancellationTokenSource();
        CtsRegistry(controller)["sugg-cancel-4"] = first;
        controller.CancelSuggestImprovements(Payload("sugg-cancel-4"));
        Assert.False(CtsRegistry(controller).ContainsKey("sugg-cancel-4"));
        Assert.False(CancelledSet(controller).Contains("sugg-cancel-4"), "a handled cancel must not tombstone");

        // A fresh generation for the same card registers a new handle that is NOT
        // pre-cancelled (no leftover tombstone from the earlier cancel).
        using var second = new CancellationTokenSource();
        CtsRegistry(controller)["sugg-cancel-4"] = second;
        Assert.False(second.IsCancellationRequested, "a new generation must not inherit a cancel");
        Assert.True(ReferenceEquals(CtsRegistry(controller)["sugg-cancel-4"], second));
        CtsRegistry(controller).Remove("sugg-cancel-4");
    }

    [Fact]
    public void Cancel_MissingCardId_ReturnsBadRequest()
    {
        var controller = MakeController();
        var result = controller.CancelSuggestImprovements(JsonDocument.Parse("{}").RootElement);
        Assert.IsType<BadRequestObjectResult>(result);
    }
}
