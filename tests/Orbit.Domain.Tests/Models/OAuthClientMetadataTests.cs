using FluentAssertions;
using Orbit.Domain.Models;

namespace Orbit.Domain.Tests.Models;

public class OAuthClientMetadataTests
{
    private const string ClientId = "https://claude.ai/oauth/mcp-oauth-client-metadata";

    [Fact]
    public void Create_CopiesValidatedMetadata()
    {
        string[] redirects = ["https://claude.ai/api/mcp/auth_callback"];
        var metadata = OAuthClientMetadata.Create(ClientId, "Claude", redirects, "none").Value;
        redirects[0] = "https://example.com/changed";
        metadata.ClientId.Should().Be(ClientId);
        metadata.ClientName.Should().Be("Claude");
        metadata.RedirectUris.Should().Equal("https://claude.ai/api/mcp/auth_callback");
    }

    [Theory]
    [InlineData("http://claude.ai/client.json")]
    [InlineData("https://claude.ai")]
    [InlineData("https://claude.ai?path=/client.json")]
    [InlineData("https://user:password@claude.ai/client.json")]
    [InlineData("https://claude.ai/client.json#fragment")]
    [InlineData("https://claude.ai/a/../client.json")]
    [InlineData("https://claude.ai/a/%2e/client.json")]
    [InlineData("https://claude.ai/a/%2e%2e/client.json")]
    [InlineData("https://claude.ai\\client.json")]
    public void Create_RejectsInvalidClientUrl(string clientId) =>
        OAuthClientMetadata.Create(clientId, "Claude", ["https://claude.ai/callback"]).IsFailure.Should().BeTrue();

    [Theory]
    [InlineData(null, "none")]
    [InlineData("", "none")]
    [InlineData(" ", "none")]
    [InlineData("Claude", "client_secret_post")]
    [InlineData("Claude", "private_key_jwt")]
    public void Create_RejectsMissingNameAndUnsupportedAuthentication(string? name, string method) =>
        OAuthClientMetadata.Create(ClientId, name, ["https://claude.ai/callback"], method).IsFailure.Should().BeTrue();

    [Fact]
    public void Create_RejectsMissingAndInvalidRedirects()
    {
        OAuthClientMetadata.Create(ClientId, "Claude", null).IsFailure.Should().BeTrue();
        OAuthClientMetadata.Create(ClientId, "Claude", []).IsFailure.Should().BeTrue();
        OAuthClientMetadata.Create(ClientId, "Claude", ["relative"]).IsFailure.Should().BeTrue();
        OAuthClientMetadata.Create(ClientId, new string('x', 201), ["https://claude.ai/callback"]).IsFailure.Should().BeTrue();
    }
}
