using System.Collections.Concurrent;
using CMSPDemo.API.Auth;
using Microsoft.Identity.Client;

namespace CMSPDemo.API.Services;

/// <summary>
/// Acquires downstream tokens for a session-authenticated user.
/// The session's stored access token is used as the OBO user assertion.
/// </summary>
public interface ISessionTokenAcquirer
{
    Task<string> GetTokenForScopeAsync(string sessionId, string[] scopes, CancellationToken ct = default);
}

public sealed class SessionTokenAcquirer : ISessionTokenAcquirer
{
    private readonly ISessionStore _sessions;
    private readonly IConfiguration _cfg;
    private readonly ILogger<SessionTokenAcquirer> _log;

    /// <summary>Cache acquired tokens per (sessionId, scopeSetKey).</summary>
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresOn)> _cache = new();

    private IConfidentialClientApplication? _cca;

    public SessionTokenAcquirer(
        ISessionStore sessions,
        IConfiguration cfg,
        ILogger<SessionTokenAcquirer> log)
    {
        _sessions = sessions;
        _cfg      = cfg;
        _log      = log;
    }

    private IConfidentialClientApplication GetClient()
    {
        if (_cca is not null) return _cca;

        var tenantId     = _cfg["AzureAd:TenantId"]     ?? "common";
        var clientId     = _cfg["AzureAd:ClientId"]
            ?? throw new InvalidOperationException("AzureAd:ClientId not configured.");
        var clientSecret = _cfg["AzureAd:ClientSecret"]
            ?? throw new InvalidOperationException("AzureAd:ClientSecret not configured (set via user-secrets).");

        _cca = ConfidentialClientApplicationBuilder.Create(clientId)
            .WithClientSecret(clientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{tenantId}/v2.0", true)
            .Build();
        return _cca;
    }

    public async Task<string> GetTokenForScopeAsync(string sessionId, string[] scopes, CancellationToken ct = default)
    {
        var session = _sessions.Get(sessionId)
            ?? throw new InvalidOperationException("Session expired or not found.");

        var cacheKey = $"{sessionId}|{string.Join(' ', scopes)}";
        if (_cache.TryGetValue(cacheKey, out var entry)
            && entry.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            return entry.Token;
        }

        var client = GetClient();
        try
        {
            var result = await client
                .AcquireTokenOnBehalfOf(scopes, new UserAssertion(session.AccessToken))
                .ExecuteAsync(ct);

            _cache[cacheKey] = (result.AccessToken, result.ExpiresOn);
            _log.LogDebug("OBO ok for session {Sid} scope {Scope}", sessionId, string.Join(' ', scopes));
            return result.AccessToken;
        }
        catch (MsalException ex)
        {
            _log.LogError(ex, "OBO failed for session {Sid} scope {Scope}", sessionId, string.Join(' ', scopes));
            throw;
        }
    }
}
