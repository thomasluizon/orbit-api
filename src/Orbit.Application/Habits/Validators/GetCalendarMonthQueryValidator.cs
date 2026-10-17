using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Habits.Validators;

public class GetCalendarMonthQueryValidator : AbstractValidator<GetCalendarMonthQuery>
{
    public GetCalendarMonthQueryValidator()
    {
        RuleFor(q => q.DateTo)
            .GreaterThanOrEqualTo(q => q.DateFrom)
            .WithCopy(ValidationErrorCodes.DateRangeOrderLower);

        RuleFor(q => q)
            .Must(q => q.DateTo.DayNumber - q.DateFrom.DayNumber <= AppConstants.MaxCalendarRangeDays)
            .WithCopy(ValidationErrorCodes.CalendarDateRangeLimit)
            .When(q => q.DateTo >= q.DateFrom);
    }
}
