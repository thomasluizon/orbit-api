using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Social.Commands;

namespace Orbit.Application.Social.Validators;

public class SendFriendRequestCommandValidator : AbstractValidator<SendFriendRequestCommand>
{
    public SendFriendRequestCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();

        RuleFor(x => x.Handle).MaximumLength(AppConstants.HandleMaxLength);
        RuleFor(x => x.ReferralCode).MaximumLength(64);

        RuleFor(x => x)
            .Must(HaveExactlyOneIdentifier)
            .WithMessage("Provide exactly one of handle or referralCode.");
    }

    private static bool HaveExactlyOneIdentifier(SendFriendRequestCommand command) =>
        !string.IsNullOrWhiteSpace(command.Handle) ^ !string.IsNullOrWhiteSpace(command.ReferralCode);
}
