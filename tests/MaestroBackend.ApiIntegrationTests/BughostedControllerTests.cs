using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace MaestroBackend.ApiIntegrationTests;

public class BughostedControllerTests : IClassFixture<MaestroWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly MaestroWebApplicationFactory _factory;

    public BughostedControllerTests(MaestroWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        factory.Responder.Reset();
    }

    [Fact]
    public async Task Login_heartbeat_commands_ack_logout_happy_path()
    {
        await _client.PostAsJsonAsync("/api/config/save", new
        {
            projects = Array.Empty<object>(),
            defaultProject = "",
            llamaUrl = "http://localhost:8080",
            bughostedUrl = "https://bughosted.example"
        });

        _factory.Responder.Responder = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/maestro/login" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"token":"abc123","user":{"name":"test"}}""", Encoding.UTF8, "application/json")
                },
                "/maestro/heartbeat" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json")
                },
                "/maestro/commands" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"commands":[{"id":1,"command":"echo hi"}]}""", Encoding.UTF8, "application/json")
                },
                "/maestro/commands/ack" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"acknowledged":true}""", Encoding.UTF8, "application/json")
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"error":"unexpected"}""", Encoding.UTF8, "application/json")
                }
            };
        };

        var login = await _client.PostAsJsonAsync("/api/bughosted/login", new { username = "u", password = "p" });
        login.EnsureSuccessStatusCode();
        var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var clientId = loginJson.RootElement.GetProperty("clientId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(clientId));

        var heartbeat = await _client.PostAsJsonAsync("/api/bughosted/heartbeat", new { clientId, kanbanData = "[]" });
        heartbeat.EnsureSuccessStatusCode();

        var commands = await _client.GetAsync($"/api/bughosted/commands?clientId={Uri.EscapeDataString(clientId!)}");
        commands.EnsureSuccessStatusCode();
        var commandsBody = await commands.Content.ReadAsStringAsync();
        Assert.Contains("\"commands\"", commandsBody);

        var ack = await _client.PostAsJsonAsync("/api/bughosted/commands/ack", new
        {
            clientId,
            commandId = 1,
            status = "executed",
            result = "done"
        });
        ack.EnsureSuccessStatusCode();

        var logout = await _client.PostAsJsonAsync("/api/bughosted/logout", new { clientId });
        logout.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Heartbeat_returns_unauthorized_when_not_logged_in()
    {
        var response = await _client.PostAsJsonAsync("/api/bughosted/heartbeat", new { clientId = "missing", kanbanData = "{}" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
