using Azure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using VerifiedIdHelpdesk.AgentPortal.Authorization;
using CoreConstants = VerifiedIdHelpdesk.Core.Constants;

var builder = WebApplication.CreateBuilder(args);

// ── Key Vault ──────────────────────────────────────────────────────────────
// SECURITY: All secrets (client secret, HMAC key) come from Key Vault via
// Managed Identity in production. LocalDev uses DefaultAzureCredential with
// your 'az login' credentials pointing to a dev vault.
// CUSTOMIZE: Set KeyVault:Uri in appsettings.json (or appsettings.Development.json for local dev).
builder.Configuration.AddAzureKeyVault(
    new Uri(builder.Configuration["KeyVault:Uri"]!),
    new DefaultAzureCredential());

// ── Authentication — Entra OIDC ───────────────────────────────────────────
// SECURITY: All portal pages require Entra authentication (fallback policy below).
// CUSTOMIZE: Configure your app registration in appsettings.json under AzureAd.
var apiScopes = builder.Configuration.GetSection("Api:Scopes").Get<string[]>() ?? [];
builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
    .EnableTokenAcquisitionToCallDownstreamApi(apiScopes)
    .AddMicrosoftGraph(builder.Configuration.GetSection("MicrosoftGraph"))
    .AddInMemoryTokenCaches();

// ── Authorization ──────────────────────────────────────────────────────────
// SECURITY: FallbackPolicy ensures every page requires login — no anonymous access.
// CUSTOMIZE: Add more group-based policies here for supervisor/admin roles.
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    // HelpDeskAgent policy — uses custom handler to handle group claim overage
    // (when user belongs to 200+ groups, the token omits the groups claim).
    options.AddPolicy(CoreConstants.HelpDeskAgentPolicy, policy =>
        policy.Requirements.Add(new HelpDeskAgentRequirement()));
});

// Register the custom authorization handler (scoped — needs per-request Graph calls).
builder.Services.AddScoped<IAuthorizationHandler, HelpDeskAgentHandler>();

// ── HTTP client for Backend API calls ─────────────────────────────────────
// CUSTOMIZE: The AgentPortal calls the Backend API to generate codes and poll status.
builder.Services.AddHttpClient("ApiClient", (sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(config["Api:BaseUrl"]!);
});

// ── MVC + Identity UI ─────────────────────────────────────────────────────
builder.Services.AddControllersWithViews()
    .AddMicrosoftIdentityUI();

builder.Services.AddApplicationInsightsTelemetry();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // SECURITY: HSTS enforces HTTPS for 1 year. Do not change the max-age below 31536000 in production.
    app.UseHsts();
}

// Security headers — applied before any response is written
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin");
    context.Response.Headers.Append("Content-Security-Policy",
        "default-src 'self'; script-src 'self' https://cdn.jsdelivr.net; " +
        "style-src 'self' 'unsafe-inline'; img-src 'self' data:;");
    await next();
});

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapStaticAssets();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Verification}/{action=Create}/{id?}")
    .WithStaticAssets();

// Required for Microsoft Identity Web UI (account controller)
app.MapControllerRoute(
    name: "MicrosoftIdentity",
    pattern: "MicrosoftIdentity/{controller=Account}/{action=SignIn}");

app.Run();
