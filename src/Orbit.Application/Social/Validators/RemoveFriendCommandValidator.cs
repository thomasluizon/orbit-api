using FluentValidation;
using Orbit.Application.Social.Commands;

namespace Orbit.Application.Social.Validators;

public class RemoveFriendCommandValidator : AbstractValidator<RemoveFriendCommand>
{
    public RemoveFriendCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.FriendUserId).NotEmpty().NotEqual(x => x.UserId);
    }
}
