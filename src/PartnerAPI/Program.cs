using CMSPDemo.PartnerAPI.Auth;
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

// ── Controllers + Swagger ─────────────────────────────────────────────────────
builder.Services.AddControllers();
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

// REST routes live in src/PartnerAPI/Controllers/.
app.MapControllers();

// MCP endpoints stay as middleware-mounted routes — the streamable-HTTP transport
// uses its own request pipeline and is not a controller concern.
app.MapMcp("/mcp/s2s").RequireAuthorization(AuthPolicies.S2SOnly)
   .WithMetadata(new EndpointNameMetadata("McpS2S"));

app.MapMcp("/mcp/obo").RequireAuthorization(AuthPolicies.OboOnly)
   .WithMetadata(new EndpointNameMetadata("McpObo"));

app.Run();
