namespace Weaver;

/// <summary>
/// The five perfect-pass gates (decided 2026-07-06). Each is a binary disqualifier
/// independent of the 0-100 progress score: <c>null</c> means the gate could not be
/// evaluated (e.g. no <see cref="BenchmarkManifest"/> was supplied) and counts as
/// *not perfect* — see docs/test-benchmark-bughosted-contract.md.
/// </summary>
public class TestGateResults
{
    /// <summary>Every edited file passes the card's formatting oracle. Null when no formatting config exists.</summary>
    public bool? FormattingClean { get; set; }

    /// <summary>No file was created/modified/deleted outside the card's allowedPaths. Null when the card has no allowedPaths.</summary>
    public bool? StructurePreserved { get; set; }

    /// <summary>Every executed command went through the terminal approval flow. Structurally guaranteed true by the current step executor.</summary>
    public bool? PermissionsRespected { get; set; }

    /// <summary>The plan's step count matched the card's expectedSteps exactly, no more, no less. Null when the card has no expectedSteps.</summary>
    public bool? ExactStepCount { get; set; }

    /// <summary>No executed step originated from a replan or repair pass — the original plan succeeded first try.</summary>
    public bool? NoReplan { get; set; }

    /// <summary>True only when every gate above is explicitly true (not null, not false).</summary>
    public bool AllTrue =>
        FormattingClean == true && StructurePreserved == true && PermissionsRespected == true &&
        ExactStepCount == true && NoReplan == true;
}

/// <summary>Best-effort model identity captured alongside a benchmark run, for cross-model/hardware comparison.</summary>
public class ModelInfo
{
    public string? Name { get; set; }
    public string? Backend { get; set; }
    public double? Temperature { get; set; }
    public long? Seed { get; set; }
}
