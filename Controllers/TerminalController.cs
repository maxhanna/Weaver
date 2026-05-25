using Microsoft.AspNetCore.Mvc;
using MaestroBackend.Services;
using System.Linq;
using System.Text.RegularExpressions;

[ApiController]
[Route("api/terminal")]
public class TerminalController : ControllerBase
{
    private readonly TerminalService _terminal;
    private const int MaxOutputChars = 24_000;

    public TerminalController(TerminalService terminal) => _terminal = terminal;

    [HttpPost("start")]
    public IActionResult Start()
    {
        _terminal.Start();
        return Ok(new { running = true });
    }

    public class ExecRequest { public string command { get; set; } = ""; }

    [HttpPost("exec")]
    public async Task<IActionResult> Exec([FromBody] ExecRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.command)) return BadRequest("command required");
        await _terminal.SendCommandAsync(req.command);
        await Task.Delay(100);
        return Ok(new { output = _terminal.ReadLastLines(200) });
    }

    [HttpPost("exec-isolated")]
    public async Task<IActionResult> ExecIsolated([FromBody] ExecRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.command)) return BadRequest("command required");
        _terminal.Start();
        var beforeLen = _terminal.ReadAll().Length;
        await _terminal.SendCommandAsync(req.command);
        var prevLen = beforeLen;
        var stableMs = 0;
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            var curLen = _terminal.ReadAll().Length;
            if (curLen == prevLen) { stableMs += 500; if (stableMs >= 2000) break; }
            else { stableMs = 0; prevLen = curLen; }
        }
        var fullOutput = _terminal.ReadAll();
        var output = beforeLen >= 0 && beforeLen < fullOutput.Length
            ? fullOutput.Substring(beforeLen)
            : "";
        if (output.Length > MaxOutputChars) output = output[..MaxOutputChars];
        return Ok(new { output });
    }

    public class InstallPackageRequest
    {
        public string PackageName { get; set; } = "";
        public string? Manager { get; set; }
        public string? Version { get; set; }
        public string? ProjectPath { get; set; }
    }

    [HttpPost("install-package")]
    public async Task<IActionResult> InstallPackage([FromBody] InstallPackageRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.PackageName))
            return BadRequest(new { error = "PackageName is required" });

        _terminal.Start();

        var manager = req.Manager;
        if (string.IsNullOrWhiteSpace(manager))
            manager = DetectPackageManager(req.ProjectPath);

        var command = (manager?.ToLowerInvariant()) switch
        {
            "dotnet" => $"dotnet add package {req.PackageName}" + (req.Version != null ? $" --version {req.Version}" : ""),
            "npm" => $"npm install {req.PackageName}" + (req.Version != null ? $"@{req.Version}" : ""),
            "pip" => $"pip install {req.PackageName}" + (req.Version != null ? $"=={req.Version}" : ""),
            _ => null
        };

        if (command == null)
            return BadRequest(new { error = $"Unknown package manager: {manager}. Use dotnet, npm, or pip." });

        var beforeLen = _terminal.ReadAll().Length;
        await _terminal.SendCommandAsync(command, req.ProjectPath);

        var prevLen = beforeLen;
        var stableMs = 0;
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            var curLen = _terminal.ReadAll().Length;
            if (curLen == prevLen) { stableMs += 500; if (stableMs >= 2000) break; }
            else { stableMs = 0; prevLen = curLen; }
        }

        var fullOutput = _terminal.ReadAll();
        var output = beforeLen >= 0 && beforeLen < fullOutput.Length
            ? fullOutput.Substring(beforeLen)
            : "";
        if (output.Length > MaxOutputChars) output = output[..MaxOutputChars];

        var success = DetectInstallSuccess(manager, output);
        var resolvedVersion = ExtractResolvedVersion(output, manager);

        return Ok(new { success, output, command, manager, resolvedVersion });
    }

    [HttpGet("output")]
    public IActionResult Output([FromQuery] int lines = 200)
    {
        return Ok(new { output = _terminal.ReadLastLines(lines) });
    }

    private static string DetectPackageManager(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
            return "dotnet";
        if (Directory.EnumerateFiles(projectPath, "*.csproj").Any() ||
            Directory.EnumerateFiles(projectPath, "*.sln").Any())
            return "dotnet";
        if (System.IO.File.Exists(Path.Combine(projectPath, "package.json")))
            return "npm";
        if (System.IO.File.Exists(Path.Combine(projectPath, "requirements.txt")) ||
            System.IO.File.Exists(Path.Combine(projectPath, "setup.py")) ||
            System.IO.File.Exists(Path.Combine(projectPath, "Pipfile")))
            return "pip";
        return "dotnet";
    }

    private static bool DetectInstallSuccess(string? manager, string output)
    {
        return manager?.ToLowerInvariant() switch
        {
            "dotnet" => output.Contains("was resolved", StringComparison.OrdinalIgnoreCase) ||
                        output.Contains("Adding Package", StringComparison.OrdinalIgnoreCase),
            "npm" => !output.Contains("ERR!", StringComparison.OrdinalIgnoreCase),
            "pip" => output.Contains("Successfully installed", StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }

    /// <summary>
    /// Extract the resolved package version from install output.
    /// For dotnet: "Package 'X' with version 'Y' was resolved." or "Adding Package 'X' with version 'Y'".
    /// For npm: "installed X@Y" or "+ X@Y"
    /// For pip: "Successfully installed X-Y"
    /// </summary>
    private static string? ExtractResolvedVersion(string output, string? manager)
    {
        try
        {
            return manager?.ToLowerInvariant() switch
            {
                "dotnet" => ExtractDotnetResolvedVersion(output),
                "npm" => ExtractNpmResolvedVersion(output),
                "pip" => ExtractPipResolvedVersion(output),
                _ => null
            };
        }
        catch { return null; }
    }

    private static string? ExtractDotnetResolvedVersion(string output)
    {
        // "Package 'SonarAnalyzer.CSharp' with version '9.0.0.68202' was resolved."
        var m = Regex.Match(output, @"with version\s+'([^']+)'", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        // "Adding Package 'SonarAnalyzer.CSharp' with version '9.0.0.68202'"
        m = Regex.Match(output, @"Adding Package.*with version\s+'([^']+)'", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? ExtractNpmResolvedVersion(string output)
    {
        // "+ package@1.2.3" or "installed package@1.2.3"
        var m = Regex.Match(output, @"[+@]\s*[\d.]+", RegexOptions.IgnoreCase);
        return m.Success ? m.Value.TrimStart('+', ' ', '@') : null;
    }

    private static string? ExtractPipResolvedVersion(string output)
    {
        // "Successfully installed package-1.2.3"
        var m = Regex.Match(output, @"Successfully installed\s+\S+?[-\s](\d[\d.]*)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    public class PingRequest
    {
        public string Host { get; set; } = "";
        public int? Port { get; set; }
        public int? Count { get; set; }
        public int? Timeout { get; set; }
        public string? ProjectPath { get; set; }
    }

    [HttpPost("ping")]
    public async Task<IActionResult> Ping([FromBody] PingRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.Host))
            return BadRequest(new { error = "Host is required" });

        _terminal.Start();
        var (success, output, method) = await TestConnectivity(
            req.Host, req.Port, req.Count, req.Timeout, req.ProjectPath);

        var stats = method == "ping" ? ParsePingStats(output) : null;
        return Ok(new { success, output, method, host = req.Host, port = req.Port, stats });
    }

    /// <summary>
    /// Try TCP port check → ICMP ping → HTTP GET, return first that succeeds.
    /// </summary>
    private async Task<(bool success, string output, string method)> TestConnectivity(
        string host, int? port, int? count, int? timeout, string? projectPath)
    {
        var c = Math.Max(1, count ?? 4);
        var t = Math.Max(500, timeout ?? 2000);

        // 1. TCP port check via Test-NetConnection (Windows) or nc (Linux)
        if (port.HasValue)
        {
            var cmd = OperatingSystem.IsWindows()
                ? $"powershell -Command \"Test-NetConnection {host} -Port {port.Value} -WarningAction SilentlyContinue | Select-Object TcpTestSucceeded, SourceAddress, RemoteAddress | Format-List\""
                : $"nc -zv -w {Math.Max(1, t / 1000)} {host} {port.Value} 2>&1";
            var (ok, outStr) = await RunTerminalCommand(cmd, projectPath);
            if (ok) return (true, outStr, "tcp");
        }

        // 2. ICMP ping
        {
            var cmd = OperatingSystem.IsWindows()
                ? $"ping {host} -n {c} -w {t}"
                : $"ping -c {c} -W {Math.Max(1, t / 1000)} {host}";
            var (ok, outStr) = await RunTerminalCommand(cmd, projectPath);
            if (ok) return (true, outStr, "ping");
        }

        // 3. HTTP GET
        {
            var url = port.HasValue ? $"http://{host}:{port.Value}" : $"http://{host}";
            var cmd = OperatingSystem.IsWindows()
                ? $"powershell -Command \"try {{ $r = Invoke-WebRequest -Uri '{url}' -TimeoutSec {Math.Max(1, t / 1000)} -UseBasicParsing -ErrorAction Stop; Write-Output ('HTTP ' + $r.StatusCode + ' OK') }} catch {{ Write-Output ('FAILED: ' + $_.Exception.Message) }}\""
                : $"curl -s -o /dev/null -w 'HTTP %{{http_code}}' --connect-timeout {Math.Max(1, t / 1000)} {url}";
            var (ok, outStr) = await RunTerminalCommand(cmd, projectPath);
            if (ok) return (true, outStr, "http");
        }

        return (false, "All connectivity checks failed", "none");
    }

    /// <summary>
    /// Run a terminal command with isolated output capture and success detection.
    /// </summary>
    private async Task<(bool success, string output)> RunTerminalCommand(string command, string? projectPath)
    {
        var beforeLen = _terminal.ReadAll().Length;
        await _terminal.SendCommandAsync(command, projectPath);
        var prevLen = beforeLen;
        var stableMs = 0;
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            var curLen = _terminal.ReadAll().Length;
            if (curLen == prevLen) { stableMs += 500; if (stableMs >= 2000) break; }
            else { stableMs = 0; prevLen = curLen; }
        }
        var fullOutput = _terminal.ReadAll();
        var output = beforeLen >= 0 && beforeLen < fullOutput.Length
            ? fullOutput.Substring(beforeLen)
            : "";
        if (output.Length > MaxOutputChars) output = output[..MaxOutputChars];
        var success = output.Contains("TcpTestSucceeded : True", StringComparison.OrdinalIgnoreCase) ||
                      output.Contains("succeeded", StringComparison.OrdinalIgnoreCase) ||
                      output.Contains("Reply from", StringComparison.OrdinalIgnoreCase) ||
                      output.Contains("TTL", StringComparison.Ordinal) ||
                      output.Contains("1 received", StringComparison.OrdinalIgnoreCase) ||
                      output.Contains("0% packet loss", StringComparison.OrdinalIgnoreCase) ||
                      output.Contains("HTTP 200", StringComparison.Ordinal) ||
                      output.Contains("HTTP 20", StringComparison.Ordinal);
        return (success, output);
    }

    private static PingStats? ParsePingStats(string output)
    {
        try
        {
            var stats = new PingStats();
            // Parse Windows format: "Packets: Sent = 4, Received = 4, Lost = 0 (0% loss)"
            var winSent = Regex.Match(output, @"Sent\s*=\s*(\d+)", RegexOptions.IgnoreCase);
            var winRecv = Regex.Match(output, @"Received\s*=\s*(\d+)", RegexOptions.IgnoreCase);
            var winLoss = Regex.Match(output, @"Lost\s*=\s*(\d+)", RegexOptions.IgnoreCase);
            var winLossPct = Regex.Match(output, @"\((\d+)% loss\)", RegexOptions.IgnoreCase);
            // Parse Unix format: "4 packets transmitted, 4 received, 0% packet loss"
            var unixSent = Regex.Match(output, @"(\d+)\s+packets?\s+transmitted", RegexOptions.IgnoreCase);
            var unixRecv = Regex.Match(output, @"(\d+)\s+(?:received|packets?\s+received)", RegexOptions.IgnoreCase);
            var unixLoss = Regex.Match(output, @"(\d+)%\s*packet loss", RegexOptions.IgnoreCase);
            // Parse RTT: "Minimum = 11ms, Maximum = 14ms, Average = 12ms" or "rtt min/avg/max/mdev = 11.123/12.456/14.789/1.234 ms"
            var winMin = Regex.Match(output, @"Minimum\s*=\s*(\d+)", RegexOptions.IgnoreCase);
            var winMax = Regex.Match(output, @"Maximum\s*=\s*(\d+)", RegexOptions.IgnoreCase);
            var winAvg = Regex.Match(output, @"Average\s*=\s*(\d+)", RegexOptions.IgnoreCase);
            var unixRtt = Regex.Match(output, @"rtt\s+min/avg/max/mdev\s*=\s*([\d.]+)/([\d.]+)/([\d.]+)/([\d.]+)", RegexOptions.IgnoreCase);

            if (winSent.Success) stats.Sent = int.Parse(winSent.Groups[1].Value);
            else if (unixSent.Success) stats.Sent = int.Parse(unixSent.Groups[1].Value);
            if (winRecv.Success) stats.Received = int.Parse(winRecv.Groups[1].Value);
            else if (unixRecv.Success) stats.Received = int.Parse(unixRecv.Groups[1].Value);
            if (winLossPct.Success) stats.PacketLoss = int.Parse(winLossPct.Groups[1].Value);
            else if (unixLoss.Success) stats.PacketLoss = int.Parse(unixLoss.Groups[1].Value);
            else if (winLoss.Success)
            {
                var lost = int.Parse(winLoss.Groups[1].Value);
                stats.PacketLoss = stats.Sent > 0 ? (int)Math.Round(lost * 100.0 / stats.Sent) : 0;
            }
            if (winMin.Success) stats.MinLatency = int.Parse(winMin.Groups[1].Value);
            if (winMax.Success) stats.MaxLatency = int.Parse(winMax.Groups[1].Value);
            if (winAvg.Success) stats.AvgLatency = int.Parse(winAvg.Groups[1].Value);
            if (unixRtt.Success)
            {
                stats.MinLatency = (int)Math.Round(double.Parse(unixRtt.Groups[1].Value));
                stats.AvgLatency = (int)Math.Round(double.Parse(unixRtt.Groups[2].Value));
                stats.MaxLatency = (int)Math.Round(double.Parse(unixRtt.Groups[3].Value));
            }
            if (stats.Sent == 0 && stats.Received == 0 && stats.PacketLoss == 0) return null;
            return stats;
        }
        catch { return null; }
    }

    private class PingStats
    {
        public int Sent { get; set; }
        public int Received { get; set; }
        public int PacketLoss { get; set; }
        public int MinLatency { get; set; }
        public int MaxLatency { get; set; }
        public int AvgLatency { get; set; }
    }
}
