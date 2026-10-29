using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class RestoreHabitQueryTests
{
    private static readonly DateOnly Today = new(2026, 6, 20);
    private static readonly DateTime DeletedAtUtc = new(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    public async Task Restore_LoadsOnlyDeletedSubtreeAndRestoredGoals(int unrelatedCount)
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var context = factory.Context;
        var user = User.Create("Restore User", $"restore-{Guid.NewGuid():N}@example.com").Value;
        var otherUser = User.Create("Other User", $"other-{Guid.NewGuid():N}@example.com").Value;
        context.Users.AddRange(user, otherUser);
        var root = CreateHabit(user.Id);
        var child = CreateHabit(user.Id, root.Id);
        var grandchild = CreateHabit(user.Id, child.Id);
        var separate = CreateHabit(user.Id, root.Id);
        separate.Restore();
        separate.SoftDelete(DeletedAtUtc.AddDays(-1));
        var belowSeparate = CreateHabit(user.Id, separate.Id);
        var live = CreateHabit(user.Id, root.Id);
        live.Restore();
        var belowLive = CreateHabit(user.Id, live.Id);
        var foreignChild = CreateHabit(otherUser.Id, root.Id);
        context.Habits.AddRange(root, child, grandchild, separate, belowSeparate, live, belowLive, foreignChild);
        var restoredIds = new[] { root.Id, child.Id, grandchild.Id, belowSeparate.Id };
        var goal = AddGoal(context, root);
        var leafGoal = AddGoal(context, belowSeparate);
        AddGoal(context, separate);
        for (var index = 0; index < unrelatedCount; index++)
        {
            var unrelated = CreateHabit(user.Id);
            context.Habits.Add(unrelated);
            AddGoal(context, unrelated);
        }
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<Result>>>(0)(call.ArgAt<CancellationToken>(1)));
        var payGate = Substitute.For<IPayGateService>();
        payGate.CanCreateHabits(user.Id, 1, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(Today);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new RestoreHabitCommandHandler(new GenericRepository<Habit>(context), payGate,
            Substitute.For<IUserStreakService>(), Substitute.For<IGoalCompletionService>(), unitOfWork, dates, cache);

        counter.Reset();
        var result = await handler.Handle(new RestoreHabitCommand(user.Id, root.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var trackedHabits = context.ChangeTracker.Entries<Habit>().Select(entry => entry.Entity).ToList();
        trackedHabits.Select(habit => habit.Id).Should().BeEquivalentTo(restoredIds);
        trackedHabits.Should().OnlyContain(habit => !habit.IsDeleted);
        context.ChangeTracker.Entries<Goal>().Select(entry => entry.Entity.Id)
            .Should().BeEquivalentTo([goal.Id, leafGoal.Id]);
        counter.Commands.Sum(command => command.Rows).Should().BeLessThanOrEqualTo(12);
        counter.CommandCount.Should().BeLessThanOrEqualTo(7);
        var parentIndex = context.Model.FindEntityType(typeof(Habit))!.GetIndexes()
            .Single(index => index.GetDatabaseName() == "IX_Habits_ParentHabitId");
        parentIndex.Properties.Select(property => property.Name).Should().Equal(nameof(Habit.ParentHabitId));
    }

    private static Habit CreateHabit(Guid userId, Guid? parentId = null)
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, userId, "Habit",
            FrequencyUnit.Day, 1, DueDate: Today, ParentHabitId: parentId)).Value;
        habit.SoftDelete(DeletedAtUtc);
        return habit;
    }

    private static Goal AddGoal(OrbitDbContext context, Habit habit)
    {
        var goal = Goal.Create(habit.UserId, "Goal", 10m, "sessions").Value;
        habit.AddGoal(goal);
        context.Goals.Add(goal);
        return goal;
    }
}
