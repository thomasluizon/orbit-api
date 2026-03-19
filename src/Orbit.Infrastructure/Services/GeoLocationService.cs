using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Orbit.Infrastructure.Services;

public interface IGeoLocationService
{
    Task<string> GetCountryCodeAsync(string? ipAddress, CancellationToken ct = default);
}

public class GeoLocationService(HttpClient httpClient, ILogger<GeoLocationService> logger) : IGeoLocationService
{
    public async Task<string> GetCountryCodeAsync(string? ipAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ipAddress) || ipAddress == "::1" || ipAddress == "127.0.0.1")
            return "US";

        try
        {
            var response = await httpClient.GetAsync($"http://ip-api.com/json/{ipAddress}?fields=countryCode", ct);
            if (!response.IsSuccessStatusCode) return "US";

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("countryCode", out var code)
                ? code.GetString() ?? "US"
                : "US";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Geolocation lookup failed for IP {IP}, defaulting to US", ipAddress);
            return "US";
        }
    }
}
