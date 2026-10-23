using FluentAssertions;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public sealed class CompositionOutcomeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Accumulator_ReportsRepeatedPendingOutcomeOnceAndKeepsDifferentOutcomes(bool hasPreview)
    {
        var results = new ProcessUserChatCommandHandler.ToolExecutionAccumulator();
        var pendingId = Guid.NewGuid();
        var pending = new PendingAgentOperation(pendingId, "habits.write", "Create", "Create Reading",
            AgentRiskClass.High, AgentConfirmationRequirement.FreshConfirmation, DateTime.UtcNow.AddMinutes(5));
        var operation = new AgentOperationResult("create_habit", "create_habit", AgentRiskClass.High,
            AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.PendingConfirmation,
            PendingOperationId: hasPreview ? null : pendingId);
        var action = new ActionResult("CreateHabit", ActionStatus.Suggestion, EntityName: "Reading");

        results.Add("create_habit", action, operation, null, hasPreview ? pending : null);
        results.Add("create_habit", action, operation, null, hasPreview ? pending : null);
        results.OperationResults.Should().Equal(operation);
        results.ActionResults.Should().Equal(action);
        results.PendingOperations.Should().HaveCount(hasPreview ? 1 : 0);

        var confirmed = operation with { Status = AgentOperationStatus.Succeeded, PendingOperationId = pendingId };
        var committed = action with { Status = ActionStatus.Success };
        results.Add("confirm_operation", committed, confirmed, null, null);
        results.Add("confirm_operation", committed, confirmed, null, null);
        results.OperationResults.Should().Equal(operation, confirmed);
        results.ActionResults.Should().Equal(action, committed);
        results.CalledToolNames.Should().BeEquivalentTo("create_habit", "confirm_operation");
    }
}
