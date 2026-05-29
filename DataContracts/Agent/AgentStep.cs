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
