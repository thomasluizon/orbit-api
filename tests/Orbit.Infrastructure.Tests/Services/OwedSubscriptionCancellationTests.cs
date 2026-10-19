using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Auth.Jobs;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class OwedSubscriptionCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scanner_RetriesAfterBackoff_ThenRemovesDebt(bool googlePlay)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(googlePlay);
        fixture.FailProviders();

        await fixture.ScanAsync();
        using (var context = fixture.Factory.CreateContext())
        {
            var pending = await context.OwedSubscriptionCancellations.SingleAsync();
            pending.Attempts.Should().Be(1);
            pending.NextAttemptAtUtc.Should().Be(fixture.Clock.UtcNow.UtcDateTime.AddMinutes(1));
        }
        await fixture.ScanAsync();
        await fixture.AssertCancellationCallsAsync(googlePlay, 1);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
        fixture.Stripe.SetSubscriptionCancelAtPeriodEndAsync("sub_owed", true, Arg.Any<CancellationToken>(), Arg.Any<string?>()).Returns(Task.CompletedTask);
        fixture.Play.CancelAsync("token", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await fixture.ScanAsync();
        await fixture.ScanAsync();

        using var final = fixture.Factory.CreateContext();
        (await final.OwedSubscriptionCancellations.AnyAsync()).Should().BeFalse();
        await fixture.AssertCancellationCallsAsync(googlePlay, 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scanner_ExhaustionRetainsRecordAndAlertsWithoutToken_ThenRemindsDaily(bool googlePlay)
    {
        using var fixture = new Fixture();
        var record = await fixture.SeedAsync(googlePlay);
        fixture.FailProviders();
        for (var attempt = 1; attempt <= OwedSubscriptionCancellation.MaxAttempts; attempt++)
        {
            await fixture.ScanAsync();
            using var context = fixture.Factory.CreateContext();
            var pending = await context.OwedSubscriptionCancellations.SingleAsync();
            pending.Attempts.Should().Be(attempt);
            fixture.Clock.UtcNow = new DateTimeOffset(pending.NextAttemptAtUtc);
        }
        fixture.Errors().Should().ContainSingle().Which.Should().Contain(record.Id.ToString());
        fixture.Errors().Should().OnlyContain(message => !message.Contains("sub_owed") && !message.Contains("token"));

        await fixture.ScanAsync();
        await fixture.ScanAsync();

        await fixture.AssertCancellationCallsAsync(googlePlay, OwedSubscriptionCancellation.MaxAttempts);
        fixture.Errors().Should().HaveCount(2);
        using var final = fixture.Factory.CreateContext();
        var exhausted = await final.OwedSubscriptionCancellations.SingleAsync();
        exhausted.ExhaustedAtUtc.Should().NotBeNull();
        exhausted.ProviderIdentifier.Should().Be(googlePlay ? "token" : "sub_owed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyJob_MissingUser_RecordsBothProvidersBeforeAttempt_AndNeverRestores(bool cancel)
    {
        using var fixture = new Fixture();
        fixture.FailProviders();
        fixture.Clock.UtcNow = DateTimeOffset.UtcNow.AddSeconds(1);
        using (var context = fixture.Factory.CreateContext())
        {
            var unitOfWork = new UnitOfWork(context, new DatabaseConnectionSettings());
            var job = new CancelSubscriptionOnDeletionJob(new GenericRepository<User>(context), unitOfWork,
                fixture.Stripe, fixture.Play, fixture.Options, fixture.CreateJob(context, unitOfWork));
            await job.ExecuteAsync(Guid.NewGuid(), "sub_owed", "token", cancel, CancellationToken.None);
        }
        using var final = fixture.Factory.CreateContext();
        var records = await final.OwedSubscriptionCancellations.ToListAsync();
        records.Should().HaveCount(cancel ? 2 : 0);
        records.Should().OnlyContain(record => record.Attempts == 1);
        await fixture.Stripe.DidNotReceive().SetSubscriptionCancelAtPeriodEndAsync(Arg.Any<string>(), false, Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task RecordAsync_RepeatedHandoffDeduplicates_AndOneProviderFailureDoesNotBlockOther()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(false);
        await fixture.SeedAsync(true);
        using (var context = fixture.Factory.CreateContext())
            await fixture.CreateJob(context).RecordAsync(SubscriptionSource.Stripe, "sub_owed", CancellationToken.None);
        fixture.Stripe.SetSubscriptionCancelAtPeriodEndAsync("sub_owed", true, Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .ThrowsAsync(new BillingProviderException("unavailable"));

        await fixture.ScanAsync();

        using var final = fixture.Factory.CreateContext();
        var remaining = await final.OwedSubscriptionCancellations.SingleAsync();
        remaining.Provider.Should().Be(SubscriptionSource.Stripe);
        remaining.Attempts.Should().Be(1);
        await fixture.Play.Received(1).CancelAsync("token", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Scanner_AlreadyStoppedOrMissingProvider_ClearsDebtWithoutMutation(bool googlePlay, bool missing)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(googlePlay);
        fixture.Stripe.GetSubscriptionDetailsAsync("sub_owed", Arg.Any<CancellationToken>())
            .Returns(missing ? null : new BillingSubscriptionDetails("active", DateTime.UtcNow, true, SubscriptionInterval.Monthly, 1000, "usd", null));
        fixture.Play.VerifyAsync("orbit_pro", "token", Arg.Any<CancellationToken>())
            .Returns(missing ? null : new PlaySubscriptionState(false, DateTime.UtcNow, null, true, "orbit_pro", null, null, CanRenew: false));

        await fixture.ScanAsync();

        await fixture.AssertCancellationCallsAsync(googlePlay, 0);
        using var final = fixture.Factory.CreateContext();
        (await final.OwedSubscriptionCancellations.AnyAsync()).Should().BeFalse();
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class Fixture : IDisposable
    {
        public SqliteOrbitDbContextFactory Factory { get; } = new();
        public IBillingService Stripe { get; } = Substitute.For<IBillingService>();
        public IPlayBillingService Play { get; } = Substitute.For<IPlayBillingService>();
        public MutableClock Clock { get; } = new();
        public IOptions<GooglePlaySettings> Options { get; } = Microsoft.Extensions.Options.Options.Create(new GooglePlaySettings { ProductId = "orbit_pro" });
        private ILogger<OwedSubscriptionCancellationJob> Logger { get; } = Substitute.For<ILogger<OwedSubscriptionCancellationJob>>();

        public Fixture()
        {
            Stripe.GetSubscriptionDetailsAsync("sub_owed", Arg.Any<CancellationToken>())
                .Returns(new BillingSubscriptionDetails("active", DateTime.UtcNow.AddDays(20), false, SubscriptionInterval.Monthly, 1000, "usd", null));
            Play.VerifyAsync("orbit_pro", "token", Arg.Any<CancellationToken>())
                .Returns(new PlaySubscriptionState(true, DateTime.UtcNow.AddDays(20), null, true, "orbit_pro", null, null, CanRenew: true));
        }

        public OwedSubscriptionCancellationJob CreateJob(OrbitDbContext context, IUnitOfWork? unitOfWork = null) =>
            new(new GenericRepository<OwedSubscriptionCancellation>(context),
                unitOfWork ?? new UnitOfWork(context, new DatabaseConnectionSettings()), Stripe, Play, Options, Logger, Clock);

        public async Task<OwedSubscriptionCancellation> SeedAsync(bool googlePlay)
        {
            using var context = Factory.CreateContext();
            var record = OwedSubscriptionCancellation.Create(googlePlay ? SubscriptionSource.GooglePlay : SubscriptionSource.Stripe,
                googlePlay ? "token" : "sub_owed");
            context.OwedSubscriptionCancellations.Add(record);
            await context.SaveChangesAsync();
            Clock.UtcNow = DateTimeOffset.UtcNow;
            return record;
        }

        public void FailProviders()
        {
            Stripe.SetSubscriptionCancelAtPeriodEndAsync("sub_owed", true, Arg.Any<CancellationToken>(), Arg.Any<string?>())
                .ThrowsAsync(new BillingProviderException("unavailable"));
            Play.CancelAsync("token", Arg.Any<CancellationToken>()).ThrowsAsync(new BillingProviderException("unavailable"));
        }

        public async Task ScanAsync()
        {
            using var provider = new ServiceCollection()
                .AddScoped(_ => Factory.CreateContext())
                .AddScoped(serviceProvider => CreateJob(serviceProvider.GetRequiredService<OrbitDbContext>()))
                .BuildServiceProvider();
            using var service = new OwedSubscriptionCancellationService(provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<OwedSubscriptionCancellationService>.Instance, Clock);
            await service.RunAsync(CancellationToken.None);
        }

        public async Task AssertCancellationCallsAsync(bool googlePlay, int count)
        {
            if (googlePlay)
                await Play.Received(count).CancelAsync("token", Arg.Any<CancellationToken>());
            else
                await Stripe.Received(count).SetSubscriptionCancelAtPeriodEndAsync("sub_owed", true, Arg.Any<CancellationToken>(), Arg.Any<string?>());
        }

        public List<string> Errors() => Logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == "Log" && (LogLevel)call.GetArguments()[0]! == LogLevel.Error)
            .Select(call => call.GetArguments()[2]!.ToString()!).ToList();

        public void Dispose() => Factory.Dispose();
    }
}
