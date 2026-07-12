using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class SupabaseObjectStorageServiceRetryTests
{
    private const string SignedUrlBody = "{\"url\":\"/object/upload/sign/uploads/key?token=signed\"}";

    private static (SupabaseObjectStorageService Sut, SequencedHttpMessageHandler Handler) Build(SequencedHttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://project.supabase.co") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(SupabaseObjectStorageService.HttpClientName).Returns(client);

        var settings = Options.Create(new SupabaseStorageSettings
        {
            Url = "https://project.supabase.co",
            SecretKey = "secret",
        });

        var sut = new SupabaseObjectStorageService(factory, settings, NullLogger<SupabaseObjectStorageService>.Instance);
        return (sut, handler);
    }

    [Fact]
    public async Task RetriesThenSucceeds_OnTransient5xx()
    {
        var (sut, handler) = Build(new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.BadGateway),
            SequencedHttpMessageHandler.Status(HttpStatusCode.OK, SignedUrlBody)));

        var result = await sut.CreateSignedUploadAsync("key");

        handler.CallCount.Should().Be(2);
        result.SignedUrl.Should().Contain("token=signed");
    }

    [Fact]
    public async Task RetriesThenSucceeds_OnTransportException()
    {
        var (sut, handler) = Build(new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Throws(),
            SequencedHttpMessageHandler.Status(HttpStatusCode.OK, SignedUrlBody)));

        var result = await sut.CreateSignedUploadAsync("key");

        handler.CallCount.Should().Be(2);
        result.SignedUrl.Should().Contain("token=signed");
    }

    [Fact]
    public async Task GivesUpAfterMaxRetries_OnPersistent5xx()
    {
        var (sut, handler) = Build(new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable)));

        var act = () => sut.CreateSignedUploadAsync("key");

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task DoesNotRetry_OnNonTransient4xx()
    {
        var (sut, handler) = Build(new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.BadRequest)));

        var act = () => sut.CreateSignedUploadAsync("key");

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.CallCount.Should().Be(1);
    }
}
