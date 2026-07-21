using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
namespace Weaver.Services;
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
                    // outlook.office365.com is the modern endpoint for all Microsoft accounts.
                    // Always override — older endpoints like imap-mail.outlook.com are unreliable.
                    acct.imapServer = "outlook.office365.com";
                    acct.imapPort = 993;
                    acct.useSsl = true;
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
    /// Connect to IMAP server and authenticate using the best available
    /// password-based SASL mechanism for the server.
    ///
    /// Microsoft consumer accounts (Hotmail/Outlook/Live) dropped plain-password
    /// IMAP auth in 2022 for most accounts, but still work when the account has
    /// IMAP enabled and "Less secure app access" or an App Password is configured.
    /// For these servers we try: NTLM → LOGIN → PLAIN (in that order) by removing
    /// the OAuth2 mechanisms and letting MailKit pick the strongest remaining one.
    ///
    /// Gmail uses an App Password and accepts PLAIN directly.
    /// </summary>
    private static async Task ConnectAndAuthenticateAsync(ImapClient client, string host, int port, bool ssl, string username, string password)
    {
        var sslOptions = ssl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
        await client.ConnectAsync(host, port, sslOptions);
        // Log what the server advertises so auth failures are diagnosable
        var advertised = string.Join(", ", client.AuthenticationMechanisms);
        Console.WriteLine($"[EmailService] {host} advertises mechanisms: [{advertised}]");
        // Remove OAuth2 — we are doing password-based auth only
        client.AuthenticationMechanisms.Remove("XOAUTH2");
        client.AuthenticationMechanisms.Remove("OAUTHBEARER");
        var hostLower = host.ToLowerInvariant();
        var isMicrosoft = hostLower.Contains("outlook.com") ||
                          hostLower.Contains("office365.com") ||
                          hostLower.Contains("hotmail.com") ||
                          hostLower.Contains("live.com");
        if (isMicrosoft)
        {
            // imap-mail.outlook.com advertises [PLAIN, XOAUTH2].
            // App passwords work with PLAIN on this server when IMAP is enabled on the account.
            // Try PLAIN explicitly first via SaslMechanismPlain, then LOGIN via SaslMechanismLogin,
            // then let MailKit pick freely as a last resort.
            var attempts = new List<(string name, Func<Task> auth)>
            {
                ("PLAIN",    () => client.AuthenticateAsync(new SaslMechanismPlain(username, password ?? ""))),
                ("LOGIN",    () => client.AuthenticateAsync(new SaslMechanismLogin(username, password ?? ""))),
                ("DEFAULT",  () => client.AuthenticateAsync(username, password ?? "")),
            };
            Exception? lastEx = null;
            foreach (var (name, auth) in attempts)
            {
                try
                {
                    Console.WriteLine($"[EmailService] {host} — trying {name}");
                    await auth();
                    Console.WriteLine($"[EmailService] {host} — ✓ authenticated via {name}");
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EmailService] {host} — ✗ {name}: {ex.Message}");
                    lastEx = ex;
                }
            }
            throw new Exception(
                $"Authentication failed for {username} on {host} (advertised: [{advertised}]). " +
                $"Tried PLAIN, LOGIN, DEFAULT — all rejected. " +
                $"Check: (1) IMAP is enabled at https://outlook.live.com/mail/options/mail/pop-imap, " +
                $"(2) the app password is current (regenerate at https://account.microsoft.com/security → App passwords), " +
                $"(3) no conditional access policy is blocking IMAP for this account. " +
                $"Last error: {lastEx?.Message}");
        }
        else
        {
            // Gmail and other providers: PLAIN with App Password
            await client.AuthenticateAsync(new SaslMechanismPlain(username, password ?? ""));
        }
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