namespace MaestroBackend;

public class AgentRequest
{
    public string Prompt { get; set; } = "";
    public string Project { get; set; } = "";
    public List<string> Files { get; set; } = new();
    public int? MaxIterations { get; set; }
    public int? MaxStepsPerBatch { get; set; }
}

public class ApplyEditsRequest
{
    public string Project { get; set; } = "";
    public List<EditAction> Edits { get; set; } = new();
    public List<CommandAction> Commands { get; set; } = new();
}

public class EditAction
{
    public string Path { get; set; } = "";
    public string OldString { get; set; } = "";
    public string NewString { get; set; } = "";
}

public class CommandAction { public string Command { get; set; } = ""; }
