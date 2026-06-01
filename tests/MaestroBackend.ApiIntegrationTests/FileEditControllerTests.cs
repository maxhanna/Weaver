using System.Net;
using System.Net.Http.Json;

namespace MaestroBackend.ApiIntegrationTests;

public class FileEditControllerTests : IClassFixture<MaestroWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly MaestroWebApplicationFactory _factory;

    public FileEditControllerTests(MaestroWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        factory.Responder.Reset();
    }

    [Fact]
    public async Task Write_rejects_paths_outside_workspace_root()
    {
        var response = await _client.PostAsJsonAsync("/api/editor/write", new
        {
            Project = "",
            Path = "..\\outside.txt",
            Content = "blocked",
            Apply = true,
            CreateIfMissing = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Write_creates_file_inside_workspace_and_list_returns_it()
    {
        var writeResponse = await _client.PostAsJsonAsync("/api/editor/write", new
        {
            Project = "sample",
            Path = "nested\\hello.txt",
            Content = "hello world",
            Apply = true,
            CreateIfMissing = true
        });
        writeResponse.EnsureSuccessStatusCode();

        var fullPath = Path.Combine(_factory.WorkspaceRootPath, "sample", "nested", "hello.txt");
        Assert.True(File.Exists(fullPath));
        Assert.Equal("hello world", await File.ReadAllTextAsync(fullPath));

        var listResponse = await _client.GetFromJsonAsync<DirectoryListResponse>("/api/editor/list?project=sample&path=nested");

        Assert.NotNull(listResponse);
        Assert.Contains(listResponse!.Entries, entry => entry.Path == "nested/hello.txt" && !entry.IsDirectory);
    }

    [Fact]
    public async Task Projects_lists_workspace_directories()
    {
        Directory.CreateDirectory(Path.Combine(_factory.WorkspaceRootPath, "proj-a"));
        Directory.CreateDirectory(Path.Combine(_factory.WorkspaceRootPath, "proj-b"));

        var response = await _client.GetAsync("/api/editor/projects");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("proj-a", body);
        Assert.Contains("proj-b", body);
    }

    [Fact]
    public async Task List_rejects_encoded_traversal_outside_project_root()
    {
        var encodedPath = Uri.EscapeDataString("..\\..\\outside");
        var response = await _client.GetAsync($"/api/editor/list?project=sample&path={encodedPath}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed class DirectoryListResponse
    {
        public string Path { get; set; } = "";
        public List<DirectoryEntry> Entries { get; set; } = new();
    }

    private sealed class DirectoryEntry
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public bool IsDirectory { get; set; }
    }
}
