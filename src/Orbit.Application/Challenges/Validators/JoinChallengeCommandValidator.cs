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
            .WithCopy(ValidationErrorCodes.ChallengeHabitsRequired);

        RuleFor(x => x.LinkedHabitIds)
            .Must(ids => ids.Count <= AppConstants.MaxHabitsPerChallengeParticipant)
            .WithCopy(ValidationErrorCodes.ChallengeHabitLimit);

        RuleForEach(x => x.LinkedHabitIds).NotEmpty();
    }
}
