using FluentValidation;
using Orbit.Application.Challenges.Commands;
using Orbit.Application.Common;

namespace Orbit.Application.Challenges.Validators;

public class SetChallengeHabitsCommandValidator : AbstractValidator<SetChallengeHabitsCommand>
{
    public SetChallengeHabitsCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.ChallengeId).NotEmpty();

        RuleFor(x => x.HabitIds)
            .NotEmpty()
            .WithMessage("Link at least one of your habits to the challenge.");

        RuleFor(x => x.HabitIds)
            .Must(ids => ids.Count <= AppConstants.MaxHabitsPerChallengeParticipant)
            .WithMessage($"You can link at most {AppConstants.MaxHabitsPerChallengeParticipant} habits.");

        RuleForEach(x => x.HabitIds).NotEmpty();
    }
}
