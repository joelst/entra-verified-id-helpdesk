using VerifiedIdHelpdesk.Core.Models;

namespace VerifiedIdHelpdesk.UnitTests;

/// <summary>
/// Unit tests for <see cref="SessionStatus"/> constants.
///
/// These tests guard against accidental string value changes. The status strings
/// are persisted to Azure Table Storage and compared in multiple places (controller,
/// background service, callback handler). Changing a value without updating all
/// consumers would silently break the verification flow.
/// </summary>
public class SessionStatusTests
{
    [Fact]
    public void Pending_ConstantValue_IsPending()
    {
        Assert.Equal("pending", SessionStatus.Pending);
    }

    [Fact]
    public void Verified_ConstantValue_IsVerified()
    {
        Assert.Equal("verified", SessionStatus.Verified);
    }

    [Fact]
    public void Expired_ConstantValue_IsExpired()
    {
        Assert.Equal("expired", SessionStatus.Expired);
    }

    [Fact]
    public void Failed_ConstantValue_IsFailed()
    {
        Assert.Equal("failed", SessionStatus.Failed);
    }

    /// <summary>
    /// Ensures all four status values are distinct — a duplicate would make status
    /// transitions ambiguous.
    /// </summary>
    [Fact]
    public void AllStatusValues_AreDistinct()
    {
        var statuses = new[]
        {
            SessionStatus.Pending,
            SessionStatus.Verified,
            SessionStatus.Expired,
            SessionStatus.Failed
        };
        Assert.Equal(statuses.Length, statuses.Distinct().Count());
    }
}
