using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Domain.Tests.Entities;

public sealed class PendingOperationConversationTests
{
    [Fact]
    public void EmptyIdentity_IsRejected() =>
        FluentActions.Invoking(() => new PendingOperationConversation(Guid.Empty, DateTime.UtcNow))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void NonUtcBatchTimestamp_IsRejected(DateTimeKind kind) =>
        FluentActions.Invoking(() => new PendingOperationConversation(Guid.NewGuid(), DateTime.SpecifyKind(new(2026, 11, 1), kind)))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void DefaultTimestamp_IsRejected() =>
        FluentActions.Invoking(() => new PendingOperationConversation(Guid.NewGuid(), default))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void CreationAndRevision_KeepConversationAndBatchIdentity()
    {
        var conversation = new PendingOperationConversation(Guid.NewGuid(), DateTime.UtcNow);
        var capability = new AgentCapability("habits.write", "Tasks", "Tasks", "habits", "write",
            AgentRiskClass.Low, IsMutation: true, IsPhaseOneReadOnly: false, AgentConfirmationRequirement.FreshConfirmation);
        const string arguments = """{"title":"Reservation"}""";
        var state = PendingAgentOperationState.Create(new()
        {
            UserId = Guid.NewGuid(),
            Capability = capability,
            OperationId = "create_habit",
            ArgumentsJson = arguments,
            Summary = "Reservation",
            OperationFingerprint = AgentOperationFingerprint.Compute("create_habit", arguments),
            Surface = AgentExecutionSurface.Chat,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
            Conversation = conversation
        });
        state.Revise(arguments, state.OperationFingerprint, "preview", DateTime.UtcNow).Should().BeTrue();
        state.ConversationId.Should().Be(conversation.Id);
        state.CreatedAtUtc.Should().Be(conversation.StartedAtUtc);
    }
}
