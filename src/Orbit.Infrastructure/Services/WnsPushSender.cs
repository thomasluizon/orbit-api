using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed class WnsPushSender(
    HttpClient http,
    IOptions<WnsSettings> settings,
    ILogger<WnsPushSender> logger,
    TimeProvider time) : IWnsPushSender, IDisposable
{
    private readonly WnsSettings _settings = settings.Value;
    private readonly SemaphoreSlim _tokenLock = new(1);
    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAtUtc;

    public async Task<WnsDeliveryOutcome> SendAsync(string channelUri, string payload, CancellationToken cancellationToken)
    {
        if (!_settings.IsConfigured || !WnsEndpointPolicy.IsAllowed(channelUri))
            return WnsDeliveryOutcome.Retained;

        try
        {
            string? rejectedToken = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var token = await GetAccessTokenAsync(rejectedToken, cancellationToken);
                if (token is null)
                    return WnsDeliveryOutcome.Retained;

                using var request = new HttpRequestMessage(HttpMethod.Post, channelUri)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload))
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Add("X-WNS-Type", "wns/raw");
                request.Headers.ExpectContinue = false;

                using var response = await http.SendAsync(request, cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                    return WnsDeliveryOutcome.Delivered;
                if (response.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
                    return WnsDeliveryOutcome.Stale;
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    rejectedToken = token;
                    continue;
                }
                logger.LogWarning("WNS delivery failed with status {StatusCode}", (int)response.StatusCode);
                return WnsDeliveryOutcome.Retained;
            }
            return WnsDeliveryOutcome.Retained;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && exception is HttpRequestException or TaskCanceledException or JsonException or ArgumentException)
        {
            logger.LogWarning("WNS delivery could not complete ({FailureType})", exception.GetType().Name);
            return WnsDeliveryOutcome.Retained;
        }
    }

    private async Task<string?> GetAccessTokenAsync(string? rejectedToken, CancellationToken cancellationToken)
    {
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is not null && _accessToken != rejectedToken && time.GetUtcNow() < _tokenExpiresAtUtc)
                return _accessToken;

            _accessToken = null;
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://login.microsoftonline.com/{Uri.EscapeDataString(_settings.TenantId)}/oauth2/v2.0/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = _settings.ClientId,
                    ["client_secret"] = _settings.ClientSecret,
                    ["scope"] = "https://wns.windows.com/.default"
                })
            };
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("WNS authentication failed with status {StatusCode}", (int)response.StatusCode);
                return null;
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("access_token", out var token)
                || token.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(token.GetString()))
            {
                logger.LogWarning("WNS authentication returned no access token");
                return null;
            }

            _accessToken = token.GetString();
            var lifetime = 0;
            if (document.RootElement.TryGetProperty("expires_in", out var expires))
            {
                if (expires.ValueKind == JsonValueKind.Number)
                    expires.TryGetInt32(out lifetime);
                else if (expires.ValueKind == JsonValueKind.String)
                    int.TryParse(expires.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out lifetime);
            }
            _tokenExpiresAtUtc = time.GetUtcNow().AddSeconds(Math.Max(0L, (long)lifetime - 60));
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public void Dispose()
    {
        _tokenLock.Dispose();
        http.Dispose();
    }
}
