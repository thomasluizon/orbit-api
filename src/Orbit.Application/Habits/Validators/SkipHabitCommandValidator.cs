using FluentValidation;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class SkipHabitCommandValidator : AbstractValidator<SkipHabitCommand>
{
    public SkipHabitCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.HabitId)
            .NotEmpty();

        RuleFor(x => x.Date)
            .NotEqual(default(DateOnly))
            .When(x => x.Date.HasValue)
            .WithMessage("Date must be a valid calendar date");
    }
}
