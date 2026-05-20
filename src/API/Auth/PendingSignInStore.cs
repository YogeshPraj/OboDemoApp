using System.Collections.Concurrent;

namespace CMSPDemo.API.Auth;

/// <summary>
/// In-flight OIDC sign-in: state, PKCE verifier, and returnUrl, captured at /api/auth/sign-in
/// and looked up at /api/auth/callback.
/// </summary>
public sealed record PendingSignIn(
    string         State,
    string         CodeVerifier,
    string         ReturnUrl,
    string         Nonce,
    DateTimeOffset CreatedAt);

public interface IPendingSignInStore
{
    void Add(PendingSignIn pending);
    PendingSignIn? PopByState(string state);
}

public sealed class InMemoryPendingSignInStore : IPendingSignInStore
{
    private readonly ConcurrentDictionary<string, PendingSignIn> _pending = new();
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(10);

    public void Add(PendingSignIn pending)
    {
        Cleanup();
        _pending[pending.State] = pending;
    }

    public PendingSignIn? PopByState(string state)
    {
        Cleanup();
        if (!_pending.TryRemove(state, out var p)) return null;
        return DateTimeOffset.UtcNow - p.CreatedAt > _ttl ? null : p;
    }

    private void Cleanup()
    {
        var cutoff = DateTimeOffset.UtcNow - _ttl;
        foreach (var kv in _pending)
            if (kv.Value.CreatedAt < cutoff)
                _pending.TryRemove(kv.Key, out _);
    }
}
