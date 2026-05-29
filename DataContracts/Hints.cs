public class GlobalHintsStore
{
    public Dictionary<string, ProjectHints> Projects { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class ProjectHints
{
    public List<KeywordHint> Hints { get; set; } = new();
    public List<LearnedAssociation> AutoLearned { get; set; } = new();
}

public class KeywordHint
{
    public List<string> Keywords { get; set; } = new();
    public List<string> Files { get; set; } = new();
}

public class LearnedAssociation
{
    public string Keyword { get; set; } = "";
    public string File { get; set; } = "";
    public int Score { get; set; }
    public string LastSeen { get; set; } = "";
}