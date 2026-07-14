using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Reflection;

namespace Weaver.Services;

public class BenchmarkService
{
    public const int BenchmarkSchemaVersion = 2;
    private static readonly object ScoresLock = new();
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
        lock (ScoresLock)
        {
            var scores = LoadScores();
            scores.Add(score);
            WriteScores(scores);
        }
    }

    public async Task<BenchmarkScore> EvaluateAsync(
        int level, string sandboxRoot, string modelUsed, double durationMs,
        IEnumerable<string>? actualStrategies = null, CancellationToken ct = default)
    {
        var plan = GetBenchmarkPlans().SingleOrDefault(p => p.Level == level)
            ?? throw new ArgumentOutOfRangeException(nameof(level), $"Unknown benchmark level {level}.");
        var root = ValidateBenchmarkRoot(sandboxRoot);
        var results = new List<BenchmarkCheckResult>();

        foreach (var check in plan.AcceptanceChecks)
            results.Add(await EvaluateCheckAsync(check, root, ct));

        var correctnessChecks = results.Where(r => r.Category != "preservation").ToList();
        var totalWeight = correctnessChecks.Sum(r => r.Weight);
        var earnedWeight = correctnessChecks.Where(r => r.Passed).Sum(r => r.Weight);
        var correctness = totalWeight == 0 ? 0 : Math.Round(earnedWeight / totalWeight * 100, 1);
        var preservationChecks = results.Where(r => r.Category == "preservation").ToList();
        var preservationWeight = preservationChecks.Sum(r => r.Weight);
        var preservation = preservationChecks.Count == 0 ? 100d
            : Math.Round(preservationChecks.Where(r => r.Passed).Sum(r => r.Weight) * 100.0 / preservationWeight, 1);
        var recovery = results.Any(r => r.FailureCategory == nameof(FailureCategory.RecoveryExhausted)) ? 0d : 100d;
        var efficiency = durationMs <= plan.TargetDurationMs ? 100d
            : Math.Round(Math.Max(0, plan.TargetDurationMs / Math.Max(1, durationMs) * 100), 1);
        var overall = Math.Round(correctness * 0.6 + preservation * 0.2 + recovery * 0.1 + efficiency * 0.1, 1);
        if (correctness < 70) overall = Math.Min(overall, correctness);
        var routingPrompt = plan.Description + "\n" + string.Join("\n", plan.Steps.Select(s => s.Change));
        var route = AgentUtilities.EvaluateMetaPlanGate(routingPrompt);
        var score = new BenchmarkScore
        {
            Level = level,
            StepsCompleted = results.Count(r => r.Passed),
            TotalSteps = results.Count,
            ScorePercent = overall,
            CorrectnessPercent = correctness,
            PreservationPercent = preservation,
            RecoveryPercent = recovery,
            EfficiencyPercent = efficiency,
            Status = results.Count > 0 && results.All(r => r.Passed)
                ? "completed" : results.Any(r => r.Passed) ? "partial" : "failed",
            ModelUsed = modelUsed ?? "",
            ExpectedStrategy = plan.ExpectedStrategy,
            PlannerRoute = route.Route,
            PlannerGateScore = route.Score,
            PlannerGateReason = route.Reason,
            ActualStrategies = (actualStrategies ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RunMetadata = CreateRunMetadata(durationMs),
            DurationMs = durationMs,
            Checks = results,
            FailedSteps = results.Where(r => !r.Passed).Select(r => r.Name).ToList(),
            ErrorReason = string.Join("; ", results.Where(r => !r.Passed).Select(r => $"{r.Name}: {r.Message}"))
        };
        score.FailureCounts = results.Where(r => !r.Passed && !string.IsNullOrWhiteSpace(r.FailureCategory))
            .GroupBy(r => r.FailureCategory!)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var overrides = LoadCustomSystemInfo();
        score.SystemInfo = ResolveSystemInfo(overrides);
        var baseline = LoadScores().Where(s => s.Level == level).OrderByDescending(s => s.Timestamp).FirstOrDefault();
        if (baseline != null) score.Regression = Compare(score, baseline);
        SaveScore(score);
        return score;
    }

    public static BenchmarkRegressionComparison Compare(BenchmarkScore current, BenchmarkScore baseline)
    {
        var currentChecks = current.Checks.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var baselineChecks = baseline.Checks.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        return new BenchmarkRegressionComparison
        {
            BaselineScoreId = baseline.Id,
            ScoreDelta = Math.Round(current.ScorePercent - baseline.ScorePercent, 1),
            DurationDeltaMs = Math.Round(current.DurationMs - baseline.DurationMs, 1),
            RouteChanged = !string.Equals(current.PlannerRoute, baseline.PlannerRoute, StringComparison.OrdinalIgnoreCase),
            NewlyFailingChecks = currentChecks.Values.Where(c => !c.Passed && baselineChecks.TryGetValue(c.Name, out var old) && old.Passed).Select(c => c.Name).ToList(),
            RecoveredChecks = currentChecks.Values.Where(c => c.Passed && baselineChecks.TryGetValue(c.Name, out var old) && !old.Passed).Select(c => c.Name).ToList()
        };
    }

    public async Task<BenchmarkPreparationResult> PrepareAsync(int level, string sandboxRoot, CancellationToken ct = default)
    {
        var plan = GetBenchmarkPlans().SingleOrDefault(p => p.Level == level)
            ?? throw new ArgumentOutOfRangeException(nameof(level), $"Unknown benchmark level {level}.");
        var baseRoot = ValidateBenchmarkRoot(sandboxRoot);
        Directory.CreateDirectory(baseRoot);
        CleanupOldRuns(Path.Combine(baseRoot, ".runs"));
        var runId = $"bm{level}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var root = Path.Combine(baseRoot, ".runs", runId);
        Directory.CreateDirectory(root);
        if (plan.SetupFiles.Count == 0) return new(runId, root);

        if (!string.IsNullOrWhiteSpace(plan.WorkspacePath))
        {
            var workspace = ResolveSandboxPath(root, plan.WorkspacePath);
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
            Directory.CreateDirectory(workspace);
        }

        foreach (var setup in plan.SetupFiles)
        {
            var path = ResolveSandboxPath(root, setup.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Older fixture declarations used raw strings containing visible escape sequences.
            // Materialize those sequences so the agent receives valid source files.
            var content = setup.Content.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\\"", "\"");
            await File.WriteAllTextAsync(path, content, Encoding.UTF8, ct);
        }
        return new(runId, root);
    }

    private static async Task<BenchmarkCheckResult> EvaluateCheckAsync(
        BenchmarkAcceptanceCheck check, string root, CancellationToken ct)
    {
        var result = new BenchmarkCheckResult { Name = check.Name, Type = check.Type, Weight = check.Weight, Category = check.Category };
        try
        {
            var path = ResolveSandboxPath(root, check.Path);
            switch (check.Type)
            {
                case BenchmarkCheckType.DirectoryExists:
                    result.Passed = Directory.Exists(path);
                    result.Message = result.Passed ? "Directory exists." : $"Missing directory: {check.Path}";
                    break;
                case BenchmarkCheckType.FileExists:
                    result.Passed = File.Exists(path);
                    result.Message = result.Passed ? "File exists." : $"Missing file: {check.Path}";
                    break;
                case BenchmarkCheckType.FileContains:
                case BenchmarkCheckType.FileNotContains:
                case BenchmarkCheckType.FileOccurrenceCount:
                    if (!File.Exists(path))
                    {
                        result.Message = $"Missing file: {check.Path}";
                        break;
                    }
                    var content = await File.ReadAllTextAsync(path, ct);
                    if (check.Type == BenchmarkCheckType.FileOccurrenceCount)
                    {
                        var comparison = check.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                        var value = check.Value ?? "";
                        var count = 0;
                        for (var index = 0; value.Length > 0 && (index = content.IndexOf(value, index, comparison)) >= 0; index += value.Length) count++;
                        result.Passed = count == check.ExpectedCount;
                        result.Message = result.Passed ? $"Found exactly {count} occurrence(s)."
                            : $"Expected {check.ExpectedCount} occurrence(s) in {check.Path}, found {count}.";
                        break;
                    }
                    var contains = content.Contains(check.Value ?? "", check.IgnoreCase
                        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                    result.Passed = check.Type == BenchmarkCheckType.FileContains ? contains : !contains;
                    result.Message = result.Passed ? "Content assertion passed."
                        : $"Content assertion failed for {check.Path}.";
                    break;
                case BenchmarkCheckType.CommandSucceeds:
                    var commandResult = await RunCheckCommandAsync(check, root, ct);
                    result.Passed = commandResult.ExitCode == 0 && !commandResult.TimedOut;
                    result.Message = result.Passed ? "Command succeeded." : commandResult.Message;
                    result.ExitCode = commandResult.ExitCode;
                    result.TimedOut = commandResult.TimedOut;
                    result.DurationMs = commandResult.DurationMs;
                    result.StandardOutput = commandResult.StandardOutput;
                    result.StandardError = commandResult.StandardError;
                    break;
                case BenchmarkCheckType.HttpResponse:
                    var httpResult = await RunHttpCheckAsync(check, ct);
                    result.Passed = httpResult.passed;
                    result.Message = httpResult.message;
                    result.DurationMs = httpResult.durationMs;
                    break;
                default:
                    result.Message = $"Unsupported check type: {check.Type}";
                    break;
            }
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
        }
        if (!result.Passed)
        {
            var normalized = FailureTaxonomy.ForBenchmarkCheck(check, result.Message);
            result.FailureCode = normalized.Code;
            result.FailureCategory = normalized.Category.ToString();
            result.NormalizedFailure = normalized.Summary;
        }
        return result;
    }

    private static string ResolveSandboxPath(string root, string? relativePath)
    {
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath ?? ""));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (candidate != root && !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Acceptance check path escapes the benchmark sandbox.");
        return candidate;
    }

    private static string ValidateBenchmarkRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Benchmark root is required.");
        var full = Path.GetFullPath(root);
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A filesystem root cannot be used as the benchmark root.");
        return full;
    }

    private static void CleanupOldRuns(string runsRoot)
    {
        if (!Directory.Exists(runsRoot)) return;
        var directories = new DirectoryInfo(runsRoot).GetDirectories()
            .OrderByDescending(d => d.CreationTimeUtc).ToList();
        foreach (var dir in directories.Where((d, index) => index >= 100 || d.CreationTimeUtc < DateTime.UtcNow.AddDays(-14)))
        {
            try { dir.Delete(recursive: true); } catch { }
        }
    }

    public BenchmarkHistorySummary BuildHistorySummary(int? level = null, string? model = null)
    {
        var scores = LoadScores().Where(s => (!level.HasValue || s.Level == level) &&
            (string.IsNullOrWhiteSpace(model) || string.Equals(s.ModelUsed, model, StringComparison.OrdinalIgnoreCase))).ToList();
        return new BenchmarkHistorySummary
        {
            RunCount = scores.Count,
            AverageScore = scores.Count == 0 ? 0 : Math.Round(scores.Average(s => s.ScorePercent), 1),
            AverageDurationMs = scores.Count == 0 ? 0 : Math.Round(scores.Average(s => s.DurationMs), 1),
            ByLevel = scores.GroupBy(s => s.Level).ToDictionary(g => g.Key, g => Math.Round(g.Average(s => s.ScorePercent), 1)),
            ByModel = scores.GroupBy(s => string.IsNullOrWhiteSpace(s.ModelUsed) ? "unknown" : s.ModelUsed).ToDictionary(g => g.Key, g => Math.Round(g.Average(s => s.ScorePercent), 1), StringComparer.OrdinalIgnoreCase),
            FailureCounts = scores.SelectMany(s => s.FailureCounts).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value), StringComparer.OrdinalIgnoreCase)
        };
    }

    private static async Task<CommandCheckOutcome> RunCheckCommandAsync(
        BenchmarkAcceptanceCheck check, string root, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(check.Command)) return CommandCheckOutcome.Failed("No verification command configured.");
        var workingDir = ResolveSandboxPath(root, check.Path);
        if (!Directory.Exists(workingDir)) return CommandCheckOutcome.Failed($"Missing working directory: {check.Path}");
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", $"/d /s /c \"{check.Command}\"")
            : new ProcessStartInfo("/bin/sh", $"-c \"{check.Command.Replace("\"", "\\\"")}\"");
        psi.WorkingDirectory = workingDir;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(psi);
        if (process == null) return CommandCheckOutcome.Failed("Verification process could not be started.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(check.TimeoutSeconds, 1, 120)));
        var timedOut = false;
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            try { process.Kill(true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
        }
        var stdout = TruncateOutput(await stdoutTask);
        var stderr = TruncateOutput(await stderrTask);
        stopwatch.Stop();
        return new(process.HasExited ? process.ExitCode : -1, timedOut, stopwatch.Elapsed.TotalMilliseconds,
            stdout, stderr, timedOut ? "Verification command timed out." : $"Command exited with code {process.ExitCode}.");
    }

    private static async Task<(bool passed, string message, double durationMs)> RunHttpCheckAsync(
        BenchmarkAcceptanceCheck check, CancellationToken ct)
    {
        if (!Uri.TryCreate(check.Url, UriKind.Absolute, out var uri)) return (false, "Invalid HTTP check URL.", 0);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(check.TimeoutSeconds, 1, 120)));
        using var client = new HttpClient();
        var started = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(check.Method ?? "GET"), uri);
            using var response = await client.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var statusOk = (int)response.StatusCode == check.ExpectedStatus;
            var bodyOk = string.IsNullOrEmpty(check.Value) || body.Contains(check.Value, StringComparison.OrdinalIgnoreCase);
            return (statusOk && bodyOk, statusOk && bodyOk ? "HTTP assertion passed."
                : $"Expected status {check.ExpectedStatus} and body content assertion; received {(int)response.StatusCode}.", started.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (false, "HTTP assertion timed out.", started.Elapsed.TotalMilliseconds); }
        catch (Exception ex) { return (false, $"HTTP assertion failed: {ex.Message}", started.Elapsed.TotalMilliseconds); }
    }

    private static string TruncateOutput(string value) => value.Length <= 2000 ? value : value[..2000] + "…";

    public bool DeleteScore(string id)
    {
        lock (ScoresLock)
        {
            var scores = LoadScores();
            var removed = scores.RemoveAll(s => s.Id == id);
            if (removed == 0) return false;
            WriteScores(scores);
            return true;
        }
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
        var temp = _scoresPath + ".tmp";
        System.IO.File.WriteAllText(temp, json, Encoding.UTF8);
        if (System.IO.File.Exists(_scoresPath)) System.IO.File.Replace(temp, _scoresPath, null);
        else System.IO.File.Move(temp, _scoresPath);
    }

    private static BenchmarkRunMetadata CreateRunMetadata(double durationMs)
    {
        var assembly = typeof(BenchmarkService).Assembly;
        var finished = DateTime.UtcNow;
        return new BenchmarkRunMetadata
        {
            SchemaVersion = BenchmarkSchemaVersion,
            WeaverVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString() ?? "unknown",
            GitCommit = ResolveGitCommit(),
            StartedAt = finished.AddMilliseconds(-Math.Max(0, durationMs)),
            FinishedAt = finished
        };
    }

    private static string? ResolveGitCommit()
    {
        var env = Environment.GetEnvironmentVariable("GIT_COMMIT");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var git = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(git))
                {
                    var head = File.ReadAllText(Path.Combine(git, "HEAD")).Trim();
                    if (!head.StartsWith("ref: ")) return head;
                    var refPath = Path.Combine(git, head[5..].Replace('/', Path.DirectorySeparatorChar));
                    return File.Exists(refPath) ? File.ReadAllText(refPath).Trim() : null;
                }
                dir = dir.Parent;
            }
        }
        catch { }
        return null;
    }

    public static BenchmarkPlanDefinition GetPlanForDifficulty(int level)
    {
        var plans = GetBenchmarkPlans();
        return plans.SingleOrDefault(p => p.Level == level) ?? plans[^1];
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
                },
                AcceptanceChecks =
                [
                    Check.Dir("Benchmark directory exists", "benchmark_test_1"),
                    Check.File("Markdown file exists", "benchmark_test_1/test.md"),
                    Check.Contains("Contains greeting", "benchmark_test_1/test.md", "Hello world"),
                    Check.Contains("Contains Paris fact", "benchmark_test_1/test.md", "The capital of France is Paris")
                ]
            },
            new BenchmarkPlanDefinition
            {
                Level = 2,
                Name = "Benchmark 2",
                Description = "Simple code generation",
                Steps = new List<BenchmarkStep>
                {
                    new() { Index = 1, Change = "Create a folder called 'benchmark_test_2' at the project root" },
                    new() { Index = 2, Change = "Create a file called 'hello.py' inside the benchmark_test_2 folder and write a Python script that prints 'Hello, World!' in it" },
                    new() { Index = 3, Change = "Modify benchmark_test_2/hello.py to ask for the user's name and greet them" },
                    new() { Index = 4, Change = "Create a JavaScript file 'hello.js' inside the benchmark_test_2 folder that logs 'Hello from JS' to the console" }
                },
                AcceptanceChecks =
                [
                    Check.File("Python script exists", "benchmark_test_2/hello.py"),
                    Check.Contains("Python asks for input", "benchmark_test_2/hello.py", "input("),
                    Check.File("JavaScript file exists", "benchmark_test_2/hello.js"),
                    Check.Contains("JavaScript greeting exists", "benchmark_test_2/hello.js", "Hello from JS")
                ]
            },
            new BenchmarkPlanDefinition
            {
                Level = 3,
                Name = "Benchmark 3",
                Description = "HTML/CSS layout tasks",
                Steps = new List<BenchmarkStep>
                {
                    new() { Index = 1, Change = "Create a folder called 'benchmark_test_3' at the project root" },
                    new() { Index = 2, Change =
                    @"Create 'page.html' in the 'benchmark_test_3' folder with a basic HTML skeleton. 
                    Add a heading that says 'Benchmark Page' and a paragraph of lorem ipsum text. Link styles.css to page.html. 
                    Add a centered div with a border, padding, and a shadow. Use styles.css to define the styles. Add a button that changes the paragraph text when clicked (inline script)." },
                    new() { Index = 3, Change = "Create 'styles.css', put it in the 'benchmark_test_3' folder. Give the body element a red background color and style the page.html page elements with CSS." },
                },
                AcceptanceChecks =
                [
                    Check.File("HTML page exists", "benchmark_test_3/page.html"),
                    Check.Contains("Heading exists", "benchmark_test_3/page.html", "Benchmark Page"),
                    Check.Contains("Stylesheet linked", "benchmark_test_3/page.html", "styles.css"),
                    Check.Contains("Interactive button exists", "benchmark_test_3/page.html", "button"),
                    Check.File("Stylesheet exists", "benchmark_test_3/styles.css"),
                    Check.Contains("Red background exists", "benchmark_test_3/styles.css", "red")
                ]
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
                },
                AcceptanceChecks =
                [
                    Check.File("Server script exists", "desktop/benchmark_test/server.py"),
                    Check.Contains("Server uses port 9999", "desktop/benchmark_test/server.py", "9999"),
                    Check.Contains("Hello endpoint exists", "desktop/benchmark_test/server.py", "/api/hello"),
                    Check.File("Index page exists", "desktop/benchmark_test/index.html"),
                    Check.Http("Hello endpoint responds", "http://127.0.0.1:9999/api/hello", 200, "Hello", 5, 3)
                ]
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
                },
                AcceptanceChecks =
                [
                    Check.File("Data structures module exists", "desktop/benchmark_test/datastructures.py"),
                    Check.Contains("Stack implemented", "desktop/benchmark_test/datastructures.py", "class Stack"),
                    Check.Contains("Queue implemented", "desktop/benchmark_test/datastructures.py", "class Queue"),
                    Check.Command("Unit tests pass", "desktop/benchmark_test", "python -m unittest discover", 30, 3)
                ]
            },
            new BenchmarkPlanDefinition
            {
                Level = 6,
                Name = "Edit Strategy 1",
                Description = "Targeted replacement in an existing C# configuration class",
                WorkspacePath = "edit_strategy/targeted-replacement",
                ExpectedStrategy = "oldString/newString targeted replacement",
                SetupFiles =
                [
                    new("edit_strategy/targeted-replacement/AppSettings.cs", """namespace Fixture;\n\npublic sealed class AppSettings\n{\n    // PRESERVE: environment fallback\n    public string Environment { get; set; } = \"Development\";\n    public int RetryCount { get; set; } = 3;\n    public bool EnableTelemetry { get; set; } = true;\n}\n""")
                ],
                Steps = [new() { Index = 1, Change = "In edit_strategy/targeted-replacement/AppSettings.cs change RetryCount from 3 to 5. Do not change any other setting or comment." }],
                AcceptanceChecks =
                [
                    Check.Contains("Retry count updated", "edit_strategy/targeted-replacement/AppSettings.cs", "RetryCount { get; set; } = 5;", 3),
                    Check.Contains("Fallback comment preserved", "edit_strategy/targeted-replacement/AppSettings.cs", "// PRESERVE: environment fallback", 2, "preservation"),
                    Check.Contains("Telemetry setting preserved", "edit_strategy/targeted-replacement/AppSettings.cs", "EnableTelemetry { get; set; } = true;", 2, "preservation"),
                    Check.NotContains("Old retry value removed", "edit_strategy/targeted-replacement/AppSettings.cs", "RetryCount { get; set; } = 3;", 1)
                ]
            },
            new BenchmarkPlanDefinition
            {
                Level = 7,
                Name = "Edit Strategy 2",
                Description = "Insert a complete method into an existing C# service",
                WorkspacePath = "edit_strategy/method-insertion",
                ExpectedStrategy = "structural method insertion",
                SetupFiles =
                [
                    new("edit_strategy/method-insertion/PriceService.cs", """namespace Fixture;\n\npublic sealed class PriceService\n{\n    public decimal ApplyTax(decimal price)\n    {\n        return price * 1.2m;\n    }\n\n    // PRESERVE: public API below\n    public decimal ApplyDiscount(decimal price, decimal discount)\n    {\n        return price - discount;\n    }\n}\n""")
                ],
                Steps = [new() { Index = 1, Change = "Add a public decimal ClampToZero(decimal price) method after ApplyTax in edit_strategy/method-insertion/PriceService.cs. It must return 0 when price is negative and otherwise return price. Preserve both existing methods." }],
                AcceptanceChecks =
                [
                    Check.Contains("New method signature exists", "edit_strategy/method-insertion/PriceService.cs", "decimal ClampToZero(decimal price)", 3),
                    Check.Contains("Negative values are clamped", "edit_strategy/method-insertion/PriceService.cs", "price < 0", 2),
                    Check.Occurs("Method inserted once", "edit_strategy/method-insertion/PriceService.cs", "ClampToZero", 1, 2),
                    Check.Contains("Tax method preserved", "edit_strategy/method-insertion/PriceService.cs", "return price * 1.2m;", 2, "preservation"),
                    Check.Contains("Discount method preserved", "edit_strategy/method-insertion/PriceService.cs", "return price - discount;", 2, "preservation")
                ]
            },
            new BenchmarkPlanDefinition
            {
                Level = 8,
                Name = "Edit Strategy 3",
                Description = "Update an existing property without duplicating it",
                WorkspacePath = "edit_strategy/property-update",
                ExpectedStrategy = "existing-property modification",
                SetupFiles =
                [
                    new("edit_strategy/property-update/CacheOptions.cs", """namespace Fixture;\n\npublic sealed class CacheOptions\n{\n    public int MaxEntries { get; set; } = 100;\n    public TimeSpan Expiration { get; set; } = TimeSpan.FromMinutes(10);\n}\n""")
                ],
                Steps = [new() { Index = 1, Change = "Update MaxEntries to 250 in edit_strategy/property-update/CacheOptions.cs. Modify the existing property; do not add another MaxEntries property." }],
                AcceptanceChecks =
                [
                    Check.Contains("Property value updated", "edit_strategy/property-update/CacheOptions.cs", "MaxEntries { get; set; } = 250;", 3),
                    Check.Occurs("Property is not duplicated", "edit_strategy/property-update/CacheOptions.cs", "MaxEntries", 1, 3, "preservation"),
                    Check.Contains("Expiration preserved", "edit_strategy/property-update/CacheOptions.cs", "TimeSpan.FromMinutes(10)", 2, "preservation")
                ]
            },
            new BenchmarkPlanDefinition
            {
                Level = 9,
                Name = "Edit Strategy 4",
                Description = "Edit the correct section when anchors are ambiguous",
                WorkspacePath = "edit_strategy/ambiguous-section",
                ExpectedStrategy = "section-aware anchored replacement",
                SetupFiles =
                [
                    new("edit_strategy/ambiguous-section/settings.html", """<section id=\"general\">\n  <h2>Settings</h2>\n  <button>Save</button>\n</section>\n<section id=\"users\">\n  <h2>Settings</h2>\n  <button>Save</button>\n</section>\n""")
                ],
                Steps = [new() { Index = 1, Change = "In edit_strategy/ambiguous-section/settings.html change only the Save button inside the users section to say Save Users. Leave the general section unchanged." }],
                AcceptanceChecks =
                [
                    Check.Contains("Users button updated", "edit_strategy/ambiguous-section/settings.html", "<button>Save Users</button>", 3),
                    Check.Occurs("Only one users label exists", "edit_strategy/ambiguous-section/settings.html", "Save Users", 1, 2),
                    Check.Occurs("General Save remains once", "edit_strategy/ambiguous-section/settings.html", "<button>Save</button>", 1, 3, "preservation"),
                    Check.Contains("General section preserved", "edit_strategy/ambiguous-section/settings.html", "<section id=\"general\">", 2, "preservation")
                ]
            },
            new BenchmarkPlanDefinition
            {
                Level = 10,
                Name = "Edit Strategy 5",
                Description = "Propagate a method signature change to its caller",
                WorkspacePath = "edit_strategy/signature-propagation",
                ExpectedStrategy = "coordinated multi-file targeted edits",
                SetupFiles =
                [
                    new("edit_strategy/signature-propagation/Greeter.cs", """namespace Fixture;\n\npublic sealed class Greeter\n{\n    public string Greet(string name) => $\"Hello {name}\";\n}\n"""),
                    new("edit_strategy/signature-propagation/Program.cs", """using Fixture;\n\nvar greeter = new Greeter();\nConsole.WriteLine(greeter.Greet(\"Ada\"));\n// PRESERVE: completion marker\nConsole.WriteLine(\"Done\");\n""")
                ],
                Steps = [new() { Index = 1, Change = "Change Greeter.Greet in edit_strategy/signature-propagation/Greeter.cs to accept a second string parameter named punctuation and append it after the name. Update the call in Program.cs to pass an exclamation mark. Preserve the completion marker and Done output." }],
                AcceptanceChecks =
                [
                    Check.Contains("Signature accepts punctuation", "edit_strategy/signature-propagation/Greeter.cs", "Greet(string name, string punctuation)", 3),
                    Check.Contains("Implementation uses punctuation", "edit_strategy/signature-propagation/Greeter.cs", "{punctuation}", 2),
                    Check.Contains("Caller passes punctuation", "edit_strategy/signature-propagation/Program.cs", "Greet(\"Ada\", \"!\")", 3),
                    Check.Contains("Completion marker preserved", "edit_strategy/signature-propagation/Program.cs", "// PRESERVE: completion marker", 2, "preservation"),
                    Check.Contains("Done output preserved", "edit_strategy/signature-propagation/Program.cs", "Console.WriteLine(\"Done\");", 2, "preservation")
                ]
            },
            new BenchmarkPlanDefinition
            {
                Level = 11, Name = "Cross-language CSS", Description = "Target one CSS property without replacing its block",
                WorkspacePath = "edit_strategy/css", ExpectedStrategy = "oldString/newString targeted replacement",
                SetupFiles = [new("edit_strategy/css/site.css", "body {\n  color: #222;\n  background: white;\n  font-family: sans-serif;\n}\n")],
                Steps = [new() { Index = 1, Change = "In edit_strategy/css/site.css change only the body background from white to #f5f5f5." }],
                AcceptanceChecks = [Check.Contains("Background updated", "edit_strategy/css/site.css", "background: #f5f5f5;", 3), Check.Contains("Color preserved", "edit_strategy/css/site.css", "color: #222;", 2, "preservation"), Check.Contains("Font preserved", "edit_strategy/css/site.css", "font-family: sans-serif;", 2, "preservation")]
            },
            new BenchmarkPlanDefinition
            {
                Level = 12, Name = "Cross-language TypeScript", Description = "Insert a TypeScript method while preserving existing behavior",
                WorkspacePath = "edit_strategy/typescript", ExpectedStrategy = "structural method insertion",
                SetupFiles = [new("edit_strategy/typescript/user-service.ts", "export class UserService {\n  getName(id: number): string {\n    return `user-${id}`;\n  }\n}\n")],
                Steps = [new() { Index = 1, Change = "Add an isValidId(id: number): boolean method to edit_strategy/typescript/user-service.ts that returns true only for positive IDs." }],
                AcceptanceChecks = [Check.Contains("Method added", "edit_strategy/typescript/user-service.ts", "isValidId(id: number): boolean", 3), Check.Contains("Positive check added", "edit_strategy/typescript/user-service.ts", "id > 0", 2), Check.Contains("Existing method preserved", "edit_strategy/typescript/user-service.ts", "return `user-${id}`;", 2, "preservation")]
            },
            new BenchmarkPlanDefinition
            {
                Level = 13, Name = "Cross-language Python", Description = "Modify indentation-sensitive Python code",
                WorkspacePath = "edit_strategy/python", ExpectedStrategy = "oldString/newString targeted replacement",
                SetupFiles = [new("edit_strategy/python/formatter.py", "def format_name(first, last):\n    full = f\"{first} {last}\"\n    return full\n\n\ndef unchanged():\n    return \"keep\"\n")],
                Steps = [new() { Index = 1, Change = "Update format_name in edit_strategy/python/formatter.py to strip whitespace from first and last before formatting. Preserve unchanged()." }],
                AcceptanceChecks = [Check.Contains("First stripped", "edit_strategy/python/formatter.py", "first.strip()", 2), Check.Contains("Last stripped", "edit_strategy/python/formatter.py", "last.strip()", 2), Check.Contains("Unchanged function preserved", "edit_strategy/python/formatter.py", "return \"keep\"", 2, "preservation"), Check.Command("Python syntax valid", "edit_strategy/python", "python -m py_compile formatter.py", 15, 3)]
            },
            new BenchmarkPlanDefinition
            {
                Level = 14, Name = "Cross-language YAML", Description = "Update a YAML value without disturbing indentation",
                WorkspacePath = "edit_strategy/yaml", ExpectedStrategy = "oldString/newString targeted replacement",
                SetupFiles = [new("edit_strategy/yaml/deploy.yml", "service:\n  name: api\n  replicas: 2\n  resources:\n    memory: 256Mi\n")],
                Steps = [new() { Index = 1, Change = "In edit_strategy/yaml/deploy.yml change replicas from 2 to 4 and preserve the nested resources section exactly." }],
                AcceptanceChecks = [Check.Contains("Replicas updated", "edit_strategy/yaml/deploy.yml", "  replicas: 4", 3), Check.Contains("Resources indentation preserved", "edit_strategy/yaml/deploy.yml", "  resources:\n    memory: 256Mi", 3, "preservation"), Check.Occurs("Replicas key remains unique", "edit_strategy/yaml/deploy.yml", "replicas:", 1, 2, "preservation")]
            },
            new BenchmarkPlanDefinition
            {
                Level = 15, Name = "Cross-language JSON", Description = "Modify JSON while retaining valid syntax and unrelated data",
                WorkspacePath = "edit_strategy/json", ExpectedStrategy = "oldString/newString targeted replacement",
                SetupFiles = [new("edit_strategy/json/settings.json", "{\n  \"theme\": \"light\",\n  \"pageSize\": 20,\n  \"features\": [\"search\", \"export\"]\n}\n")],
                Steps = [new() { Index = 1, Change = "In edit_strategy/json/settings.json change pageSize to 50 without changing theme or features." }],
                AcceptanceChecks = [Check.Contains("Page size updated", "edit_strategy/json/settings.json", "\"pageSize\": 50", 3), Check.Contains("Theme preserved", "edit_strategy/json/settings.json", "\"theme\": \"light\"", 2, "preservation"), Check.Contains("Features preserved", "edit_strategy/json/settings.json", "\"search\", \"export\"", 2, "preservation"), Check.Command("JSON syntax valid", "edit_strategy/json", "python -m json.tool settings.json", 15, 3)]
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
    public List<BenchmarkAcceptanceCheck> AcceptanceChecks { get; set; } = new();
    public string? WorkspacePath { get; set; }
    public string? ExpectedStrategy { get; set; }
    public List<BenchmarkSetupFile> SetupFiles { get; set; } = new();
    public double TargetDurationMs { get; set; } = 60_000;
}

public record BenchmarkSetupFile(string Path, string Content);
public record BenchmarkPreparationResult(string RunId, string RunRoot);

public enum BenchmarkCheckType { DirectoryExists, FileExists, FileContains, FileNotContains, FileOccurrenceCount, CommandSucceeds, HttpResponse }

public class BenchmarkAcceptanceCheck
{
    public string Name { get; set; } = "";
    public BenchmarkCheckType Type { get; set; }
    public string? Path { get; set; }
    public string? Value { get; set; }
    public string? Command { get; set; }
    public bool IgnoreCase { get; set; }
    public double Weight { get; set; } = 1;
    public int TimeoutSeconds { get; set; } = 30;
    public int ExpectedCount { get; set; }
    public string Category { get; set; } = "correctness";
    public string? Url { get; set; }
    public string? Method { get; set; }
    public int ExpectedStatus { get; set; } = 200;
}

public static class Check
{
    public static BenchmarkAcceptanceCheck Dir(string name, string path, double weight = 1) => new() { Name = name, Type = BenchmarkCheckType.DirectoryExists, Path = path, Weight = weight };
    public static BenchmarkAcceptanceCheck File(string name, string path, double weight = 1) => new() { Name = name, Type = BenchmarkCheckType.FileExists, Path = path, Weight = weight };
    public static BenchmarkAcceptanceCheck Contains(string name, string path, string value, double weight = 1) => new() { Name = name, Type = BenchmarkCheckType.FileContains, Path = path, Value = value, Weight = weight };
    public static BenchmarkAcceptanceCheck Contains(string name, string path, string value, double weight, string category) => new() { Name = name, Type = BenchmarkCheckType.FileContains, Path = path, Value = value, Weight = weight, Category = category };
    public static BenchmarkAcceptanceCheck NotContains(string name, string path, string value, double weight = 1, string category = "correctness") => new() { Name = name, Type = BenchmarkCheckType.FileNotContains, Path = path, Value = value, Weight = weight, Category = category };
    public static BenchmarkAcceptanceCheck Occurs(string name, string path, string value, int count, double weight = 1, string category = "correctness") => new() { Name = name, Type = BenchmarkCheckType.FileOccurrenceCount, Path = path, Value = value, ExpectedCount = count, Weight = weight, Category = category };
    public static BenchmarkAcceptanceCheck Command(string name, string path, string command, int timeoutSeconds = 30, double weight = 1) => new() { Name = name, Type = BenchmarkCheckType.CommandSucceeds, Path = path, Command = command, TimeoutSeconds = timeoutSeconds, Weight = weight };
    public static BenchmarkAcceptanceCheck Http(string name, string url, int status = 200, string? bodyContains = null, int timeoutSeconds = 10, double weight = 1) => new() { Name = name, Type = BenchmarkCheckType.HttpResponse, Url = url, ExpectedStatus = status, Value = bodyContains, TimeoutSeconds = timeoutSeconds, Weight = weight };
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
    public double CorrectnessPercent { get; set; }
    public double? PreservationPercent { get; set; }
    public double? RecoveryPercent { get; set; }
    public double? EfficiencyPercent { get; set; }
    public string Status { get; set; } = ""; // "completed", "partial", "failed"
    public SystemInfo? SystemInfo { get; set; }
    public string ModelUsed { get; set; } = "";
    public string? ExpectedStrategy { get; set; }
    public string? PlannerRoute { get; set; }
    public int? PlannerGateScore { get; set; }
    public string? PlannerGateReason { get; set; }
    public List<string> ActualStrategies { get; set; } = new();
    public BenchmarkRunMetadata? RunMetadata { get; set; }
    public List<string> FailedSteps { get; set; } = new();
    public string? ErrorReason { get; set; }
    public double DurationMs { get; set; }
    public List<BenchmarkCheckResult> Checks { get; set; } = new();
    public Dictionary<string, int> FailureCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public BenchmarkRegressionComparison? Regression { get; set; }
}

public class BenchmarkRegressionComparison
{
    public string BaselineScoreId { get; set; } = "";
    public double ScoreDelta { get; set; }
    public double DurationDeltaMs { get; set; }
    public bool RouteChanged { get; set; }
    public List<string> NewlyFailingChecks { get; set; } = new();
    public List<string> RecoveredChecks { get; set; } = new();
    public bool HasRegression => ScoreDelta < 0 || NewlyFailingChecks.Count > 0;
}

public class BenchmarkRunMetadata
{
    public int SchemaVersion { get; set; }
    public string WeaverVersion { get; set; } = "";
    public string? GitCommit { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
}

public class BenchmarkHistorySummary
{
    public int RunCount { get; set; }
    public double AverageScore { get; set; }
    public double AverageDurationMs { get; set; }
    public Dictionary<int, double> ByLevel { get; set; } = new();
    public Dictionary<string, double> ByModel { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> FailureCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class BenchmarkCheckResult
{
    public string Name { get; set; } = "";
    public BenchmarkCheckType Type { get; set; }
    public bool Passed { get; set; }
    public double Weight { get; set; }
    public string Message { get; set; } = "";
    public string Category { get; set; } = "correctness";
    public string? FailureCode { get; set; }
    public string? FailureCategory { get; set; }
    public string? NormalizedFailure { get; set; }
    public int? ExitCode { get; set; }
    public bool TimedOut { get; set; }
    public double? DurationMs { get; set; }
    public string? StandardOutput { get; set; }
    public string? StandardError { get; set; }
}

public sealed record CommandCheckOutcome(int ExitCode, bool TimedOut, double DurationMs,
    string StandardOutput, string StandardError, string Message)
{
    public static CommandCheckOutcome Failed(string message) => new(-1, false, 0, "", "", message);
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
