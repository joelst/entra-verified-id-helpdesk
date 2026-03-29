using VerifiedIdHelpdesk.Core.Models;

namespace VerifiedIdHelpdesk.Core.Interfaces;

/// <summary>
/// Persistent store for verification sessions. Implemented by <c>AzureTableSessionStore</c>.
/// </summary>
public interface ISessionStore
{
    /// <summary>Creates a new session and persists it.</summary>
    Task<VerificationSession> CreateAsync(VerificationSession session);

    /// <summary>Retrieves a session by its unique ID. Returns null if not found.</summary>
    Task<VerificationSession?> GetAsync(string sessionId);

    /// <summary>
    /// Retrieves a session by its code hash and caller email.
    /// Used during code validation — looks up the session without knowing the session ID.
    /// </summary>
    Task<VerificationSession?> GetByCodeHashAsync(string codeHash, string callerEmail);

    /// <summary>
    /// Retrieves a session by its Entra Verified ID request ID.
    /// Used to correlate webhook callbacks from the Verified ID service.
    /// </summary>
    Task<VerificationSession?> GetByRequestIdAsync(string requestId);

    /// <summary>Updates an existing session (all fields).</summary>
    Task UpdateAsync(VerificationSession session);

    /// <summary>
    /// Returns the count of "pending" sessions for the given agent.
    /// Used to enforce <see cref="Constants.MaxPendingSessionsPerAgent"/>.
    /// </summary>
    Task<int> CountPendingByAgentAsync(string agentEntraId);

    /// <summary>
    /// Sets the status of all sessions that are "pending" and past their ExpiresAt to "expired".
    /// Called by the background <c>SessionExpiryService</c> every 2 minutes.
    /// </summary>
    Task<int> ExpireOldSessionsAsync();
}
