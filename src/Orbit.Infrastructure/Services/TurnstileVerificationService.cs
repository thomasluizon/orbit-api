using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Common;

namespace Orbit.Infrastructure.Services;

public sealed class TurnstileVerificationService(
    IHttpClientFactory httpClientFactory,
    IOptions<BotProtectionSettings> options) : ITurnstileVerificationService
{
    public const string HttpClientName = "Turnstile";

    public async Task<TurnstileVerificationResult> VerifyAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var idempotencyKey = Guid.NewGuid().ToString("D");
        for (var attempt = 0; ; attempt++)
        {
            using var response = await HttpRetryPolicy.SendWithRetryAsync(
                () => client.PostAsync(
                    "turnstile/v0/siteverify",
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["secret"] = options.Value.SecretKey,
                        ["response"] = token,
                        ["idempotency_key"] = idempotencyKey
                    }),
                    cancellationToken),
                cancellationToken);

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<SiteverifyResponse>(stream, cancellationToken: cancellationToken);
            if (payload?.Success is null)
                throw new JsonException("Turnstile Siteverify response omitted success.");

            var errorCodes = payload.ErrorCodes ?? [];
            if (payload.Success.Value)
                return new TurnstileVerificationResult(true, errorCodes);

            if (errorCodes.Contains("internal-error", StringComparer.Ordinal))
            {
                if (attempt >= HttpRetryPolicy.MaxRetries)
                    throw new HttpRequestException("Turnstile Siteverify internal error persisted after retries.");

                await Task.Delay(HttpRetryPolicy.BaseDelayMs << attempt, cancellationToken);
                continue;
            }

            if (errorCodes.Length > 0 && errorCodes.All(code => code is "missing-input-response"
                    or "invalid-input-response" or "timeout-or-duplicate"))
                return new TurnstileVerificationResult(false, errorCodes);

            throw new HttpRequestException("Turnstile Siteverify returned a server or unknown error.");
        }
    }

    private sealed class SiteverifyResponse
    {
        [JsonPropertyName("success")]
        public bool? Success { get; init; }

        [JsonPropertyName("error-codes")]
        public string[]? ErrorCodes { get; init; }
    }
}
