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
// AuthN — validate JWT bearer tokens that the React Web client sends to the BFF.
//
//  • The BFF's audience  = api://<bff-app-id>
//  • After validation the BFF can OBO-exchange the token for PartnerAPI scopes,
//    or use its own credentials for S2S calls to PartnerAPI.
// ────────────────────────────────────────────────────────────────────────────
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(
        jwtOptions =>
        {
            builder.Configuration.Bind("AzureAd", jwtOptions);
            jwtOptions.TokenValidationParameters.ValidateIssuer = false;   // support common / MSA
            jwtOptions.MapInboundClaims = false;
        },
        idOptions => builder.Configuration.Bind("AzureAd", idOptions))
    .EnableTokenAcquisitionToCallDownstreamApi(_ => { })  // activates OBO + S2S via ITokenAcquisition
    .AddDownstreamApi("PartnerApi",                  // typed HttpClient + token injection
        builder.Configuration.GetSection("DownstreamApis:PartnerApi"))
    .AddInMemoryTokenCaches();

// ────────────────────────────────────────────────────────────────────────────
// AuthZ — BFF-specific policies
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddAuthorization(BffAuthPolicies.Register);

// ────────────────────────────────────────────────────────────────────────────
// Named HttpClient for raw forwarding to PartnerAPI.
// (Used by PartnerApiService when it needs full streaming control.)
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
// CORS — only the Vite dev server (and production origin) may call the BFF.
// ────────────────────────────────────────────────────────────────────────────
var webOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:5173" };

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(webOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders("WWW-Authenticate", "Mcp-Session-Id")));

// ────────────────────────────────────────────────────────────────────────────
// Swagger
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "CMSPDemo BFF API", Description = "Backend-for-Frontend layer between the React Web client and PartnerAPI." });
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

// Proxy routes: /api/me, /api/proxy/obo/*, /api/proxy/s2s/*, /api/proxy/mcp-*
app.MapProxyEndpoints();

// S2S helper: /api/helpers/acquire-s2s
app.MapS2SHelper();

// Health check (anonymous)
app.MapGet("/api/health", () => Results.Ok(new
{
    service = "BFF API",
    ok      = true,
    ts      = DateTimeOffset.UtcNow
})).WithName("BffHealth");

app.Run();
