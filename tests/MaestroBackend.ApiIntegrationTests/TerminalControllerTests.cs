using System.Net;
using System.Net.Http.Json;
using MaestroBackend.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MaestroBackend.ApiIntegrationTests;

public class TerminalControllerTests : IClassFixture<MaestroWebApplicationFactory>
{
    private readonly MaestroWebApplicationFactory _factory;

    public TerminalControllerTests(MaestroWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.Responder.Reset();
    }

    private (HttpClient client, FakeTerminalService terminal, WebApplicationFactory<Program> factory) CreateClientWithFakeTerminal()
    {
        var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITerminalService>();
                services.AddSingleton<ITerminalService, FakeTerminalService>();
            });
        });

        var client = customFactory.CreateClient();
        var terminal = (FakeTerminalService)customFactory.Services.GetRequiredService<ITerminalService>();
        return (client, terminal, customFactory);
    }

    [Fact]
    public async Task Exec_returns_output_from_terminal_service()
    {
        var (client, terminal, factory) = CreateClientWithFakeTerminal();
        using (factory)
        {
            var response = await client.PostAsJsonAsync("/api/terminal/exec", new { command = "echo hello" });
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();

            Assert.Contains("echo hello", body);
            Assert.Contains("done", body);
            Assert.Contains("echo hello", terminal.Commands);
        }
    }

    [Fact]
    public async Task Approvals_endpoints_return_pending_and_allow_reject()
    {
        var (client, _, factory) = CreateClientWithFakeTerminal();
        using (factory)
        {
            await client.PostAsJsonAsync("/api/terminal/exec", new { command = "blocked command" });

            var pending = await client.GetAsync("/api/terminal/approvals/pending");
            pending.EnsureSuccessStatusCode();
            var pendingBody = await pending.Content.ReadAsStringAsync();
            Assert.Contains("approval-1", pendingBody);

            var reject = await client.PostAsJsonAsync("/api/terminal/approvals/reject", new { id = "approval-1" });
            reject.EnsureSuccessStatusCode();

            var pendingAfter = await client.GetAsync("/api/terminal/approvals/pending");
            pendingAfter.EnsureSuccessStatusCode();
            var afterBody = await pendingAfter.Content.ReadAsStringAsync();
            Assert.DoesNotContain("approval-1", afterBody);
        }
    }

    [Fact]
    public async Task Approve_returns_not_found_when_id_missing()
    {
        var (client, _, factory) = CreateClientWithFakeTerminal();
        using (factory)
        {
            var response = await client.PostAsJsonAsync("/api/terminal/approvals/approve", new { id = "missing", scope = "once" });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}
