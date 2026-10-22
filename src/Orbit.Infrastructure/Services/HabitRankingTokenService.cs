using System.Text;
using Microsoft.Extensions.Options;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed class HabitRankingTokenService(IOptions<JwtSettings> options) : IHabitRankingTokenService
{
    private readonly string _signingKey = "orbit:habit-ranking:v1:" + options.Value.SecretKey;

    public string Sign(string payload) => SignedTokenCodec.Encode(Encoding.UTF8.GetBytes(payload), _signingKey);

    public bool TryRead(string token, out string payload)
    {
        payload = string.Empty;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 16384
            || !SignedTokenCodec.TryDecode(token, _signingKey, out var bytes))
            return false;
        payload = Encoding.UTF8.GetString(bytes);
        return true;
    }
}
