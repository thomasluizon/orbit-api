using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Habits.Validators;

public class GetHabitScheduleQueryValidator : AbstractValidator<GetHabitScheduleQuery>
{
    public GetHabitScheduleQueryValidator()
    {
        RuleFor(q => q.DateFrom)
            .NotNull()
            .WithCopy(ValidationErrorCodes.DateFromRequired)
            .When(q => q.IsGeneral != true && q.DateTo.HasValue);

        RuleFor(q => q.DateTo)
            .NotNull()
            .WithCopy(ValidationErrorCodes.DateToRequired)
            .When(q => q.IsGeneral != true && q.DateFrom.HasValue);

        RuleFor(q => q.DateTo)
            .GreaterThanOrEqualTo(q => q.DateFrom!.Value)
            .WithCopy(ValidationErrorCodes.DateRangeOrderLower)
            .When(q => q.IsGeneral != true && q.DateFrom.HasValue && q.DateTo.HasValue);

        RuleFor(q => q)
            .Must(q => q.DateTo!.Value.DayNumber - q.DateFrom!.Value.DayNumber <= AppConstants.MaxRangeDays)
            .WithCopy(ValidationErrorCodes.DateRangeLimitShort)
            .When(q => q.IsGeneral != true && q.DateFrom.HasValue && q.DateTo.HasValue);

        RuleFor(q => q.Page)
            .GreaterThanOrEqualTo(1);

        RuleFor(q => q.PageSize)
            .InclusiveBetween(1, AppConstants.MaxPageSize);
    }
}
