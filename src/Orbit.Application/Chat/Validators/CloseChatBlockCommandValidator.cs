using FluentValidation;
using Orbit.Application.Chat.Commands;

namespace Orbit.Application.Chat.Validators;

public sealed class CloseChatBlockCommandValidator : AbstractValidator<CloseChatBlockCommand>
{
    public CloseChatBlockCommandValidator()
    {
        RuleFor(request => request.UserId).NotEmpty();
        RuleFor(request => request.BlockId).NotEmpty();
    }
}
