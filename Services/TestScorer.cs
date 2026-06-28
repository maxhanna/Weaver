using System.Text.Json;

namespace Weaver;

/// <summary>
/// Turns the raw orchestrator step results into a <see cref="TestRunResult"/>.
///
/// The orchestrator already runs plan steps one at a time and halts on the first
/// irrecoverable failure (emitting a "plan_halted" marker). So "how far the agent
/// got before breaking" falls straight out of the step results — this class just
/// counts it and packages the machine + version metadata.
/// </summary>
public static class TestScorer
{
    // Step statuses that count as a completed step.
    static readonly HashSet<string> PassedStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "done", "created", "modified" };

    public static TestRunResult Score(
        string testName,
        string? cardId,
        IReadOnlyList<object> steps,
        AgentPlan? plan,
        bool complete,
        IReadOnlyList<string> filesEdited,
        EnvironmentMetadata machine,
        string weaverVersion)
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

        return new TestRunResult
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
            RunAt = DateTimeOffset.UtcNow
        };
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
