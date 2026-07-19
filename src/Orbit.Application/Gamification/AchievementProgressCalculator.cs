using Orbit.Application.Gamification.Models;
using Orbit.Domain.Enums;

namespace Orbit.Application.Gamification;

/// <summary>
/// Maps an achievement definition plus the user's live metric values to the DTO's
/// <c>(ProgressCurrent, ProgressTarget)</c> pair. Boolean/one-shot achievements (<see cref="ProgressMetric.None"/>
/// or no target) report <c>(null, null)</c>; an earned achievement reports a full bar; a locked one reports
/// its current metric value clamped to the target.
/// </summary>
public static class AchievementProgressCalculator
{
    public static (int? Current, int? Target) Compute(
        AchievementDefinition definition, AchievementProgressMetrics metrics, bool isEarned)
    {
        if (definition.Metric == ProgressMetric.None || definition.ProgressTarget is not int target)
            return (null, null);

        if (isEarned)
            return (target, target);

        var value = metrics.ValueFor(definition.Metric);
        return (Math.Min(value, target), target);
    }
}
