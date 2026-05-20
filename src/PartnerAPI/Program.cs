using CMSPDemo.PartnerAPI.Auth;
using CMSPDemo.PartnerAPI.Claims;
using CMSPDemo.PartnerAPI.Mcp;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Logging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();

// ── AuthN: validate tokens issued for PartnerAPI (audience = api://<partnerapi-client-id>) ──
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(
        jwtOptions =>
        {
            builder.Configuration.Bind("AzureAd", jwtOptions);
            // Tokens may arrive from BFF (OBO) or BFF's own credentials (S2S).
            // For OBO the issuer is tenant-specific; for S2S the iss claim differs.
            jwtOptions.TokenValidationParameters.ValidateIssuer = false;
            jwtOptions.MapInboundClaims = false;
        },
        idOptions => builder.Configuration.Bind("AzureAd", idOptions))
    .EnableTokenAcquisitionToCallDownstreamApi(_ => { })
    .AddInMemoryTokenCaches();

// ── AuthZ: distinguish S2S (BFF calling as itself) vs OBO (BFF forwarding user) ──
builder.Services.AddAuthorization(AuthPolicies.Register);

// ── CORS: only the BFF should call PartnerAPI ──────────────────────────────────
var bffOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:5080" };

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(bffOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders("WWW-Authenticate", "Mcp-Session-Id")));

// ── MCP server (streamable HTTP, two auth-gated mounts) ───────────────────────
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<ClaimsTools>();

// ── Swagger ───────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    IdentityModelEventSource.ShowPII = true;
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// ── Flavor 1: Service-to-Service (BFF calls with its own app token) ───────────
app.MapGet("/api/s2s/claims", (HttpContext ctx) =>
        Results.Ok(ClaimsResponse.From("partner-s2s", ctx.User)))
   .RequireAuthorization(AuthPolicies.S2SOnly)
   .WithName("S2SClaims")
   .WithSummary("Returns claims; accepts only app-only (S2S) tokens from the BFF.")
   .WithOpenApi();

// ── Flavor 2: OBO — BFF-exchanged user token ──────────────────────────────────
app.MapGet("/api/obo/claims", (HttpContext ctx) =>
        Results.Ok(ClaimsResponse.From("partner-obo", ctx.User)))
   .RequireAuthorization(AuthPolicies.OboOnly)
   .WithName("OboClaims")
   .WithSummary("Returns claims; accepts delegated user tokens (OBO from BFF).")
   .WithOpenApi();

// OBO → Graph (real OBO chain: user → BFF → PartnerAPI → Graph)
app.MapGet("/api/obo/graph-me", async (ITokenAcquisition tokenAcq, IHttpClientFactory httpFactory) =>
{
    var graphToken = await tokenAcq.GetAccessTokenForUserAsync(new[] { "User.Read" });
    using var http = httpFactory.CreateClient();
    http.DefaultRequestHeaders.Authorization = new("Bearer", graphToken);
    using var resp = await http.GetAsync("https://graph.microsoft.com/v1.0/me");
    var body = await resp.Content.ReadAsStringAsync();
    return Results.Content(body, "application/json", System.Text.Encoding.UTF8, (int)resp.StatusCode);
})
.RequireAuthorization(AuthPolicies.OboOnly)
.WithName("OboGraphMe")
.WithSummary("Calls Graph /me via OBO chain: Web → BFF → PartnerAPI → Graph.")
.WithOpenApi();

// ── Flavor 3 & 4: MCP streamable, each gated by its auth policy ───────────────
app.MapMcp("/mcp/s2s").RequireAuthorization(AuthPolicies.S2SOnly)
   .WithMetadata(new EndpointNameMetadata("McpS2S"));

app.MapMcp("/mcp/obo").RequireAuthorization(AuthPolicies.OboOnly)
   .WithMetadata(new EndpointNameMetadata("McpObo"));

app.MapGet("/api/health", () => Results.Ok(new
{
    service = "PartnerAPI",
    ok = true,
    ts = DateTimeOffset.UtcNow
})).WithName("PartnerApiHealth");

app.Run();
