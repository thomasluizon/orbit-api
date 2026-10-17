using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Connections;

public sealed class NotionAccessGate(IPayGateService payGate, IFeatureFlagService flags)
{
    public async Task<Result> CheckAsync(Guid userId, CancellationToken ct)
    {
        var plan = await payGate.CanAccessNotion(userId, ct);
        if (plan.IsFailure) return plan;
        var enabled = await flags.GetEnabledKeysForUserAsync(userId, ct, forceRefresh: true);
        return enabled.Contains(FeatureFlagKeys.NotionIntegration, StringComparer.Ordinal)
            ? Result.Success() : Result.PayGateFailure(ErrorMessages.ProFeature.Message);
    }
}
