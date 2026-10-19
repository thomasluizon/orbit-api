using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Auth.Jobs;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;
using Stripe;

namespace Orbit.Infrastructure.Tests.Services;

public class DeletionCancellationPersistenceTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task ExecuteAsync_AcceptedCancellationSurvivesRollbackOrLostResponse(
        bool playSubscription, bool lostResponse, bool failCommit)
    {
        var saveFailure = new FailNextSaveInterceptor();
        var commitFailure = new FailNextCommitInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(saveFailure, commitFailure);
        var user = User.Create("Paid", "paid@example.com").Value;
        var expiry = DateTime.UtcNow.AddDays(20);
        if (playSubscription)
            user.SetPlaySubscription("play-token", expiry);
        else
            user.SetStripeSubscription("sub_paid", expiry);
        user.Deactivate(DateTime.UtcNow.AddDays(27));
        factory.Context.Users.Add(user);
        await factory.Context.SaveChangesAsync();
        var subscriptions = Substitute.For<SubscriptionService>();
        var stripe = new StripeBillingService(new StripeServiceClients(
            new CustomerService(), new Stripe.Checkout.SessionService(), new Stripe.BillingPortal.SessionService(),
            subscriptions, new InvoiceService(), new PriceService(), new CouponService()),
            NullLogger<StripeBillingService>.Instance);
        var play = Substitute.For<IPlayBillingService>();
        var canceled = false;
        string? marker = null;
        var failAfterAcceptance = true;
        Task AcceptCancellation()
        {
            canceled = true;
            if (failAfterAcceptance)
            {
                failAfterAcceptance = false;
                if (lostResponse)
                    throw new BillingProviderException("Cancellation response lost");
                if (failCommit)
                    commitFailure.Enabled = true;
                else
                    saveFailure.Enabled = true;
            }
            return Task.CompletedTask;
        }
        Subscription ProviderSubscription() => new()
        {
            Status = "active",
            CancelAtPeriodEnd = canceled,
            Metadata = marker is null ? [] : new() { ["orbit_deletion_user_id"] = marker }
        };
        subscriptions.GetAsync("sub_paid", Arg.Any<SubscriptionGetOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => ProviderSubscription());
        subscriptions.UpdateAsync("sub_paid", Arg.Any<SubscriptionUpdateOptions>(), Arg.Any<RequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var options = call.Arg<SubscriptionUpdateOptions>();
                marker = options.Metadata?.GetValueOrDefault("orbit_deletion_user_id");
                if (options.CancelAtPeriodEnd == true)
                    await AcceptCancellation();
                else
                    canceled = false;
                return ProviderSubscription();
            });
        play.VerifyAsync("orbit_pro", "play-token", Arg.Any<CancellationToken>())
            .Returns(_ => new PlaySubscriptionState(true, expiry, SubscriptionInterval.Monthly,
                true, "orbit_pro", null, null, CanRenew: !canceled));
        play.CancelAsync("play-token", Arg.Any<CancellationToken>()).Returns(_ => AcceptCancellation());
        var stripeId = playSubscription ? null : "sub_paid";
        var playToken = playSubscription ? "play-token" : null;

        using (var firstContext = factory.CreateContext())
        {
            var run = () => CreateJob(firstContext, stripe, play)
                .ExecuteAsync(user.Id, stripeId, playToken, true, CancellationToken.None);
            if (lostResponse)
                await run.Should().ThrowAsync<BillingProviderException>();
            else
                await run.Should().ThrowAsync<DbUpdateException>();
        }
        canceled.Should().BeTrue();
        if (!playSubscription)
            marker.Should().Be(user.Id.ToString(), "the accepted Stripe update must identify its deletion owner");
        using (var retryContext = factory.CreateContext())
            await CreateJob(retryContext, stripe, play)
                .ExecuteAsync(user.Id, stripeId, playToken, true, CancellationToken.None);
        using (var signInContext = factory.CreateContext())
        {
            var returningUser = await signInContext.Users.IgnoreQueryFilters().SingleAsync();
            returningUser.CancelDeactivation();
            await signInContext.SaveChangesAsync();
        }
        using (var restorationContext = factory.CreateContext())
            await CreateJob(restorationContext, stripe, play)
                .ExecuteAsync(user.Id, stripeId, null, false, CancellationToken.None);

        using var finalContext = factory.CreateContext();
        var restoredUser = await finalContext.Users.SingleAsync();
        if (playSubscription)
        {
            restoredUser.PlayRenewalStoppedByDeletion.Should().BeFalse();
            canceled.Should().BeTrue();
        }
        else
        {
            canceled.Should().BeFalse("reactivation must restore deletion-owned Stripe renewal after a retry");
            restoredUser.StripeRenewalStoppedByDeletion.Should().BeFalse();
            marker.Should().BeNullOrEmpty("restoring renewal must remove the deletion marker");
        }
        restoredUser.PlanExpiresAt.Should().Be(expiry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_IntentCannotCommit_DoesNotMutateProvider(bool failCommit)
    {
        var saveFailure = new FailNextSaveInterceptor();
        var commitFailure = new FailNextCommitInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(saveFailure, commitFailure);
        var user = User.Create("Paid", "paid@example.com").Value;
        var expiry = DateTime.UtcNow.AddDays(20);
        user.SetStripeSubscription("sub_paid", expiry);
        user.Deactivate(DateTime.UtcNow.AddDays(27));
        factory.Context.Users.Add(user);
        await factory.Context.SaveChangesAsync();
        var stripe = Substitute.For<IBillingService>();
        stripe.GetSubscriptionDetailsAsync("sub_paid", Arg.Any<CancellationToken>())
            .Returns(new BillingSubscriptionDetails("active", expiry, false, SubscriptionInterval.Monthly, 1000, "usd", null));
        saveFailure.Enabled = !failCommit;
        commitFailure.Enabled = failCommit;
        using (var attempt = factory.CreateContext())
        {
            var run = () => CreateJob(attempt, stripe, Substitute.For<IPlayBillingService>())
                .ExecuteAsync(user.Id, "sub_paid", null, true, CancellationToken.None);

            await run.Should().ThrowAsync<DbUpdateException>();
        }

        await stripe.DidNotReceive().SetSubscriptionCancelAtPeriodEndAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<string?>());
        using var persisted = factory.CreateContext();
        (await persisted.Users.IgnoreQueryFilters().SingleAsync()).StripeRenewalStoppedByDeletion.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_AccountChangedAfterIntentCommit_DoesNotCancelStaleSubscription(bool reactivate, bool expired)
    {
        var afterCommit = new AfterCommitInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(afterCommit);
        var user = User.Create("Paid", "paid@example.com").Value;
        var expiry = DateTime.UtcNow.AddDays(expired ? -1 : 20);
        user.SetStripeSubscription("sub_paid", expiry);
        user.Deactivate(DateTime.UtcNow.AddDays(27));
        factory.Context.Users.Add(user);
        await factory.Context.SaveChangesAsync();
        var stripe = Substitute.For<IBillingService>();
        stripe.GetSubscriptionDetailsAsync("sub_paid", Arg.Any<CancellationToken>())
            .Returns(new BillingSubscriptionDetails("active", expiry, false, SubscriptionInterval.Monthly, 1000, "usd", null));
        afterCommit.Once = async () =>
        {
            using var changed = factory.CreateContext();
            var currentUser = await changed.Users.IgnoreQueryFilters().SingleAsync();
            currentUser.StripeRenewalStoppedByDeletion.Should().BeTrue();
            if (reactivate)
                currentUser.CancelDeactivation();
            else
                currentUser.SetStripeSubscription("sub_new", expiry);
            await changed.SaveChangesAsync();
        };
        using var attempt = factory.CreateContext();

        await CreateJob(attempt, stripe, Substitute.For<IPlayBillingService>())
            .ExecuteAsync(user.Id, "sub_paid", null, true, CancellationToken.None);

        afterCommit.Once.Should().BeNull();
        await stripe.DidNotReceive().SetSubscriptionCancelAtPeriodEndAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<string?>());
        using var persisted = factory.CreateContext();
        (await persisted.Users.IgnoreQueryFilters().SingleAsync()).StripeRenewalStoppedByDeletion.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_IndependentCancellationAfterIntentCommit_RemainsCanceledAfterReactivation(bool reactivateBeforePhaseTwo)
    {
        var afterCommit = new AfterCommitInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(afterCommit);
        var user = User.Create("Paid", "paid@example.com").Value;
        var expiry = DateTime.UtcNow.AddDays(20);
        user.SetStripeSubscription("sub_paid", expiry);
        user.Deactivate(DateTime.UtcNow.AddDays(27));
        factory.Context.Users.Add(user);
        await factory.Context.SaveChangesAsync();
        var stripe = Substitute.For<IBillingService>();
        var canceled = false;
        stripe.GetSubscriptionDetailsAsync("sub_paid", Arg.Any<CancellationToken>())
            .Returns(_ => new BillingSubscriptionDetails("active", expiry, canceled,
                SubscriptionInterval.Monthly, 1000, "usd", null));
        stripe.SetSubscriptionCancelAtPeriodEndAsync("sub_paid", false, Arg.Any<CancellationToken>())
            .Returns(_ => { canceled = false; return Task.CompletedTask; });
        afterCommit.Once = async () =>
        {
            canceled = true;
            if (reactivateBeforePhaseTwo)
            {
                using var signIn = factory.CreateContext();
                (await signIn.Users.IgnoreQueryFilters().SingleAsync()).CancelDeactivation();
                await signIn.SaveChangesAsync();
            }
        };

        using (var cancellationContext = factory.CreateContext())
            await CreateJob(cancellationContext, stripe, Substitute.For<IPlayBillingService>())
                .ExecuteAsync(user.Id, "sub_paid", null, true, CancellationToken.None);
        using (var signInContext = factory.CreateContext())
        {
            var returningUser = await signInContext.Users.IgnoreQueryFilters().SingleAsync();
            returningUser.CancelDeactivation();
            await signInContext.SaveChangesAsync();
        }
        using (var restorationContext = factory.CreateContext())
            await CreateJob(restorationContext, stripe, Substitute.For<IPlayBillingService>())
                .ExecuteAsync(user.Id, "sub_paid", null, false, CancellationToken.None);

        canceled.Should().BeTrue("an independent cancellation between phases must remain canceled");
        using var persisted = factory.CreateContext();
        (await persisted.Users.SingleAsync()).StripeRenewalStoppedByDeletion.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_OpenTransaction_RejectsCancellationBeforeProviderRead()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Paid", "paid@example.com").Value;
        user.SetStripeSubscription("sub_paid", DateTime.UtcNow.AddDays(20));
        user.Deactivate(DateTime.UtcNow.AddDays(27));
        factory.Context.Users.Add(user);
        await factory.Context.SaveChangesAsync();
        var stripe = Substitute.For<IBillingService>();
        using var context = factory.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var run = () => CreateJob(context, stripe, Substitute.For<IPlayBillingService>())
            .ExecuteAsync(user.Id, "sub_paid", null, true, CancellationToken.None);

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*transaction*");
        stripe.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExecuteAsync_CancellationOwnershipSurvivesReloadAndOtherProviderFailure(
        bool stripeIndependentlyCanceled, bool playFailsFirst)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Paid", "paid@example.com").Value;
        user.SetStripeSubscription("sub_paid", DateTime.UtcNow.AddDays(20));
        user.LinkPlayPurchaseToken("play-token");
        user.Deactivate(DateTime.UtcNow.AddDays(27));
        factory.Context.Users.Add(user);
        await factory.Context.SaveChangesAsync();
        var stripe = Substitute.For<IBillingService>();
        var play = Substitute.For<IPlayBillingService>();
        var stripeCanceled = stripeIndependentlyCanceled;
        string? stripeDeletionOwner = null;
        stripe.GetSubscriptionDetailsAsync("sub_paid", Arg.Any<CancellationToken>())
            .Returns(_ => new BillingSubscriptionDetails("active", user.PlanExpiresAt!.Value, stripeCanceled,
                SubscriptionInterval.Monthly, 1000, "usd", null, stripeDeletionOwner));
        stripe.SetSubscriptionCancelAtPeriodEndAsync("sub_paid", true, Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(call => { stripeCanceled = true; stripeDeletionOwner = call.ArgAt<string?>(3); return Task.CompletedTask; });
        stripe.SetSubscriptionCancelAtPeriodEndAsync("sub_paid", false, Arg.Any<CancellationToken>())
            .Returns(_ => { stripeCanceled = false; stripeDeletionOwner = null; return Task.CompletedTask; });
        var playCanceled = false;
        play.VerifyAsync("orbit_pro", "play-token", Arg.Any<CancellationToken>())
            .Returns(_ => new PlaySubscriptionState(true, user.PlanExpiresAt!.Value, SubscriptionInterval.Monthly,
                true, "orbit_pro", null, null, CanRenew: !playCanceled));
        play.CancelAsync("play-token", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (playFailsFirst)
                throw new BillingProviderException("unavailable");
            playCanceled = true;
            return Task.CompletedTask;
        });

        using (var cancellationContext = factory.CreateContext())
        {
            var job = CreateJob(cancellationContext, stripe, play);
            var run = () => job.ExecuteAsync(user.Id, "sub_paid", "play-token", true, CancellationToken.None);
            if (playFailsFirst)
                await run.Should().ThrowAsync<BillingProviderException>();
            else
                await run();
        }

        using (var persisted = factory.CreateContext())
        {
            var canceledUser = await persisted.Users.IgnoreQueryFilters().SingleAsync();
            canceledUser.StripeRenewalStoppedByDeletion.Should().Be(!stripeIndependentlyCanceled);
            canceledUser.PlayRenewalStoppedByDeletion.Should().BeTrue();
            canceledUser.IsDeactivated.Should().BeTrue();
        }
        playFailsFirst = false;
        using (var retryContext = factory.CreateContext())
            await CreateJob(retryContext, stripe, play).ExecuteAsync(user.Id, "sub_paid", "play-token", true, CancellationToken.None);
        using (var signInContext = factory.CreateContext())
        {
            var returningUser = await signInContext.Users.IgnoreQueryFilters().SingleAsync();
            returningUser.CancelDeactivation();
            await signInContext.SaveChangesAsync();
        }
        using (var restorationContext = factory.CreateContext())
            await CreateJob(restorationContext, stripe, play).ExecuteAsync(user.Id, "sub_paid", null, false, CancellationToken.None);
        using var finalContext = factory.CreateContext();
        var restoredUser = await finalContext.Users.SingleAsync();

        restoredUser.StripeRenewalStoppedByDeletion.Should().BeFalse();
        stripeCanceled.Should().Be(stripeIndependentlyCanceled);
        restoredUser.PlayRenewalStoppedByDeletion.Should().BeFalse();
        await stripe.Received(stripeIndependentlyCanceled ? 0 : 1)
            .SetSubscriptionCancelAtPeriodEndAsync("sub_paid", false, Arg.Any<CancellationToken>());
        await stripe.Received(stripeIndependentlyCanceled ? 0 : 1)
            .SetSubscriptionCancelAtPeriodEndAsync("sub_paid", true, Arg.Any<CancellationToken>(), Arg.Any<string?>());
        await play.Received().CancelAsync("play-token", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_SubscriptionChangedBeforeErasure_RetainsAccountAndLogsRecheck(bool playSubscription)
    {
        var afterCommit = new AfterCommitInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(afterCommit);
        var user = User.Create("Paid", "paid@example.com").Value;
        var expiry = DateTime.UtcNow.AddDays(20);
        if (playSubscription)
            user.SetPlaySubscription("play-token", expiry);
        else
            user.SetStripeSubscription("sub_paid", expiry);
        user.Deactivate(DateTime.UtcNow.AddDays(-1));
        factory.Context.Users.Add(user);
        await factory.Context.SaveChangesAsync();
        var stripe = Substitute.For<IBillingService>();
        stripe.GetSubscriptionDetailsAsync("sub_paid", Arg.Any<CancellationToken>())
            .Returns(new BillingSubscriptionDetails("active", expiry, false, SubscriptionInterval.Monthly, 1000, "usd", null));
        var play = Substitute.For<IPlayBillingService>();
        play.VerifyAsync("orbit_pro", "play-token", Arg.Any<CancellationToken>())
            .Returns(new PlaySubscriptionState(true, expiry, SubscriptionInterval.Monthly,
                true, "orbit_pro", null, null, CanRenew: true));
        afterCommit.Once = async () =>
        {
            using var changed = factory.CreateContext();
            var currentUser = await changed.Users.IgnoreQueryFilters().SingleAsync();
            if (playSubscription)
                currentUser.SetPlaySubscription("play-new", expiry);
            else
                currentUser.SetStripeSubscription("sub_new", expiry);
            await changed.SaveChangesAsync();
        };
        var objects = new FakeObjectStore();
        var services = new ServiceCollection();
        services.AddScoped(_ => factory.CreateContext());
        services.AddScoped<IUnitOfWork>(sp => new UnitOfWork(sp.GetRequiredService<OrbitDbContext>(), new DatabaseConnectionSettings()));
        services.AddScoped<IGenericRepository<User>, GenericRepository<User>>();
        services.AddSingleton<IObjectStorageDeletionService>(objects);
        services.AddSingleton(stripe);
        services.AddSingleton(play);
        services.AddSingleton(Options.Create(new GooglePlaySettings { ProductId = "orbit_pro" }));
        services.AddScoped<CancelSubscriptionOnDeletionJob>();
        services.AddScoped<OwedSubscriptionCancellationJob>();
        services.AddScoped<IGenericRepository<OwedSubscriptionCancellation>, GenericRepository<OwedSubscriptionCancellation>>();
        services.AddSingleton<ILogger<OwedSubscriptionCancellationJob>>(NullLogger<OwedSubscriptionCancellationJob>.Instance);
        services.AddSingleton(TimeProvider.System);
        var logger = Substitute.For<ILogger<AccountDeletionService>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        using var provider = services.BuildServiceProvider();
        using var service = new AccountDeletionService(provider.GetRequiredService<IServiceScopeFactory>(),
            logger, new ConfigurationBuilder().Build());

        await service.RunAsync(CancellationToken.None);

        using var persisted = factory.CreateContext();
        (await persisted.Users.IgnoreQueryFilters().SingleAsync()).IsDeactivated.Should().BeTrue();
        objects.Attempts.Should().Be(0);
        logger.ReceivedCalls().Should().Contain(call => call.GetMethodInfo().Name == "Log"
            && (LogLevel)call.GetArguments()[0]! == LogLevel.Warning
            && call.GetArguments()[2]!.ToString()!.Contains(user.Id.ToString())
            && call.GetArguments()[2]!.ToString()!.Contains("subscription identifiers changed"));
    }

    private static CancelSubscriptionOnDeletionJob CreateJob(OrbitDbContext context, IBillingService stripe, IPlayBillingService play) =>
        new(new GenericRepository<User>(context), new UnitOfWork(context, new DatabaseConnectionSettings()),
            stripe, play, Options.Create(new GooglePlaySettings { ProductId = "orbit_pro" }),
            new OwedSubscriptionCancellationJob(new GenericRepository<OwedSubscriptionCancellation>(context),
                new UnitOfWork(context, new DatabaseConnectionSettings()), stripe, play,
                Options.Create(new GooglePlaySettings { ProductId = "orbit_pro" }),
                NullLogger<OwedSubscriptionCancellationJob>.Instance, TimeProvider.System));

    private sealed class FailNextSaveInterceptor : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled)
            {
                Enabled = false;
                throw new DbUpdateException("Cancellation save failed");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailNextCommitInterceptor : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled)
            {
                Enabled = false;
                throw new DbUpdateException("Cancellation commit failed");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class AfterCommitInterceptor : DbTransactionInterceptor
    {
        public Func<Task>? Once { get; set; }

        public override async Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Once is { } callback)
            {
                Once = null;
                await callback();
            }
        }
    }
}
