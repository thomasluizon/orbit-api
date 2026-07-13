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

    [Fact]
    public async Task WrongUser_CannotUpdateAnothersGoal_OwnerCan_RealContext()
    {
        var databaseName = $"UpdateGoalIsolation_{Guid.NewGuid()}";
        Guid goalId;
        await using (var seed = CreateContext(databaseName))
        {
            var ownerGoal = Goal.Create(new Goal.CreateGoalParams(UserId, "Original goal", 10, "books")).Value;
            seed.Goals.Add(ownerGoal);
            await seed.SaveChangesAsync();
            goalId = ownerGoal.Id;
        }

        await using var context = CreateContext(databaseName);
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo => context.SaveChangesAsync(callInfo.Arg<CancellationToken>()));
        var tool = new UpdateGoalTool(new GenericRepository<Goal>(context), unitOfWork);

        var attackerId = Guid.NewGuid();
        var attackerResult = await tool.ExecuteAsync(RenameArgs(goalId, "Hijacked"), attackerId, CancellationToken.None);

        attackerResult.Success.Should().BeFalse();
        attackerResult.Error.Should().Contain("not found");
        await using (var afterAttack = CreateContext(databaseName))
            (await afterAttack.Goals.SingleAsync(g => g.Id == goalId)).Title
                .Should().Be("Original goal", "a foreign user must not rename another user's goal");

        var ownerResult = await tool.ExecuteAsync(RenameArgs(goalId, "Owner rename"), UserId, CancellationToken.None);

        ownerResult.Success.Should().BeTrue("the owner can update their own goal");
        await using (var afterOwner = CreateContext(databaseName))
            (await afterOwner.Goals.SingleAsync(g => g.Id == goalId)).Title.Should().Be("Owner rename");
    }

    private static JsonElement RenameArgs(Guid goalId, string title) =>
        JsonDocument.Parse($$"""{"goal_id":"{{goalId}}","title":"{{title}}"}""").RootElement;

    private static OrbitDbContext CreateContext(string databaseName) =>
        new(new DbContextOptionsBuilder<OrbitDbContext>().UseInMemoryDatabase(databaseName).Options);

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
