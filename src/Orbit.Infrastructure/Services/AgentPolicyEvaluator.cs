using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class AgentPolicyEvaluator(
    OrbitDbContext dbContext,
    IAgentCatalogService catalogService,
    IPendingAgentOperationStore pendingOperationStore,
    IOptions<AgentPlatformSettings> settings) : IAgentPolicyEvaluator
{
    private readonly AgentPlatformSettings _settings = settings.Value;

    public AgentPolicyDecision Evaluate(AgentPolicyEvaluationContext context)
    {
        var evaluated = EvaluateInternal(context, createPendingOperations: !_settings.ShadowModeEnabled);
        if (!_settings.ShadowModeEnabled)
            return evaluated;

        return new AgentPolicyDecision(
            AgentPolicyDecisionStatus.Allowed,
            evaluated.Capability,
            ShadowStatus: evaluated.Status,
            ShadowReason: evaluated.Reason);
    }

    private AgentPolicyDecision EvaluateInternal(
        AgentPolicyEvaluationContext context,
        bool createPendingOperations)
    {
        var capability = catalogService.GetCapability(context.CapabilityId);
        if (capability is null)
            return new AgentPolicyDecision(AgentPolicyDecisionStatus.Denied, null, "unsupported_by_policy");

        var user = dbContext.Users
            .AsNoTracking()
            .FirstOrDefault(item => item.Id == context.UserId);

        if (user is null)
            return new AgentPolicyDecision(AgentPolicyDecisionStatus.Denied, capability, "user_not_found");

        if (user.IsDeactivated &&
            capability.Id is not AgentCapabilityIds.AuthManage and not AgentCapabilityIds.AccountManage)
        {
            return new AgentPolicyDecision(AgentPolicyDecisionStatus.Denied, capability, "account_deactivated");
        }

        if (!UserMeetsPlanRequirement(user, capability.PlanRequirement))
            return new AgentPolicyDecision(
                AgentPolicyDecisionStatus.Denied,
                capability,
                $"plan_required:{capability.PlanRequirement}");

        var featureFlagDenial = EvaluateFeatureFlags(user, capability);
        if (featureFlagDenial is not null)
            return featureFlagDenial;

        if (context.AuthMethod == AgentAuthMethod.ApiKey &&
            !context.GrantedScopes.Contains(capability.Scope, StringComparer.OrdinalIgnoreCase))
        {
            return new AgentPolicyDecision(
                AgentPolicyDecisionStatus.Denied,
                capability,
                $"missing_scope:{capability.Scope}");
        }

        if (context.IsReadOnlyCredential && capability.IsMutation)
            return new AgentPolicyDecision(AgentPolicyDecisionStatus.Denied, capability, "read_only_credential");

        if (capability.IsMutation && string.IsNullOrWhiteSpace(context.OperationFingerprint))
            return new AgentPolicyDecision(AgentPolicyDecisionStatus.Denied, capability, "operation_not_deterministic");

        if (capability.ConfirmationRequirement is AgentConfirmationRequirement.FreshConfirmation or AgentConfirmationRequirement.StepUp)
        {
            var requireStepUp = capability.ConfirmationRequirement == AgentConfirmationRequirement.StepUp;

            if (!string.IsNullOrWhiteSpace(context.ConfirmationToken) &&
                pendingOperationStore.TryConsumeFreshConfirmation(
                    context.UserId,
                    capability.Id,
                    context.OperationFingerprint!,
                    context.ConfirmationToken,
                    requireStepUp))
            {
                return new AgentPolicyDecision(AgentPolicyDecisionStatus.Allowed, capability);
            }

            if (!createPendingOperations)
            {
                return new AgentPolicyDecision(
                    AgentPolicyDecisionStatus.ConfirmationRequired,
                    capability,
                    requireStepUp ? "step_up_required" : "confirmation_required");
            }

            var pendingOperation = pendingOperationStore.Create(
                context.UserId,
                capability,
                context.SourceName,
                context.OperationArgumentsJson ?? "{}",
                context.OperationSummary,
                context.OperationFingerprint!,
                context.Surface);

            return new AgentPolicyDecision(
                AgentPolicyDecisionStatus.ConfirmationRequired,
                capability,
                requireStepUp ? "step_up_required" : "confirmation_required",
                pendingOperation);
        }

        return new AgentPolicyDecision(AgentPolicyDecisionStatus.Allowed, capability);
    }

    private AgentPolicyDecision? EvaluateFeatureFlags(User user, AgentCapability capability)
    {
        if (capability.FeatureFlagKeys is not { Count: > 0 })
            return null;

        var flags = dbContext.AppFeatureFlags
            .AsNoTracking()
            .Where(item => capability.FeatureFlagKeys.Contains(item.Key))
            .ToList();

        foreach (var featureFlagKey in capability.FeatureFlagKeys)
        {
            var flag = flags.FirstOrDefault(item => string.Equals(item.Key, featureFlagKey, StringComparison.OrdinalIgnoreCase));
            if (flag is null || !flag.Enabled)
            {
                return new AgentPolicyDecision(
                    AgentPolicyDecisionStatus.Denied,
                    capability,
                    $"feature_disabled:{featureFlagKey}");
            }

            if (!UserMeetsPlanRequirement(user, flag.PlanRequirement))
            {
                return new AgentPolicyDecision(
                    AgentPolicyDecisionStatus.Denied,
                    capability,
                    $"feature_plan_required:{featureFlagKey}:{flag.PlanRequirement}");
            }
        }

        return null;
    }

    private static bool UserMeetsPlanRequirement(User user, string? planRequirement)
    {
        if (string.IsNullOrWhiteSpace(planRequirement))
            return true;

        return planRequirement.Trim().ToLowerInvariant() switch
        {
            "pro" => user.HasProAccess,
            "yearlypro" or "yearly_pro" or "yearly-pro" => user.IsYearlyPro,
            "free" => true,
            _ => false
        };
    }
}
