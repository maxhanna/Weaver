using System.Text;
using System.Text.Json;

namespace MaestroBackend.Services;

public class ProjectDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Description { get; set; } = "";
}

public class FrontendConfig
{
    public List<ProjectDto> projects { get; set; } = new();
    public string defaultProject { get; set; } = "";
    public bool showTerminal { get; set; } = true;
    public bool showAI { get; set; } = true;
    public bool showKanban { get; set; } = true;
    public string LlamaUrl { get; set; } = "http://localhost:8080";
}

public class ConfigFileService
{
    private readonly string _configPath;

    public ConfigFileService(IWebHostEnvironment env)
    {
        _configPath = Path.Combine(env.ContentRootPath, "maestroconfig.json");
    }

    public async Task<FrontendConfig> LoadConfigAsync()
    {
        if (!System.IO.File.Exists(_configPath)) return new FrontendConfig();
        try
        {
            var text = await System.IO.File.ReadAllTextAsync(_configPath);
            var cfg = JsonSerializer.Deserialize<FrontendConfig>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return cfg ?? new FrontendConfig();
        }
        catch
        {
            return new FrontendConfig();
        }
    }

    public async Task WriteConfigAsync(FrontendConfig cfg)
    {
        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        var tmp = _configPath + ".tmp";
        await System.IO.File.WriteAllTextAsync(tmp, json, Encoding.UTF8);
        System.IO.File.Copy(tmp, _configPath, true);
        System.IO.File.Delete(tmp);
    }
}
