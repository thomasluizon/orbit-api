using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Api.OAuth;

namespace Orbit.Infrastructure.Tests.Services;

public class OAuthAuthorizationStoreTests : IDisposable
{
    private readonly OAuthAuthorizationStore _store = new(NullLogger<OAuthAuthorizationStore>.Instance);

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

        var first = _store.ExchangeCode(code, verifier, redirectUri);
        first.Should().NotBeNull();

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

    [Fact]
    public void ExchangeCode_RejectsCodeCreatedWithDifferentRedirectUri()
    {
        var (verifier, challenge) = GeneratePkce();

        var code = _store.CreateCode(Guid.NewGuid(), challenge, "https://claude.ai/callback", "client");
        var entry = _store.ExchangeCode(code, verifier, "https://attacker.com/callback");

        entry.Should().BeNull();
    }

    [Fact]
    public void ExchangeCode_RejectsRedirectUriAttemptToOpenRedirector()
    {
        var (verifier, challenge) = GeneratePkce();
        var attackerUrl = "https://internal-api.local/redirect?url=https://attacker.com";

        var code = _store.CreateCode(Guid.NewGuid(), challenge, "https://claude.ai/callback", "client");
        var entry = _store.ExchangeCode(code, verifier, attackerUrl);

        entry.Should().BeNull();
    }

    [Fact]
    public void ExchangeCode_EnforcesExactRedirectUriMatch()
    {
        var (verifier, challenge) = GeneratePkce();
        var originalUrl = "https://claude.ai/callback?state=abc123";

        var similar = "https://claude.ai/callback?state=abc124";

        var mismatchCode = _store.CreateCode(Guid.NewGuid(), challenge, originalUrl, "client");
        _store.ExchangeCode(mismatchCode, verifier, similar).Should().BeNull();

        var exactCode = _store.CreateCode(Guid.NewGuid(), challenge, originalUrl, "client");
        _store.ExchangeCode(exactCode, verifier, originalUrl).Should().NotBeNull();
    }

    [Fact]
    public void ExchangeCode_StoresCompleteRedirectUri()
    {
        var (verifier, challenge) = GeneratePkce();
        var fullUrl = "https://claude.ai/callback?client_id=abc&state=xyz";

        var code = _store.CreateCode(Guid.NewGuid(), challenge, fullUrl, "client");
        var entry = _store.ExchangeCode(code, verifier, fullUrl);

        entry.Should().NotBeNull();
        entry!.RedirectUri.Should().Be(fullUrl);
    }

    [Fact]
    public void ExchangeCode_BindsNonceToCode_AndReturnsItVerbatim()
    {
        var (verifier, challenge) = GeneratePkce();
        var redirectUri = "https://claude.ai/callback";
        var nonce = "client-nonce-" + Guid.NewGuid().ToString("N");

        var code = _store.CreateCode(Guid.NewGuid(), challenge, redirectUri, "client", nonce);
        var entry = _store.ExchangeCode(code, verifier, redirectUri);

        entry.Should().NotBeNull();
        entry!.Nonce.Should().Be(nonce);
    }

    [Fact]
    public void ExchangeCode_WithoutNonce_LeavesNonceNull()
    {
        var (verifier, challenge) = GeneratePkce();
        var redirectUri = "https://claude.ai/callback";

        var code = _store.CreateCode(Guid.NewGuid(), challenge, redirectUri, "client");
        var entry = _store.ExchangeCode(code, verifier, redirectUri);

        entry.Should().NotBeNull();
        entry!.Nonce.Should().BeNull();
    }
}
