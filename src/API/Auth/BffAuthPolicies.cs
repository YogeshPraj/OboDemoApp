using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace CMSPDemo.API.Auth;

/// <summary>
/// The BFF accepts delegated user tokens in two flavors:
///   • JWT bearer issued by Entra and validated by Microsoft.Identity.Web (MSAL mode in the SPA).
///   • Session cookie minted after the server-side OIDC sign-in flow.
///
/// App-only callers (S2S) are rejected — they should call PartnerAPI directly.
/// </summary>
public static class BffAuthPolicies
{
    /// <summary>Delegated user identity, however it was acquired.</summary>
    public const string UserToken = "UserToken";

    public static void Register(AuthorizationOptions options)
    {
        options.AddPolicy(UserToken, p => p
            // Either scheme is acceptable; the multi-scheme dispatcher in Program.cs
            // routes individual requests to the right handler.
            .AddAuthenticationSchemes(
                JwtBearerDefaults.AuthenticationScheme,
                SessionAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx =>
            {
                // idtyp=app means it's an app-only (S2S) token — reject.
                var idtyp = ctx.User.FindFirst("idtyp")?.Value;
                if (string.Equals(idtyp, "app", StringComparison.OrdinalIgnoreCase))
                    return false;
                // Must have a scope claim — proves it's a delegated identity.
                // Session-auth users get a synthetic scp=access_as_user claim from the handler.
                return ctx.User.HasClaim(c =>
                    c.Type is "scp" or "http://schemas.microsoft.com/identity/claims/scope");
            }));

        // Default fallback policy: just require an authenticated user from either scheme.
        options.DefaultPolicy = new AuthorizationPolicyBuilder(
                JwtBearerDefaults.AuthenticationScheme,
                SessionAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser()
            .Build();
    }
}
