using System.Security.Cryptography.X509Certificates;
using Azure.Identity;
using Azure.Security.KeyVault.Certificates;
using Azure.Security.KeyVault.Secrets;
using CMSPDemo.API.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Client;

namespace CMSPDemo.API.Controllers;

public sealed record S2STokenRequest(
    string TenantId,
    string ClientId,
    string KeyVaultUrl,
    string CertificateName,
    string Scope);

public sealed record S2STokenResponse(
    string  AccessToken,
    string  TokenType,
    long    ExpiresOn,
    string? Source);

/// <summary>
/// /api/helpers/acquire-s2s — lets the Web UI trigger a client-credentials flow
/// without ever seeing the private key.
///
/// Web UI → BFF (this endpoint) → Key Vault (cert via DefaultAzureCredential)
///        → Entra ID (client_credentials) → returns access token to Web UI
///
/// The caller must be authenticated (BFF validates the user token first).
/// </summary>
[ApiController]
[Route("api/helpers")]
[Authorize(Policy = BffAuthPolicies.UserToken)]
public sealed class HelpersController : ControllerBase
{
    [HttpPost("acquire-s2s")]
    public async Task<IActionResult> AcquireS2S([FromBody] S2STokenRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.TenantId))         return BadRequest("TenantId required");
        if (string.IsNullOrWhiteSpace(req.ClientId))         return BadRequest("ClientId required");
        if (string.IsNullOrWhiteSpace(req.KeyVaultUrl))      return BadRequest("KeyVaultUrl required");
        if (string.IsNullOrWhiteSpace(req.CertificateName))  return BadRequest("CertificateName required");
        if (string.IsNullOrWhiteSpace(req.Scope))            return BadRequest("Scope required");

        var credential = new DefaultAzureCredential();
        var cert       = await LoadCertWithKeyAsync(new Uri(req.KeyVaultUrl), req.CertificateName, credential, ct);

        var cca = ConfidentialClientApplicationBuilder
            .Create(req.ClientId)
            .WithCertificate(cert, sendX5C: true)
            .WithAuthority(new Uri($"https://login.microsoftonline.com/{req.TenantId}"))
            .Build();

        var result = await cca.AcquireTokenForClient(new[] { req.Scope }).ExecuteAsync(ct);

        return Ok(new S2STokenResponse(
            AccessToken: result.AccessToken,
            TokenType:   result.TokenType,
            ExpiresOn:   result.ExpiresOn.ToUnixTimeSeconds(),
            Source:      result.AuthenticationResultMetadata.TokenSource.ToString()));
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
