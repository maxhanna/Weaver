namespace Weaver;

public class AgentStep
{
    public int Index { get; set; }
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Path { get; set; }
    public string? OldString { get; set; }
    public string? NewString { get; set; }
    public string? Command { get; set; }
    public string? Pattern { get; set; }
    public string? Url { get; set; }
    public string? Query { get; set; }
    public string? ToPath { get; set; }
    public bool? Complete { get; set; }
    public string? Prompt { get; set; }
}



/// <summary>
/// Thrown when a plan step exhausts all retries and replan cycles.
/// Caught by ExecutePlan to stop further step execution — the task
/// cannot proceed because a prerequisite step failed.
/// </summary>
public class StepFatalException : Exception
{
    public string FailedFilePath { get; }
    public string FailedChangeDescription { get; }
    public string FailureContext { get; }

    public StepFatalException(string message, string filePath, string changeDesc, string failureContext)
        : base(message)
    {
        FailedFilePath = filePath;
        FailedChangeDescription = changeDesc;
        FailureContext = failureContext;
    }
}


public sealed class StepExplorationResult
{
    public PlanStep EnrichedStep { get; init; } = new();
    public string ExplorationContext { get; init; } = "";
    public List<string> FilesRead { get; init; } = new();
    public string RefinedChange { get; init; } = "";
    public string? TargetSymbol { get; init; }
    public string? EstimatedLineRange { get; init; }
    public int Confidence { get; init; }
    public int RoundsCompleted { get; init; }
    public string? LowConfidenceWarning { get; init; }
}

public sealed class StepExplorationResponse
{
    public bool Ready { get; init; }
    public List<string> FilesToRead { get; init; } = new();
    public string? RefinedChange { get; init; }
    public string? TargetSymbol { get; init; }
    public string? LineRange { get; init; }
    public int Confidence { get; init; }
}
