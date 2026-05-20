using CMSPDemo.API.Auth;
using CMSPDemo.API.Services;
using Microsoft.Identity.Web;

namespace CMSPDemo.API.Endpoints;

/// <summary>
/// Phase 3 — Widget token handoff.
///
/// The Omnichannel widget needs an MCSApp-scoped access token (T_mcs) to hand
/// over to Copilot Studio. This endpoint returns one without exposing any
/// long-lived credential to the browser.
///
///  • In **bearer mode** the SPA already has a token; this endpoint uses
///    Microsoft.Identity.Web's OBO with the incoming bearer assertion.
///  • In **session (server-OIDC) mode** the SPA has no token at all; this
///    endpoint OBO-exchanges the access token stored in the session.
/// </summary>
public static class WidgetTokenEndpoint
{
    public static IEndpointRouteBuilder MapWidgetTokenEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/widget/mcs-token", async (
            HttpContext ctx,
            ITokenAcquisition msalTokens,
            ISessionTokenAcquirer sessionTokens,
            IConfiguration cfg,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var log      = loggerFactory.CreateLogger("WidgetToken");
            var mcsScope = cfg["DownstreamApis:McsApp:Scope"];
            if (string.IsNullOrEmpty(mcsScope))
                return Results.Problem(
                    detail:     "DownstreamApis:McsApp:Scope is not configured.",
                    statusCode: 500);

            try
            {
                string token;
                DateTimeOffset? expiresOn = null;

                var authMode = ctx.User.FindFirst(SessionAuthenticationHandler.AuthModeClaimType)?.Value;
                if (authMode == SessionAuthenticationHandler.AuthModeClaimValue)
                {
                    var sid = ctx.User.FindFirst(SessionAuthenticationHandler.SessionIdClaimType)!.Value;
                    token = await sessionTokens.GetTokenForScopeAsync(sid, new[] { mcsScope }, ct);
                }
                else
                {
                    token = await msalTokens.GetAccessTokenForUserAsync(new[] { mcsScope });
                }

                log.LogDebug("Widget token issued (authMode={Mode}, scope={Scope})", authMode ?? "bearer", mcsScope);
                return Results.Ok(new
                {
                    accessToken = token,
                    tokenType   = "Bearer",
                    expiresOn   = expiresOn,
                });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to issue widget MCS token");
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }
        })
        .RequireAuthorization()
        .WithName("WidgetMcsToken")
        .WithSummary("Issues an MCSApp-scoped token (T_mcs) for the Omnichannel widget. Works in both auth modes.");

        return app;
    }
}
