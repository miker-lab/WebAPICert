using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

var configuredThumbprints = builder.Configuration
    .GetSection("Mtls:AllowedClientThumbprints")
    .Get<string[]>();

if (configuredThumbprints is null || configuredThumbprints.Length == 0)
{
    throw new InvalidOperationException("At least one allowed client thumbprint must be configured in Mtls:AllowedClientThumbprints.");
}

var allowedClientThumbprints = configuredThumbprints
    .Select(NormalizeThumbprint)
    .Where(thumbprint => !string.IsNullOrWhiteSpace(thumbprint))
    .ToHashSet(StringComparer.Ordinal);

if (allowedClientThumbprints.Count == 0)
{
    throw new InvalidOperationException("Configured client thumbprints are empty after normalization.");
}

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddCertificateForwarding(options =>
{
    options.CertificateHeader = "X-ARR-ClientCert";
    options.HeaderConverter = ParseForwardedClientCertificate;
});

builder.Services
    .AddAuthentication(CertificateAuthenticationDefaults.AuthenticationScheme)
    .AddCertificate(options =>
    {
        options.AllowedCertificateTypes = CertificateTypes.All;
        options.ValidateCertificateUse = false;
        options.RevocationMode = X509RevocationMode.NoCheck;
        options.Events = new CertificateAuthenticationEvents
        {
            OnCertificateValidated = context =>
            {
                var certificate = context.ClientCertificate;
                if (certificate is null)
                {
                    context.Fail("Client certificate was not provided.");
                    return Task.CompletedTask;
                }

                var clientThumbprint = GetSha256Thumbprint(certificate);
                if (!allowedClientThumbprints.Contains(clientThumbprint))
                {
                    context.Fail("Client certificate thumbprint is not authorized.");
                    return Task.CompletedTask;
                }

                var claims = new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, clientThumbprint),
                    new Claim(ClaimTypes.Name, certificate.Subject),
                    new Claim("client_cert_thumbprint_sha256", clientThumbprint)
                };

                context.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, context.Scheme.Name));
                context.Success();
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = context =>
            {
                context.Fail("Client certificate authentication failed.");
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseForwardedHeaders();
app.UseCertificateForwarding();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();

static X509Certificate2 ParseForwardedClientCertificate(string headerValue)
{
    if (string.IsNullOrWhiteSpace(headerValue))
    {
        throw new InvalidOperationException("Forwarded client certificate header is empty.");
    }

    var trimmedValue = headerValue.Trim();
    var base64 = trimmedValue.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
        ? trimmedValue
            .Replace("-----BEGIN CERTIFICATE-----", "", StringComparison.Ordinal)
            .Replace("-----END CERTIFICATE-----", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal)
            .Trim()
        : trimmedValue;

    if (!TryDecodeBase64(base64, out var certificateBytes))
    {
        throw new InvalidOperationException("Forwarded client certificate header is not valid base64.");
    }

    return X509CertificateLoader.LoadCertificate(certificateBytes);
}

static bool TryDecodeBase64(string input, out byte[] bytes)
{
    bytes = [];
    if (string.IsNullOrWhiteSpace(input))
    {
        return false;
    }

    var buffer = new byte[input.Length];
    if (!Convert.TryFromBase64String(input, buffer, out var bytesWritten))
    {
        return false;
    }

    bytes = buffer[..bytesWritten];
    return true;
}

static string GetSha256Thumbprint(X509Certificate2 certificate)
{
    var hash = SHA256.HashData(certificate.RawData);
    return Convert.ToHexString(hash);
}

static string NormalizeThumbprint(string thumbprint)
{
    return thumbprint
        .Replace(":", "", StringComparison.Ordinal)
        .Replace(" ", "", StringComparison.Ordinal)
        .Trim()
        .ToUpperInvariant();
}
