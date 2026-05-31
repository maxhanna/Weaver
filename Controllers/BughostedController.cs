using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text;
using MaestroBackend.Services;

[ApiController]
[Route("api/bughosted")]
public class BughostedController : ControllerBase
{
    private readonly ConfigFileService _configFile;
    private readonly IHttpClientFactory _clientFactory;
    private readonly IConfiguration _config;
    private const string DefaultBugHostedUrl = "https://bughosted.com";
    private static readonly Dictionary<string, BughostedSession> _sessions = new();

    public BughostedController(ConfigFileService configFile, IHttpClientFactory clientFactory, IConfiguration config)
    {
        _configFile = configFile;
        _clientFactory = clientFactory;
        _config = config;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] BughostedLoginRequest req)
    {
        var cfg = await _configFile.LoadConfigAsync();
        var url = (cfg.bughostedUrl ?? DefaultBugHostedUrl).TrimEnd('/');

        try
        {
            var client = _clientFactory.CreateClient();
            var payload = JsonSerializer.Serialize(new { username = req.Username, password = req.Password });
            var httpReq = new HttpRequestMessage(HttpMethod.Post, url + "/maestro/login")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            var httpRes = await client.SendAsync(httpReq);
            var body = await httpRes.Content.ReadAsStringAsync();

            if (!httpRes.IsSuccessStatusCode)
                return Unauthorized(new { error = "Login failed", detail = body });

            var session = JsonSerializer.Deserialize<BughostedSession>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (session == null || string.IsNullOrWhiteSpace(session.Token))
                return Unauthorized(new { error = "Invalid response from server" });

            session.ClientId = Guid.NewGuid().ToString("N");
            session.Url = url;
            _sessions[session.ClientId] = session;

            return Ok(new { clientId = session.ClientId, token = session.Token, user = session.User });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat([FromBody] BughostedHeartbeatRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ClientId) || !_sessions.TryGetValue(req.ClientId, out var session))
            return Unauthorized(new { error = "Not logged in" });

        try
        {
            var client = _clientFactory.CreateClient();
            var payload = JsonSerializer.Serialize(new
            {
                token = session.Token,
                clientId = session.ClientId,
                status = "online",
                kanbanData = req.KanbanData
            });
            var httpReq = new HttpRequestMessage(HttpMethod.Post, session.Url + "/maestro/heartbeat")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            var httpRes = await client.SendAsync(httpReq);
            var body = await httpRes.Content.ReadAsStringAsync();
            return Ok(new { remoteStatus = (int)httpRes.StatusCode, remoteBody = body });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("commands")]
    public async Task<IActionResult> GetCommands([FromQuery] string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId) || !_sessions.TryGetValue(clientId, out var session))
            return Unauthorized(new { error = "Not logged in" });

        try
        {
            var client = _clientFactory.CreateClient();
            var httpReq = new HttpRequestMessage(HttpMethod.Get, session.Url + $"/maestro/commands?token={session.Token}");
            var httpRes = await client.SendAsync(httpReq);
            var body = await httpRes.Content.ReadAsStringAsync();
            if (!httpRes.IsSuccessStatusCode)
                return StatusCode((int)httpRes.StatusCode, body);
            return Content(body, "application/json");
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("commands/ack")]
    public async Task<IActionResult> AckCommand([FromBody] BughostedAckRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ClientId) || !_sessions.TryGetValue(req.ClientId, out var session))
            return Unauthorized(new { error = "Not logged in" });

        try
        {
            var client = _clientFactory.CreateClient();
            var payload = JsonSerializer.Serialize(new
            {
                token = session.Token,
                commandId = req.CommandId,
                status = req.Status,
                result = req.Result
            });
            var httpReq = new HttpRequestMessage(HttpMethod.Post, session.Url + "/maestro/commands/ack")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            var httpRes = await client.SendAsync(httpReq);
            var body = await httpRes.Content.ReadAsStringAsync();
            if (!httpRes.IsSuccessStatusCode)
                return StatusCode((int)httpRes.StatusCode, body);
            return Ok(body);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("test")]
    public async Task<IActionResult> TestConnection([FromBody] BughostedLoginRequest req)
    {
        try
        {
            var cfg = await _configFile.LoadConfigAsync();
            var url = (cfg.bughostedUrl ?? DefaultBugHostedUrl).TrimEnd('/');
            var client = _clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var payload = JsonSerializer.Serialize(new { username = req.Username, password = req.Password });
            var httpReq = new HttpRequestMessage(HttpMethod.Post, url + "/maestro/login")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var httpRes = await client.SendAsync(httpReq);
            sw.Stop();
            var body = await httpRes.Content.ReadAsStringAsync();
            return Ok(new { success = httpRes.IsSuccessStatusCode, statusCode = (int)httpRes.StatusCode, latencyMs = sw.ElapsedMilliseconds, detail = body });
        }
        catch (TaskCanceledException)
        {
            return Ok(new { success = false, error = "Timed out" });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }

    [HttpPost("logout")]
    public IActionResult Logout([FromBody] BughostedLogoutRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.ClientId))
            _sessions.Remove(req.ClientId);
        return Ok(new { status = "logged_out" });
    }
}

public class BughostedLoginRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public class BughostedHeartbeatRequest
{
    public string ClientId { get; set; } = "";
    public string? KanbanData { get; set; }
}

public class BughostedAckRequest
{
    public string ClientId { get; set; } = "";
    public int CommandId { get; set; }
    public string Status { get; set; } = "executed";
    public string? Result { get; set; }
}

public class BughostedLogoutRequest
{
    public string ClientId { get; set; } = "";
}

public class BughostedSession
{
    public string Token { get; set; } = "";
    public string? ClientId { get; set; }
    public string? Url { get; set; }
    public System.Text.Json.JsonElement? User { get; set; }
}
