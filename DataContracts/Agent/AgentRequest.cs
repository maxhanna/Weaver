
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
}
