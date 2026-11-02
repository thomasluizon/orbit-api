using System.Security.Cryptography;
using System.Text;

namespace Orbit.Infrastructure.Services;

internal static class WaitlistTokenCipher
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const byte Version = 1;

    public static byte[] Encrypt(byte[] payload, string signingKey)
    {
        var encrypted = new byte[1 + NonceSize + TagSize + payload.Length];
        encrypted[0] = Version;
        var nonce = encrypted.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var cipher = new AesGcm(DeriveKey(signingKey), TagSize);
        cipher.Encrypt(nonce, payload, encrypted.AsSpan(1 + NonceSize + TagSize),
            encrypted.AsSpan(1 + NonceSize, TagSize), encrypted.AsSpan(0, 1));
        return encrypted;
    }

    public static bool TryDecrypt(byte[] encrypted, string signingKey, out byte[] payload)
    {
        payload = [];
        if (encrypted.Length <= 1 + NonceSize + TagSize || encrypted[0] != Version)
            return false;
        var plaintext = new byte[encrypted.Length - 1 - NonceSize - TagSize];
        try
        {
            using var cipher = new AesGcm(DeriveKey(signingKey), TagSize);
            cipher.Decrypt(encrypted.AsSpan(1, NonceSize), encrypted.AsSpan(1 + NonceSize + TagSize),
                encrypted.AsSpan(1 + NonceSize, TagSize), plaintext, encrypted.AsSpan(0, 1));
            payload = plaintext;
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static byte[] DeriveKey(string signingKey) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), Encoding.UTF8.GetBytes("orbit-waitlist-confirmation-encryption-v1"));
}
