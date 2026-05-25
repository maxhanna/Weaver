using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Text;
using System.Text.Json;
using MaestroBackend.Services;

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
                contentJson = $"{{\"model\":\"{model}\",\"messages\":{messagesRaw},\"stream\":false}}";
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
        try
        {
            var resp = await client.PostAsync(target, new StringContent(body, Encoding.UTF8, Request.ContentType ?? "application/json"));
            var text = await resp.Content.ReadAsStringAsync();
            return Content(text, resp.Content.Headers.ContentType?.ToString() ?? "application/json");
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }

    private async Task<string> GetBaseURL()
    {
        var cfg = await _configFile.LoadConfigAsync();
        return cfg.llamaUrl;
    }
}
