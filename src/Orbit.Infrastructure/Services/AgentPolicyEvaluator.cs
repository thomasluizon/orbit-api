using Orbit.Application.FeatureFlags;
using Orbit.Application.Common;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class AgentPolicyEvaluator(
    OrbitDbContext dbContext,
    IAgentCatalogService catalogService,
    IPendingAgentOperationStore pendingOperationStore,
    IAgentStepUpAuthorizationBridge stepUpAuthorizationBridge,
    IFeatureFlagService featureFlagService) : IAgentPolicyEvaluator
{
    private static bool MissingSuggestionScope(AgentPolicyEvaluationContext context) => context.SourceName == "suggest_habits"
        && context.AuthMethod is AgentAuthMethod.ApiKey or AgentAuthMethod.OAuth
        && !context.GrantedScopes.Contains(AgentScopes.ReadGoals, StringComparer.OrdinalIgnoreCase);

    public AgentPolicyDecision Evaluate(AgentPolicyEvaluationContext context)
    {
        return EvaluateInternal(context);
    }

    private AgentPolicyDecision EvaluateInternal(
        AgentPolicyEvaluationContext context)
    {
        var declared = catalogService.GetCapability(context.CapabilityId);
        if (declared is null)
            return new AgentPolicyDecision(AgentPolicyDecisionStatus.Denied, null, Orbit.Application.Common.AgentPolicyReasons.UnsupportedByPolicy);

        var capability = context.ConfirmationRequirementOverride is { } escalated
            ? declared with { ConfirmationRequirement = escalated }
            : declared;

        var user = GetUser(context.UserId);
        if (user is null)
            return new AgentPolicyDecision(AgentPolicyDecisionStatus.Denied, capability, "user_not_found");

        if (capability.Id == FeatureFlagActions.CapabilityId && !user.IsAdmin)
            return new AgentPolicyDecision(AgentPolicyDecisionStatus.Denied, capability, "tool_not_found");

        var accessDecision = EvaluateAccessRequirements(context, capability, user);
        if (accessDecision is not null)
            return accessDecision;

        return EvaluateConfirmationRequirement(context, capability);
    }

    private User? GetUser(Guid userId)
    {
        return dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefault(item => item.Id == userId);
    }

    private AgentPolicyDecision? EvaluateAccessRequirements(
        AgentPolicyEvaluationContext context,
        AgentCapability capability,
        User user)
    {
        if (user.IsDeactivated &&
            capability.Id is not AgentCapabilityIds.AuthManage and not AgentCapabilityIds.AccountManage)
        {
            return new AgentPolicyDecision(AgentPolicyDecisionStatus.Denied, capability, "account_deactivated");
        }

        var gamification = capability.Id is AgentCapabilityIds.GamificationRead
            or AgentCapabilityIds.GamificationRepair or AgentCapabilityIds.GamificationRepairGap;
        var meetsPlan = gamification
            ? GamificationAccess.IsUnlocked(user, featureFlagService.ResolveForUser(user.Id).Where(flag => flag.Enabled).Select(flag => flag.Key))
            : UserMeetsPlanRequirement(user, capability.PlanRequirement);
        if (!meetsPlan)
        {
            return new AgentPolicyDecision(
                AgentPolicyDecisionStatus.Denied,
                capability,
                $"plan_required:{capability.PlanRequirement}");
        }

        var featureFlagDenial = EvaluateFeatureFlags(user, capability);
        if (featureFlagDenial is not null)
            return featureFlagDenial;

        var grantedByAdminRole = capability.Id == FeatureFlagActions.CapabilityId && user.IsAdmin;
        if (context.AuthMethod is AgentAuthMethod.ApiKey or AgentAuthMethod.OAuth &&
            !grantedByAdminRole &&
            !context.GrantedScopes.Contains(capability.Scope, StringComparer.OrdinalIgnoreCase))
        {
            return new AgentPolicyDecision(
                AgentPolicyDecisionStatus.Denied,
                capability,
                $"missing_scope:{capability.Scope}");
        }

        if (context.IsReadOnlyCredential && capability.IsMutation)
            return new AgentPolicyDecision(AgentPolicyDecisionStatus.Denied, capability, "read_only_credential");

        if (MissingSuggestionScope(context))
            return new AgentPolicyDecision(AgentPolicyDecisionStatus.Denied, capability, $"missing_scope:{AgentScopes.ReadGoals}");

        var needsFingerprint = capability.IsMutation ||
            capability.ConfirmationRequirement is AgentConfirmationRequirement.FreshConfirmation or AgentConfirmationRequirement.StepUp;
        if (needsFingerprint && string.IsNullOrWhiteSpace(context.OperationFingerprint))
            return new AgentPolicyDecision(AgentPolicyDecisionStatus.Denied, capability, "operation_not_deterministic");

        return null;
    }

    private AgentPolicyDecision EvaluateConfirmationRequirement(
        AgentPolicyEvaluationContext context,
        AgentCapability capability)
    {
        if (capability.ConfirmationRequirement is not (AgentConfirmationRequirement.FreshConfirmation or AgentConfirmationRequirement.StepUp))
            return new AgentPolicyDecision(AgentPolicyDecisionStatus.Allowed, capability);

        var requireStepUp = capability.ConfirmationRequirement == AgentConfirmationRequirement.StepUp;
        if (HasFreshConfirmation(context, capability, requireStepUp))
        {
            if (requireStepUp)
                stepUpAuthorizationBridge.OnStepUpVerified(capability.Id, context.UserId);

            return new AgentPolicyDecision(AgentPolicyDecisionStatus.Allowed, capability,
                ConsumedConfirmation: true);
        }

        var reason = requireStepUp ? "step_up_required" : "confirmation_required";
        var pendingOperation = pendingOperationStore.Create(
            context.UserId,
            capability,
            context.SourceName,
            context.OperationArgumentsJson ?? "{}",
            context.OperationSummary,
            context.OperationFingerprint!,
            context.Surface,
            context.Conversation);

        return new AgentPolicyDecision(
            AgentPolicyDecisionStatus.ConfirmationRequired,
            capability,
            reason,
            pendingOperation);
    }

    private bool HasFreshConfirmation(
        AgentPolicyEvaluationContext context,
        AgentCapability capability,
        bool requireStepUp)
    {
        return !string.IsNullOrWhiteSpace(context.ConfirmationToken) &&
               pendingOperationStore.TryConsumeFreshConfirmation(
                   context.UserId,
                   capability.Id,
                   context.OperationFingerprint!,
                   context.ConfirmationToken,
                   requireStepUp);
    }

    private AgentPolicyDecision? EvaluateFeatureFlags(User user, AgentCapability capability)
    {
        if (capability.FeatureFlagKeys is not { Count: > 0 })
            return null;

        var flags = featureFlagService.ResolveForUser(user.Id);

        foreach (var featureFlagKey in capability.FeatureFlagKeys)
        {
            var flag = flags.FirstOrDefault(item => string.Equals(item.Key, featureFlagKey, StringComparison.OrdinalIgnoreCase));
            if (flag is null || !flag.Enabled)
            {
                var reason = flag is not null && !UserMeetsPlanRequirement(user, flag.PlanRequirement)
                    ? $"feature_plan_required:{featureFlagKey}:{flag.PlanRequirement}"
                    : $"feature_disabled:{featureFlagKey}";
                return new AgentPolicyDecision(AgentPolicyDecisionStatus.Denied, capability, reason);
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
            "free" => true,
            _ => false
        };
    }
}
