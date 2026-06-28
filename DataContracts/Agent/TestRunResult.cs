namespace Weaver;

/// <summary>
/// The result of running a benchmark "test card" through the orchestrator.
/// Emitted to the frontend (as a "test_result" SSE event) and uploaded to
/// BugHosted so model / Weaver-version / hardware combinations can be compared.
///
/// The core metric is <see cref="Score"/> — "how far through the card's steps the
/// agent got before breaking", expressed 0–100.
/// </summary>
public class TestRunResult
{
    public string TestName { get; set; } = "";
    public string? CardId { get; set; }

    public int StepsPassed { get; set; }
    public int TotalSteps { get; set; }
    /// <summary>0–100, progress through the card's steps.</summary>
    public int Score { get; set; }

    /// <summary>True only when every step completed without a halt.</summary>
    public bool Passed { get; set; }
    public string? FailedStep { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>The primary file the test produced/edited, if any.</summary>
    public string? CodeFile { get; set; }
    /// <summary>Files that look like test files the agent wrote.</summary>
    public List<string> WrittenTests { get; set; } = new();

    public EnvironmentMetadata Machine { get; set; } = new();
    public string WeaverVersion { get; set; } = "";

    public DateTimeOffset RunAt { get; set; } = DateTimeOffset.UtcNow;
}
