using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class UserSessionRefreshTokenTests
{
    [Fact]
    public void Create_WithSessionAndHash_PreservesFamilyLookupValues()
    {
        var sessionId = Guid.NewGuid();

        var result = UserSessionRefreshToken.Create(sessionId, "hashed-token");

        result.IsSuccess.Should().BeTrue();
        result.Value.UserSessionId.Should().Be(sessionId);
        result.Value.TokenHash.Should().Be("hashed-token");
    }

    [Fact]
    public void Create_WithoutSessionOrHash_Fails()
    {
        UserSessionRefreshToken.Create(Guid.Empty, "hashed-token").IsFailure.Should().BeTrue();
        UserSessionRefreshToken.Create(Guid.NewGuid(), " ").IsFailure.Should().BeTrue();
    }
}
