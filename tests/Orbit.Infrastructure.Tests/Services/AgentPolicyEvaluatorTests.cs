using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Caching.Memory;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AgentPolicyEvaluatorTests : IDisposable
{
    [Theory]
    [InlineData(AgentAuthMethod.ApiKey, false)]
    [InlineData(AgentAuthMethod.ApiKey, true)]
    [InlineData(AgentAuthMethod.OAuth, false)]
    [InlineData(AgentAuthMethod.OAuth, true)]
    public async Task HabitSuggestions_RequireGoalsScopeAlongsideHabitsScope(AgentAuthMethod auth, bool readGoals)
    {
        var scopes = readGoals ? new[] { AgentScopes.ReadHabits, AgentScopes.ReadGoals } : [AgentScopes.ReadHabits];
        var result = await _policyEvaluator.EvaluateAsync(new(AgentCapabilityIds.HabitsRead, _userId,
            AgentExecutionSurface.Chat, auth, scopes, "suggest_habits", "Suggest habits"));
        result.Status.Should().Be(readGoals ? AgentPolicyDecisionStatus.Allowed : AgentPolicyDecisionStatus.Denied);
        if (!readGoals) result.Reason.Should().Be($"missing_scope:{AgentScopes.ReadGoals}");
    }

    private readonly OrbitDbContext _dbContext;
    private readonly AgentCatalogService _catalogService = new();
    private readonly PendingAgentOperationStore _pendingOperationStore;
    private readonly IAgentStepUpAuthorizationBridge _stepUpAuthorizationBridge;
    private readonly AgentPolicyEvaluator _policyEvaluator;
    private readonly Guid _userId;
    private readonly IOptions<AgentPlatformSettings> _settings = Options.Create(new AgentPlatformSettings());

    public AgentPolicyEvaluatorTests()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"AgentPolicyEvaluatorTests_{Guid.NewGuid()}")
            .Options;

        _dbContext = new OrbitDbContext(options);
        var user = User.Create("Alex", "alex@test.com").Value;
        _userId = user.Id;
        _dbContext.Users.Add(user);
        _dbContext.AppFeatureFlags.Add(AppFeatureFlag.Create("api_keys", true, "Pro", "API keys"));
        _dbContext.SaveChanges();

        _pendingOperationStore = new PendingAgentOperationStore(_dbContext, _settings);
        _stepUpAuthorizationBridge = Substitute.For<IAgentStepUpAuthorizationBridge>();
        _stepUpAuthorizationBridge
            .GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentConfirmationRequirement?)null);
        _policyEvaluator = new AgentPolicyEvaluator(
            _dbContext,
            _catalogService,
            _pendingOperationStore,
            _stepUpAuthorizationBridge,
            _settings,
            new FeatureFlagService(_dbContext, new MemoryCache(new MemoryCacheOptions()), Substitute.For<IHostEnvironment>(), new ConfigurationBuilder().Build()));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Evaluate_TargetedFlag_AllowsOnlyTargetedAccount()
    {
        var other = User.Create("Other", "other@example.com").Value;
        _dbContext.Users.Add(other);
        _dbContext.AppFeatureFlags.Add(AppFeatureFlag.Create("ai_retrospective", false));
        _dbContext.AppFeatureFlagAccounts.Add(AppFeatureFlagAccount.Create("ai_retrospective", _userId));
        _dbContext.SaveChanges();

        async Task<AgentPolicyDecision> EvaluateFor(Guid userId) => await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.RetrospectiveRead, userId, AgentExecutionSurface.Mcp, AgentAuthMethod.Jwt, [],
            "get_retrospective", "Get retrospective"));

        (await EvaluateFor(_userId)).Status.Should().Be(AgentPolicyDecisionStatus.Allowed);
        (await EvaluateFor(other.Id)).Reason.Should().Be("feature_disabled:ai_retrospective");
    }

    [Theory]
    [InlineData(AgentAuthMethod.ApiKey)]
    [InlineData(AgentAuthMethod.OAuth)]
    public async Task Evaluate_MissingApiKeyScope_DeniesOperation(AgentAuthMethod authMethod)
    {
        var decision = await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.HabitsRead,
            _userId,
            AgentExecutionSurface.Mcp,
            authMethod,
            [],
            "list_habits",
            "List habits"));

        decision.Status.Should().Be(AgentPolicyDecisionStatus.Denied);
        decision.Reason.Should().Be($"missing_scope:{AgentScopes.ReadHabits}");
    }

    [Fact]
    public async Task Evaluate_UnknownFeaturePlanRequirement_FailsClosed()
    {
        var user = _dbContext.Users.Single(item => item.Id == _userId);
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(30), Orbit.Domain.Enums.SubscriptionInterval.Monthly);
        var unknownRequirement = string.Concat("Yearly", "Pro");
        _dbContext.AppFeatureFlags.Add(AppFeatureFlag.Create(
            "ai_retrospective", true, unknownRequirement, "AI retrospective analysis"));
        _dbContext.SaveChanges();

        var decision = await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.RetrospectiveRead,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            "get_retrospective",
            "Get retrospective"));

        decision.Status.Should().Be(AgentPolicyDecisionStatus.Denied);
        decision.Reason.Should().Be($"feature_plan_required:ai_retrospective:{unknownRequirement}");
    }

    [Fact]
    public async Task Evaluate_DestructiveOperationWithoutConfirmation_CreatesPendingOperation()
    {
        var decision = await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.HabitsDelete,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            "delete_habit",
            "Delete habit via chat",
            OperationFingerprint: "delete_habit:{\"habitId\":\"123\"}"));

        decision.Status.Should().Be(AgentPolicyDecisionStatus.ConfirmationRequired);
        decision.PendingOperation.Should().NotBeNull();
        decision.PendingOperation!.CapabilityId.Should().Be(AgentCapabilityIds.HabitsDelete);
        decision.ConsumedConfirmation.Should().BeFalse();
    }

    [Fact]
    public async Task Evaluate_HighRiskMutationWithoutStepUp_RequiresPendingOperation()
    {
        var decision = await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.ApiKeysManage,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            "manage_api_keys",
            "Create API key",
            OperationFingerprint: "manage_api_keys:{\"action\":\"create\",\"name\":\"Claude\"}",
            OperationArgumentsJson: "{\"action\":\"create\",\"name\":\"Claude\"}"));

        decision.Status.Should().Be(AgentPolicyDecisionStatus.ConfirmationRequired);
        decision.Reason.Should().Be("step_up_required");
        decision.PendingOperation.Should().NotBeNull();
        decision.PendingOperation!.ActionKey.Should().Be("createApiKey");
    }

    [Fact]
    public async Task Evaluate_WithFreshConfirmationToken_AllowsDestructiveOperation()
    {
        var initialDecision = await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.HabitsDelete,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            "delete_habit",
            "Delete habit via chat",
            OperationFingerprint: "delete_habit:{\"habitId\":\"123\"}"));

        var confirmation = _pendingOperationStore.Confirm(_userId, initialDecision.PendingOperation!.Id);
        confirmation.Should().NotBeNull();

        var confirmedDecision = await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.HabitsDelete,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            "delete_habit",
            "Delete habit via chat",
            OperationFingerprint: "delete_habit:{\"habitId\":\"123\"}",
            ConfirmationToken: confirmation!.ConfirmationToken));

        confirmedDecision.Status.Should().Be(AgentPolicyDecisionStatus.Allowed);
        confirmedDecision.ConsumedConfirmation.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_WithStepUpConfirmation_AllowsHighRiskOperation()
    {
        var initialDecision = await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.ApiKeysManage,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            "manage_api_keys",
            "Create API key",
            OperationFingerprint: "manage_api_keys:{\"action\":\"create\",\"name\":\"Claude\"}",
            OperationArgumentsJson: "{\"action\":\"create\",\"name\":\"Claude\"}"));

        var pendingOperation = initialDecision.PendingOperation;
        pendingOperation.Should().NotBeNull();

        var confirmation = _pendingOperationStore.Confirm(_userId, pendingOperation!.Id);
        confirmation.Should().NotBeNull();

        var storedOperation = _dbContext.PendingAgentOperations.Single(item => item.Id == pendingOperation.Id);
        storedOperation.MarkStepUpSatisfied();
        _dbContext.SaveChanges();

        var confirmedDecision = await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.ApiKeysManage,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            "manage_api_keys",
            "Create API key",
            OperationFingerprint: "manage_api_keys:{\"action\":\"create\",\"name\":\"Claude\"}",
            ConfirmationToken: confirmation!.ConfirmationToken));

        confirmedDecision.Status.Should().Be(AgentPolicyDecisionStatus.Allowed);
    }

    [Fact]
    public async Task Evaluate_ApiKeyReadWithoutEscalation_AllowsTheReadOutright()
    {
        var decision = await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.ApiKeysRead,
            _userId,
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt,
            [],
            "get_api_keys",
            "Read API keys",
            OperationFingerprint: "get_api_keys:{}"));

        decision.Status.Should().Be(AgentPolicyDecisionStatus.Allowed);
        decision.ConsumedConfirmation.Should().BeFalse();
    }

    [Fact]
    public async Task Evaluate_ApiKeyReadEscalatedToStepUp_OpensAPendingOperationDoor()
    {
        var decision = await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.ApiKeysRead,
            _userId,
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt,
            [],
            "get_api_keys",
            "Read API keys",
            OperationFingerprint: "get_api_keys:{}",
            ConfirmationRequirementOverride: AgentConfirmationRequirement.StepUp));

        decision.Status.Should().Be(AgentPolicyDecisionStatus.ConfirmationRequired);
        decision.Reason.Should().Be("step_up_required");
        decision.PendingOperation.Should().NotBeNull();
        decision.PendingOperation!.ConfirmationRequirement.Should().Be(AgentConfirmationRequirement.StepUp);
    }

    [Fact]
    public async Task Evaluate_VerifiedStepUpOnTheApiKeyRead_TellsTheAuthorizationBridge()
    {
        var initialDecision = await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.ApiKeysRead,
            _userId,
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt,
            [],
            "get_api_keys",
            "Read API keys",
            OperationFingerprint: "get_api_keys:{}",
            ConfirmationRequirementOverride: AgentConfirmationRequirement.StepUp));

        var pendingOperation = initialDecision.PendingOperation;
        pendingOperation.Should().NotBeNull();

        var confirmation = _pendingOperationStore.Confirm(_userId, pendingOperation!.Id);
        confirmation.Should().NotBeNull();

        var storedOperation = _dbContext.PendingAgentOperations.Single(item => item.Id == pendingOperation.Id);
        storedOperation.MarkStepUpSatisfied();
        _dbContext.SaveChanges();

        var confirmedDecision = await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.ApiKeysRead,
            _userId,
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt,
            [],
            "get_api_keys",
            "Read API keys",
            OperationFingerprint: "get_api_keys:{}",
            ConfirmationToken: confirmation!.ConfirmationToken,
            ConfirmationRequirementOverride: AgentConfirmationRequirement.StepUp));

        confirmedDecision.Status.Should().Be(AgentPolicyDecisionStatus.Allowed);
        await _stepUpAuthorizationBridge.Received(1).OnStepUpVerifiedAsync(AgentCapabilityIds.ApiKeysRead, _userId);
    }

    [Fact]
    public async Task Evaluate_VerifiedStepUpOnApiKeyManagement_TellsTheAuthorizationBridge()
    {
        var initialDecision = await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.ApiKeysManage,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            "manage_api_keys",
            "Revoke API key",
            OperationFingerprint: "manage_api_keys:{\"action\":\"revoke\"}"));

        var pendingOperation = initialDecision.PendingOperation;
        pendingOperation.Should().NotBeNull();

        var confirmation = _pendingOperationStore.Confirm(_userId, pendingOperation!.Id);
        var storedOperation = _dbContext.PendingAgentOperations.Single(item => item.Id == pendingOperation.Id);
        storedOperation.MarkStepUpSatisfied();
        _dbContext.SaveChanges();

        var confirmedDecision = await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.ApiKeysManage,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            "manage_api_keys",
            "Revoke API key",
            OperationFingerprint: "manage_api_keys:{\"action\":\"revoke\"}",
            ConfirmationToken: confirmation!.ConfirmationToken));

        confirmedDecision.Status.Should().Be(AgentPolicyDecisionStatus.Allowed);
        await _stepUpAuthorizationBridge.Received(1).OnStepUpVerifiedAsync(AgentCapabilityIds.ApiKeysManage, _userId);
    }

    [Fact]
    public async Task Evaluate_FreshConfirmationWithoutStepUp_TellsTheBridgeNothing()
    {
        var initialDecision = await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.HabitsDelete,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            "delete_habit",
            "Delete habit via chat",
            OperationFingerprint: "delete_habit:{\"habitId\":\"123\"}"));

        var confirmation = _pendingOperationStore.Confirm(_userId, initialDecision.PendingOperation!.Id);

        var confirmedDecision = await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.HabitsDelete,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            "delete_habit",
            "Delete habit via chat",
            OperationFingerprint: "delete_habit:{\"habitId\":\"123\"}",
            ConfirmationToken: confirmation!.ConfirmationToken));

        confirmedDecision.Status.Should().Be(AgentPolicyDecisionStatus.Allowed);
        await _stepUpAuthorizationBridge.DidNotReceive().OnStepUpVerifiedAsync(Arg.Any<string>(), Arg.Any<Guid>());
    }

    [Fact]
    public async Task Evaluate_InShadowMode_ReturnsAllowedWithShadowDecision()
    {
        var shadowEvaluator = new AgentPolicyEvaluator(
            _dbContext,
            _catalogService,
            _pendingOperationStore,
            _stepUpAuthorizationBridge,
            Options.Create(new AgentPlatformSettings { ShadowModeEnabled = true }),
            new FeatureFlagService(_dbContext, new MemoryCache(new MemoryCacheOptions()), Substitute.For<IHostEnvironment>(), new ConfigurationBuilder().Build()));

        var decision = (await shadowEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.ApiKeysManage,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            "manage_api_key",
            "Create API key",
            OperationFingerprint: "create_api_key:{\"name\":\"Claude\"}")));

        decision.Status.Should().Be(AgentPolicyDecisionStatus.Allowed);
        decision.ShadowStatus.Should().Be(AgentPolicyDecisionStatus.ConfirmationRequired);
        decision.ShadowReason.Should().Be("step_up_required");
        decision.PendingOperation.Should().BeNull();
        decision.ConsumedConfirmation.Should().BeFalse();
    }

    [Fact]
    public async Task Evaluate_InShadowMode_PreservesConsumedConfirmation()
    {
        const string fingerprint = "delete_habit:{\"habitId\":\"123\"}";
        var pending = await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.HabitsDelete,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            "delete_habit",
            "Delete habit via chat",
            OperationFingerprint: fingerprint));
        var confirmation = _pendingOperationStore.Confirm(_userId, pending.PendingOperation!.Id);
        confirmation.Should().NotBeNull();

        var shadowEvaluator = new AgentPolicyEvaluator(
            _dbContext,
            _catalogService,
            _pendingOperationStore,
            _stepUpAuthorizationBridge,
            Options.Create(new AgentPlatformSettings { ShadowModeEnabled = true }),
            new FeatureFlagService(_dbContext, new MemoryCache(new MemoryCacheOptions()), Substitute.For<IHostEnvironment>(), new ConfigurationBuilder().Build()));
        var decision = (await shadowEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.HabitsDelete,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            "delete_habit",
            "Delete habit via chat",
            OperationFingerprint: fingerprint,
            ConfirmationToken: confirmation!.ConfirmationToken)));

        decision.Status.Should().Be(AgentPolicyDecisionStatus.Allowed);
        decision.ShadowStatus.Should().Be(AgentPolicyDecisionStatus.Allowed);
        decision.ConsumedConfirmation.Should().BeTrue();

        var replay = (await shadowEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.HabitsDelete,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            "delete_habit",
            "Delete habit via chat",
            OperationFingerprint: fingerprint,
            ConfirmationToken: confirmation.ConfirmationToken)));

        replay.ConsumedConfirmation.Should().BeFalse();
        replay.ShadowStatus.Should().Be(AgentPolicyDecisionStatus.ConfirmationRequired);
    }

    [Theory]
    [InlineData(AgentCapabilityIds.HabitsDelete, "delete_habit")]
    [InlineData(AgentCapabilityIds.HabitsBulkDelete, "bulk_delete_habits")]
    [InlineData(AgentCapabilityIds.TagsDelete, "delete_tag")]
    public async Task Evaluate_DestructiveChatCapability_OnChatSurface_RequiresConfirmation(
        string capabilityId,
        string sourceName)
    {
        var decision = await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            capabilityId,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            sourceName,
            $"{sourceName} via chat",
            OperationFingerprint: $"{sourceName}:{{\"id\":\"123\"}}"));

        decision.Status.Should().Be(AgentPolicyDecisionStatus.ConfirmationRequired);
        decision.Reason.Should().Be("confirmation_required");
        decision.PendingOperation.Should().NotBeNull();
        decision.PendingOperation!.CapabilityId.Should().Be(capabilityId);
    }

    [Theory]
    [InlineData("bulk_create_habits")]
    [InlineData("bulk_log_habits")]
    [InlineData("bulk_skip_habits")]
    [InlineData("bulk_update_habits")]
    [InlineData("bulk_reschedule_habits")]
    public async Task Evaluate_ReversibleBulkCapability_RequiresConfirmationWhenChatHoldIsApplied(string sourceName)
    {
        var context = new AgentPolicyEvaluationContext(
            AgentCapabilityIds.HabitsBulkWrite, _userId, AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt, [], sourceName, $"{sourceName} via chat",
            OperationFingerprint: $"{sourceName}:{{}}");

        var declared = await _policyEvaluator.EvaluateAsync(context);
        var held = await _policyEvaluator.EvaluateAsync(context with
        {
            ConfirmationRequirementOverride = AgentConfirmationRequirement.FreshConfirmation
        });

        declared.Status.Should().Be(AgentPolicyDecisionStatus.Allowed);
        declared.Capability!.RiskClass.Should().Be(AgentRiskClass.Low);
        declared.PendingOperation.Should().BeNull();
        held.Status.Should().Be(AgentPolicyDecisionStatus.ConfirmationRequired);
        held.PendingOperation!.RiskClass.Should().Be(AgentRiskClass.Low);
        held.PendingOperation.ConfirmationRequirement.Should().Be(AgentConfirmationRequirement.FreshConfirmation);
    }

    [Fact]
    public async Task Evaluate_GoalsDelete_FreeUserRequiresConfirmation()
    {
        var decision = await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.GoalsDelete,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            "delete_goal",
            "delete_goal via chat",
            OperationFingerprint: "delete_goal:{\"goalId\":\"123\"}"));

        decision.Status.Should().Be(AgentPolicyDecisionStatus.ConfirmationRequired);
        decision.Reason.Should().Be("confirmation_required");
        decision.PendingOperation.Should().NotBeNull();
    }

    [Fact]
    public async Task Evaluate_DeactivatedUser_NonAuthCapability_DeniesWithAccountDeactivated()
    {
        var user = _dbContext.Users.First(item => item.Id == _userId);
        user.Deactivate(DateTime.UtcNow.AddDays(7));
        _dbContext.SaveChanges();

        var decision = await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.HabitsWrite,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            "create_habit",
            "create_habit via chat",
            OperationFingerprint: "create_habit:{\"title\":\"Meditate\"}"));

        decision.Status.Should().Be(AgentPolicyDecisionStatus.Denied);
        decision.Reason.Should().Be("account_deactivated");
    }

    [Fact]
    public async Task Evaluate_DeactivatedUser_AccountManageCapability_IsNotBlockedByDeactivation()
    {
        var user = _dbContext.Users.First(item => item.Id == _userId);
        user.Deactivate(DateTime.UtcNow.AddDays(7));
        _dbContext.SaveChanges();

        var decision = await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.AccountManage,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            "manage_account",
            "manage_account via chat",
            OperationFingerprint: "manage_account:{\"action\":\"delete\"}"));

        decision.Reason.Should().NotBe("user_not_found");
        decision.Reason.Should().NotBe("account_deactivated");
    }

    [Theory]
    [InlineData("create_habit")]
    [InlineData("log_habit")]
    public async Task Evaluate_LowRiskHabitWrite_OnChatSurface_WaitsForApproval(string sourceName)
    {
        var decision = await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.HabitsWrite,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            sourceName,
            $"{sourceName} via chat",
            OperationFingerprint: $"{sourceName}:{{\"title\":\"Meditate\"}}",
            ConfirmationRequirementOverride: AgentConfirmationRequirement.FreshConfirmation));

        decision.Status.Should().Be(AgentPolicyDecisionStatus.ConfirmationRequired);
        decision.Reason.Should().Be("confirmation_required");
        decision.PendingOperation.Should().NotBeNull();
        decision.PendingOperation!.ConfirmationRequirement
            .Should().Be(AgentConfirmationRequirement.FreshConfirmation);
    }

    [Theory]
    [InlineData("create_habit")]
    [InlineData("log_habit")]
    public async Task Evaluate_LowRiskHabitWrite_OnMcpSurface_IsAllowed(string sourceName)
    {
        var decision = await _policyEvaluator.EvaluateAsync(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.HabitsWrite,
            _userId,
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt,
            [],
            sourceName,
            $"{sourceName} via mcp",
            OperationFingerprint: $"{sourceName}:{{\"title\":\"Meditate\"}}"));

        decision.Status.Should().Be(AgentPolicyDecisionStatus.Allowed);
        decision.PendingOperation.Should().BeNull();
    }
}
