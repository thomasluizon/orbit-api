using FluentValidation;
using Orbit.Application.Common;

namespace Orbit.Application.Goals.Queries;

public class GetGoalProgressHistoryQueryValidator : AbstractValidator<GetGoalProgressHistoryQuery>
{
    public GetGoalProgressHistoryQueryValidator()
    {
        RuleFor(x => x.DateFrom)
            .LessThanOrEqualTo(x => x.DateTo)
            .WithCopy(ValidationErrorCodes.DateRangeOrder);

        RuleFor(x => x)
            .Must(x => x.DateTo.DayNumber - x.DateFrom.DayNumber <= AppConstants.MaxRangeDays)
            .WithCopy(ValidationErrorCodes.DateRangeLimit);
    }
}
