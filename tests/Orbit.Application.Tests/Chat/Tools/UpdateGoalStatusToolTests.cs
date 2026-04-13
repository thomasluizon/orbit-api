using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class UpdateGoalStatusToolTests
{
    private readonly IGenericRepository<Goal> _goalRepository = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGamificationService _gamificationService = Substitute.For<IGamificationService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ILogger<UpdateGoalStatusTool> _logger = Substitute.For<ILogger<UpdateGoalStatusTool>>();
    private readonly UpdateGoalStatusTool _tool;
    private static readonly Guid UserId = Guid.NewGuid();

    public UpdateGoalStatusToolTests()
    {
        _tool = new UpdateGoalStatusTool(_goalRepository, _gamificationService, _unitOfWork, _logger);
    }

    [Fact]
    public async Task ExecuteAsync_MarksGoalCompleted_AndProcessesGamification()
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(UserId, "Run a marathon", 42, "km")).Value;
        _goalRepository.FindOneTrackedAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<Goal, bool>>>(),
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Goal?>(goal));

        var result = await Execute($$"""{"goal_id":"{{goal.Id}}","status":"Completed"}""");

        result.Success.Should().BeTrue();
        goal.Status.Should().Be(GoalStatus.Completed);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _gamificationService.Received(1).ProcessGoalCompleted(UserId, Arg.Any<CancellationToken>());
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
