using FluentAssertions;
using Orbit.Api.Extensions;

namespace Orbit.Infrastructure.Tests.Middleware;

public class McpAuthorizationTests
{
    private static bool BypassesAuthentication(string body)
    {
        using var document = WebApplicationExtensions.TryParseMcpBody(body);
        return WebApplicationExtensions.IsMcpUnauthenticatedMethod(document?.RootElement);
    }

    [Theory]
    [InlineData("{\"method\":\"Initialize\"}")]
    [InlineData("{\"method\":\"INITIALIZE\"}")]
    [InlineData("{\"method\":\"Ping\"}")]
    [InlineData("{\"method\":\"PING\"}")]
    [InlineData("{\"method\":\"Notifications/initialized\"}")]
    [InlineData("{\"method\":\"NOTIFICATIONS/cancelled\"}")]
    public void MiscasedHandshakeMethod_StillRequiresAuthentication(string body)
    {
        BypassesAuthentication(body).Should().BeFalse();
    }

    [Theory]
    [InlineData("{\"method\":\" initialize\"}")]
    [InlineData("{\"method\":\"initialize \"}")]
    [InlineData("{\"method\":\"ping\\t\"}")]
    public void WhitespacePaddedHandshakeMethod_StillRequiresAuthentication(string body)
    {
        BypassesAuthentication(body).Should().BeFalse();
    }

    [Fact]
    public void BareNotificationsWord_WithoutNamespaceSlash_RequiresAuthentication()
    {
        BypassesAuthentication("{\"method\":\"notifications\"}").Should().BeFalse();
    }

    [Theory]
    [InlineData("{\"method\":\"notifications/\"}")]
    [InlineData("{\"method\":\"notifications/progress\"}")]
    [InlineData("{\"method\":\"notifications/message/nested\"}")]
    public void NotificationsNamespaceMethod_BypassesAuthentication(string body)
    {
        BypassesAuthentication(body).Should().BeTrue();
    }

    [Theory]
    [InlineData("{\"method\":\"resources/read\"}")]
    [InlineData("{\"method\":\"resources/list\"}")]
    [InlineData("{\"method\":\"prompts/get\"}")]
    [InlineData("{\"method\":\"prompts/list\"}")]
    [InlineData("{\"method\":\"completion/complete\"}")]
    [InlineData("{\"method\":\"logging/setLevel\"}")]
    public void ProtectedMcpMethods_RequireAuthentication(string body)
    {
        BypassesAuthentication(body).Should().BeFalse();
    }
}
