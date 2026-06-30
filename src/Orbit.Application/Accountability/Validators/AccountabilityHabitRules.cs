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
            .WithMessage($"You can link at most {AppConstants.MaxAccountabilityHabitsPerUser} habits per pair.")
            .Must(ids => ids is null || ids.All(id => id != Guid.Empty))
            .WithMessage("Habit ids must not be empty.")
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
            .WithMessage("Habit ids must not contain duplicates.");
    }
}
