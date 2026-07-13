using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using System.Text.Json;
using System.Text;
using System.IO.Compression;
using Weaver.Services;

namespace Weaver.Controllers;

[ApiController]
[Route("api/bughosted")]
public class BughostedController : ControllerBase
{
    private readonly ConfigFileService _configFile;
    private readonly IHttpClientFactory _clientFactory;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private const string DefaultBugHostedUrl = "https://bughosted.com";
    private static readonly Dictionary<string, BughostedSession> _sessions = new();

    public BughostedController(ConfigFileService configFile, IHttpClientFactory clientFactory, IConfiguration config, IWebHostEnvironment env)
    {
        _configFile = configFile;
        _clientFactory = clientFactory;
        _config = config;
        _env = env;
    }

    // ─── Filesystem proxy (for BugHosted IDE remote file access) ─────────────

    /// <summary>
    /// Resolves the workspace root the same way FileEditController does.
    /// </summary>
    private string ResolveWorkspaceRoot()
    {
        var configured = _config.GetValue<string>("Editor:WorkspaceRoot");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(Path.Combine(_env.ContentRootPath, configured));
        return Path.GetFullPath(Path.Combine(_env.ContentRootPath, ".."));
    }

    /// <summary>
    /// Validates that a fully-resolved path stays inside the workspace root.
    /// </summary>
    private bool IsInsideWorkspace(string fullPath, string workspaceRoot)
        => fullPath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// List directory contents (folders + files) at the given relative path.
    /// Query params: clientId, path (relative to workspace root, default = root)
    /// </summary>
    [HttpGet("fs/list")]
    public IActionResult FsList([FromQuery] string clientId, [FromQuery] string path = "")
    {
        if (string.IsNullOrWhiteSpace(clientId) || !_sessions.ContainsKey(clientId))
            return Unauthorized(new { error = "Not logged in" });

        var workspaceRoot = ResolveWorkspaceRoot();
        var relativePath = (path ?? "").Trim().TrimStart('/', '\\');
        var targetFull = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath));

        if (!IsInsideWorkspace(targetFull, workspaceRoot))
            return BadRequest(new { error = "Path outside workspace root" });

        if (!Directory.Exists(targetFull))
            return NotFound(new { error = "Directory not found" });

        try
        {
            var dirs = Directory.GetDirectories(targetFull)
                .Select(d => new
                {
                    name = Path.GetFileName(d),
                    path = Path.GetRelativePath(workspaceRoot, d).Replace('\\', '/'),
                    isDirectory = true
                });

            var files = Directory.GetFiles(targetFull)
                .Select(f => new
                {
                    name = Path.GetFileName(f),
                    path = Path.GetRelativePath(workspaceRoot, f).Replace('\\', '/'),
                    isDirectory = false
                });

            var entries = dirs.Concat(files)
                .OrderByDescending(x => x.isDirectory)
                .ThenBy(x => x.name);

            return Ok(new
            {
                path = Path.GetRelativePath(workspaceRoot, targetFull).Replace('\\', '/'),
                entries
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Read the content of a single file.
    /// Query params: clientId, path (relative to workspace root)
    /// </summary>
    [HttpGet("fs/content")]
    public IActionResult FsContent([FromQuery] string clientId, [FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(clientId) || !_sessions.ContainsKey(clientId))
            return Unauthorized(new { error = "Not logged in" });

        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "path is required" });

        var workspaceRoot = ResolveWorkspaceRoot();
        var relativePath = path.Trim().TrimStart('/', '\\');
        var targetFull = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath));

        if (!IsInsideWorkspace(targetFull, workspaceRoot))
            return BadRequest(new { error = "Path outside workspace root" });

        if (!System.IO.File.Exists(targetFull))
            return NotFound(new { error = "File not found" });

        try
        {
            var content = System.IO.File.ReadAllText(targetFull, Encoding.UTF8);
            return Ok(new
            {
                path = Path.GetRelativePath(workspaceRoot, targetFull).Replace('\\', '/'),
                content
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Save (overwrite) the content of a file.
    /// Body: { clientId, path, content, createIfMissing? }
    /// </summary>
    [HttpPost("fs/save")]
    public async Task<IActionResult> FsSave([FromBody] BughostedFsSaveRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.ClientId) || !_sessions.ContainsKey(req.ClientId))
            return Unauthorized(new { error = "Not logged in" });

        if (string.IsNullOrWhiteSpace(req.Path))
            return BadRequest(new { error = "path is required" });

        var workspaceRoot = ResolveWorkspaceRoot();
        var relativePath = req.Path.Trim().TrimStart('/', '\\');
        var targetFull = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath));

        if (!IsInsideWorkspace(targetFull, workspaceRoot))
            return BadRequest(new { error = "Path outside workspace root" });

        if (!System.IO.File.Exists(targetFull) && !req.CreateIfMissing)
            return NotFound(new { error = "File not found" });

        try
        {
            var dir = Path.GetDirectoryName(targetFull);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            await System.IO.File.WriteAllTextAsync(targetFull, req.Content ?? string.Empty, Encoding.UTF8);
            return Ok(new { path = Path.GetRelativePath(workspaceRoot, targetFull).Replace('\\', '/'), written = true });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
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
            var httpReq = new HttpRequestMessage(HttpMethod.Post, url + "/weaver/login")
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

            var weaverAddress = $"{Request.Scheme}://{Request.Host}";
            return Ok(new { clientId = session.ClientId, token = session.Token, user = session.User, weaverAddress });
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
            // Determine the weaver's own address from the incoming request
            var weaverAddress = $"{Request.Scheme}://{Request.Host}";

            var client = _clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(120);
            var payload = JsonSerializer.Serialize(new
            {
                token = session.Token,
                clientId = session.ClientId,
                status = "online",
                kanbanData = GzipCompress(req.KanbanData ?? ""),
                settings = GzipCompress(req.Settings ?? ""),
                weaverAddress
            });
            var httpReq = new HttpRequestMessage(HttpMethod.Post, session.Url + "/weaver/heartbeat")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            var httpRes = await client.SendAsync(httpReq);
            var body = await httpRes.Content.ReadAsStringAsync();
            Console.WriteLine("====== SENDING HEARTBEAT ========" + session.Url + "/weaver/heartbeat");
            return Ok(new { remoteStatus = (int)httpRes.StatusCode, remoteBody = body });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("settings")]
    public async Task<IActionResult> PostSettings([FromBody] BughostedSettingsRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ClientId) || !_sessions.TryGetValue(req.ClientId, out var session))
            return Unauthorized(new { error = "Not logged in" });

        try
        {
            var client = _clientFactory.CreateClient();
            var payload = JsonSerializer.Serialize(new
            {
                token = session.Token,
                settingsData = req.SettingsData
            });
            var httpReq = new HttpRequestMessage(HttpMethod.Post, session.Url + "/weaver/settings")
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

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings([FromQuery] string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId) || !_sessions.TryGetValue(clientId, out var session))
            return Unauthorized(new { error = "Not logged in" });

        try
        {
            var client = _clientFactory.CreateClient();
            var httpReq = new HttpRequestMessage(HttpMethod.Get, session.Url + $"/weaver/settings?token={session.Token}");
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

    [HttpGet("version")]
    public async Task<IActionResult> GetVersion()
    {
        try
        {
            var local = await GetLocalVersionAsync();
            var remote = await GetRemoteVersionAsync() ?? "0";
            return Ok(new { local, remote, updateAvailable = remote != local });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("update")]
    public IActionResult TriggerUpdate()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var remoteVer = await GetRemoteVersionAsync();
                if (remoteVer == null) return;
                await SetLocalVersionAsync(remoteVer);

                var tempDir = Path.Combine(Path.GetTempPath(), "weaver-update");
                Directory.CreateDirectory(tempDir);
                var tempExe = Path.Combine(tempDir, "Weaver.exe");

                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromMinutes(5);
                var bytes = await client.GetByteArrayAsync("https://bughosted.com/assets/Weaver.exe");
                await System.IO.File.WriteAllBytesAsync(tempExe, bytes);

                var currentExe = Environment.ProcessPath!;
                Process.Start(currentExe, $"--update-self \"{tempExe}\" \"{currentExe}\"");
            }
            catch { }

            await Task.Delay(500);
            Environment.Exit(0);
        });

        return Ok(new { updating = true, message = "Update started" });
    }

    [HttpGet("commands")]
    public async Task<IActionResult> GetCommands([FromQuery] string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId) || !_sessions.TryGetValue(clientId, out var session))
            return Unauthorized(new { error = "Not logged in" });

        try
        {
            var client = _clientFactory.CreateClient();
            var httpReq = new HttpRequestMessage(HttpMethod.Get, session.Url + $"/weaver/commands?token={session.Token}");
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

    [HttpGet("events")]
    public async Task Events([FromQuery] string clientId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId) || !_sessions.TryGetValue(clientId, out var session))
        {
            Response.StatusCode = 401;
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        Response.Headers["Connection"] = "keep-alive";
        var bufferingFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
        bufferingFeature?.DisableBuffering();
        await Response.Body.FlushAsync(ct);

        var client = _clientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        var seenIds = new HashSet<int>();
        var keepAliveElapsed = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pollCts.CancelAfter(TimeSpan.FromSeconds(10));
                var httpReq = new HttpRequestMessage(HttpMethod.Get,
                    $"{session.Url}/weaver/commands?token={session.Token}");
                var httpRes = await client.SendAsync(httpReq, pollCts.Token);

                if (httpRes.IsSuccessStatusCode)
                {
                    var body = await httpRes.Content.ReadAsStringAsync(pollCts.Token);
                    var cmds = JsonSerializer.Deserialize<List<JsonElement>>(body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (cmds != null)
                    {
                        foreach (var cmd in cmds)
                        {
                            var id = cmd.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
                            if (id != 0 && seenIds.Add(id))
                            {
                                await Response.WriteAsync($"event: command\ndata: {cmd.GetRawText()}\n\n", ct);
                                await Response.Body.FlushAsync(ct);
                                keepAliveElapsed = 0;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (HttpRequestException) { }
            catch (IOException) { break; }
            catch (Exception ex) { Console.WriteLine($"SSE poll error: {ex.Message}"); }

            if (ct.IsCancellationRequested) break;

            // Process pending file requests (from the database table)
            try
            {
                await ProcessPendingFileRequests(client, session, ct);
            }
            catch (Exception ex) { Console.WriteLine($"File request processing error: {ex.Message}"); }

            keepAliveElapsed++;
            if (keepAliveElapsed >= 30)
            {
                try { await Response.WriteAsync(":\n\n", ct); await Response.Body.FlushAsync(ct); }
                catch { break; }
                keepAliveElapsed = 0;
            }

            try { await Task.Delay(1000, ct); }
            catch { break; }
        }
    }
    /// <summary>
    /// Send benchmark data to BugHosted server.
    /// Body: BenchmarkDataDTO with Token, Date, Benchmark, Steps, Score, Status, Duration, Model, OS, CPU, RAM, GPU
    /// </summary>
    [HttpPost("addbenchmark")]
    public async Task<IActionResult> AddBenchmark([FromBody] BenchmarkDataDTO dto)
    {
        if (string.IsNullOrWhiteSpace(dto.ClientId) || !_sessions.TryGetValue(dto.ClientId, out var session))
            return Unauthorized(new { error = "Not logged in" });

        var cfg = await _configFile.LoadConfigAsync();
        var url = (cfg.bughostedUrl ?? DefaultBugHostedUrl).TrimEnd('/');
        dto.Token = session.Token; // Ensure the token is set from the session
        try
        {
            var client = _clientFactory.CreateClient();
            var payload = JsonSerializer.Serialize(new { dto });
            var httpReq = new HttpRequestMessage(HttpMethod.Post, url + "/addbenchmark")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            var httpRes = await client.SendAsync(httpReq);
            var body = await httpRes.Content.ReadAsStringAsync();

            if (!httpRes.IsSuccessStatusCode)
            {
                return BadRequest(new { error = "Failed to send benchmark", detail = body });
            }

            return Ok(new { message = "Benchmark sent successfully" });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending benchmark: {ex.Message}");
            return StatusCode(500, new { error = "Internal server error while sending benchmark" });
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
            var ackPayload = new Dictionary<string, object?>
            {
                ["token"] = session.Token,
                ["commandId"] = req.CommandId,
                ["status"] = req.Status,
                ["result"] = req.Result
            };
            if (!string.IsNullOrWhiteSpace(req.RequestId))
                ackPayload["requestId"] = req.RequestId;
            var payload = JsonSerializer.Serialize(ackPayload);
            var httpReq = new HttpRequestMessage(HttpMethod.Post, session.Url + "/weaver/commands/ack")
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
            var httpReq = new HttpRequestMessage(HttpMethod.Post, url + "/weaver/login")
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

    [HttpPost("fileEdit")]
    public async Task<IActionResult> FileEdit([FromBody] BughostedFileEditRequest req)
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
                path = req.Path,
                content = req.Content
            });
            var httpReq = new HttpRequestMessage(HttpMethod.Post, session.Url + "/weaver/fileEdit")
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

    [HttpPost("logout")]
    public IActionResult Logout([FromBody] BughostedLogoutRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.ClientId))
            _sessions.Remove(req.ClientId);
        return Ok(new { status = "logged_out" });
    }

    /// <summary>
    /// Polls bughosted.com for pending file requests, processes them locally,
    /// and fulfills them via the API.
    /// </summary>
    private async Task ProcessPendingFileRequests(HttpClient client, BughostedSession session, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(session.ClientId) || string.IsNullOrWhiteSpace(session.Token)) return;

        var pendingUrl = $"{session.Url}/weaver/file-requests/pending?token={session.Token}";
        var pendingRes = await client.GetAsync(pendingUrl, ct);
        if (!pendingRes.IsSuccessStatusCode) return;

        var body = await pendingRes.Content.ReadAsStringAsync(ct);
        var requests = JsonSerializer.Deserialize<List<JsonElement>>(body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (requests == null || requests.Count == 0) return;

        var workspaceRoot = ResolveWorkspaceRoot();

        foreach (var req in requests)
        {
            if (ct.IsCancellationRequested) break;

            var id = req.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
            var type = req.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : "";
            var path = req.TryGetProperty("path", out var pathProp) ? (pathProp.GetString() ?? "") : "";
            var content = req.TryGetProperty("content", out var cProp) ? cProp.GetString() : null;

            if (id == 0 || string.IsNullOrWhiteSpace(type)) continue;
            if (type != "listing" && string.IsNullOrWhiteSpace(path)) continue;

            string? resultJson = null;
            string status = "fulfilled";

            try
            {
                resultJson = ProcessFileRequestLocally(type, path, content, workspaceRoot);
            }
            catch (Exception ex)
            {
                resultJson = JsonSerializer.Serialize(new { error = ex.Message });
                status = "error";
            }

            var fulfillPayload = JsonSerializer.Serialize(new
            {
                token = session.Token,
                requestId = id,
                status,
                result = resultJson ?? "{}"
            });
            var fulfillContent = new StringContent(fulfillPayload, Encoding.UTF8, "application/json");
            try
            {
                await client.PostAsync($"{session.Url}/weaver/file-requests/fulfill", fulfillContent, ct);
            }
            catch { }
        }
    }

    /// <summary>
    /// Processes a single file request locally (listing, content, or save).
    /// Returns a JSON string representing the result.
    /// </summary>
    private string ProcessFileRequestLocally(string type, string path, string? content, string workspaceRoot)
    {
        var relativePath = (path ?? "").Trim().TrimStart('/', '\\');
        var targetFull = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath));

        if (!IsInsideWorkspace(targetFull, workspaceRoot))
            return JsonSerializer.Serialize(new { error = "Path outside workspace root" });

        switch (type)
        {
            case "listing":
                {
                    if (!Directory.Exists(targetFull))
                        return JsonSerializer.Serialize(new { error = "Directory not found" });

                    var dirs = Directory.GetDirectories(targetFull)
                        .Select(d => new
                        {
                            name = Path.GetFileName(d),
                            path = Path.GetRelativePath(workspaceRoot, d).Replace('\\', '/'),
                            isDirectory = true
                        });

                    var files = Directory.GetFiles(targetFull)
                        .Select(f => new
                        {
                            name = Path.GetFileName(f),
                            path = Path.GetRelativePath(workspaceRoot, f).Replace('\\', '/'),
                            isDirectory = false
                        });

                    var entries = dirs.Concat(files)
                        .OrderByDescending(x => x.isDirectory)
                        .ThenBy(x => x.name);

                    return JsonSerializer.Serialize(new
                    {
                        path = Path.GetRelativePath(workspaceRoot, targetFull).Replace('\\', '/'),
                        entries
                    });
                }

            case "content":
                {
                    if (!System.IO.File.Exists(targetFull))
                        return JsonSerializer.Serialize(new { error = "File not found" });

                    var fileContent = System.IO.File.ReadAllText(targetFull, Encoding.UTF8);
                    return JsonSerializer.Serialize(new
                    {
                        path = Path.GetRelativePath(workspaceRoot, targetFull).Replace('\\', '/'),
                        content = fileContent
                    });
                }

            case "save":
                {
                    var dir = Path.GetDirectoryName(targetFull);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    System.IO.File.WriteAllText(targetFull, content ?? string.Empty, Encoding.UTF8);
                    return JsonSerializer.Serialize(new
                    {
                        path = Path.GetRelativePath(workspaceRoot, targetFull).Replace('\\', '/'),
                        written = true
                    });
                }

            default:
                return JsonSerializer.Serialize(new { error = $"Unknown request type: {type}" });
        }
    }

    static string GetVersionFilePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Weaver");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, ".weaver-version");
    }

    static async Task<string> GetLocalVersionAsync()
    {
        var versionFile = GetVersionFilePath();

        if (!System.IO.File.Exists(versionFile))
        {
            await System.IO.File.WriteAllTextAsync(versionFile, "0");
            return "0";
        }

        return (await System.IO.File.ReadAllTextAsync(versionFile)).Trim();
    }

    static async Task SetLocalVersionAsync(string version)
    {
        var versionFile = GetVersionFilePath();
        await System.IO.File.WriteAllTextAsync(versionFile, version);
    }

    static async Task<string?> GetRemoteVersionAsync()
    {
        try
        {
            using var client = new HttpClient();
            var json = await client.GetStringAsync("https://bughosted.com/weaver/version");
            return json.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string GzipCompress(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var bytes = Encoding.UTF8.GetBytes(input);
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Fastest))
        {
            gzip.Write(bytes, 0, bytes.Length);
        }
        return Convert.ToBase64String(ms.ToArray());
    }
}

public class BenchmarkDataDTO
{
    public string? ClientId { get; set; }
    public string? Token { get; set; }
    public string? Date { get; set; }
    public string? Benchmark { get; set; }
    public string? Steps { get; set; }
    public string? Score { get; set; }
    public string? Status { get; set; }
    public string? Duration { get; set; }
    public string? Model { get; set; }
    public string? OS { get; set; }
    public string? CPU { get; set; }
    public string? RAM { get; set; }
    public string? GPU { get; set; }
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
    public string? Settings { get; set; }
}

public class BughostedAckRequest
{
    public string ClientId { get; set; } = "";
    public int CommandId { get; set; }
    public string Status { get; set; } = "executed";
    public string? Result { get; set; }
    public string? RequestId { get; set; }
}

public class BughostedLogoutRequest
{
    public string ClientId { get; set; } = "";
}

public class BughostedSettingsRequest
{
    public string ClientId { get; set; } = "";
    public string? SettingsData { get; set; }
}

public class BughostedFileEditRequest
{
    public string ClientId { get; set; } = "";
    public string Path { get; set; } = "";
    public string Content { get; set; } = "";
}

public class BughostedSession
{
    public string Token { get; set; } = "";
    public string? ClientId { get; set; }
    public string? Url { get; set; }
    public System.Text.Json.JsonElement? User { get; set; }
}

public class BughostedFsSaveRequest
{
    public string ClientId { get; set; } = "";
    public string Path { get; set; } = "";
    public string Content { get; set; } = "";
    public bool CreateIfMissing { get; set; } = true;
}