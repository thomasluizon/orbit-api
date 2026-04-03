using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Services;

public class GoogleTokenService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<GoogleTokenService> logger) : IGoogleTokenService
{
    private const string GoogleTokenUrl = "https://oauth2.googleapis.com/token";
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
                logger.LogWarning(ex, "Failed to refresh Google token for user {UserId}", user.Id);
            }
        }

        return user.GoogleAccessToken;
    }
}
