using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class ResendEmailServiceMarketingRetryTests
{
    private static (ResendEmailService Sut, SequenceHandler Handler) Build(params HttpStatusCode[] responses)
    {
        var handler = new SequenceHandler(responses);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.resend.com") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("Resend").Returns(client);

        var settings = Options.Create(new ResendSettings
        {
            ApiKey = "key",
            FromEmail = "Orbit <noreply@send.test>",
            MarketingFromEmail = "Orbit <news@send.test>",
            MarketingRetryBaseDelayMs = 1,
        });
        var frontend = Options.Create(new FrontendSettings { BaseUrl = "https://app.test" });

        var sut = new ResendEmailService(factory, settings, frontend, NullLogger<ResendEmailService>.Instance);
        return (sut, handler);
    }

    private static Task Send(ResendEmailService sut) =>
        sut.SendMarketingEmailAsync(
            "user@example.com", "Subject", "<p>body</p>", "en", "https://api.useorbit.org/api/marketing/unsubscribe?token=t");

    [Fact]
    public async Task RetriesThenSucceeds_On429()
    {
        var (sut, handler) = Build(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);

        await Send(sut);

        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task RetriesThenSucceeds_On5xx()
    {
        var (sut, handler) = Build(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);

        await Send(sut);

        handler.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task GivesUpAfterMaxRetries_OnPersistentFailure()
    {
        var (sut, handler) = Build(HttpStatusCode.InternalServerError);

        await Send(sut);

        handler.CallCount.Should().Be(5);
    }

    [Fact]
    public async Task DoesNotRetry_OnNonRetriable4xx()
    {
        var (sut, handler) = Build(HttpStatusCode.BadRequest);

        await Send(sut);

        handler.CallCount.Should().Be(1);
    }

    private sealed class SequenceHandler(params HttpStatusCode[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _responses = new(responses);

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var status = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }
}
