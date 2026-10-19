using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Auth.Jobs;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class DeletionCancellationPersistenceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_AcceptedCancellationSurvivesRollbackOrLostResponse(bool playSubscription, bool lostResponse)
    {
        var saveFailure = new FailNextSaveInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(saveFailure);
        var user = User.Create("Paid", "paid@example.com").Value;
        var expiry = DateTime.UtcNow.AddDays(20);
        if (playSubscription)
            user.SetPlaySubscription("play-token", expiry);
        else
            user.SetStripeSubscription("sub_paid", expiry);
        user.Deactivate(DateTime.UtcNow.AddDays(27));
        factory.Context.Users.Add(user);
        await factory.Context.SaveChangesAsync();
        var stripe = Substitute.For<IBillingService>();
        var play = Substitute.For<IPlayBillingService>();
        var canceled = false;
        var failAfterAcceptance = true;
        Task AcceptCancellation()
        {
            canceled = true;
            if (failAfterAcceptance)
            {
                failAfterAcceptance = false;
                if (lostResponse)
                    throw new BillingProviderException("Cancellation response lost");
                saveFailure.Enabled = true;
            }
            return Task.CompletedTask;
        }
        stripe.GetSubscriptionDetailsAsync("sub_paid", Arg.Any<CancellationToken>())
            .Returns(_ => new BillingSubscriptionDetails("active", expiry, canceled,
                SubscriptionInterval.Monthly, 1000, "usd", null));
        stripe.SetSubscriptionCancelAtPeriodEndAsync("sub_paid", true, Arg.Any<CancellationToken>())
            .Returns(_ => AcceptCancellation());
        stripe.SetSubscriptionCancelAtPeriodEndAsync("sub_paid", false, Arg.Any<CancellationToken>())
            .Returns(_ => { canceled = false; return Task.CompletedTask; });
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
            restoredUser.PlayRenewalStoppedByDeletion.Should().BeTrue();
            canceled.Should().BeTrue();
        }
        else
        {
            canceled.Should().BeFalse("reactivation must restore deletion-owned Stripe renewal after a retry");
            restoredUser.StripeRenewalStoppedByDeletion.Should().BeFalse();
        }
        restoredUser.PlanExpiresAt.Should().Be(expiry);
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
        stripe.GetSubscriptionDetailsAsync("sub_paid", Arg.Any<CancellationToken>())
            .Returns(_ => new BillingSubscriptionDetails("active", user.PlanExpiresAt!.Value, stripeCanceled,
                SubscriptionInterval.Monthly, 1000, "usd", null));
        stripe.SetSubscriptionCancelAtPeriodEndAsync("sub_paid", true, Arg.Any<CancellationToken>())
            .Returns(_ => { stripeCanceled = true; return Task.CompletedTask; });
        stripe.SetSubscriptionCancelAtPeriodEndAsync("sub_paid", false, Arg.Any<CancellationToken>())
            .Returns(_ => { stripeCanceled = false; return Task.CompletedTask; });
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
        restoredUser.PlayRenewalStoppedByDeletion.Should().BeTrue();
        await stripe.Received(stripeIndependentlyCanceled ? 0 : 1)
            .SetSubscriptionCancelAtPeriodEndAsync("sub_paid", false, Arg.Any<CancellationToken>());
        await stripe.Received(stripeIndependentlyCanceled ? 0 : 1)
            .SetSubscriptionCancelAtPeriodEndAsync("sub_paid", true, Arg.Any<CancellationToken>());
        await play.Received().CancelAsync("play-token", Arg.Any<CancellationToken>());
    }

    private static CancelSubscriptionOnDeletionJob CreateJob(OrbitDbContext context, IBillingService stripe, IPlayBillingService play) =>
        new(new GenericRepository<User>(context), new UnitOfWork(context, new DatabaseConnectionSettings()),
            stripe, play, Options.Create(new GooglePlaySettings { ProductId = "orbit_pro" }));

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
}
