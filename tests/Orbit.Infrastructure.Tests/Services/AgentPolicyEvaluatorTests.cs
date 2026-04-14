using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AgentPolicyEvaluatorTests : IDisposable
{
    private readonly OrbitDbContext _dbContext;
    private readonly AgentCatalogService _catalogService = new();
    private readonly PendingAgentOperationStore _pendingOperationStore;
    private readonly AgentPolicyEvaluator _policyEvaluator;
    private readonly Guid _userId;
    private readonly IOptions<AgentPlatformSettings> _settings = Options.Create(new AgentPlatformSettings());

    public AgentPolicyEvaluatorTests()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"AgentPolicyEvaluatorTests_{Guid.NewGuid()}")
            .Options;

        _dbContext = new OrbitDbContext(options);
        var user = User.Create("Thomas", "thomas@test.com").Value;
        _userId = user.Id;
        _dbContext.Users.Add(user);
        _dbContext.AppFeatureFlags.Add(AppFeatureFlag.Create("api_keys", true, "Pro", "API keys"));
        _dbContext.SaveChanges();

        _pendingOperationStore = new PendingAgentOperationStore(_dbContext, _settings);
        _policyEvaluator = new AgentPolicyEvaluator(_dbContext, _catalogService, _pendingOperationStore, _settings);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Evaluate_MissingApiKeyScope_DeniesOperation()
    {
        var decision = _policyEvaluator.Evaluate(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.HabitsRead,
            _userId,
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.ApiKey,
            [],
            "list_habits",
            "List habits"));

        decision.Status.Should().Be(AgentPolicyDecisionStatus.Denied);
        decision.Reason.Should().Be($"missing_scope:{AgentScopes.ReadHabits}");
    }

    [Fact]
    public void Evaluate_DestructiveOperationWithoutConfirmation_CreatesPendingOperation()
    {
        var decision = _policyEvaluator.Evaluate(new AgentPolicyEvaluationContext(
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
    }

    [Fact]
    public void Evaluate_HighRiskMutationWithoutStepUp_RequiresPendingOperation()
    {
        var decision = _policyEvaluator.Evaluate(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.ApiKeysManage,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            "manage_api_key",
            "Create API key",
            OperationFingerprint: "create_api_key:{\"name\":\"Claude\"}"));

        decision.Status.Should().Be(AgentPolicyDecisionStatus.ConfirmationRequired);
        decision.Reason.Should().Be("step_up_required");
        decision.PendingOperation.Should().NotBeNull();
    }

    [Fact]
    public void Evaluate_WithFreshConfirmationToken_AllowsDestructiveOperation()
    {
        var initialDecision = _policyEvaluator.Evaluate(new AgentPolicyEvaluationContext(
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

        var confirmedDecision = _policyEvaluator.Evaluate(new AgentPolicyEvaluationContext(
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
    }

    [Fact]
    public void Evaluate_WithStepUpConfirmation_AllowsHighRiskOperation()
    {
        var initialDecision = _policyEvaluator.Evaluate(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.ApiKeysManage,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            "manage_api_key",
            "Create API key",
            OperationFingerprint: "create_api_key:{\"name\":\"Claude\"}"));

        var pendingOperation = initialDecision.PendingOperation;
        pendingOperation.Should().NotBeNull();

        var confirmation = _pendingOperationStore.Confirm(_userId, pendingOperation!.Id);
        confirmation.Should().NotBeNull();

        var stepUpOperation = _pendingOperationStore.MarkStepUp(_userId, pendingOperation.Id);
        stepUpOperation.Should().NotBeNull();

        var confirmedDecision = _policyEvaluator.Evaluate(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.ApiKeysManage,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            "manage_api_key",
            "Create API key",
            OperationFingerprint: "create_api_key:{\"name\":\"Claude\"}",
            ConfirmationToken: confirmation!.ConfirmationToken));

        confirmedDecision.Status.Should().Be(AgentPolicyDecisionStatus.Allowed);
    }

    [Fact]
    public void Evaluate_InShadowMode_ReturnsAllowedWithShadowDecision()
    {
        var shadowEvaluator = new AgentPolicyEvaluator(
            _dbContext,
            _catalogService,
            _pendingOperationStore,
            Options.Create(new AgentPlatformSettings { ShadowModeEnabled = true }));

        var decision = shadowEvaluator.Evaluate(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.ApiKeysManage,
            _userId,
            AgentExecutionSurface.Chat,
            AgentAuthMethod.Jwt,
            [],
            "manage_api_key",
            "Create API key",
            OperationFingerprint: "create_api_key:{\"name\":\"Claude\"}"));

        decision.Status.Should().Be(AgentPolicyDecisionStatus.Allowed);
        decision.ShadowStatus.Should().Be(AgentPolicyDecisionStatus.ConfirmationRequired);
        decision.ShadowReason.Should().Be("step_up_required");
        decision.PendingOperation.Should().BeNull();
    }
}
