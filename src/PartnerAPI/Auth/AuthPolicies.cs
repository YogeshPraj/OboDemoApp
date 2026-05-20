using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace CMSPDemo.PartnerAPI.Auth;

/// <summary>
/// Authorization policies for PartnerAPI.
/// Callers are either the BFF (using its own app credentials → S2SOnly)
/// or the BFF forwarding an OBO-exchanged user token → OboOnly.
/// </summary>
public static class AuthPolicies
{
    public const string S2SOnly = "S2SOnly";
    public const string OboOnly = "OboOnly";

    public static void Register(AuthorizationOptions options)
    {
        // S2S: app-only token — idtyp=app, or roles present without scp.
        options.AddPolicy(S2SOnly, p => p
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx =>
            {
                var idtyp = ctx.User.FindFirst("idtyp")?.Value;
                if (string.Equals(idtyp, "app", StringComparison.OrdinalIgnoreCase))
                    return true;
                var hasScope = ctx.User.HasClaim(c => c.Type is "scp" or "http://schemas.microsoft.com/identity/claims/scope");
                var hasRoles = ctx.User.HasClaim(c => c.Type is "roles" or ClaimTypes.Role);
                return !hasScope && hasRoles;
            }));

        // OBO: delegated user token — must have scp and not idtyp=app.
        options.AddPolicy(OboOnly, p => p
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx =>
            {
                var idtyp = ctx.User.FindFirst("idtyp")?.Value;
                if (string.Equals(idtyp, "app", StringComparison.OrdinalIgnoreCase))
                    return false;
                return ctx.User.HasClaim(c => c.Type is "scp" or "http://schemas.microsoft.com/identity/claims/scope");
            }));
    }
}
