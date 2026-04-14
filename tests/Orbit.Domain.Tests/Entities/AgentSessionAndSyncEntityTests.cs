using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Domain.Tests.Entities;

public class AgentSessionAndSyncEntityTests
{
    [Fact]
    public void AgentStepUpChallengeState_CreateAndVerifyLifecycle_Works()
    {
        var userId = Guid.NewGuid();
        var pendingOperationId = Guid.NewGuid();
        var expiresAtUtc = DateTime.UtcNow.AddMinutes(10);

        var state = AgentStepUpChallengeState.Create(userId, pendingOperationId, "hash", expiresAtUtc);

        state.UserId.Should().Be(userId);
        state.PendingOperationId.Should().Be(pendingOperationId);
        state.CodeHash.Should().Be("hash");
        state.AttemptCount.Should().Be(0);
        state.IsExpired(expiresAtUtc.AddMinutes(-1)).Should().BeFalse();
        state.CanVerify(3, expiresAtUtc.AddMinutes(-1)).Should().BeTrue();

        state.RecordFailedAttempt();
        state.AttemptCount.Should().Be(1);

        state.MarkVerified();
        state.VerifiedAtUtc.Should().NotBeNull();
        state.CanVerify(3, expiresAtUtc.AddMinutes(-1)).Should().BeFalse();
        state.IsExpired(expiresAtUtc.AddMinutes(1)).Should().BeTrue();
    }

    [Fact]
    public void UserSession_CreateRotateRevokeAndValidation_Works()
    {
        var userId = Guid.NewGuid();
        var expiresAtUtc = DateTime.UtcNow.AddDays(7);

        UserSession.Create(Guid.Empty, "hash", expiresAtUtc).IsFailure.Should().BeTrue();
        UserSession.Create(userId, "", expiresAtUtc).IsFailure.Should().BeTrue();

        var session = UserSession.Create(userId, "hash", expiresAtUtc).Value;

        session.UserId.Should().Be(userId);
        session.TokenHash.Should().Be("hash");
        session.CanUse(expiresAtUtc.AddMinutes(-1)).Should().BeTrue();
        session.CanUse(expiresAtUtc.AddMinutes(1)).Should().BeFalse();

        var rotatedExpiry = expiresAtUtc.AddDays(1);
        var usedAtUtc = DateTime.UtcNow.AddHours(1);
        session.Rotate("new-hash", rotatedExpiry, usedAtUtc);

        session.TokenHash.Should().Be("new-hash");
        session.ExpiresAtUtc.Should().Be(rotatedExpiry);
        session.LastUsedAtUtc.Should().Be(usedAtUtc);

        var revokedAtUtc = DateTime.UtcNow.AddHours(2);
        session.Revoke(revokedAtUtc);
        session.RevokedAtUtc.Should().Be(revokedAtUtc);
        session.CanUse(revokedAtUtc.AddMinutes(-1)).Should().BeFalse();

        session.Revoke(revokedAtUtc.AddMinutes(5));
        session.RevokedAtUtc.Should().Be(revokedAtUtc);
    }

    [Fact]
    public void GoogleCalendarSyncSuggestion_CreateDismissAndImport_Works()
    {
        var userId = Guid.NewGuid();
        var suggestion = GoogleCalendarSyncSuggestion.Create(
            userId,
            "event-123",
            "Team Sync",
            new DateTime(2026, 4, 14, 12, 0, 0, DateTimeKind.Utc),
            """{"id":"event-123"}""",
            new DateTime(2026, 4, 13, 8, 0, 0, DateTimeKind.Utc));

        suggestion.UserId.Should().Be(userId);
        suggestion.GoogleEventId.Should().Be("event-123");
        suggestion.Title.Should().Be("Team Sync");
        suggestion.RawEventJson.Should().Contain("event-123");

        suggestion.MarkDismissed(new DateTime(2026, 4, 14, 13, 0, 0, DateTimeKind.Utc));
        suggestion.DismissedAtUtc.Should().Be(new DateTime(2026, 4, 14, 13, 0, 0, DateTimeKind.Utc));

        var habitId = Guid.NewGuid();
        suggestion.MarkImported(habitId, new DateTime(2026, 4, 14, 14, 0, 0, DateTimeKind.Utc));
        suggestion.ImportedHabitId.Should().Be(habitId);
        suggestion.ImportedAtUtc.Should().Be(new DateTime(2026, 4, 14, 14, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void DistributedRateLimitBucket_CreateAndIncrement_Works()
    {
        var windowStartUtc = DateTime.UtcNow;
        var windowEndsAtUtc = windowStartUtc.AddMinutes(1);

        var bucket = DistributedRateLimitBucket.Create("auth", "user:1", windowStartUtc, windowEndsAtUtc);

        bucket.PolicyName.Should().Be("auth");
        bucket.PartitionKey.Should().Be("user:1");
        bucket.WindowStartUtc.Should().Be(windowStartUtc);
        bucket.WindowEndsAtUtc.Should().Be(windowEndsAtUtc);
        bucket.Count.Should().Be(1);

        var previousUpdatedAtUtc = bucket.UpdatedAtUtc;
        bucket.Increment();

        bucket.Count.Should().Be(2);
        bucket.UpdatedAtUtc.Should().BeOnOrAfter(previousUpdatedAtUtc);
    }

    [Fact]
    public void AgentContracts_RecordsAndCatalogConstants_AreAccessible()
    {
        var requestSchema = Parse("""{"type":"object"}""");
        var responseSchema = Parse("""{"type":"object"}""");

        var capability = new AgentCapability(
            AgentCapabilityIds.HabitsWrite,
            "Habits Write",
            "Write habits",
            "habits",
            AgentScopes.WriteHabits,
            AgentRiskClass.Low,
            true,
            false,
            AgentConfirmationRequirement.FreshConfirmation,
            ChatToolNames: ["create_habit"]);
        var operation = new AgentOperation(
            "create_habit",
            "Create habit",
            "Create a habit",
            capability.Id,
            AgentRiskClass.Low,
            AgentConfirmationRequirement.FreshConfirmation,
            true,
            true,
            requestSchema,
            responseSchema);
        var surface = new AppSurface("today", "Today", "Today surface", ["Open Today"], ["Pinned"], [capability.Id], ["HabitsController.Get"]);
        var field = new UserDataFieldDescriptor("title", "Habit title", true, true);
        var catalog = new UserDataCatalogEntry("habits", "Habits", "Habit catalog", "medium", "keep", true, true, [field]);
        var pendingOperation = new PendingAgentOperation(Guid.NewGuid(), capability.Id, "Create habit", "Create a new habit", AgentRiskClass.High, AgentConfirmationRequirement.StepUp, DateTime.UtcNow.AddMinutes(10));
        var confirmation = new PendingAgentOperationConfirmation(pendingOperation.Id, "agc_token", DateTime.UtcNow.AddMinutes(5));
        var execution = new PendingAgentOperationExecution(pendingOperation.Id, capability.Id, operation.Id, requestSchema, AgentExecutionSurface.Chat, AgentConfirmationRequirement.StepUp);
        var challenge = new AgentStepUpChallenge(Guid.NewGuid(), pendingOperation.Id, DateTime.UtcNow.AddMinutes(10));
        var evaluationContext = new AgentPolicyEvaluationContext(capability.Id, Guid.NewGuid(), AgentExecutionSurface.Mcp, AgentAuthMethod.Jwt, [AgentScopes.WriteHabits], "execute_agent_operation_v2", "Create habit");
        var policyDecision = new AgentPolicyDecision(AgentPolicyDecisionStatus.ConfirmationRequired, capability, PendingOperation: pendingOperation);
        var executeRequest = new AgentExecuteOperationRequest(Guid.NewGuid(), operation.Id, requestSchema, AgentExecutionSurface.Mcp, AgentAuthMethod.Jwt, [AgentScopes.WriteHabits], ConfirmationToken: confirmation.ConfirmationToken);
        var operationResult = new AgentOperationResult(operation.Id, "execute_agent_operation_v2", AgentRiskClass.High, AgentConfirmationRequirement.StepUp, AgentOperationStatus.PendingConfirmation, PendingOperationId: pendingOperation.Id, Payload: new { habitId = Guid.NewGuid() });
        var denial = new AgentPolicyDenial(operation.Id, "execute_agent_operation_v2", AgentRiskClass.High, AgentConfirmationRequirement.StepUp, "denied");
        var executeResponse = new AgentExecuteOperationResponse(operationResult, pendingOperation, denial);
        var clientContext = new AgentClientContext("ios", "en-US", "24h", "today", true);
        var snapshot = new AgentContextSnapshot("pro", "en-US", "UTC", true, true, 1, "dark", "sunset", true, true, "Idle", ["beta"], ["Health"], ["Morning"], ["Run"], ["Lose weight"], clientContext);
        var auditEntry = new AgentAuditEntry(Guid.NewGuid(), capability.Id, "execute_agent_operation_v2", AgentExecutionSurface.Mcp, AgentAuthMethod.Jwt, AgentRiskClass.High, AgentPolicyDecisionStatus.Allowed, AgentOperationStatus.Succeeded, Summary: "Created habit");

        capability.ChatToolNames.Should().Contain("create_habit");
        operation.RequestSchema.GetProperty("type").GetString().Should().Be("object");
        surface.HowToSteps.Should().ContainSingle();
        catalog.Fields.Should().ContainSingle().Which.Name.Should().Be("title");
        pendingOperation.ConfirmationRequirement.Should().Be(AgentConfirmationRequirement.StepUp);
        confirmation.ConfirmationToken.Should().Be("agc_token");
        execution.Surface.Should().Be(AgentExecutionSurface.Chat);
        challenge.PendingOperationId.Should().Be(pendingOperation.Id);
        evaluationContext.GrantedScopes.Should().Contain(AgentScopes.WriteHabits);
        policyDecision.PendingOperation.Should().Be(pendingOperation);
        executeRequest.ConfirmationToken.Should().Be("agc_token");
        operationResult.PendingOperationId.Should().Be(pendingOperation.Id);
        executeResponse.PolicyDenial.Should().Be(denial);
        clientContext.Platform.Should().Be("ios");
        snapshot.ClientContext.Should().Be(clientContext);
        auditEntry.Summary.Should().Be("Created habit");

        AgentScopes.ClaudeDefaultScopes.Should().Contain(AgentScopes.ChatInteract);
        AgentScopes.ClaudeDefaultScopes.Should().OnlyHaveUniqueItems();

        var capabilityIds = typeof(AgentCapabilityIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(fieldInfo => fieldInfo.GetValue(null))
            .OfType<string>()
            .ToList();
        var scopes = typeof(AgentScopes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(fieldInfo => fieldInfo.FieldType == typeof(string))
            .Select(fieldInfo => fieldInfo.GetValue(null))
            .OfType<string>()
            .ToList();

        capabilityIds.Should().OnlyContain(value => !string.IsNullOrWhiteSpace(value));
        capabilityIds.Should().OnlyHaveUniqueItems();
        scopes.Should().OnlyContain(value => !string.IsNullOrWhiteSpace(value));
        scopes.Should().OnlyHaveUniqueItems();
    }

    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
