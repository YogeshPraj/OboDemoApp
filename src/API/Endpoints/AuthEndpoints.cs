using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using CMSPDemo.API.Auth;
using Microsoft.Identity.Client;
using Microsoft.IdentityModel.Tokens;

namespace CMSPDemo.API.Endpoints;

/// <summary>
/// Server-side OIDC sign-in endpoints — the Emerald/WebAuth pattern.
///
/// /api/auth/sign-in   → builds the Entra authorize URL (with PKCE) and either
///                       302-redirects or returns it as JSON for popup mode.
/// /api/auth/callback  → Entra redirects here with ?code=…&state=… ; BFF
///                       redeems the code, parses the id_token, creates a
///                       session, sets the CMSP_SESSION cookie, and bounces
///                       the browser back to the SPA returnUrl.
/// /api/auth/me        → returns minimal identity info; SPA polls this to
///                       discover the user's signed-in state.
/// /api/auth/sign-out  → clears the session and the cookie.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapGet("/sign-in", SignIn)
            .WithName("AuthSignIn")
            .WithSummary("Build the Entra authorize URL (server-side OIDC). Anonymous.");

        group.MapGet("/callback", Callback)
            .WithName("AuthCallback")
            .WithSummary("Entra redirect target. Redeems the auth code into a server-side session.");

        group.MapGet("/me", Me)
            .RequireAuthorization()
            .WithName("AuthMe")
            .WithSummary("Returns the caller's identity (works in both bearer and session auth modes).");

        group.MapPost("/sign-out", SignOut)
            .WithName("AuthSignOut")
            .WithSummary("Drops the session and clears the cookie.");

        return app;
    }

    // ─── /api/auth/sign-in ────────────────────────────────────────────────────

    private static IResult SignIn(
        HttpContext ctx,
        IConfiguration cfg,
        IPendingSignInStore pending,
        string? returnUrl,
        string? mode)
    {
        var tenantId   = cfg["AzureAd:TenantId"] ?? "common";
        var clientId   = cfg["AzureAd:ClientId"]
            ?? throw new InvalidOperationException("AzureAd:ClientId not configured.");
        var audience   = cfg["AzureAd:Audience"] ?? $"api://{clientId}";
        var redirectUri = cfg["AzureAd:OidcRedirectUri"]
            ?? $"{ctx.Request.Scheme}://{ctx.Request.Host}/api/auth/callback";

        var state         = GenerateRandomString(32);
        var nonce         = GenerateRandomString(32);
        var codeVerifier  = GenerateRandomString(32);
        var codeChallenge = Sha256Base64Url(codeVerifier);

        pending.Add(new PendingSignIn(state, codeVerifier, returnUrl ?? "/", nonce, DateTimeOffset.UtcNow));

        // {AadAppIdUri}/.default — same scope Emerald uses for code redemption
        var scope = $"openid profile offline_access {audience}/.default";

        var authorizeUrl =
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
            ? Results.Ok(new { url = authorizeUrl })
            : Results.Redirect(authorizeUrl);
    }

    // ─── /api/auth/callback ───────────────────────────────────────────────────

    private static async Task<IResult> Callback(
        HttpContext ctx,
        IConfiguration cfg,
        IPendingSignInStore pending,
        ISessionStore sessions,
        ILoggerFactory loggerFactory,
        string? code,
        string? state,
        string? error,
        string? error_description)
    {
        var log = loggerFactory.CreateLogger("AuthCallback");

        if (!string.IsNullOrEmpty(error))
        {
            log.LogError("Auth callback error: {Error} — {Description}", error, error_description);
            return Results.BadRequest($"Sign-in failed: {error} — {error_description}");
        }
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return Results.BadRequest("Missing code or state.");

        var p = pending.PopByState(state);
        if (p is null) return Results.BadRequest("Invalid or expired state.");

        var tenantId     = cfg["AzureAd:TenantId"] ?? "common";
        var clientId     = cfg["AzureAd:ClientId"]!;
        var clientSecret = cfg["AzureAd:ClientSecret"]
            ?? throw new InvalidOperationException("AzureAd:ClientSecret not configured (set with dotnet user-secrets).");
        var audience     = cfg["AzureAd:Audience"] ?? $"api://{clientId}";
        var redirectUri  = cfg["AzureAd:OidcRedirectUri"]
            ?? $"{ctx.Request.Scheme}://{ctx.Request.Host}/api/auth/callback";

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
            var sessionId = GenerateRandomString(32);

            sessions.Add(new UserSession
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

            ctx.Response.Cookies.Append("CMSP_SESSION", sessionId, new CookieOptions
            {
                HttpOnly = true,
                Secure   = ctx.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                MaxAge   = TimeSpan.FromHours(8),
                Path     = "/",
            });

            log.LogInformation("Session created for {Upn} (oid={Oid}, tid={Tid})",
                Pick(claims, "preferred_username") ?? "?", Pick(claims, "oid"), Pick(claims, "tid"));

            // Bounce back to the SPA.
            var allowedOrigins = cfg.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
            var spaOrigin      = allowedOrigins.FirstOrDefault() ?? "http://localhost:5173";
            var returnTo       = p.ReturnUrl?.StartsWith('/') == true ? $"{spaOrigin}{p.ReturnUrl}" : (p.ReturnUrl ?? spaOrigin);
            return Results.Redirect(returnTo);
        }
        catch (MsalException ex)
        {
            log.LogError(ex, "Auth code redemption failed.");
            return Results.BadRequest($"Sign-in failed: {ex.Message}");
        }
    }

    // ─── /api/auth/me ─────────────────────────────────────────────────────────

    private static IResult Me(HttpContext ctx)
    {
        var u = ctx.User;
        if (u.Identity?.IsAuthenticated != true) return Results.Unauthorized();

        return Results.Ok(new
        {
            authMode = u.FindFirst(SessionAuthenticationHandler.AuthModeClaimType)?.Value ?? "bearer",
            oid      = u.FindFirst("oid")?.Value,
            upn      = u.FindFirst("preferred_username")?.Value ?? u.FindFirst("upn")?.Value,
            tid      = u.FindFirst("tid")?.Value,
            name     = u.FindFirst("name")?.Value,
            puid     = u.FindFirst("puid")?.Value,
        });
    }

    // ─── /api/auth/sign-out ───────────────────────────────────────────────────

    private static IResult SignOut(HttpContext ctx, ISessionStore sessions, IConfiguration cfg)
    {
        var sid = ctx.Request.Cookies["CMSP_SESSION"];
        if (!string.IsNullOrEmpty(sid)) sessions.Remove(sid);

        ctx.Response.Cookies.Delete("CMSP_SESSION", new CookieOptions { Path = "/" });

        var tenantId     = cfg["AzureAd:TenantId"] ?? "common";
        var allowed      = cfg.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
        var spaOrigin    = allowed.FirstOrDefault() ?? "http://localhost:5173";
        var entraLogout  = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/logout" +
                           $"?post_logout_redirect_uri={Uri.EscapeDataString(spaOrigin)}";
        return Results.Ok(new { signedOut = true, entraLogoutUrl = entraLogout });
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private static string GenerateRandomString(int byteLength)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteLength);
        return Base64UrlEncoder.Encode(bytes);
    }

    private static string Sha256Base64Url(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncoder.Encode(hash);
    }

    private static Dictionary<string, string?> ParseJwtClaims(string jwt)
    {
        var handler = new JwtSecurityTokenHandler();
        var t       = handler.ReadJwtToken(jwt);
        var dict    = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
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
