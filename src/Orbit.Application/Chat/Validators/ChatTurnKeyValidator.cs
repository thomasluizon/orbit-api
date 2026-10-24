using FluentValidation;

namespace Orbit.Application.Chat.Validators;

public sealed class ChatTurnKeyValidator : AbstractValidator<string>
{
    public ChatTurnKeyValidator()
    {
        RuleFor(key => key).NotEmpty().MaximumLength(200).Must(key => !string.IsNullOrWhiteSpace(key));
    }
}
