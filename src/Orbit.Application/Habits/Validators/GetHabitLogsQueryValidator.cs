using FluentValidation;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Habits.Validators;

public class GetHabitLogsQueryValidator : AbstractValidator<GetHabitLogsQuery>
{
    public GetHabitLogsQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.HabitId).NotEmpty();
    }
}
