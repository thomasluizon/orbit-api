using FluentAssertions;
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

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

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
