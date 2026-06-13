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
            .WithMessage("dateTo must be >= dateFrom");

        RuleFor(q => q)
            .Must(q => q.DateTo.DayNumber - q.DateFrom.DayNumber <= AppConstants.MaxCalendarRangeDays)
            .WithMessage($"Date range must not exceed {AppConstants.MaxCalendarRangeDays} days")
            .When(q => q.DateTo >= q.DateFrom);
    }
}
