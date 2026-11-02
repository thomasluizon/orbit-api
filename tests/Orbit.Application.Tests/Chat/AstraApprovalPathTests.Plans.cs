using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Application.Tests.Chat;

public sealed partial class AstraApprovalPathTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plan_ApprovalReportsActualExecutionOrStalePreviewFailure(bool stale)
    {
        var linked = AddHabit("Read");
        var unit = new UnitOfWork(_db, new DatabaseConnectionSettings());
        var tool = new CreateGoalTool(Orbit.Tests.Shared.GoalToolCommandMediator.Create(new GenericRepository<Goal>(_db), unit,
            habits: new GenericRepository<Habit>(_db), configureTransactions: false));
        var plans = Substitute.For<IConversationPlanStore>();
        var (executor, store, _) = Build(tool, plans: plans);
        var arguments = JsonSerializer.SerializeToElement(new { title = "Read 12 books", target_value = 12, unit = "books", habit_ids = new[] { linked.Id } });
        var held = await executor.ExecuteAsync(new(_userId, tool.Name, arguments, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, IncludeChangePreview: true));
        var id = held.PendingOperation!.Id;
        var pending = store.GetExecution(_userId, id)!;
        var confirmed = store.Confirm(_userId, id)!;
        if (stale)
        {
            linked.PostponeTo(_today.AddDays(1));
            await _db.SaveChangesAsync();
        }
        var status = stale ? "failed" : "done";
        var plan = AstraPlan.Create("Reading", [new("1", "Prepare a preview", "done", tool.Name, "execution"),
            new("2", "Save your goal", status, tool.Name, "approval", PendingOperationId: id)]).Value;
        plans.ChangeAsync(_userId, id, status, Arg.Any<CancellationToken>()).Returns(plan);
        var result = await executor.ExecuteAsync(new(_userId, tool.Name, pending.Arguments, AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt, ConfirmationToken: confirmed.ConfirmationToken));
        result.Operation.Status.Should().Be(stale ? AgentOperationStatus.Denied : AgentOperationStatus.Succeeded);
        result.Plan.Should().Be(plan);
        await plans.Received(1).ChangeAsync(_userId, id, status, Arg.Any<CancellationToken>());
        _db.Goals.Count().Should().Be(stale ? 0 : 1);
    }
}
