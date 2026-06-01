using System.Net.Http.Json;
using MaestroBackend.Services;
using System.Net;

namespace MaestroBackend.ApiIntegrationTests;

public class ConfigControllerTests : IClassFixture<MaestroWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly MaestroWebApplicationFactory _factory;

    public ConfigControllerTests(MaestroWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        factory.Responder.Reset();
    }

    [Fact]
    public async Task Save_preserves_existing_credentials_when_omitted()
    {
        var initial = new
        {
            projects = Array.Empty<object>(),
            defaultProject = "",
            emailUsername = "owner@example.com",
            emailPassword = "secret",
            emailImapServer = "imap.example.com",
            bughostedUsername = "maestro",
            bughostedPassword = "token",
            bughostedUrl = "https://bughosted.example"
        };

        var seedResponse = await _client.PostAsJsonAsync("/api/config/save", initial);
        seedResponse.EnsureSuccessStatusCode();

        var update = new
        {
            projects = Array.Empty<object>(),
            defaultProject = "",
            emailUsername = "",
            emailPassword = "",
            emailImapServer = "",
            bughostedUsername = "",
            bughostedPassword = "",
            bughostedUrl = ""
        };

        var response = await _client.PostAsJsonAsync("/api/config/save", update);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<FrontendConfig>();

        Assert.NotNull(payload);
        Assert.Equal("owner@example.com", payload!.emailUsername);
        Assert.Equal("secret", payload.emailPassword);
        Assert.Equal("imap.example.com", payload.emailImapServer);
        Assert.Equal("maestro", payload.bughostedUsername);
        Assert.Equal("token", payload.bughostedPassword);
        Assert.Equal("https://bughosted.example", payload.bughostedUrl);
    }

    [Fact]
    public async Task Add_and_set_default_project_updates_config()
    {
        var addResponse = await _client.PostAsJsonAsync("/api/config/projects/add?setDefault=true", new
        {
            Name = "Sample",
            Path = "sample-project",
            Description = "Sample project"
        });

        addResponse.EnsureSuccessStatusCode();

        var getResponse = await _client.GetFromJsonAsync<FrontendConfig>("/api/config");

        Assert.NotNull(getResponse);
        Assert.Equal("sample-project", getResponse!.defaultProject);
        Assert.Contains(getResponse.projects, project => project.Path == "sample-project");
    }

    [Fact]
    public async Task Remove_project_updates_default_project()
    {
        await _client.PostAsJsonAsync("/api/config/projects/add?setDefault=true", new
        {
            Name = "First",
            Path = "first",
            Description = "First project"
        });
        await _client.PostAsJsonAsync("/api/config/projects/add", new
        {
            Name = "Second",
            Path = "second",
            Description = "Second project"
        });

        var removeResponse = await _client.PostAsJsonAsync("/api/config/projects/remove", new
        {
            Name = "First",
            Path = "first"
        });
        removeResponse.EnsureSuccessStatusCode();

        var cfg = await _client.GetFromJsonAsync<FrontendConfig>("/api/config");
        Assert.NotNull(cfg);
        Assert.Equal("second", cfg!.defaultProject);
        Assert.DoesNotContain(cfg.projects, p => p.Path == "first");
    }

    [Fact]
    public async Task Remove_missing_project_returns_not_found()
    {
        var response = await _client.PostAsJsonAsync("/api/config/projects/remove", new
        {
            Name = "Missing",
            Path = "missing"
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
