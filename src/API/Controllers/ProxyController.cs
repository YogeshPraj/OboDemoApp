using CMSPDemo.API.Auth;
using CMSPDemo.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CMSPDemo.API.Controllers;

/// <summary>
/// BFF proxy endpoints.
///
/// Route convention:  /api/proxy/{flavor}/…
///   obo → BFF exchanges user token via OBO before calling PartnerAPI
///   s2s → BFF uses its own app credentials before calling PartnerAPI
///
/// The Web client only ever talks to these routes; it never reaches PartnerAPI directly.
/// </summary>
[ApiController]
[Route("api/proxy")]
[Authorize(Policy = BffAuthPolicies.UserToken)]
public sealed class ProxyController : ControllerBase
{
    private readonly PartnerApiService _partner;
    public ProxyController(PartnerApiService partner) => _partner = partner;

    // ── OBO flavor — user identity preserved ─────────────────────────────────

    /// <summary>BFF → PartnerAPI /api/obo/claims (OBO: user context preserved).</summary>
    [HttpGet("obo/claims")]
    public async Task OboClaims(CancellationToken ct)
    {
        using var upstream = await _partner.ForwardAsUserAsync("/api/obo/claims", HttpMethod.Get, ct: ct);
        await upstream.ProxyToAsync(HttpContext, ct);
    }

    /// <summary>BFF → PartnerAPI → Graph /me (full OBO chain).</summary>
    [HttpGet("obo/graph-me")]
    public async Task OboGraphMe(CancellationToken ct)
    {
        using var upstream = await _partner.ForwardAsUserAsync("/api/obo/graph-me", HttpMethod.Get, ct: ct);
        await upstream.ProxyToAsync(HttpContext, ct);
    }

    /// <summary>BFF → PartnerAPI /mcp/obo (MCP streamable, OBO). Streams SSE response.</summary>
    [HttpPost("mcp-obo")]
    public async Task McpObo(CancellationToken ct)
    {
        var body    = await ReadBodyAsync(ct);
        var headers = ExtractMcpHeaders();
        using var upstream = await _partner.ForwardAsUserAsync(
            "/mcp/obo", HttpMethod.Post,
            body, Request.ContentType ?? "application/json",
            headers, ct);
        await upstream.ProxyToAsync(HttpContext, ct);
    }

    // ── S2S flavor — BFF's own credentials ───────────────────────────────────

    /// <summary>BFF → PartnerAPI /api/s2s/claims (S2S: BFF's own app credentials).</summary>
    [HttpGet("s2s/claims")]
    public async Task S2SClaims(CancellationToken ct)
    {
        using var upstream = await _partner.ForwardAsAppAsync("/api/s2s/claims", HttpMethod.Get, ct: ct);
        await upstream.ProxyToAsync(HttpContext, ct);
    }

    /// <summary>BFF → PartnerAPI /mcp/s2s (MCP streamable, S2S). Streams SSE response.</summary>
    [HttpPost("mcp-s2s")]
    public async Task McpS2S(CancellationToken ct)
    {
        var body    = await ReadBodyAsync(ct);
        var headers = ExtractMcpHeaders();
        using var upstream = await _partner.ForwardAsAppAsync(
            "/mcp/s2s", HttpMethod.Post,
            body, Request.ContentType ?? "application/json",
            headers, ct);
        await upstream.ProxyToAsync(HttpContext, ct);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<string?> ReadBodyAsync(CancellationToken ct)
    {
        if (!Request.HasJsonContentType() && Request.ContentLength is null or 0)
            return null;
        using var sr = new StreamReader(Request.Body);
        return await sr.ReadToEndAsync(ct);
    }

    private IEnumerable<(string, string)> ExtractMcpHeaders()
    {
        if (Request.Headers.TryGetValue("Mcp-Session-Id", out var sid) && !string.IsNullOrEmpty(sid))
            yield return ("Mcp-Session-Id", sid!);
        if (Request.Headers.TryGetValue("Accept", out var accept))
            yield return ("Accept", accept!);
    }
}
