using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class ResendContactsServiceRetryTests
{
    private static (ResendContactsService Sut, SequencedHttpMessageHandler Handler) Build(SequencedHttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.resend.com") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("Resend").Returns(client);

        var sut = new ResendContactsService(factory, NullLogger<ResendContactsService>.Instance);
        return (sut, handler);
    }

    [Fact]
    public async Task RetriesThenSucceeds_OnTransient5xx()
    {
        var (sut, handler) = Build(new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable),
            SequencedHttpMessageHandler.Status(HttpStatusCode.Created)));

        await sut.AddContactAsync("user@example.com");

        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task GivesUpAfterMaxRetries_OnPersistent5xx()
    {
        var (sut, handler) = Build(new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.InternalServerError)));

        await sut.AddContactAsync("user@example.com");

        handler.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task DoesNotRetry_OnNonTransient4xx()
    {
        var (sut, handler) = Build(new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.BadRequest)));

        await sut.AddContactAsync("user@example.com");

        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task DoesNotRetry_OnDuplicateConflict()
    {
        var (sut, handler) = Build(new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.Conflict)));

        await sut.AddContactAsync("user@example.com");

        handler.CallCount.Should().Be(1);
    }
}
