using FluentValidation;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Habits.Validators;

public class GetHabitsQueryValidator : AbstractValidator<GetHabitsQuery>
{
    private static readonly string[] ValidFrequencyUnits = ["Day", "Week", "Month", "Year", "none"];

    public GetHabitsQueryValidator()
    {
        RuleFor(q => q.Search)
            .MaximumLength(200)
            .When(q => q.Search != null);

        RuleFor(q => q.DueDateTo)
            .GreaterThanOrEqualTo(q => q.DueDateFrom)
            .WithMessage("dueDateTo must be >= dueDateFrom")
            .When(q => q.DueDateFrom.HasValue && q.DueDateTo.HasValue);

        RuleFor(q => q.FrequencyUnitFilter)
            .Must(v => ValidFrequencyUnits.Contains(v, StringComparer.OrdinalIgnoreCase))
            .WithMessage("frequencyUnit must be Day, Week, Month, Year, or none")
            .When(q => q.FrequencyUnitFilter != null);
    }
}
