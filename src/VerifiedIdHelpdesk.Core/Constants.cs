namespace VerifiedIdHelpdesk.Core;

/// <summary>
/// Application-wide constants. Modify here to customize limits and policy names.
/// </summary>
public static class Constants
{
    // ── Code generation ──────────────────────────────────────────────────────

    /// <summary>
    /// Character set used for code generation. Excludes confusable characters (0/O, 1/I/L).
    /// CUSTOMIZE: Adjust if you need a different character set.
    /// </summary>
    public const string CodeCharset = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>
    /// Number of characters in the generated code (displayed as XXXX-XXXX).
    /// SECURITY: Reducing below 8 significantly weakens brute-force resistance.
    /// </summary>
    public const int CodeLength = 8;

    /// <summary>
    /// Minutes until the code expires after generation.
    /// CUSTOMIZE: Adjust based on your helpdesk workflow. 10 minutes is the recommended default.
    /// </summary>
    public const int CodeExpiryMinutes = 10;

    // ── Rate limiting ─────────────────────────────────────────────────────────

    /// <summary>
    /// Maximum failed code entry attempts before the session is locked (status = "failed").
    /// SECURITY: After this limit, the agent must generate a new code. Prevents brute force.
    /// </summary>
    public const int MaxFailedAttempts = 5;

    /// <summary>
    /// Maximum number of simultaneously "pending" sessions per agent.
    /// SECURITY: Prevents a single agent from flooding the system with pending sessions.
    /// </summary>
    public const int MaxPendingSessionsPerAgent = 3;

    // ── Storage ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Azure Table Storage partition key for all verification sessions.
    /// CUSTOMIZE: Change if you need multiple partition strategies.
    /// </summary>
    public const string SessionPartitionKey = "VerificationSession";

    /// <summary>
    /// Name of the Azure Storage Table that stores verification sessions.
    /// </summary>
    public const string SessionTableName = "VerificationSessions";

    // ── Authorization ─────────────────────────────────────────────────────────

    /// <summary>
    /// ASP.NET Core authorization policy name for helpdesk agents.
    /// CUSTOMIZE: Use this name in [Authorize(Policy = ...)] attributes.
    /// </summary>
    public const string HelpDeskAgentPolicy = "HelpDeskAgent";

    // ── SignalR ────────────────────────────────────────────────────────────────

    /// <summary>
    /// URL path for the SignalR hub. Must match in both the API server and JavaScript client.
    /// </summary>
    public const string VerificationHubPath = "/hubs/verification";

    // ── Notifications ──────────────────────────────────────────────────────────

    /// <summary>Delivery channel identifier for email.</summary>
    public const string ChannelEmail = "email";

    /// <summary>Delivery channel identifier for Microsoft Teams.</summary>
    public const string ChannelTeams = "teams";

    /// <summary>Delivery channel identifier for SMS (not yet implemented).</summary>
    public const string ChannelSms = "sms";
}
