using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Goals.Commands;

namespace Orbit.Application.Goals.Validators;

public class LinkHabitsToGoalCommandValidator : AbstractValidator<LinkHabitsToGoalCommand>
{
    public LinkHabitsToGoalCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.GoalId).NotEmpty();
        RuleFor(x => x.HabitIds)
            .Must(ids => ids.Count <= AppConstants.MaxHabitsPerGoal)
            .WithMessage($"A goal can have at most {AppConstants.MaxHabitsPerGoal} linked habits.");
    }
}
