using System.Net;

namespace Orbit.Infrastructure.Common;

/// <summary>
/// Bounded retry for outbound HTTP calls to critical external services. Retries only transient
/// failures (network/timeout exceptions and 408/429/5xx responses) with exponential backoff,
/// giving up after <see cref="MaxRetries"/> retries. Permanent responses (2xx, 4xx other than
/// 408/429) are returned to the caller unretried; user cancellation propagates without a retry.
/// </summary>
public static class HttpRetryPolicy
{
    public const int MaxRetries = 2;
    public const int BaseDelayMs = 300;

    public static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// Invokes <paramref name="send"/>, retrying transient outcomes up to <paramref name="maxRetries"/>
    /// times. <paramref name="send"/> must build a fresh request (and content) on each call, since an
    /// <see cref="HttpContent"/> can only be sent once. Transient responses are disposed before the
    /// next attempt; the caller owns the returned response.
    /// </summary>
    public static async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken,
        int maxRetries = MaxRetries,
        int baseDelayMs = BaseDelayMs)
    {
        var attempt = 0;
        while (true)
        {
            HttpResponseMessage response;
            try
            {
                response = await send();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && attempt < maxRetries)
            {
                await Task.Delay(baseDelayMs << attempt, cancellationToken);
                attempt++;
                continue;
            }

            if (response.IsSuccessStatusCode || !IsTransient(response.StatusCode) || attempt >= maxRetries)
                return response;

            response.Dispose();
            await Task.Delay(baseDelayMs << attempt, cancellationToken);
            attempt++;
        }
    }
}
