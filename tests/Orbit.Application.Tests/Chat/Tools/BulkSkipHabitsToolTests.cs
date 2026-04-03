using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class BulkSkipHabitsToolTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly BulkSkipHabitsTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public BulkSkipHabitsToolTests()
    {
        _tool = new BulkSkipHabitsTool(_habitRepo, _habitLogRepo, _userDateService);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
    }

    [Fact]
    public async Task SkipMultiple_ReturnsSkippedNames()
    {
        var h1 = CreateHabit("Water", FrequencyUnit.Day, 1, Today);
        var h2 = CreateHabit("Exercise", FrequencyUnit.Day, 1, Today);
        SetupHabitLookup(h1, h2);

        var result = await Execute($$$"""{"habit_ids": ["{{{h1.Id}}}", "{{{h2.Id}}}"]}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain("Water");
        result.EntityName.Should().Contain("Exercise");
    }

    [Fact]
    public async Task SomeNotFound_SkipsFoundOnes()
    {
        var h1 = CreateHabit("Water", FrequencyUnit.Day, 1, Today);
        var missingId = Guid.NewGuid();
        SetupHabitLookup(h1);

        var result = await Execute($$$"""{"habit_ids": ["{{{h1.Id}}}", "{{{missingId}}}"]}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain("Water");
    }

    [Fact]
    public async Task AllNotFound_ReturnsError()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        // Return null for all lookups
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns((Habit?)null);

        var result = await Execute($$$"""{"habit_ids": ["{{{id1}}}", "{{{id2}}}"]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("No habits were skipped");
    }

    [Fact]
    public async Task CompletedHabits_SkipsCompleted()
    {
        var completed = Habit.Create(new HabitCreateParams(UserId, "Task", null, null, DueDate: Today)).Value;
        completed.Log(Today); // Complete it
        var active = CreateHabit("Water", FrequencyUnit.Day, 1, Today);
        SetupHabitLookup(completed, active);

        var result = await Execute($$$"""{"habit_ids": ["{{{completed.Id}}}", "{{{active.Id}}}"]}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain("Water");
        result.EntityName.Should().NotContain("Task");
    }

    [Fact]
    public async Task EmptyIdList_ReturnsError()
    {
        var result = await Execute("""{"habit_ids": []}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("No valid habit IDs");
    }

    [Fact]
    public async Task MissingHabitIds_ReturnsError()
    {
        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_ids is required");
    }

    [Fact]
    public async Task OneTimeTask_PostponesToTomorrow()
    {
        var task = Habit.Create(new HabitCreateParams(UserId, "Buy milk", null, null, DueDate: Today)).Value;
        SetupHabitLookup(task);

        var result = await Execute($$$"""{"habit_ids": ["{{{task.Id}}}"]}""");

        result.Success.Should().BeTrue();
        task.DueDate.Should().Be(Today.AddDays(1));
    }

    private static Habit CreateHabit(string title, FrequencyUnit? freq, int? qty, DateOnly dueDate)
    {
        return Habit.Create(new HabitCreateParams(UserId, title, freq, qty, DueDate: dueDate)).Value;
    }

    private void SetupHabitLookup(params Habit[] habits)
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns(callInfo =>
        {
            var predicate = callInfo.ArgAt<Expression<Func<Habit, bool>>>(0).Compile();
            return habits.FirstOrDefault(predicate);
        });
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
