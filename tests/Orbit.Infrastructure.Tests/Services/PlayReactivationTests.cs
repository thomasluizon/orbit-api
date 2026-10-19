using System.Data.Common;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Jobs;
using Orbit.Application.Common;
using Orbit.Application.Notifications.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class PlayReactivationTests
{
    private readonly IPlayBillingService _play = Substitute.For<IPlayBillingService>();
    private readonly IBillingService _stripe = Substitute.For<IBillingService>();
    private readonly IBackgroundJobClient _jobs = Substitute.For<IBackgroundJobClient>();
    private readonly IOptions<GooglePlaySettings> _settings = Options.Create(new GooglePlaySettings { ProductId = "orbit_pro" });
    private bool _renewalOff;
    private readonly List<Job> _noticeJobs = [];

    public PlayReactivationTests()
    {
        _jobs.Create(Arg.Do<Job>(job =>
        {
            if (job.Type == typeof(NotifyPlayRenewalOnReactivationJob))
                _noticeJobs.Add(job);
        }), Arg.Any<IState>()).Returns("queued-job");
        _play.VerifyAsync("orbit_pro", "play-token", Arg.Any<CancellationToken>())
            .Returns(_ => new PlaySubscriptionState(true, DateTime.UtcNow.AddDays(20), SubscriptionInterval.Monthly,
                true, "orbit_pro", null, null, CanRenew: !_renewalOff));
        _play.CancelAsync("play-token", Arg.Any<CancellationToken>())
            .Returns(_ => { _renewalOff = true; return Task.CompletedTask; });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignIn_DeletionOwnedButPlayStillRenewing_DoesNotNotify(bool google)
    {
        using var database = new SqliteOrbitDbContextFactory();
        var user = User.Create("Returning", "paid@example.com").Value;
        user.SetPlaySubscription("play-token", DateTime.UtcNow.AddDays(20));
        user.Deactivate(DateTime.UtcNow.AddDays(27));
        database.Context.Users.Add(user);
        await database.Context.SaveChangesAsync();
        _play.CancelAsync("play-token", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new BillingProviderException("Play unavailable")));
        using (var context = database.CreateContext())
        {
            var cancel = () => CreateCancellationJob(context).ExecuteAsync(user.Id, null, "play-token", true, CancellationToken.None);
            await cancel.Should().ThrowAsync<BillingProviderException>();
            (await context.Users.IgnoreQueryFilters().SingleAsync()).PlayRenewalStoppedByDeletion.Should().BeTrue();
        }
        _renewalOff.Should().BeFalse();

        await SignInAsync(database, google);
        await RunNoticeJobAsync(database, _noticeJobs.Should().ContainSingle().Subject);
        using (var context = database.CreateContext())
            await CreateCancellationJob(context).ExecuteAsync(user.Id, null, "play-token", true, CancellationToken.None);

        using var persisted = database.CreateContext();
        (await persisted.Notifications.CountAsync()).Should().Be(0, "Play is still renewing despite deletion ownership");
        (await persisted.Users.SingleAsync()).PlayRenewalStoppedByDeletion.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, "en", false)]
    [InlineData(true, "en", false)]
    [InlineData(false, "pt-BR", false)]
    [InlineData(true, "pt-BR", false)]
    [InlineData(false, "en", true)]
    [InlineData(true, "en", true)]
    [InlineData(false, "pt-BR", true)]
    [InlineData(true, "pt-BR", true)]
    public async Task SignIn_AfterRealCancellation_NotifiesOnceAndClearsOwnership(bool google, string language, bool expired)
    {
        using var database = new SqliteOrbitDbContextFactory();
        var userId = await CancelForDeletionAsync(database, language);
        var expiryAtUtc = DateTime.UtcNow.AddDays(expired ? -1 : 20);
        var owed = OwedSubscriptionCancellation.Create(SubscriptionSource.GooglePlay, "play-token");
        using (var context = database.CreateContext())
        {
            var user = await context.Users.IgnoreQueryFilters().SingleAsync();
            user.SetPlaySubscription("play-token", expiryAtUtc);
            context.OwedSubscriptionCancellations.AddRange(owed,
                OwedSubscriptionCancellation.Create(SubscriptionSource.GooglePlay, "unrelated-token"),
                OwedSubscriptionCancellation.Create(SubscriptionSource.Stripe, "play-token"));
            await context.SaveChangesAsync();
        }
        _play.ClearReceivedCalls();
        _play.VerifyAsync("orbit_pro", "play-token", Arg.Any<CancellationToken>())
            .Returns(new PlaySubscriptionState(!expired, expiryAtUtc, SubscriptionInterval.Monthly,
                true, "orbit_pro", null, null, CanRenew: false));

        await SignInAsync(database, google);
        await SignInAsync(database, !google);
        using (var delayedContext = database.CreateContext())
        {
            await CreateCancellationJob(delayedContext).ExecuteAsync(userId, null, "play-token", true, CancellationToken.None);
            await CreateOwedJob(delayedContext).ExecuteAsync(owed.Id, CancellationToken.None);
        }

        using var persisted = database.CreateContext();
        var reactivated = await persisted.Users.SingleAsync();
        reactivated.IsDeactivated.Should().BeFalse();
        reactivated.PlayRenewalStoppedByDeletion.Should().BeFalse();
        reactivated.PlanExpiresAt.Should().Be(expiryAtUtc);
        reactivated.IsPro.Should().Be(!expired);
        _renewalOff.Should().BeTrue();
        _play.ReceivedCalls().Should().BeEmpty("sign-in and stale cancellation retries must not mutate Play renewal");
        (await persisted.Notifications.CountAsync()).Should().Be(0, "the notice must wait for Play verification");
        var job = _noticeJobs.Should().ContainSingle().Subject;
        await RunNoticeJobAsync(database, job);
        await RunNoticeJobAsync(database, job);
        await _play.Received(2).VerifyAsync("orbit_pro", "play-token", Arg.Any<CancellationToken>());
        var remaining = await persisted.OwedSubscriptionCancellations.ToListAsync();
        remaining.Should().HaveCount(2);
        remaining.Should().NotContain(record => record.Id == owed.Id);
        var inbox = await new GetNotificationsQueryHandler(new GenericRepository<Notification>(persisted))
            .Handle(new GetNotificationsQuery(userId), CancellationToken.None);
        inbox.Value.UnreadCount.Should().Be(1);
        var notice = inbox.Value.Items.Should().ContainSingle().Subject;
        notice.IsRead.Should().BeFalse();
        notice.Title.Should().Be(language == "pt-BR"
            ? "Sua renovação no Google Play está desativada" : "Your Google Play renewal is off");
        notice.Body.Should().Contain(language == "pt-BR"
            ? "Pagamentos e assinaturas > Assinaturas" : "Payments & subscriptions > Subscriptions");
        notice.Body.Should().Contain(language == "pt-BR" ? "Assinar novamente" : "Resubscribe");
        notice.Body.Should().Contain(language == "pt-BR" ? "assine o Pro de novo pelo Orbit" : "subscribe to Pro again in Orbit");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SignIn_FailedSaveOrCommit_RollsBackFlagAndOwedRemovalWithoutQueuingThenRetries(bool google, bool failCommit)
    {
        var failure = new ReactivationFailureInterceptor(failCommit);
        using var database = new SqliteOrbitDbContextFactory(failure);
        await CancelForDeletionAsync(database, "en");
        var owed = OwedSubscriptionCancellation.Create(SubscriptionSource.GooglePlay, "play-token");
        database.Context.OwedSubscriptionCancellations.Add(owed);
        await database.Context.SaveChangesAsync();
        failure.Enabled = true;

        var signIn = () => SignInAsync(database, google);
        await signIn.Should().ThrowAsync<DbUpdateException>();
        _noticeJobs.Should().BeEmpty();

        using (var rolledBack = database.CreateContext())
        {
            var user = await rolledBack.Users.IgnoreQueryFilters().SingleAsync();
            user.IsDeactivated.Should().BeTrue();
            user.PlayRenewalStoppedByDeletion.Should().BeTrue();
            (await rolledBack.Notifications.CountAsync()).Should().Be(0);
            (await rolledBack.OwedSubscriptionCancellations.SingleAsync()).Id.Should().Be(owed.Id);
        }
        await SignInAsync(database, google);
        await RunNoticeJobAsync(database, _noticeJobs.Should().ContainSingle().Subject);
        using var persisted = database.CreateContext();
        (await persisted.Users.SingleAsync()).PlayRenewalStoppedByDeletion.Should().BeFalse();
        (await persisted.Notifications.CountAsync()).Should().Be(1);
        (await persisted.OwedSubscriptionCancellations.CountAsync()).Should().Be(0);
        _renewalOff.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoticeJob_PlayFailure_CreatesNothingAndRetries(bool google)
    {
        using var database = new SqliteOrbitDbContextFactory();
        await CancelForDeletionAsync(database, "pt-BR");
        await SignInAsync(database, google);
        var job = _noticeJobs.Should().ContainSingle().Subject;
        _play.VerifyAsync("orbit_pro", "play-token", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PlaySubscriptionState?>(new BillingProviderException("Play unavailable")));

        var execute = () => RunNoticeJobAsync(database, job);
        await execute.Should().ThrowAsync<BillingProviderException>();

        using (var failed = database.CreateContext())
        {
            (await failed.Notifications.CountAsync()).Should().Be(0);
            (await failed.Users.SingleAsync()).PlayRenewalStoppedByDeletion.Should().BeFalse();
        }
        var retry = job.Method.GetCustomAttributes(typeof(AutomaticRetryAttribute), false)
            .Should().ContainSingle().Subject.Should().BeOfType<AutomaticRetryAttribute>().Subject;
        retry.Attempts.Should().BeGreaterThan(0);
        _play.VerifyAsync("orbit_pro", "play-token", Arg.Any<CancellationToken>())
            .Returns(new PlaySubscriptionState(true, DateTime.UtcNow.AddDays(20), SubscriptionInterval.Monthly,
                true, "orbit_pro", null, null, CanRenew: false));

        await RunNoticeJobAsync(database, job);
        await RunNoticeJobAsync(database, job);

        using var persisted = database.CreateContext();
        var notice = (await persisted.Notifications.ToListAsync()).Should().ContainSingle().Subject;
        notice.Title.Should().Be("Sua renovação no Google Play está desativada");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoticeJob_FailedSaveOrCommit_RollsBackThenRetriesOnce(bool failCommit)
    {
        var failure = new ReactivationFailureInterceptor(failCommit);
        using var database = new SqliteOrbitDbContextFactory(failure);
        await CancelForDeletionAsync(database, "en");
        await SignInAsync(database, false);
        var job = _noticeJobs.Should().ContainSingle().Subject;
        failure.Enabled = true;

        var execute = () => RunNoticeJobAsync(database, job);
        await execute.Should().ThrowAsync<DbUpdateException>();

        using (var failed = database.CreateContext())
            (await failed.Notifications.CountAsync()).Should().Be(0);
        await RunNoticeJobAsync(database, job);
        await RunNoticeJobAsync(database, job);

        using var persisted = database.CreateContext();
        (await persisted.Notifications.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task NoticeJob_UnknownPurchase_DoesNotGuessRenewalState()
    {
        using var database = new SqliteOrbitDbContextFactory();
        await CancelForDeletionAsync(database, "en");
        await SignInAsync(database, false);
        _play.VerifyAsync("orbit_pro", "play-token", Arg.Any<CancellationToken>())
            .Returns((PlaySubscriptionState?)null);

        await RunNoticeJobAsync(database, _noticeJobs.Should().ContainSingle().Subject);

        using var persisted = database.CreateContext();
        (await persisted.Notifications.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("deactivated")]
    [InlineData("changed-purchase")]
    [InlineData("deleted")]
    public async Task NoticeJob_AccountChangedBeforeDelivery_DoesNotNotify(string change)
    {
        using var database = new SqliteOrbitDbContextFactory();
        await CancelForDeletionAsync(database, "en");
        await SignInAsync(database, false);
        using (var context = database.CreateContext())
        {
            var user = await context.Users.SingleAsync();
            if (change == "deactivated")
                user.Deactivate(DateTime.UtcNow.AddDays(27));
            else if (change == "changed-purchase")
                user.SetPlaySubscription("replacement-token", DateTime.UtcNow.AddDays(20));
            else
                context.Users.Remove(user);
            await context.SaveChangesAsync();
        }

        await RunNoticeJobAsync(database, _noticeJobs.Should().ContainSingle().Subject);

        using var persisted = database.CreateContext();
        (await persisted.Notifications.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task NoticeJob_DismissedNotice_IsNotRecreatedOnReplay()
    {
        using var database = new SqliteOrbitDbContextFactory();
        await CancelForDeletionAsync(database, "en");
        await SignInAsync(database, false);
        var job = _noticeJobs.Should().ContainSingle().Subject;
        await RunNoticeJobAsync(database, job);
        using (var context = database.CreateContext())
        {
            (await context.Notifications.SingleAsync()).SoftDelete();
            await context.SaveChangesAsync();
        }

        await RunNoticeJobAsync(database, job);

        using var persisted = database.CreateContext();
        (await persisted.Notifications.CountAsync()).Should().Be(0);
        (await persisted.Notifications.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignIn_IndependentlyCanceledPlay_DoesNotNotifyOrRemoveOwedRecords(bool google)
    {
        using var database = new SqliteOrbitDbContextFactory();
        var user = User.Create("Returning", "paid@example.com").Value;
        user.SetPlaySubscription("play-token", DateTime.UtcNow.AddDays(20));
        user.Deactivate(DateTime.UtcNow.AddDays(27));
        database.Context.Users.Add(user);
        database.Context.OwedSubscriptionCancellations.Add(
            OwedSubscriptionCancellation.Create(SubscriptionSource.GooglePlay, "play-token"));
        await database.Context.SaveChangesAsync();

        await SignInAsync(database, google);

        using var persisted = database.CreateContext();
        (await persisted.Notifications.CountAsync()).Should().Be(0);
        (await persisted.OwedSubscriptionCancellations.CountAsync()).Should().Be(1);
        _play.ReceivedCalls().Should().BeEmpty();
        _noticeJobs.Should().BeEmpty();
    }

    private async Task<Guid> CancelForDeletionAsync(SqliteOrbitDbContextFactory database, string language)
    {
        var user = User.Create("Returning", "paid@example.com").Value;
        user.SetLanguage(language);
        user.SetPlaySubscription("play-token", DateTime.UtcNow.AddDays(20));
        user.Deactivate(DateTime.UtcNow.AddDays(27));
        database.Context.Users.Add(user);
        await database.Context.SaveChangesAsync();
        using var context = database.CreateContext();
        await CreateCancellationJob(context).ExecuteAsync(user.Id, null, "play-token", true, CancellationToken.None);
        _renewalOff.Should().BeTrue();
        (await context.Users.IgnoreQueryFilters().SingleAsync()).PlayRenewalStoppedByDeletion.Should().BeTrue();
        return user.Id;
    }

    private async Task SignInAsync(SqliteOrbitDbContextFactory database, bool google)
    {
        using var context = database.CreateContext();
        var work = new UnitOfWork(context, new DatabaseConnectionSettings());
        var sessions = Substitute.For<IAuthSessionService>();
        sessions.CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SessionTokens("access", "refresh")));
        var users = new AuthUserStore(context);
        var reactivation = new AccountReactivation(new GenericRepository<OwedSubscriptionCancellation>(context));
        var dispatcher = new SignupJobDispatcher(_jobs, new HttpContextAccessor(), NullLogger<SignupJobDispatcher>.Instance);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set("verify:paid@example.com", new VerificationEntry("123456", 0, DateTime.UtcNow));
        var result = google
            ? await new GoogleSignInFlow(users, work, sessions, dispatcher, _jobs, reactivation)
                .CompleteAsync("paid@example.com", "Returning", "en", null, null, null, CancellationToken.None)
            : await new VerifyCodeCommandHandler(cache, users, work, sessions, dispatcher, _jobs, reactivation)
                .Handle(new VerifyCodeCommand("paid@example.com", "123456"), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
    }

    private CancelSubscriptionOnDeletionJob CreateCancellationJob(OrbitDbContext context) =>
        new(new GenericRepository<User>(context), new UnitOfWork(context, new DatabaseConnectionSettings()),
            _stripe, _play, _settings, CreateOwedJob(context));

    private OwedSubscriptionCancellationJob CreateOwedJob(OrbitDbContext context) =>
        new(new GenericRepository<OwedSubscriptionCancellation>(context),
            new UnitOfWork(context, new DatabaseConnectionSettings()), _stripe, _play, _settings,
            NullLogger<OwedSubscriptionCancellationJob>.Instance, TimeProvider.System);

    private async Task RunNoticeJobAsync(SqliteOrbitDbContextFactory database, Job job)
    {
        using var context = database.CreateContext();
        var play = Substitute.For<IPlayBillingService>();
        play.VerifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            context.Database.CurrentTransaction.Should().BeNull("Play verification must run outside a database transaction");
            return await _play.VerifyAsync(call.ArgAt<string>(0), call.ArgAt<string>(1), call.Arg<CancellationToken>());
        });
        var noticeJob = new NotifyPlayRenewalOnReactivationJob(new GenericRepository<User>(context),
            new GenericRepository<Notification>(context), new UnitOfWork(context, new DatabaseConnectionSettings()), play, _settings);
        var execution = job.Method.Invoke(noticeJob, job.Args.ToArray());
        await execution.Should().BeAssignableTo<Task>().Subject;
    }

    private sealed class ReactivationFailureInterceptor(bool failCommit) : DbTransactionInterceptor, ISaveChangesInterceptor
    {
        public bool Enabled { get; set; }

        public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && !failCommit)
                Fail();
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Enabled && failCommit)
                Fail();
            return ValueTask.FromResult(result);
        }

        private void Fail()
        {
            Enabled = false;
            throw new DbUpdateException("Reactivation persistence failed");
        }
    }
}
