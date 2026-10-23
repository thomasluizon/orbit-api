using FluentValidation;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Validators;

public sealed class ChatFollowUpValidator : AbstractValidator<ChatFollowUp>
{
    public ChatFollowUpValidator()
    {
        RuleFor(item => item.Label).NotEmpty().MaximumLength(40);
        RuleFor(item => item.Question).NotEmpty().MaximumLength(240)
            .Must(text => !text.Contains("[[", StringComparison.Ordinal)
                && !text.Contains("http", StringComparison.OrdinalIgnoreCase));
        RuleFor(item => item.Context).MaximumLength(500);
        RuleFor(item => item.Target).Must(target => target is null || ChatFollowUp.IsTarget(target));
    }
}
