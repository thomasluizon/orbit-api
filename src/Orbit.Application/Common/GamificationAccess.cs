using Orbit.Domain.Entities;

namespace Orbit.Application.Common;

public static class GamificationAccess
{
    public static bool IsUnlocked(User user, IEnumerable<string> enabledFlags) =>
        user.HasProAccess || enabledFlags.Contains(FeatureFlagKeys.GamificationFreeTier, StringComparer.OrdinalIgnoreCase);
}
