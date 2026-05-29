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
    public string buildCommands { get; set; } = "dotnet clean & dotnet build";
    public string llamaUrl { get; set; } = "http://localhost:8080";
    public string terminalApprovalMode { get; set; } = "approveAll";
    public List<string> approvedTerminalRoots { get; set; } = new();
    public string? emailImapServer { get; set; }
    public int emailImapPort { get; set; } = 993;
    public bool emailUseSsl { get; set; } = true;
    public string? emailUsername { get; set; }
    public string? emailPassword { get; set; }
}

public class ConfigFileService
{
    private readonly string _configPath;

    public string ConfigPath => _configPath;

    public ConfigFileService(IWebHostEnvironment env)
    {
        _configPath = Path.Combine(env.ContentRootPath, "maestroconfig.json");
    }

    public async Task EnsureConfigAsync()
    {
        if (System.IO.File.Exists(_configPath)) return;
        await WriteConfigAsync(new FrontendConfig());
    }

    public async Task<FrontendConfig> LoadConfigAsync()
    {
        await EnsureConfigAsync();
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
