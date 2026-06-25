using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

/// <summary>
/// Pure validation guards for <see cref="Goal"/> invariants shared by the create and update paths.
/// Returns the matching <see cref="DomainErrors"/> entry on the first violation (title, then target
/// value, then unit), or null when the core fields are valid.
/// </summary>
internal static class GoalInvariants
{
    public static AppError? ValidateCoreFields(string title, decimal targetValue, string unit)
    {
        if (string.IsNullOrWhiteSpace(title))
            return DomainErrors.TitleRequired;

        if (targetValue <= 0)
            return DomainErrors.TargetValueInvalid;

        if (string.IsNullOrWhiteSpace(unit))
            return DomainErrors.UnitRequired;

        return null;
    }
}
