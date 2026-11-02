using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Goals.Commands;
using Orbit.Application.Goals.Queries;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Queries;
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

public class UserCacheVersionTests
{
    [Fact]
    public async Task MutationInAnotherScope_MakesTheOldEntryUnreachableInBothCaches()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        using var firstDb = factory.CreateContext();
        using var secondDb = factory.CreateContext();
        using var firstMemory = new MemoryCache(new MemoryCacheOptions());
        using var secondMemory = new MemoryCache(new MemoryCacheOptions());
        var first = new VersionedMemoryCache(firstMemory, new UserCacheVersions(firstDb));
        var second = new VersionedMemoryCache(secondMemory, new UserCacheVersions(secondDb));
        var userId = Guid.NewGuid();
        var key = "goal-review:" + userId;
        var oldSuffix = await CacheInvalidationHelper.VersionSuffixAsync(second, userId);
        second.Set(key + oldSuffix, "stale");
        await CacheInvalidationHelper.InvalidateUserAiCachesAsync(first, userId, new DateOnly(2026, 1, 1));
        var nextSuffix = await CacheInvalidationHelper.VersionSuffixAsync(second, userId);
        nextSuffix.Should().NotBe(oldSuffix);
        second.TryGetValue(key + nextSuffix, out _).Should().BeFalse();
        (await new UserCacheVersions(firstDb).GetAsync(userId)).Should().Be(1);
    }

    [Fact]
    public async Task GoalWriteDuringModelCall_CannotPopulateTheNextReviewsVersion()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("User", "cache@example.com").Value;
        var goal = Goal.Create(user.Id, "Before", 100, "pages").Value;
        factory.Context.AddRange(user, goal);
        await factory.Context.SaveChangesAsync();
        using var reader = factory.CreateContext();
        using var writer = factory.CreateContext();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var writerMemory = new MemoryCache(new MemoryCacheOptions());
        var cache = new VersionedMemoryCache(memory, new UserCacheVersions(reader));
        var writerCache = new VersionedMemoryCache(writerMemory, new UserCacheVersions(writer));
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 1, 1));
        var payGate = Substitute.For<IPayGateService>();
        payGate.TryConsumeAiMessage(user.Id, Arg.Any<IUnitOfWork>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var sync = Substitute.For<IGoalProgressReadSyncer>();
        sync.ComputeFreshValuesAsync(user.Id, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, int>());
        var model = Substitute.For<IGoalReviewService>();
        var calls = 0;
        model.GenerateReviewAsync(Arg.Any<string>(), "en", Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(async _ =>
            {
                if (++calls == 1)
                {
                    var update = new UpdateGoalCommandHandler(new(new GenericRepository<Goal>(writer), new GenericRepository<GoalProgressLog>(writer)),
                        dates, Substitute.For<IGoalCompletionService>(), new UnitOfWork(writer, new DatabaseConnectionSettings()), writerCache);
                    (await update.Handle(new(user.Id, goal.Id, "After", null, 100, "pages", null), default)).IsSuccess.Should().BeTrue();
                }
                return Result.Success("review");
            });
        var query = new GetGoalReviewQueryHandler(new GenericRepository<Goal>(reader), payGate,
            new UnitOfWork(reader, new DatabaseConnectionSettings()), model, dates, sync, cache);
        (await query.Handle(new(user.Id, "en"), default)).Value.FromCache.Should().BeFalse();
        (await query.Handle(new(user.Id, "en"), default)).Value.FromCache.Should().BeFalse();
        calls.Should().Be(2);
        (await query.Handle(new(user.Id, "en"), default)).Value.FromCache.Should().BeTrue();
    }

    [Fact]
    public async Task HabitEditKeepingDueDate_MakesTheNextRescheduleReadRegenerate()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("User", "reschedule@example.com").Value;
        var today = new DateOnly(2026, 1, 5);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, user.Id, "Before", null, null, DueDate: today.AddDays(-2))).Value;
        factory.Context.AddRange(user, habit);
        await factory.Context.SaveChangesAsync();
        using var reader = factory.CreateContext();
        using var writer = factory.CreateContext();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var writerMemory = new MemoryCache(new MemoryCacheOptions());
        var cache = new VersionedMemoryCache(memory, new UserCacheVersions(reader));
        var writerCache = new VersionedMemoryCache(writerMemory, new UserCacheVersions(writer));
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(today);
        var gate = Substitute.For<IPayGateService>();
        gate.CanUseSmartReschedule(user.Id, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var model = Substitute.For<IRescheduleSuggestionService>();
        model.GenerateAsync(Arg.Any<Habit>(), today, "en", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new RescheduleSuggestion(FrequencyUnit.Day, 1, today.AddDays(1), null, [], "Tomorrow")));
        var query = new GetRescheduleSuggestionQueryHandler(new GenericRepository<Habit>(reader), new GenericRepository<HabitLog>(reader),
            new GenericRepository<User>(reader), gate, model, cache, dates);
        (await query.Handle(new(user.Id, habit.Id, "en"), default)).Value.FromCache.Should().BeFalse();
        (await query.Handle(new(user.Id, habit.Id, "en"), default)).Value.FromCache.Should().BeTrue();
        var edited = await writer.Habits.SingleAsync();
        edited.Update(new(HabitSchedule.OneTime, "After", null, null, null, null, false, edited.NextDueDate)).IsSuccess.Should().BeTrue();
        await writer.SaveChangesAsync();
        await CacheInvalidationHelper.InvalidateUserAiCachesAsync(writerCache, user.Id, today);
        reader.ChangeTracker.Clear();
        (await query.Handle(new(user.Id, habit.Id, "en"), default)).Value.FromCache.Should().BeFalse();
        await model.Received(2).GenerateAsync(Arg.Any<Habit>(), today, "en", Arg.Any<CancellationToken>());
    }
}
