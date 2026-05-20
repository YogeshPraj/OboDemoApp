using CMSPDemo.API.Auth;
using CMSPDemo.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web;

namespace CMSPDemo.API.Controllers;

/// <summary>
/// /api/me — the BFF's "who am I" endpoint.
///
/// Calls Microsoft Graph /me on behalf of the signed-in user via OBO.
/// The BFF does NOT route this through PartnerAPI — Graph is a first-class
/// downstream service from the BFF.
///
/// Auth mode handling:
///  • Bearer JWT (MSAL on SPA)   → Microsoft.Identity.Web does the OBO using the inbound assertion.
///  • Session cookie (Server OIDC) → SessionTokenAcquirer does the OBO using the access token
///                                  stored in the session.
/// </summary>
[ApiController]
public sealed class MeController : ControllerBase
{
    private const string GraphMeUrl   = "https://graph.microsoft.com/v1.0/me";
    private const string GraphReadScope = "User.Read";

    private readonly IHttpClientFactory      _http;
    private readonly ITokenAcquisition       _msalTokens;
    private readonly ISessionTokenAcquirer   _sessionTokens;
    private readonly ILogger<MeController>   _log;

    public MeController(
        IHttpClientFactory http,
        ITokenAcquisition msalTokens,
        ISessionTokenAcquirer sessionTokens,
        ILogger<MeController> log)
    {
        _http          = http;
        _msalTokens    = msalTokens;
        _sessionTokens = sessionTokens;
        _log           = log;
    }

    [HttpGet("/api/me")]
    [Authorize(Policy = BffAuthPolicies.UserToken)]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var authMode = User.FindFirst(SessionAuthenticationHandler.AuthModeClaimType)?.Value ?? "bearer";

        try
        {
            // 1. OBO-acquire a Graph token, routing through the correct token acquirer.
            string graphToken;
            if (authMode == SessionAuthenticationHandler.AuthModeClaimValue)
            {
                var sid = User.FindFirst(SessionAuthenticationHandler.SessionIdClaimType)!.Value;
                graphToken = await _sessionTokens.GetTokenForScopeAsync(sid, new[] { GraphReadScope }, ct);
            }
            else
            {
                graphToken = await _msalTokens.GetAccessTokenForUserAsync(new[] { GraphReadScope });
            }

            // 2. Call Graph /me with that token.
            using var client = _http.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", graphToken);

            using var resp = await client.GetAsync(GraphMeUrl, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Graph /me returned {Status}: {Body}", (int)resp.StatusCode, body);
                return StatusCode((int)resp.StatusCode, new
                {
                    error    = "Graph /me failed",
                    authMode,
                    upstream = new { status = (int)resp.StatusCode, body },
                });
            }

            // 3. Return Graph's response augmented with BFF-side metadata.
            return Content(
                BuildResponseJson(authMode, body),
                "application/json",
                System.Text.Encoding.UTF8);
        }
        catch (Microsoft.Identity.Client.MsalUiRequiredException ex)
        {
            _log.LogWarning(ex, "Graph OBO requires interactive consent for User.Read");
            return Problem(
                detail:     "OBO to Graph requires consent for User.Read. The BFF app registration must " +
                            "have User.Read declared as a delegated permission, and the user must have consented.",
                statusCode: 403);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "MeController failed");
            return Problem(detail: ex.Message, statusCode: 500);
        }
    }

    /// <summary>Anonymous health probe.</summary>
    [HttpGet("/api/health")]
    [AllowAnonymous]
    public IActionResult Health()
        => Ok(new { service = "BFF API", ok = true, ts = DateTimeOffset.UtcNow });

    // ─── helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Merge the Graph /me payload with the local auth-mode marker without
    /// re-parsing/re-serializing the whole JSON tree (preserves Graph's exact shape).
    /// </summary>
    private static string BuildResponseJson(string authMode, string graphJson)
    {
        var trimmed = graphJson.TrimStart();
        if (!trimmed.StartsWith('{')) return graphJson;   // not an object — return verbatim

        // Insert "flavor" + "authMode" right after the opening brace.
        var prefix = $"{{\"flavor\":\"bff\",\"authMode\":\"{authMode}\",\"graph\":";
        return prefix + graphJson + "}";
    }
}

/// <summary>Used by AuthController.Me (lightweight identity for the SPA startup probe).</summary>
internal static class CallerSummary
{
    public static object Build(System.Security.Claims.ClaimsPrincipal user, string flavor)
    {
        string? Get(params string[] types) => types
            .Select(t => user.FindFirst(t)?.Value)
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));

        var scopes = (Get("scp", "http://schemas.microsoft.com/identity/claims/scope") ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return new
        {
            flavor,
            authMode          = Get(SessionAuthenticationHandler.AuthModeClaimType) ?? "bearer",
            name              = Get("name", System.Security.Claims.ClaimTypes.Name),
            userPrincipalName = Get("preferred_username", "upn"),
            objectId          = Get("oid", "http://schemas.microsoft.com/identity/claims/objectidentifier"),
            tenantId          = Get("tid", "http://schemas.microsoft.com/identity/claims/tenantid"),
            appId             = Get("appid", "azp"),
            scopes,
            claims            = user.Claims.Select(c => new { c.Type, c.Value }).ToArray()
        };
    }
}
