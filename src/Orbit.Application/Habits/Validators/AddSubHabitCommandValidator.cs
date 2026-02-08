using FluentValidation;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class AddSubHabitCommandValidator : AbstractValidator<AddSubHabitCommand>
{
    public AddSubHabitCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.HabitId)
            .NotEmpty();

        RuleFor(x => x.Title)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.SortOrder)
            .GreaterThanOrEqualTo(0);
    }
}
