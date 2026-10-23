using System.Reflection;
using Weaver;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Reads the card-scoped state store of a reflection-constructed controller.
///
/// The store replaced static dictionaries (see docs/REFACTORING_ANALYSIS.md §4.1): state
/// that must outlive one request is now an injected singleton, and a controller built via
/// RuntimeHelpers.GetUninitializedObject lazily gets a private per-instance store instead.
/// Tests therefore reach the store THROUGH their own controller instance — which is what
/// stops one test class from mutating another class's card state in parallel.
/// </summary>
internal static class CardStoreAccess
{
    private static readonly PropertyInfo StoreProperty =
        typeof(AgentController).GetProperty("Store", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException(
            "AgentController.Store not found — did the CardSessionStore wiring change?");

    /// <summary>The store owned by this controller instance (creating it on first use).</summary>
    public static CardSessionStore Of(AgentController controller) =>
        (CardSessionStore)StoreProperty.GetValue(controller)!;
}
