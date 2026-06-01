using MaestroBackend.Services;

namespace MaestroBackend.ApiIntegrationTests;

public sealed class FakeTerminalService : ITerminalService
{
    private readonly List<PendingTerminalApprovalDto> _pending = new();
    private readonly List<string> _commands = new();
    private readonly object _lock = new();

    public string Output { get; set; } = "";
    public bool IsRunning { get; private set; }

    public IReadOnlyList<string> Commands
    {
        get
        {
            lock (_lock)
            {
                return _commands.ToList();
            }
        }
    }

    public void Start(string? shell = null, string args = "/K")
    {
        IsRunning = true;
    }

    public Task SendCommandAsync(string command, string? workingDirectory = null)
    {
        lock (_lock)
        {
            _commands.Add(command);
            if (command.Contains("blocked", StringComparison.OrdinalIgnoreCase))
            {
                _pending.Add(new PendingTerminalApprovalDto
                {
                    Id = "approval-1",
                    Command = command,
                    RootCommand = "blocked",
                    WorkingDirectory = workingDirectory,
                    CreatedUtc = DateTime.UtcNow
                });
            }
            else
            {
                Output += command + Environment.NewLine + "done" + Environment.NewLine;
            }
        }

        return Task.CompletedTask;
    }

    public IReadOnlyList<PendingTerminalApprovalDto> GetPendingApprovals()
    {
        lock (_lock)
        {
            return _pending.ToList();
        }
    }

    public Task<bool> ApproveCommandAsync(string id, string scope = "once")
    {
        lock (_lock)
        {
            var idx = _pending.FindIndex(p => p.Id == id);
            if (idx < 0) return Task.FromResult(false);
            _pending.RemoveAt(idx);
            Output += $"approved:{scope}" + Environment.NewLine;
            return Task.FromResult(true);
        }
    }

    public bool RejectCommand(string id)
    {
        lock (_lock)
        {
            var idx = _pending.FindIndex(p => p.Id == id);
            if (idx < 0) return false;
            _pending.RemoveAt(idx);
            Output += "rejected" + Environment.NewLine;
            return true;
        }
    }

    public string ReadAll()
    {
        lock (_lock)
        {
            return Output;
        }
    }

    public string ReadLastLines(int lines = 200)
    {
        lock (_lock)
        {
            var split = Output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .TakeLast(Math.Max(0, lines));
            return string.Join(Environment.NewLine, split);
        }
    }
}
