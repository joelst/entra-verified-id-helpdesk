# Secrets Rotation Guide

Procedures for rotating secrets and certificates used by the Verified ID Helpdesk application.

## HMAC Key Rotation

The HMAC key is used to hash verification codes before storing them. Rotating it invalidates all pending codes.

1. **Generate a new key:**

   ```powershell
   [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
   ```

2. **Set the new key in Key Vault:**

   ```bash
   az keyvault secret set --vault-name <kv> --name HmacKey --value "<new-key>"
   ```

3. **Restart all API app instances:**

   ```bash
   az webapp restart --name <api-app> --resource-group <rg>
   ```

4. **Impact:** All pending verification codes become invalid — they were hashed with the old key. Active sessions will fail code validation. Plan rotation during low-usage windows.

5. **Rollback:** Set the old key back in Key Vault and restart the API app.

## Client Certificate Renewal

The client certificate is used for Entra ID application authentication.

1. **Check current certificate expiry:**

   ```bash
   az keyvault certificate show --vault-name <kv> --name EntraClientCert --query attributes.expires
   ```

2. **Generate and upload a new certificate:**

   ```powershell
   ./scripts/Set-AppCertificate.ps1 -KeyVaultName <kv> -CertName EntraClientCert -AppId <client-id>
   ```

   The script updates Key Vault and the Entra app registration automatically.

3. **Restart apps:**

   ```bash
   az webapp restart --name <api-app> --resource-group <rg>
   az webapp restart --name <agent-portal-app> --resource-group <rg>
   ```

4. **Impact:** Minimal — the new certificate is picked up on restart. The old certificate remains valid until its expiry date.

5. **Schedule:** Renew at least 30 days before expiry. The default certificate lifetime is 12 months.

## Emergency: Compromised HMAC Key

If you suspect the HMAC key has been compromised:

1. **Immediately** rotate the key (see [HMAC Key Rotation](#hmac-key-rotation) above).

2. **Expire all pending sessions** — query Azure Table Storage for sessions with `Status eq 'pending'` and set their status to `expired`.

3. **Review audit logs** in Application Insights for suspicious `verification_completed` events.

4. **Notify affected agents** to re-verify any recently verified callers.

## Emergency: Compromised Certificate

If you suspect the client certificate has been compromised:

1. **Revoke the old certificate** in the Entra admin center (App Registrations → Certificates & secrets → Certificates).

2. **Generate and upload a new certificate** (see [Client Certificate Renewal](#client-certificate-renewal) above).

3. **Restart all apps.**

4. **Review sign-in logs** in the Entra admin center for unauthorized token acquisitions using the compromised certificate.
