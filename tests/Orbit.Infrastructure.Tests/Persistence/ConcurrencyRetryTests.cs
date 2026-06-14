using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Goals.Commands;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

/// <summary>
/// Exercises the optimistic-concurrency conflict handling that backs the xmin-tokened User/Goal
/// mutations. The EF in-memory provider does not enforce xmin, so the conflict is injected with a
/// save interceptor that throws <see cref="DbUpdateConcurrencyException"/> on the first save (the
/// same shape Postgres raises on a stale token). Tests assert the HANDLERS' response to a conflict —
/// retry-with-re-evaluation for counters/progress, and a clean conflict result when it persists —
/// not real database token behavior, which is unreachable in this harness.
/// </summary>
public class ConcurrencyRetryTests
{
    private const int AdRewardDailyCap = 3;
    private const int AdRewardBonusPerClaim = 5;

    [Fact]
    public async Task ClaimAdReward_ConflictThenAtCapOnReload_DoesNotOverGrant()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dbName = NewDbName();
        Guid userId;

        await using (var seed = CreateContext(dbName))
        {
            var seedUser = CreateFreeUser();
            seedUser.GrantAdReward(today, dailyCap: AdRewardDailyCap);
            seedUser.GrantAdReward(today, dailyCap: AdRewardDailyCap);
            seed.Users.Add(seedUser);
            await seed.SaveChangesAsync();
            userId = seedUser.Id;
        }

        var interceptor = new ConflictOnceInterceptor(onFirstSave: () =>
        {
            using var racer = CreateContext(dbName);
            var racedUser = racer.Users.Single(u => u.Id == userId);
            racedUser.GrantAdReward(today, dailyCap: AdRewardDailyCap);
            racer.SaveChanges();
        });

        await using var context = CreateContext(dbName, interceptor);
        var handler = new ClaimAdRewardCommandHandler(
            new GenericRepository<User>(context), new UnitOfWork(context), StubToday(today), StubLimit());

        var result = await handler.Handle(new ClaimAdRewardCommand(userId), CancellationToken.None);

        interceptor.SaveAttempts.Should().Be(1);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DomainErrors.AdRewardLimitReached.Code);

        await using var verify = CreateContext(dbName);
        var persisted = verify.Users.Single(u => u.Id == userId);
        persisted.AdRewardsClaimedToday.Should().Be(AdRewardDailyCap);
        persisted.AdRewardBonusMessages.Should().Be(AdRewardBonusPerClaim * AdRewardDailyCap);
    }

    [Fact]
    public async Task ClaimAdReward_ConflictThenStillUnderCap_RetriesAndGrantsOnce()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dbName = NewDbName();
        Guid userId;

        await using (var seed = CreateContext(dbName))
        {
            var seedUser = CreateFreeUser();
            seed.Users.Add(seedUser);
            await seed.SaveChangesAsync();
            userId = seedUser.Id;
        }

        var interceptor = new ConflictOnceInterceptor();
        await using var context = CreateContext(dbName, interceptor);
        var handler = new ClaimAdRewardCommandHandler(
            new GenericRepository<User>(context), new UnitOfWork(context), StubToday(today), StubLimit());

        var result = await handler.Handle(new ClaimAdRewardCommand(userId), CancellationToken.None);

        interceptor.SaveAttempts.Should().Be(2);
        result.IsSuccess.Should().BeTrue();

        await using var verify = CreateContext(dbName);
        var persisted = verify.Users.Single(u => u.Id == userId);
        persisted.AdRewardsClaimedToday.Should().Be(1);
        persisted.AdRewardBonusMessages.Should().Be(AdRewardBonusPerClaim);
    }

    [Fact]
    public async Task UpdateGoalProgress_Conflict_RetriesAndWritesOneCoherentLog()
    {
        var dbName = NewDbName();
        Guid userId, goalId;

        await using (var seed = CreateContext(dbName))
        {
            var seedGoal = CreateGoal(targetValue: 100, out userId);
            seed.Goals.Add(seedGoal);
            await seed.SaveChangesAsync();
            goalId = seedGoal.Id;
        }

        var interceptor = new ConflictOnceInterceptor();
        await using var context = CreateContext(dbName, interceptor);
        var handler = CreateGoalProgressHandler(context);

        var result = await handler.Handle(
            new UpdateGoalProgressCommand(userId, goalId, 40, "leg day"), CancellationToken.None);

        interceptor.SaveAttempts.Should().Be(2);
        result.IsSuccess.Should().BeTrue();

        await using var verify = CreateContext(dbName);
        var goal = verify.Goals.Single(g => g.Id == goalId);
        goal.CurrentValue.Should().Be(40);
        var logs = verify.GoalProgressLogs.Where(l => l.GoalId == goalId).ToList();
        logs.Should().HaveCount(1);
        logs[0].PreviousValue.Should().Be(0);
        logs[0].Value.Should().Be(40);
    }

    [Fact]
    public async Task UpdateGoalProgress_PersistentConflict_ReturnsConflictResult()
    {
        var dbName = NewDbName();
        Guid userId, goalId;

        await using (var seed = CreateContext(dbName))
        {
            var seedGoal = CreateGoal(targetValue: 100, out userId);
            seed.Goals.Add(seedGoal);
            await seed.SaveChangesAsync();
            goalId = seedGoal.Id;
        }

        var interceptor = new ConflictAlwaysInterceptor();
        await using var context = CreateContext(dbName, interceptor);
        var handler = CreateGoalProgressHandler(context);

        var result = await handler.Handle(
            new UpdateGoalProgressCommand(userId, goalId, 40), CancellationToken.None);

        interceptor.SaveAttempts.Should().Be(2);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ConcurrentUpdateConflict);

        await using var verify = CreateContext(dbName);
        verify.GoalProgressLogs.Count(l => l.GoalId == goalId).Should().Be(0);
    }

    [Fact]
    public async Task PlainEdit_PersistentConflict_PropagatesConcurrencyExceptionForCentral409()
    {
        var dbName = NewDbName();
        Guid goalId;

        await using (var seed = CreateContext(dbName))
        {
            var seedGoal = CreateGoal(targetValue: 100, out _);
            seed.Goals.Add(seedGoal);
            await seed.SaveChangesAsync();
            goalId = seedGoal.Id;
        }

        await using var context = CreateContext(dbName, new ConflictAlwaysInterceptor());
        var unitOfWork = new UnitOfWork(context);
        var goal = context.Goals.Single(g => g.Id == goalId);
        goal.Update("Renamed", null, 120, "kg", null);

        var act = async () => await unitOfWork.SaveChangesAsync(CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    private static string NewDbName() => $"ConcurrencyRetryTests_{Guid.NewGuid()}";

    private static User CreateFreeUser()
    {
        var user = User.Create("Tester", $"{Guid.NewGuid():N}@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        return user;
    }

    private static Goal CreateGoal(decimal targetValue, out Guid userId)
    {
        userId = Guid.NewGuid();
        return Goal.Create(new Goal.CreateGoalParams(userId, "Squat", targetValue, "kg")).Value;
    }

    private static UpdateGoalProgressCommandHandler CreateGoalProgressHandler(OrbitDbContext context) =>
        new(
            new GenericRepository<Goal>(context),
            new GenericRepository<GoalProgressLog>(context),
            PassingGoalGate(),
            Substitute.For<IGamificationService>(),
            new UnitOfWork(context),
            NullLogger<UpdateGoalProgressCommandHandler>.Instance);

    private static OrbitDbContext CreateContext(string dbName, ISaveChangesInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<OrbitDbContext>().UseInMemoryDatabase(dbName);
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);
        return new OrbitDbContext(builder.Options);
    }

    private static IUserDateService StubToday(DateOnly today)
    {
        var service = Substitute.For<IUserDateService>();
        service.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(today);
        return service;
    }

    private static IPayGateService StubLimit()
    {
        var payGate = Substitute.For<IPayGateService>();
        payGate.GetAiMessageLimit(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(25);
        return payGate;
    }

    private static IPayGateService PassingGoalGate()
    {
        var payGate = Substitute.For<IPayGateService>();
        payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        return payGate;
    }

    private sealed class ConflictOnceInterceptor(Action? onFirstSave = null) : SaveChangesInterceptor
    {
        public int SaveAttempts { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            if (SaveAttempts == 1)
            {
                onFirstSave?.Invoke();
                throw new DbUpdateConcurrencyException("simulated stale token");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class ConflictAlwaysInterceptor : SaveChangesInterceptor
    {
        public int SaveAttempts { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            throw new DbUpdateConcurrencyException("simulated stale token");
        }
    }
}
