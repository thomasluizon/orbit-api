using System.Net;
using Stripe;

namespace Orbit.Infrastructure.Common;

/// <summary>
/// Bounded retry for Stripe SDK calls. Retries only transient failures — connection/network errors
/// and timeouts (which the Stripe SDK surfaces as the raw <see cref="HttpRequestException"/> /
/// <see cref="OperationCanceledException"/>) and rate-limit (429) or 5xx server responses (surfaced
/// as a <see cref="StripeException"/> carrying the HTTP status) — with exponential backoff, giving up
/// after <see cref="MaxRetries"/> retries. Business errors (card declined, invalid request,
/// authentication — any 4xx other than 429) surface to the caller unretried, as does user cancellation.
/// </summary>
public static class StripeRetryPolicy
{
    public const int MaxRetries = 2;
    public const int BaseDelayMs = 200;

    public static bool IsTransient(StripeException exception) =>
        exception.HttpStatusCode == HttpStatusCode.TooManyRequests
        || (int)exception.HttpStatusCode >= 500;

    /// <summary>
    /// Invokes <paramref name="action"/>, retrying transient failures up to <paramref name="maxRetries"/>
    /// times with exponential backoff. Non-transient failures (business errors) throw on the first attempt.
    /// </summary>
    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken,
        int maxRetries = MaxRetries,
        int baseDelayMs = BaseDelayMs)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                return await action();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxRetries && IsTransientException(ex))
            {
                await Task.Delay(baseDelayMs << attempt, cancellationToken);
                attempt++;
            }
        }
    }

    private static bool IsTransientException(Exception exception) =>
        exception switch
        {
            StripeException stripeException => IsTransient(stripeException),
            HttpRequestException => true,
            OperationCanceledException => true,
            _ => false
        };
}
