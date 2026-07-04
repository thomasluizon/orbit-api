using FluentValidation;
using Orbit.Application.Challenges.Commands;
using Orbit.Application.Common;

namespace Orbit.Application.Challenges.Validators;

public class JoinChallengeCommandValidator : AbstractValidator<JoinChallengeCommand>
{
    private const int MaxJoinCodeLength = 16;

    public JoinChallengeCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();

        RuleFor(x => x.Code).NotEmpty().MaximumLength(MaxJoinCodeLength);

        RuleFor(x => x.LinkedHabitIds)
            .NotEmpty()
            .WithMessage("Link at least one of your habits to the challenge.");

        RuleFor(x => x.LinkedHabitIds)
            .Must(ids => ids.Count <= AppConstants.MaxHabitsPerChallengeParticipant)
            .WithMessage($"You can link at most {AppConstants.MaxHabitsPerChallengeParticipant} habits.");

        RuleForEach(x => x.LinkedHabitIds).NotEmpty();
    }
}
