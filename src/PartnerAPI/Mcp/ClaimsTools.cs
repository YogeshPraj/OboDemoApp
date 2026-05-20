using System.ComponentModel;
using CMSPDemo.PartnerAPI.Claims;
using ModelContextProtocol.Server;

namespace CMSPDemo.PartnerAPI.Mcp;

[McpServerToolType]
public sealed class ClaimsTools
{
    private readonly IHttpContextAccessor _http;
    private readonly string _flavor;

    public ClaimsTools(IHttpContextAccessor http)
    {
        _http = http;
        _flavor = http.HttpContext?.Request.Path.Value?.Contains("/mcp/s2s", StringComparison.OrdinalIgnoreCase) == true
            ? "mcp-s2s"
            : "mcp-obo";
    }

    [McpServerTool, Description("Returns the full claims set of the caller (BFF acting as user or BFF acting as app).")]
    public ClaimsResponse GetCallerClaims()
    {
        var user = _http.HttpContext?.User
            ?? throw new InvalidOperationException("No HttpContext available.");
        return ClaimsResponse.From(_flavor, user);
    }

    [McpServerTool, Description("Returns a human-friendly summary of who the caller is.")]
    public string WhoAmI()
    {
        var user = _http.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true) return "Anonymous";
        var r = ClaimsResponse.From(_flavor, user);
        return $"{r.Name ?? r.UserPrincipalName ?? r.AppId} " +
               $"(tid={r.TenantId}, scopes=[{string.Join(',', r.Scopes)}], roles=[{string.Join(',', r.Roles)}])";
    }

    [McpServerTool, Description("Echoes back the provided text — useful to verify MCP streamable transport is working.")]
    public string Echo([Description("Text to echo back.")] string text) => text;
}
