using System.Diagnostics;
using System.Text;
using System.Linq;

namespace MaestroBackend.Services;

public class TerminalService : IDisposable
{
    private Process? _process;
    private readonly StringBuilder _buffer = new();
    private readonly object _lock = new();

    public bool IsRunning => _process != null && !_process.HasExited;

    public void Start(string shell = "cmd.exe", string args = "/K")
    {
        if (IsRunning) return;
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

    public async Task SendCommandAsync(string command)
    {
        if (!IsRunning) Start();
        if (_process?.StandardInput != null)
        {
            await _process.StandardInput.WriteLineAsync(command);
            await _process.StandardInput.FlushAsync();
        }
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
}
