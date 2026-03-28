using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed class EncryptionService : IEncryptionService
{
    private const int NonceSize = 12;  // AES-GCM standard nonce size
    private const int TagSize = 16;    // AES-GCM standard tag size

    private readonly byte[]? _key;
    private readonly byte[]? _hmacKey;
    private readonly bool _isConfigured;

    public EncryptionService(IOptions<EncryptionSettings> settings, ILogger<EncryptionService> logger)
    {
        try
        {
            var keyString = settings.Value.Key;
            var hmacKeyString = settings.Value.HmacKey;

            if (string.IsNullOrEmpty(keyString) || keyString.Contains("REPLACE")
                || string.IsNullOrEmpty(hmacKeyString) || hmacKeyString.Contains("REPLACE"))
            {
                logger.LogWarning("Encryption keys not configured -- encryption is disabled (passthrough mode)");
                _isConfigured = false;
                return;
            }

            _key = Convert.FromBase64String(keyString);
            _hmacKey = Convert.FromBase64String(hmacKeyString);

            if (_key.Length != 32)
                throw new ArgumentException("Encryption key must be 256 bits (32 bytes) when decoded from Base64.");

            if (_hmacKey.Length < 16)
                throw new ArgumentException("HMAC key must be at least 128 bits (16 bytes) when decoded from Base64.");

            _isConfigured = true;
        }
        catch (FormatException)
        {
            logger.LogWarning("Encryption keys are not valid Base64 -- encryption is disabled (passthrough mode)");
            _isConfigured = false;
        }
    }

    /// <summary>
    /// Whether encryption is properly configured with valid keys.
    /// When false, Encrypt/Decrypt act as passthrough (return input unchanged).
    /// </summary>
    public bool IsConfigured => _isConfigured;

    public string Encrypt(string plaintext)
    {
        if (!_isConfigured)
            return plaintext;

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key!, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Format: nonce + ciphertext + tag, then Base64 encode
        var result = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(result, 0);
        ciphertext.CopyTo(result, NonceSize);
        tag.CopyTo(result, NonceSize + ciphertext.Length);

        return Convert.ToBase64String(result);
    }

    public string Decrypt(string ciphertextBase64)
    {
        if (!_isConfigured)
            return ciphertextBase64;

        // Backward compatibility: if the value does not look like Base64 ciphertext,
        // return it as-is (plaintext passthrough for pre-encryption data)
        if (!IsEncrypted(ciphertextBase64))
            return ciphertextBase64;

        var combined = Convert.FromBase64String(ciphertextBase64);

        if (combined.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext is too short to contain nonce and tag.");

        var nonce = combined[..NonceSize];
        var tag = combined[^TagSize..];
        var ciphertext = combined[NonceSize..^TagSize];

        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_key!, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    public string? EncryptNullable(string? plaintext)
    {
        return plaintext is null ? null : Encrypt(plaintext);
    }

    public string? DecryptNullable(string? ciphertext)
    {
        return ciphertext is null ? null : Decrypt(ciphertext);
    }

    public string ComputeHmac(string input)
    {
        if (!_isConfigured)
        {
            // Fallback: return normalized email for lookups (no HMAC without key)
            return input.Trim().ToLowerInvariant();
        }

        var normalizedInput = input.Trim().ToLowerInvariant();
        var inputBytes = Encoding.UTF8.GetBytes(normalizedInput);

        using var hmac = new HMACSHA256(_hmacKey!);
        var hash = hmac.ComputeHash(inputBytes);

        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Checks if a value appears to be encrypted (valid Base64 with minimum length for nonce + tag).
    /// Used for backward compatibility during migration from plaintext to encrypted data.
    /// </summary>
    private static bool IsEncrypted(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        // Minimum Base64 length for nonce (12) + tag (16) = 28 bytes = ~40 Base64 chars
        if (value.Length < 40)
            return false;

        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length >= NonceSize + TagSize;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
