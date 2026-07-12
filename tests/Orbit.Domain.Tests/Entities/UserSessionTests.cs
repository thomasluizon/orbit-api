using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class UserSessionTests
{
    [Fact]
    public void Rotate_ActiveSession_UpdatesTokenExpiryAndLastUsed()
    {
        var session = UserSession.Create(Guid.NewGuid(), "old-hash", DateTime.UtcNow.AddDays(1)).Value;
        var rotatedAt = DateTime.UtcNow.AddMinutes(5);
        var newExpiry = DateTime.UtcNow.AddDays(30);

        var result = session.Rotate("new-hash", newExpiry, rotatedAt);

        result.IsSuccess.Should().BeTrue();
        session.TokenHash.Should().Be("new-hash");
        session.ExpiresAtUtc.Should().Be(newExpiry);
        session.LastUsedAtUtc.Should().Be(rotatedAt);
    }

    [Fact]
    public void Rotate_NonExpiringSession_Succeeds()
    {
        var session = UserSession.Create(Guid.NewGuid(), "old-hash", null).Value;

        var result = session.Rotate("new-hash", null, DateTime.UtcNow);

        result.IsSuccess.Should().BeTrue();
        session.TokenHash.Should().Be("new-hash");
    }

    [Fact]
    public void Rotate_RevokedSession_FailsAndLeavesTokenUntouched()
    {
        var session = UserSession.Create(Guid.NewGuid(), "old-hash", DateTime.UtcNow.AddDays(1)).Value;
        session.Revoke(DateTime.UtcNow);

        var result = session.Rotate("new-hash", DateTime.UtcNow.AddDays(30), DateTime.UtcNow);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("SESSION_NOT_ACTIVE");
        session.TokenHash.Should().Be("old-hash");
    }

    [Fact]
    public void Rotate_ExpiredSession_Fails()
    {
        var session = UserSession.Create(Guid.NewGuid(), "old-hash", DateTime.UtcNow.AddDays(-1)).Value;

        var result = session.Rotate("new-hash", DateTime.UtcNow.AddDays(30), DateTime.UtcNow);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("SESSION_NOT_ACTIVE");
        session.TokenHash.Should().Be("old-hash");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rotate_BlankNewTokenHash_Fails(string newTokenHash)
    {
        var session = UserSession.Create(Guid.NewGuid(), "old-hash", DateTime.UtcNow.AddDays(1)).Value;

        var result = session.Rotate(newTokenHash, DateTime.UtcNow.AddDays(30), DateTime.UtcNow);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("TOKEN_HASH_REQUIRED");
        session.TokenHash.Should().Be("old-hash");
    }
}
