using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Domain.Tests.Entities;

public class AgentStateAndAuditTests
{
    [Fact]
    public void PendingAgentOperationState_Create_UsesDefaultsForBlankArguments()
    {
        var capability = CreateCapability(AgentCapabilityIds.HabitsDelete, AgentRiskClass.Destructive, AgentConfirmationRequirement.FreshConfirmation);

        var state = PendingAgentOperationState.Create(new PendingAgentOperationStateCreateRequest
        {
            UserId = Guid.NewGuid(),
            Capability = capability,
            OperationId = "delete_habit",
            ArgumentsJson = " ",
            Summary = "Delete habit",
            OperationFingerprint = "delete_habit:{}",
            Surface = AgentExecutionSurface.Chat,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)
        });

        state.ArgumentsJson.Should().Be("{}");
        state.DisplayName.Should().Be(capability.DisplayName);
        state.RiskClass.Should().Be(AgentRiskClass.Destructive);
    }

    [Fact]
    public void PendingAgentOperationState_IsUsable_RequiresConfirmation()
    {
        var state = CreateState();

        state.IsUsable(state.CapabilityId, state.OperationFingerprint, requireStepUp: false, DateTime.UtcNow)
            .Should().BeFalse();
    }

    [Fact]
    public void PendingAgentOperationState_IsUsable_RequiresStepUpWhenRequested()
    {
        var state = CreateState();
        state.SetConfirmationTokenHash("hash");

        state.IsUsable(state.CapabilityId, state.OperationFingerprint, requireStepUp: true, DateTime.UtcNow)
            .Should().BeFalse();
    }

    [Fact]
    public void PendingAgentOperationState_IsUsable_ReturnsTrueAfterConfirmationAndStepUp()
    {
        var state = CreateState();
        state.SetConfirmationTokenHash("hash");
        state.MarkStepUpSatisfied();

        state.IsUsable(state.CapabilityId, state.OperationFingerprint, requireStepUp: true, DateTime.UtcNow)
            .Should().BeTrue();
    }

    [Fact]
    public void PendingAgentOperationState_IsUsable_ReturnsFalseAfterConsumed()
    {
        var state = CreateState();
        state.SetConfirmationTokenHash("hash");
        state.MarkConsumed();

        state.IsUsable(state.CapabilityId, state.OperationFingerprint, requireStepUp: false, DateTime.UtcNow)
            .Should().BeFalse();
    }

    [Fact]
    public void AgentAuditLog_Create_TruncatesLongFields()
    {
        var entry = new AgentAuditEntry(
            Guid.NewGuid(),
            new string('c', 150),
            new string('s', 150),
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt,
            AgentRiskClass.High,
            AgentPolicyDecisionStatus.Denied,
            AgentOperationStatus.Failed,
            new string('i', 150),
            new string('u', 600),
            new string('t', 150),
            new string('n', 250),
            new string('a', 5000),
            new string('e', 600),
            AgentPolicyDecisionStatus.Allowed,
            new string('r', 600));

        var log = AgentAuditLog.Create(entry);

        log.CapabilityId.Length.Should().Be(100);
        log.SourceName.Length.Should().Be(100);
        log.CorrelationId!.Length.Should().Be(100);
        log.Summary!.Length.Should().Be(500);
        log.TargetId!.Length.Should().Be(100);
        log.TargetName!.Length.Should().Be(200);
        log.RedactedArguments!.Length.Should().Be(4000);
        log.Error!.Length.Should().Be(500);
        log.ShadowReason!.Length.Should().Be(500);
    }

    [Fact]
    public void AgentAuditLog_Create_PreservesWhitespaceOnlyValues()
    {
        var entry = new AgentAuditEntry(
            Guid.NewGuid(),
            "capability",
            "source",
            AgentExecutionSurface.Chat,
            AgentAuthMethod.ApiKey,
            AgentRiskClass.Low,
            AgentPolicyDecisionStatus.Allowed,
            AgentOperationStatus.Succeeded,
            Summary: " ");

        var log = AgentAuditLog.Create(entry);

        log.Summary.Should().Be(" ");
        log.PolicyDecision.Should().Be(AgentPolicyDecisionStatus.Allowed);
        log.OutcomeStatus.Should().Be(AgentOperationStatus.Succeeded);
    }

    private static PendingAgentOperationState CreateState()
    {
        return PendingAgentOperationState.Create(new PendingAgentOperationStateCreateRequest
        {
            UserId = Guid.NewGuid(),
            Capability = CreateCapability(AgentCapabilityIds.ApiKeysManage, AgentRiskClass.High, AgentConfirmationRequirement.StepUp),
            OperationId = "create_api_key",
            ArgumentsJson = """{"name":"Claude"}""",
            Summary = "Create key",
            OperationFingerprint = "create_api_key:{\"name\":\"Claude\"}",
            Surface = AgentExecutionSurface.Chat,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)
        });
    }

    private static AgentCapability CreateCapability(
        string id,
        AgentRiskClass riskClass,
        AgentConfirmationRequirement confirmationRequirement)
    {
        return new AgentCapability(
            id,
            id,
            id,
            "test",
            "scope",
            riskClass,
            true,
            false,
            confirmationRequirement);
    }
}
