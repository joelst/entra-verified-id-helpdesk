using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VerifiedIdHelpdesk.Core;
using VerifiedIdHelpdesk.Core.Interfaces;
using VerifiedIdHelpdesk.Core.Models;

namespace VerifiedIdHelpdesk.Infrastructure;

public class AzureTableSessionStore : ISessionStore
{
    private readonly TableClient _table;
    private readonly ILogger<AzureTableSessionStore> _logger;

    public AzureTableSessionStore(IConfiguration config, ILogger<AzureTableSessionStore> logger)
    {
        _logger = logger;
        var accountUri = config["Storage:AccountUri"]
            ?? throw new InvalidOperationException("Storage:AccountUri is not configured.");

        _table = new TableClient(
            new Uri(accountUri),
            "VerificationSessions",
            new DefaultAzureCredential());

        _table.CreateIfNotExists();
    }

    public async Task<VerificationSession> CreateAsync(VerificationSession session)
    {
        var entity = ToEntity(session);
        await _table.AddEntityAsync(entity);
        return session;
    }

    public async Task<VerificationSession?> GetAsync(string sessionId)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(
                Constants.SessionPartitionKey, sessionId);
            return FromEntity(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<VerificationSession?> GetByRequestIdAsync(string requestId)
    {
        var filter = TableClient.CreateQueryFilter(
            $"PartitionKey eq {Constants.SessionPartitionKey} and RequestId eq {requestId}");

        await foreach (var entity in _table.QueryAsync<TableEntity>(filter: filter, maxPerPage: 1))
            return FromEntity(entity);

        return null;
    }

    public async Task<VerificationSession?> GetByCodeHashAsync(string codeHash, string callerEmail)
    {
        var filter = TableClient.CreateQueryFilter(
            $"PartitionKey eq {Constants.SessionPartitionKey} and CodeHash eq {codeHash} and CallerEmail eq {callerEmail}");

        await foreach (var entity in _table.QueryAsync<TableEntity>(filter: filter, maxPerPage: 1))
            return FromEntity(entity);

        return null;
    }

    public async Task UpdateAsync(VerificationSession session)
    {
        var entity = ToEntity(session);
        await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace);
    }

    public async Task<int> CountPendingByAgentAsync(string agentEntraId)
    {
        var filter = TableClient.CreateQueryFilter(
            $"PartitionKey eq {Constants.SessionPartitionKey} and AgentEntraId eq {agentEntraId} and Status eq {"pending"}");

        int count = 0;
        await foreach (var _ in _table.QueryAsync<TableEntity>(filter: filter))
            count++;

        return count;
    }

    public async Task<int> ExpireOldSessionsAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var filter = TableClient.CreateQueryFilter(
            $"PartitionKey eq {Constants.SessionPartitionKey} and Status eq {"pending"} and ExpiresAt le {now}");

        var expiredSessions = new List<TableEntity>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(filter: filter))
            expiredSessions.Add(entity);

        foreach (var entity in expiredSessions)
        {
            entity["Status"] = "expired";
            await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace);
            _logger.LogInformation("code_expired {@Event}", new
            {
                EventName = "code_expired",
                SessionId = entity.RowKey
            });
        }

        return expiredSessions.Count;
    }

    private static TableEntity ToEntity(VerificationSession s) => new(Constants.SessionPartitionKey, s.SessionId)
    {
        ["CodeHash"] = s.CodeHash,
        ["CallerEmail"] = s.CallerEmail,
        ["CallerEntraId"] = s.CallerEntraId,
        ["CallerDisplayName"] = s.CallerDisplayName,
        ["TicketId"] = s.TicketId,
        ["Note"] = s.Note,
        ["AgentEntraId"] = s.AgentEntraId,
        ["AgentDisplayName"] = s.AgentDisplayName,
        ["DeliveryChannel"] = s.DeliveryChannel,
        ["Status"] = s.Status,
        ["VerifiedClaims"] = s.VerifiedClaims,
        ["RequestId"] = s.RequestId,
        ["FailedAttempts"] = s.FailedAttempts,
        ["CreatedAt"] = s.CreatedAt,
        ["ExpiresAt"] = s.ExpiresAt,
        ["VerifiedAt"] = s.VerifiedAt
    };

    private static VerificationSession FromEntity(TableEntity e) => new()
    {
        SessionId = e.RowKey,
        CodeHash = e.GetString("CodeHash") ?? string.Empty,
        CallerEmail = e.GetString("CallerEmail") ?? string.Empty,
        CallerEntraId = e.GetString("CallerEntraId") ?? string.Empty,
        CallerDisplayName = e.GetString("CallerDisplayName") ?? string.Empty,
        TicketId = e.GetString("TicketId") ?? string.Empty,
        Note = e.GetString("Note") ?? string.Empty,
        AgentEntraId = e.GetString("AgentEntraId") ?? string.Empty,
        AgentDisplayName = e.GetString("AgentDisplayName") ?? string.Empty,
        DeliveryChannel = e.GetString("DeliveryChannel") ?? string.Empty,
        Status = e.GetString("Status") ?? "pending",
        VerifiedClaims = e.GetString("VerifiedClaims"),
        RequestId = e.GetString("RequestId"),
        FailedAttempts = e.GetInt32("FailedAttempts") ?? 0,
        CreatedAt = e.GetDateTime("CreatedAt") ?? DateTime.UtcNow,
        ExpiresAt = e.GetDateTime("ExpiresAt") ?? DateTime.UtcNow,
        VerifiedAt = e.GetDateTime("VerifiedAt")
    };
}
