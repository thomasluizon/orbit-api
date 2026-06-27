using FluentValidation;
using Orbit.Application.Social.Commands;

namespace Orbit.Application.Social.Validators;

public class AcceptFriendRequestCommandValidator : AbstractValidator<AcceptFriendRequestCommand>
{
    public AcceptFriendRequestCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.FriendshipId).NotEmpty();
    }
}
