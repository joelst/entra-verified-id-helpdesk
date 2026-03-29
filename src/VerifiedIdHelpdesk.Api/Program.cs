using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using VerifiedIdHelpdesk.Core;
using VerifiedIdHelpdesk.Core.Interfaces;
using VerifiedIdHelpdesk.Infrastructure;
using VerifiedIdHelpdesk.Notifications;
using VerifiedIdHelpdesk.Api.Hubs;
using VerifiedIdHelpdesk.Api.Services;
using CoreConstants = VerifiedIdHelpdesk.Core.Constants;

var builder = WebApplication.CreateBuilder(args);

// Key Vault — must be first so downstream config reads secrets
builder.Configuration.AddAzureKeyVault(
    new Uri(builder.Configuration["KeyVault:Uri"]!),
    new DefaultAzureCredential());

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
builder.Services.AddSingleton<ISessionStore, AzureTableSessionStore>();
builder.Services.AddSingleton<IVerifiedIdClient, EntraVerifiedIdClient>();
builder.Services.AddSingleton<INotificationService, GraphNotificationService>();
builder.Services.AddHostedService<SessionExpiryService>();
builder.Services.AddApplicationInsightsTelemetry();

builder.Services.AddControllers();

var app = builder.Build();

app.UseCors("AllowPortals");
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<VerificationHub>(CoreConstants.VerificationHubPath);
app.Run();
