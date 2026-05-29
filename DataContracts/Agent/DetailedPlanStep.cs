public class DetailedPlanStep
{
    public int Index { get; set; }
    public string Description { get; set; } = "";
    public string TargetArea { get; set; } = "";
    public string ChangeType { get; set; } = "edit";
    public string? OldString { get; set; }
    public string? NewString { get; set; }
    public string? GeneratedCode { get; set; }
    public string? ReviewFeedback { get; set; }
}

public class DetailedPlanDeserialized
{
    public string? thinking { get; set; }
    public List<DetailedPlanStepDeserialized>? steps { get; set; }
}

public class DetailedPlanStepDeserialized
{
    public string description { get; set; } = "";
    public string targetArea { get; set; } = "";
    public string changeType { get; set; } = "edit";
}
