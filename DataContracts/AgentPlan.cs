namespace MaestroBackend;

public class AgentPlan
{
    public string Thinking { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<PlanStep> Plan { get; set; } = new();
}

public class PlanStep
{
    public string File { get; set; } = string.Empty;
    public string Change { get; set; } = string.Empty;
    public int Priority { get; set; }
    public int? LineFrom { get; set; }
    public int? LineTo { get; set; }
}

public class MinimalEditDto
{
    public string Path { get; set; } = "";
    public string OldString { get; set; } = "";
    public string NewString { get; set; } = "";
}

public class MinimalEditsEnvelope
{
    public List<MinimalEditDto> Edits { get; set; } = new();
}
