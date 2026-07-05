using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Services;

public sealed class MarketingUnsubscribeTokenService(IDataProtectionProvider dataProtectionProvider)
    : IMarketingUnsubscribeTokenService
{
    private const string Purpose = "marketing-unsubscribe";
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(Purpose);

    public string CreateToken(Guid userId)
    {
        var payload = $"{userId:N}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";
        return _protector.Protect(payload);
    }

    public bool TryValidateToken(string token, out Guid userId)
    {
        userId = Guid.Empty;

        if (string.IsNullOrWhiteSpace(token))
            return false;

        string payload;
        try
        {
            payload = _protector.Unprotect(token);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }

        var separatorIndex = payload.IndexOf('|');
        var idSegment = separatorIndex < 0 ? payload : payload[..separatorIndex];
        return Guid.TryParseExact(idSegment, "N", out userId);
    }
}
