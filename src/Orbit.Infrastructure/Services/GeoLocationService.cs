using Microsoft.Extensions.Logging;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Services;

public class GeoLocationService(HttpClient httpClient, ILogger<GeoLocationService> logger) : IGeoLocationService
{
    public async Task<string> GetCountryCodeAsync(string? ipAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ipAddress) || ipAddress == "::1" || ipAddress == "127.0.0.1")
            return "US";

        try
        {
            var response = await httpClient.GetAsync($"https://ipapi.co/{ipAddress}/country/", ct);
            if (!response.IsSuccessStatusCode) return "US";

            var countryCode = (await response.Content.ReadAsStringAsync(ct)).Trim();
            return string.IsNullOrWhiteSpace(countryCode) ? "US" : countryCode;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Geolocation lookup failed for IP {IP}, defaulting to US", ipAddress);
            return "US";
        }
    }
}
