using FluentValidation;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class LogHabitCommandValidator : AbstractValidator<LogHabitCommand>
{
    public LogHabitCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.HabitId)
            .NotEmpty();

        RuleFor(x => x.Value)
            .GreaterThan(0)
            .When(x => x.Value.HasValue)
            .WithMessage("Log value must be positive");

        RuleFor(x => x.Note)
            .MaximumLength(500)
            .When(x => x.Note is not null)
            .WithMessage("Note must not exceed 500 characters");
    }
}
