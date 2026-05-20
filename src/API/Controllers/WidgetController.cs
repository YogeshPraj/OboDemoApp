using CMSPDemo.API.Auth;
using CMSPDemo.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web;

namespace CMSPDemo.API.Controllers;

/// <summary>
/// Phase 3 — widget token handoff.
/// Returns an MCSApp-scoped access token (T_mcs) for the Omnichannel widget.
/// Works in both auth modes: bearer (Microsoft.Identity.Web OBO) or session
/// (SessionTokenAcquirer using the stored access token as the OBO assertion).
/// </summary>
[ApiController]
[Route("api/widget")]
[Authorize(Policy = BffAuthPolicies.UserToken)]
public sealed class WidgetController : ControllerBase
{
    private readonly ITokenAcquisition       _msalTokens;
    private readonly ISessionTokenAcquirer   _sessionTokens;
    private readonly IConfiguration          _cfg;
    private readonly ILogger<WidgetController> _log;

    public WidgetController(
        ITokenAcquisition msalTokens,
        ISessionTokenAcquirer sessionTokens,
        IConfiguration cfg,
        ILogger<WidgetController> log)
    {
        _msalTokens     = msalTokens;
        _sessionTokens  = sessionTokens;
        _cfg            = cfg;
        _log            = log;
    }

    public sealed record TokenResponse(string AccessToken, string TokenType = "Bearer", DateTimeOffset? ExpiresOn = null);

    [HttpGet("mcs-token")]
    public async Task<IActionResult> McsToken(CancellationToken ct)
    {
        var mcsScope = _cfg["DownstreamApis:McsApp:Scope"];
        if (string.IsNullOrEmpty(mcsScope))
            return Problem(detail: "DownstreamApis:McsApp:Scope is not configured.", statusCode: 500);

        try
        {
            string token;
            var authMode = User.FindFirst(SessionAuthenticationHandler.AuthModeClaimType)?.Value;
            if (authMode == SessionAuthenticationHandler.AuthModeClaimValue)
            {
                var sid = User.FindFirst(SessionAuthenticationHandler.SessionIdClaimType)!.Value;
                token = await _sessionTokens.GetTokenForScopeAsync(sid, new[] { mcsScope }, ct);
            }
            else
            {
                token = await _msalTokens.GetAccessTokenForUserAsync(new[] { mcsScope });
            }

            _log.LogDebug("Widget token issued (authMode={Mode}, scope={Scope})", authMode ?? "bearer", mcsScope);
            return Ok(new TokenResponse(token));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to issue widget MCS token");
            return Problem(detail: ex.Message, statusCode: 500);
        }
    }
}
