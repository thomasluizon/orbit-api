using System.Security.Cryptography;
using System.Text;

namespace Orbit.Infrastructure.Services;

internal static class SignedTokenCodec
{
    public static string Encode(byte[] payloadBytes, string signingKey)
    {
        var signature = ComputeSignature(payloadBytes, signingKey);
        return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(signature)}";
    }

    public static bool TryDecode(string token, string signingKey, out byte[] payloadBytes)
    {
        payloadBytes = [];

        var separatorIndex = token.IndexOf('.');
        if (separatorIndex <= 0 || separatorIndex == token.Length - 1)
            return false;

        byte[] decodedPayload;
        byte[] providedSignature;
        try
        {
            decodedPayload = Base64UrlDecode(token[..separatorIndex]);
            providedSignature = Base64UrlDecode(token[(separatorIndex + 1)..]);
        }
        catch (FormatException)
        {
            return false;
        }

        var expectedSignature = ComputeSignature(decodedPayload, signingKey);
        if (!CryptographicOperations.FixedTimeEquals(providedSignature, expectedSignature))
            return false;

        payloadBytes = decodedPayload;
        return true;
    }

    private static byte[] ComputeSignature(byte[] payloadBytes, string signingKey)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey));
        return hmac.ComputeHash(payloadBytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            0 => padded,
            _ => throw new FormatException("Invalid base64url length."),
        };
        return Convert.FromBase64String(padded);
    }
}
