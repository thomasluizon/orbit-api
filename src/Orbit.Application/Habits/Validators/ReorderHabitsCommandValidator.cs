using FluentValidation;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class ReorderHabitsCommandValidator : AbstractValidator<ReorderHabitsCommand>
{
    public ReorderHabitsCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.Positions)
            .NotEmpty()
            .WithMessage("Positions list must not be empty");

        RuleFor(x => x.Positions)
            .Must(positions => positions is null || positions.Select(p => p.HabitId).Distinct().Count() == positions.Count)
            .WithMessage("Positions list must not contain duplicate habit IDs");

        RuleForEach(x => x.Positions)
            .ChildRules(position =>
            {
                position.RuleFor(p => p.HabitId)
                    .NotEmpty();

                position.RuleFor(p => p.Position)
                    .GreaterThanOrEqualTo(0);
            });
    }
}
