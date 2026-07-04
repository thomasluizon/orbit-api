using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class ResendAudienceServiceTests
{
    private readonly FakeHttpMessageHandler _handler = new();

    private ResendAudienceService BuildService(string audienceId)
    {
        var settings = Options.Create(new ResendSettings
        {
            ApiKey = "re_test_key",
            FromEmail = "noreply@useorbit.org",
            AudienceId = audienceId
        });

        var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://api.resend.com") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("Resend").Returns(httpClient);

        return new ResendAudienceService(
            factory,
            settings,
            new NullLoggerFactory().CreateLogger<ResendAudienceService>());
    }

    [Fact]
    public async Task AddContactAsync_PostsToAudienceContactsEndpointWithEmail()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.Created);
        var service = BuildService("aud_123");

        await service.AddContactAsync("User@Test.com");

        _handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        _handler.LastRequest.RequestUri!.PathAndQuery.Should().Be("/audiences/aud_123/contacts");
        _handler.LastRequestBody.Should().Contain("\"email\":\"User@Test.com\"");
        _handler.LastRequestBody.Should().Contain("\"unsubscribed\":false");
    }

    [Fact]
    public async Task AddContactAsync_ApiFailure_DoesNotThrow()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("Contact already exists")
        };
        var service = BuildService("aud_123");

        var act = () => service.AddContactAsync("user@test.com");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AddContactAsync_MissingAudienceId_ThrowsLoudly()
    {
        var service = BuildService("");

        var act = () => service.AddContactAsync("user@test.com");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*AudienceId*");
        _handler.LastRequest.Should().BeNull();
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public HttpResponseMessage ResponseToReturn { get; set; } = new(HttpStatusCode.OK);
        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastRequestBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return ResponseToReturn;
        }
    }
}
