using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class OAuthClientTests
{
    [Fact]
    public void MarkAuthorized_PreservesFirstCompletion()
    {
        var client = OAuthClient.Create("Claude", ["https://claude.ai/api/mcp/auth_callback"]);
        var authorizedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        client.MarkAuthorized(authorizedAtUtc);
        client.MarkAuthorized(authorizedAtUtc.AddHours(1));
        client.AuthorizedAtUtc.Should().Be(authorizedAtUtc);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void MarkAuthorized_RequiresUtc(DateTimeKind kind)
    {
        var client = OAuthClient.Create("Claude", ["https://claude.ai/api/mcp/auth_callback"]);
        var act = () => client.MarkAuthorized(new DateTime(2026, 1, 1, 0, 0, 0, kind));
        act.Should().Throw<ArgumentException>();
        client.AuthorizedAtUtc.Should().BeNull();
    }
}
