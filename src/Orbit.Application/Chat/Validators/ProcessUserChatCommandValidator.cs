using FluentValidation;
using Orbit.Application.Chat.Commands;

namespace Orbit.Application.Chat.Validators;

public class ProcessUserChatCommandValidator : AbstractValidator<ProcessUserChatCommand>
{
    private const int MaxMessageLength = 4000;

    public ProcessUserChatCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.Message)
            .NotEmpty()
            .WithMessage("Message cannot be empty.")
            .MaximumLength(MaxMessageLength)
            .WithMessage($"Message cannot exceed {MaxMessageLength} characters.");
    }
}
