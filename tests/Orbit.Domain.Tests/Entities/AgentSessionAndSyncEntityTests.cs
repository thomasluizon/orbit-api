using System.Reflection;
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
    public void CanVerify_FalseAfterAttemptLimitOrAtExpiry()
    {
        var expiry = DateTime.UtcNow.AddMinutes(5);
        var state = AgentStepUpChallengeState.Create(Guid.NewGuid(), Guid.NewGuid(), "hash", expiry);
        state.CanVerify(3, expiry.AddTicks(-1)).Should().BeTrue();
        state.CanVerify(3, expiry).Should().BeFalse();
        state.CanVerify(3, expiry.AddTicks(1)).Should().BeFalse();
        for (var i = 0; i < 3; i++) state.RecordFailedAttempt();
        state.CanVerify(3, expiry.AddTicks(-1)).Should().BeFalse();
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
    public void AgentScopeAndCapabilityConstants_AreUniqueAndNonBlank()
    {
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
        AgentScopes.All.Should().BeEquivalentTo(scopes);
    }

}
