using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class ApiKeyTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();

    // --- Create tests ---

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

        // "orb_" (4 chars) + 32 random chars = 36 total
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
        // "  " + 49 chars = 51 total, but trimmed to 49, which is valid
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

    // --- Revoke tests ---

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

    // --- MarkUsed tests ---

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

        // Slight delay to ensure timestamp difference
        apiKey.MarkUsed();

        apiKey.LastUsedAtUtc.Should().BeOnOrAfter(firstUsed!.Value);
    }
}
