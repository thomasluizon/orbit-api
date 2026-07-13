using FluentAssertions;
using Orbit.Api.Extensions;

namespace Orbit.Infrastructure.Tests.Middleware;

/// <summary>
/// The authorization gate of the MCP selective-auth middleware: <see cref="WebApplicationExtensions"/>
/// only lets a request past authentication when its JSON-RPC <c>method</c> is on a tight allowlist
/// (the <c>initialize</c>/<c>ping</c> handshake and the side-effect-free <c>notifications/</c>
/// namespace); every other method — every tool call, resource read, or prompt fetch — must
/// authenticate. The isolated classification/parse contract is covered in
/// <see cref="Extensions.WebApplicationExtensionsMcpTests"/>; this file locks the fail-closed EDGES of
/// that allowlist so no casing trick or prefix look-alike can widen the unauthenticated surface.
/// The HTTP-level challenge itself (the 401 + WWW-Authenticate write in the middleware's private
/// authentication step) runs only inside the ASP.NET request pipeline, which the unit-only suite does
/// not host, so it is asserted here at the decision-surface level.
/// </summary>
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
