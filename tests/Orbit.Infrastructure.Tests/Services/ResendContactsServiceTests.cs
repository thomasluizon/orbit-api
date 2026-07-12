using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class ResendContactsServiceTests
{
    private readonly FakeHttpMessageHandler _handler = new();

    private ResendContactsService BuildService()
    {
        var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://api.resend.com") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("Resend").Returns(httpClient);

        var logger = Substitute.For<ILogger<ResendContactsService>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);

        return new ResendContactsService(factory, logger);
    }

    [Fact]
    public async Task AddContactAsync_PostsToContactsEndpointWithEmail()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.Created);
        var service = BuildService();

        await service.AddContactAsync("User@Test.com");

        _handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        _handler.LastRequest.RequestUri!.PathAndQuery.Should().Be("/contacts");
        _handler.LastRequestBody.Should().Contain("\"email\":\"User@Test.com\"");
        _handler.LastRequestBody.Should().Contain("\"unsubscribed\":false");
    }

    [Fact]
    public async Task AddContactAsync_DuplicateEmailConflict_IsTreatedAsBenignSuccess()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("Contact already exists")
        };
        var service = BuildService();

        var act = () => service.AddContactAsync("user@test.com");

        await act.Should().NotThrowAsync();
        _handler.LastRequest!.RequestUri!.PathAndQuery.Should().Be("/contacts");
    }

    [Fact]
    public async Task AddContactAsync_ApiFailure_DoesNotThrow()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Server error")
        };
        var service = BuildService();

        var act = () => service.AddContactAsync("user@test.com");

        await act.Should().NotThrowAsync();
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
