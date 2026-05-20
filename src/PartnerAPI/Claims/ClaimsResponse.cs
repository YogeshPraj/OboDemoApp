using System.Security.Claims;

namespace CMSPDemo.PartnerAPI.Claims;

public sealed record ClaimEntry(string Type, string Value);

public sealed record ClaimsResponse(
    string Flavor,
    string? Name,
    string? UserPrincipalName,
    string? ObjectId,
    string? TenantId,
    string? AppId,
    string? IdType,
    string[] Scopes,
    string[] Roles,
    IReadOnlyList<ClaimEntry> Claims)
{
    public static ClaimsResponse From(string flavor, ClaimsPrincipal user)
    {
        string? Get(params string[] types) => types
            .Select(t => user.FindFirst(t)?.Value)
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));

        var scopes = (Get("scp", "http://schemas.microsoft.com/identity/claims/scope") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var roles = user.FindAll("roles").Concat(user.FindAll(ClaimTypes.Role))
            .Select(c => c.Value).Distinct().ToArray();

        return new ClaimsResponse(
            Flavor:             flavor,
            Name:               Get("name", ClaimTypes.Name),
            UserPrincipalName:  Get("preferred_username", "upn", ClaimTypes.Upn),
            ObjectId:           Get("oid", "http://schemas.microsoft.com/identity/claims/objectidentifier"),
            TenantId:           Get("tid", "http://schemas.microsoft.com/identity/claims/tenantid"),
            AppId:              Get("appid", "azp", "http://schemas.microsoft.com/identity/claims/appid"),
            IdType:             Get("idtyp"),
            Scopes:             scopes,
            Roles:              roles,
            Claims:             user.Claims.Select(c => new ClaimEntry(c.Type, c.Value)).ToArray());
    }
}
