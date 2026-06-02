using System.Text;
using System.Text.Json;

namespace MaestroBackend.Services;

public class ProjectDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Description { get; set; } = "";
}

public class EmailAccountConfig
{
    public string? imapServer { get; set; }
    public int imapPort { get; set; } = 993;
    public bool useSsl { get; set; } = true;
    public string? username { get; set; }
    public string? password { get; set; }
    public string? label { get; set; }
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
    public List<string> disallowedTerminalRoots { get; set; } = new();
    // Multiple email accounts
    public List<EmailAccountConfig> emailAccounts { get; set; } = new();
    // Legacy single-account fields (kept for backward compat with existing configs)
    public string? emailImapServer { get; set; }
    public int emailImapPort { get; set; } = 993;
    public bool emailUseSsl { get; set; } = true;
    public string? emailUsername { get; set; }
    public string? emailPassword { get; set; }
    public string? bughostedUrl { get; set; }
    public string? bughostedUsername { get; set; }
    public string? bughostedPassword { get; set; }
    public bool bughostedHeartbeatEnabled { get; set; } = false;
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
            cfg ??= new FrontendConfig();

            // Migration: populate emailAccounts from legacy single-account fields
            if (cfg.emailAccounts.Count == 0 &&
                !string.IsNullOrWhiteSpace(cfg.emailUsername))
            {
                cfg.emailAccounts.Add(new EmailAccountConfig
                {
                    imapServer = cfg.emailImapServer,
                    imapPort = cfg.emailImapPort,
                    useSsl = cfg.emailUseSsl,
                    username = cfg.emailUsername,
                    password = cfg.emailPassword,
                    label = cfg.emailUsername.Contains('@') ? cfg.emailUsername[..cfg.emailUsername.IndexOf('@')] : cfg.emailUsername
                });
            }

            return cfg;
        }
        catch
        {
            return new FrontendConfig();
        }
    }

    public async Task WriteConfigAsync(FrontendConfig cfg)
    {
        // Sync legacy single-account fields from first email account for backward compat
        if (cfg.emailAccounts.Count > 0)
        {
            var first = cfg.emailAccounts[0];
            cfg.emailImapServer = first.imapServer;
            cfg.emailImapPort = first.imapPort;
            cfg.emailUseSsl = first.useSsl;
            cfg.emailUsername = first.username;
            cfg.emailPassword = first.password;
        }
        else
        {
            cfg.emailImapServer = null;
            cfg.emailUsername = null;
            cfg.emailPassword = null;
        }

        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        var tmp = _configPath + ".tmp";
        await System.IO.File.WriteAllTextAsync(tmp, json, Encoding.UTF8);
        System.IO.File.Copy(tmp, _configPath, true);
        System.IO.File.Delete(tmp);
    }
}
