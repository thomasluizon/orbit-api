using FluentValidation;
using Orbit.Application.Challenges.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Enums;

namespace Orbit.Application.Challenges.Validators;

public class CreateChallengeCommandValidator : AbstractValidator<CreateChallengeCommand>
{
    public CreateChallengeCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();

        RuleFor(x => x.Title).NotEmpty().MaximumLength(AppConstants.MaxChallengeTitleLength);

        RuleFor(x => x.Description).MaximumLength(AppConstants.MaxChallengeDescriptionLength);

        RuleFor(x => x.TargetCount)
            .NotNull().GreaterThan(0)
            .When(x => x.Type == ChallengeType.CoopGoal)
            .WithMessage("A goal challenge requires a target count greater than 0.");

        RuleFor(x => x.TargetCount)
            .Null()
            .When(x => x.Type == ChallengeType.StreakTogether)
            .WithMessage("A streak challenge cannot have a target count.");

        RuleFor(x => x.PeriodEndUtc)
            .NotNull()
            .When(x => x.Type == ChallengeType.CoopGoal)
            .WithMessage("A goal challenge requires an end date.");

        RuleFor(x => x.PeriodEndUtc)
            .GreaterThanOrEqualTo(x => x.PeriodStartUtc)
            .When(x => x.PeriodEndUtc.HasValue);

        RuleFor(x => x.LinkedHabitIds)
            .NotEmpty()
            .WithMessage("Link at least one of your habits to the challenge.");

        RuleFor(x => x.LinkedHabitIds)
            .Must(ids => ids.Count <= AppConstants.MaxHabitsPerChallengeParticipant)
            .WithMessage($"You can link at most {AppConstants.MaxHabitsPerChallengeParticipant} habits.");

        RuleForEach(x => x.LinkedHabitIds).NotEmpty();

        RuleFor(x => x.InvitedFriendUserIds)
            .Must(ids => ids.Count <= AppConstants.MaxChallengeParticipants - 1)
            .WithMessage($"You can invite at most {AppConstants.MaxChallengeParticipants - 1} friends.");
    }
}
