using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Habits.Validators;

public class GetHabitLogsQueryValidator : AbstractValidator<GetHabitLogsQuery>
{
    public GetHabitLogsQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.HabitId).NotEmpty();

        RuleFor(x => x.From).NotNull()
            .WithCopy(ValidationErrorCodes.DateFromRequired)
            .When(x => x.To.HasValue);
        RuleFor(x => x.To).NotNull()
            .WithCopy(ValidationErrorCodes.DateToRequired)
            .When(x => x.From.HasValue);

        When(x => x.From.HasValue && x.To.HasValue, () =>
        {
            RuleFor(x => x.To).GreaterThanOrEqualTo(x => x.From)
                .WithCopy(ValidationErrorCodes.DateRangeOrder);
            RuleFor(x => x.To)
                .Must((query, to) => query.From is { } from && to is { } end
                    && end.DayNumber - from.DayNumber < AppConstants.MaxRangeDays)
                .WithCopy(ValidationErrorCodes.DateRangeLimit)
                .When(x => x.To >= x.From);
        });
    }
}
