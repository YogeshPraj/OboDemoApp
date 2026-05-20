using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CMSPDemo.API.Auth;

public class SessionAuthenticationOptions : AuthenticationSchemeOptions
{
    public string CookieName { get; set; } = "CMSP_SESSION";
}

/// <summary>
/// Authenticates a request based on the <c>CMSP_SESSION</c> cookie.
/// The cookie holds an opaque session id; the user's tokens are stored
/// server-side in <see cref="ISessionStore"/>.
/// </summary>
public sealed class SessionAuthenticationHandler
    : AuthenticationHandler<SessionAuthenticationOptions>
{
    public const string SchemeName = "Session";
    public const string AuthModeClaimType  = "authmode";
    public const string AuthModeClaimValue = "session";
    public const string SessionIdClaimType = "cmsp_session_id";

    private readonly ISessionStore _store;

    public SessionAuthenticationHandler(
        IOptionsMonitor<SessionAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder     encoder,
        ISessionStore  store)
        : base(options, logger, encoder)
    {
        _store = store;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var cookie = Context.Request.Cookies[Options.CookieName];
        if (string.IsNullOrEmpty(cookie))
            return Task.FromResult(AuthenticateResult.NoResult());

        var session = _store.Get(cookie);
        if (session is null)
            return Task.FromResult(AuthenticateResult.Fail("Session not found or expired."));

        var claims = new List<Claim>
        {
            new("oid",                session.Oid),
            new("tid",                session.Tid),
            new("name",               session.Name),
            new("preferred_username", session.Upn),
            new("scp",                "access_as_user"),   // mirrors the OBO-token shape
            new(ClaimTypes.NameIdentifier, session.Oid),
            new(ClaimTypes.Name,           session.Name),
            new(ClaimTypes.Email,          session.Upn),
            new(AuthModeClaimType,  AuthModeClaimValue),
            new(SessionIdClaimType, session.SessionId),
        };
        if (!string.IsNullOrEmpty(session.Puid))
            claims.Add(new Claim("puid", session.Puid));

        var identity  = new ClaimsIdentity(claims, Scheme.Name, "preferred_username", null);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
