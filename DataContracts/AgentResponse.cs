using System.Text.Json.Serialization;

namespace MaestroBackend;

public class AgentResponse
{
    public string Thinking { get; set; } = "";
    public string Summary { get; set; } = "";
    public bool Complete { get; set; }
    public List<AgentStep> Steps { get; set; } = new();
    public string Phase { get; set; } = "";
    public string Synthesis { get; set; } = "";
}

public class ClarificationCheck
{
    public bool NeedsClarification { get; set; }
    public string Question { get; set; } = "";
}

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
