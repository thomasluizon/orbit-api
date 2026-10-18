using System.Net;
using System.Text.Json;
using FluentAssertions;
using Google.Apis.AndroidPublisher.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class GooglePlayBillingServiceTests
{
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task VerifyAsync_MissingPurchase_ReturnsNull(HttpStatusCode status)
    {
        using var publisher = CreatePublisher(new RecordingHandler { Status = status });

        var purchase = await CreateService(publisher).VerifyAsync("orbit_pro", "purchase-token", CancellationToken.None);

        purchase.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task CancelAsync_DisappearedAfterVerification_Completes(HttpStatusCode status)
    {
        using var publisher = CreatePublisher(new RecordingHandler { Status = status });

        var run = () => CreateService(publisher).CancelAsync("purchase-token", CancellationToken.None);

        await run.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task VerifyAsync_ProviderOutage_RemainsRetryable(HttpStatusCode status)
    {
        using var publisher = CreatePublisher(new RecordingHandler { Status = status });

        var run = () => CreateService(publisher).VerifyAsync("orbit_pro", "purchase-token", CancellationToken.None);

        await run.Should().ThrowAsync<BillingProviderException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubscriptionProviderTimeout_RemainsRetryable(bool cancel)
    {
        using var publisher = CreatePublisher(new RecordingHandler { Failure = new TaskCanceledException("provider timeout") });
        var service = CreateService(publisher);

        Func<Task> run = () => cancel
            ? service.CancelAsync("purchase-token", CancellationToken.None)
            : service.VerifyAsync("orbit_pro", "purchase-token", CancellationToken.None);

        await run.Should().ThrowAsync<BillingProviderException>();
    }

    [Fact]
    public async Task CancelAsync_UsesV2CancelAndUserRequestedRenewalStop()
    {
        var handler = new RecordingHandler();
        using var publisher = CreatePublisher(handler);
        var service = CreateService(publisher);

        await service.CancelAsync("purchase-token", CancellationToken.None);

        handler.Calls.Should().Be(1);
        handler.Method.Should().Be(HttpMethod.Post);
        handler.Path.Should().Be("/androidpublisher/v3/applications/org.orbit.app/purchases/subscriptionsv2/tokens/purchase-token:cancel");
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("cancellationContext").GetProperty("cancellationType").GetString()
            .Should().Be("USER_REQUESTED_STOP_RENEWALS");
    }

    [Fact]
    public async Task CancelAsync_ProviderFailure_IsRetryableBillingFailure()
    {
        var handler = new RecordingHandler { Status = HttpStatusCode.BadRequest };
        using var publisher = CreatePublisher(handler);
        var service = CreateService(publisher);

        var run = () => service.CancelAsync("purchase-token", CancellationToken.None);

        (await run.Should().ThrowAsync<BillingProviderException>()).Which.InnerException.Should().BeOfType<Google.GoogleApiException>();
    }

    [Fact]
    public async Task CancelAsync_Cancellation_IsNotWrapped()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var publisher = CreatePublisher(new RecordingHandler());
        var service = CreateService(publisher);

        var run = () => service.CancelAsync("purchase-token", cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    private static GooglePlayBillingService CreateService(AndroidPublisherService publisher) =>
        new(publisher, Options.Create(new GooglePlaySettings { PackageName = "org.orbit.app" }));

    private static AndroidPublisherService CreatePublisher(RecordingHandler handler)
    {
        var factory = Substitute.For<Google.Apis.Http.IHttpClientFactory>();
        factory.CreateHttpClient(Arg.Any<CreateHttpClientArgs>())
            .Returns(new ConfigurableHttpClient(new ConfigurableMessageHandler(handler)));
        return new AndroidPublisherService(new BaseClientService.Initializer { HttpClientFactory = factory, GZipEnabled = false });
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public Exception? Failure { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null)
                throw Failure;
            Calls++;
            Method = request.Method;
            Path = request.RequestUri!.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(Status) { Content = new StringContent("") };
        }
    }
}
