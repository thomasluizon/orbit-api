using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

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

    [Fact]
    public async Task WrongUser_CannotChangeAnothersGoalStatus_OwnerCan_RealContext()
    {
        var databaseName = $"UpdateGoalStatusIsolation_{Guid.NewGuid()}";
        Guid goalId;
        await using (var seed = CreateContext(databaseName))
        {
            var ownerGoal = Goal.Create(new Goal.CreateGoalParams(UserId, "Owner-only goal", 42, "km")).Value;
            seed.Goals.Add(ownerGoal);
            await seed.SaveChangesAsync();
            goalId = ownerGoal.Id;
        }

        await using var context = CreateContext(databaseName);
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo => context.SaveChangesAsync(callInfo.Arg<CancellationToken>()));
        var tool = new UpdateGoalStatusTool(
            new GenericRepository<Goal>(context), _gamificationService, unitOfWork, _logger);

        var attackerId = Guid.NewGuid();
        var attackerResult = await tool.ExecuteAsync(StatusArgs(goalId, "Abandoned"), attackerId, CancellationToken.None);

        attackerResult.Success.Should().BeFalse();
        attackerResult.Error.Should().Contain("not found");
        await using (var afterAttack = CreateContext(databaseName))
            (await afterAttack.Goals.SingleAsync(g => g.Id == goalId)).Status
                .Should().Be(GoalStatus.Active, "a foreign user must not change another user's goal status");

        var ownerResult = await tool.ExecuteAsync(StatusArgs(goalId, "Abandoned"), UserId, CancellationToken.None);

        ownerResult.Success.Should().BeTrue("the owner can change their own goal status");
        await using (var afterOwner = CreateContext(databaseName))
            (await afterOwner.Goals.SingleAsync(g => g.Id == goalId)).Status.Should().Be(GoalStatus.Abandoned);
    }

    private static JsonElement StatusArgs(Guid goalId, string status) =>
        JsonDocument.Parse($$"""{"goal_id":"{{goalId}}","status":"{{status}}"}""").RootElement;

    private static OrbitDbContext CreateContext(string databaseName) =>
        new(new DbContextOptionsBuilder<OrbitDbContext>().UseInMemoryDatabase(databaseName).Options);

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
