namespace MaestroBackend.Services;

public interface ITerminalService
{
    bool IsRunning { get; }
    void Start(string? shell = null, string args = "/K");
    Task SendCommandAsync(string command, string? workingDirectory = null);
    IReadOnlyList<PendingTerminalApprovalDto> GetPendingApprovals();
    Task<bool> ApproveCommandAsync(string id, string scope = "once");
    bool RejectCommand(string id);
    string ReadAll();
    string ReadLastLines(int lines = 200);
}
