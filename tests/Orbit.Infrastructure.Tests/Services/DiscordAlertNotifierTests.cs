using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class DiscordAlertNotifierTests
{
    private const string WebhookUrl = "https://discord.com/api/webhooks/123/abc";

    [Fact]
    public async Task SendCriticalAsync_ScrubsEmailTokenAndBodyFromPayload()
    {
        var handler = new FakeHttpMessageHandler { ResponseToReturn = new HttpResponseMessage(HttpStatusCode.NoContent) };
        var notifier = CreateNotifier(handler, WebhookUrl);

        await notifier.SendCriticalAsync(
            "InvalidOperationException: send-code failed for alice@example.com using Bearer eyJabc123.def456.ghi789",
            "POST /api/auth/verify-code",
            new Dictionary<string, string?>
            {
                ["Method"] = "POST",
                ["Path"] = "/api/auth/verify-code",
                ["RequestId"] = "req_abc",
                ["ClientIp"] = "203.0.113.7",
                ["UserId"] = "11111111-1111-1111-1111-111111111111",
                ["Email"] = "bob@example.com",
            },
            CancellationToken.None);

        var body = handler.LastRequestBody;
        body.Should().NotContain("alice@example.com");
        body.Should().NotContain("bob@example.com");
        body.Should().NotContain("eyJabc123.def456.ghi789");
        body.Should().NotContain("Bearer eyJ");
        body.Should().Contain("req_abc");
        body.Should().Contain("/api/auth/verify-code");
        body.Should().Contain("11111111-1111-1111-1111-111111111111");
    }

    [Fact]
    public async Task SendCriticalAsync_DoesNotPost_WhenWebhookUrlEmpty()
    {
        var handler = new FakeHttpMessageHandler();
        var notifier = CreateNotifier(handler, "");

        await notifier.SendCriticalAsync(
            "boom",
            "GET /health",
            new Dictionary<string, string?> { ["RequestId"] = "req_x" },
            CancellationToken.None);

        handler.LastRequest.Should().BeNull();
    }

    private static DiscordAlertNotifier CreateNotifier(FakeHttpMessageHandler handler, string webhookUrl)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://discord.com") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("Discord").Returns(httpClient);

        var settings = Options.Create(new DiscordAlertSettings { WebhookUrl = webhookUrl });
        return new DiscordAlertNotifier(factory, settings, NullLogger<DiscordAlertNotifier>.Instance);
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public HttpResponseMessage ResponseToReturn { get; set; } = new(HttpStatusCode.NoContent);
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
