using FluentValidation;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Habits.Validators;

public class GetHabitMetricsQueryValidator : AbstractValidator<GetHabitMetricsQuery>
{
    public GetHabitMetricsQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.HabitId).NotEmpty();
        RuleFor(x => x.DateTo).Must((q, to) => q.DateFrom.HasValue == to.HasValue
            && (!to.HasValue || to >= q.DateFrom && to.Value.DayNumber - q.DateFrom!.Value.DayNumber <= 3659));
    }
}
