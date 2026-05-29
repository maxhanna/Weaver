public class QuestionAnswerRequest
{
    public string Id { get; set; } = "";
    public Dictionary<string, string> Answers { get; set; } = new();
}