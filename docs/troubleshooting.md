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

The in-memory token cache was lost, typically after an app restart or recycle. User-initiated AgentPortal actions can trigger re-authentication through `[AuthorizeForScopes]`, but background polling endpoints should not force an interactive challenge. For production, implement a distributed token cache.

### "IDW10502: MsalUiRequiredException"

Same root cause as above. The user needs to re-authenticate for user-initiated actions.

### "AgentPortal keeps asking me to sign in again"

Check the AgentPortal cookie policy. `CookiePolicyOptions.MinimumSameSitePolicy` must remain `Unspecified`.

If a global minimum of `Lax` or `Strict` is forced, the OpenID Connect correlation and nonce cookies cannot use `SameSite=None`, and the Entra redirect flow will loop back to sign-in.

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

### "callback_failure" or "Unauthorized" in Microsoft Authenticator

Check the API logs for one of these callback rejections:

- invalid or missing callback token
- requestId mismatch
- invalid or missing JWT

This app authenticates callbacks with a **one-time callback token** plus the stored Verified ID `requestId`.

If you enabled `VerifiedId:RequireCallbackJwtValidation=true`, the API also requires a valid `receipt.id_token` on successful `presentation_verified` callbacks. Leave this setting `false` unless you have confirmed your tenant and wallet flow consistently includes that receipt JWT. Retrieval and error callbacks still rely on the one-time callback token plus `requestId` correlation.

Successful callback authentication is logged at Debug level as either `mode=token_requestid` or `mode=token_requestid_receiptjwt`. If you see callback rejections but never see one of those accepted modes, focus first on the callback token header, request correlation, and strict-JWT setting.

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

### "I selected Teams but the code arrived by email"

This is currently expected. Teams delivery is temporarily disabled, and any remaining `teams` requests are intentionally routed through the email notification path instead. Re-enable the Teams option in the Agent Portal and the `SendTeamsMessageAsync` path only after you are ready to turn that integration back on.

## Session Issues

### "Code expired immediately"

Fixed in this release. This was caused by a string vs `DateTimeOffset` comparison in the Azure Table Storage filter.

### "Session stuck in pending"

The `SessionExpiryService` background job runs every 2 minutes to expire stale sessions. Check that the API app is running and healthy via the `/health` endpoint.

## CORS Issues

### "CORS error in browser console"

Check that `AgentPortal:BaseUrl` and `VerifyPortal:BaseUrl` in the API app settings match the actual deployed URLs. Values must include the scheme (`https://`) and must **not** have a trailing slash.
