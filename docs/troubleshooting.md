# Troubleshooting Guide

Common issues and solutions organized by category.

## Deployment Issues

### "Key Vault access denied"

Check managed identity role assignments. The app requires both **Key Vault Secrets User** and **Key Vault Certificate User** roles.

```bash
# Run the provided script to grant permissions
./scripts/Grant-ManagedIdentityPermissions.ps1
```

### "TypeLoadException on startup"

NuGet package version mismatch. Run `dotnet restore` and ensure all projects target the same .NET version.

```bash
dotnet restore VerifiedIdHelpdesk.slnx
```

### "Oryx build failed"

Check that the `repoUrl` and `repoBranch` Bicep parameters point to a valid repository and branch. Verify the GitHub repo is public or that a deploy token is configured.

## Authentication Issues

### "MsalUiRequiredException: user_null"

The in-memory token cache was lost (typically after an app restart). The `[AuthorizeForScopes]` attribute handles re-authentication automatically. For production, implement a distributed token cache (Phase 4.3).

### "IDW10502: MsalUiRequiredException"

Same root cause as above. The user needs to re-authenticate.

### "401 Unauthorized on API calls"

Check that `Api:Scopes` matches the exposed API scope on the app registration. Verify admin consent has been granted.

### "Access Denied on Agent Portal"

The user is not in the HelpDeskAgents security group. Check that `AuthorizationGroups:HelpDeskAgents` matches the group's Object ID.

If the user is in 200+ groups, the token groups claim triggers overage handling. The `HelpDeskAgentHandler` falls back to the Graph API — ensure the `GroupMember.Read.All` application permission is granted and admin-consented.

## Verified ID Issues

### "VerifiedId API returned NotFound"

Check that `VerifiedId:DidAuthority` matches your Entra Verified ID setup. Verify that Verified ID is enabled in the Entra admin center.

### "Callback not received"

Ensure the API's public URL is reachable from the internet. For local development, use a tunnel (`devtunnel`, `ngrok`). Check that the callback URL doesn't have a trailing slash mismatch.

### "QR code not scanning"

Ensure Microsoft Authenticator is up to date. Check that the credential type in `VerifiedId:CredentialType` matches what's configured in Entra.

## Graph API Issues

### "Insufficient privileges"

Admin consent is required. Run:

```bash
az ad app permission admin-consent --id <client-id>
```

### "Directory search returns empty"

Check that the `User.Read.All` application permission is granted and admin-consented. The directory search uses the `$search` query parameter which requires the `ConsistencyLevel: eventual` header (already implemented in `EntraVerifiedIdClient`).

### "Email not sent"

Check that the `Mail.Send` permission is granted and admin-consented. Verify that `Notifications:SenderEmail` is a valid mailbox UPN.

## Session Issues

### "Code expired immediately"

Fixed in this release. This was caused by a string vs `DateTimeOffset` comparison in the Azure Table Storage filter.

### "Session stuck in pending"

The `SessionExpiryService` background job runs every 2 minutes to expire stale sessions. Check that the API app is running and healthy via the `/health` endpoint.

## CORS Issues

### "CORS error in browser console"

Check that `AgentPortal:BaseUrl` and `VerifyPortal:BaseUrl` in the API app settings match the actual deployed URLs. Values must include the scheme (`https://`) and must **not** have a trailing slash.
