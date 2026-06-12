/// <summary>
/// One item in the structured plan the LLM produces during Phase 2.
/// </summary>
public class PlanItem
{
    public string File { get; set; } = "";
    public string Change { get; set; } = "";
    public int Priority { get; set; } = 1;
}

public class PlanItemDeserialized
{
    public string file { get; set; } = "";
    public string change { get; set; } = "";
    public int priority { get; set; } = 1;
}

public class AgentPlanDeserialized
{
    public string thinking { get; set; } = "";
    public string summary { get; set; } = "";
    public List<PlanItemDeserialized> plan { get; set; } = new();
}

/// <summary>
/// The full plan envelope returned by the Phase-2 LLM call.
/// </summary> 
public class AgentPlan
{
    public string Thinking { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public int Score { get; set; } = 100;
    public List<PlanStep> Plan { get; set; } = new();
}

public class PlanStep
{
    public string File { get; set; } = string.Empty;
    public string Change { get; set; } = string.Empty;
    public int Priority { get; set; }
    public string OldString { get; set; } = string.Empty;
    public string NewString { get; set; } = string.Empty;
    public List<string>? ReferenceFiles { get; set; }
}