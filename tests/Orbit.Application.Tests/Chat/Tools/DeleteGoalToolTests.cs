using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class DeleteGoalToolTests
{
    private readonly IGenericRepository<Goal> _goalRepository = Substitute.For<IGenericRepository<Goal>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly DeleteGoalTool _tool;
    private static readonly Guid UserId = Guid.NewGuid();

    public DeleteGoalToolTests()
    {
        _tool = new DeleteGoalTool(_goalRepository, _unitOfWork);
    }

    [Fact]
    public async Task ExecuteAsync_SoftDeletesGoal()
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(UserId, "Run a marathon", 42, "km")).Value;
        _goalRepository.FindOneTrackedAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<Goal, bool>>>(),
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Goal?>(goal));

        var result = await Execute($$"""{"goal_id":"{{goal.Id}}"}""");

        result.Success.Should().BeTrue();
        goal.IsDeleted.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
