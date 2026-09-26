using Orbit.Application.Auth.Services;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.ApiKeys.Services;

public sealed class ApiKeyManagementAuthorization(
    IAppConfigService appConfigService,
    EmailChallengeService challengeService) : IAgentStepUpAuthorizationBridge
{
    private static readonly TimeSpan GrantLifetime =
        TimeSpan.FromMinutes(AppConstants.SensitiveOperationChallengeTtlMinutes);

    public Task<bool> IsRequiredAsync(CancellationToken cancellationToken) =>
        appConfigService.GetAsync(AppConfigKeys.RequireApiKeyCreationStepUp, false, cancellationToken);

    /// <summary>Opens the grant after a door verified the emailed code.</summary>
    public void Grant(Guid userId, TimeSpan lifetime) =>
        challengeService.AuthorizeOnce(EmailChallengeOperation.ApiKeyManagement, userId, lifetime);

    /// <summary>Reads the grant without spending it. Listing and revoking use this.</summary>
    public bool HasGrant(Guid userId) =>
        challengeService.HasAuthorization(EmailChallengeOperation.ApiKeyManagement, userId);

    /// <summary>Spends the grant. Only the creation of new key material uses this.</summary>
    public bool TryConsumeGrant(Guid userId) =>
        challengeService.TryConsumeAuthorization(EmailChallengeOperation.ApiKeyManagement, userId);

    public async Task<AgentConfirmationRequirement?> GetRequiredConfirmationAsync(
        string capabilityId,
        CancellationToken cancellationToken)
    {
        if (!IsApiKeyCapability(capabilityId))
            return null;

        return await IsRequiredAsync(cancellationToken)
            ? AgentConfirmationRequirement.StepUp
            : null;
    }

    public void OnStepUpVerified(string capabilityId, Guid userId)
    {
        if (IsApiKeyCapability(capabilityId))
            Grant(userId, GrantLifetime);
    }

    private static bool IsApiKeyCapability(string capabilityId) =>
        capabilityId is AgentCapabilityIds.ApiKeysRead or AgentCapabilityIds.ApiKeysManage;
}
