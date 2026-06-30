using FluentValidation;
using Orbit.Application.Challenges.Commands;

namespace Orbit.Application.Challenges.Validators;

public class LeaveChallengeCommandValidator : AbstractValidator<LeaveChallengeCommand>
{
    public LeaveChallengeCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.ChallengeId).NotEmpty();
    }
}
