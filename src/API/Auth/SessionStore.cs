using System.Collections.Concurrent;

namespace CMSPDemo.API.Auth;

/// <summary>
/// Snapshot of a server-OIDC-authenticated user.
/// The browser only holds an opaque session-id cookie; this object — and the
/// user's tokens — never leave the BFF.
/// </summary>
public sealed record UserSession
{
    public required string SessionId    { get; init; }
    public required string IdToken      { get; init; }
    /// <summary>The access token from auth-code redemption. Audience = BFF.
    /// Used as the OBO user assertion when calling downstream APIs.</summary>
    public required string AccessToken  { get; set; }
    public required DateTimeOffset ExpiresOn { get; set; }
    public required string Oid          { get; init; }
    public required string Upn          { get; init; }
    public required string Tid          { get; init; }
    public          string? Puid        { get; init; }
    public required string Name         { get; init; }
    public          DateTimeOffset CreatedAt  { get; init; } = DateTimeOffset.UtcNow;
    public          DateTimeOffset LastSeenAt { get; set; }  = DateTimeOffset.UtcNow;
}

public interface ISessionStore
{
    void Add(UserSession session);
    UserSession? Get(string sessionId);
    void Remove(string sessionId);
    int ActiveCount { get; }
}

public sealed class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, UserSession> _sessions = new();
    private readonly TimeSpan _idleTimeout = TimeSpan.FromHours(8);
    private DateTimeOffset _lastCleanup = DateTimeOffset.UtcNow;

    public int ActiveCount => _sessions.Count;

    public void Add(UserSession session)
    {
        _sessions[session.SessionId] = session;
        TryCleanup();
    }

    public UserSession? Get(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var s)) return null;
        if (DateTimeOffset.UtcNow - s.LastSeenAt > _idleTimeout)
        {
            _sessions.TryRemove(sessionId, out _);
            return null;
        }
        s.LastSeenAt = DateTimeOffset.UtcNow;
        return s;
    }

    public void Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);

    private void TryCleanup()
    {
        if (DateTimeOffset.UtcNow - _lastCleanup < TimeSpan.FromMinutes(15)) return;
        _lastCleanup = DateTimeOffset.UtcNow;
        var cutoff = DateTimeOffset.UtcNow - _idleTimeout;
        foreach (var kv in _sessions)
            if (kv.Value.LastSeenAt < cutoff)
                _sessions.TryRemove(kv.Key, out _);
    }
}
