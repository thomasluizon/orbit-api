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
        return SignedTokenCodec.Encode(payloadBytes, _settings.UnsubscribeSigningKey);
    }

    public bool TryValidateToken(string token, out Guid userId)
    {
        userId = Guid.Empty;

        if (string.IsNullOrWhiteSpace(_settings.UnsubscribeSigningKey) || string.IsNullOrWhiteSpace(token))
            return false;

        if (!SignedTokenCodec.TryDecode(token, _settings.UnsubscribeSigningKey, out var payloadBytes))
            return false;

        return Guid.TryParseExact(Encoding.UTF8.GetString(payloadBytes), "N", out userId);
    }
}
