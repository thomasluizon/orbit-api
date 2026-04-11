using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Orbit.Api.OAuth;

namespace Orbit.Infrastructure.Tests.Services;

public class OAuthAuthorizationStoreTests : IDisposable
{
    private readonly OAuthAuthorizationStore _store = new();

    public void Dispose()
    {
        _store.Dispose();
        GC.SuppressFinalize(this);
    }

    private static (string codeVerifier, string codeChallenge) GeneratePkce()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Convert.ToBase64String(hash)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        return (verifier, challenge);
    }

    [Fact]
    public void CreateCode_ReturnsNonEmptyCode()
    {
        var (_, challenge) = GeneratePkce();
        var code = _store.CreateCode(Guid.NewGuid(), challenge, "http://localhost", "client1");

        code.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ExchangeCode_ValidPkce_ReturnsEntry()
    {
        var userId = Guid.NewGuid();
        var (verifier, challenge) = GeneratePkce();
        var redirectUri = "http://localhost/callback";
        var clientId = "test-client";

        var code = _store.CreateCode(userId, challenge, redirectUri, clientId);
        var entry = _store.ExchangeCode(code, verifier, redirectUri);

        entry.Should().NotBeNull();
        entry!.UserId.Should().Be(userId);
        entry.RedirectUri.Should().Be(redirectUri);
        entry.ClientId.Should().Be(clientId);
    }

    [Fact]
    public void ExchangeCode_WrongVerifier_ReturnsNull()
    {
        var (_, challenge) = GeneratePkce();
        var redirectUri = "http://localhost/callback";

        var code = _store.CreateCode(Guid.NewGuid(), challenge, redirectUri, "client");
        var entry = _store.ExchangeCode(code, "wrong-verifier", redirectUri);

        entry.Should().BeNull();
    }

    [Fact]
    public void ExchangeCode_WrongRedirectUri_ReturnsNull()
    {
        var (verifier, challenge) = GeneratePkce();

        var code = _store.CreateCode(Guid.NewGuid(), challenge, "http://localhost/correct", "client");
        var entry = _store.ExchangeCode(code, verifier, "http://localhost/wrong");

        entry.Should().BeNull();
    }

    [Fact]
    public void ExchangeCode_CodeNotFound_ReturnsNull()
    {
        var entry = _store.ExchangeCode("nonexistent-code", "verifier", "http://localhost");

        entry.Should().BeNull();
    }

    [Fact]
    public void ExchangeCode_CodeUsedTwice_ReturnsNullSecondTime()
    {
        var (verifier, challenge) = GeneratePkce();
        var redirectUri = "http://localhost/callback";

        var code = _store.CreateCode(Guid.NewGuid(), challenge, redirectUri, "client");

        // First exchange succeeds
        var first = _store.ExchangeCode(code, verifier, redirectUri);
        first.Should().NotBeNull();

        // Second exchange fails (code already removed)
        var second = _store.ExchangeCode(code, verifier, redirectUri);
        second.Should().BeNull();
    }

    [Fact]
    public void CreateCode_MultipleCodesAreUnique()
    {
        var (_, challenge) = GeneratePkce();
        var code1 = _store.CreateCode(Guid.NewGuid(), challenge, "http://localhost", "client");
        var code2 = _store.CreateCode(Guid.NewGuid(), challenge, "http://localhost", "client");

        code1.Should().NotBe(code2);
    }

    [Fact]
    public void CreateCode_DoesNotContainPlusOrSlash()
    {
        var (_, challenge) = GeneratePkce();

        for (int i = 0; i < 20; i++)
        {
            var code = _store.CreateCode(Guid.NewGuid(), challenge, "http://localhost", "client");
            code.Should().NotContain("+");
            code.Should().NotContain("/");
        }
    }
}
