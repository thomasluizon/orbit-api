using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class BulkMoveHabitParentPersistenceTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(10, false)]
    [InlineData(100, false)]
    [InlineData(10, true)]
    public async Task Handle_UsesOneHabitSelectAndPersistsBatch(int count, bool promote)
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var context = factory.Context;
        var user = User.Create("User", "bulk-parent@example.com").Value;
        var target = Create(user.Id);
        var habits = Enumerable.Range(0, count).Select(_ => Create(user.Id, promote ? target.Id : null)).ToArray();
        context.Users.Add(user);
        context.Habits.AddRange([target, .. habits]);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        await using var unitOfWork = new UnitOfWork(context, new DatabaseConnectionSettings());
        var handler = CreateHandler(context, unitOfWork, cache, Substitute.For<IUserStreakService>());

        counter.Reset();
        var result = await handler.Handle(new(user.Id, habits.Select(h => h.Id).ToArray(), promote ? null : target.Id),
            CancellationToken.None);

        result.Value.Results.Should().OnlyContain(r => r.Status == BulkItemStatus.Success);
        counter.SelectCommandCount.Should().Be(1);
        counter.Commands.Where(command => command.Sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            .Should().ContainSingle().Which.Sql.Should().Contain("\"Habits\"");
        context.ChangeTracker.Clear();
        var persisted = await context.Habits.Where(h => h.Id != target.Id).ToListAsync();
        persisted.Should().HaveCount(count).And.OnlyContain(h => h.ParentHabitId == (promote ? null : target.Id));
    }

    [Fact]
    public async Task StreakFailureAfterHabitSave_RollsBackAllMoves()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        var user = User.Create("User", "bulk-parent-rollback@example.com").Value;
        var target = Create(user.Id);
        var habits = Enumerable.Range(0, 3).Select(_ => Create(user.Id)).ToArray();
        context.Users.Add(user);
        context.Habits.AddRange([target, .. habits]);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var streak = Substitute.For<IUserStreakService>();
        streak.RecalculateAsync(user.Id, cancellationToken: Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("streak recalculation failed"));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        await using var unitOfWork = new UnitOfWork(context, new DatabaseConnectionSettings());
        var handler = CreateHandler(context, unitOfWork, cache, streak);

        var act = () => handler.Handle(new(user.Id, habits.Select(h => h.Id).ToArray(), target.Id), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        context.ChangeTracker.Clear();
        (await context.Habits.ToListAsync()).Should().OnlyContain(h => h.ParentHabitId == null);
    }

    private static Habit Create(Guid userId, Guid? parent = null) => Habit.Create(new HabitCreateParams(
        userId, "Habit", FrequencyUnit.Day, 1, new DateOnly(2026, 3, 20), ParentHabitId: parent)).Value;

    private static BulkMoveHabitParentCommandHandler CreateHandler(
        OrbitDbContext context, IUnitOfWork unitOfWork, IMemoryCache cache, IUserStreakService streak)
    {
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(AppConfigKeys.MaxHabitDepth, AppConstants.MaxHabitDepth, Arg.Any<CancellationToken>()).Returns(5);
        config.GetAsync(AppConfigKeys.FreeMaxHabits, AppConstants.DefaultFreeMaxHabits, Arg.Any<CancellationToken>()).Returns(200);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 3, 20));
        return new(new GenericRepository<Habit>(context), config, streak, unitOfWork, dates, cache);
    }
}
