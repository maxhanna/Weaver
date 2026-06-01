using System.Diagnostics;
using System.Text;
using System.Linq;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace MaestroBackend.Services;

public class TerminalService : ITerminalService, IDisposable
{
    private Process? _process;
    private readonly StringBuilder _buffer = new();
    private readonly object _lock = new();
    private readonly ConfigFileService _configFile;
    private readonly ConcurrentDictionary<string, PendingTerminalApproval> _pendingApprovals = new();
    private string _shellName = "";

    public TerminalService(ConfigFileService configFile)
    {
        _configFile = configFile;
    }

    public bool IsRunning => _process != null && !_process.HasExited;

    public void Start(string? shell = null, string args = "/K")
    {
        if (IsRunning) return;
        shell ??= OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/bash";
        if (OperatingSystem.IsWindows() && args == "/K") args = "-NoExit";
        _shellName = Path.GetFileNameWithoutExtension(shell).ToLowerInvariant();
        var psi = new ProcessStartInfo
        {
            FileName = shell,
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => Append(e.Data);
        _process.ErrorDataReceived += (_, e) => Append(e.Data);
        _process.Exited += (_, _) => Append($"[Terminal exited with code {_process.ExitCode}]");
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public async Task SendCommandAsync(string command, string? workingDirectory = null)
    {
        if (!IsRunning) Start();
        await RequireApprovalAsync(command, workingDirectory);
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            var dir = Path.GetFullPath(workingDirectory);
            command = _shellName switch
            {
                "cmd" or "cmd.exe" => $"cd /d \"{dir}\" & {command}",
                "powershell" or "pwsh" => $"Set-Location -LiteralPath \"{dir}\"; {command}",
                _ => $"cd \"{dir}\" && {command}"
            };
        }
        if (_process?.StandardInput != null)
        {
            var translated = command;
            // cmd.exe uses & as command separator, not ; — translate for CMD only
            if (_shellName == "cmd" || _shellName == "cmd.exe")
                translated = Regex.Replace(translated, @";\s*", " & ");
            await _process.StandardInput.WriteLineAsync(translated);
            await _process.StandardInput.FlushAsync();
        }
    }

    public IReadOnlyList<PendingTerminalApprovalDto> GetPendingApprovals()
    {
        return _pendingApprovals.Values
            .OrderBy(p => p.CreatedUtc)
            .Select(p => new PendingTerminalApprovalDto
            {
                Id = p.Id,
                Command = p.Command,
                RootCommand = p.RootCommand,
                WorkingDirectory = p.WorkingDirectory,
                CreatedUtc = p.CreatedUtc
            })
            .ToList();
    }

    public async Task<bool> ApproveCommandAsync(string id, string scope = "once")
    {
        if (!_pendingApprovals.TryRemove(id, out var pending)) return false;

        if (scope.Equals("root", StringComparison.OrdinalIgnoreCase))
        {
            var cfg = await _configFile.LoadConfigAsync();
            cfg.terminalApprovalMode = "approveRoot";
            cfg.approvedTerminalRoots ??= new List<string>();
            if (!cfg.approvedTerminalRoots.Contains(pending.RootCommand, StringComparer.OrdinalIgnoreCase))
            {
                cfg.approvedTerminalRoots.Add(pending.RootCommand);
                cfg.approvedTerminalRoots.Sort(StringComparer.OrdinalIgnoreCase);
            }
            await _configFile.WriteConfigAsync(cfg);
        }
        else if (scope.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var cfg = await _configFile.LoadConfigAsync();
            cfg.terminalApprovalMode = "approveAll";
            await _configFile.WriteConfigAsync(cfg);
        }

        pending.Decision.TrySetResult(true);
        return true;
    }

    public bool RejectCommand(string id)
    {
        if (!_pendingApprovals.TryRemove(id, out var pending)) return false;
        pending.Decision.TrySetResult(false);
        return true;
    }

    private async Task RequireApprovalAsync(string command, string? workingDirectory)
    {
        var cfg = await _configFile.LoadConfigAsync();
        var mode = string.IsNullOrWhiteSpace(cfg.terminalApprovalMode)
            ? "approveAll"
            : cfg.terminalApprovalMode;
        var root = ExtractCommandRoot(command);

        if (cfg.disallowedTerminalRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Execution of terminal commands with root '{root}' is disallowed by configuration.");
        }
        await _configFile.WriteConfigAsync(cfg);


        if (mode.Equals("approveAll", StringComparison.OrdinalIgnoreCase))
            return;

        if (mode.Equals("approveRoot", StringComparison.OrdinalIgnoreCase))
        {
            cfg.approvedTerminalRoots ??= new List<string>();
            if (cfg.approvedTerminalRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
                return;
        }

        var pending = new PendingTerminalApproval
        {
            Id = Guid.NewGuid().ToString("N"),
            Command = command,
            RootCommand = root,
            WorkingDirectory = workingDirectory,
            CreatedUtc = DateTime.UtcNow
        };
        _pendingApprovals[pending.Id] = pending;
        Append($"[Maestro approval required] {command}");

        var approved = await pending.Decision.Task;
        if (!approved)
            throw new UnauthorizedAccessException($"Terminal command rejected: {command}");
    }

    public static string ExtractCommandRoot(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";
        var trimmed = command.Trim();
        trimmed = Regex.Replace(trimmed, @"^(?:cmd(?:\.exe)?\s+/c|powershell(?:\.exe)?\s+-command)\s+", "",
            RegexOptions.IgnoreCase).Trim();
        if (trimmed.Length >= 2 && (trimmed[0] == '"' || trimmed[0] == '\''))
        {
            var quote = trimmed[0];
            var endQuote = trimmed.IndexOf(quote, 1);
            if (endQuote > 1)
            {
                var quotedPath = trimmed.Substring(1, endQuote - 1);
                return Path.GetFileNameWithoutExtension(quotedPath).ToLowerInvariant();
            }
        }
        var match = Regex.Match(trimmed, @"^[""']?([A-Za-z0-9_.:\-\\/]+)");
        if (!match.Success) return trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var root = match.Groups[1].Value.Trim('"', '\'');
        return Path.GetFileNameWithoutExtension(root).ToLowerInvariant();
    }

    private void Append(string? data)
    {
        if (string.IsNullOrEmpty(data)) return;
        lock (_lock)
        {
            _buffer.AppendLine(data);
            if (_buffer.Length > 200000) _buffer.Remove(0, _buffer.Length - 100000);
        }
    }

    public string ReadAll()
    {
        lock (_lock) return _buffer.ToString();
    }

    public string ReadLastLines(int lines = 200)
    {
        lock (_lock)
        {
            var text = _buffer.ToString();
            var split = text.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
            var take = Math.Min(lines, split.Length);
            return string.Join(System.Environment.NewLine, split.Skip(Math.Max(0, split.Length - take)));
        }
    }

    public void Dispose()
    {
        try
        {
            if (_process != null && !_process.HasExited)
            {
                _process.Kill();
            }
            _process?.Dispose();
        }
        catch { }
    }

    private class PendingTerminalApproval
    {
        public string Id { get; init; } = "";
        public string Command { get; init; } = "";
        public string RootCommand { get; init; } = "";
        public string? WorkingDirectory { get; init; }
        public DateTime CreatedUtc { get; init; }
        public TaskCompletionSource<bool> Decision { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public class PendingTerminalApprovalDto
{
    public string Id { get; set; } = "";
    public string Command { get; set; } = "";
    public string RootCommand { get; set; } = "";
    public string? WorkingDirectory { get; set; }
    public DateTime CreatedUtc { get; set; }
}