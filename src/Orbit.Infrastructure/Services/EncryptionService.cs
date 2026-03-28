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
    private const string EncPrefix = "enc:"; // Deterministic prefix for encrypted values

    private readonly byte[]? _key;
    private readonly bool _isConfigured;

    public EncryptionService(IOptions<EncryptionSettings> settings, ILogger<EncryptionService> logger)
    {
        try
        {
            var keyString = settings.Value.Key;

            if (string.IsNullOrEmpty(keyString) || keyString.Contains("REPLACE"))
            {
                logger.LogWarning("Encryption key not configured -- encryption is disabled (passthrough mode)");
                _isConfigured = false;
                return;
            }

            _key = Convert.FromBase64String(keyString);

            if (_key.Length != 32)
                throw new ArgumentException("Encryption key must be 256 bits (32 bytes) when decoded from Base64.");

            _isConfigured = true;
        }
        catch (FormatException)
        {
            logger.LogWarning("Encryption key is not valid Base64 -- encryption is disabled (passthrough mode)");
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

        return EncPrefix + Convert.ToBase64String(result);
    }

    public string Decrypt(string ciphertextBase64)
    {
        if (!_isConfigured)
            return ciphertextBase64;

        // Deterministic check: only values we encrypted have the prefix
        if (!ciphertextBase64.StartsWith(EncPrefix))
            return ciphertextBase64;

        var combined = Convert.FromBase64String(ciphertextBase64[EncPrefix.Length..]);

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
}
