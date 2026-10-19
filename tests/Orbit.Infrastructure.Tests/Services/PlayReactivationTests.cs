using System.Data.Common;
using FluentAssertions;
using Hangfire;
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

    public PlayReactivationTests()
    {
        _play.VerifyAsync("orbit_pro", "play-token", Arg.Any<CancellationToken>())
            .Returns(_ => new PlaySubscriptionState(true, DateTime.UtcNow.AddDays(20), SubscriptionInterval.Monthly,
                true, "orbit_pro", null, null, CanRenew: !_renewalOff));
        _play.CancelAsync("play-token", Arg.Any<CancellationToken>())
            .Returns(_ => { _renewalOff = true; return Task.CompletedTask; });
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
    public async Task SignIn_FailedSaveOrCommit_RollsBackNoticeFlagAndOwedRemovalThenRetries(bool google, bool failCommit)
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

        using (var rolledBack = database.CreateContext())
        {
            var user = await rolledBack.Users.IgnoreQueryFilters().SingleAsync();
            user.IsDeactivated.Should().BeTrue();
            user.PlayRenewalStoppedByDeletion.Should().BeTrue();
            (await rolledBack.Notifications.CountAsync()).Should().Be(0);
            (await rolledBack.OwedSubscriptionCancellations.SingleAsync()).Id.Should().Be(owed.Id);
        }
        await SignInAsync(database, google);
        using var persisted = database.CreateContext();
        (await persisted.Users.SingleAsync()).PlayRenewalStoppedByDeletion.Should().BeFalse();
        (await persisted.Notifications.CountAsync()).Should().Be(1);
        (await persisted.OwedSubscriptionCancellations.CountAsync()).Should().Be(0);
        _renewalOff.Should().BeTrue();
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
        var reactivation = new AccountReactivation(new GenericRepository<Notification>(context),
            new GenericRepository<OwedSubscriptionCancellation>(context));
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
