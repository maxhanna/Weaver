using System.Collections.Concurrent;

namespace MaestroBackend.Services;

public interface IAgentPendingStore
{
    IEnumerable<PendingQuestion> GetQuestions();
    IEnumerable<PendingContextReview> GetContextReviews();
    void SetQuestion(PendingQuestion question);
    bool TryRemoveQuestion(string id, out PendingQuestion question);

    void SetContextReview(PendingContextReview review);
    bool TryRemoveContextReview(string id, out PendingContextReview review);
}

public sealed class AgentPendingStore : IAgentPendingStore
{
    private readonly ConcurrentDictionary<string, PendingQuestion> _pendingQuestions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingContextReview> _pendingContextReviews = new(StringComparer.Ordinal);

    public IEnumerable<PendingQuestion> GetQuestions() => _pendingQuestions.Values;
    public IEnumerable<PendingContextReview> GetContextReviews() => _pendingContextReviews.Values.ToArray();

    public void SetQuestion(PendingQuestion question)
    {
        _pendingQuestions[question.Id] = question;
    }

    public bool TryRemoveQuestion(string id, out PendingQuestion question)
    {
        return _pendingQuestions.TryRemove(id, out question!);
    }

    public void SetContextReview(PendingContextReview review)
    {
        _pendingContextReviews[review.Id] = review;
    }

    public bool TryRemoveContextReview(string id, out PendingContextReview review)
    {
        return _pendingContextReviews.TryRemove(id, out review!);
    }
}
