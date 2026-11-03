using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Weaver;
using Weaver.Controllers;
using Weaver.Services;
using Xunit;
using Xunit.Abstractions;

namespace Weaver.UnitTests;

/// <summary>
/// The NewsService: fresh-news fetching (parallel RSS/API sources, URL+title dedup,
/// round-robin interleave, snippet/LLM summaries, markdown digest) plus the
/// news-digest routing inside ExecuteWebPlanStep (the news marker now lives in WebNeedClassifier). The guard behind "a model asked for the latest AI
/// news and invented a headline with a fake example.com URL": with the marker, the search
/// step returns REAL dated items with REAL URLs the agent can fetch or paste; without it,
/// the search stays on the plain DuckDuckGo path.
/// </summary>
public class NewsServiceTests
{
    private readonly ITestOutputHelper _out;

    public NewsServiceTests(ITestOutputHelper output) => _out = output;

    private const string NewsPrompt =
        "Get a latest AI news article from the web and paste the article in a text file on the desktop.";
    private const string PlainPrompt =
        "Search the web for recent AI breakthroughs and verify each result is a real published paper.";
    private const string Query = "latest AI news";

    // ── Topic/place-aware planning: the prompt becomes a plan (query + topics + places + region) ──
    [Fact]
    public void ExtractPlanDeterministic_MontrealPrompt_DetectsPlaceRegionAndQuery()
    {
        var plan = NewsService.ExtractPlanDeterministic(
            "Find the local Montreal news and insert it into a text document on desktop", null);

        Assert.Contains("montreal", plan.Places);
        Assert.Equal("canada", plan.Region);
        Assert.Contains("local", plan.Topics);
        Assert.Contains("montreal", plan.SearchQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AI News", plan.SearchQuery);
    }

    [Fact]
    public void ExtractPlanDeterministic_AIQuery_DetectsAITopic_NoPlaces()
    {
        var plan = NewsService.ExtractPlanDeterministic("Search the web for the latest AI news", null);

        Assert.Contains("ai", plan.Topics);
        Assert.Empty(plan.Places);
        Assert.Null(plan.Region);
        Assert.Equal("AI news", plan.SearchQuery); // original casing preserved
    }

    [Fact]
    public void ParsePlanJson_FencedOutput_Parses()
    {
        var plan = NewsService.ParsePlanJson(
            "```json\n{\"query\": \"paris food news\", \"topics\": [\"food\"], \"places\": [\"paris\"], \"region\": \"france\"}\n```");

        Assert.NotNull(plan);
        Assert.Equal("paris food news", plan!.SearchQuery);
        Assert.Equal("food", Assert.Single(plan.Topics));
        Assert.Equal("france", plan.Region);
    }

    [Fact]
    public void ParsePlanJson_Garbage_ReturnsNull()
    {
        Assert.Null(NewsService.ParsePlanJson("LLM SUMMARY"));
        Assert.Null(NewsService.ParsePlanJson(null));
        Assert.Null(NewsService.ParsePlanJson("{\"query\": \"\"}"));
    }

    [Fact]
    public async Task FetchNewsAsync_LocalMontrealNews_UsesLocalitySources_NotTechFeeds()
    {
        // No LLM plan extractor — the deterministic fallback must do the locality routing.
        var factory = new NewsScriptedClientFactory();
        var service = new NewsService(factory, "http://localhost:8080", "test-model");

        var digest = await service.FetchNewsAsync(
            "Find the local Montreal news and insert it into a text document on desktop",
            limit: 8, ct: CancellationToken.None);

        // Google News query search carries the place into the provider; region feeds joined.
        Assert.Contains(factory.Gets, u => u.Contains("news.google.com") && u.Contains("montreal", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(factory.Gets, u => u.Contains("gl=CA")); // Canada locale
        Assert.Contains(factory.Gets, u => u.Contains("cbc.ca"));
        Assert.Contains(factory.Gets, u => u.Contains("globalnews.ca"));
        // Tech feeds must NOT be fetched for a local-news query.
        Assert.DoesNotContain(factory.Gets, u => u.Contains("venturebeat"));
        Assert.DoesNotContain(factory.Gets, u => u.Contains("hn.algolia"));
        // The CBC fixture landed; header reflects a general (non-AI) digest.
        Assert.Contains("# News — ", digest);
        Assert.DoesNotContain("# AI News", digest);
        Assert.Contains("Montreal Metro gets $10B transit boost", digest);
        Assert.Contains("1 item(s) from CBC.", digest);
    }

    [Fact]
    public async Task FetchNewsAsync_LlmPlan_OverridesQueryTopicsAndRegion()
    {
        // The LLM planner (fenced JSON, as a real model tends to emit) picks food + Paris.
        var factory = new NewsScriptedClientFactory();
        var service = new NewsService(factory, "http://localhost:8080", "test-model",
            (prompt, _) => Task.FromResult<string?>(
                "```json\n{\"query\": \"paris food news\", \"topics\": [\"food\"], \"places\": [\"paris\"], \"region\": \"france\"}\n```"));

        var digest = await service.FetchNewsAsync(
            "Find the latest food news in Paris and write it to a text file",
            limit: 8, ct: CancellationToken.None);

        // The query lands in the Google News search URL (encoding-agnostic: the recorded Uri
        // may show literal spaces or %20 depending on normalization).
        Assert.Contains(factory.Gets, u => u.Contains("news.google.com") && u.Contains("paris") && u.Contains("food"));
        Assert.Contains(factory.Gets, u => u.Contains("gl=FR")); // France locale from region
        Assert.Contains(factory.Gets, u => u.Contains("eater.com"));
        Assert.DoesNotContain(factory.Gets, u => u.Contains("export.arxiv")); // no science topic
        Assert.DoesNotContain(factory.Gets, u => u.Contains("venturebeat"));
        Assert.Contains("# News — ", digest);
        Assert.Contains("Montreal's new poutine festival draws record crowds", digest);
    }

    [Fact]
    public async Task FetchNewsAsync_LlmPlan_Garbage_DeterministicFallback()
    {
        // A broken/empty LLM plan must not break the digest — deterministic extraction takes over.
        var factory = new NewsScriptedClientFactory();
        var service = new NewsService(factory, "http://localhost:8080", "test-model",
            (_, _) => Task.FromResult<string?>("LLM SUMMARY"));

        var digest = await service.FetchNewsAsync("AI news", limit: 8, ct: CancellationToken.None);

        Assert.Contains("# AI News — ", digest);
        Assert.Contains("AI Startup Raises $200M", digest);
        Assert.Contains("https://vb.example.com/1", digest);
    }

    // ── Digest pipeline: parallel sources → dedup → round-robin → snippets → markdown ──
    // VB: [V1, V2, V3] · TC: [T1 (SAME TITLE as V1 → deduped away), T2] · HN: [H1] ·
    // arXiv: [A1] · Lobsters: [L1] — the other four RSS feeds return empty.
    [Fact]
    public async Task FetchNewsAsync_ParsesDedupsInterleaves_AndAssemblesDigest()
    {
        var factory = new NewsScriptedClientFactory();
        var service = new NewsService(factory, "http://localhost:8080", "test-model");

        var digest = await service.FetchNewsAsync("AI news", limit: 8, ct: CancellationToken.None);

        Assert.Contains("# AI News — ", digest);
        Assert.Contains("\"AI news\"", digest);
        Assert.Contains("7 item(s) from VentureBeat, TechCrunch, Hacker News, Lobsters, arXiv.", digest);
        // Every source parsed into the digest.
        Assert.Contains("AI Startup Raises $200M", digest);
        Assert.Contains("New AI Model Beats Benchmarks", digest);
        Assert.Contains("AI Chip Design News", digest);
        Assert.Contains("AI Robot Vacuum Review", digest);
        Assert.Contains("Show HN: Local AI News Reader", digest);
        Assert.Contains("An Efficient Transformer for Multi-Task Policy Learning", digest);
        Assert.Contains("Lobsters: AI News Thread", digest);
        // Dedup: T1 ("AI Startup Raises $200M") was the same story as V1 → collapsed, and the
        // kept copy is V1's (richer snippet, VentureBeat URL) — never the tracker's URL.
        Assert.Contains("https://vb.example.com/1", digest);
        Assert.DoesNotContain("https://tc.example.com/1", digest);
        // Interleave order is round-robin by feed: V1, T2, H1, L1, A1, V2, V3.
        AssertOrder(digest,
            "AI Startup Raises $200M", "AI Robot Vacuum Review", "Show HN: Local AI News Reader",
            "Lobsters: AI News Thread", "An Efficient Transformer for Multi-Task Policy Learning",
            "New AI Model Beats Benchmarks", "AI Chip Design News");
        // Long feed descriptions (>200 chars) become the summary — no LLM call needed.
        Assert.Contains("VentureBeat long-form description", digest);
        // The real article URLs landed (fetchable by a follow-up _web_fetch, not invented).
        Assert.Contains("https://arxiv.org/abs/2406.07539", digest);
        Assert.Contains("Feed: Hacker News", digest);
        Assert.Contains("Feed: arXiv", digest);
        Assert.Contains("Feed: Lobsters", digest);
    }

    [Fact]
    public async Task FetchNewsAsync_DeadFeed_DoesNotKillDigest()
    {
        var factory = new NewsScriptedClientFactory { FailTechCrunch = true };
        var service = new NewsService(factory, "http://localhost:8080", "test-model");

        var digest = await service.FetchNewsAsync("AI news", limit: 8, ct: CancellationToken.None);

        // TechCrunch vanished; every other source still contributed.
        Assert.Contains("AI Startup Raises $200M", digest);
        Assert.Contains("6 item(s) from VentureBeat, Hacker News, Lobsters, arXiv.", digest);
        Assert.DoesNotContain("TechCrunch", digest);
    }

    [Fact]
    public async Task FetchNewsAsync_AllSourcesDead_ReturnsNoResultsDigest_NoThrow()
    {
        var factory = new NewsScriptedClientFactory { FailAll = true };
        var service = new NewsService(factory, "http://localhost:8080", "test-model");

        var digest = await service.FetchNewsAsync("AI news", limit: 8, ct: CancellationToken.None);

        Assert.Contains("# AI News", digest);
        Assert.Contains("No fresh items could be fetched", digest);
    }

    [Fact]
    public async Task FetchNewsAsync_ThinSnippet_FetchesArticleAndUsesLlmSummary()
    {
        var factory = new NewsScriptedClientFactory { ServeArticleHtml = true };
        var service = new NewsService(factory, "http://localhost:8080", "test-model");

        var digest = await service.FetchNewsAsync("AI news", limit: 8, ct: CancellationToken.None);

        // The thin snippet (< 200 chars) triggered the article fetch + LLM summarizer path.
        Assert.Contains("LLM SUMMARY", digest);
        Assert.Contains("Thin Snippet Story", digest);
    }

    // ── Full-article dump support (the "dump the article" untruncated fix) ──
    [Fact]
    public void FirstArticleUrl_ExtractsTopItemSourceUrl()
    {
        var digest =
            "# AI News — 2026-08-13 — \"AI news\"\n" +
            "8 item(s) from VentureBeat.\n\n" +
            "## Summary\nA roundup.\n\n" +
            "## AI Startup Raises $200M\n" +
            "Source: https://vb.example.com/1\n" +
            "Published: Wed, 12 Aug 2026 10:00:00 GMT\n" +
            "Feed: VentureBeat\nsummary\n\n" +
            "## Second item\nSource: https://vb.example.com/2\nsummary";

        Assert.Equal("https://vb.example.com/1", NewsService.FirstArticleUrl(digest));
        Assert.Null(NewsService.FirstArticleUrl("no source line here"));
        Assert.Null(NewsService.FirstArticleUrl(null));
    }

    [Fact]
    public async Task FetchFullArticleAsync_ReturnsUntruncatedArticleText()
    {
        var factory = new NewsScriptedClientFactory { ServeArticleHtml = true };
        var service = new NewsService(factory, "http://localhost:8080", "test-model");

        var full = await service.FetchFullArticleAsync("https://example.com/thin-story", CancellationToken.None);

        // The whole <article> body (300 x's) — untruncated, no "…" chop marker.
        Assert.NotNull(full);
        Assert.Equal(300, full.Length);
        Assert.Equal(new string('x', 300), full);
        Assert.DoesNotContain("…", full);

        // A non-http(s) URL and an empty URL never fetch.
        Assert.Null(await service.FetchFullArticleAsync("ftp://example.com/x", CancellationToken.None));
        Assert.Null(await service.FetchFullArticleAsync("", CancellationToken.None));
    }

    // ── Routing inside ExecuteWebPlanStep (the live _web_search step) ──
    [Fact]
    public async Task ExecuteWebPlanStep_NewsPrompt_ReturnsFreshDigest_NotInventedHeadline()
    {
        var factory = new NewsScriptedClientFactory();
        var controller = BuildRoutingController(factory, Path.GetTempPath());

        var wr = await RunWebSearchStep(controller, NewsPrompt, Query);

        Assert.Equal("done", wr.GetValueOrDefault("status")?.ToString());
        var outp = wr.GetValueOrDefault("output")?.ToString() ?? "";
        Assert.Contains("# AI News — ", outp);
        // A REAL title + REAL URL from the live feed — exactly what the pre-fix run invented.
        Assert.Contains("AI Startup Raises $200M", outp);
        Assert.Contains("https://vb.example.com/1", outp);
        Assert.DoesNotContain("example.com/fakearticle", outp);
    }

    [Fact]
    public async Task ExecuteWebPlanStep_ArticleWritePrompt_ReturnsFreshDigest_NotInventedHeadline()
    {
        // The exact run that produced the "invented headline + fake URL" failure: an AI
        // article must be written to a desktop file, with NO "news" word and NO recency
        // word — only the persist-intent rule C routes it to the digest.
        var factory = new NewsScriptedClientFactory();
        var controller = BuildRoutingController(factory, Path.GetTempPath());

        var wr = await RunWebSearchStep(controller,
            "Search the web for an interesting and relevant AI article and write the data into a text file on my desktop.",
            "AI research breakthroughs latest");

        Assert.Equal("done", wr.GetValueOrDefault("status")?.ToString());
        var outp = wr.GetValueOrDefault("output")?.ToString() ?? "";
        Assert.Contains("# AI News — ", outp);
        Assert.Contains("AI Startup Raises $200M", outp);
        Assert.DoesNotContain("example.com/fakearticle", outp);
    }

    [Fact]
    public async Task ExecuteWebPlanStep_NewsDumpTask_AppendsFullArticleText_Untruncated()
    {
        // The "dump the article" task: the digest summaries land first, then the FULL article
        // body is fetched and appended — the file must carry the whole article, untruncated.
        var factory = new NewsScriptedClientFactory { ServeArticleHtml = true };
        var dir = Path.Combine(Path.GetTempPath(), "weaver_newsdump_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var controller = BuildRoutingController(factory, Path.GetTempPath());
            var method = typeof(AgentController).GetMethod("ExecuteWebPlanStep", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var allResults = new List<object>();
            var prompt = "Fetch the latest AI news and write the article data into news_article.txt";
            var task = (Task<(int, string)>)method.Invoke(controller, new object?[]
            {
                "_web_search", "latest AI news", prompt, dir, /*emitSse*/ false, CancellationToken.None,
                allResults, new List<PlanStep>(), /*itemIdx*/ 0, /*stepIndex*/ 0, /*discoveryContext*/ "", new StringBuilder()
            })!;
            await task;

            var file = Path.Combine(dir, "news_article.txt");
            Assert.True(File.Exists(file), "the repo-relative demanded file should have been dumped");
            var content = File.ReadAllText(file);
            Assert.Contains("## Full article text", content);
            Assert.Contains(new string('x', 300), content); // the whole <article> body, untruncated
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ExecuteWebPlanStep_NonNewsPrompt_StaysOnDuckDuckGo()
    {
        var factory = new NewsScriptedClientFactory();
        var controller = BuildRoutingController(factory, Path.GetTempPath());

        var wr = await RunWebSearchStep(controller, PlainPrompt, "AI research breakthroughs latest");

        var outp = wr.GetValueOrDefault("output")?.ToString() ?? "";
        Assert.Contains("## Results", outp); // DuckDuckGo format, untouched
        Assert.DoesNotContain("# AI News", outp);
    }

    // ── Digest shrinkage through the agent pipeline stages ────────────────────────────────
    // The digest is PRE-COMPACTED by design (DefaultLimit=8 items, MaxSnippetChars=1500,
    // MaxDigestChars=25000), so unlike a raw multi-MB fetch — which the discovery context
    // chops at 20k/section — a production digest should ride through every agent stage
    // UNTRUNCATED. Measured through the same five stages as the web-results shrinkage test.
    private static readonly MethodInfo DigestCapMethod = typeof(AgentController).GetMethod(
        "CapWebStepOutputForClient", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo DigestAppendMethod = typeof(AgentController).GetMethod(
        "AppendWebResultsToDiscoveryContext", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo DigestPlannerMethod = typeof(AgentController).GetMethod(
        "BuildPlannerDiscoveryContext", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo DigestThinkingMethod = typeof(AgentController).GetMethod(
        "ExtractWebResultSectionsForThinking", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo DigestEditMethod = typeof(AgentController).GetMethod(
        "HarvestWebResultsForEditContext", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static (string capped, bool truncated) DigestClientCap(string? output)
        => ((ValueTuple<string, bool>)DigestCapMethod.Invoke(null, new object?[] { output })!);

    private static string DigestDiscovery(string ctx, List<Dictionary<string, object?>> results)
        => (string)DigestAppendMethod.Invoke(null, new object?[] { ctx, results, 20000, 60000 })!;

    private static string DigestPlanner(string discovery)
        => (string)DigestPlannerMethod.Invoke(null, new object?[] { discovery })!;

    private static string DigestThinking(string discovery)
        => (string)DigestThinkingMethod.Invoke(null, new object?[] { discovery })!;

    private static string DigestEdit(List<Dictionary<string, object?>> results)
        => (string)DigestEditMethod.Invoke(null, new object?[] { results })!;

    /// <summary>A digest built faithfully to BuildDigest's format at the production ceiling:
    /// 8 items (DefaultLimit) × 1500-char snippets (MaxSnippetChars) ≈ 14k.</summary>
    private static string BuildMaxDesignDigest()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# AI News — 2026-08-12 — \"AI research breakthroughs latest\"");
        sb.AppendLine("8 item(s) from VentureBeat, TechCrunch, MIT Technology Review, Wired, Ars Technica.");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine("A roundup of the latest AI research and industry news, with fresh dated items and real URLs. " +
                      new string('s', 400));
        for (var i = 0; i < 8; i++)
        {
            sb.AppendLine();
            sb.AppendLine($"## Item {i}: a realistic AI headline with substantial detail for the digest body");
            sb.AppendLine($"Source: https://example.com/item{i}");
            sb.AppendLine("Published: Wed, 12 Aug 2026 10:00:00 GMT");
            sb.AppendLine("Feed: VentureBeat");
            sb.AppendLine(new string((char)('a' + i), 1500));
        }
        return sb.ToString();
    }

    [Fact]
    public async Task NewsDigest_RealRun_PreCompactedByDesign_NoTruncationInAnyStage()
    {
        // A REAL digest through the actual parser + fixtures (VB/TC/HN/arXiv/Lobsters items).
        var factory = new NewsScriptedClientFactory();
        var service = new NewsService(factory, "http://localhost:8080", "test-model");
        var digest = await service.FetchNewsAsync("AI news", limit: NewsService.DefaultLimit, ct: CancellationToken.None);

        Assert.True(digest.Length >= 500, $"digest should contain real items — got {digest.Length} chars");
        Assert.True(digest.Length <= 12000, "a DefaultLimit digest should fit inside every stage cap");
        var results = new List<Dictionary<string, object?>>
        {
            new() { ["type"] = "_web_search", ["status"] = "done", ["query"] = "AI news", ["output"] = digest }
        };

        var (_, clientTruncated) = DigestClientCap(digest);
        Assert.False(clientTruncated, $"the 12k client display cap must NOT fire on a {digest.Length}-char digest");
        var discovery = DigestDiscovery("", results);
        var planner = DigestPlanner(discovery);
        var thinking = DigestThinking(discovery);
        var edit = DigestEdit(results);
        // The planner/thinking passes TrimEnd trailing whitespace per section (content is
        // preserved, line endings normalized), so compare normalized content — the whole
        // digest must ride through UNTRUNCATED (no "…" chop marker may touch it).
        var normDigest = Norm(digest);
        foreach (var stage in new[] { discovery, planner, thinking, edit })
        {
            var normStage = Norm(stage);
            Assert.Contains(normDigest, normStage, StringComparison.Ordinal); // zero loss
            var at = normStage.IndexOf(normDigest, StringComparison.Ordinal);
            Assert.DoesNotContain("…", normStage.Substring(at, normDigest.Length));
        }

        _out.WriteLine("=== News-digest shrinkage through the agent pipeline (real run, " + digest.Length + " chars) ===");
        _out.WriteLine($"  Pre-compaction (NewsService design caps): DefaultLimit=8 items · MaxSnippetChars=1500 · MaxDigestChars=25000");
        _out.WriteLine($"  Stage 0  allResults (digest, raw)          : {digest.Length,6:N0} chars  (pre-compacted by the feed parser — no pipeline loss to recover)");
        _out.WriteLine($"  Stage 1  client SSE display cap (12k)      : {(DigestClientCap(digest).capped).Length,6:N0} chars  truncated={(clientTruncated ? "YES" : "no")}");
        _out.WriteLine($"  Stage 2  discovery context (20k/sec, 60k total): {discovery.Length,6:N0} chars  survival 100% — no truncation");
        _out.WriteLine($"  Stage 3  planner prompt (verbatim)         : {planner.Length,6:N0} chars  0% further loss");
        _out.WriteLine($"  Stage 4  thinking prompt (verbatim)         : {thinking.Length,6:N0} chars  0% further loss");
        _out.WriteLine($"  Stage 5  edit-resolution (60k on demand)   : {edit.Length,6:N0} chars  0% further loss");
        _out.WriteLine($"  → Pre-compacted by design: 8 items × 1500-char snippets fit inside every stage cap; the pipeline adds ZERO loss.");
    }

    [Fact]
    public void NewsDigest_MaxDesignSize_OnlyClientDisplayCapFires()
    {
        // The digest at its production ceiling (8 items × 1500-char snippets ≈ 14k): larger
        // than the 12k client DISPLAY cap, but still inside the 20k discovery per-section cap.
        var digest = BuildMaxDesignDigest();
        Assert.True(digest.Length > 12000 && digest.Length <= 20000, $"fixture must exceed the client cap but fit the discovery cap: {digest.Length}");
        var results = new List<Dictionary<string, object?>>
        {
            new() { ["type"] = "_web_search", ["status"] = "done", ["query"] = "AI news", ["output"] = digest }
        };

        var (_, clientTruncated) = DigestClientCap(digest);
        Assert.True(clientTruncated, "the client DISPLAY cap should fire on the max-size digest (display only)");
        var normDigest = Norm(digest);
        var discovery = DigestDiscovery("", results);
        var planner = DigestPlanner(discovery);
        var thinking = DigestThinking(discovery);
        var edit = DigestEdit(results);
        foreach (var stage in new[] { discovery, planner, thinking, edit })
            Assert.Contains(normDigest, Norm(stage), StringComparison.Ordinal); // agent context keeps ALL of it

        _out.WriteLine("=== News-digest at the production ceiling (" + digest.Length + " chars) ===");
        _out.WriteLine($"  Stage 1  client SSE display cap (12k)      : truncated={(clientTruncated ? "YES" : "no")} — display only, agent context unaffected");
        _out.WriteLine($"  Stage 2  discovery context (20k/sec)       : {discovery.Length,6:N0} chars  full digest present, 0% loss");
        _out.WriteLine($"  Stage 3  planner / Stage 4 thinking        : verbatim, 0% further loss");
        _out.WriteLine($"  Stage 5  edit-resolution (60k on demand)   : {edit.Length,6:N0} chars  0% loss");
        _out.WriteLine($"  → Even the largest production digest fits the discovery budget — the digest path never hits the 20k chop that a raw fetch does.");
    }

    /// <summary>Normalizes line endings and trailing whitespace so stage outputs built with
    /// different newline conventions (AppendLine → Environment.NewLine vs the pipeline's
    /// '\n' separators + TrimEnd) can be compared content-wise.</summary>
    private static string Norm(string s) => s.Replace("\r\n", "\n").Trim();

    private static void AssertOrder(string haystack, params string[] needles)
    {
        var prev = -1;
        foreach (var n in needles)
        {
            var idx = haystack.IndexOf(n, StringComparison.Ordinal);
            Assert.True(idx > prev, $"'{n}' should come after the previous needle (prev={prev}, idx={idx})");
            prev = idx;
        }
    }

    private static async Task<Dictionary<string, object?>> RunWebSearchStep(
        AgentController controller, string prompt, string query)
    {
        var method = typeof(AgentController).GetMethod("ExecuteWebPlanStep", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ExecuteWebPlanStep not found");
        var allResults = new List<object>();
        var task = (Task<(int stepIndex, string discoveryContext)>)method.Invoke(controller, new object?[]
        {
            "_web_search", query, prompt, Path.GetTempPath(), /*emitSse*/ false, CancellationToken.None,
            allResults, new List<PlanStep>(), /*itemIdx*/ 0, /*stepIndex*/ 0, /*discoveryContext*/ "", new StringBuilder()
        })!;
        await task;
        return (Dictionary<string, object?>)Assert.Single(allResults);
    }

    private static AgentController BuildRoutingController(IHttpClientFactory factory, string baseDir)
    {
        // Unique per construction (matches the weaver_webtask_<guid> pattern used by the
        // other DB-backed test classes). A shared fixed temp DB is reopened across runs and
        // its stale WAL/-shm sidecars made DatabaseService's init fail transiently with
        // SQLite Error 10 'disk I/O error' under full-suite parallel load (2 of 5 runs).
        var dir = Path.Combine(baseDir, "weaver_news_" + Guid.NewGuid().ToString("N"));
        var db = new DatabaseService(
            Path.Combine(dir, "weaver.db"),
            dir,
            Path.Combine(dir, "weaverconfig.json"));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Editor:DisableLLMRetries"] = "true" })
            .Build();
        var controller = (AgentController)RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        SetField(controller, "_clientFactory", factory);
        SetField(controller, "_config", config);
        SetField(controller, "_db", db);
        SetField(controller, "_configFile", new ConfigFileService(db));
        return controller;
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field {name} not found");
        field.SetValue(target, value);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // Scripted HTTP: each source serves a deterministic fixture; the four extra RSS
    // feeds serve empty feeds; article hosts serve HTML only in the summarizer test.
    // ═══════════════════════════════════════════════════════════════════════════════
    private sealed class NewsScriptedClientFactory : IHttpClientFactory
    {
        public bool FailTechCrunch;
        public bool FailAll;
        public bool ServeArticleHtml;
        public readonly List<string> Gets = new();

        public HttpClient CreateClient(string name) => new(new ScriptedHandler(this));
        public HttpClient CreateClient() => CreateClient("default");

        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly NewsScriptedClientFactory _owner;
            public ScriptedHandler(NewsScriptedClientFactory owner) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                lock (_owner.Gets) _owner.Gets.Add(request.RequestUri?.ToString() ?? "");
                if (request.Method == HttpMethod.Post)
                    return Task.FromResult(Json(new { choices = new[] { new { message = new { content = "LLM SUMMARY" } } } }));
                if (_owner.FailAll)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                var host = request.RequestUri?.Host ?? "";
                if (host.Contains("duckduckgo"))
                    return Task.FromResult(Json(new
                    {
                        AbstractText = "Survey text", AbstractURL = "https://example.com/ai-overview", Answer = "",
                        RelatedTopics = new object[] { new { Text = "AlphaFold 3 predicts protein structures", FirstURL = "https://example.com/alphafold3" } }
                    }));
                if (host.Contains("hn.algolia"))
                    return Task.FromResult(Json(new
                    {
                        hits = new[]
                        {
                            new { title = "Show HN: Local AI News Reader", url = "https://example.com/hn-1",
                                  created_at = "2026-08-11T10:00:00Z", objectID = "1", story_text = "" }
                        }
                    }));
                if (host.Contains("lobste.rs"))
                    return Task.FromResult(Json(new[]
                    {
                        new { title = "Lobsters: AI News Thread", url = "https://lobste.rs/s/ai123",
                              created_at = "2026-08-11T10:00:00Z", description = "Discussion of the latest AI news." }
                    }));
                if (host.Contains("export.arxiv"))
                    return Task.FromResult(Text(ArxivAtom));
                if (host.Contains("techcrunch") && _owner.FailTechCrunch)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                if (host.Contains("venturebeat"))
                    return Task.FromResult(Text(_owner.ServeArticleHtml ? ThinSnippetRss : VentureBeatRss));
                if (host.Contains("techcrunch"))
                    return Task.FromResult(Text(TechCrunchRss));
                if (host.Contains("theverge") || host.Contains("arstechnica") ||
                    host.Contains("technologyreview") || host.Contains("wired"))
                    return Task.FromResult(Text(Rss()));
                if (host.Contains("example.com") && _owner.ServeArticleHtml)
                    return Task.FromResult(Text("<html><body><article>" + new string('x', 300) + "</article></body></html>"));
                if (host.Contains("cbc.ca"))
                    return Task.FromResult(Text(Rss(
                        ("Montreal Metro gets $10B transit boost", "https://cbc.example.com/montreal-transit",
                            "Quebec and Ottawa announced a major transit investment for Montreal. " + new string('q', 220)))));
                if (host.Contains("eater"))
                    return Task.FromResult(Text(Rss(
                        ("Montreal's new poutine festival draws record crowds", "https://eater.example.com/poutine",
                            "Food writers flocked to the poutine festival this year. " + new string('e', 210)))));
                return Task.FromResult(Json(new { }));
            }
        }

        private static HttpResponseMessage Json(object obj)
            => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Text(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };

        private static string Rss(params (string title, string link, string desc)[] items)
            => "<?xml version=\"1.0\" encoding=\"UTF-8\"?><rss version=\"2.0\"><channel>" +
               string.Concat(items.Select(i =>
                   $"<item><title>{i.title}</title><link>{i.link}</link><description><![CDATA[{i.desc}]]></description></item>")) +
               "</channel></rss>";

        private static string VentureBeatRss => Rss(
            ("AI Startup Raises $200M", "https://vb.example.com/1",
                "VentureBeat long-form description " + new string('z', 300)),
            ("New AI Model Beats Benchmarks", "https://vb.example.com/2",
                "A model beats benchmarks " + new string('y', 250)),
            ("AI Chip Design News", "https://vb.example.com/3",
                "Chip design " + new string('x', 220)));

        private static string TechCrunchRss => Rss(
            ("AI Startup Raises $200M", "https://tc.example.com/1",
                "TechCrunch's shorter take on the same story"),
            ("AI Robot Vacuum Review", "https://tc.example.com/2",
                "Robot vacuum review " + new string('w', 260)));

        // The summarizer test's feed: a THIN snippet (< 200 chars) whose article URL lives on
        // example.com — so the item triggers the article-fetch + injected-summarizer path.
        private static string ThinSnippetRss => Rss(
            ("Thin Snippet Story", "https://example.com/thin-story", "A very short teaser."));

        private static string ArxivAtom =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><feed xmlns=\"http://www.w3.org/2005/Atom\">" +
            "<entry><title>An Efficient Transformer for Multi-Task Policy Learning</title>" +
            "<id>https://arxiv.org/abs/2406.07539</id><published>2026-08-10T00:00:00Z</published>" +
            "<summary>Transformer architecture for multi-task robot policies.</summary></entry></feed>";
    }
}
