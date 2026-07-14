using System.Text.RegularExpressions;

namespace Weaver.Services;

public enum FailureCategory
{
    Unknown,
    Planning,
    Context,
    StrategySelection,
    AnchorResolution,
    EditApplication,
    StructuralValidation,
    BuildOrTest,
    BehavioralVerification,
    RecoveryExhausted
}

public sealed record NormalizedFailure(
    string Code, FailureCategory Category, string Summary, bool ReusableLesson);

public static class FailureTaxonomy
{
    public static NormalizedFailure Classify(string? reason, string? outcome = null)
    {
        var text = $"{outcome} {reason}".Trim();
        var lower = text.ToLowerInvariant();

        if (Matches(lower, "replan.*failed|recovery.*exhaust|failed after \\d+ attempts|stuck|stagnat|giving up"))
            return Failure("RECOVERY_EXHAUSTED", FailureCategory.RecoveryExhausted,
                "Recovery attempts were exhausted without a valid edit.");
        if (Matches(lower, "build failed|compil|lint|unit test|tests? failed|verification command|exit code"))
            return Failure("BUILD_TEST_FAILED", FailureCategory.BuildOrTest,
                "The resulting project failed an objective build or test check.");
        if (Matches(lower, "oldstring not found|anchor|found \\d+ times|ambiguous|wrong location|target line mismatch"))
            return Failure("ANCHOR_RESOLUTION_FAILED", FailureCategory.AnchorResolution,
                "The edit anchor was missing, ambiguous, or resolved to the wrong location.");
        if (Matches(lower, "missing context|discovery context|wrong file attached|symbol.*does not exist|not exist in.*context"))
            return Failure("CONTEXT_INSUFFICIENT", FailureCategory.Context,
                "The available context did not contain the required file or symbol.");
        if (Matches(lower, "wrong strategy|format c|insertion pattern|duplicate property|modify.*existing|strategy"))
            return Failure("EDIT_STRATEGY_MISMATCH", FailureCategory.StrategySelection,
                "The selected edit strategy did not match the requested code change.");
        if (Matches(lower, "syntax|brace|indent|structur|content deletion|too short|wipe|preserv|duplicate"))
            return Failure("STRUCTURAL_VALIDATION_FAILED", FailureCategory.StructuralValidation,
                "The proposed edit violated structural or preservation constraints.");
        if (Matches(lower, "plan|prerequisite|scope creep|contradict|redo anything"))
            return Failure("PLAN_INVALID", FailureCategory.Planning,
                "The plan contained an invalid, redundant, or incorrectly ordered step.");
        if (Matches(lower, "missing file|missing directory|content assertion|file exists|write|replace"))
            return Failure("EDIT_APPLICATION_FAILED", FailureCategory.EditApplication,
                "The expected artifact or content change was not applied.");
        if (Matches(lower, "behavior|http|endpoint|response|expected result|llm verify|score"))
            return Failure("BEHAVIOR_VERIFICATION_FAILED", FailureCategory.BehavioralVerification,
                "The result did not satisfy behavioral verification.");

        return new("UNKNOWN_FAILURE", FailureCategory.Unknown,
            NormalizeFallback(reason), ReusableLesson: false);
    }

    public static NormalizedFailure ForBenchmarkCheck(BenchmarkAcceptanceCheck check, string message)
    {
        if (check.Type == BenchmarkCheckType.CommandSucceeds)
            return Failure("BUILD_TEST_FAILED", FailureCategory.BuildOrTest,
                "The benchmark verification command did not succeed.");
        if (check.Type == BenchmarkCheckType.HttpResponse)
            return Failure("BEHAVIOR_VERIFICATION_FAILED", FailureCategory.BehavioralVerification,
                "The benchmark HTTP behavior did not match the expected response.");
        if (check.Category == "preservation")
            return Failure("PRESERVATION_FAILED", FailureCategory.StructuralValidation,
                "The edit changed or removed content that the scenario required preserving.");
        return Classify($"{check.Name}: {message}");
    }

    private static bool Matches(string text, string pattern) =>
        Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static NormalizedFailure Failure(string code, FailureCategory category, string summary) =>
        new(code, category, summary, ReusableLesson: true);

    private static string NormalizeFallback(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "An unclassified failure occurred.";
        var oneLine = Regex.Replace(reason, @"\s+", " ").Trim();
        return oneLine.Length <= 160 ? oneLine : oneLine[..157] + "...";
    }
}
