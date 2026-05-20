using Microsoft.Identity.Web;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;

namespace CMSPDemo.API.Services;

/// <summary>
/// Thin HTTP client layer that sits between the BFF endpoints and PartnerAPI.
///
/// Token acquisition strategy:
///  • OBO (user flows): BFF receives the user's delegated token; it exchanges it
///    for a PartnerAPI-scoped token via the OAuth 2.0 On-Behalf-Of grant.
///    Microsoft.Identity.Web handles the caching and exchange transparently.
///  • S2S (app flows): BFF uses its own client credentials to acquire an
///    app-only token for PartnerAPI (client_credentials grant).
/// </summary>
public sealed class PartnerApiService
{
    private readonly IHttpClientFactory  _http;
    private readonly ITokenAcquisition   _tokens;
    private readonly IConfiguration      _config;
    private readonly ILogger<PartnerApiService> _log;

    private string BaseUrl      => _config["DownstreamApis:PartnerApi:BaseUrl"]   ?? "http://localhost:5081";
    private string[] OboScopes  => _config.GetSection("DownstreamApis:PartnerApi:Scopes").Get<string[]>()   ?? Array.Empty<string>();
    private string[] AppScopes  => _config.GetSection("DownstreamApis:PartnerApi:AppScopes").Get<string[]>() ?? Array.Empty<string>();

    public PartnerApiService(
        IHttpClientFactory http,
        ITokenAcquisition tokens,
        IConfiguration config,
        ILogger<PartnerApiService> log)
    {
        _http   = http;
        _tokens = tokens;
        _config = config;
        _log    = log;
    }

    // ─── OBO (user delegated) ────────────────────────────────────────────────

    /// <summary>
    /// Sends a request to PartnerAPI on behalf of the signed-in user.
    /// The incoming user token is exchanged (OBO) for a PartnerAPI-scoped token.
    /// Returns the raw HttpResponseMessage so the caller can choose how to surface it.
    /// </summary>
    public Task<HttpResponseMessage> ForwardAsUserAsync(
        string relativePath,
        HttpMethod method,
        string? jsonBody      = null,
        string? contentType   = null,
        IEnumerable<(string Key, string Value)>? extraHeaders = null,
        CancellationToken ct  = default)
        => ForwardAsync(isApp: false, relativePath, method, jsonBody, contentType, extraHeaders, ct);

    // ─── S2S (app-only) ──────────────────────────────────────────────────────

    /// <summary>
    /// Sends a request to PartnerAPI using the BFF's own credentials (S2S).
    /// </summary>
    public Task<HttpResponseMessage> ForwardAsAppAsync(
        string relativePath,
        HttpMethod method,
        string? jsonBody      = null,
        string? contentType   = null,
        IEnumerable<(string Key, string Value)>? extraHeaders = null,
        CancellationToken ct  = default)
        => ForwardAsync(isApp: true, relativePath, method, jsonBody, contentType, extraHeaders, ct);

    // ─── Internal ────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> ForwardAsync(
        bool   isApp,
        string relativePath,
        HttpMethod method,
        string? jsonBody,
        string? contentType,
        IEnumerable<(string Key, string Value)>? extraHeaders,
        CancellationToken ct)
    {
        string token;
        try
        {
            token = isApp
                ? await _tokens.GetAccessTokenForAppAsync(AppScopes.First())
                : await _tokens.GetAccessTokenForUserAsync(OboScopes);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Token acquisition failed (isApp={IsApp})", isApp);
            throw;
        }

        var client = _http.CreateClient("PartnerApi");
        var request = new HttpRequestMessage(method, $"{BaseUrl.TrimEnd('/')}/{relativePath.TrimStart('/')}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (extraHeaders is not null)
            foreach (var (k, v) in extraHeaders)
                request.Headers.TryAddWithoutValidation(k, v);

        if (jsonBody is not null)
        {
            request.Content = new StringContent(
                jsonBody,
                Encoding.UTF8,
                contentType ?? "application/json");
        }

        _log.LogDebug("→ PartnerAPI {Method} {Path} (isApp={IsApp})", method, relativePath, isApp);

        // ResponseHeadersRead lets us stream the response body without buffering it all.
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        _log.LogDebug("← PartnerAPI {Status} {Path}", (int)response.StatusCode, relativePath);
        return response;
    }
}

/// <summary>
/// Convenience extension that copies a PartnerAPI response verbatim to an
/// ASP.NET Core HttpContext response (status, content-type, headers, body).
/// Streams the body so MCP SSE payloads are forwarded incrementally.
/// </summary>
public static class PartnerApiResponseExtensions
{
    private static readonly HashSet<string> _skipHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Transfer-Encoding", "Content-Length"   // these are re-computed by ASP.NET
    };

    public static async Task ProxyToAsync(this HttpResponseMessage upstream, HttpContext ctx, CancellationToken ct = default)
    {
        ctx.Response.StatusCode = (int)upstream.StatusCode;

        foreach (var (k, vals) in upstream.Headers.Concat(upstream.Content.Headers))
        {
            if (_skipHeaders.Contains(k)) continue;
            ctx.Response.Headers[k] = vals.ToArray();
        }

        await using var body = await upstream.Content.ReadAsStreamAsync(ct);
        await body.CopyToAsync(ctx.Response.Body, ct);
    }
}
