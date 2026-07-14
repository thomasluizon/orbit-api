using System.Net;
using FluentAssertions;
using Orbit.Infrastructure.Common;
using Stripe;

namespace Orbit.Infrastructure.Tests.Common;

public class StripeRetryPolicyTests
{
    private static StripeException Transient() =>
        new(HttpStatusCode.ServiceUnavailable, new StripeError { Type = "api_error" }, "Service unavailable");

    private static StripeException CardDeclined() =>
        new(HttpStatusCode.PaymentRequired, new StripeError { Type = "card_error", Code = "card_declined" }, "Your card was declined.");

    [Fact]
    public async Task ExecuteWithRetryAsync_TransientStripeError_RetriesThenSucceeds()
    {
        var attempts = 0;

        var result = await StripeRetryPolicy.ExecuteWithRetryAsync(
            () =>
            {
                attempts++;
                if (attempts == 1)
                    throw Transient();
                return Task.FromResult("ok");
            },
            CancellationToken.None,
            baseDelayMs: 0);

        attempts.Should().Be(2);
        result.Should().Be("ok");
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_NetworkError_RetriesThenSucceeds()
    {
        var attempts = 0;

        var result = await StripeRetryPolicy.ExecuteWithRetryAsync(
            () =>
            {
                attempts++;
                if (attempts == 1)
                    throw new HttpRequestException("connection reset");
                return Task.FromResult("ok");
            },
            CancellationToken.None,
            baseDelayMs: 0);

        attempts.Should().Be(2);
        result.Should().Be("ok");
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_CardDeclined_DoesNotRetry()
    {
        var attempts = 0;

        var act = async () => await StripeRetryPolicy.ExecuteWithRetryAsync<string>(
            () =>
            {
                attempts++;
                throw CardDeclined();
            },
            CancellationToken.None,
            baseDelayMs: 0);

        await act.Should().ThrowAsync<StripeException>();
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_PersistentTransient_ExhaustsRetriesThenThrows()
    {
        var attempts = 0;

        var act = async () => await StripeRetryPolicy.ExecuteWithRetryAsync<string>(
            () =>
            {
                attempts++;
                throw Transient();
            },
            CancellationToken.None,
            baseDelayMs: 0);

        await act.Should().ThrowAsync<StripeException>();
        attempts.Should().Be(StripeRetryPolicy.MaxRetries + 1);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_UserCancellation_PropagatesWithoutRetry()
    {
        var attempts = 0;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await StripeRetryPolicy.ExecuteWithRetryAsync<string>(
            () =>
            {
                attempts++;
                throw new OperationCanceledException(cts.Token);
            },
            cts.Token,
            baseDelayMs: 0);

        await act.Should().ThrowAsync<OperationCanceledException>();
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_Timeout_RetriesThenSucceeds()
    {
        var attempts = 0;

        var result = await StripeRetryPolicy.ExecuteWithRetryAsync(
            () =>
            {
                attempts++;
                if (attempts == 1)
                    throw new OperationCanceledException("timed out");
                return Task.FromResult("ok");
            },
            CancellationToken.None,
            baseDelayMs: 0);

        attempts.Should().Be(2);
        result.Should().Be("ok");
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_NonTransientException_DoesNotRetry()
    {
        var attempts = 0;

        var act = async () => await StripeRetryPolicy.ExecuteWithRetryAsync<string>(
            () =>
            {
                attempts++;
                throw new InvalidOperationException("boom");
            },
            CancellationToken.None,
            baseDelayMs: 0);

        await act.Should().ThrowAsync<InvalidOperationException>();
        attempts.Should().Be(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.PaymentRequired, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public void IsTransient_ClassifiesStatusCorrectly(HttpStatusCode status, bool expected)
    {
        var exception = new StripeException(status, new StripeError { Type = "api_error" }, "x");

        StripeRetryPolicy.IsTransient(exception).Should().Be(expected);
    }
}
