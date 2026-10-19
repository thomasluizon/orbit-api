using System.Net;
using System.Text.Json;
using FluentAssertions;
using Google.Apis.AndroidPublisher.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Google.Apis.AndroidPublisher.v3.Data;
using Google.Apis.Json;
using Orbit.Application.Auth.Jobs;
using User = Orbit.Domain.Entities.User;
using Orbit.Domain.Interfaces;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyAsync_PausedAutoRenewal_StopsRenewalWithoutGrantingEntitlement(bool hasResumeTime)
    {
        var wirePurchase = new SubscriptionPurchaseV2
        {
            SubscriptionState = "SUBSCRIPTION_STATE_PAUSED",
            PausedStateContext = hasResumeTime
                ? new PausedStateContext { AutoResumeTimeDateTimeOffset = DateTimeOffset.UtcNow.AddDays(60) }
                : null,
            LineItems = new[]
            {
                new SubscriptionPurchaseLineItem
                {
                    ProductId = "orbit_pro",
                    ExpiryTimeDateTimeOffset = DateTimeOffset.UtcNow.AddDays(-1),
                    AutoRenewingPlan = new AutoRenewingPlan { AutoRenewEnabled = true }
                }
            }
        };
        var handler = new RecordingHandler { ResponseBody = NewtonsoftJsonSerializer.Instance.Serialize(wirePurchase) };
        using var publisher = CreatePublisher(handler);
        var play = CreateService(publisher);
        var purchase = await play.VerifyAsync("orbit_pro", "purchase-token", CancellationToken.None);
        purchase!.IsActive.Should().BeFalse();
        var user = User.Create("Paused", "paused@example.com").Value;
        user.SetPlaySubscription("purchase-token", purchase.ExpiresAt);
        user.Deactivate(DateTime.UtcNow.AddDays(7));
        using var factory = new Orbit.Infrastructure.Tests.Persistence.SqliteOrbitDbContextFactory();
        factory.Context.Users.Add(user);
        await factory.Context.SaveChangesAsync();
        var unitOfWork = new Orbit.Infrastructure.Persistence.UnitOfWork(factory.Context,
            new Orbit.Infrastructure.Configuration.DatabaseConnectionSettings());
        var stripe = Substitute.For<IBillingService>();
        var options = Options.Create(new GooglePlaySettings { ProductId = "orbit_pro" });
        var job = new CancelSubscriptionOnDeletionJob(new Orbit.Infrastructure.Persistence.GenericRepository<User>(factory.Context),
            unitOfWork, stripe, play, options,
            new OwedSubscriptionCancellationJob(new Orbit.Infrastructure.Persistence.GenericRepository<Orbit.Domain.Entities.OwedSubscriptionCancellation>(factory.Context),
                unitOfWork, stripe, play, options,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<OwedSubscriptionCancellationJob>.Instance, TimeProvider.System));

        await job.CancelBeforeDeletionAsync(user, CancellationToken.None);

        handler.Method.Should().Be(HttpMethod.Post);
        handler.Path.Should().EndWith("purchase-token:cancel");
        handler.Calls.Should().Be(4);
    }

    [Theory]
    [InlineData("SUBSCRIPTION_STATE_ACTIVE", true, true, true)]
    [InlineData("SUBSCRIPTION_STATE_ACTIVE", false, true, false)]
    [InlineData("SUBSCRIPTION_STATE_ACTIVE", null, true, true)]
    [InlineData("SUBSCRIPTION_STATE_IN_GRACE_PERIOD", true, true, true)]
    [InlineData("SUBSCRIPTION_STATE_ON_HOLD", true, false, true)]
    [InlineData("SUBSCRIPTION_STATE_PAUSED", true, false, true)]
    [InlineData("SUBSCRIPTION_STATE_PAUSED", false, false, false)]
    [InlineData("SUBSCRIPTION_STATE_CANCELED", true, true, false)]
    [InlineData("SUBSCRIPTION_STATE_EXPIRED", true, false, false)]
    [InlineData(null, true, false, true)]
    public async Task VerifyAsync_SeparatesRenewabilityFromEntitlement(string? state, bool? autoRenew,
        bool entitled, bool canRenew)
    {
        var wirePurchase = new SubscriptionPurchaseV2
        {
            SubscriptionState = state,
            LineItems = new[]
            {
                new SubscriptionPurchaseLineItem
                {
                    ProductId = "orbit_pro",
                    ExpiryTimeDateTimeOffset = DateTimeOffset.UtcNow.AddDays(20),
                    AutoRenewingPlan = new AutoRenewingPlan { AutoRenewEnabled = autoRenew }
                }
            }
        };
        using var publisher = CreatePublisher(new RecordingHandler
        {
            ResponseBody = NewtonsoftJsonSerializer.Instance.Serialize(wirePurchase)
        });

        var purchase = await CreateService(publisher).VerifyAsync("orbit_pro", "purchase-token", CancellationToken.None);

        purchase!.IsActive.Should().Be(entitled);
        purchase.CanRenew.Should().Be(canRenew);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyAsync_AllLineItems_DetectsAnyRenewablePlan(bool addRenewableItem)
    {
        var lineItems = new List<SubscriptionPurchaseLineItem>
        {
            new() { ProductId = "orbit_pro", ExpiryTimeDateTimeOffset = DateTimeOffset.UtcNow.AddDays(20),
                AutoRenewingPlan = addRenewableItem ? new AutoRenewingPlan { AutoRenewEnabled = false } : null,
                PrepaidPlan = addRenewableItem ? null : new PrepaidPlan() }
        };
        if (addRenewableItem)
            lineItems.Add(new SubscriptionPurchaseLineItem { AutoRenewingPlan = new AutoRenewingPlan { AutoRenewEnabled = true } });
        using var publisher = CreatePublisher(new RecordingHandler
        {
            ResponseBody = NewtonsoftJsonSerializer.Instance.Serialize(new SubscriptionPurchaseV2
            {
                SubscriptionState = "SUBSCRIPTION_STATE_ACTIVE",
                LineItems = lineItems
            })
        });

        var purchase = await CreateService(publisher).VerifyAsync("orbit_pro", "purchase-token", CancellationToken.None);

        purchase!.CanRenew.Should().Be(addRenewableItem);
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
        public string ResponseBody { get; init; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null)
                throw Failure;
            Calls++;
            Method = request.Method;
            Path = request.RequestUri!.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(Status) { Content = new StringContent(request.Method == HttpMethod.Get ? ResponseBody : "") };
        }
    }
}
