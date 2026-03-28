using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.IntegrationTests;

[Collection("Sequential")]
public class EncryptionTests
{
    private readonly IEncryptionService _encryptionService;

    public EncryptionTests()
    {
        var settings = Options.Create(new EncryptionSettings
        {
            Key = "DdyUCjjdK326cB9lY00tyUvRDpCQcYJOJIpu21I1D8c=",
            HmacKey = "bpHAcSwafewwDacpGLrBr4uaqwNJpRDWvgGNmL+TWIc="
        });
        _encryptionService = new EncryptionService(settings, NullLogger<EncryptionService>.Instance);
    }

    [Fact]
    public void Encrypt_Decrypt_Roundtrip_ReturnsOriginalValue()
    {
        var plaintext = "test@example.com";

        var encrypted = _encryptionService.Encrypt(plaintext);
        var decrypted = _encryptionService.Decrypt(encrypted);

        decrypted.Should().Be(plaintext);
    }

    [Fact]
    public void Encrypt_ProducesDifferentCiphertextEachTime()
    {
        var plaintext = "same-input";

        var encrypted1 = _encryptionService.Encrypt(plaintext);
        var encrypted2 = _encryptionService.Encrypt(plaintext);

        encrypted1.Should().NotBe(encrypted2, "AES-GCM uses random nonce, so encryptions differ");
    }

    [Fact]
    public void Encrypt_Decrypt_HandlesEmptyString()
    {
        var plaintext = "";

        var encrypted = _encryptionService.Encrypt(plaintext);
        var decrypted = _encryptionService.Decrypt(encrypted);

        decrypted.Should().Be(plaintext);
    }

    [Fact]
    public void Encrypt_Decrypt_HandlesLongString()
    {
        var plaintext = new string('A', 10000);

        var encrypted = _encryptionService.Encrypt(plaintext);
        var decrypted = _encryptionService.Decrypt(encrypted);

        decrypted.Should().Be(plaintext);
    }

    [Fact]
    public void Encrypt_Decrypt_HandlesUnicodeCharacters()
    {
        var plaintext = "Joao Carlos da Silva";

        var encrypted = _encryptionService.Encrypt(plaintext);
        var decrypted = _encryptionService.Decrypt(encrypted);

        decrypted.Should().Be(plaintext);
    }

    [Fact]
    public void Encrypt_OutputStartsWithPrefix()
    {
        var encrypted = _encryptionService.Encrypt("test");

        encrypted.Should().StartWith("enc:");
    }

    [Fact]
    public void Decrypt_PlaintextPassthrough_ReturnsAsIs()
    {
        // Pre-encryption data should pass through without error
        var plaintext = "not-encrypted@email.com";

        var result = _encryptionService.Decrypt(plaintext);

        result.Should().Be(plaintext);
    }

    [Fact]
    public void Decrypt_LongBase64Plaintext_ReturnsAsIs()
    {
        // Values that look like Base64 but aren't encrypted should pass through
        var url = "https://fcm.googleapis.com/fcm/send/cY2M7GEaPK0:APA91bHxyz";

        var result = _encryptionService.Decrypt(url);

        result.Should().Be(url);
    }

    [Fact]
    public void EncryptNullable_NullInput_ReturnsNull()
    {
        var result = _encryptionService.EncryptNullable(null);

        result.Should().BeNull();
    }

    [Fact]
    public void DecryptNullable_NullInput_ReturnsNull()
    {
        var result = _encryptionService.DecryptNullable(null);

        result.Should().BeNull();
    }

    [Fact]
    public void EncryptNullable_DecryptNullable_Roundtrip()
    {
        var plaintext = "nullable-test-value";

        var encrypted = _encryptionService.EncryptNullable(plaintext);
        var decrypted = _encryptionService.DecryptNullable(encrypted);

        encrypted.Should().NotBeNull();
        decrypted.Should().Be(plaintext);
    }

    [Fact]
    public void ComputeHmac_IsDeterministic()
    {
        var input = "test@example.com";

        var hash1 = _encryptionService.ComputeHmac(input);
        var hash2 = _encryptionService.ComputeHmac(input);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void ComputeHmac_NormalizesInput()
    {
        var hash1 = _encryptionService.ComputeHmac("Test@Example.COM");
        var hash2 = _encryptionService.ComputeHmac("test@example.com");
        var hash3 = _encryptionService.ComputeHmac("  test@example.com  ");

        hash1.Should().Be(hash2);
        hash2.Should().Be(hash3);
    }

    [Fact]
    public void ComputeHmac_DifferentInputs_DifferentHashes()
    {
        var hash1 = _encryptionService.ComputeHmac("user1@example.com");
        var hash2 = _encryptionService.ComputeHmac("user2@example.com");

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void ComputeHmac_ReturnsHexString()
    {
        var hash = _encryptionService.ComputeHmac("test@example.com");

        hash.Should().MatchRegex("^[0-9a-f]{64}$", "HMAC-SHA256 produces 64 hex characters");
    }

    [Fact]
    public void Constructor_InvalidKeyLength_ThrowsArgumentException()
    {
        var badSettings = Options.Create(new EncryptionSettings
        {
            Key = Convert.ToBase64String(new byte[16]), // 16 bytes instead of 32
            HmacKey = "bpHAcSwafewwDacpGLrBr4uaqwNJpRDWvgGNmL+TWIc="
        });

        var act = () => new EncryptionService(badSettings, NullLogger<EncryptionService>.Instance);

        act.Should().Throw<ArgumentException>().WithMessage("*256 bits*");
    }

    [Fact]
    public void Constructor_PlaceholderKey_EnablesPassthroughMode()
    {
        var placeholderSettings = Options.Create(new EncryptionSettings
        {
            Key = "REPLACE-IN-DEVELOPMENT-JSON",
            HmacKey = "REPLACE-IN-DEVELOPMENT-JSON"
        });

        var service = new EncryptionService(placeholderSettings, NullLogger<EncryptionService>.Instance);

        // In passthrough mode, encrypt returns input unchanged
        service.Encrypt("hello").Should().Be("hello");
        service.Decrypt("hello").Should().Be("hello");
        service.ComputeHmac("Test@Example.COM").Should().Be("test@example.com");
    }
}
