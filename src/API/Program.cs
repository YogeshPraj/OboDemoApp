using CMSPDemo.API.Auth;
using CMSPDemo.API.Endpoints;
using CMSPDemo.API.Helpers;
using CMSPDemo.API.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Logging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();

// ────────────────────────────────────────────────────────────────────────────
// Session-based auth (server-side OIDC, Emerald-style)
//
//  • InMemorySessionStore   — holds UserSession objects keyed by session id
//  • InMemoryPendingSignInStore — holds (state, codeVerifier, returnUrl) during sign-in
//  • SessionTokenAcquirer   — OBO from the session's stored access token
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<ISessionStore,         InMemorySessionStore>();
builder.Services.AddSingleton<IPendingSignInStore,   InMemoryPendingSignInStore>();
builder.Services.AddScoped<ISessionTokenAcquirer,    SessionTokenAcquirer>();

// ────────────────────────────────────────────────────────────────────────────
// AuthN — multi-scheme.
//
//  The default scheme "BearerOrSession" inspects the request:
//    • Authorization: Bearer …  → JwtBearer (Microsoft.Identity.Web)
//    • else                     → Session   (reads CMSP_SESSION cookie)
//
//  That lets a single endpoint accept both MSAL-mode and server-OIDC-mode
//  callers without changing controller-level attributes.
// ────────────────────────────────────────────────────────────────────────────
var authBuilder = builder.Services
    .AddAuthentication(opts =>
    {
        opts.DefaultScheme             = "BearerOrSession";
        opts.DefaultAuthenticateScheme = "BearerOrSession";
        opts.DefaultChallengeScheme    = "BearerOrSession";
    });

authBuilder.AddPolicyScheme("BearerOrSession", "BearerOrSession", opts =>
{
    opts.ForwardDefaultSelector = ctx =>
    {
        var hdr = ctx.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(hdr) && hdr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return JwtBearerDefaults.AuthenticationScheme;
        return SessionAuthenticationHandler.SchemeName;
    };
});

authBuilder
    .AddMicrosoftIdentityWebApi(
        jwtOptions =>
        {
            builder.Configuration.Bind("AzureAd", jwtOptions);
            jwtOptions.TokenValidationParameters.ValidateIssuer = false;  // support common / MSA
            jwtOptions.MapInboundClaims = false;
        },
        idOptions => builder.Configuration.Bind("AzureAd", idOptions),
        jwtBearerScheme: JwtBearerDefaults.AuthenticationScheme)
    .EnableTokenAcquisitionToCallDownstreamApi(_ => { })     // OBO + S2S via ITokenAcquisition
    .AddDownstreamApi("PartnerApi",                          // typed HttpClient + token injection
        builder.Configuration.GetSection("DownstreamApis:PartnerApi"))
    .AddInMemoryTokenCaches();

authBuilder.AddScheme<SessionAuthenticationOptions, SessionAuthenticationHandler>(
    SessionAuthenticationHandler.SchemeName, _ => { });

// ────────────────────────────────────────────────────────────────────────────
// AuthZ — BFF-specific policies (now multi-scheme-aware)
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddAuthorization(BffAuthPolicies.Register);

// ────────────────────────────────────────────────────────────────────────────
// Named HttpClient for raw forwarding to PartnerAPI.
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddHttpClient("PartnerApi", client =>
{
    var baseUrl = builder.Configuration["DownstreamApis:PartnerApi:BaseUrl"] ?? "http://localhost:5081";
    client.BaseAddress = new Uri(baseUrl);
    client.DefaultRequestHeaders.Add("Accept", "application/json, text/event-stream");
});

// ────────────────────────────────────────────────────────────────────────────
// Application services
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddScoped<PartnerApiService>();

// ────────────────────────────────────────────────────────────────────────────
// CORS — Vite dev server + production origin.
// AllowCredentials() is required so the browser sends the CMSP_SESSION cookie
// on cross-origin fetch in server-OIDC mode.
// ────────────────────────────────────────────────────────────────────────────
var webOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:5173" };

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(webOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()
    .WithExposedHeaders("WWW-Authenticate", "Mcp-Session-Id")));

// ────────────────────────────────────────────────────────────────────────────
// Swagger
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title       = "CMSPDemo BFF API",
        Description = "Backend-for-Frontend layer between the React Web client and PartnerAPI."
    });
});

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

// ────────────────────────────────────────────────────────────────────────────
// Routes
// ────────────────────────────────────────────────────────────────────────────

// Server-OIDC sign-in (/api/auth/*) + me + sign-out
app.MapAuthEndpoints();

// Proxy routes: /api/me, /api/proxy/obo/*, /api/proxy/s2s/*, /api/proxy/mcp-*
app.MapProxyEndpoints();

// S2S helper: /api/helpers/acquire-s2s
app.MapS2SHelper();

// Phase 3 — widget token broker: /api/widget/mcs-token
app.MapWidgetTokenEndpoint();

// Health check (anonymous)
app.MapGet("/api/health", () => Results.Ok(new
{
    service = "BFF API",
    ok      = true,
    ts      = DateTimeOffset.UtcNow
})).WithName("BffHealth");

app.Run();
