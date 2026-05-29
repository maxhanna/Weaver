using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;

namespace MaestroBackend.Services;

public class EmailConfigStatus
{
    public bool IsConfigured { get; set; }
    public string? MissingField { get; set; }
    public string? AutoServer { get; set; }
    public int? AutoPort { get; set; }
    public bool AutoSsl { get; set; } = true;
    public string? ExistingUsername { get; set; }
    public string? ExistingServer { get; set; }
}

public class EmailMessage
{
    public string From { get; set; } = "";
    public string Subject { get; set; } = "";
    public DateTime Date { get; set; }
    public string Body { get; set; } = "";
    public bool IsUnread { get; set; }
}

public class EmailService
{
    private readonly ConfigFileService _configFile;

    public EmailService(ConfigFileService configFile)
    {
        _configFile = configFile;
    }

    public async Task<EmailConfigStatus> CheckAndAutoConfigureAsync()
    {
        var cfg = await _configFile.LoadConfigAsync();
        var status = new EmailConfigStatus
        {
            ExistingUsername = cfg.emailUsername,
            ExistingServer = cfg.emailImapServer
        };

        // Auto-infer Gmail IMAP settings from username
        if (!string.IsNullOrWhiteSpace(cfg.emailUsername))
        {
            var username = cfg.emailUsername.Trim().ToLowerInvariant();
            if (username.EndsWith("@gmail.com") || username.EndsWith("@googlemail.com"))
            {
                if (string.IsNullOrWhiteSpace(cfg.emailImapServer))
                {
                    status.AutoServer = "imap.gmail.com";
                    status.AutoPort = 993;
                    status.AutoSsl = true;
                    cfg.emailImapServer = "imap.gmail.com";
                    cfg.emailImapPort = 993;
                    cfg.emailUseSsl = true;
                    await _configFile.WriteConfigAsync(cfg);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(cfg.emailImapServer))
        {
            status.IsConfigured = false;
            status.MissingField = "emailImapServer";
            return status;
        }
        if (string.IsNullOrWhiteSpace(cfg.emailUsername))
        {
            status.IsConfigured = false;
            status.MissingField = "emailUsername";
            return status;
        }
        if (string.IsNullOrWhiteSpace(cfg.emailPassword))
        {
            status.IsConfigured = false;
            status.MissingField = "emailPassword";
            return status;
        }

        status.IsConfigured = true;
        return status;
    }

    public async Task<List<EmailMessage>> FetchLatestEmailsAsync(int maxEmails = 10, bool unreadOnly = true)
    {
        var cfg = await _configFile.LoadConfigAsync();
        if (string.IsNullOrWhiteSpace(cfg.emailImapServer) || string.IsNullOrWhiteSpace(cfg.emailUsername))
            throw new InvalidOperationException("Email not configured. Set emailImapServer, emailUsername, and emailPassword in maestroconfig.json.");

        using var client = new ImapClient();
        client.ServerCertificateValidationCallback = (s, c, h, e) => true;
        await client.ConnectAsync(cfg.emailImapServer, cfg.emailImapPort, cfg.emailUseSsl);
        await client.AuthenticateAsync(cfg.emailUsername, cfg.emailPassword ?? "");
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly);

        IList<UniqueId> uids;
        if (unreadOnly)
        {
            uids = await client.Inbox.SearchAsync(SearchQuery.NotSeen);
        }
        else
        {
            uids = await client.Inbox.SearchAsync(SearchQuery.All);
        }

        uids = uids.OrderByDescending(u => u).Take(maxEmails).ToList();

        var emails = new List<EmailMessage>();
        foreach (var uid in uids)
        {
            var message = await client.Inbox.GetMessageAsync(uid);
            emails.Add(new EmailMessage
            {
                From = message.From.ToString(),
                Subject = message.Subject ?? "",
                Date = message.Date.DateTime,
                Body = message.TextBody ?? message.HtmlBody ?? "",
                IsUnread = unreadOnly
            });
        }

        await client.DisconnectAsync(true);
        return emails;
    }

    public async Task<string> ValidateConfigAsync()
    {
        try
        {
            var cfgFilePath = _configFile.ConfigPath;
            var cfg = await _configFile.LoadConfigAsync();
            if (string.IsNullOrWhiteSpace(cfg.emailImapServer))
                return "not_configured";
            if (string.IsNullOrWhiteSpace(cfg.emailUsername) || string.IsNullOrWhiteSpace(cfg.emailPassword))
                return $"error: emailUsername or emailPassword is empty — check {cfgFilePath}";

            using var client = new ImapClient();
            client.ServerCertificateValidationCallback = (s, c, h, e) => true;
            await client.ConnectAsync(cfg.emailImapServer, cfg.emailImapPort, cfg.emailUseSsl);
            await client.AuthenticateAsync(cfg.emailUsername, cfg.emailPassword);
            await client.DisconnectAsync(true);
            return $"ok (config: {cfgFilePath})";
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }
}
