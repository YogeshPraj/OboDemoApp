using Microsoft.AspNetCore.Authorization;

namespace CMSPDemo.API.Auth;

/// <summary>
/// The BFF only accepts delegated user tokens from the React client.
/// App-only callers (S2S) are rejected — they should call PartnerAPI directly.
/// </summary>
public static class BffAuthPolicies
{
    /// <summary>Delegated user token (sent by the React Web client via MSAL).</summary>
    public const string UserToken = "UserToken";

    public static void Register(AuthorizationOptions options)
    {
        options.AddPolicy(UserToken, p => p
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx =>
            {
                // idtyp=app means it's an app-only (S2S) token — reject.
                var idtyp = ctx.User.FindFirst("idtyp")?.Value;
                if (string.Equals(idtyp, "app", StringComparison.OrdinalIgnoreCase))
                    return false;
                // Must have a scope claim — proves it's a delegated token.
                return ctx.User.HasClaim(c =>
                    c.Type is "scp" or "http://schemas.microsoft.com/identity/claims/scope");
            }));
    }
}
