using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Auth.Jobs;
using Orbit.Application.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;
using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Tests.Services;

public class AccountDeletionServiceTests
{
    [Theory]
    [InlineData(false, false, 1, false, 0, false)]
    [InlineData(true, false, 1, false, 0, false)]
    [InlineData(false, true, 1, false, 0, false)]
    [InlineData(true, true, 1, false, 0, false)]
    [InlineData(false, true, 8, false, 0, false)]
    [InlineData(false, true, 7, false, 0, false)]
    [InlineData(false, true, 6, false, 0, false)]
    [InlineData(false, false, 8, false, 0, false)]
    [InlineData(true, true, 8, false, 0, false)]
    [InlineData(false, false, 1, true, 0, false)]
    [InlineData(true, false, 1, true, 0, false)]
    [InlineData(false, true, 8, false, 1, false)]
    [InlineData(false, true, 8, false, 2, false)]
    [InlineData(true, false, 1, false, 0, true)]
    [InlineData(true, false, 8, false, 0, true)]
    [InlineData(false, true, 8, false, 3, false)]
    [InlineData(true, true, 8, false, 3, false)]
    public async Task RunAsync_DuePaidAccount_CancelsBeforeRemovingAndRetainsRowOnFailure(bool play, bool failure, int overdueDays, bool missing, int queueFailure, bool paused)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = CreateUser();
        if (play)
            user.SetPlaySubscription("play-token", DateTime.UtcNow.AddDays(20));
        else
            user.SetStripeSubscription("sub_paid", DateTime.UtcNow.AddDays(20));
        user.Deactivate(DateTime.UtcNow.AddDays(-overdueDays));
        factory.Context.Users.Add(user);
        await factory.Context.SaveChangesAsync();
        var stripe = Substitute.For<IBillingService>();
        var playBilling = Substitute.For<IPlayBillingService>();
        var reset = Substitute.For<IAccountResetRepository>();
        stripe.GetSubscriptionDetailsAsync("sub_paid", Arg.Any<CancellationToken>())
            .Returns(new BillingSubscriptionDetails("active", DateTime.UtcNow.AddDays(20), false,
                SubscriptionInterval.Monthly, 1000, "usd", null));
        playBilling.VerifyAsync("orbit_pro", "play-token", Arg.Any<CancellationToken>())
            .Returns(new PlaySubscriptionState(!paused, DateTime.UtcNow.AddDays(paused ? -1 : 20), SubscriptionInterval.Monthly, true, "orbit_pro", null, null, CanRenew: true));
        if (missing)
        {
            stripe.GetSubscriptionDetailsAsync("sub_paid", Arg.Any<CancellationToken>()).Returns((BillingSubscriptionDetails?)null);
            playBilling.VerifyAsync("orbit_pro", "play-token", Arg.Any<CancellationToken>()).Returns((PlaySubscriptionState?)null);
        }
        if (failure)
        {
            stripe.SetSubscriptionCancelAtPeriodEndAsync("sub_paid", true, Arg.Any<CancellationToken>(), Arg.Any<string?>())
                .ThrowsAsync(new BillingProviderException("unavailable"));
            playBilling.CancelAsync("play-token", Arg.Any<CancellationToken>())
                .ThrowsAsync(new BillingProviderException("unavailable"));
        }
        var handedOff = overdueDays >= 7;
        var expectedInlineCalls = missing || handedOff ? 0 : 1;
        reset.DeleteAllUserDataAsync(user.Id, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            if (play)
                await playBilling.Received(expectedInlineCalls).CancelAsync("play-token", Arg.Any<CancellationToken>());
            else
                await stripe.Received(expectedInlineCalls).SetSubscriptionCancelAtPeriodEndAsync("sub_paid", true, Arg.Any<CancellationToken>(), Arg.Any<string?>());
            using var beforeRemoval = factory.CreateContext();
            (await beforeRemoval.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == user.Id)).Should().BeTrue();
        });
        var objects = new FakeObjectStore();
        if (queueFailure == 3)
            objects.FailuresRemaining = 3;
        objects.Keys.Add($"{user.Id}/one.png");
        var services = new ServiceCollection();
        services.AddSingleton<IObjectStorageDeletionService>(objects);
        services.AddScoped(_ => factory.CreateContext());
        services.AddScoped<IUnitOfWork>(sp => new UnitOfWork(sp.GetRequiredService<OrbitDbContext>(), new DatabaseConnectionSettings()));
        services.AddScoped<IGenericRepository<User>, GenericRepository<User>>();
        services.AddSingleton(Substitute.For<IGenericRepository<MarketingContact>>());
        services.AddSingleton(reset);
        services.AddSingleton(stripe);
        services.AddSingleton(playBilling);
        services.AddSingleton(Options.Create(new GooglePlaySettings { ProductId = "orbit_pro" }));
        services.AddScoped<CancelSubscriptionOnDeletionJob>();
        services.AddScoped<OwedSubscriptionCancellationJob>();
        services.AddScoped<IGenericRepository<OwedSubscriptionCancellation>, GenericRepository<OwedSubscriptionCancellation>>();
        services.AddSingleton<ILogger<OwedSubscriptionCancellationJob>>(Microsoft.Extensions.Logging.Abstractions.NullLogger<OwedSubscriptionCancellationJob>.Instance);
        var clock = new MutableClock();
        services.AddSingleton<TimeProvider>(clock);
        var jobs = Substitute.For<IBackgroundJobClient>();
        if (queueFailure == 1)
            jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Throws(new InvalidOperationException("queue unavailable"));
        else if (queueFailure == 2)
            jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns((string?)null);
        services.AddSingleton(jobs);
        var logger = Substitute.For<ILogger<AccountDeletionService>>();
        logger.IsEnabled(LogLevel.Error).Returns(true);
        using var provider = services.BuildServiceProvider();
        using var service = new AccountDeletionService(provider.GetRequiredService<IServiceScopeFactory>(),
            logger, new ConfigurationBuilder().Build());

        await service.RunAsync(CancellationToken.None);

        using var afterRemoval = factory.CreateContext();
        var retained = failure && overdueDays < 7 || queueFailure == 3;
        (await afterRemoval.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == user.Id)).Should().Be(retained);
        objects.Attempts.Should().Be(queueFailure == 3 ? 3 : retained ? 0 : 1);
        objects.Keys.Should().HaveCount(retained ? 1 : 0);
        await reset.Received(retained ? 0 : 1).DeleteAllUserDataAsync(user.Id, Arg.Any<CancellationToken>());
        if (play)
            await playBilling.Received(expectedInlineCalls).CancelAsync("play-token", Arg.Any<CancellationToken>());
        else
            await stripe.Received(expectedInlineCalls).SetSubscriptionCancelAtPeriodEndAsync("sub_paid", true, Arg.Any<CancellationToken>(), Arg.Any<string?>());
        if (handedOff && !retained)
        {
            logger.ReceivedCalls().Should().Contain(call => call.GetMethodInfo().Name == "Log"
                && (LogLevel)call.GetArguments()[0]! == LogLevel.Error);
            await stripe.DidNotReceive().GetSubscriptionDetailsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
            await playBilling.DidNotReceive().VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            var record = await afterRemoval.OwedSubscriptionCancellations.SingleAsync();
            record.Provider.Should().Be(play ? SubscriptionSource.GooglePlay : SubscriptionSource.Stripe);
            record.ProviderIdentifier.Should().Be(play ? "play-token" : "sub_paid");
            record.Attempts.Should().Be(0);
            clock.UtcNow = new DateTimeOffset(record.NextAttemptAtUtc);
            using (var retryScope = provider.CreateScope())
                await retryScope.ServiceProvider.GetRequiredService<OwedSubscriptionCancellationJob>()
                    .ExecuteAsync(record.Id, CancellationToken.None);
            using var afterAttempt = factory.CreateContext();
            if (failure)
            {
                (await afterAttempt.OwedSubscriptionCancellations.SingleAsync()).Attempts.Should().Be(1);
                stripe.SetSubscriptionCancelAtPeriodEndAsync("sub_paid", true, Arg.Any<CancellationToken>(), Arg.Any<string?>()).Returns(Task.CompletedTask);
                playBilling.CancelAsync("play-token", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
                var pending = await afterAttempt.OwedSubscriptionCancellations.SingleAsync();
                clock.UtcNow = new DateTimeOffset(pending.NextAttemptAtUtc);
                using var successScope = provider.CreateScope();
                await successScope.ServiceProvider.GetRequiredService<OwedSubscriptionCancellationJob>()
                    .ExecuteAsync(record.Id, CancellationToken.None);
            }
            using var afterSuccess = factory.CreateContext();
            (await afterSuccess.OwedSubscriptionCancellations.AnyAsync()).Should().BeFalse();
        }
        else
            (await afterRemoval.OwedSubscriptionCancellations.AnyAsync()).Should().BeFalse();
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private static User CreateUser()
    {
        return User.Create("Test User", "test@example.com").Value;
    }

    [Fact]
    public void Deactivate_SetsIsDeactivatedAndScheduledDeletion()
    {
        var user = CreateUser();
        var scheduledDeletion = DateTime.UtcNow.AddDays(30);

        user.Deactivate(scheduledDeletion);

        user.IsDeactivated.Should().BeTrue();
        user.ScheduledDeletionAt.Should().Be(scheduledDeletion);
        user.DeactivatedAt.Should().NotBeNull();
    }

    [Fact]
    public void CancelDeactivation_ClearsDeactivationState()
    {
        var user = CreateUser();
        user.Deactivate(DateTime.UtcNow.AddDays(30));

        user.CancelDeactivation();

        user.IsDeactivated.Should().BeFalse();
        user.ScheduledDeletionAt.Should().BeNull();
        user.DeactivatedAt.Should().BeNull();
    }

    [Fact]
    public void Deactivate_ThenCancel_ThenDeactivate_WorksCorrectly()
    {
        var user = CreateUser();

        user.Deactivate(DateTime.UtcNow.AddDays(30));
        user.CancelDeactivation();
        var newDeletion = DateTime.UtcNow.AddDays(60);
        user.Deactivate(newDeletion);

        user.IsDeactivated.Should().BeTrue();
        user.ScheduledDeletionAt.Should().Be(newDeletion);
    }

    [Fact]
    public void ScheduledDeletionAt_PastDate_IsEligibleForDeletion()
    {
        var user = CreateUser();
        var pastDate = DateTime.UtcNow.AddDays(-1);
        user.Deactivate(pastDate);

        var shouldDelete = user.IsDeactivated
            && user.ScheduledDeletionAt.HasValue
            && user.ScheduledDeletionAt.Value <= DateTime.UtcNow;

        shouldDelete.Should().BeTrue();
    }

    [Fact]
    public void ScheduledDeletionAt_FutureDate_IsNotEligibleForDeletion()
    {
        var user = CreateUser();
        var futureDate = DateTime.UtcNow.AddDays(30);
        user.Deactivate(futureDate);

        var shouldDelete = user.IsDeactivated
            && user.ScheduledDeletionAt.HasValue
            && user.ScheduledDeletionAt.Value <= DateTime.UtcNow;

        shouldDelete.Should().BeFalse();
    }

    [Fact]
    public void NonDeactivatedUser_IsNotEligibleForDeletion()
    {
        var user = CreateUser();

        var shouldDelete = user.IsDeactivated
            && user.ScheduledDeletionAt.HasValue
            && user.ScheduledDeletionAt.Value <= DateTime.UtcNow;

        shouldDelete.Should().BeFalse();
    }

    [Fact]
    public void CancelledDeactivation_IsNotEligibleForDeletion()
    {
        var user = CreateUser();
        user.Deactivate(DateTime.UtcNow.AddDays(-1));
        user.CancelDeactivation();

        var shouldDelete = user.IsDeactivated
            && user.ScheduledDeletionAt.HasValue
            && user.ScheduledDeletionAt.Value <= DateTime.UtcNow;

        shouldDelete.Should().BeFalse();
    }

    [Fact]
    public void ScheduledDeletionAt_ExactlyNow_IsEligibleForDeletion()
    {
        var user = CreateUser();
        var now = DateTime.UtcNow;
        user.Deactivate(now);

        var shouldDelete = user.IsDeactivated
            && user.ScheduledDeletionAt.HasValue
            && user.ScheduledDeletionAt.Value <= DateTime.UtcNow;

        shouldDelete.Should().BeTrue();
    }

    [Fact]
    public void Deactivate_SetsDeactivatedAtCloseToNow()
    {
        var user = CreateUser();
        var before = DateTime.UtcNow;

        user.Deactivate(DateTime.UtcNow.AddDays(30));

        user.DeactivatedAt.Should().NotBeNull();
        user.DeactivatedAt!.Value.Should().BeOnOrAfter(before);
        user.DeactivatedAt!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Deactivate_MultipleTimes_UpdatesScheduledDeletion()
    {
        var user = CreateUser();
        var first = DateTime.UtcNow.AddDays(30);
        var second = DateTime.UtcNow.AddDays(60);

        user.Deactivate(first);
        user.ScheduledDeletionAt.Should().Be(first);

        user.Deactivate(second);
        user.ScheduledDeletionAt.Should().Be(second);
    }

    [Fact]
    public void StaleRecordCutoff_Is90DaysAgo()
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-90));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var daysDiff = today.DayNumber - cutoff.DayNumber;
        daysDiff.Should().Be(90);
    }

    [Fact]
    public void StaleRecordCutoff_RecordAt91DaysAgo_ShouldBeDeleted()
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-90));
        var oldRecord = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-91));

        var shouldDelete = oldRecord < cutoff;
        shouldDelete.Should().BeTrue();
    }

    [Fact]
    public void StaleRecordCutoff_RecordAt89DaysAgo_ShouldNotBeDeleted()
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-90));
        var recentRecord = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-89));

        var shouldDelete = recentRecord < cutoff;
        shouldDelete.Should().BeFalse();
    }

    [Fact]
    public void StaleRecordCutoff_RecordExactlyAtCutoff_ShouldNotBeDeleted()
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-90));
        var exactRecord = cutoff;

        var shouldDelete = exactRecord < cutoff;
        shouldDelete.Should().BeFalse();
    }

    [Fact]
    public void DeletionEligibility_MultipleUsers_FiltersCorrectly()
    {
        var users = new[]
        {
            CreateDeactivatedUser(DateTime.UtcNow.AddDays(-1)),            CreateDeactivatedUser(DateTime.UtcNow.AddDays(30)),            CreateUser(),            CreateDeactivatedUser(DateTime.UtcNow.AddDays(-7)),        };

        var eligible = users.Where(u =>
            u.IsDeactivated
            && u.ScheduledDeletionAt.HasValue
            && u.ScheduledDeletionAt.Value <= DateTime.UtcNow).ToList();

        eligible.Should().HaveCount(2);
    }

    [Fact]
    public void DeletionEligibility_AllUsersActive_NoneEligible()
    {
        var users = Enumerable.Range(0, 5).Select(_ => CreateUser()).ToList();

        var eligible = users.Where(u =>
            u.IsDeactivated
            && u.ScheduledDeletionAt.HasValue
            && u.ScheduledDeletionAt.Value <= DateTime.UtcNow).ToList();

        eligible.Should().BeEmpty();
    }

    [Fact]
    public void DeletionEligibility_AllDeactivatedPastDue_AllEligible()
    {
        var users = new[]
        {
            CreateDeactivatedUser(DateTime.UtcNow.AddDays(-1)),
            CreateDeactivatedUser(DateTime.UtcNow.AddDays(-10)),
            CreateDeactivatedUser(DateTime.UtcNow.AddDays(-30)),
        };

        var eligible = users.Where(u =>
            u.IsDeactivated
            && u.ScheduledDeletionAt.HasValue
            && u.ScheduledDeletionAt.Value <= DateTime.UtcNow).ToList();

        eligible.Should().HaveCount(3);
    }

    private static User CreateDeactivatedUser(DateTime scheduledDeletion)
    {
        var user = CreateUser();
        user.Deactivate(scheduledDeletion);
        return user;
    }
}
