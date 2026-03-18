using FluentValidation;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Habits.Validators;

public class GetHabitScheduleQueryValidator : AbstractValidator<GetHabitScheduleQuery>
{
    private const int MaxRangeDays = 366;

    public GetHabitScheduleQueryValidator()
    {
        RuleFor(q => q.DateTo)
            .GreaterThanOrEqualTo(q => q.DateFrom)
            .WithMessage("dateTo must be >= dateFrom");

        RuleFor(q => q)
            .Must(q => q.DateTo.DayNumber - q.DateFrom.DayNumber <= MaxRangeDays)
            .WithMessage($"Date range must not exceed {MaxRangeDays} days");

        RuleFor(q => q.Page)
            .GreaterThanOrEqualTo(1);

        RuleFor(q => q.PageSize)
            .InclusiveBetween(1, 100);
    }
}
