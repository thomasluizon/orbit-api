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
            .WithCopy(ValidationErrorCodes.ChatMessageRequired)
            .MaximumLength(AppConstants.MaxChatMessageLength)
            .WithCopy(ValidationErrorCodes.ChatMessageLength);

        RuleFor(x => x.History)
            .Must(history => history is null || history.Count <= AppConstants.MaxChatHistoryMessages)
            .WithCopy(ValidationErrorCodes.ChatHistoryLimit);

        RuleForEach(x => x.History)
            .Must(message => ChatHistoryMessage.IsSupportedRole(message.Role))
            .WithCopy(ValidationErrorCodes.ChatHistoryRole);

        RuleForEach(x => x.History)
            .Must(message => !string.IsNullOrWhiteSpace(message.Content) &&
                             message.Content.Length <= AppConstants.MaxChatHistoryMessageLength)
            .WithCopy(ValidationErrorCodes.ChatHistoryMessageLength);
    }
}
