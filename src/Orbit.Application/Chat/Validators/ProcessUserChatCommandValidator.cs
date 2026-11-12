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
            .WithCopy(ErrorCodes.MessageTooLong, AppConstants.MaxChatMessageLength)
            .MaximumLength(AppConstants.MaxChatMessageLength)
            .WithCopy(ErrorCodes.MessageTooLong, AppConstants.MaxChatMessageLength);

        When(x => x.BlockAnswer is not null, () =>
            RuleFor(x => x.BlockAnswer!).SetValidator(new AiBlockAnswerValidator()));

        RuleFor(x => x.ClientContext)
            .Must(context => context?.ConversationId is null || context.ConversationId != Guid.Empty)
            .WithCopy(ErrorCodes.InvalidClientContext);

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
            .WithCopy(ErrorCodes.ChatHistoryTooLarge);

        RuleForEach(x => x.History)
            .Must(message => message is not null && ChatHistoryMessage.IsSupportedRole(message.Role))
            .WithCopy(ErrorCodes.InvalidChatHistory);

        RuleForEach(x => x.History)
            .Must(message => message is not null && !string.IsNullOrWhiteSpace(message.Content) &&
                             message.Content.Length <= AppConstants.MaxChatHistoryMessageLength)
            .WithCopy(ErrorCodes.InvalidChatHistory);
    }
}
