using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Weaver.Services;

public class BenchmarkService
{
    private readonly string _scoresPath;
    private readonly string _systemInfoPath;

    public BenchmarkService(string weaverDataDir)
    {
        _scoresPath = Path.Combine(weaverDataDir, "benchmark_scores.json");
        _systemInfoPath = Path.Combine(weaverDataDir, "system_info.json");
    }

    public static SystemInfo DetectSystemInfo()
    {
        var info = new SystemInfo
        {
            Os = RuntimeInformation.OSDescription,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Framework = RuntimeInformation.FrameworkDescription,
            MachineName = Environment.MachineName,
            ProcessorCount = Environment.ProcessorCount,
            Is64Bit = Environment.Is64BitOperatingSystem,
            UserName = Environment.UserName,
            OsVersion = Environment.OSVersion.ToString()
        };

        PopulateWindowsHardwareInfo(info);

        return info;
    }

    private static void PopulateWindowsHardwareInfo(SystemInfo info)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_Processor");
            var cpus = new List<string>();
            foreach (var o in searcher.Get())
            {
                using var obj = o;
                var name = obj["Name"]?.ToString() ?? "";
                var cores = obj["NumberOfCores"]?.ToString() ?? "";
                var threads = obj["NumberOfLogicalProcessors"]?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(name))
                    cpus.Add($"{name} ({cores} cores, {threads} threads)");
            }
            info.Cpu = cpus.Count > 0 ? string.Join("; ", cpus) : null;
        }
        catch { /* WMI not available */ }

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem");
            foreach (var o in searcher.Get())
            {
                using var obj = o;
                var ram = obj["TotalPhysicalMemory"]?.ToString();
                if (long.TryParse(ram, out var bytes))
                    info.RamBytes = bytes;
                break;
            }
        }
        catch { /* WMI not available */ }

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
            var gpus = new List<string>();
            foreach (var o in searcher.Get())
            {
                using var obj = o;
                var name = obj["Name"]?.ToString() ?? "";
                var ram = obj["AdapterRAM"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var entry = name;
                    if (long.TryParse(ram, out var ramBytes) && ramBytes > 0)
                        entry += $" ({ramBytes / 1024 / 1024} MB)";
                    gpus.Add(entry);
                }
            }
            info.Gpu = gpus.Count > 0 ? string.Join("; ", gpus) : null;
        }
        catch { /* WMI not available */ }

    }

    public List<BenchmarkScore> LoadScores()
    {
        try
        {
            if (!System.IO.File.Exists(_scoresPath))
                return new List<BenchmarkScore>();
            var raw = System.IO.File.ReadAllText(_scoresPath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(raw))
                return new List<BenchmarkScore>();
            return JsonSerializer.Deserialize<List<BenchmarkScore>>(raw, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new List<BenchmarkScore>();
        }
        catch
        {
            return new List<BenchmarkScore>();
        }
    }

    public CustomSystemInfo? LoadCustomSystemInfo()
    {
        try
        {
            if (!System.IO.File.Exists(_systemInfoPath))
                return null;
            var raw = System.IO.File.ReadAllText(_systemInfoPath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            return JsonSerializer.Deserialize<CustomSystemInfo>(raw, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    public void SaveCustomSystemInfo(CustomSystemInfo info)
    {
        var dir = Path.GetDirectoryName(_systemInfoPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(info, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        System.IO.File.WriteAllText(_systemInfoPath, json, Encoding.UTF8);
    }

    public SystemInfo ResolveSystemInfo(CustomSystemInfo? overrides)
    {
        var detected = DetectSystemInfo();
        if (overrides == null)
            return detected;
        if (!string.IsNullOrWhiteSpace(overrides.Os))
            detected.Os = overrides.Os;
        if (!string.IsNullOrWhiteSpace(overrides.Cpu))
            detected.Cpu = overrides.Cpu;
        if (overrides.RamGb.HasValue)
            detected.RamBytes = (long)(overrides.RamGb.Value * 1024 * 1024 * 1024);
        if (!string.IsNullOrWhiteSpace(overrides.Gpu))
            detected.Gpu = overrides.Gpu;
        return detected;
    }

    public void SaveScore(BenchmarkScore score)
    {
        var scores = LoadScores();
        scores.Add(score);
        WriteScores(scores);
    }

    public bool DeleteScore(string id)
    {
        var scores = LoadScores();
        var removed = scores.RemoveAll(s => s.Id == id);
        if (removed == 0)
            return false;
        WriteScores(scores);
        return true;
    }

    private void WriteScores(List<BenchmarkScore> scores)
    {
        var dir = Path.GetDirectoryName(_scoresPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(scores, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        System.IO.File.WriteAllText(_scoresPath, json, Encoding.UTF8);
    }

    public static BenchmarkPlanDefinition GetPlanForDifficulty(int level)
    {
        var plans = GetBenchmarkPlans();
        return level < plans.Count ? plans[level] : plans[^1];
    }

    public static List<BenchmarkPlanDefinition> GetBenchmarkPlans()
    {
        return new List<BenchmarkPlanDefinition>
        {
            new BenchmarkPlanDefinition
            {
                Level = 1,
                Name = "Benchmark 1",
                Description = "Basic file creation and editing",
                Steps = new List<BenchmarkStep>
                {
                    new() { Index = 1, Change = "Create a folder called 'benchmark_test_1' at the project root" },
                    new() { Index = 2, Change = "Create a file called 'test.md' inside the benchmark_test_1 folder and write 'Hello world' in it" },
                    new() { Index = 3, Change = "In benchmark_test_1/test.md append 'The capital of France is Paris'" }
                }
            },
            new BenchmarkPlanDefinition
            {
                Level = 2,
                Name = "Benchmark 2",
                Description = "Simple code generation",
                Steps = new List<BenchmarkStep>
                { 
                    new() { Index = 1, Change = "Create a file called 'hello.py' at the project root folder and write a Python script that prints 'Hello, World!' in it" },
                    new() { Index = 2, Change = "Modify hello.py to ask for the user's name and greet them" },
                    new() { Index = 3, Change = "Create a JavaScript file 'hello.js' that logs 'Hello from JS' to the console" }
                }
            },
            new BenchmarkPlanDefinition
            {
                Level = 3,
                Name = "Benchmark 3",
                Description = "HTML/CSS layout tasks",
                Steps = new List<BenchmarkStep>
                {
                    new() { Index = 1, Change = "Create 'page.html' on the desktop/benchmark_test folder with a basic HTML skeleton" },
                    new() { Index = 2, Change = "Add a heading that says 'Benchmark Page' and a paragraph of lorem ipsum text" },
                    new() { Index = 3, Change = "Create 'style.css' and link it to the page; set the background color to light blue" },
                    new() { Index = 4, Change = "Add a centered div with a border, padding, and a shadow" },
                    new() { Index = 5, Change = "Add a button that changes the paragraph text when clicked (inline script)" }
                }
            },
            new BenchmarkPlanDefinition
            {
                Level = 4,
                Name = "Benchmark 4",
                Description = "Basic web server",
                Steps = new List<BenchmarkStep>
                {
                    new() { Index = 1, Change = "Create 'server.py' in desktop/benchmark_test that runs a simple HTTP server on port 9999" },
                    new() { Index = 2, Change = "The server should serve 'index.html' when accessing /" },
                    new() { Index = 3, Change = "Create index.html with some basic content" },
                    new() { Index = 4, Change = "Add a /api/hello endpoint that returns JSON: {\"message\": \"Hello\"}" },
                    new() { Index = 5, Change = "Start the server and verify it responds to a request" }
                }
            },
            new BenchmarkPlanDefinition
            {
                Level = 5,
                Name = "Benchmark 5",
                Description = "Data structure implementation",
                Steps = new List<BenchmarkStep>
                {
                    new() { Index = 1, Change = "Create 'datastructures.py' in desktop/benchmark_test" },
                    new() { Index = 2, Change = "Implement a Stack class with push, pop, peek, and is_empty methods" },
                    new() { Index = 3, Change = "Implement a Queue class with enqueue, dequeue, peek, and is_empty methods" },
                    new() { Index = 4, Change = "Write unit tests for both classes using Python's unittest module" },
                    new() { Index = 5, Change = "Run the tests and verify they pass" }
                }
            }
        };
    }
}

public class CustomSystemInfo
{
    public string? Os { get; set; }
    public string? Cpu { get; set; }
    public double? RamGb { get; set; }
    public string? Gpu { get; set; }
    public string? BenchmarkProjectRoot { get; set; }
    public string? Model { get; set; }
}

public class BenchmarkPlanDefinition
{
    public int Level { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<BenchmarkStep> Steps { get; set; } = new();
}

public class BenchmarkStep
{
    public int Index { get; set; }
    public string Change { get; set; } = "";
}

public class BenchmarkScore
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public int Level { get; set; }
    public int StepsCompleted { get; set; }
    public int TotalSteps { get; set; }
    public double ScorePercent { get; set; }
    public string Status { get; set; } = ""; // "completed", "partial", "failed"
    public SystemInfo? SystemInfo { get; set; }
    public string ModelUsed { get; set; } = "";
    public List<string> FailedSteps { get; set; } = new();
    public string? ErrorReason { get; set; }
    public double DurationMs { get; set; }
}

public class SystemInfo
{
    public string Os { get; set; } = "";
    public string OsArchitecture { get; set; } = "";
    public string ProcessArchitecture { get; set; } = "";
    public string Framework { get; set; } = "";
    public string MachineName { get; set; } = "";
    public int ProcessorCount { get; set; }
    public bool Is64Bit { get; set; }
    public string? Cpu { get; set; }
    public long? RamBytes { get; set; }
    public string? Gpu { get; set; }
    public string UserName { get; set; } = "";
    public string OsVersion { get; set; } = "";
}
