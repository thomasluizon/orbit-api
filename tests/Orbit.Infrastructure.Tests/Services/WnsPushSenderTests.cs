using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class WnsPushSenderTests
{
    private const string Channel = "https://dm3p.notify.windows.com/?token=channel";
    private static readonly string TokenResponse = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/Push/WnsTokenResponse.json"));

    private static WnsSettings Configured() => new()
    {
        TenantId = "11111111-1111-1111-1111-111111111111",
        ClientId = "22222222-2222-2222-2222-222222222222",
        ClientSecret = "test &+secret"
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Send_UsesDocumentedEntraAndRawExchanges_AndCachesToken(bool numericLifetime)
    {
        const string payload = "{\"title\":\"Órbita\",\"body\":\"Hora de ler\",\"url\":\"/progress?wrapped=month&year=2026&month=9\"}";
        var settings = Configured();
        var authCalls = 0;
        var pushCalls = 0;
        var handler = new Handler(async request =>
        {
            request.Method.Should().Be(HttpMethod.Post);
            if (request.RequestUri!.Host == "login.microsoftonline.com")
            {
                authCalls++;
                request.RequestUri.AbsoluteUri.Should().Be($"https://login.microsoftonline.com/{settings.TenantId}/oauth2/v2.0/token");
                request.Content!.Headers.ContentType!.MediaType.Should().Be("application/x-www-form-urlencoded");
                var form = (await request.Content.ReadAsStringAsync()).Split('&')
                    .Select(part => part.Split('=', 2)).ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));
                form.Should().BeEquivalentTo(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = settings.ClientId,
                    ["client_secret"] = settings.ClientSecret,
                    ["scope"] = "https://wns.windows.com/.default"
                });
                return Json(numericLifetime ? TokenResponse.Replace("\"86399\"", "86399") : TokenResponse);
            }
            pushCalls++;
            request.RequestUri.OriginalString.Should().Be(Channel);
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            request.Headers.Authorization.Parameter.Should().Be("documented-test-access-token");
            request.Headers.GetValues("X-WNS-Type").Should().Equal("wns/raw");
            request.Content!.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
            request.Content.Headers.ContentLength.Should().Be(Encoding.UTF8.GetByteCount(payload));
            (await request.Content.ReadAsStringAsync()).Should().Be(payload);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var sender = Create(handler, settings);

        (await sender.SendAsync(Channel, payload, default)).Should().Be(WnsDeliveryOutcome.Delivered);
        (await sender.SendAsync(Channel, payload, default)).Should().Be(WnsDeliveryOutcome.Delivered);
        authCalls.Should().Be(1);
        pushCalls.Should().Be(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, WnsDeliveryOutcome.Stale)]
    [InlineData(HttpStatusCode.Gone, WnsDeliveryOutcome.Stale)]
    [InlineData(HttpStatusCode.Forbidden, WnsDeliveryOutcome.Retained)]
    [InlineData(HttpStatusCode.BadRequest, WnsDeliveryOutcome.Retained)]
    [InlineData(HttpStatusCode.NotAcceptable, WnsDeliveryOutcome.Retained)]
    [InlineData(HttpStatusCode.ServiceUnavailable, WnsDeliveryOutcome.Retained)]
    [InlineData(HttpStatusCode.TemporaryRedirect, WnsDeliveryOutcome.Retained)]
    public async Task Send_ClassifiesDocumentedDeliveryStatus(HttpStatusCode status, WnsDeliveryOutcome outcome)
    {
        using var sender = Create(new Handler(request => Task.FromResult(request.RequestUri!.Host == "login.microsoftonline.com"
            ? Json(TokenResponse) : new HttpResponseMessage(status))), Configured());
        (await sender.SendAsync(Channel, "{}", default)).Should().Be(outcome);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unauthorized_RefreshesTokenOnce(bool retrySucceeds)
    {
        var authCalls = 0;
        var pushCalls = 0;
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return Task.FromResult(Json(TokenResponse.Replace("documented-test-access-token", $"access-token-{++authCalls}")));
            request.Headers.Authorization!.Parameter.Should().Be($"access-token-{authCalls}");
            return Task.FromResult(new HttpResponseMessage(++pushCalls == 2 && retrySucceeds
                ? HttpStatusCode.OK : HttpStatusCode.Unauthorized));
        });
        using var sender = Create(handler, Configured());
        (await sender.SendAsync(Channel, "{}", default)).Should().Be(retrySucceeds ? WnsDeliveryOutcome.Delivered : WnsDeliveryOutcome.Retained);
        authCalls.Should().Be(2);
        pushCalls.Should().Be(2);
    }

    [Fact]
    public async Task TokenExpiry_RefreshesBeforeNextDelivery()
    {
        var time = new TestTime();
        var authCalls = 0;
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
            {
                authCalls++;
                return Task.FromResult(Json(TokenResponse));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var sender = new WnsPushSender(new HttpClient(handler), Options.Create(Configured()), NullLogger<WnsPushSender>.Instance, time);
        (await sender.SendAsync(Channel, "{}", default)).Should().Be(WnsDeliveryOutcome.Delivered);
        time.Advance(TimeSpan.FromDays(1));
        (await sender.SendAsync(Channel, "{}", default)).Should().Be(WnsDeliveryOutcome.Delivered);
        authCalls.Should().Be(2);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"access_token\":null}")]
    [InlineData("{\"access_token\":\"\"}")]
    [InlineData("not-json")]
    [InlineData("[]")]
    public async Task MalformedTokenResponse_RetainsChannel(string body)
    {
        var handler = new Handler(_ => Task.FromResult(Json(body)));
        using var sender = Create(handler, Configured());
        (await sender.SendAsync(Channel, "{}", default)).Should().Be(WnsDeliveryOutcome.Retained);
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AuthenticationFailure_DoesNotAttemptDelivery()
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
        using var sender = Create(handler, Configured());
        (await sender.SendAsync(Channel, "{}", default)).Should().Be(WnsDeliveryOutcome.Retained);
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task MissingCredentials_AreInertAndQuiet_AndUntrustedChannelsNeverReceiveTokens()
    {
        var configured = Configured();
        var variants = new[]
        {
            new WnsSettings(),
            new WnsSettings { TenantId = configured.TenantId, ClientId = configured.ClientId },
            new WnsSettings { ClientSecret = "secret", ClientId = configured.ClientId },
            new WnsSettings { ClientSecret = "secret", TenantId = configured.TenantId }
        };
        var handler = new Handler(_ => throw new InvalidOperationException("Network must remain unused"));
        var logger = Substitute.For<ILogger<WnsPushSender>>();
        foreach (var settings in variants)
        {
            settings.IsConfigured.Should().BeFalse();
            using var sender = new WnsPushSender(new HttpClient(handler), Options.Create(settings), logger, TimeProvider.System);
            (await sender.SendAsync(Channel, "{}", default)).Should().Be(WnsDeliveryOutcome.Retained);
        }
        using var configuredSender = Create(handler, configured);
        (await configuredSender.SendAsync("https://notify.windows.com.attacker.org/", "{}", default)).Should().Be(WnsDeliveryOutcome.Retained);
        handler.Calls.Should().Be(0);
        logger.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var handler = new Handler(_ => Task.FromResult(Json(TokenResponse)));
        using var sender = Create(handler, Configured());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var act = () => sender.SendAsync(Channel, "{}", cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static WnsPushSender Create(Handler handler, WnsSettings settings) =>
        new(new HttpClient(handler), Options.Create(settings), NullLogger<WnsPushSender>.Instance, TimeProvider.System);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return respond(request);
        }
    }

    private sealed class TestTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
