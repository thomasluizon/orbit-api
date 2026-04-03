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

public class MoveHabitToolTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly MoveHabitTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public MoveHabitToolTests()
    {
        _tool = new MoveHabitTool(_habitRepo);
    }

    [Fact]
    public async Task MoveUnderParent_SetsParentId()
    {
        var child = CreateHabit("Floss");
        var parent = CreateHabit("Before Bed");
        SetupFindOneTracked(child, parent);
        SetupFindAsyncForCycleCheck(parent);

        var result = await Execute($$$"""{"habit_id": "{{{child.Id}}}", "new_parent_id": "{{{parent.Id}}}"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Floss");
    }

    [Fact]
    public async Task PromoteToTopLevel_ClearsParentId()
    {
        var child = CreateHabit("Floss");
        SetupFindOneTrackedSingle(child);

        // new_parent_id is null (not provided or explicitly null)
        var result = await Execute($$$"""{"habit_id": "{{{child.Id}}}"}""");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task HabitNotFound_ReturnsError()
    {
        var id = Guid.NewGuid();
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns((Habit?)null);

        var result = await Execute($$$"""{"habit_id": "{{{id}}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task ParentNotFound_ReturnsError()
    {
        var child = CreateHabit("Floss");
        var missingParentId = Guid.NewGuid();

        // First call returns the child, second call (parent lookup) returns null
        var callCount = 0;
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns(callInfo =>
        {
            callCount++;
            return callCount == 1 ? child : null;
        });

        var result = await Execute($$$"""{"habit_id": "{{{child.Id}}}", "new_parent_id": "{{{missingParentId}}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task SelfReference_ReturnsError()
    {
        var habit = CreateHabit("Water");
        SetupFindOneTrackedSingle(habit);

        // Second call returns the same habit (for parent lookup)
        var callCount = 0;
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns(callInfo =>
        {
            callCount++;
            return habit;
        });

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "new_parent_id": "{{{habit.Id}}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("own parent");
    }

    [Fact]
    public async Task MissingHabitId_ReturnsError()
    {
        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_id is required");
    }

    private static Habit CreateHabit(string title)
    {
        return Habit.Create(new HabitCreateParams(UserId, title, FrequencyUnit.Day, 1, DueDate: Today)).Value;
    }

    /// <summary>
    /// Sets up FindOneTrackedAsync to return child on first call, parent on second call.
    /// </summary>
    private void SetupFindOneTracked(Habit child, Habit parent)
    {
        var callCount = 0;
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns(callInfo =>
        {
            callCount++;
            return callCount == 1 ? child : parent;
        });
    }

    private void SetupFindOneTrackedSingle(Habit habit)
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns(habit);
    }

    /// <summary>
    /// Sets up FindAsync for cycle detection. Returns parent with no ParentHabitId (breaks chain).
    /// </summary>
    private void SetupFindAsyncForCycleCheck(Habit parent)
    {
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>()
        ).Returns(new List<Habit> { parent }.AsReadOnly());
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
