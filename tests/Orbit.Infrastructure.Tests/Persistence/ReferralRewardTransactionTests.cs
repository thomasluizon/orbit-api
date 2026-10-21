using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class ReferralRewardTransactionTests
{
    [Fact]
    public async Task Handle_FailureAfterSqlSave_RollsBackBothEntitlementsAndRewardThenRetryGrantsOnce()
    {
        var failure = new FailAfterRewardSave();
        using var factory = new SqliteOrbitDbContextFactory(failure);
        var db = factory.Context;
        var nowAtUtc = DateTime.UtcNow;
        var referred = User.Create("Referred", "referred@example.com").Value;
        var referrer = User.Create("Referrer", "referrer@example.com").Value;
        var referral = Referral.Create(referrer.Id, referred.Id);
        typeof(User).GetProperty("CreatedAtUtc")!.SetValue(referred, nowAtUtc.AddDays(-3));
        typeof(Referral).GetProperty("CreatedAtUtc")!.SetValue(referral, nowAtUtc.AddDays(-3));
        var habit = Habit.Create(new HabitCreateParams(referred.Id, "Habit", FrequencyUnit.Day, 1,
            DueDate: DateOnly.FromDateTime(nowAtUtc))).Value;
        db.AddRange(referrer, referred, referral, habit);
        for (var offset = 0; offset < 3; offset++)
            db.HabitLogs.Add(HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id,
                DateOnly.FromDateTime(nowAtUtc).AddDays(-offset), 1, 0, nowAtUtc.AddDays(-offset).AddMinutes(-1)));
        await db.SaveChangesAsync();
        var work = new UnitOfWork(db, new DatabaseConnectionSettings());
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTimeZoneAsync(referred.Id, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        dates.GetUserTodayAsync(referred.Id, Arg.Any<CancellationToken>()).Returns(DateOnly.FromDateTime(nowAtUtc));
        var jobs = Substitute.For<IBackgroundJobClient>();
        jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("queued");
        var repos = new ReferralRepositories(new GenericRepository<User>(db), new GenericRepository<Referral>(db),
            new GenericRepository<Habit>(db), new GenericRepository<HabitLog>(db), new GenericRepository<Notification>(db));
        var handler = new CheckReferralCompletionCommandHandler(repos, jobs, dates, work, TimeProvider.System);
        failure.Enabled = true;

        var attempt = () => handler.Handle(new CheckReferralCompletionCommand(referred.Id), CancellationToken.None);
        await attempt.Should().ThrowAsync<InvalidOperationException>().WithMessage("injected after SQL save");

        failure.SawBothEntitlementsAndReward.Should().BeTrue();
        var persisted = await db.Referrals.AsNoTracking().SingleAsync();
        persisted.Status.Should().Be(ReferralStatus.Pending);
        persisted.RewardGrantedAtUtc.Should().BeNull();
        (await db.Users.AsNoTracking().ToListAsync()).Should().AllSatisfy(u => u.PlanExpiresAt.Should().BeNull());
        failure.Enabled = false;
        (await attempt()).IsSuccess.Should().BeTrue();
        var expiry = (await db.Users.AsNoTracking().SingleAsync(u => u.Id == referred.Id)).PlanExpiresAt;
        expiry.Should().NotBeNull();
        (await attempt()).IsSuccess.Should().BeTrue();
        (await db.Users.AsNoTracking().SingleAsync(u => u.Id == referred.Id)).PlanExpiresAt.Should().Be(expiry);
        (await db.Referrals.AsNoTracking().SingleAsync()).Status.Should().Be(ReferralStatus.Rewarded);
    }

    [Fact]
    public async Task Handle_QueueFailureInAmbientTransaction_PreservesCallerTracking()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var nowAtUtc = DateTime.UtcNow;
        var referred = User.Create("Referred", "referred@example.com").Value;
        var referrer = User.Create("Referrer", "referrer@example.com").Value;
        var caller = User.Create("Caller", "caller@example.com").Value;
        var referral = Referral.Create(referrer.Id, referred.Id);
        typeof(User).GetProperty("CreatedAtUtc")!.SetValue(referred, nowAtUtc.AddDays(-3));
        typeof(Referral).GetProperty("CreatedAtUtc")!.SetValue(referral, nowAtUtc.AddDays(-3));
        var habit = Habit.Create(new HabitCreateParams(referred.Id, "Habit", FrequencyUnit.Day, 1,
            DueDate: DateOnly.FromDateTime(nowAtUtc))).Value;
        db.AddRange(referrer, referred, caller, referral, habit);
        for (var offset = 0; offset < 3; offset++)
            db.HabitLogs.Add(HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id,
                DateOnly.FromDateTime(nowAtUtc).AddDays(-offset), 1, 0, nowAtUtc.AddDays(-offset).AddMinutes(-1)));
        await db.SaveChangesAsync();
        var work = new UnitOfWork(db, new DatabaseConnectionSettings());
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTimeZoneAsync(referred.Id, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        dates.GetUserTodayAsync(referred.Id, Arg.Any<CancellationToken>()).Returns(DateOnly.FromDateTime(nowAtUtc));
        var jobs = Substitute.For<IBackgroundJobClient>();
        jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns(_ => throw new InvalidOperationException("queue unavailable"));
        var repos = new ReferralRepositories(new GenericRepository<User>(db), new GenericRepository<Referral>(db),
            new GenericRepository<Habit>(db), new GenericRepository<HabitLog>(db), new GenericRepository<Notification>(db));
        var handler = new CheckReferralCompletionCommandHandler(repos, jobs, dates, work, TimeProvider.System);
        await using var ambient = await db.Database.BeginTransactionAsync();
        db.Notifications.Add(Notification.Create(caller.Id, "Caller work", "Pending caller work", "/profile"));

        var attempt = () => handler.Handle(new CheckReferralCompletionCommand(referred.Id), CancellationToken.None);
        await attempt.Should().ThrowAsync<InvalidOperationException>().WithMessage("queue unavailable");
        caller.SetLanguage("pt-BR");
        await work.SaveChangesAsync();
        await ambient.CommitAsync();
        db.ChangeTracker.Clear();

        (await db.Users.SingleAsync(u => u.Id == caller.Id)).Language.Should().Be("pt-BR");
        (await db.Notifications.CountAsync()).Should().Be(1);
        (await db.Referrals.SingleAsync()).Status.Should().Be(ReferralStatus.Pending);
        (await db.Users.SingleAsync(u => u.Id == referred.Id)).PlanExpiresAt.Should().BeNull();
    }

    private sealed class FailAfterRewardSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public bool SawBothEntitlementsAndReward { get; private set; }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context is { } db)
            {
                SawBothEntitlementsAndReward = db.ChangeTracker.Entries<Referral>().Any(e => e.Entity.RewardGrantedAtUtc is not null)
                    && db.ChangeTracker.Entries<User>().Count(e => e.Entity.BonusProExpiresAtUtc.HasValue) == 2;
                throw new InvalidOperationException("injected after SQL save");
            }
            return ValueTask.FromResult(result);
        }
    }
}
