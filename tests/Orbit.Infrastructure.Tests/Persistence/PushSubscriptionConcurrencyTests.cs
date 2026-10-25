using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Notifications.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public sealed class PushSubscriptionConcurrencyTests : IDisposable
{
    private readonly BeforeSaveInterceptor _beforeSave = new();
    private readonly SqliteOrbitDbContextFactory _factory;
    private readonly Guid _accountA;
    private readonly Guid _accountB;
    private readonly Guid _device = Guid.NewGuid();
    private readonly List<string> _destinations = [];
    private readonly VapidSettings _vapid;
    private readonly string _webKey;
    private readonly string _webAuth = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
    private Func<Task>? _duringDelivery;

    public PushSubscriptionConcurrencyTests()
    {
        _factory = new SqliteOrbitDbContextFactory(_beforeSave);
        var first = User.Create("First", "first@example.com").Value;
        var second = User.Create("Second", "second@example.com").Value;
        _accountA = first.Id;
        _accountB = second.Id;
        _factory.Context.Users.AddRange(first, second);
        _factory.Context.SaveChanges();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(true);
        var publicKey = new byte[65];
        publicKey[0] = 4;
        parameters.Q.X!.CopyTo(publicKey, 1);
        parameters.Q.Y!.CopyTo(publicKey, 33);
        _webKey = Convert.ToBase64String(publicKey);
        _vapid = new VapidSettings
        {
            PublicKey = Base64Url(publicKey),
            PrivateKey = Base64Url(parameters.D!),
            Subject = "mailto:push@example.com"
        };
    }

    [Theory]
    [InlineData("wns")]
    [InlineData("apns")]
    public async Task StaleDelivery_AfterRenewal_PreservesRenewedRegistration(string transport)
    {
        var oldEndpoint = Endpoint(transport, "old");
        var newEndpoint = Endpoint(transport, "new");
        (await Subscribe(_accountA, oldEndpoint, transport)).IsSuccess.Should().BeTrue();
        var original = await ReadSingle();
        await using var deliveryContext = _factory.CreateContext();
        var service = CreateService(deliveryContext);
        _duringDelivery = async () =>
        {
            (await Subscribe(_accountA, newEndpoint, transport)).IsSuccess.Should().BeTrue();
            var renewed = await ReadSingle();
            renewed.Id.Should().Be(original.Id);
            renewed.Endpoint.Should().Be(newEndpoint);
        };

        (await service.TrySendToUserAsync(_accountA, "Reminder", "Body", "/today")).Should().BeFalse();

        var surviving = await ReadSingle();
        surviving.Id.Should().Be(original.Id);
        surviving.UserId.Should().Be(_accountA);
        surviving.Endpoint.Should().Be(newEndpoint);
        await using var nextDelivery = _factory.CreateContext();
        (await CreateService(nextDelivery).TrySendToUserAsync(_accountA, "Reminder", "Body")).Should().BeTrue();
        _destinations.Should().Equal(oldEndpoint, newEndpoint);
    }

    [Theory]
    [InlineData("wns")]
    [InlineData("apns")]
    [InlineData("web")]
    public async Task StaleDelivery_AfterOwnershipTransfer_PreservesNewOwnersRegistration(string transport)
    {
        var endpoint = Endpoint(transport, "shared");
        (await Subscribe(_accountA, endpoint, transport)).IsSuccess.Should().BeTrue();
        await using var deliveryContext = _factory.CreateContext();
        _duringDelivery = async () =>
            (await Subscribe(_accountB, endpoint, transport)).IsSuccess.Should().BeTrue();

        (await CreateService(deliveryContext).TrySendToUserAsync(_accountA, "Reminder", "Body")).Should().BeFalse();

        (await ReadSingle()).UserId.Should().Be(_accountB);
        await using var nextDelivery = _factory.CreateContext();
        (await CreateService(nextDelivery).TrySendToUserAsync(_accountB, "Private reminder", "Private content")).Should().BeTrue();
        _destinations.Should().Equal(endpoint, endpoint);
    }

    [Theory]
    [InlineData("wns")]
    [InlineData("apns")]
    public async Task Renewal_AfterOwnershipTransfer_NeverRedirectsNewOwnersNotification(string transport)
    {
        var oldEndpoint = Endpoint(transport, "shared");
        var newEndpoint = Endpoint(transport, "account-a");
        (await Subscribe(_accountA, oldEndpoint, transport)).IsSuccess.Should().BeTrue();
        var original = await ReadSingle();
        await using var renewalContext = _factory.CreateContext();
        _beforeSave.Context = renewalContext;
        _beforeSave.BeforeSave = async () =>
        {
            var renewing = renewalContext.ChangeTracker.Entries<PushSubscription>().Single().Entity;
            renewing.UserId.Should().Be(_accountA);
            renewing.Endpoint.Should().Be(newEndpoint);
            (await Subscribe(_accountB, oldEndpoint, transport)).IsSuccess.Should().BeTrue();
            var transferred = await ReadSingle();
            transferred.Id.Should().Be(original.Id);
            transferred.UserId.Should().Be(_accountB);
            transferred.Endpoint.Should().Be(oldEndpoint);
        };

        var result = await Handler(renewalContext).Handle(Command(_accountA, newEndpoint, transport), CancellationToken.None);

        _beforeSave.Invoked.Should().BeTrue();
        await using var deliveryContext = _factory.CreateContext();
        (await CreateService(deliveryContext).TrySendToUserAsync(_accountB, "Private reminder", "Private content")).Should().BeTrue();
        _destinations.Should().Equal([oldEndpoint], "B must keep its claimed channel after A's stale renewal");
        _destinations.Should().NotContain(newEndpoint);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ConcurrentUpdateConflict);
        (await ReadSingle()).Endpoint.Should().Be(oldEndpoint);
        renewalContext.ChangeTracker.HasChanges().Should().BeFalse();
    }

    private async Task<PushSubscription> ReadSingle()
    {
        await using var context = _factory.CreateContext();
        return await context.PushSubscriptions.AsNoTracking().SingleAsync();
    }

    private async Task<Result> Subscribe(Guid userId, string endpoint, string transport)
    {
        await using var context = _factory.CreateContext();
        return await Handler(context).Handle(Command(userId, endpoint, transport), CancellationToken.None);
    }

    private static SubscribePushCommandHandler Handler(OrbitDbContext context) =>
        new(new GenericRepository<PushSubscription>(context), new UnitOfWork(context, new DatabaseConnectionSettings()));

    private SubscribePushCommand Command(Guid userId, string endpoint, string transport) =>
        new(userId, endpoint, transport == "web" ? _webKey : transport,
            transport == "web" ? _webAuth : transport,
            transport == "apns" ? "org.useorbit.desktop" : null,
            transport == "web" ? null : _device);

    private static string Endpoint(string transport, string token) => transport switch
    {
        "wns" => $"https://dm3p.notify.windows.com/?token={token}",
        "web" => $"https://fcm.googleapis.com/{token}",
        _ => token
    };

    private PushNotificationService CreateService(OrbitDbContext context)
    {
        var wns = Substitute.For<IWnsPushSender>();
        wns.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async call => await Deliver(call.ArgAt<string>(0)) ? WnsDeliveryOutcome.Delivered : WnsDeliveryOutcome.Stale);
        var apns = Substitute.For<IApnsPushSender>();
        apns.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(async call => await Deliver(call.ArgAt<string>(0)) ? ApnsDeliveryOutcome.Delivered : ApnsDeliveryOutcome.Stale);
        return new PushNotificationService(context, Options.Create(_vapid), NullLogger<PushNotificationService>.Instance,
            new HttpClient(new ProviderHandler(async endpoint => await Deliver(endpoint))), apns, wns);
    }

    private async Task<bool> Deliver(string endpoint)
    {
        _destinations.Add(endpoint);
        if (_duringDelivery is not { } interleave)
            return true;
        _duringDelivery = null;
        await interleave();
        return false;
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => _factory.Dispose();

    private sealed class ProviderHandler(Func<string, Task<bool>> deliver) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(await deliver(request.RequestUri!.AbsoluteUri) ? HttpStatusCode.Created : HttpStatusCode.Gone);
    }

    private sealed class BeforeSaveInterceptor : SaveChangesInterceptor
    {
        internal DbContext? Context { get; set; }
        internal Func<Task>? BeforeSave { get; set; }
        internal bool Invoked { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context == Context && BeforeSave is { } interleave)
            {
                BeforeSave = null;
                Invoked = true;
                await interleave();
            }
            return result;
        }
    }
}
