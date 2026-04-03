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

public class UpdateGoalProgressToolTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<GoalProgressLog> _progressLogRepo = Substitute.For<IGenericRepository<GoalProgressLog>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly UpdateGoalProgressTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();

    public UpdateGoalProgressToolTests()
    {
        _tool = new UpdateGoalProgressTool(_goalRepo, _progressLogRepo, _unitOfWork);
    }

    [Fact]
    public async Task ExecuteAsync_GoalFound_UpdatesProgress()
    {
        var goal = Goal.Create(UserId, "Lose Weight", 10, "kg").Value;
        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal });

        var args = JsonDocument.Parse("{\"goal_name\": \"Lose Weight\", \"current_value\": 5}").RootElement;
        var result = await _tool.ExecuteAsync(args, UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Lose Weight");
        result.EntityId.Should().Be(goal.Id.ToString());
        await _progressLogRepo.Received(1).AddAsync(Arg.Any<GoalProgressLog>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_GoalNotFound_ReturnsError()
    {
        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal>());

        var args = JsonDocument.Parse("{\"goal_name\": \"Nonexistent\", \"current_value\": 5}").RootElement;
        var result = await _tool.ExecuteAsync(args, UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("No active goal found");
    }

    [Fact]
    public async Task ExecuteAsync_MissingGoalName_ReturnsError()
    {
        var args = JsonDocument.Parse("{\"current_value\": 5}").RootElement;
        var result = await _tool.ExecuteAsync(args, UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("goal_name is required");
    }

    [Fact]
    public async Task ExecuteAsync_MissingCurrentValue_ReturnsError()
    {
        var args = JsonDocument.Parse("{\"goal_name\": \"Test\"}").RootElement;
        var result = await _tool.ExecuteAsync(args, UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("current_value is required");
    }

    [Fact]
    public async Task ExecuteAsync_FuzzyMatchByContains_FindsGoal()
    {
        var goal = Goal.Create(UserId, "Lose 10kg by Summer", 10, "kg").Value;
        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal });

        var args = JsonDocument.Parse("{\"goal_name\": \"Lose\", \"current_value\": 3}").RootElement;
        var result = await _tool.ExecuteAsync(args, UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Lose 10kg by Summer");
    }

    [Fact]
    public async Task ExecuteAsync_WithNote_PassesNoteToProgressLog()
    {
        var goal = Goal.Create(UserId, "Read Books", 12, "books").Value;
        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal });

        var args = JsonDocument.Parse("{\"goal_name\": \"Read Books\", \"current_value\": 3, \"note\": \"Finished book 3\"}").RootElement;
        var result = await _tool.ExecuteAsync(args, UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        await _progressLogRepo.Received(1).AddAsync(Arg.Any<GoalProgressLog>(), Arg.Any<CancellationToken>());
    }
}
