using FluentValidation;
using Orbit.Application.Common;

namespace Orbit.Application.Gamification.Queries;

public class GetStreakHistoryQueryValidator : AbstractValidator<GetStreakHistoryQuery>
{
    public GetStreakHistoryQueryValidator()
    {
        RuleFor(x => x.DateFrom)
            .LessThanOrEqualTo(x => x.DateTo)
            .WithCopy(ValidationErrorCodes.DateRangeOrder);

        RuleFor(x => x)
            .Must(x => x.DateTo.DayNumber - x.DateFrom.DayNumber <= AppConstants.MaxRangeDays)
            .WithCopy(ValidationErrorCodes.DateRangeLimit);
    }
}
