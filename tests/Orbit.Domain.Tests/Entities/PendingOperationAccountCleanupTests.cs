using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Domain.Tests.Entities;

public class PendingOperationAccountCleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cleanup_CancelsExpiredAndLiveApprovalsWithoutMakingThemUsable(bool expired)
    {
        var now = TimeProvider.System.GetUtcNow().UtcDateTime;
        var operation = Create(now.AddMinutes(expired ? -10 : 10));
        operation.SetConfirmationTokenHash("hash");
        operation.MarkStepUpSatisfied();
        var revision = operation.RevisionNumber;

        operation.CancelForAccountCleanup(Guid.NewGuid(), now).Should().BeFalse();
        operation.CancelForAccountCleanup(Guid.Empty, now).Should().BeFalse();
        operation.ConsumedAtUtc.Should().BeNull();
        operation.ConfirmationTokenHash.Should().Be("hash");
        operation.CancelForAccountCleanup(operation.UserId, now).Should().BeTrue();

        operation.ConsumedAtUtc.Should().Be(now);
        operation.ConfirmationTokenHash.Should().BeNull();
        operation.RevisionNumber.Should().Be(revision + 1);
        operation.IsUsable(operation.CapabilityId, operation.OperationFingerprint, true, now).Should().BeFalse();
        operation.CancelForAccountCleanup(operation.UserId, now.AddMinutes(1)).Should().BeFalse();
        operation.ConsumedAtUtc.Should().Be(now);
        operation.RevisionNumber.Should().Be(revision + 1);
    }

    [Fact]
    public void OrdinaryCancellation_StillRejectsExpiredOperations()
    {
        var now = TimeProvider.System.GetUtcNow().UtcDateTime;
        var operation = Create(now.AddMinutes(-1));
        operation.Cancel(now).Should().BeFalse();
        operation.ConsumedAtUtc.Should().BeNull();
    }

    private static PendingAgentOperationState Create(DateTime expiresAtUtc) => PendingAgentOperationState.Create(new()
    {
        UserId = Guid.NewGuid(),
        Capability = new AgentCapability("test", "Test", "Test", "test", "test", AgentRiskClass.High,
            true, false, AgentConfirmationRequirement.StepUp),
        OperationId = "test",
        ArgumentsJson = "{}",
        Summary = "Preview",
        OperationFingerprint = "fingerprint",
        Surface = AgentExecutionSurface.Chat,
        ExpiresAtUtc = expiresAtUtc
    });
}
