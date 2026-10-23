using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed class ApnsPushSender : IApnsPushSender, IDisposable
{
    private readonly HttpClient _http;
    private readonly ApnsSettings _settings;
    private readonly ILogger<ApnsPushSender> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _concurrency = new(8);
    private readonly object _tokenLock = new();
    private string? _providerToken;
    private DateTimeOffset _tokenIssuedAtUtc;

    public ApnsPushSender(HttpClient http, IOptions<ApnsSettings> settings, ILogger<ApnsPushSender> logger, TimeProvider time)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;
        _time = time;
        if (!_settings.IsConfigured)
            _logger.LogWarning("APNs is disabled because its signing key or topic is not configured");
    }

    public async Task<ApnsDeliveryOutcome> SendAsync(string deviceToken, string title, string body, string? url, CancellationToken cancellationToken)
    {
        if (!_settings.IsConfigured)
            return ApnsDeliveryOutcome.Retained;

        await _concurrency.WaitAsync(cancellationToken);
        try
        {
            var host = _settings.UseSandbox ? "api.sandbox.push.apple.com" : "api.push.apple.com";
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/3/device/{Uri.EscapeDataString(deviceToken)}")
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = JsonContent.Create(new { aps = new { alert = new { title, body }, sound = "default" }, body = new { url } })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("bearer", GetProviderToken());
            request.Headers.Add("apns-topic", _settings.Topic);
            request.Headers.Add("apns-push-type", "alert");
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
                return ApnsDeliveryOutcome.Delivered;
            if (response.StatusCode == HttpStatusCode.Gone)
                return ApnsDeliveryOutcome.Stale;
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                if (payload.RootElement.TryGetProperty("reason", out var reason) && reason.GetString() == "BadDeviceToken")
                    return ApnsDeliveryOutcome.Stale;
            }
            _logger.LogWarning("APNs delivery failed with status {StatusCode}", (int)response.StatusCode);
            return ApnsDeliveryOutcome.Retained;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or JsonException or CryptographicException or ArgumentException)
        {
            _logger.LogWarning("APNs delivery could not complete ({FailureType})", ex.GetType().Name);
            return ApnsDeliveryOutcome.Retained;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private string GetProviderToken()
    {
        lock (_tokenLock)
        {
            var now = _time.GetUtcNow();
            if (_providerToken is not null && now - _tokenIssuedAtUtc < TimeSpan.FromMinutes(50))
                return _providerToken;
            using var key = ECDsa.Create();
            key.ImportFromPem(_settings.PrivateKey);
            var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = _settings.KeyId }));
            var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new { iss = _settings.TeamId, iat = now.ToUnixTimeSeconds() }));
            var content = $"{header}.{payload}";
            var signature = key.SignData(Encoding.ASCII.GetBytes(content), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            _providerToken = $"{content}.{Encode(signature)}";
            _tokenIssuedAtUtc = now;
            return _providerToken;
        }
    }

    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        _concurrency.Dispose();
        _http.Dispose();
    }
}
