namespace Weaver;

public class AgentResponse
{
    public string Thinking { get; set; } = "";
    public string Summary { get; set; } = "";
    public bool Complete { get; set; }
    public List<AgentStep> Steps { get; set; } = new();
}