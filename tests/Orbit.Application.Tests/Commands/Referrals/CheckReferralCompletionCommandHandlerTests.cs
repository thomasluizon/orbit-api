using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Referrals.Commands;
using Orbit.Application.Referrals.Jobs;
using Orbit.Application.Referrals.Validators;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Application.Tests.Commands.Referrals;

public class CheckReferralCompletionCommandHandlerTests : IDisposable
{
    private readonly DbContextOptions<OrbitDbContext> _options = new DbContextOptionsBuilder<OrbitDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
    private readonly OrbitDbContext _db;
    private readonly IUnitOfWork _work = Substitute.For<IUnitOfWork>().PassThroughTransactions();
    private readonly IBackgroundJobClient _jobs = Substitute.For<IBackgroundJobClient>();
    private readonly IUserDateService _dates = Substitute.For<IUserDateService>();
    private readonly DateTime _nowAtUtc = DateTime.UtcNow;
    private readonly User _referred = User.Create("Referred", "referred@example.com").Value;
    private readonly User _referrer = User.Create("Referrer", "referrer@example.com").Value;
    private readonly Referral _referral;
    private readonly Habit _habit;
    private readonly CheckReferralCompletionCommandHandler _handler;
    private readonly ReferralRepositories _repos;
    private Action? _beforeSave;
    private Func<Task>? _beforeSaveAsync;

    public CheckReferralCompletionCommandHandlerTests()
    {
        _db = new OrbitDbContext(_options);
        _referral = Referral.Create(_referrer.Id, _referred.Id);
        SetCreated(_referred, _nowAtUtc.AddDays(-3));
        SetCreated(_referral, _nowAtUtc.AddDays(-3));
        _habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _referred.Id, "Habit", FrequencyUnit.Day, 1,
            DueDate: DateOnly.FromDateTime(_nowAtUtc))).Value;
        _db.AddRange(_referrer, _referred, _referral, _habit);
        _db.SaveChanges();
        _repos = new(new GenericRepository<User>(_db), new GenericRepository<Referral>(_db),
            new GenericRepository<Habit>(_db), new GenericRepository<HabitLog>(_db), new GenericRepository<Notification>(_db));
        _work.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            _beforeSave?.Invoke();
            if (_beforeSaveAsync is not null)
                await _beforeSaveAsync();
            return await _db.SaveChangesAsync(call.Arg<CancellationToken>());
        });
        _dates.GetUserTimeZoneAsync(_referred.Id, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        _dates.GetUserTodayAsync(_referred.Id, Arg.Any<CancellationToken>()).Returns(DateOnly.FromDateTime(_nowAtUtc));
        _jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("job-id");
        _handler = new(_repos, _jobs, _dates, _work, new Clock(new DateTimeOffset(_nowAtUtc)));
    }

    private static void SetCreated(object entity, DateTime instant) => entity.GetType().GetProperty("CreatedAtUtc")!.SetValue(entity, instant);

    private async Task AddLogAsync(int daysAgo, decimal value = 1, DateTime? createdAtUtc = null)
    {
        var created = createdAtUtc ?? _nowAtUtc.AddDays(-daysAgo).AddHours(-1);
        var log = HabitLog.FromScheduleRead(Guid.NewGuid(), _habit.Id,
            DateOnly.FromDateTime(_nowAtUtc).AddDays(-daysAgo), value, 0, created);
        _db.HabitLogs.Add(log);
        await _db.SaveChangesAsync();
    }

    private async Task QualifyingLogsAsync()
    {
        await AddLogAsync(2);
        await AddLogAsync(1);
        await AddLogAsync(0);
    }

    private Task<Orbit.Domain.Common.Result> RunAsync() => _handler.Handle(new(_referred.Id), CancellationToken.None);

    [Fact]
    public async Task Handle_ThreeLogsOnSameDay_DoesNotQualify()
    {
        await AddLogAsync(0);
        await AddLogAsync(0);
        await AddLogAsync(0);
        (await RunAsync()).IsSuccess.Should().BeTrue();
        _referral.Status.Should().Be(ReferralStatus.Pending);
        _jobs.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ThreeDistinctDays_GrantsBothPartiesOnceAndQueuesDelivery()
    {
        await QualifyingLogsAsync();
        (await RunAsync()).IsSuccess.Should().BeTrue();
        var referrerExpiry = _referrer.PlanExpiresAt;
        var referredExpiry = _referred.PlanExpiresAt;
        (await RunAsync()).IsSuccess.Should().BeTrue();
        _referral.Status.Should().Be(ReferralStatus.Rewarded);
        _referral.RewardGrantedAtUtc.Should().NotBeNull();
        _referrer.IsPro.Should().BeTrue();
        _referred.IsPro.Should().BeTrue();
        _referrer.PlanExpiresAt.Should().Be(referrerExpiry);
        _referred.PlanExpiresAt.Should().Be(referredExpiry);
        _referrer.PlanExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(AppConstants.ReferralBonusProDays), TimeSpan.FromSeconds(5));
        _jobs.Received(2).Create(Arg.Is<Job>(j => j.Type == typeof(DeliverReferralRewardJob)), Arg.Any<IState>());
        (await _db.Notifications.CountAsync()).Should().Be(0);
        await _work.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handle_PaidSubscriber_QueuesCouponAndKeepsExactExpiry(bool stripe)
    {
        var expiry = _nowAtUtc.AddDays(30).AddTicks(1234);
        if (stripe)
            _referrer.SetStripeSubscription("sub_paid", expiry);
        else
            _referrer.SetPlaySubscription("play_paid", expiry);
        await _db.SaveChangesAsync();
        await QualifyingLogsAsync();
        await RunAsync();
        _referrer.PlanExpiresAt.Should().Be(expiry);
        _referral.ReferrerReward.Should().Be(ReferralRewardKind.Coupon);
        _referral.ReferredReward.Should().Be(ReferralRewardKind.ProDays);
        _referred.IsPro.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handle_PreReferralDateOrTimestamp_DoesNotQualify(bool priorDate)
    {
        await AddLogAsync(1);
        await AddLogAsync(0);
        await AddLogAsync(priorDate ? 4 : 2, createdAtUtc: priorDate ? _nowAtUtc.AddDays(-1) : _referral.CreatedAtUtc.AddTicks(-1));
        await RunAsync();
        _referral.Status.Should().Be(ReferralStatus.Pending);
    }

    [Fact]
    public async Task Handle_WindowExpired_DoesNotQualify()
    {
        await QualifyingLogsAsync();
        SetCreated(_referred, _nowAtUtc.AddDays(-8));
        await _db.SaveChangesAsync();
        await RunAsync();
        _referral.Status.Should().Be(ReferralStatus.Pending);
    }

    [Fact]
    public async Task Handle_WindowBoundary_QualifiesAtExactEnd()
    {
        await QualifyingLogsAsync();
        SetCreated(_referred, _nowAtUtc.AddDays(-7));
        await _db.SaveChangesAsync();
        await RunAsync();
        _referral.Status.Should().Be(ReferralStatus.Rewarded);
    }

    [Fact]
    public async Task Handle_SkipsDeletedZeroAndFutureLogs()
    {
        await AddLogAsync(2);
        await AddLogAsync(1, value: 0);
        await AddLogAsync(-1);
        await AddLogAsync(0);
        _db.HabitLogs.First(l => l.Date == DateOnly.FromDateTime(_nowAtUtc)).SoftDelete();
        await _db.SaveChangesAsync();
        await RunAsync();
        _referral.Status.Should().Be(ReferralStatus.Pending);
    }

    [Fact]
    public async Task Handle_TimeZoneBoundary_UsesLocalReferralDate()
    {
        var created = new DateTime(_nowAtUtc.Year, _nowAtUtc.Month, _nowAtUtc.Day, 1, 0, 0, DateTimeKind.Utc).AddDays(-2);
        SetCreated(_referral, created);
        await _db.SaveChangesAsync();
        _dates.GetUserTimeZoneAsync(_referred.Id, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"));
        var localDate = DateOnly.FromDateTime(created).AddDays(-1);
        foreach (var offset in new[] { 0, 1, 2 })
        {
            _db.HabitLogs.Add(HabitLog.FromScheduleRead(Guid.NewGuid(), _habit.Id, localDate.AddDays(offset), 1, 0, created.AddHours(offset + 1)));
        }
        await _db.SaveChangesAsync();
        await RunAsync();
        _referral.Status.Should().Be(ReferralStatus.Rewarded);
    }

    [Fact]
    public async Task Handle_AlreadyGrantedTimestamp_DoesNotGrantAgain()
    {
        await QualifyingLogsAsync();
        _referral.MarkRewarded();
        typeof(Referral).GetProperty("Status")!.SetValue(_referral, ReferralStatus.Pending);
        await _db.SaveChangesAsync();
        await RunAsync();
        _referrer.PlanExpiresAt.Should().BeNull();
        _jobs.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_FailureAfterMarkRewardedBeforeSave_RetryGrantsExactlyOnce()
    {
        await QualifyingLogsAsync();
        var fail = true;
        _beforeSave = () =>
        {
            _referral.Status.Should().Be(ReferralStatus.Rewarded);
            _referrer.IsPro.Should().BeTrue();
            if (fail)
                throw new InvalidOperationException("injected before commit");
        };
        var first = () => RunAsync();
        await first.Should().ThrowAsync<InvalidOperationException>();
        var persisted = await _db.Referrals.AsNoTracking().SingleAsync();
        persisted.Status.Should().Be(ReferralStatus.Pending);
        persisted.RewardGrantedAtUtc.Should().BeNull();
        (await _db.Users.AsNoTracking().SingleAsync(u => u.Id == _referrer.Id)).PlanExpiresAt.Should().BeNull();
        fail = false;
        await RunAsync();
        var expiry = (await _db.Users.AsNoTracking().SingleAsync(u => u.Id == _referrer.Id)).PlanExpiresAt;
        await RunAsync();
        (await _db.Users.AsNoTracking().SingleAsync(u => u.Id == _referrer.Id)).PlanExpiresAt.Should().Be(expiry);
    }

    [Fact]
    public async Task Handle_QueueFailure_RollsBackEntitlementAndRemainsRetryable()
    {
        await QualifyingLogsAsync();
        _jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns(_ => throw new InvalidOperationException("queue unavailable"));
        var run = () => RunAsync();
        await run.Should().ThrowAsync<InvalidOperationException>();
        (await _db.Referrals.AsNoTracking().SingleAsync()).Status.Should().Be(ReferralStatus.Pending);
        (await _db.Users.AsNoTracking().SingleAsync(u => u.Id == _referred.Id)).PlanExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task Handle_ConcurrentLoser_ReturnsSuccessAndOnlyWinnerGrants()
    {
        await QualifyingLogsAsync();
        using var winnerDb = new OrbitDbContext(_options);
        var winnerWork = Substitute.For<IUnitOfWork>().PassThroughTransactions();
        winnerWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(call => winnerDb.SaveChangesAsync(call.Arg<CancellationToken>()));
        var winnerRepos = new ReferralRepositories(new GenericRepository<User>(winnerDb), new GenericRepository<Referral>(winnerDb),
            new GenericRepository<Habit>(winnerDb), new GenericRepository<HabitLog>(winnerDb), new GenericRepository<Notification>(winnerDb));
        var winner = new CheckReferralCompletionCommandHandler(winnerRepos, _jobs, _dates, winnerWork,
            new Clock(new DateTimeOffset(_nowAtUtc)));
        var reachedSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoser = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _beforeSaveAsync = async () =>
        {
            reachedSave.TrySetResult();
            await releaseLoser.Task;
            throw new DbUpdateConcurrencyException("winner committed first");
        };
        var loser = RunAsync();
        await reachedSave.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            (await winner.Handle(new CheckReferralCompletionCommand(_referred.Id), CancellationToken.None)).IsSuccess.Should().BeTrue();
        }
        finally
        {
            releaseLoser.TrySetResult();
        }
        (await loser).IsSuccess.Should().BeTrue();
        var expiry = (await _db.Users.AsNoTracking().SingleAsync(u => u.Id == _referred.Id)).PlanExpiresAt;
        expiry.Should().BeCloseTo(DateTime.UtcNow.AddDays(7), TimeSpan.FromSeconds(5));
        _beforeSaveAsync = null;
        await RunAsync();
        (await _db.Users.AsNoTracking().SingleAsync(u => u.Id == _referred.Id)).PlanExpiresAt.Should().Be(expiry);
    }

    [Fact]
    public async Task Handle_CapIncludesIncomingRewardAndDoesNotBlockOtherParticipant()
    {
        var incoming = Referral.Create(Guid.NewGuid(), _referrer.Id);
        incoming.MarkRewarded(ReferralRewardKind.ProDays, ReferralRewardKind.ProDays);
        _db.Referrals.Add(incoming);
        for (var i = 1; i < AppConstants.ReferralRewardCap; i++)
        {
            var historical = Referral.Create(_referrer.Id, Guid.NewGuid());
            historical.MarkRewarded(ReferralRewardKind.ProDays, ReferralRewardKind.ProDays);
            _db.Referrals.Add(historical);
        }
        await _db.SaveChangesAsync();
        await QualifyingLogsAsync();
        await RunAsync();
        _referral.ReferrerReward.Should().Be(ReferralRewardKind.None);
        _referral.ReferredReward.Should().Be(ReferralRewardKind.ProDays);
        _referrer.PlanExpiresAt.Should().BeNull();
        _referred.IsPro.Should().BeTrue();
        _jobs.Received(1).Create(Arg.Any<Job>(), Arg.Any<IState>());
        AppConstants.ReferralBonusProDays.Should().Be(7);
        AppConstants.ReferralRewardCap.Should().Be(5);
        AppConstants.DefaultMaxReferrals.Should().Be(10);
    }

    [Fact]
    public async Task ExecuteAsync_CouponFailureKeepsBonusAndRetriesWithStableKey()
    {
        _referrer.SetStripeSubscription("sub_paid", _nowAtUtc.AddDays(30));
        await _db.SaveChangesAsync();
        await QualifyingLogsAsync();
        await RunAsync();
        var bonusExpiry = _referred.PlanExpiresAt;
        var paidExpiry = _referrer.PlanExpiresAt;
        var rewards = Substitute.For<IReferralRewardService>();
        var push = Substitute.For<IPushNotificationService>();
        var fail = true;
        var key = $"referral-reward-{_referral.Id}-{_referrer.Id}";
        rewards.CreateReferralCouponAsync(_referrer.Id, key, Arg.Any<CancellationToken>())
            .Returns(_ => fail ? Task.FromException<string>(new InvalidOperationException("Stripe unavailable")) : Task.FromResult("coupon_paid"));
        var job = new DeliverReferralRewardJob(_repos, rewards, push, _work);
        var first = () => job.ExecuteAsync(_referral.Id, _referrer.Id, CancellationToken.None);
        await first.Should().ThrowAsync<InvalidOperationException>();
        _db.ChangeTracker.Clear();
        (await _db.Users.AsNoTracking().SingleAsync(u => u.Id == _referred.Id)).PlanExpiresAt.Should().Be(bonusExpiry);
        (await _db.Referrals.AsNoTracking().SingleAsync()).ReferrerRewardDeliveredAtUtc.Should().BeNull();
        fail = false;
        await job.ExecuteAsync(_referral.Id, _referrer.Id, CancellationToken.None);
        await job.ExecuteAsync(_referral.Id, _referrer.Id, CancellationToken.None);
        await rewards.Received(2).CreateReferralCouponAsync(_referrer.Id, key, Arg.Any<CancellationToken>());
        await rewards.Received(1).ApplyCouponToSubscriptionAsync("sub_paid", "coupon_paid", Arg.Any<CancellationToken>());
        (await _db.Users.AsNoTracking().SingleAsync(u => u.Id == _referrer.Id)).PlanExpiresAt.Should().Be(paidExpiry);
        (await _db.Notifications.CountAsync()).Should().Be(1);
        await push.Received(1).SendToUserAsync(_referrer.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", "7 days of Pro")]
    [InlineData("pt-BR", "7 dias de Pro")]
    public async Task ExecuteAsync_ProDays_DeliversOnceWithCorrectReward(string language, string expected)
    {
        _referrer.SetLanguage(language);
        _referred.SetLanguage(language);
        await _db.SaveChangesAsync();
        await QualifyingLogsAsync();
        await RunAsync();
        var rewards = Substitute.For<IReferralRewardService>();
        var push = Substitute.For<IPushNotificationService>();
        var job = new DeliverReferralRewardJob(_repos, rewards, push, _work);
        foreach (var id in new[] { _referrer.Id, _referred.Id, _referrer.Id, _referred.Id })
            await job.ExecuteAsync(_referral.Id, id, CancellationToken.None);
        rewards.ReceivedCalls().Should().BeEmpty();
        var notifications = await _db.Notifications.ToListAsync();
        notifications.Should().HaveCount(2);
        notifications.Should().AllSatisfy(n => n.Body.Should().Contain(expected));
        _referral.ReferrerRewardDeliveredAtUtc.Should().NotBeNull();
        _referral.ReferredRewardDeliveredAtUtc.Should().NotBeNull();
        await push.Received(2).SendToUserAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_PlaySubscriberWithRetainedStripeId_StoresCouponForPlay()
    {
        _referrer.SetStripeSubscription("sub_old", _nowAtUtc.AddDays(-1));
        _referrer.SetPlaySubscription("play_current", _nowAtUtc.AddDays(30));
        await _db.SaveChangesAsync();
        await QualifyingLogsAsync();
        await RunAsync();
        var rewards = Substitute.For<IReferralRewardService>();
        var key = $"referral-reward-{_referral.Id}-{_referrer.Id}";
        rewards.CreateReferralCouponAsync(_referrer.Id, key, Arg.Any<CancellationToken>()).Returns("coupon_play");
        var job = new DeliverReferralRewardJob(_repos, rewards, Substitute.For<IPushNotificationService>(), _work);

        await job.ExecuteAsync(_referral.Id, _referrer.Id, CancellationToken.None);

        _referrer.ReferralCouponId.Should().Be("coupon_play");
        await rewards.DidNotReceive().ApplyCouponToSubscriptionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_JobArrivingBeforeCommit_IsRetryableAndGrantsNothing()
    {
        var rewards = Substitute.For<IReferralRewardService>();
        var push = Substitute.For<IPushNotificationService>();
        var job = new DeliverReferralRewardJob(_repos, rewards, push, _work);
        var run = () => job.ExecuteAsync(_referral.Id, _referrer.Id, CancellationToken.None);
        await run.Should().ThrowAsync<InvalidOperationException>();
        rewards.ReceivedCalls().Should().BeEmpty();
        push.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_NoReferral_ReturnsSuccessWithoutQueueing()
    {
        _db.Referrals.Remove(_referral);
        await _db.SaveChangesAsync();
        (await RunAsync()).IsSuccess.Should().BeTrue();
        _jobs.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Validator_RequiresAccountId(bool valid)
    {
        var result = new CheckReferralCompletionCommandValidator().Validate(new CheckReferralCompletionCommand(valid ? Guid.NewGuid() : Guid.Empty));
        result.IsValid.Should().Be(valid);
    }

    public void Dispose() => _db.Dispose();

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
