using System.Text;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// The first end-to-end suite for the LIVE WEB TEST pipeline: it detects how Weaver
/// itself spins up its server and spins it up (the same code path the agent uses for
/// "test the …" prompts), then verifies it over HTTP — and, when a Chromium browser is
/// installed, drives the REAL headless browser against a live static server through the
/// CDP driver, ending with a full BrowserAutomationService run in browser mode.
///
/// Windows: uses the already-built Weaver.exe apphost (no rebuild). CI (Linux): falls
/// back to `dotnet run --project Weaver.csproj`, mirroring the launcher's own plan.
/// The browser tests pass in degraded HTTP mode when no browser exists, keeping the
/// Linux CI green.
/// </summary>
[Collection("LiveProcessTests")]
public class ServerLaunchE2ETests : IDisposable
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Weaver.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the Weaver repo root from " + AppContext.BaseDirectory);
    }

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "weaver-e2e-" + Guid.NewGuid().ToString("N"));

    public ServerLaunchE2ETests()
    {
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
    }

    // ── Weaver's own server ──────────────────────────────────────────────────

    [Fact]
    public void DetectLaunchPlan_WeaverRepo_DetectsDotnetRun()
    {
        var plan = ServerLauncherService.DetectLaunchPlan(RepoRoot);

        Assert.NotNull(plan);
        Assert.Equal("dotnet", plan!.Kind);
        Assert.Equal("dotnet", plan.Command);
        Assert.Contains("run --project Weaver.csproj", plan.Arguments);
        Assert.Contains("{port}", plan.Arguments);
        Assert.Equal(5000, plan.PortHint);
        Assert.Equal(RepoRoot, plan.WorkingDirectory);
    }

    [Fact]
    public async Task Launch_WeaverServer_ServesIndexAndIde()
    {
        var (command, args) = BuildWeaverLaunchPlan();
        var plan = new ServerLauncherService.ServerLaunchPlan(
            "Weaver's own server", command, args, RepoRoot, 5000,
            "Weaver's own server (exe or dotnet run)");

        await using var server = await (new ServerLauncherService()).LaunchAsync(plan, timeout: TimeSpan.FromSeconds(180));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var root = await http.GetStringAsync(server.Url + "/");
        Assert.True(root.Contains("weaver", StringComparison.OrdinalIgnoreCase) ||
                    root.Contains("<html", StringComparison.OrdinalIgnoreCase),
            "Index should be Weaver's page: " + Excerpt(root));

        var ide = await http.GetStringAsync(server.Url + "/ide.html");
        Assert.Contains("ide-panel", ide, StringComparison.OrdinalIgnoreCase);

        // The launched instance is real and separate; the launcher picks a free port.
        Assert.Equal("Weaver's own server", server.Kind);
    }

    /// <summary>Weaver's self-contained apphost sits in the app project's RID output
    /// (bin/Debug/net10.0/win-x64 on Windows, bin/Release/linux-x64 on CI) — the test
    /// project's own bin folder only receives the framework-dependent Weaver.dll plus a
    /// hosting-expectations-mismatched apphost copy. Launch the real apphost when present
    /// (faster and fully hermetic); otherwise fall back to `dotnet run` (the launcher's
    /// real detection).</summary>
    private static (string Command, string Arguments) BuildWeaverLaunchPlan()
    {
        var exeName = OperatingSystem.IsWindows() ? "Weaver.exe" : "Weaver";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var exe = Path.Combine(dir.FullName, exeName);
            // Only an apphost NEXT TO its self-contained runtime files launches standalone;
            // a bare apphost copy (no hostpolicy.dll/coreclr.dll beside it) dies with
            // "library 'hostpolicy.dll' required to execute the application was not found".
            if (File.Exists(exe) && File.Exists(Path.Combine(dir.FullName, "hostpolicy.dll")))
                return (exe, "--urls http://127.0.0.1:{port} --no-open-browser");
            dir = dir.Parent!;
        }
        return ("dotnet", "run --project Weaver.csproj --urls http://127.0.0.1:{port}");
    }

    private static string Excerpt(string text)
    {
        var t = text.Replace('\n', ' ').Trim();
        return t.Length > 160 ? t[..160] : t;
    }

    // ── live CDP browser ─────────────────────────────────────────────────────

    private async Task<(string Url, RunningServer Server)> StartStaticSiteAsync()
    {
        var root = Path.Combine(_tmp, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), """
            <!DOCTYPE html>
            <html>
            <head><title>Live Fixture</title></head>
            <body>
              <nav><a href="/pricing.html">Pricing</a></nav>
              <h1>Live Fixture Home</h1>
              <p>Rendered by the real browser.</p>
              <button>Save changes</button>
            </body>
            </html>
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "pricing.html"), """
            <!DOCTYPE html>
            <html>
            <head><title>Pricing</title></head>
            <body><h1>Pricing Page</h1><p>Plans here.</p></body>
            </html>
            """);
        var plan = ServerLauncherService.DetectLaunchPlan(root)!;
        var server = await (new ServerLauncherService()).LaunchAsync(plan, timeout: TimeSpan.FromSeconds(30));
        return (server.Url, server);
    }

    [Fact]
    public async Task CdpBrowserDriver_NavigateSnapshotClick_Live()
    {
        var (url, server) = await StartStaticSiteAsync();
        try
        {
            var driver = await CdpBrowserDriver.TryCreateAsync();
            if (driver == null) return; // no Chromium on this host — covered by HTTP fallback

            await using (driver)
            {
                await driver.NavigateAsync(url);
                await driver.SettleAsync(TimeSpan.FromSeconds(1));
                var snap = await driver.GetSnapshotAsync();
                Assert.Contains("Live Fixture Home", snap.Headings);
                Assert.Contains(new PageLink("Pricing", "/pricing.html"), snap.Links);
                Assert.Contains("Save changes", snap.Buttons);
                Assert.Contains("Rendered by the real browser", snap.BodyText);
                Assert.StartsWith("data:image/jpeg;base64,", snap.ScreenshotDataUrl);

                // Click the nav link → the page navigates → re-snapshot sees Pricing.
                var clicked = await driver.ClickByTextAsync("pricing");
                Assert.Equal("NAVIGATING", clicked);
                await driver.SettleAsync(TimeSpan.FromSeconds(1));
                var after = await driver.GetSnapshotAsync();
                Assert.Contains("Pricing Page", after.Headings);

                // Clicking text that does not exist returns null.
                Assert.Null(await driver.ClickByTextAsync("does not exist"));

                // Raw JS evaluation round-trips values.
                var title = await driver.EvaluateAsync("document.title");
                Assert.Equal("Pricing", title.GetString());
            }
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task BrowserAutomationService_FullRun_StaticSite_BrowserOrHttp()
    {
        // The service launches its own server from the fixture root — no pre-started one.
        var root = Path.Combine(_tmp, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), """
            <!DOCTYPE html>
            <html>
            <head><title>Live Fixture</title></head>
            <body>
              <nav><a href="/pricing.html">Pricing</a></nav>
              <h1>Live Fixture Home</h1>
              <p>Rendered by the real browser.</p>
            </body>
            </html>
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "pricing.html"), """
            <!DOCTYPE html>
            <html>
            <head><title>Pricing</title></head>
            <body><h1>Pricing Page</h1><p>Plans here.</p></body>
            </html>
            """);

        var service = new BrowserAutomationService
        {
            Launcher = new ServerLauncherService(),
            ServerTimeout = TimeSpan.FromSeconds(60),
            BrowserSettleTime = TimeSpan.FromSeconds(1)
        };
        var report = await service.RunUiTestAsync(root, "pricing page", "test the pricing page");

        Assert.True(report.Passed, report.ToString());
        Assert.Contains(report.Mode, new[] { "browser", "http" });
        Assert.Equal("Pricing", report.SectionLabel); // the nav link that leads to Pricing
        Assert.Equal(1, report.Navigations);
        Assert.Contains("Plans here", report.BodyTextExcerpt);
    }
}
