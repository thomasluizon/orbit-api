using System.Text.Json;
using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Domain.Tests.Entities;

public sealed class PendingOperationExecutionTests
{
    private const string Execution = """{"operationId":"create_habit","status":"Succeeded","targetId":"habit-1"}""";
    private const string Arguments = """{"title":"Reservation"}""";

    [Fact]
    public void RecordExecution_ConfirmedOperation_KeepsTheResult()
    {
        var state = Confirmed();

        state.RecordExecution(Execution).Should().BeTrue();

        state.ExecutionJson.Should().Be(Execution);
    }

    [Fact]
    public void RecordExecution_UnconfirmedOperation_IsRejected()
    {
        var state = Proposal();

        state.RecordExecution(Execution).Should().BeFalse();

        state.ExecutionJson.Should().BeNull();
    }

    [Fact]
    public void RecordExecution_RejectedOperation_IsRejected()
    {
        var state = Confirmed();
        state.CancelForAccountCleanup(state.UserId, DateTime.UtcNow).Should().BeTrue();

        state.RecordExecution(Execution).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("\"done\"")]
    [InlineData("{")]
    public void RecordExecution_NonObjectJson_IsRejected(string json) =>
        Confirmed().RecordExecution(json).Should().BeFalse();

    [Fact]
    public void RecordExecution_OverBudget_IsRejected()
    {
        var json = JsonSerializer.Serialize(new { summary = new string('a', PendingAgentOperationState.MaxExecutionJsonLength) });

        Confirmed().RecordExecution(json).Should().BeFalse();
    }

    [Fact]
    public void Revise_ClearsThePreviousExecution()
    {
        var state = Confirmed();
        state.RecordExecution(Execution).Should().BeTrue();

        state.Revise(Arguments, state.OperationFingerprint, "edited-preview", DateTime.UtcNow).Should().BeTrue();

        state.ExecutionJson.Should().BeNull();
    }

    [Fact]
    public void PreviewOutcome_StoredWithoutExecution_LoadsAndWritesTheSameJson()
    {
        var id = Guid.NewGuid();
        var stored = $$"""[{"PendingOperationId":"{{id}}","Outcome":"approved","Arguments":{"title":"Read"},"Revision":2}]""";

        var outcomes = JsonSerializer.Deserialize<ConversationPreviewOutcome[]>(stored)!;

        outcomes.Single().Execution.Should().BeNull();
        JsonSerializer.Serialize(outcomes).Should().Be(stored);
    }

    [Fact]
    public void PreviewOutcome_WithExecution_WritesItAsAnAppendedField()
    {
        var outcome = new ConversationPreviewOutcome(Guid.NewGuid(), "approved", JsonSerializer.SerializeToElement(new { title = "Read" }),
            2, JsonSerializer.Deserialize<JsonElement>(Execution));

        var json = JsonSerializer.SerializeToElement(outcome);

        json.EnumerateObject().Select(property => property.Name).Should()
            .Equal("PendingOperationId", "Outcome", "Arguments", "Revision", "Execution");
        json.GetProperty("Execution").GetProperty("targetId").GetString().Should().Be("habit-1");
    }

    private static PendingAgentOperationState Confirmed()
    {
        var state = Proposal();
        state.SetConfirmationTokenHash("hash");
        return state;
    }

    private static PendingAgentOperationState Proposal() => PendingAgentOperationState.Create(new()
    {
        UserId = Guid.NewGuid(),
        Capability = new AgentCapability("habits.write", "Tasks", "Tasks", "habits", "write",
            AgentRiskClass.Low, IsMutation: true, IsPhaseOneReadOnly: false, AgentConfirmationRequirement.FreshConfirmation),
        OperationId = "create_habit",
        ArgumentsJson = Arguments,
        Summary = "Reservation",
        OperationFingerprint = AgentOperationFingerprint.Compute("create_habit", Arguments),
        Surface = AgentExecutionSurface.Chat,
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
    });
}
