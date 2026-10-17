using Orbit.Application.Common;
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
            .WithCopy(ValidationErrorCodes.PositionsRequired);

        RuleFor(x => x.Positions)
            .Must(positions => positions is null || positions.Select(p => p.HabitId).Distinct().Count() == positions.Count)
            .WithCopy(ValidationErrorCodes.PositionsUnique);

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
