using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the steering feedback behind the fetch-in-command guard (the
/// AgentPlanningPolicy.FetchCommandFeedbackFor builder): when a _command step runs Invoke-RestMethod/curl/urllib
/// against a URL on a web-needing task, the rejection must do more than say "not a
/// shell command" — it must (1) name the attempted URL, (2) tell the planner whether
/// that URL is verbatim among the harvested search results, and (3) steer to a
/// _web_fetch step carrying a REAL URL copied exactly from the '### WEB RESULTS'
/// sections, so hallucinated URLs (the ".../haha-im-in-danger/" run that fetched
/// garbage and saved "@{title=;...}") stop entering the command surface.
/// </summary>
public class FetchCommandSteeringTests
{
    private static string Feedback(string prompt, string command, List<PlanStep> planSoFar, string discovery)
        => AgentPlanningPolicy.FetchCommandFeedbackFor(
            prompt, new PlanStep { File = "_command", Change = command }, planSoFar, discovery);

    private static string? Url(string command)
        => AgentPlanningPolicy.ExtractCommandUrl(command);

    private const string SearchStep = "AI research breakthroughs latest";

    // ── No web step ran yet ──────────────────────────────────────────────────────────────

    [Fact]
    public void NoSearchYet_SteersToSearchFirst_NeverInvent()
    {
        var fb = Feedback(
            "Search the web for recent AI breakthroughs and write the data into a text file on my desktop",
            "curl https://www.example.com/made-up -o out.txt",
            new List<PlanStep>(), "");
        Assert.StartsWith("This _command step fetches content from a URL with a download tool", fb);
        Assert.Contains("Run a \"_web_search\" step first", fb);
        Assert.Contains("_web_fetch", fb);
        Assert.Contains("never invent, guess, or reconstruct a URL", fb);
        Assert.Contains("Reserve _command for real terminal commands", fb);
    }

    // ── Search ran; the command's URL is NOT among the results (the hallucination case) ──

    [Fact]
    public void SearchRan_UrlNotInResults_NamesInventedUrlAndDemandsVerbatim()
    {
        var discovery = "\n\n### WEB RESULTS [AI research breakthroughs latest] ###\n" +
                        "AlphaFold 3 predicts protein structures with atom-level accuracy (https://example.com/alphafold3)\n";
        var fb = Feedback(
            "Search the web for recent AI breakthroughs and write the data into a text file on my desktop",
            "Invoke-RestMethod -Uri \"https://www.wired.com/story/ai-newsrooms-are-breaking-news-now-haha-im-in-danger/\" | Select-Object title,summary,publishedDate",
            new List<PlanStep> { new() { File = "_web_search", Change = SearchStep } }, discovery);

        // The hallucinated URL is called out by name and the verbatim rule is explicit.
        Assert.Contains("https://www.wired.com/story/ai-newsrooms-are-breaking-news-now-haha-im-in-danger/", fb);
        Assert.Contains("NOT among the harvested search results", fb);
        Assert.Contains("looks invented", fb);
        Assert.Contains("verbatim", fb);
        Assert.Contains("NEVER invent, guess, or reconstruct a URL", fb);
        Assert.Contains("_web_fetch", fb);
        Assert.Contains("### WEB RESULTS", fb);
    }

    // ── Search ran; the command's URL IS verbatim among the results ─────────────────────

    [Fact]
    public void SearchRan_UrlInResults_SteersToWebFetchWithThatExactUrl()
    {
        var discovery = "\n\n### WEB RESULTS [AI research breakthroughs latest] ###\n" +
                        "AlphaFold 3 predicts protein structures with atom-level accuracy (https://example.com/alphafold3)\n";
        var fb = Feedback(
            "Search the web for recent AI breakthroughs and write the data into a text file on my desktop",
            "Invoke-RestMethod -Uri \"https://example.com/alphafold3\" | Select-Object title,summary",
            new List<PlanStep> { new() { File = "_web_search", Change = SearchStep } }, discovery);

        Assert.Contains("IS in the harvested search results", fb);
        Assert.Contains("https://example.com/alphafold3", fb);
        Assert.Contains("_web_fetch", fb);
        Assert.Contains("THAT exact URL verbatim", fb);
        Assert.DoesNotContain("looks invented", fb);
    }

    // ── URL extraction ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractCommandUrl_QuotedPowerShellUrl_TrimsTrailingPunctuation()
    {
        Assert.Equal("https://www.example.com/latest-ai-breakthrough",
            Url("Invoke-RestMethod -Uri \"https://www.example.com/latest-ai-breakthrough\" | Select-Object title"));
        Assert.Equal("https://pokeapi.co/api/v2/pokemon?limit=1000",
            Url("curl https://pokeapi.co/api/v2/pokemon?limit=1000 -o out.json"));
        Assert.Null(Url("dotnet test --filter Unit"));
    }

    [Fact]
    public void Feedback_StartsWithStableMarker_SoAutoInjectCountersRecognizeIt()
    {
        // The fetchCommandRejections counters (Planning.cs) match on the stable prefix —
        // the dynamic detail (URL / results) must not break the >= 2 auto-inject of
        // _web_search when the planner keeps proposing fetch commands.
        var fb = Feedback("Search the web for recent AI breakthroughs", "curl https://x.example.com/y", new List<PlanStep>(), "");
        Assert.StartsWith("This _command step fetches content from a URL with a download tool", fb);
    }
}
