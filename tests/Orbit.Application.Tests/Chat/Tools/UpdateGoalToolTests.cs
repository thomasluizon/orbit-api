using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class UpdateGoalToolTests
{
    private readonly IGenericRepository<Goal> _goalRepository = Substitute.For<IGenericRepository<Goal>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly UpdateGoalTool _tool;
    private static readonly Guid UserId = Guid.NewGuid();

    public UpdateGoalToolTests()
    {
        _tool = new UpdateGoalTool(_goalRepository, _unitOfWork);
    }

    [Fact]
    public async Task ExecuteAsync_UpdatesGoalFields()
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(UserId, "Old title", 10, "books")).Value;
        _goalRepository.FindOneTrackedAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<Goal, bool>>>(),
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Goal?>(goal));

        var result = await Execute(
            $$"""
            {
              "goal_id": "{{goal.Id}}",
              "title": "Read 24 books",
              "description": "Updated description",
              "target_value": 24,
              "unit": "books",
              "deadline": "2026-12-31"
            }
            """);

        result.Success.Should().BeTrue();
        result.EntityId.Should().Be(goal.Id.ToString());
        result.EntityName.Should().Be("Read 24 books");
        goal.Title.Should().Be("Read 24 books");
        goal.Description.Should().Be("Updated description");
        goal.TargetValue.Should().Be(24);
        goal.Deadline.Should().Be(new DateOnly(2026, 12, 31));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenGoalIdMissing()
    {
        var result = await Execute("""{"title":"Missing id"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("goal_id is required");
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
