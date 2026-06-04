using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WeaverBackend.Services;

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
    public bool showIDE { get; set; } = true;
    public bool showKanban { get; set; } = true;
    public bool showCalendar { get; set; } = false;
    public bool prByDefault { get; set; } = false;
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
    private const string EncryptedPrefix = "DPAPI_B64:";

    public string ConfigPath => _configPath;

    public ConfigFileService(IWebHostEnvironment env)
    {
        _configPath = Path.Combine(env.ContentRootPath, "weaverconfig.json");
    }

    /// <summary>
    /// Encrypts a password string using Windows DPAPI (CurrentUser scope).
    /// Returns the encrypted value as base64 with a prefix marker.
    /// If input is null/empty or already encrypted, returns as-is.
    /// </summary>
    private static string? EncryptPassword(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        // Don't double-encrypt
        if (plaintext.StartsWith(EncryptedPrefix, StringComparison.Ordinal)) return plaintext;
        if (!OperatingSystem.IsWindows())
            return plaintext;

        try
        {
            var plainBytes = Encoding.UTF8.GetBytes(plaintext);
            var encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            return EncryptedPrefix + Convert.ToBase64String(encryptedBytes);
        }
        catch
        {
            // If DPAPI fails, store as plaintext fallback
            return plaintext;
        }
    }

    private static string? DecryptPassword(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return encrypted;
        if (!encrypted.StartsWith(EncryptedPrefix, StringComparison.Ordinal)) return encrypted;
        if (!OperatingSystem.IsWindows())
            return encrypted;

        try
        {
            var b64 = encrypted[EncryptedPrefix.Length..];
            var encryptedBytes = Convert.FromBase64String(b64);
            var plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            // If decryption fails, return as-is (might be corrupted or different user)
            return encrypted;
        }
    }

    private static void EncryptAccountPasswords(FrontendConfig cfg)
    {
        foreach (var acct in cfg.emailAccounts)
            acct.password = EncryptPassword(acct.password);
        cfg.emailPassword = EncryptPassword(cfg.emailPassword);
        cfg.bughostedPassword = EncryptPassword(cfg.bughostedPassword);
    }

    private static void DecryptAccountPasswords(FrontendConfig cfg)
    {
        foreach (var acct in cfg.emailAccounts)
            acct.password = DecryptPassword(acct.password);
        cfg.emailPassword = DecryptPassword(cfg.emailPassword);
        cfg.bughostedPassword = DecryptPassword(cfg.bughostedPassword);
    }

    public async Task EnsureConfigAsync()
    {
        if (System.IO.File.Exists(_configPath)) return;
        await WriteConfigAsync(new FrontendConfig());
    }

    public async Task<FrontendConfig> LoadConfigAsync()
    {
        await EnsureConfigAsync();
        FrontendConfig cfg;
        try
        {
            var text = await System.IO.File.ReadAllTextAsync(_configPath);
            cfg = JsonSerializer.Deserialize<FrontendConfig>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new FrontendConfig();

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
        }
        catch
        {
            cfg = new FrontendConfig();
        }
        // Always decrypt passwords after loading
        DecryptAccountPasswords(cfg);
        return cfg;
    }

    public async Task WriteConfigAsync(FrontendConfig cfg)
    {
        // Encrypt passwords before persisting to disk
        EncryptAccountPasswords(cfg);
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

        // Restore plaintext passwords in memory for the caller
        DecryptAccountPasswords(cfg);

        var tmp = _configPath + ".tmp";
        await System.IO.File.WriteAllTextAsync(tmp, json, Encoding.UTF8);
        System.IO.File.Copy(tmp, _configPath, true);
        System.IO.File.Delete(tmp);
    }
}
