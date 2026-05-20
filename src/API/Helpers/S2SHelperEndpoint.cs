using System.Security.Cryptography.X509Certificates;
using Azure.Identity;
using Azure.Security.KeyVault.Certificates;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Identity.Client;

namespace CMSPDemo.API.Helpers;

/// <summary>
/// /api/helpers/acquire-s2s — lets the Web UI trigger a client-credentials flow
/// without ever seeing the private key.
///
/// Flow:
///   Web UI → BFF (this endpoint) → Key Vault (cert via DefaultAzureCredential)
///          → Entra ID (client_credentials) → returns access token to Web UI
///
/// The caller must be authenticated (BFF validates the user token first).
/// This endpoint requires a signed-in user but the resulting token is app-level.
/// </summary>
public sealed record S2STokenRequest(
    string TenantId,
    string ClientId,
    string KeyVaultUrl,
    string CertificateName,
    string Scope);

public sealed record S2STokenResponse(
    string AccessToken,
    string TokenType,
    long   ExpiresOn,
    string? Source);

public static class S2SHelperEndpoint
{
    public static IEndpointRouteBuilder MapS2SHelper(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/helpers/acquire-s2s",
            async (S2STokenRequest req, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(req.TenantId))         return Results.BadRequest("TenantId required");
                if (string.IsNullOrWhiteSpace(req.ClientId))         return Results.BadRequest("ClientId required");
                if (string.IsNullOrWhiteSpace(req.KeyVaultUrl))      return Results.BadRequest("KeyVaultUrl required");
                if (string.IsNullOrWhiteSpace(req.CertificateName))  return Results.BadRequest("CertificateName required");
                if (string.IsNullOrWhiteSpace(req.Scope))            return Results.BadRequest("Scope required");

                var credential = new DefaultAzureCredential();
                var cert       = await LoadCertWithKeyAsync(new Uri(req.KeyVaultUrl), req.CertificateName, credential, ct);

                var cca = ConfidentialClientApplicationBuilder
                    .Create(req.ClientId)
                    .WithCertificate(cert, sendX5C: true)
                    .WithAuthority(new Uri($"https://login.microsoftonline.com/{req.TenantId}"))
                    .Build();

                var result = await cca.AcquireTokenForClient(new[] { req.Scope }).ExecuteAsync(ct);

                return Results.Ok(new S2STokenResponse(
                    AccessToken: result.AccessToken,
                    TokenType:   result.TokenType,
                    ExpiresOn:   result.ExpiresOn.ToUnixTimeSeconds(),
                    Source:      result.AuthenticationResultMetadata.TokenSource.ToString()));
            })
        .RequireAuthorization()          // caller must be authenticated with a user token
        .WithName("AcquireS2SToken")
        .WithSummary("Acquire a client-credentials token from a Key Vault certificate. The private key never leaves Azure.")
        .WithOpenApi();

        return app;
    }

    private static async Task<X509Certificate2> LoadCertWithKeyAsync(
        Uri vaultUri, string certName, DefaultAzureCredential cred, CancellationToken ct)
    {
        var certClient = new CertificateClient(vaultUri, cred);
        var kvCert     = await certClient.GetCertificateAsync(certName, ct);
        var secretId   = kvCert.Value.SecretId
            ?? throw new InvalidOperationException("KV cert has no associated secret (cert may be non-exportable).");

        var secretClient = new SecretClient(vaultUri, cred);
        var segments     = secretId.Segments;
        var name         = segments[^2].TrimEnd('/');
        var version      = segments[^1];
        var secret       = await secretClient.GetSecretAsync(name, version, ct);

        var pfxBytes = Convert.FromBase64String(secret.Value.Value);
#pragma warning disable SYSLIB0057
        return new X509Certificate2(pfxBytes, (string?)null,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057
    }
}
