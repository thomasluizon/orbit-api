using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Habits.Validators;

public class GetHabitScheduleQueryValidator : AbstractValidator<GetHabitScheduleQuery>
{
    public GetHabitScheduleQueryValidator()
    {
        // Both null = "all habits" view. Both set = date-range query.
        // Only reject when one is set but the other is missing.
        RuleFor(q => q.DateFrom)
            .NotNull()
            .WithMessage("dateFrom is required")
            .When(q => q.IsGeneral != true && q.DateTo.HasValue);

        RuleFor(q => q.DateTo)
            .NotNull()
            .WithMessage("dateTo is required")
            .When(q => q.IsGeneral != true && q.DateFrom.HasValue);

        RuleFor(q => q.DateTo)
            .GreaterThanOrEqualTo(q => q.DateFrom!.Value)
            .WithMessage("dateTo must be >= dateFrom")
            .When(q => q.IsGeneral != true && q.DateFrom.HasValue && q.DateTo.HasValue);

        RuleFor(q => q)
            .Must(q => q.DateTo!.Value.DayNumber - q.DateFrom!.Value.DayNumber <= AppConstants.MaxRangeDays)
            .WithMessage($"Date range must not exceed {AppConstants.MaxRangeDays} days")
            .When(q => q.IsGeneral != true && q.DateFrom.HasValue && q.DateTo.HasValue);

        RuleFor(q => q.Page)
            .GreaterThanOrEqualTo(1);

        RuleFor(q => q.PageSize)
            .InclusiveBetween(1, AppConstants.MaxPageSize);
    }
}
