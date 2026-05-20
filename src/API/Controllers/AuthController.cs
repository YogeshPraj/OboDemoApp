using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using CMSPDemo.API.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Client;
using Microsoft.IdentityModel.Tokens;

namespace CMSPDemo.API.Controllers;

/// <summary>
/// Server-side OIDC sign-in (Emerald/WebAuth pattern).
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IConfiguration       _cfg;
    private readonly IPendingSignInStore  _pending;
    private readonly ISessionStore        _sessions;
    private readonly ILogger<AuthController> _log;

    public AuthController(
        IConfiguration cfg,
        IPendingSignInStore pending,
        ISessionStore sessions,
        ILogger<AuthController> log)
    {
        _cfg      = cfg;
        _pending  = pending;
        _sessions = sessions;
        _log      = log;
    }

    /// <summary>
    /// Build the Entra authorize URL (PKCE S256) and either 302-redirect
    /// the browser or return the URL as JSON for popup-driven flows.
    /// </summary>
    [HttpGet("sign-in")]
    [AllowAnonymous]
    public IActionResult SignIn([FromQuery] string? returnUrl, [FromQuery] string? mode)
    {
        var tenantId    = _cfg["AzureAd:TenantId"] ?? "common";
        var clientId    = _cfg["AzureAd:ClientId"]
            ?? throw new InvalidOperationException("AzureAd:ClientId not configured.");
        var audience    = _cfg["AzureAd:Audience"] ?? $"api://{clientId}";
        var redirectUri = _cfg["AzureAd:OidcRedirectUri"]
            ?? $"{Request.Scheme}://{Request.Host}/api/auth/callback";

        var state         = RandomBase64Url(32);
        var nonce         = RandomBase64Url(32);
        var codeVerifier  = RandomBase64Url(32);
        var codeChallenge = Sha256Base64Url(codeVerifier);

        _pending.Add(new PendingSignIn(state, codeVerifier, returnUrl ?? "/", nonce, DateTimeOffset.UtcNow));

        // {AadAppIdUri}/.default — same scope Emerald uses for code redemption.
        var scope = $"openid profile offline_access {audience}/.default";
        var url =
            $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/authorize" +
            $"?client_id={Uri.EscapeDataString(clientId)}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(scope)}" +
            $"&state={state}" +
            $"&nonce={nonce}" +
            $"&code_challenge={codeChallenge}" +
            $"&code_challenge_method=S256" +
            $"&prompt=select_account";

        return string.Equals(mode, "json", StringComparison.OrdinalIgnoreCase)
            ? Ok(new { url })
            : Redirect(url);
    }

    /// <summary>
    /// Entra redirect target. Redeems the auth code into a server-side session and
    /// bounces the browser to the SPA returnUrl with the CMSP_SESSION cookie set.
    /// </summary>
    [HttpGet("callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery] string? error_description)
    {
        if (!string.IsNullOrEmpty(error))
        {
            _log.LogError("Auth callback error: {Error} — {Description}", error, error_description);
            return BadRequest($"Sign-in failed: {error} — {error_description}");
        }
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return BadRequest("Missing code or state.");

        var p = _pending.PopByState(state);
        if (p is null) return BadRequest("Invalid or expired state.");

        var tenantId     = _cfg["AzureAd:TenantId"] ?? "common";
        var clientId     = _cfg["AzureAd:ClientId"]!;
        var clientSecret = _cfg["AzureAd:ClientSecret"]
            ?? throw new InvalidOperationException("AzureAd:ClientSecret not configured (set with dotnet user-secrets).");
        var audience     = _cfg["AzureAd:Audience"] ?? $"api://{clientId}";
        var redirectUri  = _cfg["AzureAd:OidcRedirectUri"]
            ?? $"{Request.Scheme}://{Request.Host}/api/auth/callback";

        var cca = ConfidentialClientApplicationBuilder.Create(clientId)
            .WithClientSecret(clientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{tenantId}/v2.0", true)
            .WithRedirectUri(redirectUri)
            .Build();

        try
        {
            var result = await cca
                .AcquireTokenByAuthorizationCode(new[] { $"{audience}/.default" }, code)
                .WithPkceCodeVerifier(p.CodeVerifier)
                .ExecuteAsync();

            var claims    = ParseJwtClaims(result.IdToken);
            var sessionId = RandomBase64Url(32);

            _sessions.Add(new UserSession
            {
                SessionId   = sessionId,
                IdToken     = result.IdToken,
                AccessToken = result.AccessToken,
                ExpiresOn   = result.ExpiresOn,
                Oid         = Pick(claims, "oid", "sub") ?? "",
                Upn         = Pick(claims, "preferred_username", "upn", "email") ?? "",
                Tid         = Pick(claims, "tid") ?? "",
                Puid        = Pick(claims, "puid"),
                Name        = Pick(claims, "name") ?? "",
            });

            Response.Cookies.Append("CMSP_SESSION", sessionId, new CookieOptions
            {
                HttpOnly = true,
                Secure   = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                MaxAge   = TimeSpan.FromHours(8),
                Path     = "/",
            });

            _log.LogInformation("Session created for {Upn} (oid={Oid}, tid={Tid})",
                Pick(claims, "preferred_username") ?? "?", Pick(claims, "oid"), Pick(claims, "tid"));

            var allowedOrigins = _cfg.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
            var spaOrigin      = allowedOrigins.FirstOrDefault() ?? "http://localhost:5173";
            var returnTo       = p.ReturnUrl?.StartsWith('/') == true
                ? $"{spaOrigin}{p.ReturnUrl}"
                : (p.ReturnUrl ?? spaOrigin);
            return Redirect(returnTo);
        }
        catch (MsalException ex)
        {
            _log.LogError(ex, "Auth code redemption failed.");
            return BadRequest($"Sign-in failed: {ex.Message}");
        }
    }

    /// <summary>Returns the caller's identity — used by the SPA on startup to detect sign-in state.</summary>
    [HttpGet("me")]
    [Authorize(Policy = BffAuthPolicies.UserToken)]
    public IActionResult Me()
    {
        return Ok(new
        {
            authMode = User.FindFirst(SessionAuthenticationHandler.AuthModeClaimType)?.Value ?? "bearer",
            oid      = User.FindFirst("oid")?.Value,
            upn      = User.FindFirst("preferred_username")?.Value ?? User.FindFirst("upn")?.Value,
            tid      = User.FindFirst("tid")?.Value,
            name     = User.FindFirst("name")?.Value,
            puid     = User.FindFirst("puid")?.Value,
        });
    }

    /// <summary>Drops the server-side session and clears the cookie.</summary>
    [HttpPost("sign-out")]
    [AllowAnonymous]
    public IActionResult SignOutEndpoint()
    {
        var sid = Request.Cookies["CMSP_SESSION"];
        if (!string.IsNullOrEmpty(sid)) _sessions.Remove(sid);

        Response.Cookies.Delete("CMSP_SESSION", new CookieOptions { Path = "/" });

        var tenantId  = _cfg["AzureAd:TenantId"] ?? "common";
        var allowed   = _cfg.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
        var spaOrigin = allowed.FirstOrDefault() ?? "http://localhost:5173";
        var entraLogout = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/logout" +
                          $"?post_logout_redirect_uri={Uri.EscapeDataString(spaOrigin)}";
        return Ok(new { signedOut = true, entraLogoutUrl = entraLogout });
    }

    // ─── helpers ──────────────────────────────────────────────────────────

    private static string RandomBase64Url(int byteLength)
        => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(byteLength));

    private static string Sha256Base64Url(string verifier)
        => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static Dictionary<string, string?> ParseJwtClaims(string jwt)
    {
        var t   = new JwtSecurityTokenHandler().ReadJwtToken(jwt);
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in t.Claims) dict[c.Type] = c.Value;
        return dict;
    }

    private static string? Pick(Dictionary<string, string?> d, params string[] keys)
    {
        foreach (var k in keys)
            if (d.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v)) return v;
        return null;
    }
}
