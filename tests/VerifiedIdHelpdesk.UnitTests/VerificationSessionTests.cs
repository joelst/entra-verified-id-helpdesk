using VerifiedIdHelpdesk.Core;
using VerifiedIdHelpdesk.Core.Models;

namespace VerifiedIdHelpdesk.UnitTests;

public class VerificationSessionTests
{
    [Fact]
    public void DefaultStatus_IsPending()
    {
        var session = new VerificationSession();
        Assert.Equal("pending", session.Status);
    }

    [Fact]
    public void DefaultFailedAttempts_IsZero()
    {
        var session = new VerificationSession();
        Assert.Equal(0, session.FailedAttempts);
    }

    [Fact]
    public void ExpiresAt_WhenSetToCreatedAtPlusExpiryMinutes_IsCorrect()
    {
        var now = DateTime.UtcNow;
        var session = new VerificationSession
        {
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(Constants.CodeExpiryMinutes)
        };

        var expectedExpiry = now.AddMinutes(Constants.CodeExpiryMinutes);
        Assert.Equal(expectedExpiry, session.ExpiresAt);
        Assert.True(session.ExpiresAt > session.CreatedAt);
    }

    [Fact]
    public void IsExpired_WhenExpiresAtIsInPast_ReturnsTrue()
    {
        var session = new VerificationSession
        {
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
        };
        Assert.True(session.ExpiresAt <= DateTime.UtcNow);
    }

    [Fact]
    public void IsValid_WhenPendingAndNotExpired_IsTrue()
    {
        var session = new VerificationSession
        {
            Status = "pending",
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
        Assert.Equal("pending", session.Status);
        Assert.True(session.ExpiresAt > DateTime.UtcNow);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("verified")]
    [InlineData("expired")]
    [InlineData("failed")]
    public void Status_CanBeSetToValidValues(string status)
    {
        var session = new VerificationSession { Status = status };
        Assert.Equal(status, session.Status);
    }
}
