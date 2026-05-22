using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting;
using System.IO;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;

[ApiController]
[Route("api/config")]
public class ConfigController : ControllerBase
{
    private readonly IWebHostEnvironment _env;
    public ConfigController(IWebHostEnvironment env) => _env = env;

    private string ConfigPath => Path.Combine(_env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot"), "config.json");

    public class ProjectDto { public string Name { get; set; } = ""; public string Path { get; set; } = ""; public string Description { get; set; } = ""; }
    public class FrontendConfig { public List<ProjectDto> projects { get; set; } = new List<ProjectDto>(); public string defaultProject { get; set; } = ""; }

    private async Task<FrontendConfig> LoadConfigAsync()
    {
        if (!System.IO.File.Exists(ConfigPath)) return new FrontendConfig();
        try
        {
            var text = await System.IO.File.ReadAllTextAsync(ConfigPath);
            var cfg = JsonSerializer.Deserialize<FrontendConfig>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return cfg ?? new FrontendConfig();
        }
        catch
        {
            return new FrontendConfig();
        }
    }

    private async Task WriteConfigAsync(FrontendConfig cfg)
    {
        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        var tmp = ConfigPath + ".tmp";
        await System.IO.File.WriteAllTextAsync(tmp, json, Encoding.UTF8);
        System.IO.File.Copy(tmp, ConfigPath, true);
        System.IO.File.Delete(tmp);
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var cfg = await LoadConfigAsync();
        return Ok(cfg);
    }

    [HttpPost("projects/add")]
    public async Task<IActionResult> AddProject([FromBody] ProjectDto proj, [FromQuery] bool setDefault = false)
    {
        if (proj == null || string.IsNullOrWhiteSpace(proj.Name) || string.IsNullOrWhiteSpace(proj.Path))
            return BadRequest("Name and Path are required");

        var cfg = await LoadConfigAsync();
        cfg.projects ??= new List<ProjectDto>();
        if (cfg.projects.Any(p => string.Equals(p.Path, proj.Path, StringComparison.OrdinalIgnoreCase)))
            return Conflict("Project path already exists");

        cfg.projects.Add(proj);
        if (setDefault) cfg.defaultProject = proj.Path;
        await WriteConfigAsync(cfg);
        return Ok(cfg);
    }

    [HttpPost("projects/remove")]
    public async Task<IActionResult> RemoveProject([FromBody] ProjectDto proj)
    {
        if (proj == null || string.IsNullOrWhiteSpace(proj.Path))
            return BadRequest("Path is required");

        var cfg = await LoadConfigAsync();
        cfg.projects ??= new List<ProjectDto>();
        var removed = cfg.projects.RemoveAll(p => string.Equals(p.Path, proj.Path, StringComparison.OrdinalIgnoreCase)
                                                 || string.Equals(p.Name, proj.Name, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            if (cfg.defaultProject == proj.Path) cfg.defaultProject = cfg.projects.Count > 0 ? cfg.projects[0].Path : string.Empty;
            await WriteConfigAsync(cfg);
            return Ok(cfg);
        }
        return NotFound("Project not found");
    }

    [HttpPost("save")]
    public async Task<IActionResult> Save([FromBody] JsonElement body)
    {
        try
        {
            var cfg = JsonSerializer.Deserialize<FrontendConfig>(body.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new FrontendConfig();
            await WriteConfigAsync(cfg);
            return Ok(cfg);
        }
        catch (Exception ex) { return StatusCode(500, ex.Message); }
    }
}
