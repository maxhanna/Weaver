public class PendingContextReview
{
    public string Id { get; set; } = "";
    public List<string> Files { get; set; } = new();
    public DateTime CreatedUtc { get; set; }
    public TaskCompletionSource<List<string>> Answer { get; set; } = new();
}
