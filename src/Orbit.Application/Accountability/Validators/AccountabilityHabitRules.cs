using FluentValidation;
using Orbit.Application.Common;

namespace Orbit.Application.Accountability.Validators;

public static class AccountabilityHabitRules
{
    public static void AddHabitIdsRules<T>(IRuleBuilder<T, IReadOnlyList<Guid>> rule)
    {
        rule
            .NotEmpty()
            .Must(ids => ids is null || ids.Count <= AppConstants.MaxAccountabilityHabitsPerUser)
            .WithCopy(ValidationErrorCodes.AccountabilityHabitLimit)
            .Must(ids => ids is null || ids.All(id => id != Guid.Empty))
            .WithCopy(ValidationErrorCodes.HabitIdsRequired)
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
            .WithCopy(ValidationErrorCodes.HabitIdsUnique);
    }
}
