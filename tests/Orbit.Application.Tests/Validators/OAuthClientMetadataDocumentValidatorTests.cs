using FluentAssertions;
using Orbit.Application.Auth.Models;
using Orbit.Application.Auth.Validators;

namespace Orbit.Application.Tests.Validators;

public class OAuthClientMetadataDocumentValidatorTests
{
    private readonly OAuthClientMetadataDocumentValidator _validator = new();
    private static OAuthClientMetadataDocument Valid => new("https://claude.ai/oauth/mcp-oauth-client-metadata",
        "Claude", ["https://claude.ai/api/mcp/auth_callback"], "none");

    [Fact]
    public void Validate_AcceptsPublicClient() => _validator.Validate(Valid).IsValid.Should().BeTrue();

    [Fact]
    public void Validate_RejectsInvalidMetadata()
    {
        foreach (var document in new[]
        {
            Valid with { ClientId = "http://claude.ai/client.json" },
            Valid with { ClientName = null }, Valid with { ClientName = new string('x', 201) },
            Valid with { RedirectUris = null }, Valid with { RedirectUris = [] },
            Valid with { RedirectUris = ["relative"] }, Valid with { TokenEndpointAuthMethod = "client_secret_basic" }
        })
            _validator.Validate(document).IsValid.Should().BeFalse();
    }
}
