using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Habits.Validators;

public class GetDailySummaryQueryValidator : AbstractValidator<GetDailySummaryQuery>
{
    public GetDailySummaryQueryValidator()
    {
        RuleFor(q => q.DateTo)
            .GreaterThanOrEqualTo(q => q.DateFrom)
            .WithCopy(ValidationErrorCodes.DateRangeOrderLower);

        RuleFor(q => q)
            .Must(q => q.DateTo.DayNumber - q.DateFrom.DayNumber <= AppConstants.MaxRangeDays)
            .WithCopy(ValidationErrorCodes.DateRangeLimitShort)
            .When(q => q.DateTo >= q.DateFrom);
    }
}
