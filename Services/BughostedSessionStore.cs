using System.Collections.Concurrent;

namespace MaestroBackend.Services;

public interface IBughostedSessionStore
{
    void Set(BughostedSession session);
    bool TryGet(string clientId, out BughostedSession session);
    bool Remove(string clientId);
}

public sealed class BughostedSessionStore : IBughostedSessionStore
{
    private readonly ConcurrentDictionary<string, BughostedSession> _sessions = new(StringComparer.Ordinal);

    public void Set(BughostedSession session)
    {
        if (string.IsNullOrWhiteSpace(session.ClientId))
            throw new ArgumentException("Session client id is required.", nameof(session));

        _sessions[session.ClientId] = session;
    }

    public bool TryGet(string clientId, out BughostedSession session)
    {
        return _sessions.TryGetValue(clientId, out session!);
    }

    public bool Remove(string clientId)
    {
        return _sessions.TryRemove(clientId, out _);
    }
}
