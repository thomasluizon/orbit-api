using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class ResendContactsServiceTests
{
    private readonly FakeHttpMessageHandler _handler = new();

    private ResendContactsService BuildService(ResendSettings? settings = null)
    {
        var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://api.resend.com") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("Resend").Returns(httpClient);

        settings ??= new ResendSettings { ApiKey = "test", FromEmail = "test@test.com" };

        return new ResendContactsService(
            factory,
            Options.Create(settings),
            NullLogger<ResendContactsService>.Instance);
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

    [Fact]
    public async Task UpsertProductContactAsync_Created_PostsContactWithPropertiesAndSegment()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.Created);
        var service = BuildService(SettingsWithSegments());

        await service.UpsertProductContactAsync("user@test.com", "pt-BR", "pro");

        _handler.Requests.Should().ContainSingle();
        var request = _handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.Path.Should().Be("/contacts");
        request.Body.Should().Contain("\"unsubscribed\":false");
        request.Body.Should().Contain("\"locale\":\"pt-BR\"");
        request.Body.Should().Contain("\"plan\":\"pro\"");
        request.Body.Should().Contain("\"segments\":[\"seg_product\"]");
    }

    [Fact]
    public async Task UpsertProductContactAsync_ExistingContact_PatchesThenAddsToSegment()
    {
        _handler.Responder = request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/contacts"
            ? new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("exists") }
            : new HttpResponseMessage(HttpStatusCode.OK);
        var service = BuildService(SettingsWithSegments());

        await service.UpsertProductContactAsync("user@test.com", "en", "free");

        _handler.Requests.Should().HaveCount(3);
        _handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        _handler.Requests[0].Path.Should().Be("/contacts");
        _handler.Requests[1].Method.Should().Be(HttpMethod.Patch);
        _handler.Requests[1].Path.Should().Be("/contacts/user%40test.com");
        _handler.Requests[1].Body.Should().Contain("\"unsubscribed\":false");
        _handler.Requests[2].Method.Should().Be(HttpMethod.Post);
        _handler.Requests[2].Path.Should().Be("/contacts/user%40test.com/segments/seg_product");
    }

    [Fact]
    public async Task SetContactUnsubscribedAsync_PatchesUnsubscribedFlag()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);
        var service = BuildService();

        await service.SetContactUnsubscribedAsync("user@test.com", unsubscribed: true);

        _handler.LastRequest!.Method.Should().Be(HttpMethod.Patch);
        _handler.LastRequest.RequestUri!.PathAndQuery.Should().Be("/contacts/user%40test.com");
        _handler.LastRequestBody.Should().Contain("\"unsubscribed\":true");
    }

    [Fact]
    public async Task SetContactUnsubscribedAsync_NotFound_DoesNotThrow()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.NotFound);
        var service = BuildService();

        var act = () => service.SetContactUnsubscribedAsync("user@test.com", unsubscribed: true);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RemoveContactAsync_DeletesGlobalContact()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK);
        var service = BuildService();

        await service.RemoveContactAsync("user@test.com");

        _handler.LastRequest!.Method.Should().Be(HttpMethod.Delete);
        _handler.LastRequest.RequestUri!.PathAndQuery.Should().Be("/contacts/user%40test.com");
    }

    [Fact]
    public async Task RemoveContactAsync_ApiFailure_DoesNotThrow()
    {
        _handler.ResponseToReturn = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Server error")
        };
        var service = BuildService();

        var act = () => service.RemoveContactAsync("user@test.com");

        await act.Should().NotThrowAsync();
    }

    private static ResendSettings SettingsWithSegments() => new()
    {
        ApiKey = "test",
        FromEmail = "test@test.com",
        ProductSegmentId = "seg_product",
        WaitlistSegmentId = "seg_waitlist"
    };

    private sealed record RecordedRequest(HttpMethod Method, string Path, string Body);

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public HttpResponseMessage ResponseToReturn { get; set; } = new(HttpStatusCode.OK);
        public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }
        public List<RecordedRequest> Requests { get; } = [];
        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastRequestBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is not null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : "";
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.PathAndQuery, LastRequestBody));

            return Responder?.Invoke(request) ?? ResponseToReturn;
        }
    }
}
