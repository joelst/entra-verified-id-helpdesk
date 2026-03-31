using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Identity.Web;
using System.Threading.RateLimiting;
using VerifiedIdHelpdesk.Core;
using VerifiedIdHelpdesk.Core.Interfaces;
using VerifiedIdHelpdesk.Infrastructure;
using VerifiedIdHelpdesk.Notifications;
using VerifiedIdHelpdesk.Api.Hubs;
using VerifiedIdHelpdesk.Api.Services;
using CoreConstants = VerifiedIdHelpdesk.Core.Constants;

var builder = WebApplication.CreateBuilder(args);

// Key Vault — must be first so downstream config reads secrets.
// Skipped in the Testing environment (integration tests) and when URI is not configured.
var keyVaultUri = builder.Configuration["KeyVault:Uri"];
if (!builder.Environment.IsEnvironment("Testing") && !string.IsNullOrEmpty(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
}

// Authentication — validate bearer tokens issued by Entra
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration);

// Authorization
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(CoreConstants.HelpDeskAgentPolicy, policy =>
        policy.RequireClaim("groups", builder.Configuration["AuthorizationGroups:HelpDeskAgents"]!));
});

// CORS — only allow Agent Portal and IDVerify origins
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowPortals", policy => policy
        .WithOrigins(
            builder.Configuration["AgentPortal:BaseUrl"]!,
            builder.Configuration["VerifyPortal:BaseUrl"]!)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

// SignalR
builder.Services.AddSignalR();

// Microsoft Graph — app-level identity (DefaultAzureCredential)
builder.Services.AddSingleton(_ =>
{
    var credential = new DefaultAzureCredential();
    // CUSTOMIZE: Scopes determine what Graph APIs can be called (email, Teams, directory search).
    return new Microsoft.Graph.GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
});

// Application services
builder.Services.AddHttpClient("VerifiedIdClient"); // Named HttpClient for Entra Verified ID API
builder.Services.AddSingleton<ISessionStore, AzureTableSessionStore>();
builder.Services.AddSingleton<IVerifiedIdClient, EntraVerifiedIdClient>();
builder.Services.AddSingleton<INotificationService, GraphNotificationService>();
builder.Services.AddHostedService<SessionExpiryService>();

// Application Insights — skipped in Testing environment (no connection string available).
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddApplicationInsightsTelemetry();
}

builder.Services.AddControllers();

// Health checks
builder.Services.AddHealthChecks();

// Rate limiting — protect public endpoints from abuse
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // /api/verification/initiate — 10 requests/minute per IP
    options.AddPolicy("initiate", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1)
            }));

    // /api/verification/public-status — 60 requests/minute per IP
    options.AddPolicy("public-status", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1)
            }));

    // /api/verification/callback — 30 requests/minute per IP
    options.AddPolicy("callback", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1)
            }));
});

var app = builder.Build();

app.UseCors("AllowPortals");
app.UseRateLimiter();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");
app.MapHub<VerificationHub>(CoreConstants.VerificationHubPath);
app.Run();

// Expose Program class for WebApplicationFactory in integration tests
public partial class Program { }
