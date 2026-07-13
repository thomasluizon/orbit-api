using FluentValidation;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Habits.Validators;

public class GetHabitFullDetailQueryValidator : AbstractValidator<GetHabitFullDetailQuery>
{
    public GetHabitFullDetailQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.HabitId).NotEmpty();
    }
}
