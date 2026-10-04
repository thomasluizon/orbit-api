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
            .WithCopy(ValidationErrorCodes.ChallengeTargetPositive);

        RuleFor(x => x.TargetCount)
            .Null()
            .When(x => x.Type == ChallengeType.StreakTogether)
            .WithCopy(ValidationErrorCodes.StreakChallengeTargetAbsent);

        RuleFor(x => x.PeriodEndUtc)
            .NotNull()
            .When(x => x.Type == ChallengeType.CoopGoal)
            .WithCopy(ValidationErrorCodes.ChallengeEndDateRequired);

        RuleFor(x => x.PeriodEndUtc)
            .GreaterThanOrEqualTo(x => x.PeriodStartUtc)
            .When(x => x.PeriodEndUtc.HasValue);

        RuleFor(x => x.LinkedHabitIds)
            .NotEmpty()
            .WithCopy(ValidationErrorCodes.ChallengeHabitsRequired);

        RuleFor(x => x.LinkedHabitIds)
            .Must(ids => ids.Count <= AppConstants.MaxHabitsPerChallengeParticipant)
            .WithCopy(ValidationErrorCodes.ChallengeHabitLimit);

        RuleForEach(x => x.LinkedHabitIds).NotEmpty();

        RuleFor(x => x.InvitedFriendUserIds)
            .Must(ids => ids.Count <= AppConstants.MaxChallengeParticipants - 1)
            .WithCopy(ValidationErrorCodes.ChallengeInviteLimit);
    }
}
