using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Services;

public sealed class MarketingUnsubscribeTokenService(IOptions<MarketingSettings> options)
    : IMarketingUnsubscribeTokenService
{
    private readonly MarketingSettings _settings = options.Value;

    public string CreateToken(Guid userId)
    {
        if (string.IsNullOrWhiteSpace(_settings.UnsubscribeSigningKey))
            throw new InvalidOperationException("Marketing:UnsubscribeSigningKey is not configured.");

        var payloadBytes = Encoding.UTF8.GetBytes(userId.ToString("N"));
        var signature = ComputeSignature(payloadBytes);

        return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(signature)}";
    }

    public bool TryValidateToken(string token, out Guid userId)
    {
        userId = Guid.Empty;

        if (string.IsNullOrWhiteSpace(_settings.UnsubscribeSigningKey) || string.IsNullOrWhiteSpace(token))
            return false;

        var separatorIndex = token.IndexOf('.');
        if (separatorIndex <= 0 || separatorIndex == token.Length - 1)
            return false;

        byte[] payloadBytes;
        byte[] providedSignature;
        try
        {
            payloadBytes = Base64UrlDecode(token[..separatorIndex]);
            providedSignature = Base64UrlDecode(token[(separatorIndex + 1)..]);
        }
        catch (FormatException)
        {
            return false;
        }

        var expectedSignature = ComputeSignature(payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(providedSignature, expectedSignature))
            return false;

        return Guid.TryParseExact(Encoding.UTF8.GetString(payloadBytes), "N", out userId);
    }

    private byte[] ComputeSignature(byte[] payloadBytes)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_settings.UnsubscribeSigningKey));
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
