using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
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
    public int AccountCount { get; set; }
    public List<string> AccountLabels { get; set; } = new();
}

public class EmailMessage
{
    public string From { get; set; } = "";
    public string Subject { get; set; } = "";
    public DateTime Date { get; set; }
    public string Body { get; set; } = "";
    public bool IsUnread { get; set; }
    public string? AccountLabel { get; set; }
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
        var accounts = cfg.emailAccounts;
        if (accounts.Count == 0)
        {
            return new EmailConfigStatus { IsConfigured = false, MissingField = "emailAccounts" };
        }

        // Auto-infer IMAP settings for each account
        foreach (var acct in accounts)
        {
            if (!string.IsNullOrWhiteSpace(acct.username))
            {
                var username = acct.username.Trim().ToLowerInvariant();
                if (username.EndsWith("@gmail.com") || username.EndsWith("@googlemail.com"))
                {
                    if (acct.imapServer == null || !acct.imapServer.Contains("gmail.com", StringComparison.OrdinalIgnoreCase))
                    {
                        acct.imapServer = "imap.gmail.com";
                        acct.imapPort = 993;
                        acct.useSsl = true;
                    }
                }
                else if (username.EndsWith("@outlook.com") || username.EndsWith("@hotmail.com") ||
                         username.EndsWith("@live.com") || username.EndsWith("@msn.com"))
                {
                    // Only auto-set if server is missing or not already a valid Outlook IMAP server
                    if (acct.imapServer == null ||
                        (!acct.imapServer.Contains("office365.com", StringComparison.OrdinalIgnoreCase) &&
                         !acct.imapServer.Contains("outlook.com", StringComparison.OrdinalIgnoreCase) &&
                         !acct.imapServer.Contains("hotmail.com", StringComparison.OrdinalIgnoreCase)))
                    {
                        acct.imapServer = "outlook.office365.com";
                        acct.imapPort = 993;
                        acct.useSsl = true;
                    }
                }
            }
        }

        await _configFile.WriteConfigAsync(cfg);

        var first = accounts[0];
        var status = new EmailConfigStatus
        {
            ExistingUsername = first.username,
            ExistingServer = first.imapServer,
            AccountCount = accounts.Count,
            AccountLabels = accounts.Select(a => a.label ?? a.username ?? "").Where(l => !string.IsNullOrWhiteSpace(l)).ToList()!
        };

        if (string.IsNullOrWhiteSpace(first.imapServer))
        {
            status.IsConfigured = false;
            status.MissingField = "emailImapServer";
            return status;
        }
        if (string.IsNullOrWhiteSpace(first.username))
        {
            status.IsConfigured = false;
            status.MissingField = "emailUsername";
            return status;
        }
        if (string.IsNullOrWhiteSpace(first.password))
        {
            status.IsConfigured = false;
            status.MissingField = "emailPassword";
            return status;
        }

        status.IsConfigured = true;
        return status;
    }

    public async Task<List<EmailMessage>> FetchLatestEmailsAsync(int maxEmails = 10, bool unreadOnly = true, int? accountIndex = null)
    {
        var cfg = await _configFile.LoadConfigAsync();
        var accounts = cfg.emailAccounts;

        if (accountIndex.HasValue)
        {
            if (accountIndex.Value < 0 || accountIndex.Value >= accounts.Count)
                throw new IndexOutOfRangeException($"Account index {accountIndex} is out of range. {accounts.Count} account(s) configured.");
            return await FetchFromAccountAsync(accounts[accountIndex.Value], maxEmails, unreadOnly);
        }

        // No specific account — fetch from all accounts
        var allEmails = new List<EmailMessage>();
        for (var i = 0; i < accounts.Count; i++)
        {
            try
            {
                var accountEmails = await FetchFromAccountAsync(accounts[i], maxEmails, unreadOnly);
                allEmails.AddRange(accountEmails);
            }
            catch (Exception ex)
            {
                allEmails.Add(new EmailMessage
                {
                    From = "SYSTEM",
                    Subject = $"[ERROR] Could not fetch from {accounts[i].label ?? accounts[i].username ?? $"account {i + 1}"}",
                    Date = DateTime.UtcNow,
                    Body = ex.Message,
                    IsUnread = false,
                    AccountLabel = accounts[i].label ?? accounts[i].username ?? $"Account {i + 1}"
                });
            }
        }

        // Sort all emails by date descending, then take max
        allEmails = allEmails.OrderByDescending(e => e.Date).Take(maxEmails).ToList();
        return allEmails;
    }

    /// <summary>
    /// Connect to IMAP server and authenticate with password-based SASL.
    /// Removes OAuth2 mechanisms and explicitly uses PLAIN to work around
    /// servers (Outlook/Office 365) that advertise XOAUTH2 but reject
    /// password-based attempts through it.
    /// </summary>
    private static async Task ConnectAndAuthenticateAsync(ImapClient client, string host, int port, bool ssl, string username, string password)
    {
        await client.ConnectAsync(host, port, ssl);
        // Some servers (Outlook/Office 365) only advertise XOAUTH2 which
        // requires an OAuth2 token. Remove it so MailKit uses a password-
        // based SASL mechanism instead.
        client.AuthenticationMechanisms.Remove("XOAUTH2");
        client.AuthenticationMechanisms.Remove("OAUTHBEARER");
        await client.AuthenticateAsync(new SaslMechanismPlain(username, password ?? ""));
    }

    private async Task<List<EmailMessage>> FetchFromAccountAsync(EmailAccountConfig acct, int maxEmails, bool unreadOnly)
    {
        if (string.IsNullOrWhiteSpace(acct.imapServer) || string.IsNullOrWhiteSpace(acct.username))
            throw new InvalidOperationException($"Email account '{acct.label ?? acct.username}' is not fully configured.");

        using var client = new ImapClient();
        client.ServerCertificateValidationCallback = (s, c, h, e) => true;
        await ConnectAndAuthenticateAsync(client, acct.imapServer, acct.imapPort, acct.useSsl, acct.username, acct.password ?? "");
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

        if (uids.Count == 0) return new List<EmailMessage>();

        // Fetch message summaries (date + UID) for all matching UIDs so we
        // can sort by INTERNALDATE (UID order does not always reflect date
        // order on Outlook/Office 365).  Then take only the N most recent
        // and fetch full bodies for those.
        var summaries = await client.Inbox.FetchAsync(uids, MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate);
        var recentUids = summaries
            .OrderByDescending(s => s.InternalDate)
            .Take(maxEmails)
            .Select(s => s.UniqueId)
            .ToList();

        var emails = new List<EmailMessage>();
        foreach (var uid in recentUids)
        {
            var message = await client.Inbox.GetMessageAsync(uid);
            emails.Add(new EmailMessage
            {
                From = message.From.ToString(),
                Subject = message.Subject ?? "",
                Date = message.Date.DateTime,
                Body = message.TextBody ?? message.HtmlBody ?? "",
                IsUnread = unreadOnly,
                AccountLabel = acct.label ?? acct.username ?? "Email"
            });
        }

        await client.DisconnectAsync(true);
        return emails;
    }

    public async Task<string> ValidateConfigAsync(int? accountIndex = null)
    {
        try
        {
            var cfg = await _configFile.LoadConfigAsync();
            var accounts = cfg.emailAccounts;

            if (accountIndex.HasValue)
            {
                if (accountIndex.Value < 0 || accountIndex.Value >= accounts.Count)
                    return $"error: account index {accountIndex} out of range ({accounts.Count} account(s))";
                return await ValidateAccountAsync(accounts[accountIndex.Value], accountIndex.Value);
            }

            if (accounts.Count == 0)
                return "not_configured";

            // Validate all accounts
            var results = new List<string>();
            for (var i = 0; i < accounts.Count; i++)
            {
                var result = await ValidateAccountAsync(accounts[i], i);
                results.Add(result);
            }
            return string.Join(" | ", results);
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    private async Task<string> ValidateAccountAsync(EmailAccountConfig acct, int index)
    {
        if (string.IsNullOrWhiteSpace(acct.imapServer))
            return $"account[{index}] not_configured (missing imapServer)";
        if (string.IsNullOrWhiteSpace(acct.username))
            return $"account[{index}] not_configured (missing username)";
        if (string.IsNullOrWhiteSpace(acct.password))
            return $"account[{index}] not_configured (missing password)";

        try
        {
            using var client = new ImapClient();
            client.ServerCertificateValidationCallback = (s, c, h, e) => true;
            await ConnectAndAuthenticateAsync(client, acct.imapServer, acct.imapPort, acct.useSsl, acct.username, acct.password);
            await client.DisconnectAsync(true);
            var label = acct.label ?? acct.username ?? $"account {index + 1}";
            return $"ok ({label})";
        }
        catch (Exception ex)
        {
            var label = acct.label ?? acct.username ?? $"account {index + 1}";
            return $"error ({label}): {ex.Message}";
        }
    }

    /// <summary>
    /// Tests connection for a single account by index, returning a structured result.
    /// </summary>
    public async Task<EmailTestResult> TestConnectionAsync(int accountIndex)
    {
        var cfg = await _configFile.LoadConfigAsync();
        if (accountIndex < 0 || accountIndex >= cfg.emailAccounts.Count)
            return new EmailTestResult { Success = false, Message = "Account not found" };

        var acct = cfg.emailAccounts[accountIndex];
        var label = acct.label ?? acct.username ?? $"Account {accountIndex + 1}";

        if (string.IsNullOrWhiteSpace(acct.imapServer) || string.IsNullOrWhiteSpace(acct.username) || string.IsNullOrWhiteSpace(acct.password))
            return new EmailTestResult { Success = false, Message = $"{label}: Please fill in all fields" };

        try
        {
            using var client = new ImapClient();
            client.ServerCertificateValidationCallback = (s, c, h, e) => true;
            await ConnectAndAuthenticateAsync(client, acct.imapServer, acct.imapPort, acct.useSsl, acct.username, acct.password);
            await client.DisconnectAsync(true);
            return new EmailTestResult { Success = true, Message = $"{label}: Connection successful" };
        }
        catch (Exception ex)
        {
            return new EmailTestResult { Success = false, Message = $"{label}: {ex.Message}" };
        }
    }

    /// <summary>
    /// Tests connection for inline credentials (from UI form, not yet saved).
    /// </summary>
    public async Task<EmailTestResult> TestConnectionInlineAsync(string imapServer, int imapPort, bool useSsl, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(imapServer) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return new EmailTestResult { Success = false, Message = "Please fill in all fields" };

        try
        {
            using var client = new ImapClient();
            client.ServerCertificateValidationCallback = (s, c, h, e) => true;
            await ConnectAndAuthenticateAsync(client, imapServer, imapPort, useSsl, username, password);
            await client.DisconnectAsync(true);
            return new EmailTestResult { Success = true, Message = "Connection successful" };
        }
        catch (Exception ex)
        {
            return new EmailTestResult { Success = false, Message = ex.Message };
        }
    }
}

public class EmailTestResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}