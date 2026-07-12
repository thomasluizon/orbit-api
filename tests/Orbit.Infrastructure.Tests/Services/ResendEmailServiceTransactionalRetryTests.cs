using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class ResendEmailServiceTransactionalRetryTests
{
    private static (ResendEmailService Sut, SequencedHttpMessageHandler Handler) Build(SequencedHttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.resend.com") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("Resend").Returns(client);

        var settings = Options.Create(new ResendSettings
        {
            ApiKey = "key",
            FromEmail = "Orbit <noreply@send.test>",
        });
        var frontend = Options.Create(new FrontendSettings { BaseUrl = "https://app.test" });

        var sut = new ResendEmailService(factory, settings, frontend, NullLogger<ResendEmailService>.Instance);
        return (sut, handler);
    }

    private static Task Send(ResendEmailService sut) =>
        sut.SendVerificationCodeAsync("user@example.com", "123456");

    [Fact]
    public async Task RetriesThenSucceeds_OnTransient5xx()
    {
        var (sut, handler) = Build(new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable),
            SequencedHttpMessageHandler.Status(HttpStatusCode.OK)));

        await Send(sut);

        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task RetriesThenSucceeds_OnTransportException()
    {
        var (sut, handler) = Build(new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Throws(),
            SequencedHttpMessageHandler.Status(HttpStatusCode.OK)));

        await Send(sut);

        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task GivesUpAfterMaxRetries_OnPersistent5xx()
    {
        var (sut, handler) = Build(new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.InternalServerError)));

        await Send(sut);

        handler.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task DoesNotRetry_OnNonTransient4xx()
    {
        var (sut, handler) = Build(new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.BadRequest)));

        await Send(sut);

        handler.CallCount.Should().Be(1);
    }
}
