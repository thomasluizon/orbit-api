using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class CreateGoalToolTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly CreateGoalTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();

    public CreateGoalToolTests()
    {
        _tool = new CreateGoalTool(_goalRepo, _unitOfWork, _habitRepo);
    }

    [Fact]
    public async Task SuccessfulCreation_ReturnsSuccessWithTitle()
    {
        var result = await Execute("""{"title": "Read 12 books"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Read 12 books");
        result.EntityId.Should().NotBeNullOrEmpty();
        await _goalRepo.Received(1).AddAsync(Arg.Any<Goal>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingTitle_ReturnsError()
    {
        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("title is required");
    }

    [Fact]
    public async Task EmptyTitle_ReturnsError()
    {
        var result = await Execute("""{"title": "  "}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("title is required");
    }

    [Fact]
    public async Task WithTargetAndUnit_CreatesGoalCorrectly()
    {
        var result = await Execute("""{"title": "Read books", "target_value": 12, "unit": "books"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Read books");
    }

    [Fact]
    public async Task WithDeadline_CreatesGoalWithDeadline()
    {
        var result = await Execute("""{"title": "Lose weight", "target_value": 5, "unit": "kg", "deadline": "2026-12-31"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Lose weight");
    }

    [Fact]
    public async Task DefaultTargetValue_UsesOne()
    {
        var result = await Execute("""{"title": "Complete goal"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Complete goal");
    }

    [Fact]
    public async Task WithDescription_CreatesGoalWithDescription()
    {
        var result = await Execute("""{"title": "Save money", "description": "For vacation fund", "target_value": 5000, "unit": "dollars"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Save money");
    }

    [Fact]
    public void ParameterSchema_ExposesOptionalHabitIds()
    {
        JsonSerializer.Serialize(_tool.GetParameterSchema()).Should().Contain("habit_ids");
    }

    [Fact]
    public async Task CreateGoalTool_WithHabitIds_LinksThem()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Read", FrequencyUnit.Day, 1, DueDate: new DateOnly(2026, 8, 6))).Value;
        _habitRepo.FindTrackedAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns([habit]);

        var result = await Execute($$$"""{"title":"Read daily","habit_ids":["{{{habit.Id}}}"]}""");

        result.Success.Should().BeTrue();
        await _goalRepo.Received(1).AddAsync(
            Arg.Is<Goal>(goal => goal.Habits.Count == 1 && goal.Habits.Contains(habit)),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateGoalTool_WithForeignHabitId_FailsWithoutCreatingGoal()
    {
        _habitRepo.FindTrackedAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await Execute($$$"""{"title":"Read daily","habit_ids":["{{{Guid.NewGuid()}}}"]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Be(ErrorMessages.HabitNotFound.Message);
        await _goalRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
