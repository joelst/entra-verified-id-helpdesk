namespace VerifiedIdHelpdesk.Core.Exceptions;

/// <summary>Base exception for all verification-related errors.</summary>
public class VerificationException : Exception
{
    public VerificationException(string message) : base(message) { }
    public VerificationException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when a code submission does not match any active session,
/// or the session has expired or been locked.
/// SECURITY: Do not expose the specific reason to end users — use a generic message.
/// </summary>
public class InvalidCodeException : VerificationException
{
    public InvalidCodeException() : base("The provided code is invalid or has expired.") { }
}

/// <summary>
/// Thrown when the maximum number of failed code attempts is exceeded.
/// The agent must generate a new code.
/// </summary>
public class MaxAttemptsExceededException : VerificationException
{
    public MaxAttemptsExceededException()
        : base($"Maximum failed attempts exceeded. Please ask the agent to generate a new code.") { }
}

/// <summary>
/// Thrown when an agent tries to create more than <see cref="Constants.MaxPendingSessionsPerAgent"/> sessions.
/// </summary>
public class TooManyPendingSessionsException : VerificationException
{
    public TooManyPendingSessionsException()
        : base($"You have too many pending verification sessions. Please wait for them to expire or be completed.") { }
}
