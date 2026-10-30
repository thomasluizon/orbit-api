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

        When(x => x.BlockAnswer is not null, () =>
            RuleFor(x => x.BlockAnswer!).SetValidator(new AstraBlockAnswerValidator()));

        RuleFor(x => x.ClientContext)
            .Must(context => context?.SupportsProactiveOffers != true
                || context.ConversationId is { } id && id != Guid.Empty
                    && context.SupportsParts == true && context.SupportsPendingOperationChanges == true)
            .WithCopy(ErrorCodes.InvalidClientContext);

        RuleFor(x => x.ClientContext)
            .Must(context => context?.SelectedOfferKey is null || context.SelectedOfferKey.Length is > 0 and <= 200
                && !context.SelectedOfferKey.Any(char.IsControl))
            .WithCopy(ErrorCodes.InvalidClientContext);

        RuleFor(x => x.ClientContext)
            .Must(context => context?.DismissedOfferKeys is null || context.DismissedOfferKeys.Count <= 100
                && context.DismissedOfferKeys.All(key => !string.IsNullOrWhiteSpace(key)
                    && key.Length <= 200 && !key.Any(char.IsControl)))
            .WithCopy(ErrorCodes.InvalidClientContext);

        RuleFor(x => x.ClientContext)
            .Must(context => context?.PreviousStartingStep is not { } step
                || new StartingStepArgumentsValidator().Validate(new StartingStepArguments(step.Goal, step.Step, step.Language, 1)).IsValid)
            .WithCopy(ErrorCodes.InvalidClientContext);

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
