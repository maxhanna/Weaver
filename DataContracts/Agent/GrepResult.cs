namespace WeaverBackend;

public class GrepResult
{
    public string Status { get; set; } = "running";
    public string? Query { get; set; }
    public string? Path { get; set; }
    public string? Error { get; set; }
    public List<GrepMatch> Matches { get; set; } = new();
    public int TotalMatches => Matches.Count;
}

public class GrepMatch
{
    public string FilePath { get; set; } = "";
    public int LineNumber { get; set; }
    public string Content { get; set; } = "";
    public int ContextStartLine { get; set; }
    public string? SurroundingContext { get; set; }
}
