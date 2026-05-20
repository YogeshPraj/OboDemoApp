using System.Security.Claims;
using CMSPDemo.API.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CMSPDemo.API.Controllers;

/// <summary>
/// Root-level identity + health endpoints on the BFF.
/// </summary>
[ApiController]
public sealed class MeController : ControllerBase
{
    /// <summary>
    /// Returns the caller's identity as seen by the BFF (before any OBO exchange).
    /// Works in both auth modes — bearer JWT or session cookie.
    /// </summary>
    [HttpGet("/api/me")]
    [Authorize(Policy = BffAuthPolicies.UserToken)]
    public IActionResult Me()
        => Ok(CallerSummary.Build(User, "bff"));

    /// <summary>Anonymous health probe.</summary>
    [HttpGet("/api/health")]
    [AllowAnonymous]
    public IActionResult Health()
        => Ok(new { service = "BFF API", ok = true, ts = DateTimeOffset.UtcNow });
}

/// <summary>Shared helper used by Me and AuthController.</summary>
internal static class CallerSummary
{
    public static object Build(ClaimsPrincipal user, string flavor)
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
            name              = Get("name", ClaimTypes.Name),
            userPrincipalName = Get("preferred_username", "upn"),
            objectId          = Get("oid", "http://schemas.microsoft.com/identity/claims/objectidentifier"),
            tenantId          = Get("tid", "http://schemas.microsoft.com/identity/claims/tenantid"),
            appId             = Get("appid", "azp"),
            scopes,
            claims            = user.Claims.Select(c => new { c.Type, c.Value }).ToArray()
        };
    }
}
