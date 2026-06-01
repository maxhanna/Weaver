using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MaestroBackend.ApiIntegrationTests;

public sealed class MaestroWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), "MaestroTests", Guid.NewGuid().ToString("N"));

    public string ContentRootPath => Path.Combine(_testRoot, "content-root");
    public string WorkspaceRootPath => Path.Combine(_testRoot, "workspace-root");

    public TestHttpResponder Responder => Services.GetRequiredService<TestHttpResponder>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(ContentRootPath);
        Directory.CreateDirectory(WorkspaceRootPath);
        Directory.CreateDirectory(Path.Combine(ContentRootPath, "wwwroot"));
        File.WriteAllText(Path.Combine(ContentRootPath, "wwwroot", "index.html"), "<!doctype html><title>test</title>");

        builder.UseEnvironment("Development");
        builder.UseContentRoot(ContentRootPath);

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Editor:WorkspaceRoot"] = WorkspaceRootPath,
                ["Ai:Model"] = "test-model"
            });
        });

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<TestHttpResponder>();
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory, TestHttpClientFactory>();
        });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }
}
