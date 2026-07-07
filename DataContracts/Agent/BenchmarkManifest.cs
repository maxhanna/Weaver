namespace Weaver;

/// <summary>
/// Optional benchmark configuration carried on a test card. Makes the perfect-pass
/// gates in <see cref="TestGateResults"/> decidable — without it, gates that need a
/// card-authored expectation (<see cref="TestGateResults.ExactStepCount"/>,
/// <see cref="TestGateResults.StructurePreserved"/>) report null (unmeasured).
/// </summary>
public class BenchmarkManifest
{
    /// <summary>How many plan steps this card expects. Null if the card doesn't pin it.</summary>
    public int? ExpectedSteps { get; set; }

    /// <summary>Project-root-relative globs (supporting `*` and `**`) the agent may create/modify.</summary>
    public List<string> AllowedPaths { get; set; } = new();

    public BenchmarkFormatting? Formatting { get; set; }

    /// <summary>Repeat count for determinism measurement (Phase 4a leaderboard aggregates a rate).</summary>
    public int Runs { get; set; } = 1;
}

/// <summary>Formatting oracle configuration for the <c>formattingClean</c> gate.</summary>
public class BenchmarkFormatting
{
    /// <summary>"formatter" (run a check command per extension), "golden" (diff against a fixture — not yet implemented), or "none".</summary>
    public string Mode { get; set; } = "none";

    /// <summary>File extension (no dot, e.g. "cs") to check-mode formatter command. "{file}" is replaced with the full path.</summary>
    public Dictionary<string, string> Commands { get; set; } = new();
}
