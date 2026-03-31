# Integration API — WSL2 + Ubuntu 24.04 + Caddy Development Setup
*mTLS · Multi-Tenant Certificate Management · Reverse Proxy Configuration*

---

## Overview

This guide covers the complete setup of Caddy as a reverse proxy in WSL2 on Windows 11, acting as the TLS and mTLS termination layer for the Integration API frontend during development. The goal is full security parity with the IIS production setup — no certificate bypass, no dev-mode shortcuts.

**Architecture:**

```
Windows 11
  ├── Visual Studio / Rider   (ASP.NET frontend on http://localhost:5000)
  ├── Postman / curl          (sends HTTPS requests with client cert)
  └── WSL2 Ubuntu 24.04
       └── Caddy :7443        (TLS + mTLS termination)
            └── forwards to localhost:5000 with X-ARR-ClientCert header
```

---

## Part 1 — WSL2 Networking Setup

Enable mirrored networking so WSL2 and Windows share `localhost`. This eliminates the need for dynamic IP detection.

### 1.1 Enable Mirrored Networking

Open Notepad and edit or create the file at:

```
C:\Users\YourUsername\.wslconfig
```

Add the following content:

```ini
[wsl2]
networkingMode=mirrored
dnsTunneling=true
firewall=true
```

Then restart WSL2 from PowerShell (as Administrator):

```powershell
wsl --shutdown
# Wait a few seconds, then reopen your WSL2 terminal
```

> **ℹ️ Note:** Mirrored networking is available on Windows 11 22H2 and later. Run `winver` to verify your build.

---

## Part 2 — Install Caddy in WSL2

Run the following commands inside your WSL2 Ubuntu 24.04 terminal.

### 2.1 Add Caddy Repository and Install

```bash
sudo apt install -y debian-keyring debian-archive-keyring apt-transport-https curl

curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' \
  | sudo gpg --dearmor \
  -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg

curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' \
  | sudo tee /etc/apt/sources.list.d/caddy-stable.list

sudo apt update && sudo apt install -y caddy

# Verify installation
caddy version
```

### 2.2 Stop the Default Caddy Service

Caddy installs as a systemd service listening on port 80/443. Stop it so you can run it manually with your own config:

```bash
sudo systemctl stop caddy
sudo systemctl disable caddy
```

---

## Part 3 — Create the Dev Root CA

You will create a Root CA that signs all dev tenant certificates. IIS in production and Caddy in development both trust this CA. Tenant thumbprints differ between dev and prod — they are registered separately in their respective databases.

### 3.1 Directory Structure

```bash
mkdir -p ~/integration-ca-dev/{ca,tenants,server}
cd ~/integration-ca-dev
```

### 3.2 Generate the Root CA Key and Certificate

```bash
# Generate CA private key — protect this with a strong passphrase
openssl genrsa -aes256 -out ca/ca.key 4096

# Generate self-signed CA certificate (10 year validity for dev CA)
openssl req -new -x509 -days 3650 \
  -key ca/ca.key \
  -out ca/ca.crt \
  -subj "/C=FI/O=YourCompany/CN=IntegrationAPI Dev CA"

# Verify
openssl x509 -in ca/ca.crt -noout -subject -dates
```

> **⚠️ Warning:** Keep `ca/ca.key` secure. Anyone with this key can issue trusted client certificates. Do not commit it to version control.

---

## Part 4 — Create the Caddy Server Certificate

Caddy needs a server certificate for localhost that Windows trusts. You will sign it with your dev CA, then import the CA into Windows.

### 4.1 Create Extension File for Server Cert

```bash
cat > server/localhost.ext << 'EOF'
basicConstraints = CA:FALSE
subjectKeyIdentifier = hash
authorityKeyIdentifier = keyid,issuer
keyUsage = critical, digitalSignature, keyEncipherment
extendedKeyUsage = serverAuth
subjectAltName = DNS:localhost,IP:127.0.0.1
EOF
```

### 4.2 Generate Server Key and Certificate

```bash
# Server private key
openssl genrsa -out server/localhost.key 2048

# Certificate signing request
openssl req -new \
  -key server/localhost.key \
  -out server/localhost.csr \
  -subj "/C=FI/O=YourCompany/CN=localhost"

# Sign with dev CA
openssl x509 -req -days 825 \
  -in server/localhost.csr \
  -CA ca/ca.crt \
  -CAkey ca/ca.key \
  -CAcreateserial \
  -out server/localhost.crt \
  -extfile server/localhost.ext

# Verify SAN is present
openssl x509 -in server/localhost.crt -noout -text | grep -A1 'Subject Alternative'
```

### 4.3 Trust the Dev CA on Windows

Copy the CA cert to a Windows-accessible path:

```bash
# From WSL2 — copy to Windows user folder
cp ca/ca.crt /mnt/c/Users/YourUsername/Downloads/integration-dev-ca.crt
```

Then in PowerShell as Administrator on Windows:

```powershell
# Import into Trusted Root — makes localhost cert trusted by browsers and .NET
Import-Certificate `
  -FilePath "$env:USERPROFILE\Downloads\integration-dev-ca.crt" `
  -CertStoreLocation Cert:\LocalMachine\Root

# Verify
Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.Subject -like '*IntegrationAPI Dev CA*' }
```

### 4.4 Trust the Dev CA on Fedora

Fedora uses the system-wide trust store at `/etc/pki/ca-trust/`. Adding the CA there makes it trusted by `curl`, the .NET runtime, and system tools without any per-tool configuration.

```bash
# Copy CA cert into the system anchor directory
sudo cp ~/integration-ca-dev/ca/ca.crt \
  /etc/pki/ca-trust/source/anchors/integration-dev-ca.crt

# Rebuild the trust store
sudo update-ca-trust

# Verify — should print the Subject with your CA name
trust list | grep -A3 'IntegrationAPI Dev CA'
```

Confirm the trust store is updated correctly:

```bash
# Should return certificate details, not an error
openssl verify -CAfile /etc/pki/tls/certs/ca-bundle.crt \
  ~/integration-ca-dev/server/localhost.crt
```

Confirm curl trusts the CA without needing `--cacert`:

```bash
# No --cacert flag needed once the CA is trusted system-wide
curl -v \
  --cert ~/integration-ca-dev/tenants/tenant-a.crt \
  --key  ~/integration-ca-dev/tenants/tenant-a.key \
  -H 'X-Api-Key: your-tenant-a-api-key' \
  https://localhost:7443/api/v1/your-endpoint
```

To remove the CA later if needed:

```bash
sudo rm /etc/pki/ca-trust/source/anchors/integration-dev-ca.crt
sudo update-ca-trust
```

> **ℹ️ Note:** The .NET runtime on Linux uses the system trust store via OpenSSL, so once `update-ca-trust` has run your ASP.NET app will also trust the dev CA without any additional configuration.

> **ℹ️ Note:** Firefox on Fedora manages its own certificate store independently of the system trust store. If you need Firefox to trust the CA, go to **Settings → Privacy & Security → Certificates → View Certificates → Authorities → Import** and select `ca.crt`.

---

## Part 5 — Generate Tenant Client Certificates

Repeat this process for each tenant. Each tenant receives a PFX file containing their private key and certificate.

### 5.1 Create Client Cert Extension File

This is reused for all tenant certs:

```bash
cat > tenants/client.ext << 'EOF'
basicConstraints = CA:FALSE
nsCertType = client
nsComment = IntegrationAPI Client Certificate
subjectKeyIdentifier = hash
authorityKeyIdentifier = keyid,issuer
keyUsage = critical, nonRepudiation, digitalSignature, keyEncipherment
extendedKeyUsage = clientAuth
EOF
```

### 5.2 Tenant A

```bash
# Private key
openssl genrsa -out tenants/tenant-a.key 2048

# CSR
openssl req -new \
  -key tenants/tenant-a.key \
  -out tenants/tenant-a.csr \
  -subj "/C=FI/O=YourCompany/CN=TenantA/OU=IntegrationAPI"

# Sign with dev CA (2 year validity)
openssl x509 -req -days 730 \
  -in tenants/tenant-a.csr \
  -CA ca/ca.crt \
  -CAkey ca/ca.key \
  -CAcreateserial \
  -out tenants/tenant-a.crt \
  -extfile tenants/client.ext

# Package as PFX (set a strong export password when prompted)
openssl pkcs12 -export \
  -in tenants/tenant-a.crt \
  -inkey tenants/tenant-a.key \
  -out tenants/tenant-a.pfx \
  -name "TenantA IntegrationAPI Dev"

# Extract thumbprint for database seeding
openssl x509 -in tenants/tenant-a.crt -fingerprint -sha256 -noout \
  | sed 's/SHA256 Fingerprint=//' \
  | tr -d ':' \
  | tr '[:lower:]' '[:upper:]'
```

### 5.3 Tenant B

```bash
openssl genrsa -out tenants/tenant-b.key 2048

openssl req -new \
  -key tenants/tenant-b.key \
  -out tenants/tenant-b.csr \
  -subj "/C=FI/O=YourCompany/CN=TenantB/OU=IntegrationAPI"

openssl x509 -req -days 730 \
  -in tenants/tenant-b.csr \
  -CA ca/ca.crt \
  -CAkey ca/ca.key \
  -CAcreateserial \
  -out tenants/tenant-b.crt \
  -extfile tenants/client.ext

openssl pkcs12 -export \
  -in tenants/tenant-b.crt \
  -inkey tenants/tenant-b.key \
  -out tenants/tenant-b.pfx \
  -name "TenantB IntegrationAPI Dev"

openssl x509 -in tenants/tenant-b.crt -fingerprint -sha256 -noout \
  | sed 's/SHA256 Fingerprint=//' \
  | tr -d ':' \
  | tr '[:lower:]' '[:upper:]'
```

### 5.4 Seed Thumbprints into Dev Database

```sql
-- Replace GUIDs and thumbprints with your actual values
INSERT INTO Tenants (TenantId, Name, IsActive)
VALUES
  ('11111111-1111-1111-1111-111111111111', 'Tenant A', 1),
  ('22222222-2222-2222-2222-222222222222', 'Tenant B', 1);

INSERT INTO TenantCertificates (TenantId, Thumbprint, ValidFrom, ValidTo, IsActive)
VALUES
  ('11111111-...', '<TENANT_A_THUMBPRINT>', '2026-01-01', '2028-01-01', 1),
  ('22222222-...', '<TENANT_B_THUMBPRINT>', '2026-01-01', '2028-01-01', 1);
```

---

## Part 6 — Caddyfile Configuration

### 6.1 Create Working Directory

```bash
mkdir -p ~/caddy-dev/certs

# Copy certs Caddy needs
cp ~/integration-ca-dev/server/localhost.crt ~/caddy-dev/certs/
cp ~/integration-ca-dev/server/localhost.key ~/caddy-dev/certs/
cp ~/integration-ca-dev/ca/ca.crt           ~/caddy-dev/certs/
```

### 6.2 Caddyfile

```caddy
# ~/caddy-dev/Caddyfile

{
    # Disable Caddy's automatic HTTPS — we manage certs manually
    auto_https off

    # Disable OCSP — dev CA has no OCSP endpoint
    ocsp_stapling off

    # Admin API on non-default port to avoid conflicts
    admin localhost:2020
}

localhost:7443 {
    # Server TLS — our dev cert signed by dev CA
    tls /home/{$USER}/caddy-dev/certs/localhost.crt \
        /home/{$USER}/caddy-dev/certs/localhost.key {

        # Require and verify client certificates
        client_auth {
            mode       require_and_verify
            trust_pool file /home/{$USER}/caddy-dev/certs/ca.crt
        }
    }

    # Forward to ASP.NET frontend running on Windows localhost
    reverse_proxy localhost:5000 {

        # Remove any client-supplied cert headers first — prevent spoofing
        header_up -X-ARR-ClientCert

        # Pass client cert as PEM — matched by HeaderConverter in ASP.NET
        header_up X-ARR-ClientCert {tls_client_certificate_pem}

        # Standard proxy headers
        header_up X-Forwarded-Proto "https"
        header_up X-Forwarded-Host  {host}
        header_up X-Real-IP         {remote_host}
    }

    # Structured access log
    log {
        output file /home/{$USER}/caddy-dev/access.log
        format json
    }
}
```

> **ℹ️ Note:** The `header_up -X-ARR-ClientCert` line removes any client-supplied header *before* Caddy adds the real one. This prevents a caller from spoofing the cert header directly.

---

## Part 7 — ASP.NET Frontend Configuration

### 7.1 Program.cs — Certificate Forwarding

The PEM format from Caddy needs a custom converter since `AddCertificateForwarding` expects base64 DER by default. This converter handles both formats, making the app work identically behind Caddy and IIS:

```csharp
// Program.cs
builder.Services.AddCertificateForwarding(options =>
{
    options.CertificateHeader = "X-ARR-ClientCert";

    // IIS sends base64 DER — handled automatically by the else branch
    // Caddy sends PEM — needs stripping of headers
    options.HeaderConverter = headerValue =>
    {
        if (string.IsNullOrWhiteSpace(headerValue))
            return null;

        // Handle PEM format from Caddy
        if (headerValue.Contains("-----BEGIN"))
        {
            var base64 = headerValue
                .Replace("-----BEGIN CERTIFICATE-----", "")
                .Replace("-----END CERTIFICATE-----", "")
                .Replace("\n", "")
                .Replace("\r", "")
                .Trim();
            var bytes = Convert.FromBase64String(base64);
            return new X509Certificate2(bytes);
        }

        // Handle base64 DER format from IIS
        var derBytes = Convert.FromBase64String(headerValue);
        return new X509Certificate2(derBytes);
    };
});

// Must come before UseRouting
app.UseForwardedHeaders();
app.UseCertificateForwarding();
```

### 7.2 launchSettings.json

Run the app on plain HTTP — Caddy owns TLS:

```json
{
  "profiles": {
    "Development": {
      "commandName": "Project",
      "applicationUrl": "http://localhost:5000",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    }
  }
}
```

---

## Part 8 — Running the Setup

### 8.1 Start Caddy

```bash
cd ~/caddy-dev

# Validate Caddyfile first
caddy validate --config Caddyfile

# Run Caddy (stays in foreground — use a separate terminal)
caddy run --config Caddyfile
```

### 8.2 Start ASP.NET Frontend

From Visual Studio or Rider, start the Frontend project in Development mode on `http://localhost:5000`.

### 8.3 Test with curl from WSL2

```bash
# Happy path — Tenant A with correct API key
curl -v \
  --cert ~/integration-ca-dev/tenants/tenant-a.crt \
  --key  ~/integration-ca-dev/tenants/tenant-a.key \
  --cacert ~/integration-ca-dev/ca/ca.crt \
  -H 'X-Api-Key: your-tenant-a-api-key' \
  https://localhost:7443/api/v1/your-endpoint

# Rejection — no client cert (Caddy should refuse the connection)
curl -v \
  --cacert ~/integration-ca-dev/ca/ca.crt \
  https://localhost:7443/api/v1/your-endpoint

# Rejection — cross-tenant mismatch, should return 401
curl -v \
  --cert ~/integration-ca-dev/tenants/tenant-a.crt \
  --key  ~/integration-ca-dev/tenants/tenant-a.key \
  --cacert ~/integration-ca-dev/ca/ca.crt \
  -H 'X-Api-Key: tenant-b-key' \
  https://localhost:7443/api/v1/your-endpoint
```

### 8.4 Test with Postman on Windows

Copy the PFX to Windows:

```bash
cp ~/integration-ca-dev/tenants/tenant-a.pfx /mnt/c/Users/YourUsername/Downloads/
```

Import it in PowerShell:

```powershell
Import-PfxCertificate `
  -FilePath "$env:USERPROFILE\Downloads\tenant-a.pfx" `
  -CertStoreLocation Cert:\CurrentUser\My
```

In Postman: **Settings → Certificates → Add Certificate → Host: `localhost:7443`** → select the PFX and enter the export password.

---

## Part 9 — Final Directory Layout

```
~/integration-ca-dev/
  ca/
    ca.key          ← KEEP SECURE — never commit
    ca.crt          ← import to Windows, used by Caddy
    ca.srl          ← serial tracker (auto-managed)
  server/
    localhost.key   ← Caddy server key
    localhost.crt   ← Caddy server cert
    localhost.csr   ← intermediate, can discard
    localhost.ext   ← ext config
  tenants/
    client.ext      ← shared extension config
    tenant-a.key    ← keep secure
    tenant-a.crt    ← public cert — thumbprint goes in DB
    tenant-a.pfx    ← distribute to Tenant A / use in Postman
    tenant-b.key
    tenant-b.crt
    tenant-b.pfx

~/caddy-dev/
  Caddyfile
  access.log        ← generated at runtime
  certs/
    ca.crt          ← copied from integration-ca-dev
    localhost.crt   ← copied from integration-ca-dev
    localhost.key   ← copied from integration-ca-dev
```

---

## Part 10 — Quick Reference

| Component | Location | Notes |
|---|---|---|
| Caddy | WSL2 | Runs manually via `caddy run` |
| ASP.NET Frontend | Windows | HTTP only on `localhost:5000` |
| Public HTTPS | `localhost:7443` | TLS terminated by Caddy |
| Client cert header | `X-ARR-ClientCert` | PEM format, matches IIS name |
| Dev Root CA | `~/integration-ca-dev/ca/` | Trusted by Windows cert store |
| Tenant PFX files | `~/integration-ca-dev/tenants/` | For curl and Postman testing |
| Thumbprints | Dev database | Seeded per tenant after cert generation |
| Cert rotation | Add new thumbprint, keep old active | Zero downtime rotation |

---

> **⚠️ Important:** Never commit `ca.key`, `*.key`, or `*.pfx` files to version control. Add them to `.gitignore`. Only `ca.crt` and `*.crt` (public certs) are safe to commit if needed.
