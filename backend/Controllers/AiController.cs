using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;

[ApiController]
[Route("api/ai")]
public class AiController : ControllerBase
{
    private readonly IHttpClientFactory _clientFactory;
    private readonly IConfiguration _config;
    public AiController(IHttpClientFactory cf, IConfiguration config)
    {
        _clientFactory = cf;
        _config = config;
    }

    [HttpPost("generate")]
    public async Task<IActionResult> Generate([FromBody] JsonElement payload)
    {
        var baseUrl = _config.GetValue<string>("LlamaUrl") ?? "http://192.168.2.58:8080";
        var target = baseUrl.TrimEnd('/') + "/generate";
        var client = _clientFactory.CreateClient("llama");
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        try
        {
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
    public async Task<IActionResult> Proxy([FromQuery]string path)
    {
        var baseUrl = _config.GetValue<string>("LlamaUrl") ?? "http://192.168.2.58:8080";
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
}
