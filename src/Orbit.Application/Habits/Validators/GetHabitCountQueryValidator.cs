using FluentValidation;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Habits.Validators;

public class GetHabitCountQueryValidator : AbstractValidator<GetHabitCountQuery>
{
    public GetHabitCountQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
