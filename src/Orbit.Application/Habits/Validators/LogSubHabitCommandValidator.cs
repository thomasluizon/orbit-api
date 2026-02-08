using FluentValidation;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class LogSubHabitCommandValidator : AbstractValidator<LogSubHabitCommand>
{
    public LogSubHabitCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.HabitId)
            .NotEmpty();

        RuleFor(x => x.Completions)
            .NotEmpty()
            .WithMessage("At least one sub-habit completion is required");

        RuleForEach(x => x.Completions)
            .ChildRules(completion =>
            {
                completion.RuleFor(c => c.SubHabitId)
                    .NotEmpty();
            });
    }
}
