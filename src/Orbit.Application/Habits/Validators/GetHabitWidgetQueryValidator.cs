using FluentValidation;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Habits.Validators;

public class GetHabitWidgetQueryValidator : AbstractValidator<GetHabitWidgetQuery>
{
    public GetHabitWidgetQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
