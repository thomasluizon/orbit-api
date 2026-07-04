using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Services;

public sealed class WaitlistConfirmationTokenService(
    IOptions<WaitlistSettings> options,
    TimeProvider clock) : IWaitlistConfirmationTokenService
{
    private readonly WaitlistSettings _settings = options.Value;

    public string CreateToken(string email, string language)
    {
        if (string.IsNullOrWhiteSpace(_settings.SigningKey))
            throw new InvalidOperationException("Waitlist:SigningKey is not configured.");

        var expiresAtUnix = clock.GetUtcNow()
            .AddHours(_settings.TokenLifetimeHours)
            .ToUnixTimeSeconds();

        var payload = $"{email}|{language}|{expiresAtUnix}";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var signature = ComputeSignature(payloadBytes);

        return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(signature)}";
    }

    public bool TryValidateToken(string token, out string email, out string language)
    {
        email = string.Empty;
        language = string.Empty;

        if (string.IsNullOrWhiteSpace(_settings.SigningKey) || string.IsNullOrWhiteSpace(token))
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

        var parts = Encoding.UTF8.GetString(payloadBytes).Split('|');
        if (parts.Length != 3 || !long.TryParse(parts[2], out var expiresAtUnix))
            return false;

        if (clock.GetUtcNow().ToUnixTimeSeconds() > expiresAtUnix)
            return false;

        email = parts[0];
        language = parts[1];
        return true;
    }

    private byte[] ComputeSignature(byte[] payloadBytes)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_settings.SigningKey));
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
