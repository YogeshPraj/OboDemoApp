using System.Text;
using CMSPDemo.PartnerAPI.Auth;
using CMSPDemo.PartnerAPI.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web;

namespace CMSPDemo.PartnerAPI.Controllers;

/// <summary>
/// Claims endpoints — one per "flavor" the partner API exposes.
///
/// /api/s2s/claims     accepts only app-only (S2S) tokens (BFF's client credentials).
/// /api/obo/claims     accepts only delegated user tokens (OBO from BFF).
/// /api/obo/graph-me   demonstrates the full OBO chain by calling Graph /me.
/// </summary>
[ApiController]
[Route("api")]
public sealed class ClaimsController : ControllerBase
{
    private readonly ITokenAcquisition  _tokenAcq;
    private readonly IHttpClientFactory _httpFactory;

    public ClaimsController(ITokenAcquisition tokenAcq, IHttpClientFactory httpFactory)
    {
        _tokenAcq    = tokenAcq;
        _httpFactory = httpFactory;
    }

    /// <summary>Returns claims; accepts only app-only (S2S) tokens from the BFF.</summary>
    [HttpGet("s2s/claims")]
    [Authorize(Policy = AuthPolicies.S2SOnly)]
    public IActionResult S2SClaims()
        => Ok(ClaimsResponse.From("partner-s2s", User));

    /// <summary>Returns claims; accepts delegated user tokens (OBO from BFF).</summary>
    [HttpGet("obo/claims")]
    [Authorize(Policy = AuthPolicies.OboOnly)]
    public IActionResult OboClaims()
        => Ok(ClaimsResponse.From("partner-obo", User));

    /// <summary>Calls Graph /me via the OBO chain: Web → BFF → PartnerAPI → Graph.</summary>
    [HttpGet("obo/graph-me")]
    [Authorize(Policy = AuthPolicies.OboOnly)]
    public async Task<IActionResult> OboGraphMe(CancellationToken ct)
    {
        var graphToken = await _tokenAcq.GetAccessTokenForUserAsync(new[] { "User.Read" });
        using var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", graphToken);
        using var resp = await http.GetAsync("https://graph.microsoft.com/v1.0/me", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return Content(body, "application/json", Encoding.UTF8);
    }
}
