using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

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

    [Fact]
    public async Task WrongUser_CannotDeleteAnothersGoal_OwnerCan_RealContext()
    {
        var databaseName = $"DeleteGoalIsolation_{Guid.NewGuid()}";
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
        var tool = new DeleteGoalTool(new GenericRepository<Goal>(context), unitOfWork);

        var attackerId = Guid.NewGuid();
        var attackerResult = await tool.ExecuteAsync(GoalArgs(goalId), attackerId, CancellationToken.None);

        attackerResult.Success.Should().BeFalse();
        attackerResult.Error.Should().Contain("not found");
        await using (var afterAttack = CreateContext(databaseName))
            (await afterAttack.Goals.AnyAsync(g => g.Id == goalId))
                .Should().BeTrue("a foreign user must not delete another user's goal");

        var ownerResult = await tool.ExecuteAsync(GoalArgs(goalId), UserId, CancellationToken.None);

        ownerResult.Success.Should().BeTrue("the owner can delete their own goal");
        await using (var afterOwner = CreateContext(databaseName))
            (await afterOwner.Goals.AnyAsync(g => g.Id == goalId))
                .Should().BeFalse("the owner's delete soft-deletes the goal so it no longer resolves");
    }

    private static JsonElement GoalArgs(Guid goalId) =>
        JsonDocument.Parse($$"""{"goal_id":"{{goalId}}"}""").RootElement;

    private static OrbitDbContext CreateContext(string databaseName) =>
        new(new DbContextOptionsBuilder<OrbitDbContext>().UseInMemoryDatabase(databaseName).Options);

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
