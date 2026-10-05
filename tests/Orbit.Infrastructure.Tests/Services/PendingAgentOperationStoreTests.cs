using FluentAssertions;
using System.Text.Json;
using Orbit.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class PendingAgentOperationStoreTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> GatedOperationKeys = new Dictionary<string, string>
    {
        ["delete_habit"] = "deleteHabit",
        ["bulk_delete_habits"] = "deleteHabits",
        ["delete_goal"] = "deleteGoal",
        ["delete_tag"] = "deleteTag",
        ["delete_notifications"] = "deleteNotifications",
        ["manage_calendar_sync"] = "manageCalendarSync",
        ["delete_user_facts"] = "deleteUserFacts",
        ["manage_subscription"] = "manageSubscription",
        ["get_api_keys"] = "viewApiKeys",
        ["manage_api_keys"] = "manageApiKeys",
        ["manage_account"] = "manageAccount"
    };

    private readonly OrbitDbContext _dbContext;
    private readonly PendingAgentOperationStore _store;
    private readonly AgentCatalogService _catalogService = new();
    private readonly Guid _userId;

    public PendingAgentOperationStoreTests()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"PendingAgentOperationStoreTests_{Guid.NewGuid()}")
            .Options;

        _dbContext = new OrbitDbContext(options);
        var user = User.Create("Alex", "thomas@test.com").Value;
        _userId = user.Id;
        _dbContext.Users.Add(user);
        _dbContext.SaveChanges();

        _store = new PendingAgentOperationStore(
            _dbContext,
            Options.Create(new AgentPlatformSettings()));
    }

    [Theory]
    [InlineData("bulk_update_habits", "updateHabits")]
    [InlineData("bulk_reschedule_habits", "rescheduleHabits")]
    [InlineData("bulk_log_habits", "logHabits")]
    [InlineData("bulk_skip_habits", "skipHabits")]
    [InlineData("bulk_create_habits", "createHabits")]
    public void Create_ChatHeldBulkOperationPreservesItsActionKey(string operationId, string expectedKey)
    {
        var declared = _catalogService.GetCapabilityByChatTool(operationId)!;
        declared.RiskClass.Should().Be(AgentRiskClass.Low);
        declared.ConfirmationRequirement.Should().Be(AgentConfirmationRequirement.None);
        var held = declared with { ConfirmationRequirement = AgentConfirmationRequirement.FreshConfirmation };

        var pending = _store.Create(_userId, held, operationId, "{}", operationId,
            $"{operationId}:{Guid.NewGuid()}", AgentExecutionSurface.Chat);

        pending.ActionKey.Should().Be(expectedKey);
        pending.RiskClass.Should().Be(AgentRiskClass.Low);
        pending.ConfirmationRequirement.Should().Be(AgentConfirmationRequirement.FreshConfirmation);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Create_EveryConfirmationGatedOperationHasAnActionKey()
    {
        var gated = _catalogService.GetCapabilities()
            .Where(capability => capability.ConfirmationRequirement is
                AgentConfirmationRequirement.FreshConfirmation or AgentConfirmationRequirement.StepUp)
            .SelectMany(capability => capability.ChatToolNames ?? [])
            .Append("get_api_keys")
            .ToHashSet(StringComparer.Ordinal);

        gated.Should().BeEquivalentTo(GatedOperationKeys.Keys);

        foreach (var (operationId, expectedKey) in GatedOperationKeys)
        {
            var capability = _catalogService.GetCapabilityByChatTool(operationId)!;
            var pending = _store.Create(_userId, capability, operationId, "{}", operationId,
                $"{operationId}:{Guid.NewGuid()}", AgentExecutionSurface.Chat);

            pending.ActionKey.Should().Be(expectedKey, operationId);
        }
    }

    [Theory]
    [InlineData("delete_notifications", "delete_one", "deleteNotification")]
    [InlineData("delete_notifications", "delete_all", "deleteAllNotifications")]
    [InlineData("delete_notifications", "delete_selected", "deleteNotifications")]
    [InlineData("manage_calendar_sync", "set_auto_sync", "setCalendarSync")]
    [InlineData("manage_calendar_sync", "dismiss_import", "dismissCalendarImport")]
    [InlineData("manage_calendar_sync", "dismiss_suggestion", "dismissCalendarSuggestion")]
    [InlineData("manage_calendar_sync", "run_sync", "syncCalendar")]
    [InlineData("manage_subscription", "create_checkout", "createCheckout")]
    [InlineData("manage_subscription", "create_portal", "openBillingPortal")]
    [InlineData("manage_api_keys", "create", "createApiKey")]
    [InlineData("manage_api_keys", "revoke", "revokeApiKey")]
    [InlineData("manage_account", "reset_account", "resetAccount")]
    [InlineData("manage_account", "request_deletion", "requestAccountDeletion")]
    [InlineData("manage_account", "confirm_deletion", "confirmAccountDeletion")]
    public void Create_MultiActionOperationUsesItsSelectedConsequence(
        string operationId, string action, string expectedKey)
    {
        var capability = _catalogService.GetCapabilityByChatTool(operationId)!;
        var pending = _store.Create(_userId, capability, operationId,
            JsonSerializer.Serialize(new { action }), operationId, Guid.NewGuid().ToString(),
            AgentExecutionSurface.Chat);

        pending.ActionKey.Should().Be(expectedKey);
    }

    [Fact]
    public void Create_SerializedActionKeyIsAdditiveForAnOlderClient()
    {
        var capability = _catalogService.GetCapability(AgentCapabilityIds.HabitsBulkWrite)!;
        var pending = _store.Create(_userId, capability, "bulk_log_habits", "{}", "Log habits",
            Guid.NewGuid().ToString(), AgentExecutionSurface.Chat);
        var json = JsonSerializer.Serialize(pending, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("actionKey").GetString().Should().Be("logHabits");
        var oldClient = JsonSerializer.Deserialize<OldPendingOperation>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        oldClient.Should().NotBeNull();
        oldClient!.CapabilityId.Should().Be(AgentCapabilityIds.HabitsBulkWrite);
    }

    [Fact]
    public void Create_UnknownOperationFailsBeforeItIsStored()
    {
        var capability = _catalogService.GetCapability(AgentCapabilityIds.HabitsBulkWrite)!;

        var create = () => _store.Create(_userId, capability, "unknown_operation", "{}",
            "Unknown", Guid.NewGuid().ToString(), AgentExecutionSurface.Chat);

        create.Should().Throw<InvalidOperationException>();
        _dbContext.PendingAgentOperations.Should().BeEmpty();
    }

    private sealed record OldPendingOperation(Guid Id, string CapabilityId, string DisplayName,
        string Summary, AgentRiskClass RiskClass, AgentConfirmationRequirement ConfirmationRequirement,
        DateTime ExpiresAtUtc);

    [Fact]
    public void GetExecution_ReturnsStoredOperationPayload()
    {
        var capability = _catalogService.GetCapability(AgentCapabilityIds.HabitsDelete)!;
        var pendingOperation = _store.Create(
            _userId,
            capability,
            "delete_habit",
            "{\"habit_id\":\"habit-123\"}",
            "Delete habit",
            "delete_habit:{\"habit_id\":\"habit-123\"}",
            AgentExecutionSurface.Chat);

        var execution = _store.GetExecution(_userId, pendingOperation.Id);

        execution.Should().NotBeNull();
        execution!.PendingOperationId.Should().Be(pendingOperation.Id);
        execution.CapabilityId.Should().Be(AgentCapabilityIds.HabitsDelete);
        execution.OperationId.Should().Be("delete_habit");
        execution.Surface.Should().Be(AgentExecutionSurface.Chat);
        execution.Arguments.GetProperty("habit_id").GetString().Should().Be("habit-123");
    }

    [Fact]
    public void TryConsumeFreshConfirmation_TwoReadersClaimOneTokenOnce()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        using var secondContext = factory.CreateContext();
        var firstContext = factory.Context;
        var user = User.Create("Alex", $"{Guid.NewGuid():N}@example.com").Value;
        firstContext.Users.Add(user);
        firstContext.SaveChanges();

        var settings = Options.Create(new AgentPlatformSettings());
        var firstStore = new PendingAgentOperationStore(firstContext, settings);
        var secondStore = new PendingAgentOperationStore(secondContext, settings);
        var capability = _catalogService.GetCapability(AgentCapabilityIds.HabitsDelete)!;
        const string fingerprint = "delete_habit:race";
        var pending = firstStore.Create(
            user.Id, capability, "delete_habit", "{}", "Delete habit", fingerprint,
            AgentExecutionSurface.Chat);
        var confirmation = firstStore.Confirm(user.Id, pending.Id)!;

        firstContext.ChangeTracker.Clear();
        firstContext.PendingAgentOperations.Single(item => item.Id == pending.Id);
        secondContext.PendingAgentOperations.Single(item => item.Id == pending.Id);

        var firstClaim = firstStore.TryConsumeFreshConfirmation(
            user.Id, capability.Id, fingerprint, confirmation.ConfirmationToken, false);
        var secondClaim = secondStore.TryConsumeFreshConfirmation(
            user.Id, capability.Id, fingerprint, confirmation.ConfirmationToken, false);

        firstClaim.Should().BeTrue();
        secondClaim.Should().BeFalse();
        secondContext.ChangeTracker.Entries<PendingAgentOperationState>().Should().BeEmpty();
        using var verifyContext = factory.CreateContext();
        verifyContext.PendingAgentOperations.Single(item => item.Id == pending.Id)
            .ConsumedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void Revise_InvalidatesIssuedTokenAndStoresOnlyRevisedArguments()
    {
        var capability = _catalogService.GetCapability(AgentCapabilityIds.HabitsBulkWrite)!;
        const string originalJson = "{\"filter\":{\"all\":true},\"updates\":{\"emoji\":\"A\"}}";
        const string revisedJson = "{\"revised_items\":[]}";
        var originalFingerprint = AgentOperationFingerprint.Compute("bulk_update_habits", originalJson);
        var revisedFingerprint = AgentOperationFingerprint.Compute("bulk_update_habits", revisedJson);
        var pending = _store.Create(_userId, capability, "bulk_update_habits", originalJson,
            "Update habits", originalFingerprint, AgentExecutionSurface.Chat);
        var confirmation = _store.Confirm(_userId, pending.Id)!;

        _store.Revise(_userId, pending.Id, originalFingerprint, revisedJson,
            revisedFingerprint, "preview-state").Should().BeTrue();

        _store.TryConsumeFreshConfirmation(_userId, capability.Id, originalFingerprint,
            confirmation.ConfirmationToken, false).Should().BeFalse();
        var execution = _store.GetExecution(_userId, pending.Id)!;
        execution.Arguments.GetProperty("revised_items").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
        execution.PreviewFingerprint.Should().Be("preview-state");
    }

    [Fact]
    public void Revise_WithSameArgumentsRefreshesPreviewAndInvalidatesApproval()
    {
        var capability = _catalogService.GetCapability(AgentCapabilityIds.HabitsBulkWrite)!;
        const string json = "{\"filter\":{\"all\":true},\"updates\":{\"emoji\":\"A\"}}";
        var fingerprint = AgentOperationFingerprint.Compute("bulk_update_habits", json);
        var pending = _store.Create(_userId, capability, "bulk_update_habits", json,
            "Update habits", fingerprint, AgentExecutionSurface.Chat);
        var confirmation = _store.Confirm(_userId, pending.Id)!;

        _store.Revise(_userId, pending.Id, fingerprint, json, fingerprint, "fresh-preview")
            .Should().BeTrue();

        var execution = _store.GetExecution(_userId, pending.Id)!;
        execution.Arguments.GetRawText().Should().Be(json);
        execution.PreviewFingerprint.Should().Be("fresh-preview");
        _store.TryConsumeFreshConfirmation(_userId, capability.Id, fingerprint,
            confirmation.ConfirmationToken, false).Should().BeFalse();
    }

    [Fact]
    public void Revise_WhenConfirmCommitsAfterUnconfirmedRead_RejectsRefresh()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        using var confirmContext = factory.CreateContext();
        var refreshContext = factory.Context;
        var user = User.Create("Alex", $"{Guid.NewGuid():N}@example.com").Value;
        refreshContext.Users.Add(user);
        refreshContext.SaveChanges();

        var settings = Options.Create(new AgentPlatformSettings());
        var refreshStore = new PendingAgentOperationStore(refreshContext, settings);
        var confirmStore = new PendingAgentOperationStore(confirmContext, settings);
        var capability = _catalogService.GetCapability(AgentCapabilityIds.HabitsBulkWrite)!;
        const string json = "{\"filter\":{\"all\":true},\"updates\":{\"emoji\":\"A\"}}";
        var fingerprint = AgentOperationFingerprint.Compute("bulk_update_habits", json);
        var pending = refreshStore.Create(user.Id, capability, "bulk_update_habits", json,
            "Update habits", fingerprint, AgentExecutionSurface.Chat);

        refreshContext.ChangeTracker.Clear();
        refreshContext.PendingAgentOperations.Single(item => item.Id == pending.Id)
            .ConfirmedAtUtc.Should().BeNull();
        var confirmation = confirmStore.Confirm(user.Id, pending.Id);
        confirmation.Should().NotBeNull();

        refreshStore.Revise(user.Id, pending.Id, fingerprint, json, fingerprint, "fresh-preview")
            .Should().BeFalse();

        using var verifyContext = factory.CreateContext();
        var stored = verifyContext.PendingAgentOperations.Single(item => item.Id == pending.Id);
        stored.PreviewFingerprint.Should().BeNull();
        stored.ConfirmedAtUtc.Should().NotBeNull();
        stored.ConfirmationTokenHash.Should().NotBeNull();
        var verifyStore = new PendingAgentOperationStore(verifyContext, settings);
        verifyStore.TryConsumeFreshConfirmation(user.Id, capability.Id, fingerprint,
            confirmation!.ConfirmationToken, false).Should().BeTrue();
    }

    [Fact]
    public void Confirm_WhenRefreshCommitsAfterUnconfirmedRead_RejectsOldApproval()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        using var confirmContext = factory.CreateContext();
        var refreshContext = factory.Context;
        var user = User.Create("Alex", $"{Guid.NewGuid():N}@example.com").Value;
        refreshContext.Users.Add(user);
        refreshContext.SaveChanges();

        var settings = Options.Create(new AgentPlatformSettings());
        var refreshStore = new PendingAgentOperationStore(refreshContext, settings);
        var confirmStore = new PendingAgentOperationStore(confirmContext, settings);
        var capability = _catalogService.GetCapability(AgentCapabilityIds.HabitsBulkWrite)!;
        const string json = "{\"filter\":{\"all\":true},\"updates\":{\"emoji\":\"A\"}}";
        var fingerprint = AgentOperationFingerprint.Compute("bulk_update_habits", json);
        var pending = refreshStore.Create(user.Id, capability, "bulk_update_habits", json,
            "Update habits", fingerprint, AgentExecutionSurface.Chat);

        confirmContext.PendingAgentOperations.Single(item => item.Id == pending.Id)
            .ConfirmedAtUtc.Should().BeNull();
        refreshContext.ChangeTracker.Clear();
        refreshStore.Revise(user.Id, pending.Id, fingerprint, json, fingerprint, "fresh-preview")
            .Should().BeTrue();

        confirmStore.Confirm(user.Id, pending.Id).Should().BeNull();

        using var verifyContext = factory.CreateContext();
        var stored = verifyContext.PendingAgentOperations.Single(item => item.Id == pending.Id);
        stored.PreviewFingerprint.Should().Be("fresh-preview");
        stored.ConfirmedAtUtc.Should().BeNull();
        stored.ConfirmationTokenHash.Should().BeNull();
    }

    [Fact]
    public void Cancel_RejectsAnyLaterConfirmationOrExecution()
    {
        var capability = _catalogService.GetCapability(AgentCapabilityIds.HabitsBulkDelete)!;
        const string json = "{\"habit_ids\":[]}";
        var fingerprint = AgentOperationFingerprint.Compute("bulk_delete_habits", json);
        var pending = _store.Create(_userId, capability, "bulk_delete_habits", json,
            "Delete habits", fingerprint, AgentExecutionSurface.Chat);

        _store.Cancel(_userId, pending.Id, fingerprint).Should().BeTrue();

        _store.Confirm(_userId, pending.Id).Should().BeNull();
        _store.GetExecution(_userId, pending.Id).Should().BeNull();
    }

    [Fact]
    public void Revise_RejectsFingerprintThatDoesNotMatchPayload()
    {
        var capability = _catalogService.GetCapability(AgentCapabilityIds.HabitsBulkWrite)!;
        const string json = "{\"filter\":{\"all\":true}}";
        var fingerprint = AgentOperationFingerprint.Compute("bulk_update_habits", json);
        var pending = _store.Create(_userId, capability, "bulk_update_habits", json,
            "Update habits", fingerprint, AgentExecutionSurface.Chat);

        _store.Revise(_userId, pending.Id, fingerprint, "{\"revised_items\":[]}",
            "incorrect", "preview-state").Should().BeFalse();

        _store.GetExecution(_userId, pending.Id)!.Arguments.GetRawText().Should().Be(json);
    }
}
