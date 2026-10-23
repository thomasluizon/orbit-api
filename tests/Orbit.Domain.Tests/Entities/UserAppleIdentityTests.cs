using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class UserAppleIdentityTests
{
    [Fact]
    public void Identity_UpdatesTokenButCannotReplaceSubject()
    {
        var user = User.Create("Apple", "apple@example.com").Value;
        user.SetAppleIdentity("subject", "refresh", "native");
        user.SetAppleIdentity("subject", "new-refresh", "web");
        user.AppleRefreshToken.Should().Be("new-refresh");
        var replace = () => user.SetAppleIdentity("other", "refresh", "native");
        replace.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Identity_RevokeRequiresDeactivationAndExactToken()
    {
        var user = User.Create("Apple", "apple@example.com").Value;
        user.SetAppleIdentity("subject", "refresh", "native");
        var clear = () => user.ClearRevokedAppleToken("refresh");
        clear.Should().Throw<InvalidOperationException>();
        user.Deactivate(DateTime.UtcNow.AddDays(7));
        var wrong = () => user.ClearRevokedAppleToken("other");
        wrong.Should().Throw<InvalidOperationException>();
        clear();
        user.AppleRefreshToken.Should().BeNull();
        user.AppleSubject.Should().Be("subject");
    }

    [Theory]
    [InlineData("", "refresh", "client")]
    [InlineData("subject", "", "client")]
    [InlineData("subject", "refresh", "")]
    public void Identity_RejectsMissingCredentials(string subject, string token, string client)
    {
        var user = User.Create("Apple", "apple@example.com").Value;
        var act = () => user.SetAppleIdentity(subject, token, client);
        act.Should().Throw<ArgumentException>();
    }
}
