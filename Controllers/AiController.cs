using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Text;
using System.Text.Json;
using WeaverBackend.Services;

[ApiController]
[Route("api/ai")]
public class AiController : ControllerBase
{
    private readonly IHttpClientFactory _clientFactory;
    private readonly IConfiguration _config;
    private readonly ConfigFileService _configFile;

    public AiController(IHttpClientFactory cf, IConfiguration config, ConfigFileService configFile)
    {
        _clientFactory = cf;
        _config = config;
        _configFile = configFile;
    }

    [HttpPost("generate")]
    public async Task<IActionResult> Generate([FromBody] JsonElement payload)
    {
        string baseUrl = await GetBaseURL();
        var target = baseUrl.TrimEnd('/') + "/v1/chat/completions";
        var client = _clientFactory.CreateClient("llama");

        // Determine model: prefer payload.model, then configuration, then fallback
        string model = _config.GetValue<string>("Ai:Model") ?? "medgemma:4b";
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.String)
        {
            var m = modelProp.GetString();
            if (!string.IsNullOrWhiteSpace(m)) model = m!;
        }

        try
        {
            string contentJson;

            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("messages", out var messagesProp))
            {
                // Use provided messages but ensure model is present
                var messagesRaw = messagesProp.GetRawText();
                var toolsRaw = payload.TryGetProperty("tools", out var toolsProp) ? $",\"tools\":{toolsProp.GetRawText()}" : "";
                contentJson = $"{{\"model\":\"{model}\",\"messages\":{messagesRaw}{toolsRaw},\"stream\":false}}";
            }
            else if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("prompt", out var promptProp))
            {
                var prompt = promptProp.GetString() ?? string.Empty;
                var messagesText = JsonSerializer.Serialize(new[] { new { role = "user", content = prompt } });
                contentJson = $"{{\"model\":\"{model}\",\"messages\":{messagesText},\"stream\":false}}";
            }
            else if (payload.ValueKind == JsonValueKind.String)
            {
                var prompt = payload.GetString() ?? string.Empty;
                var messagesText = JsonSerializer.Serialize(new[] { new { role = "user", content = prompt } });
                contentJson = $"{{\"model\":\"{model}\",\"messages\":{messagesText},\"stream\":false}}";
            }
            else
            {
                // Fallback: forward the body as-is
                contentJson = JsonSerializer.Serialize(payload);
            }

            var content = new StringContent(contentJson, Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(target, content);
            var text = await resp.Content.ReadAsStringAsync();
            return Content(text, resp.Content.Headers.ContentType?.ToString() ?? "application/json");
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }

    [HttpPost("proxy")]
    public async Task<IActionResult> Proxy([FromQuery] string path)
    {
        string baseUrl = await GetBaseURL();
        var target = baseUrl.TrimEnd('/') + "/" + (path ?? string.Empty).TrimStart('/');
        var client = _clientFactory.CreateClient("llama");
        var body = await new StreamReader(Request.Body).ReadToEndAsync();
        var mediaType = string.IsNullOrWhiteSpace(Request.ContentType)
            ? "application/json"
            : Request.ContentType.Split(';', 2)[0].Trim();
        try
        {
            var resp = await client.PostAsync(target, new StringContent(body, Encoding.UTF8, mediaType));
            var text = await resp.Content.ReadAsStringAsync();
            return Content(text, resp.Content.Headers.ContentType?.ToString() ?? "application/json");
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }

    /// <summary>
    /// Checks if the LLM server is reachable via HTTP GET with a short timeout.
    /// Returns 200 OK on success, or 502 Bad Gateway if the server is down.
    /// </summary>
    [HttpGet("ping")]
    public async Task<IActionResult> Ping()
    {
        string baseUrl = await GetBaseURL();
        var client = _clientFactory.CreateClient("llama");
        client.Timeout = TimeSpan.FromSeconds(5);
        try
        {
            using var resp = await client.GetAsync(baseUrl.TrimEnd('/') + "/api/tags");
            return Ok(new { reachable = true, statusCode = (int)resp.StatusCode });
        }
        catch (TaskCanceledException)
        {
            return StatusCode(502, new { reachable = false, error = "Connection timed out" });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, new { reachable = false, error = ex.Message });
        }
    }

    private async Task<string> GetBaseURL()
    {
        var cfg = await _configFile.LoadConfigAsync();
        return cfg.llamaUrl;
    }
}
