# WebAPICert

ASP.NET Core Web API protected with client-certificate authentication (mTLS) for all endpoints.

## Architecture

Client requests terminate TLS at Caddy and are proxied to the API over local HTTP.

`Client (curl/Postman) -> Caddy :7443 (TLS + mTLS) -> ASP.NET Core API :5000`

Caddy must forward the validated client certificate in `X-ARR-ClientCert`.  
The API authenticates that certificate and authorizes requests only when the certificate SHA-256 thumbprint matches configured allowed thumbprints.

## Configuration

Allowed client thumbprints are configured in:

- `WebAPICert/appsettings.json`
- `WebAPICert/appsettings.Development.json`

Key:

- `Mtls:AllowedClientThumbprints` (array of SHA-256 thumbprints)

Current configured thumbprint:

- `1606423564635B54363909B98F469F7B99AFBE08D21B37897C7E9A7FDDBB5C76`

## Run locally

From repository root:

```bash
dotnet restore WebAPICert.slnx
dotnet build WebAPICert.slnx
dotnet run --project WebAPICert/WebAPICert.csproj
```

The app runs on `http://localhost:5000` (Caddy fronts it at `https://localhost:7443`).

## Test with curl

Success case (valid client cert + allowed thumbprint):

```bash
curl -k -X GET "https://localhost:7443/weatherforecast" \
  --cert ~/integration-ca-dev/tenants/tenant-a.crt \
  --key ~/integration-ca-dev/tenants/tenant-a.key \
  --cacert ~/integration-ca-dev/ca/ca.crt \
  -H "Accept: application/json"
```

Failure case (no client cert):

```bash
curl -k -X GET "https://localhost:7443/weatherforecast" \
  --cacert ~/integration-ca-dev/ca/ca.crt \
  -H "Accept: application/json"
```

Failure case (wrong client cert thumbprint):

```bash
curl -k -X GET "https://localhost:7443/weatherforecast" \
  --cert ~/integration-ca-dev/tenants/tenant-b.crt \
  --key ~/integration-ca-dev/tenants/tenant-b.key \
  --cacert ~/integration-ca-dev/ca/ca.crt \
  -H "Accept: application/json"
```

## Notes

- All endpoints are protected by a global authorization fallback policy.
- Keep Caddy configured to strip client-provided `X-ARR-ClientCert` before adding its own forwarded certificate value.
- Never commit private certificate material (`*.key`, `*.pfx`, CA private key).
