
public class AgentRequest
{
    public string Prompt { get; set; } = "";
    public string Project { get; set; } = "";
    public List<string> Files { get; set; } = new();
    public int? MaxIterations { get; set; }
    public int? MaxStepsPerBatch { get; set; }
    public string? SteeringContext { get; set; }
    public bool SelfImproving { get; set; }
    public bool IsDecomposing { get; set; }
    public string? CardId { get; set; } 
    public bool CreateTests { get; set; }

    /// <summary>Indices of plan steps already completed (0-based).</summary>
    public List<int>? CompletedStepIndices { get; set; }
}

public class ExistingPlanItem
{
    public int Index { get; set; }
    public string File { get; set; } = "";
    public string Change { get; set; } = "";
    public bool Done { get; set; }
    public string OldString { get; set; } = "";
    public string NewString { get; set; } = "";
}
