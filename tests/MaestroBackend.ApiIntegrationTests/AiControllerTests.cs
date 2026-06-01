using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace MaestroBackend.ApiIntegrationTests;

public class AiControllerTests : IClassFixture<MaestroWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly MaestroWebApplicationFactory _factory;

    public AiControllerTests(MaestroWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        factory.Responder.Reset();
    }

    [Fact]
    public async Task Generate_forwards_prompt_to_llama_endpoint()
    {
        _factory.Responder.Responder = request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://localhost:8080/v1/chat/completions", request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"ok"}}]}""", Encoding.UTF8, "application/json")
            };
        };

        var response = await _client.PostAsJsonAsync("/api/ai/generate", new
        {
            prompt = "hello"
        });

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"content\":\"ok\"", payload);
    }

    [Fact]
    public async Task Ping_returns_reachable_status_from_upstream()
    {
        _factory.Responder.Responder = request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("http://localhost:8080/api/tags", request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };
        };

        var response = await _client.GetAsync("/api/ai/ping");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<PingResponse>();

        Assert.NotNull(payload);
        Assert.True(payload!.Reachable);
        Assert.Equal(200, payload.StatusCode);
    }

    [Fact]
    public async Task Proxy_forwards_body_to_requested_path()
    {
        _factory.Responder.Responder = request =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json")
            };
        };

        var response = await _client.PostAsJsonAsync("/api/ai/proxy?path=v1/models", new { ping = true });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"ok\":true", payload);
        Assert.NotEmpty(_factory.Responder.Requests);
        var request = _factory.Responder.Requests[^1];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://localhost:8080/v1/models", request.RequestUri!.ToString());
    }

    private sealed class PingResponse
    {
        public bool Reachable { get; set; }
        public int StatusCode { get; set; }
    }
}
