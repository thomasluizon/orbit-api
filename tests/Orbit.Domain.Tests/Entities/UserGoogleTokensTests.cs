using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class UserGoogleTokensTests
{
    [Fact]
    public void SetGoogleTokens_PreservesRefreshTokenWhenAbsent()
    {
        var user = User.Create("User", "user@example.com").Value;
        user.SetGoogleTokens("first", "refresh");

        user.SetGoogleTokens("second", null);

        user.GoogleAccessToken.Should().Be("second");
        user.GoogleRefreshToken.Should().Be("refresh");
    }

    [Fact]
    public void SetGoogleTokens_RejectsBlankAccessToken()
    {
        var user = User.Create("User", "user@example.com").Value;

        var act = () => user.SetGoogleTokens(" ", "refresh");

        act.Should().Throw<ArgumentException>();
        user.GoogleAccessToken.Should().BeNull();
    }
}
