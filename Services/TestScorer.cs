using System.Text.Json;
using System.Text.RegularExpressions;

namespace Weaver;

/// <summary>
/// Turns the raw orchestrator step results into a <see cref="TestRunResult"/>.
///
/// The orchestrator already runs plan steps one at a time and halts on the first
/// irrecoverable failure (emitting a "plan_halted" marker). So "how far the agent
/// got before breaking" falls straight out of the step results — this class just
/// counts it and packages the machine + version metadata.
///
/// Phase 4a adds five perfect-pass gates (see TestGateResults) layered on top of
/// the 0-100 progress score: PerfectPass = Passed && every gate is explicitly true.
/// </summary>
public static class TestScorer
{
    // Step statuses that count as a completed step.
    static readonly HashSet<string> PassedStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "done", "created", "modified" };

    // Step dict "origin" values that disqualify the noReplan gate.
    static readonly HashSet<string> NonPlanOrigins =
        new(StringComparer.OrdinalIgnoreCase) { "replan", "repair" };

    /// <summary>Computes everything except the formattingClean gate (which needs async I/O — see ScoreAsync).</summary>
    public static TestRunResult Score(
        string testName,
        string? cardId,
        IReadOnlyList<object> steps,
        AgentPlan? plan,
        bool complete,
        IReadOnlyList<string> filesEdited,
        EnvironmentMetadata machine,
        string weaverVersion,
        BenchmarkManifest? benchmark = null,
        ModelInfo? model = null)
    {
        var dicts = steps.OfType<Dictionary<string, object?>>().ToList();

        var stepResults = dicts.Where(d => Str(d, "type") != "plan_halted").ToList();
        var haltMarker = dicts.FirstOrDefault(d => Str(d, "type") == "plan_halted");

        // Denominator: the card's full step count. Prefer the plan (the authored
        // steps); fall back to however many step results we observed.
        var totalSteps = plan?.Plan?.Count ?? 0;
        if (totalSteps <= 0) totalSteps = stepResults.Count;

        var stepsPassed = stepResults.Count(d =>
        {
            var st = Str(d, "status");
            return st != null && PassedStatuses.Contains(st);
        });
        if (totalSteps > 0 && stepsPassed > totalSteps) stepsPassed = totalSteps;

        var halted = haltMarker != null
            || stepResults.Any(d => string.Equals(Str(d, "status"), "error", StringComparison.OrdinalIgnoreCase));

        var score = totalSteps > 0
            ? (int)Math.Round(100.0 * stepsPassed / totalSteps)
            : (stepsPassed > 0 && !halted ? 100 : 0);
        score = Math.Clamp(score, 0, 100);

        // Failure details — from the halt marker if present, else the first errored step.
        string? failedStep = null, failureReason = null;
        if (haltMarker != null)
        {
            failedStep = Str(haltMarker, "failedFile");
            failureReason = Str(haltMarker, "reason");
        }
        else
        {
            var firstError = stepResults.FirstOrDefault(d =>
                string.Equals(Str(d, "status"), "error", StringComparison.OrdinalIgnoreCase));
            if (firstError != null)
            {
                failedStep = Str(firstError, "path") ?? Str(firstError, "description");
                failureReason = Str(firstError, "error");
            }
        }

        var passed = complete && !halted && (totalSteps == 0 || stepsPassed >= totalSteps);

        var codeFile = filesEdited.FirstOrDefault()
                       ?? plan?.Plan?.FirstOrDefault(p => !p.File.StartsWith("_"))?.File;

        var writtenTests = filesEdited.Where(LooksLikeTestFile).ToList();

        var plannedSteps = plan?.Plan?.Count;

        // noReplan: any step tagged with an origin other than the original plan
        // (set by AgentController at its replan/repair call sites) disqualifies it.
        // Steps with no "origin" key are original-plan steps by construction.
        var noReplan = !stepResults.Any(d => NonPlanOrigins.Contains(Str(d, "origin") ?? ""));

        var exactStepCount = benchmark?.ExpectedSteps is int expected
            ? plannedSteps == expected && stepsPassed == expected
            : (bool?)null;

        var structurePreserved = benchmark?.AllowedPaths is { Count: > 0 } allowed
            ? filesEdited.All(f => allowed.Any(pattern => GlobMatch(f, pattern)))
            : (bool?)null;

        var gates = new TestGateResults
        {
            // Every "command" step type is routed through TerminalService's approval
            // flow with no bypass path in the current step executor — see
            // docs/test-benchmark-bughosted-contract.md for the caveat that this will
            // need real per-command telemetry if a bypass path is ever introduced.
            PermissionsRespected = true,
            ExactStepCount = exactStepCount,
            StructurePreserved = structurePreserved,
            NoReplan = noReplan
        };

        var result = new TestRunResult
        {
            TestName = testName,
            CardId = cardId,
            StepsPassed = stepsPassed,
            TotalSteps = totalSteps,
            Score = score,
            Passed = passed,
            FailedStep = failedStep,
            FailureReason = failureReason,
            CodeFile = codeFile,
            WrittenTests = writtenTests,
            Machine = machine,
            WeaverVersion = weaverVersion,
            RunAt = DateTimeOffset.UtcNow,
            ExpectedSteps = benchmark?.ExpectedSteps,
            PlannedSteps = plannedSteps,
            Gates = gates,
            Model = model
        };
        result.PerfectPass = result.Passed && result.Gates.AllTrue;
        return result;
    }

    /// <summary>
    /// Score, then run the formattingClean gate's oracle (the one gate that needs
    /// async I/O — a shell-out to a formatter). Everything else is identical to Score.
    /// </summary>
    public static async Task<TestRunResult> ScoreAsync(
        string testName,
        string? cardId,
        IReadOnlyList<object> steps,
        AgentPlan? plan,
        bool complete,
        IReadOnlyList<string> filesEdited,
        EnvironmentMetadata machine,
        string weaverVersion,
        string projectRoot,
        BenchmarkManifest? benchmark = null,
        ModelInfo? model = null)
    {
        var result = Score(testName, cardId, steps, plan, complete, filesEdited, machine, weaverVersion, benchmark, model);
        result.Gates.FormattingClean = await FormattingGate.CheckAsync(projectRoot, filesEdited, benchmark?.Formatting);
        result.PerfectPass = result.Passed && result.Gates.AllTrue;
        return result;
    }

    /// <summary>Matches a project-relative path against a glob pattern supporting `*` and `**`.</summary>
    static bool GlobMatch(string path, string pattern)
    {
        var normalizedPath = path.Replace('\\', '/').TrimStart('/');
        var normalizedPattern = pattern.Replace('\\', '/').TrimStart('/');

        var regexPattern = "^" + Regex.Escape(normalizedPattern)
            .Replace(@"\*\*", "")
            .Replace(@"\*", "[^/]*")
            .Replace("", ".*")
            + "$";
        return Regex.IsMatch(normalizedPath, regexPattern, RegexOptions.IgnoreCase);
    }

    static bool LooksLikeTestFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var lower = path.Replace('\\', '/').ToLowerInvariant();
        var name = lower.Substring(lower.LastIndexOf('/') + 1);
        return name.Contains("test") || name.Contains(".spec.") || name.Contains("_spec")
               || lower.Contains("/tests/") || lower.Contains("/test/") || lower.Contains("/__tests__/");
    }

    static string? Str(Dictionary<string, object?> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v == null) return null;
        if (v is JsonElement je)
            return je.ValueKind == JsonValueKind.String ? je.GetString() : je.ToString();
        return v.ToString();
    }
}
