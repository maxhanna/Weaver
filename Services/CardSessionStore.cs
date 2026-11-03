using System.Collections.Concurrent;
using System.Text;

namespace Weaver;

/// <summary>
/// Process-lifetime state keyed by card (or by run key), shared across HTTP requests.
///
/// Why this exists rather than fields on the controller: controllers are TRANSIENT, but a
/// steer message, a cancel, an answer to a pending question, or a context review arrives on
/// a DIFFERENT request than the one running the pipeline. That state genuinely has to
/// outlive a single request, so it cannot live in <see cref="Weaver.AgentRunContext"/>
/// (which is per-execution).
///
/// It is injected as a singleton instead of being a `static` field on the controller so
/// that:
///  - each test instance can own its own store (no process-global mutation, so parallel
///    test classes cannot bleed into each other through it), and
///  - the lifetime is explicit at the composition root rather than implied by `static`.
///
/// Deliberately NOT here: the connectivity-verdict cache (process-wide, not card-scoped),
/// the AsyncLocal step index, and the SSE write lock — those are not card state.
/// </summary>
public sealed class CardSessionStore
{
    /// <summary>Answers awaiting a pending question, keyed by question id.</summary>
    public ConcurrentDictionary<string, PendingQuestion> PendingQuestions { get; } = new();

    /// <summary>Context reviews awaiting a human decision, keyed by review id.</summary>
    public ConcurrentDictionary<string, PendingContextReview> PendingContextReviews { get; } = new();

    /// <summary>Step indexes the user cancelled mid-plan, keyed by card id.</summary>
    public ConcurrentDictionary<string, HashSet<int>> CancelledSteps { get; } = new();

    /// <summary>
    /// Live steering messages posted to POST api/agent/steer while a card is RUNNING, keyed
    /// by cardId. The incremental planner drains the pending message on its next turn and
    /// merges it into the persistent steering context (consumed once, never re-applied). The
    /// latest message wins — repeated steers overwrite, they never queue.
    /// </summary>
    public ConcurrentDictionary<string, string> LiveSteer { get; } = new();

    /// <summary>Streaming "thinking" buffers for the step in flight ("preplan:&lt;cardId&gt;" keys included).</summary>
    public ConcurrentDictionary<string, StringBuilder> StepThinkingBuffers { get; } = new();

    /// <summary>Cached complexity scores and their atomic-step estimates, keyed by card id.</summary>
    public ConcurrentDictionary<string, int> ComplexityScores { get; } = new();
    public ConcurrentDictionary<string, int> AtomicStepEstimates { get; } = new();

    // ── Suggestion generation (idle-only) ───────────────────────────────────────
    // The gate is an object rather than a lock on the store so the critical sections that
    // guard the two collections below stay obviously paired.
    public object SuggestionGateLock { get; } = new();

    /// <summary>Server-side abort handles for in-flight suggestion generations.</summary>
    public Dictionary<string, CancellationTokenSource> SuggestionCts { get; } = new();

    /// <summary>
    /// Tombstone set for cancels that arrive BEFORE the generation request registers its
    /// handle (it is still building its prompt). The registration consumes the tombstone
    /// and skips the LLM call entirely.
    /// </summary>
    public HashSet<string> SuggestionCancelled { get; } = new();

    /// <summary>
    /// Executions currently running ("card:&lt;cardId&gt;" or "anon:&lt;guid&gt;" → started ticks).
    /// Any entry means the board is NOT idle: Done-card suggestion generation must not start,
    /// and the moment a run starts every in-flight generation is aborted — the suggestion
    /// system only ever works while the system is completely idle.
    /// </summary>
    public ConcurrentDictionary<string, long> ExecutingCards { get; } = new();
}
