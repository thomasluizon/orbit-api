using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class ApiKeyTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();

    [Theory]
    [InlineData("Claude.ai")]
    [InlineData("claude.ai")]
    [InlineData("CLAUDE.AI")]
    [InlineData("  cLaUdE.aI  ")]
    [InlineData("\tClaude.ai\r\n")]
    public void Create_ManualConnectorName_ReturnsTypedFailure(string name)
    {
        var result = ApiKey.Create(ValidUserId, name);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("API_KEY_NAME_RESERVED");
    }

    [Fact]
    public void Create_OAuthConnectorName_RemainsAvailableToOAuthIssuer()
    {
        var familyId = Guid.NewGuid();
        var result = ApiKey.Create(ValidUserId, "Claude.ai", oauthFamilyId: familyId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Entity.Name.Should().Be("Claude.ai");
        result.Value.Entity.OAuthFamilyId.Should().Be(familyId);
    }

    [Fact]
    public void Create_ValidatesAndPreservesOAuthClientIdentity()
    {
        const string clientId = "https://claude.ai/oauth/mcp-oauth-client-metadata";
        var family = Guid.NewGuid();
        ApiKey.Create(ValidUserId, "OAuth", oauthClientId: clientId).IsFailure.Should().BeTrue();
        ApiKey.Create(ValidUserId, "OAuth", oauthFamilyId: family, oauthClientId: " ").IsFailure.Should().BeTrue();
        ApiKey.Create(ValidUserId, "OAuth", oauthFamilyId: family, oauthClientId: new string('x', 1025)).IsFailure.Should().BeTrue();
        ApiKey.Create(ValidUserId, "OAuth", oauthFamilyId: family, oauthClientId: "http://client.example/metadata").IsFailure.Should().BeTrue();
        ApiKey.Create(ValidUserId, "OAuth", oauthFamilyId: family, oauthClientId: clientId).Value.Entity.OAuthClientId.Should().Be(clientId);
        ApiKey.Create(ValidUserId, "OAuth").Value.Entity.OAuthClientId.Should().BeNull();
    }

    [Fact]
    public void Create_ValidatesAndPreservesOAuthFamily()
    {
        var family = Guid.NewGuid();
        ApiKey.Create(Guid.NewGuid(), "OAuth", oauthFamilyId: Guid.Empty).IsFailure.Should().BeTrue();
        ApiKey.Create(Guid.NewGuid(), "OAuth", oauthFamilyId: family).Value.Entity.OAuthFamilyId.Should().Be(family);
    }

    [Fact]
    public void Create_ValidInput_ReturnsSuccess()
    {
        var result = ApiKey.Create(ValidUserId, "My API Key");

        result.IsSuccess.Should().BeTrue();
        result.Value.Entity.UserId.Should().Be(ValidUserId);
        result.Value.Entity.Name.Should().Be("My API Key");
        result.Value.Entity.IsRevoked.Should().BeFalse();
        result.Value.Entity.LastUsedAtUtc.Should().BeNull();
        result.Value.RawKey.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Create_RawKeyStartsWithPrefix()
    {
        var result = ApiKey.Create(ValidUserId, "Test Key");

        result.Value.RawKey.Should().StartWith("orb_");
    }

    [Fact]
    public void Create_RawKeyHasCorrectLength()
    {
        var result = ApiKey.Create(ValidUserId, "Test Key");

        result.Value.RawKey.Should().HaveLength(36);
    }

    [Fact]
    public void Create_KeyPrefixMatchesRawKeyStart()
    {
        var result = ApiKey.Create(ValidUserId, "Test Key");

        result.Value.Entity.KeyPrefix.Should().Be(result.Value.RawKey[..12]);
    }

    [Fact]
    public void Create_KeyHashIsNotEmpty()
    {
        var result = ApiKey.Create(ValidUserId, "Test Key");

        result.Value.Entity.KeyHash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Create_KeyHashDiffersFromRawKey()
    {
        var result = ApiKey.Create(ValidUserId, "Test Key");

        result.Value.Entity.KeyHash.Should().NotBe(result.Value.RawKey);
    }

    [Fact]
    public void Create_KeyHashRemainsReadableByRollbackVerifier()
    {
        var (key, rawKey) = ApiKey.Create(ValidUserId, "Rollback compatible").Value;

        BCrypt.Net.BCrypt.Verify(rawKey, key.KeyHash).Should().BeTrue();
        key.KeyHash.Should().StartWith("$2a$11$");
        key.Verify(rawKey).Should().BeTrue();
        key.Verify($"{rawKey[..^1]}!").Should().BeFalse();
    }

    [Fact]
    public void Create_GeneratesUniqueKeys()
    {
        var result1 = ApiKey.Create(ValidUserId, "Key 1");
        var result2 = ApiKey.Create(ValidUserId, "Key 2");

        result1.Value.RawKey.Should().NotBe(result2.Value.RawKey);
    }

    [Fact]
    public void Create_EmptyUserId_ReturnsFailure()
    {
        var result = ApiKey.Create(Guid.Empty, "Test Key");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User ID is required");
    }

    [Fact]
    public void Create_EmptyName_ReturnsFailure()
    {
        var result = ApiKey.Create(ValidUserId, "");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("API key name is required");
    }

    [Fact]
    public void Create_WhitespaceName_ReturnsFailure()
    {
        var result = ApiKey.Create(ValidUserId, "   ");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("API key name is required");
    }

    [Fact]
    public void Create_NameOver50Chars_ReturnsFailure()
    {
        var longName = new string('a', 51);

        var result = ApiKey.Create(ValidUserId, longName);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("50 characters or less");
    }

    [Fact]
    public void Create_NameExactly50Chars_ReturnsSuccess()
    {
        var name = new string('a', 50);

        var result = ApiKey.Create(ValidUserId, name);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Create_TrimsName()
    {
        var result = ApiKey.Create(ValidUserId, "  My Key  ");

        result.Value.Entity.Name.Should().Be("My Key");
    }

    [Fact]
    public void Create_NameWithLeadingWhitespaceOver50_TrimsBeforeValidation()
    {
        var name = "  " + new string('a', 49);

        var result = ApiKey.Create(ValidUserId, name);

        result.IsSuccess.Should().BeTrue();
        result.Value.Entity.Name.Should().HaveLength(49);
    }

    [Fact]
    public void Create_SetsCreatedAtUtc()
    {
        var before = DateTime.UtcNow;

        var result = ApiKey.Create(ValidUserId, "Test Key");

        var after = DateTime.UtcNow;
        result.Value.Entity.CreatedAtUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void Revoke_SetsIsRevokedTrue()
    {
        var apiKey = ApiKey.Create(ValidUserId, "Test Key").Value.Entity;

        apiKey.Revoke();

        apiKey.IsRevoked.Should().BeTrue();
    }

    [Fact]
    public void Revoke_AlreadyRevoked_StaysRevoked()
    {
        var apiKey = ApiKey.Create(ValidUserId, "Test Key").Value.Entity;
        apiKey.Revoke();

        apiKey.Revoke();

        apiKey.IsRevoked.Should().BeTrue();
    }

    [Fact]
    public void MarkUsed_SetsLastUsedAtUtc()
    {
        var apiKey = ApiKey.Create(ValidUserId, "Test Key").Value.Entity;
        apiKey.LastUsedAtUtc.Should().BeNull();

        var before = DateTime.UtcNow;
        apiKey.MarkUsed();
        var after = DateTime.UtcNow;

        apiKey.LastUsedAtUtc.Should().NotBeNull();
        apiKey.LastUsedAtUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void MarkUsed_CalledTwice_UpdatesTimestamp()
    {
        var apiKey = ApiKey.Create(ValidUserId, "Test Key").Value.Entity;
        apiKey.MarkUsed();
        var firstUsed = apiKey.LastUsedAtUtc;

        apiKey.MarkUsed();

        apiKey.LastUsedAtUtc.Should().BeOnOrAfter(firstUsed!.Value);
    }
}
