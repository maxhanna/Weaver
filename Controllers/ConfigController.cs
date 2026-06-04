using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using WeaverBackend.Services;

[ApiController]
[Route("api/config")]
public class ConfigController : ControllerBase
{
    private readonly ConfigFileService _configFile;

    public ConfigController(ConfigFileService configFile) => _configFile = configFile;

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var cfg = await _configFile.LoadConfigAsync();
        return Ok(cfg);
    }

    [HttpPost("projects/add")]
    public async Task<IActionResult> AddProject([FromBody] ProjectDto proj, [FromQuery] bool setDefault = false)
    {
        if (proj == null || string.IsNullOrWhiteSpace(proj.Name) || string.IsNullOrWhiteSpace(proj.Path))
            return BadRequest("Name and Path are required");

        var cfg = await _configFile.LoadConfigAsync();
        cfg.projects ??= new List<ProjectDto>();
        if (cfg.projects.Any(p => string.Equals(p.Path, proj.Path, StringComparison.OrdinalIgnoreCase)))
            return Conflict("Project path already exists");

        cfg.projects.Add(proj);
        if (setDefault) cfg.defaultProject = proj.Path;
        await _configFile.WriteConfigAsync(cfg);
        return Ok(cfg);
    }

    [HttpPost("projects/remove")]
    public async Task<IActionResult> RemoveProject([FromBody] ProjectDto proj)
    {
        if (proj == null || string.IsNullOrWhiteSpace(proj.Path))
            return BadRequest("Path is required");

        var cfg = await _configFile.LoadConfigAsync();
        cfg.projects ??= new List<ProjectDto>();
        var removed = cfg.projects.RemoveAll(p => string.Equals(p.Path, proj.Path, StringComparison.OrdinalIgnoreCase)
                                                 || string.Equals(p.Name, proj.Name, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            if (cfg.defaultProject == proj.Path) cfg.defaultProject = cfg.projects.Count > 0 ? cfg.projects[0].Path : string.Empty;
            await _configFile.WriteConfigAsync(cfg);
            return Ok(cfg);
        }
        return NotFound("Project not found");
    }

    [HttpPost("save")]
    public async Task<IActionResult> Save([FromBody] JsonElement body)
    {
        try
        {
            var incoming = JsonSerializer.Deserialize<FrontendConfig>(body.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new FrontendConfig();
            // Preserve existing credentials if frontend omits them (password fields)
            var existing = await _configFile.LoadConfigAsync();

            // Preserve account passwords when frontend omits them
            if (incoming.emailAccounts != null && existing.emailAccounts != null)
            {
                for (var i = 0; i < incoming.emailAccounts.Count; i++)
                {
                    var inc = incoming.emailAccounts[i];
                    if (string.IsNullOrWhiteSpace(inc.password) && i < existing.emailAccounts.Count)
                        inc.password = existing.emailAccounts[i].password;
                }
            }

            // Legacy single-field fallback preservation
            if (string.IsNullOrWhiteSpace(incoming.emailPassword) && !string.IsNullOrWhiteSpace(existing.emailPassword))
                incoming.emailPassword = existing.emailPassword;
            if (string.IsNullOrWhiteSpace(incoming.emailUsername) && !string.IsNullOrWhiteSpace(existing.emailUsername))
                incoming.emailUsername = existing.emailUsername;
            if (string.IsNullOrWhiteSpace(incoming.emailImapServer) && !string.IsNullOrWhiteSpace(existing.emailImapServer))
                incoming.emailImapServer = existing.emailImapServer;
            if (string.IsNullOrWhiteSpace(incoming.bughostedPassword) && !string.IsNullOrWhiteSpace(existing.bughostedPassword))
                incoming.bughostedPassword = existing.bughostedPassword;
            if (string.IsNullOrWhiteSpace(incoming.bughostedUsername) && !string.IsNullOrWhiteSpace(existing.bughostedUsername))
                incoming.bughostedUsername = existing.bughostedUsername;
            if (string.IsNullOrWhiteSpace(incoming.bughostedUrl) && !string.IsNullOrWhiteSpace(existing.bughostedUrl))
                incoming.bughostedUrl = existing.bughostedUrl;
            await _configFile.WriteConfigAsync(incoming);
            return Ok(incoming);
        }
        catch (Exception ex) { return StatusCode(500, ex.Message); }
    }

    [HttpPost("default-project")]
    public async Task<IActionResult> SetDefaultProject([FromBody] SetDefaultProjectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectPath))
            return BadRequest("Project path is required");

        var cfg = await _configFile.LoadConfigAsync();
        if (cfg.projects == null || !cfg.projects.Any(p => string.Equals(p.Path, request.ProjectPath, StringComparison.OrdinalIgnoreCase)))
            return NotFound("Project not found");

        cfg.defaultProject = request.ProjectPath;
        await _configFile.WriteConfigAsync(cfg);
        return Ok(cfg);
    }
}

public class SetDefaultProjectRequest { public string ProjectPath { get; set; } = ""; }
