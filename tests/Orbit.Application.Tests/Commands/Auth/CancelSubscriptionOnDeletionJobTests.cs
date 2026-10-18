using System.Linq.Expressions;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Auth.Jobs;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Auth;

public class CancelSubscriptionOnDeletionJobTests
{
    private readonly IGenericRepository<User> _users = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IBillingService _stripe = Substitute.For<IBillingService>();
    private readonly IPlayBillingService _play = Substitute.For<IPlayBillingService>();
    private readonly CancelSubscriptionOnDeletionJob _job;
    private readonly User _user = User.Create("Paid", "paid@example.com").Value;

    public CancelSubscriptionOnDeletionJobTests()
    {
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        _users.FindOneTrackedIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(_user);
        _stripe.GetSubscriptionDetailsAsync("sub_paid", Arg.Any<CancellationToken>())
            .Returns(new BillingSubscriptionDetails("active", DateTime.UtcNow.AddDays(20), false,
                SubscriptionInterval.Monthly, 1000, "usd", null));
        _play.VerifyAsync("orbit_pro", "play-token", Arg.Any<CancellationToken>())
            .Returns(new PlaySubscriptionState(true, DateTime.UtcNow.AddDays(20), SubscriptionInterval.Monthly,
                true, "orbit_pro", null, null));
        _job = new CancelSubscriptionOnDeletionJob(_users, _unitOfWork, _stripe, _play,
            Options.Create(new GooglePlaySettings { ProductId = "orbit_pro" }));
    }

    [Fact]
    public async Task ExecuteAsync_Stripe_StopsRenewalAndPreservesEntitlement()
    {
        _user.SetStripeSubscription("sub_paid", DateTime.UtcNow.AddDays(20));
        var expiry = _user.PlanExpiresAt;
        _user.Deactivate(DateTime.UtcNow.AddDays(27));

        await RunAsync();

        await _stripe.Received(1).SetSubscriptionCancelAtPeriodEndAsync("sub_paid", true, Arg.Any<CancellationToken>());
        _play.ReceivedCalls().Should().BeEmpty();
        _user.PlanExpiresAt.Should().Be(expiry);
        _user.IsPro.Should().BeTrue();
        Received.InOrder(() =>
        {
            _unitOfWork.AcquireUserRowLockAsync(_user.Id, Arg.Any<CancellationToken>());
            _users.FindOneTrackedIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>());
            _stripe.SetSubscriptionCancelAtPeriodEndAsync("sub_paid", true, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ExecuteAsync_Play_VerifiesThenCancelsOnce()
    {
        _user.SetPlaySubscription("play-token", DateTime.UtcNow.AddDays(20));
        _user.Deactivate(DateTime.UtcNow.AddDays(27));
        var expiry = _user.PlanExpiresAt;

        await _job.ExecuteAsync(_user.Id, null, "play-token", true, CancellationToken.None);

        Received.InOrder(() =>
        {
            _play.VerifyAsync("orbit_pro", "play-token", Arg.Any<CancellationToken>());
            _play.CancelAsync("play-token", Arg.Any<CancellationToken>());
        });
        await _play.Received(1).CancelAsync("play-token", Arg.Any<CancellationToken>());
        _stripe.ReceivedCalls().Should().BeEmpty();
        _user.PlanExpiresAt.Should().Be(expiry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_PlayInactiveOrUnknown_DoesNotCancel(bool unknown)
    {
        _user.SetPlaySubscription("play-token", DateTime.UtcNow.AddDays(20));
        _user.Deactivate(DateTime.UtcNow.AddDays(27));
        _play.VerifyAsync("orbit_pro", "play-token", Arg.Any<CancellationToken>())
            .Returns(unknown ? null : new PlaySubscriptionState(false, DateTime.UtcNow.AddDays(-1), null, true, "orbit_pro", null, null));

        await _job.ExecuteAsync(_user.Id, null, "play-token", true, CancellationToken.None);

        await _play.DidNotReceive().CancelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ProviderFailure_PropagatesForDurableRetry(bool play)
    {
        var failure = new BillingProviderException("provider unavailable");
        if (play)
        {
            _user.SetPlaySubscription("play-token", DateTime.UtcNow.AddDays(20));
            _play.CancelAsync("play-token", Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        }
        else
        {
            _user.SetStripeSubscription("sub_paid", DateTime.UtcNow.AddDays(20));
            _stripe.SetSubscriptionCancelAtPeriodEndAsync("sub_paid", true, Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        }
        _user.Deactivate(DateTime.UtcNow.AddDays(27));

        var run = () => _job.ExecuteAsync(_user.Id, play ? null : "sub_paid", play ? "play-token" : null, true, CancellationToken.None);

        (await run.Should().ThrowAsync<BillingProviderException>()).Which.Should().BeSameAs(failure);
        _user.IsDeactivated.Should().BeTrue();
        var retry = typeof(CancelSubscriptionOnDeletionJob).GetMethod(nameof(CancelSubscriptionOnDeletionJob.ExecuteAsync))!
            .GetCustomAttributes(typeof(AutomaticRetryAttribute), false).Cast<AutomaticRetryAttribute>().Single();
        retry.Attempts.Should().Be(26);
        retry.DelaysInSeconds.Should().Equal(60, 300, 3600);
        retry.OnAttemptsExceeded.Should().Be(AttemptsExceededAction.Fail);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_Restoration_OnlyRestoresBeforeExpiry(bool expired, bool lifetime)
    {
        _user.SetStripeSubscription("sub_paid", DateTime.UtcNow.AddDays(expired ? -1 : 20));

        if (lifetime)
            _user.GrantLifetimePro();

        await _job.ExecuteAsync(_user.Id, "sub_paid", null, false, CancellationToken.None);

        await _stripe.Received(expired ? 0 : 1).SetSubscriptionCancelAtPeriodEndAsync("sub_paid", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_CancelAfterReactivation_DoesNotStopRenewal()
    {
        _user.SetStripeSubscription("sub_paid", DateTime.UtcNow.AddDays(20));

        await RunAsync();

        _stripe.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_RestoreAfterAnotherDeletion_DoesNotRestartRenewal()
    {
        _user.SetStripeSubscription("sub_paid", DateTime.UtcNow.AddDays(20));
        _user.Deactivate(DateTime.UtcNow.AddDays(27));

        await _job.ExecuteAsync(_user.Id, "sub_paid", null, false, CancellationToken.None);

        _stripe.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("canceled", false)]
    [InlineData("incomplete_expired", false)]
    [InlineData("active", true)]
    public async Task ExecuteAsync_StripeAlreadyStopped_DoesNotUpdateAgain(string status, bool canceled)
    {
        _user.SetStripeSubscription("sub_paid", DateTime.UtcNow.AddDays(20));
        _user.Deactivate(DateTime.UtcNow.AddDays(27));
        _stripe.GetSubscriptionDetailsAsync("sub_paid", Arg.Any<CancellationToken>())
            .Returns(new BillingSubscriptionDetails(status, DateTime.UtcNow, canceled, SubscriptionInterval.Monthly, 1000, "usd", null));

        await RunAsync();

        await _stripe.DidNotReceive().SetSubscriptionCancelAtPeriodEndAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_MissingUser_CancelsRetainedIdentifiersWithoutRestoring(bool cancel)
    {
        var transactionActive = false;
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                transactionActive = true;
                await call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>());
                transactionActive = false;
            });
        _stripe.When(stripe => stripe.GetSubscriptionDetailsAsync("sub_paid", Arg.Any<CancellationToken>()))
            .Do(_ => transactionActive.Should().BeFalse());
        _play.When(play => play.VerifyAsync("orbit_pro", "play-token", Arg.Any<CancellationToken>()))
            .Do(_ => transactionActive.Should().BeFalse());
        _users.FindOneTrackedIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);

        await _job.ExecuteAsync(_user.Id, "sub_paid", "play-token", cancel, CancellationToken.None);

        await _stripe.Received(cancel ? 1 : 0).SetSubscriptionCancelAtPeriodEndAsync("sub_paid", true, Arg.Any<CancellationToken>());
        await _play.Received(cancel ? 1 : 0).CancelAsync("play-token", Arg.Any<CancellationToken>());
        await _stripe.DidNotReceive().SetSubscriptionCancelAtPeriodEndAsync(Arg.Any<string>(), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelBeforeDeletionAsync_StripeFailure_StillAttemptsPlayCancellation()
    {
        _user.SetStripeSubscription("sub_paid", DateTime.UtcNow.AddDays(20));
        _user.SetPlaySubscription("play-token", DateTime.UtcNow.AddDays(20));
        _stripe.GetSubscriptionDetailsAsync("sub_paid", Arg.Any<CancellationToken>())
            .ThrowsAsync(new BillingProviderException("unavailable"));

        var run = () => _job.CancelBeforeDeletionAsync(_user, CancellationToken.None);

        await run.Should().ThrowAsync<BillingProviderException>();
        await _play.Received(1).CancelAsync("play-token", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ReplacedSubscription_DoesNotTouchNewOrOldSubscription()
    {
        _user.SetStripeSubscription("sub_new", DateTime.UtcNow.AddDays(20));
        _user.Deactivate(DateTime.UtcNow.AddDays(27));

        await RunAsync();

        _stripe.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public void EnqueueRestoration_ExpiredLifetimeBilling_DoesNotEnqueue()
    {
        _user.SetStripeSubscription("sub_paid", DateTime.UtcNow.AddDays(-1));
        _user.GrantLifetimePro();
        var jobs = Substitute.For<IBackgroundJobClient>();

        CancelSubscriptionOnDeletionJob.EnqueueRestoration(jobs, _user);

        jobs.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
    }

    [Fact]
    public async Task CancelBeforeDeletionAsync_RetainedStripeAndPlayIdentifiers_ChecksBothProviders()
    {
        _user.SetStripeSubscription("sub_paid", DateTime.UtcNow.AddDays(20));
        _user.SetPlaySubscription("play-token", DateTime.UtcNow.AddDays(20));

        await _job.CancelBeforeDeletionAsync(_user, CancellationToken.None);

        await _stripe.Received(1).SetSubscriptionCancelAtPeriodEndAsync("sub_paid", true, Arg.Any<CancellationToken>());
        await _play.Received(1).CancelAsync("play-token", Arg.Any<CancellationToken>());
    }

    private Task RunAsync() => _job.ExecuteAsync(_user.Id, "sub_paid", null, true, CancellationToken.None);
}
