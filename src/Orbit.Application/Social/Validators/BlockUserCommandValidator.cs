using FluentValidation;
using Orbit.Application.Social.Commands;

namespace Orbit.Application.Social.Validators;

public class BlockUserCommandValidator : AbstractValidator<BlockUserCommand>
{
    public BlockUserCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.BlockedUserId).NotEmpty().NotEqual(x => x.UserId);
    }
}
