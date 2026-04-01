# Fork and Deploy Your Own Instance

This guide walks you through forking this repository, customizing it, and deploying from your own GitHub account. This gives you full control over the source code and deployment pipeline.

## Why Fork?

- Control exactly what code runs in your environment
- Make customizations (branding, credential types, notification templates)
- Use your own CI/CD pipeline with GitHub Actions
- Review and approve all updates before deploying

## Step 1: Fork the Repository

1. Go to [github.com/joelst/entra-verified-id-helpdesk](https://github.com/joelst/entra-verified-id-helpdesk)
2. Click **Fork** → choose your GitHub org or personal account
3. Clone your fork locally:
   ```bash
   git clone https://github.com/<your-org>/entra-verified-id-helpdesk.git
   cd entra-verified-id-helpdesk
   ```

## Step 2: Update the Deploy to Azure Button

The README contains a "Deploy to Azure" button that points to the original repo. Update it to point to your fork.

In `README.md`, find the Deploy to Azure badge (near the top) and replace the template URLs:

**Original:**
```
https://raw.githubusercontent.com/joelst/entra-verified-id-helpdesk/main/infra/azuredeploy.json
```

**Your fork:**
```
https://raw.githubusercontent.com/<your-org>/entra-verified-id-helpdesk/main/infra/azuredeploy.json
```

Do the same for the `createUIDefinitionUri`.

**Tip:** Use this URL pattern to generate your button:
```
https://portal.azure.com/#create/Microsoft.Template/uri/<url-encoded-template-url>/createUIDefinitionUri/<url-encoded-ui-definition-url>
```

## Step 3: Update Deployment Source

The Bicep template has two parameters that control where App Service pulls source code from:

| Parameter    | Default                                                | Change to                                                  |
| ------------ | ------------------------------------------------------ | ---------------------------------------------------------- |
| `repoUrl`    | `https://github.com/joelst/entra-verified-id-helpdesk` | `https://github.com/<your-org>/entra-verified-id-helpdesk` |
| `repoBranch` | `main`                                                 | Your preferred branch (e.g., `main`, `production`)         |

You can set these during deployment in the Azure Portal UI, or update the defaults in `infra/main.bicep`:

```bicep
@description('GitHub repository URL for source deployment')
param repoUrl string = 'https://github.com/<your-org>/entra-verified-id-helpdesk'

@description('Branch to deploy from')
param repoBranch string = 'main'
```

## Step 4: Common Customizations

### Branding
- **Logo**: Replace `src/VerifiedIdHelpdesk.AgentPortal/wwwroot/images/logo.svg` and `src/VerifiedIdHelpdesk.VerifyPortal/wwwroot/images/logo.svg`
- **Colors**: Edit CSS variables in `src/VerifiedIdHelpdesk.AgentPortal/wwwroot/css/theme.css` (`:root` section)
- **App titles**: Update `_Layout.cshtml` in both portals
- **Screenshots in docs**: If you are shipping a branded fork, replace the images under `/images` so the README matches your UI and tenant branding

### Credential Type
- Change `VerifiedId:CredentialType` in app settings to match your Entra Verified ID credential
- Update `VerifiedId:DidAuthority` to your organization's DID

### Notification Templates
- Email HTML template: `src/VerifiedIdHelpdesk.Notifications/GraphNotificationService.cs` in `SendEmailAsync`
- Teams message: Same file, `SendTeamsMessageAsync`
- The caller-facing portal address comes from `VerifyPortal:BaseUrl`; keep that app setting correct in both the **Api** and **AgentPortal** so verbal instructions, copy buttons, and notifications all point to your branded portal.

### Code Format
- Code length, charset, expiry: Edit `src/VerifiedIdHelpdesk.Core/Constants.cs`

## Step 5: Set Up GitHub Actions (Optional)

Your fork includes CI workflows that run automatically:

| Workflow          | File                                      | Trigger                    |
| ----------------- | ----------------------------------------- | -------------------------- |
| Build & Test      | `.github/workflows/build.yml`             | Push to `main`, `dev`; PRs |
| Dependency Review | `.github/workflows/dependency-review.yml` | PRs to `main`, `dev`       |

Dependabot is configured in `.github/dependabot.yml` to scan NuGet packages and GitHub Actions weekly.

**To enable:**
1. Go to your fork's **Settings → Actions → General**
2. Select "Allow all actions and reusable workflows"
3. Go to **Settings → Code security → Dependabot** and enable Dependabot alerts + security updates

## Step 6: Deploy

Follow the main [README deployment guide](../README.md#quick-start-deploy-to-azure) — it works the same way, just pulling from your fork instead of the original.

**Azure CLI quick deploy from your fork:**
```bash
az deployment group create \
  --resource-group <your-rg> \
  --template-file infra/main.bicep \
  --parameters suffix=<your-suffix> \
    tenantId=<your-tenant-id> \
    didAuthority='did:web:<your-domain>' \
    senderEmail=<sender@yourdomain.com> \
    senderUserId=<sender-object-id> \
    repoUrl='https://github.com/<your-org>/entra-verified-id-helpdesk' \
    repoBranch=main
```

## Staying Up to Date

To pull updates from the original repo into your fork:

```bash
# Add the original repo as upstream (one-time)
git remote add upstream https://github.com/joelst/entra-verified-id-helpdesk.git

# Fetch and merge updates
git fetch upstream
git merge upstream/main

# Resolve any conflicts, test, then push
git push origin main
```

**Tip:** Review changes before merging — use `git diff main upstream/main` to see what changed.

## See Also

- [Configuration Reference](app-settings.md) — all app settings explained
- [README](../README.md) — full setup and deployment guide
- [Security Model](../README.md#security-model) — security architecture details
