using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace WebAPICert.Mtls;

public sealed class ForwardedClientCertificateAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ForwardedClientCertificate";
    private const string CertificateHeader = "X-ARR-ClientCert";

    private readonly IReadOnlySet<string> _allowedThumbprints;

    public ForwardedClientCertificateAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IReadOnlySet<string> allowedThumbprints)
        : base(options, logger, encoder)
    {
        _allowedThumbprints = allowedThumbprints;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headerValue = Request.Headers[CertificateHeader].ToString();
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return Task.FromResult(AuthenticateResult.Fail("Forwarded client certificate header is missing."));
        }

        if (!TryParseCertificate(headerValue, out var certificate))
        {
            return Task.FromResult(AuthenticateResult.Fail("Forwarded client certificate is invalid."));
        }

        var clientThumbprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        if (!_allowedThumbprints.Contains(clientThumbprint))
        {
            return Task.FromResult(AuthenticateResult.Fail("Client certificate thumbprint is not authorized."));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, clientThumbprint),
            new Claim(ClaimTypes.Name, certificate.Subject),
            new Claim("client_cert_thumbprint_sha256", clientThumbprint)
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static bool TryParseCertificate(string headerValue, out X509Certificate2 certificate)
    {
        certificate = null!;

        var normalized = Uri.UnescapeDataString(headerValue.Trim())
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\r", "\r", StringComparison.Ordinal);

        var base64 = normalized.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
            ? normalized
                .Replace("-----BEGIN CERTIFICATE-----", "", StringComparison.Ordinal)
                .Replace("-----END CERTIFICATE-----", "", StringComparison.Ordinal)
                .Replace("\n", "", StringComparison.Ordinal)
                .Replace("\r", "", StringComparison.Ordinal)
                .Trim()
            : normalized;

        if (string.IsNullOrWhiteSpace(base64))
        {
            return false;
        }

        var buffer = new byte[base64.Length];
        if (!Convert.TryFromBase64String(base64, buffer, out var written))
        {
            return false;
        }

        try
        {
            certificate = X509CertificateLoader.LoadCertificate(buffer[..written]);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
