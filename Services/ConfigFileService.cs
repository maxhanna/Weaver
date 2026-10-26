using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Weaver.Services;

public class ProjectDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Description { get; set; } = "";
    public string BuildCommands { get; set; } = "";
    /// <summary>
    /// How much whole-app context the improvement-suggestion algorithm should send for
    /// this project: "full" (default — skeleton + other kanban cards + git history),
    /// "board" (skeleton + other kanban cards, no git), or "skeleton" (layout only).
    /// </summary>
    public string SuggestionContextDepth { get; set; } = "full";

    /// <summary>
    /// Whether the idle suggestion loop runs for this project. When ON (default), the agent
    /// automatically tops up Done-column cards to the project's suggestion cap while it is idle;
    /// turning it OFF keeps the agent from generating suggestions unless a card is completed.
    /// </summary>
    public bool IdleSuggestions { get; set; } = true;

    /// <summary>
    /// Maximum number of improvement suggestions generated per completed card for this
    /// project (0-4; default 3). 0 disables suggestion generation entirely for the project.
    /// </summary>
    public int MaxSuggestionsPerCard { get; set; } = 3;
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

public class LlamaEndpoint
{
    public string id { get; set; } = "";
    public string name { get; set; } = "";
    public string url { get; set; } = "http://localhost:8080";
    public string model { get; set; } = "";
}

public class SavedThemeConfig
{
    public string name { get; set; } = "";
    public Dictionary<string, string> colors { get; set; } = new();
}

public class FontSizesConfig
{
    public int log { get; set; } = 18;
    public int llm { get; set; } = 14;
    public int plan { get; set; } = 14;
    public int metaplan { get; set; } = 12;
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
    public bool showNotes { get; set; } = false;
    public bool showMeeting { get; set; } = false;
    public bool meetingMuted { get; set; } = false;
    public int meetingVolume { get; set; } = 70;
    public bool prByDefault { get; set; } = false;
    /// <summary>
    /// Project-wide default for the strict-verifier (hard-gate) toggle. Mirrors the per-card
    /// <see cref="AgentRequest.StrictVerifier"/> three-state: null = legacy behavior (LLM
    /// verifier round + repair loop still run on a deterministic failure), true = hard-gate
    /// on by default for new cards, false = relaxed by default (deterministic issues are
    /// non-blocking). A card's explicit StrictVerifier value always wins over this default.
    /// </summary>
    public bool? defaultStrictVerifier { get; set; }
    public string buildCommands { get; set; } = "dotnet clean & dotnet build";
    public string llamaUrl { get; set; } = "http://localhost:8080";
    public string llamaModel { get; set; } = "medgemma:4b";
    public List<LlamaEndpoint> llamaEndpoints { get; set; } = new();
    public string terminalApprovalMode { get; set; } = "approveAll";
    public List<string> approvedTerminalRoots { get; set; } = new();
    public List<string> disallowedTerminalRoots { get; set; } = new();
    public int maxFileContextChars { get; set; } = 24000;
    public int maxFullFileTokens { get; set; } = 4096;
    public int maxContextChars { get; set; } = 22000;
    public int fileBodyTruncationChars { get; set; } = 8000;
    public int buildOutputTailChars { get; set; } = 8000;
    public int defaultMaxTokens { get; set; } = 2048;
    /// <summary>
    /// Total context window of the configured LLM endpoint (input + output tokens).
    /// Used to derive the terminal-agent conversation compaction threshold: once the
    /// accumulated conversation reaches ~60% of this window it is rolled up, keeping
    /// the last turns in full. Default 8192 is a safe floor for locally-served models;
    /// raise it if your endpoint serves a larger n_ctx and you want the agent to retain
    /// more command history before compaction.
    /// </summary>
    public int contextWindowTokens { get; set; } = 8192;
    public List<EmailAccountConfig> emailAccounts { get; set; } = new();
    public string? emailImapServer { get; set; }
    public int emailImapPort { get; set; } = 993;
    public bool emailUseSsl { get; set; } = true;
    public string? emailUsername { get; set; }
    public string? emailPassword { get; set; }
    public string? bughostedUrl { get; set; }
    public string? bughostedUsername { get; set; }
    public string? bughostedPassword { get; set; }
    public bool bughostedHeartbeatEnabled { get; set; } = false;
    public Dictionary<string, string>? themeColors { get; set; }
    public List<SavedThemeConfig> savedThemes { get; set; } = new();
    public List<string> enabledTools { get; set; } = new();
    public bool includeProjectSkeleton { get; set; } = true;
    public bool includeEditKnowledge { get; set; } = false;
    public bool extendThinking { get; set; } = true;
    public int thinkingMaxTokens { get; set; } = 4096;
    public bool compactThinkingContext { get; set; } = true;
    public bool summarizeDiffContext { get; set; } = true;
    public int diffContextSummaryChars { get; set; } = 6000;
    public int llmTimeoutMinutes { get; set; } = 0;
    public bool useVSCodeInsteadOfIDE { get; set; } = false;
    public string ideTheme { get; set; } = "weaver-dark";
    public bool ideMinimapVisible { get; set; } = true;
    public FontSizesConfig fontSizes { get; set; } = new();
}

public class ConfigFileService
{
    private readonly DatabaseService _db;
    private const string EncryptedPrefix = "DPAPI_B64:";
    private const string ConfigKey = "config";

    public ConfigFileService(DatabaseService db)
    {
        _db = db;
    }

    private static string? EncryptPassword(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
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
            return encrypted;
        }
    }

    private static void EncryptAccountPasswords(FrontendConfig cfg)
    {
        foreach (var acct in cfg.emailAccounts)
            acct.password = EncryptPassword(acct.password);
        cfg.emailPassword = EncryptPassword(cfg.emailPassword);
        cfg.bughostedUsername = EncryptPassword(cfg.bughostedUsername);
        cfg.bughostedPassword = EncryptPassword(cfg.bughostedPassword);
    }

    private static void DecryptAccountPasswords(FrontendConfig cfg)
    {
        foreach (var acct in cfg.emailAccounts)
            acct.password = DecryptPassword(acct.password);
        cfg.emailPassword = DecryptPassword(cfg.emailPassword);
        cfg.bughostedUsername = DecryptPassword(cfg.bughostedUsername);
        cfg.bughostedPassword = DecryptPassword(cfg.bughostedPassword);
    }

    public async Task EnsureConfigAsync()
    {
        var existing = _db.GetValue("weaver_config", ConfigKey);
        if (existing != null) return;
        await WriteConfigAsync(new FrontendConfig());
    }

    public async Task<FrontendConfig> LoadConfigAsync()
    {
        await EnsureConfigAsync();
        FrontendConfig cfg;
        try
        {
            var text = _db.GetValue("weaver_config", ConfigKey);
            cfg = string.IsNullOrWhiteSpace(text)
                ? new FrontendConfig()
                : JsonSerializer.Deserialize<FrontendConfig>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new FrontendConfig();

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
        DecryptAccountPasswords(cfg);
        return cfg;
    }

    public async Task WriteConfigAsync(FrontendConfig cfg)
    {
        EncryptAccountPasswords(cfg);
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
        DecryptAccountPasswords(cfg);

        _db.SetValue("weaver_config", ConfigKey, json);
    }
}
