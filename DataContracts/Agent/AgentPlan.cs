using System.Text.Json.Serialization;

namespace Weaver;

/// <summary>
/// One item in the structured plan the LLM produces during Phase 2.
/// </summary>
public class PlanItem
{
    public string File { get; set; } = "";
    public string Change { get; set; } = "";
    public int Priority { get; set; } = 1;
}
public class MetaPlanSubPlan
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string ContextNote { get; set; } = "";
    public List<string> Files { get; set; } = new();
}

public class MetaPlanResult
{
    public string MetaThinking { get; set; } = "";
    public string MetaSummary { get; set; } = "";
    public int Complexity { get; set; }
    public List<MetaPlanSubPlan> SubPlans { get; set; } = new();
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
    /// <summary>Line number in the file where this edit targets (1-based).</summary>
    [JsonPropertyName("line")]
    public int LineNumber { get; set; }

    /// <summary>Meta-plan group label (sub-plan title) this step belongs to, if any.</summary>
    [JsonPropertyName("metaGroup")]
    public string? MetaGroup { get; set; }
}
