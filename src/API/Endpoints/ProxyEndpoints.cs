using CMSPDemo.API.Services;
using System.Security.Claims;

namespace CMSPDemo.API.Endpoints;

/// <summary>
/// All BFF proxy routes.
///
/// Route convention:  /api/proxy/{flavor}/…
///   obo → BFF exchanges user token via OBO before calling PartnerAPI
///   s2s → BFF uses its own app credentials before calling PartnerAPI
///
/// The Web client ONLY knows about these routes; it never talks to PartnerAPI directly.
/// </summary>
public static class ProxyEndpoints
{
    public static IEndpointRouteBuilder MapProxyEndpoints(this IEndpointRouteBuilder app)
    {
        // ── /api/me — who is the signed-in user from the BFF's perspective ──────
        app.MapGet("/api/me", (HttpContext ctx) =>
            Results.Ok(BuildCallerSummary(ctx.User, "bff")))
        .RequireAuthorization()
        .WithName("BffMe")
        .WithSummary("Returns the caller's identity as seen by the BFF (before any OBO exchange).")
        .WithOpenApi();

        // ═══════════════════════════════════════════════════════════════════════
        //  OBO PROXIES — Web user token → BFF OBO-exchanges → PartnerAPI OBO token
        // ═══════════════════════════════════════════════════════════════════════

        app.MapGet("/api/proxy/obo/claims",
            async (PartnerApiService partner, HttpContext ctx, CancellationToken ct) =>
            {
                using var upstream = await partner.ForwardAsUserAsync("/api/obo/claims", HttpMethod.Get, ct: ct);
                await upstream.ProxyToAsync(ctx, ct);
            })
        .RequireAuthorization()
        .WithName("ProxyOboClaims")
        .WithSummary("BFF → PartnerAPI /api/obo/claims (OBO: user context preserved).")
        .WithOpenApi();

        app.MapGet("/api/proxy/obo/graph-me",
            async (PartnerApiService partner, HttpContext ctx, CancellationToken ct) =>
            {
                using var upstream = await partner.ForwardAsUserAsync("/api/obo/graph-me", HttpMethod.Get, ct: ct);
                await upstream.ProxyToAsync(ctx, ct);
            })
        .RequireAuthorization()
        .WithName("ProxyOboGraphMe")
        .WithSummary("BFF → PartnerAPI → Graph /me (full OBO chain: Web → BFF → PartnerAPI → Graph).")
        .WithOpenApi();

        // MCP OBO — streamed SSE response forwarded verbatim
        app.MapPost("/api/proxy/mcp-obo",
            async (PartnerApiService partner, HttpContext ctx, CancellationToken ct) =>
            {
                var body    = await ReadBodyAsync(ctx, ct);
                var headers = ExtractMcpHeaders(ctx);
                using var upstream = await partner.ForwardAsUserAsync(
                    "/mcp/obo", HttpMethod.Post,
                    body, ctx.Request.ContentType ?? "application/json",
                    headers, ct);
                await upstream.ProxyToAsync(ctx, ct);
            })
        .RequireAuthorization()
        .WithName("ProxyMcpObo")
        .WithSummary("BFF → PartnerAPI /mcp/obo (MCP streamable, OBO). Streams SSE response.")
        .WithOpenApi();

        // ═══════════════════════════════════════════════════════════════════════
        //  S2S PROXIES — BFF uses its own app credentials to call PartnerAPI
        // ═══════════════════════════════════════════════════════════════════════

        app.MapGet("/api/proxy/s2s/claims",
            async (PartnerApiService partner, HttpContext ctx, CancellationToken ct) =>
            {
                using var upstream = await partner.ForwardAsAppAsync("/api/s2s/claims", HttpMethod.Get, ct: ct);
                await upstream.ProxyToAsync(ctx, ct);
            })
        .RequireAuthorization()
        .WithName("ProxyS2SClaims")
        .WithSummary("BFF → PartnerAPI /api/s2s/claims (S2S: BFF's own app credentials).")
        .WithOpenApi();

        // MCP S2S — streamed SSE response forwarded verbatim
        app.MapPost("/api/proxy/mcp-s2s",
            async (PartnerApiService partner, HttpContext ctx, CancellationToken ct) =>
            {
                var body    = await ReadBodyAsync(ctx, ct);
                var headers = ExtractMcpHeaders(ctx);
                using var upstream = await partner.ForwardAsAppAsync(
                    "/mcp/s2s", HttpMethod.Post,
                    body, ctx.Request.ContentType ?? "application/json",
                    headers, ct);
                await upstream.ProxyToAsync(ctx, ct);
            })
        .RequireAuthorization()
        .WithName("ProxyMcpS2S")
        .WithSummary("BFF → PartnerAPI /mcp/s2s (MCP streamable, S2S). Streams SSE response.")
        .WithOpenApi();

        return app;
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<string?> ReadBodyAsync(HttpContext ctx, CancellationToken ct)
    {
        if (!ctx.Request.HasJsonContentType() && ctx.Request.ContentLength is null or 0)
            return null;
        using var sr = new StreamReader(ctx.Request.Body);
        return await sr.ReadToEndAsync(ct);
    }

    /// <summary>Forward MCP session header so PartnerAPI can maintain session continuity.</summary>
    private static IEnumerable<(string, string)> ExtractMcpHeaders(HttpContext ctx)
    {
        if (ctx.Request.Headers.TryGetValue("Mcp-Session-Id", out var sid) && !string.IsNullOrEmpty(sid))
            yield return ("Mcp-Session-Id", sid!);
        if (ctx.Request.Headers.TryGetValue("Accept", out var accept))
            yield return ("Accept", accept!);
    }

    private static object BuildCallerSummary(ClaimsPrincipal user, string flavor)
    {
        string? Get(params string[] types) => types
            .Select(t => user.FindFirst(t)?.Value)
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));

        var scopes = (Get("scp", "http://schemas.microsoft.com/identity/claims/scope") ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return new
        {
            flavor,
            name             = Get("name", System.Security.Claims.ClaimTypes.Name),
            userPrincipalName = Get("preferred_username", "upn"),
            objectId         = Get("oid", "http://schemas.microsoft.com/identity/claims/objectidentifier"),
            tenantId         = Get("tid", "http://schemas.microsoft.com/identity/claims/tenantid"),
            appId            = Get("appid", "azp"),
            scopes,
            claims           = user.Claims.Select(c => new { c.Type, c.Value }).ToArray()
        };
    }
}
