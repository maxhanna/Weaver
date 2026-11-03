using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the RepeatedWebStepReason guard: once a _web_search/_web_fetch step has been
/// committed AND its output was harvested into the discovery context ('### WEB RESULTS'
/// present), a NEW web step targeting the same query/URL — or a near-reworded variant —
/// is the planner re-researching instead of using what the search returned (the "loop of
/// searching"). The generic edit-step dedup (0.82 Jaccard) lets reworded queries slip
/// through, so the web guard uses the 0.35 overlap threshold the additional-step queue
/// uses. Gated on the results being IN context: a failed/empty search harvests nothing,
/// so retrying it stays allowed.
/// </summary>
public class RepeatedWebStepGuardTests
{
    private const string CtxWithResults =
        "### read file.ts\n```\ncode\n```\n\n### WEB RESULTS [AI research breakthroughs latest] ###\n" +
        "## Results\n  - Article (https://example.com/ai-article)\n";

    private static PlanStep Search(string query) => new() { File = "_web_search", Change = query };
    private static PlanStep Fetch(string url) => new() { File = "_web_fetch", Change = url };

    [Fact]
    public void IdenticalSearchQuery_IsRejected_WhenResultsAreInContext()
    {
        var reason = AgentPlanningPolicy.RepeatedWebStepReason(
            Search("AI research breakthroughs latest"),
            new List<PlanStep> { Search("AI research breakthroughs latest") },
            CtxWithResults);
        Assert.NotNull(reason);
        Assert.Contains("already ran _web_search", reason);
        Assert.Contains("Do NOT search the web again", reason);
    }

    [Fact]
    public void NearRewordedSearchQuery_IsRejected_WhenResultsAreInContext()
    {
        // "AI news article sources" vs "AI news articles RSS feed or public API" shares too
        // few whole tokens for the 0.82 edit threshold (~0.2) but is the SAME research intent.
        var reason = AgentPlanningPolicy.RepeatedWebStepReason(
            Search("AI news article sources"),
            new List<PlanStep> { Search("AI news articles RSS feed or public API") },
            CtxWithResults);
        Assert.NotNull(reason);
        Assert.Contains("already ran _web_search", reason);
    }

    [Fact]
    public void SameFetchUrl_IsRejected_WhenResultsAreInContext()
    {
        var reason = AgentPlanningPolicy.RepeatedWebStepReason(
            Fetch("https://example.com/ai-article"),
            new List<PlanStep> { Fetch("https://example.com/ai-article") },
            CtxWithResults);
        Assert.NotNull(reason);
        Assert.Contains("already fetched", reason);
        Assert.Contains("Do NOT fetch it again", reason);
    }

    [Fact]
    public void DifferentFetchUrl_IsAllowed()
    {
        // Fetching a SECOND article from the same result set is legitimate work.
        Assert.Null(AgentPlanningPolicy.RepeatedWebStepReason(
            Fetch("https://example.com/other-article"),
            new List<PlanStep> { Fetch("https://example.com/ai-article") },
            CtxWithResults));
    }

    [Fact]
    public void DifferentTopicSearch_IsAllowed()
    {
        // A genuinely different research question (no shared intent words) may run.
        Assert.Null(AgentPlanningPolicy.RepeatedWebStepReason(
            Search("current weather in London"),
            new List<PlanStep> { Search("AI research breakthroughs latest") },
            CtxWithResults));
    }

    [Fact]
    public void NoResultsInContext_AllowsRetry()
    {
        // The search FAILED (nothing harvested) — retrying the same query is a repair, not a loop.
        var ctxNoResults = "### read file.ts\n```\ncode\n```\n";
        Assert.Null(AgentPlanningPolicy.RepeatedWebStepReason(
            Search("AI research breakthroughs latest"),
            new List<PlanStep> { Search("AI research breakthroughs latest") },
            ctxNoResults));
    }

    [Fact]
    public void NonWebStep_IsNeverRejected()
    {
        Assert.Null(AgentPlanningPolicy.RepeatedWebStepReason(
            new PlanStep { File = "src/app.ts", Change = "Add a helper method" },
            new List<PlanStep> { Search("AI research breakthroughs latest") },
            CtxWithResults));
    }

    [Fact]
    public void FirstWebStep_IsAllowed_EvenWithResultsInContext()
    {
        // The guard only rejects a web step that duplicates a COMMITTED web step.
        Assert.Null(AgentPlanningPolicy.RepeatedWebStepReason(
            Search("AI research breakthroughs latest"),
            new List<PlanStep>(),
            CtxWithResults));
    }
}
