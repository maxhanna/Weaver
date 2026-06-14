namespace Weaver;

public class PendingQuestion
{
    public string Id { get; set; } = "";
    public string Question { get; set; } = "";
    public List<QuestionField> Fields { get; set; } = new();
    public DateTime CreatedUtc { get; set; }
    public TaskCompletionSource<Dictionary<string, string>> Answer { get; set; } = new();
}
