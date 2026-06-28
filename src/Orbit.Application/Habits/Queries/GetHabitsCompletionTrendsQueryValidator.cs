using FluentValidation;
using Orbit.Application.Common;

namespace Orbit.Application.Habits.Queries;

public class GetHabitsCompletionTrendsQueryValidator : AbstractValidator<GetHabitsCompletionTrendsQuery>
{
    public GetHabitsCompletionTrendsQueryValidator()
    {
        RuleFor(x => x.DateFrom)
            .LessThanOrEqualTo(x => x.DateTo)
            .WithMessage("DateFrom must be on or before DateTo.");

        RuleFor(x => x)
            .Must(x => x.DateTo.DayNumber - x.DateFrom.DayNumber <= AppConstants.MaxRangeDays)
            .WithMessage($"Date range must not exceed {AppConstants.MaxRangeDays} days.");
    }
}
