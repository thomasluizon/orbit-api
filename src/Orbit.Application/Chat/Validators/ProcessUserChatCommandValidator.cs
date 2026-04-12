using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Validators;

public class ProcessUserChatCommandValidator : AbstractValidator<ProcessUserChatCommand>
{
    public ProcessUserChatCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.Message)
            .NotEmpty()
            .WithMessage("Message cannot be empty.")
            .MaximumLength(AppConstants.MaxChatMessageLength)
            .WithMessage($"Message cannot exceed {AppConstants.MaxChatMessageLength} characters.");

        RuleFor(x => x.History)
            .Must(history => history is null || history.Count <= AppConstants.MaxChatHistoryMessages)
            .WithMessage($"Chat history cannot exceed {AppConstants.MaxChatHistoryMessages} messages.");

        RuleForEach(x => x.History)
            .Must(message => ChatHistoryMessage.IsSupportedRole(message.Role))
            .WithMessage("Chat history contains an invalid role.");

        RuleForEach(x => x.History)
            .Must(message => !string.IsNullOrWhiteSpace(message.Content) &&
                             message.Content.Length <= AppConstants.MaxChatHistoryMessageLength)
            .WithMessage($"Chat history messages must be between 1 and {AppConstants.MaxChatHistoryMessageLength} characters.");
    }
}
