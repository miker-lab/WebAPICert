using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using WebAPICert.Mtls;

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

builder.Services.AddSingleton<IReadOnlySet<string>>(allowedClientThumbprints);
builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services
    .AddAuthentication(ForwardedClientCertificateAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ForwardedClientCertificateAuthenticationHandler>(
        ForwardedClientCertificateAuthenticationHandler.SchemeName,
        _ => { });

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
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();

static string NormalizeThumbprint(string thumbprint)
{
    return thumbprint
        .Replace(":", "", StringComparison.Ordinal)
        .Replace(" ", "", StringComparison.Ordinal)
        .Trim()
        .ToUpperInvariant();
}
