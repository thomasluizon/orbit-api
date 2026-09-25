using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class TurnstileVerificationServiceTests
{
    [Theory]
    [InlineData("{\"success\":true,\"error-codes\":[]}", true)]
    [InlineData("{\"success\":false,\"error-codes\":[\"timeout-or-duplicate\"]}", false)]
    public async Task Siteverify_SendsDocumentedFormAndReadsResult(string json, bool expectedSuccess)
    {
        HttpRequestMessage? sentRequest = null;
        string? sentForm = null;
        using var client = Client(async request =>
        {
            sentRequest = request;
            sentForm = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        });

        var result = await Service(client).VerifyAsync("client-token");

        result.Success.Should().Be(expectedSuccess);
        sentRequest!.Method.Should().Be(HttpMethod.Post);
        sentRequest.RequestUri!.ToString().Should().Be("https://challenges.cloudflare.com/turnstile/v0/siteverify");
        sentRequest.Content!.Headers.ContentType!.MediaType.Should().Be("application/x-www-form-urlencoded");
        sentForm.Should().Contain("secret=test-secret").And.Contain("response=client-token").And.Contain("idempotency_key=");
    }

    [Fact]
    public async Task Siteverify_ServerError_ThrowsAfterRetries()
    {
        var attempts = 0;
        using var client = Client(_ =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });

        var action = () => Service(client).VerifyAsync("client-token");

        await action.Should().ThrowAsync<HttpRequestException>();
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task Siteverify_NetworkFailure_ThrowsAfterRetries()
    {
        using var client = Client(_ => throw new HttpRequestException("network down"));

        var action = () => Service(client).VerifyAsync("client-token");

        await action.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Siteverify_OmittedSuccess_IsUnavailable()
    {
        using var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"error-codes\":[]}")
        }));

        var action = () => Service(client).VerifyAsync("client-token");

        await action.Should().ThrowAsync<System.Text.Json.JsonException>();
    }

    private static TurnstileVerificationService Service(HttpClient client)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(TurnstileVerificationService.HttpClientName).Returns(client);
        return new TurnstileVerificationService(factory, Options.Create(new BotProtectionSettings
        {
            Enabled = true,
            SecretKey = "test-secret"
        }));
    }

    private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        => new(new Handler(send)) { BaseAddress = new Uri("https://challenges.cloudflare.com/") };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
