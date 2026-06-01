using System.Net;
using System.Net.Http.Json;

namespace MaestroBackend.ApiIntegrationTests;

public class FileHintsControllerTests : IClassFixture<MaestroWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly MaestroWebApplicationFactory _factory;

    public FileHintsControllerTests(MaestroWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        factory.Responder.Reset();
    }

    [Fact]
    public async Task Get_returns_not_found_when_file_missing()
    {
        var hintsPath = Path.Combine(_factory.ContentRootPath, "filehints.json");
        if (File.Exists(hintsPath))
            File.Delete(hintsPath);

        var response = await _client.GetAsync("/api/FileHints");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Put_persists_content_and_get_returns_it()
    {
        var payload = new { hints = new[] { "Program.cs" } };
        var putResponse = await _client.PutAsJsonAsync("/api/FileHints", payload);
        putResponse.EnsureSuccessStatusCode();

        var getResponse = await _client.GetAsync("/api/FileHints");
        getResponse.EnsureSuccessStatusCode();
        var body = await getResponse.Content.ReadAsStringAsync();

        Assert.Contains("\"Program.cs\"", body);
    }
}
