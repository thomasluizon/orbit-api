using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Orbit.Api.Extensions;

namespace Orbit.Api.RateLimiting;

public readonly record struct ClientIpResolution(string? Address, bool IsVerifiedBff);

/// <summary>
/// Only a BFF authenticated by its separate forwarding secret may supply a visitor IP.
/// Direct requests retain the connection IP established by ForwardedHeaders middleware.
/// Verified BFF requests without a single valid visitor IP return a null address so each
/// caller can enforce its own rejection or unknown-bucket policy.
/// </summary>
public sealed class BffClientIpResolver(IOptions<BffForwardingSettings> options)
{
    public ClientIpResolution Resolve(HttpContext context)
    {
        var configuredSecret = options.Value.BffForwardingSecret;
        var suppliedSecret = context.Request.Headers["X-Orbit-Bff-Secret"];
        if (string.IsNullOrWhiteSpace(configuredSecret)
            || suppliedSecret.Count != 1
            || string.IsNullOrWhiteSpace(suppliedSecret[0])
            || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(configuredSecret)),
                SHA256.HashData(Encoding.UTF8.GetBytes(suppliedSecret.ToString()))))
        {
            return new(context.GetClientIpAddress(), false);
        }

        var suppliedIp = context.Request.Headers["X-Orbit-Client-Ip"];
        if (suppliedIp.Count != 1 || !IPAddress.TryParse(suppliedIp[0], out var clientIp))
            return new(null, true);

        return new((clientIp.IsIPv4MappedToIPv6 ? clientIp.MapToIPv4() : clientIp).ToString(), true);
    }
}
