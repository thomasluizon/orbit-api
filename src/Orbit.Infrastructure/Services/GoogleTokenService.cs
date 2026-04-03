using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Services;

public partial class GoogleTokenService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<GoogleTokenService> logger) : IGoogleTokenService
{
    // S1075: Stable Google OAuth API endpoint - not configurable
    private const string GoogleTokenUrl = "https://oauth2.googleapis.com/token"; // NOSONAR
    public async Task<string?> GetValidAccessTokenAsync(User user, CancellationToken ct = default)
    {
        if (user.GoogleAccessToken is null)
            return null;

        // Try the stored token first - if refresh token exists, try refresh
        if (user.GoogleRefreshToken is not null)
        {
            try
            {
                var client = httpClientFactory.CreateClient();
                var response = await client.PostAsync(GoogleTokenUrl,
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["client_id"] = configuration["Google:ClientId"]!,
                        ["client_secret"] = configuration["Google:ClientSecret"]!,
                        ["refresh_token"] = user.GoogleRefreshToken,
                        ["grant_type"] = "refresh_token"
                    }), ct);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                    var newToken = json.TryGetProperty("access_token", out var tokenProp) ? tokenProp.GetString() : null;
                    if (newToken is not null)
                    {
                        user.SetGoogleTokens(newToken, null);
                        return newToken;
                    }
                }
            }
            catch (Exception ex)
            {
                LogGoogleTokenRefreshFailed(logger, ex, user.Id);
            }
        }

        return user.GoogleAccessToken;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to refresh Google token for user {UserId}")]
    private static partial void LogGoogleTokenRefreshFailed(ILogger logger, Exception ex, Guid userId);

}
