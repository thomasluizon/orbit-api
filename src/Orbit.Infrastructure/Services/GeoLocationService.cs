using System.Net;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Services;

public partial class GeoLocationService(HttpClient httpClient, ILogger<GeoLocationService> logger) : IGeoLocationService
{
    private const string UnknownCountryCode = "ZZ";

    public async Task<string> GetCountryCodeAsync(string? ipAddress, CancellationToken ct = default)
    {
        if (!TryNormalizePublicIpAddress(ipAddress, out var normalizedIpAddress))
            return UnknownCountryCode;

        try
        {
            var response = await httpClient.GetAsync($"https://ipapi.co/{normalizedIpAddress}/country/", ct);
            if (!response.IsSuccessStatusCode)
                return UnknownCountryCode;

            var countryCode = (await response.Content.ReadAsStringAsync(ct)).Trim();
            return string.IsNullOrWhiteSpace(countryCode)
                ? UnknownCountryCode
                : countryCode.ToUpperInvariant();
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                LogGeolocationLookupFailed(logger, ex, normalizedIpAddress);
            return UnknownCountryCode;
        }
    }

    private static bool TryNormalizePublicIpAddress(string? ipAddress, out string? normalizedIpAddress)
    {
        normalizedIpAddress = null;

        if (string.IsNullOrWhiteSpace(ipAddress) || !IPAddress.TryParse(ipAddress.Trim(), out var parsedIpAddress))
            return false;

        var candidateIpAddress = parsedIpAddress.IsIPv4MappedToIPv6
            ? parsedIpAddress.MapToIPv4()
            : parsedIpAddress;

        if (IPAddress.IsLoopback(candidateIpAddress) || IsPrivateIpAddress(candidateIpAddress))
            return false;

        normalizedIpAddress = candidateIpAddress.ToString();
        return true;
    }

    private static bool IsPrivateIpAddress(IPAddress ipAddress)
    {
        if (ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = ipAddress.GetAddressBytes();
            return bytes[0] switch
            {
                10 => true,
                127 => true,
                169 when bytes[1] == 254 => true,
                172 when bytes[1] >= 16 && bytes[1] <= 31 => true,
                192 when bytes[1] == 168 => true,
                _ => false
            };
        }

        if (ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return ipAddress.IsIPv6LinkLocal
                || ipAddress.IsIPv6SiteLocal
                || ipAddress.IsIPv6UniqueLocal;
        }

        return false;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Geolocation lookup failed for IP {Ip}, defaulting to unknown country")]
    private static partial void LogGeolocationLookupFailed(ILogger logger, Exception ex, string? ip);
}
