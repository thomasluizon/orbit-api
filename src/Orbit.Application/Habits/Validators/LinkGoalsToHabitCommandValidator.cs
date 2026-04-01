using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class LinkGoalsToHabitCommandValidator : AbstractValidator<LinkGoalsToHabitCommand>
{
    public LinkGoalsToHabitCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.HabitId)
            .NotEmpty();

        RuleFor(x => x.GoalIds)
            .Must(ids => ids.Count <= AppConstants.MaxGoalsPerHabit)
            .WithMessage($"A habit can have at most {AppConstants.MaxGoalsPerHabit} linked goals.");
    }
}
