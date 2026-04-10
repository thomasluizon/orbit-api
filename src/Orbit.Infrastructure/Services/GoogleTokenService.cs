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

        // Legacy behavior: attempt refresh, fall back to stored access token on failure.
        // Auto-sync uses TryRefreshAsync directly to distinguish invalid_grant from transient errors.
        if (user.GoogleRefreshToken is null)
            return user.GoogleAccessToken;

        var outcome = await TryRefreshAsync(user, ct);
        return outcome.AccessToken ?? user.GoogleAccessToken;
    }

    public async Task<GoogleTokenRefreshOutcome> TryRefreshAsync(User user, CancellationToken ct = default)
    {
        if (user.GoogleRefreshToken is null)
        {
            return new GoogleTokenRefreshOutcome(
                user.GoogleAccessToken,
                GoogleTokenRefreshResult.TransientFailure,
                "no_refresh_token");
        }

        try
        {
            var client = httpClientFactory.CreateClient();
            using var response = await client.PostAsync(GoogleTokenUrl,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = configuration["Google:ClientId"]!,
                    ["client_secret"] = configuration["Google:ClientSecret"]!,
                    ["refresh_token"] = user.GoogleRefreshToken,
                    ["grant_type"] = "refresh_token"
                }), ct);

            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                return ParseSuccessResponse(user, body);
            }

            return ParseErrorResponse(user.Id, body, response.StatusCode);
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                LogGoogleTokenRefreshFailed(logger, ex, user.Id);
            return new GoogleTokenRefreshOutcome(null, GoogleTokenRefreshResult.TransientFailure, "exception");
        }
    }

    private static GoogleTokenRefreshOutcome ParseSuccessResponse(User user, string body)
    {
        var json = JsonDocument.Parse(body).RootElement;
        var newAccessToken = json.TryGetProperty("access_token", out var accessProp)
            ? accessProp.GetString()
            : null;
        var rotatedRefreshToken = json.TryGetProperty("refresh_token", out var refreshProp)
            ? refreshProp.GetString()
            : null;

        if (newAccessToken is null)
        {
            return new GoogleTokenRefreshOutcome(null, GoogleTokenRefreshResult.TransientFailure, "missing_access_token");
        }

        user.SetGoogleTokens(newAccessToken, rotatedRefreshToken);
        return new GoogleTokenRefreshOutcome(newAccessToken, GoogleTokenRefreshResult.Success, null);
    }

    private GoogleTokenRefreshOutcome ParseErrorResponse(Guid userId, string body, System.Net.HttpStatusCode statusCode)
    {
        string? errorCode = null;
        try
        {
            var errorJson = JsonDocument.Parse(body).RootElement;
            if (errorJson.TryGetProperty("error", out var errProp))
                errorCode = errProp.GetString();
        }
        catch (JsonException)
        {
            // Non-JSON body, leave errorCode null
        }

        if (errorCode is "invalid_grant" or "unauthorized_client")
        {
            if (logger.IsEnabled(LogLevel.Warning))
                LogGoogleRefreshTokenRevoked(logger, userId, errorCode);
            return new GoogleTokenRefreshOutcome(null, GoogleTokenRefreshResult.RefreshTokenInvalid, errorCode);
        }

        if (logger.IsEnabled(LogLevel.Warning))
            LogGoogleTokenRefreshHttpError(logger, userId, (int)statusCode, errorCode ?? "unknown");
        return new GoogleTokenRefreshOutcome(null, GoogleTokenRefreshResult.TransientFailure, errorCode ?? "http_error");
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to refresh Google token for user {UserId}")]
    private static partial void LogGoogleTokenRefreshFailed(ILogger logger, Exception ex, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Google refresh token revoked for user {UserId}: {ErrorCode}")]
    private static partial void LogGoogleRefreshTokenRevoked(ILogger logger, Guid userId, string errorCode);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Google token refresh HTTP error for user {UserId}: status={StatusCode} error={ErrorCode}")]
    private static partial void LogGoogleTokenRefreshHttpError(ILogger logger, Guid userId, int statusCode, string errorCode);
}
