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
        return SignedTokenCodec.Encode(payloadBytes, _settings.SigningKey);
    }

    public bool TryValidateToken(string token, out string email, out string language)
    {
        email = string.Empty;
        language = string.Empty;

        if (string.IsNullOrWhiteSpace(_settings.SigningKey) || string.IsNullOrWhiteSpace(token))
            return false;

        if (!SignedTokenCodec.TryDecode(token, _settings.SigningKey, out var payloadBytes))
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
}
