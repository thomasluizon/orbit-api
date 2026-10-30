using Orbit.Application.Chat.Models;
using Orbit.Application.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private ResponseCards? CapacityRefusalAnswer(ToolExecutionAccumulator results,
        ProcessUserChatCommand request, ChatContext context)
    {
        if (CapacityRefusalText(results, context) is null) return null;
        var cards = ComposeAnswer(FollowUpDirective.Parse(null), false, context.Language, request, context, results);
        var response = ChatResponseNegotiation.ForClient(new ChatResponse(cards.AiMessage, [], Parts: cards.Parts),
            request.ClientContext, context.EnabledFeatureFlags, context.Language, context.User?.TimeZone, context.UserToday);
        return new ResponseCards(response.AiMessage, Parts: response.Parts) { ServerComposedText = true };
    }

    private static string? CapacityRefusalText(ToolExecutionAccumulator results, ChatContext context)
    {
        if (results.PendingOperations.Count > 0 || results.ActionResults.Count > 0 || results.AskBlock is not null)
            return null;
        var failure = results.OperationResults.LastOrDefault(operation => operation is
        {
            Status: AgentOperationStatus.Failed,
            ErrorCode: ErrorCodes.HabitLimitReached,
            Payload: HabitCapacityFailure
        })?.Payload as HabitCapacityFailure;
        if (failure is null || !ErrorCopy.TryResolve(failure.ErrorCode, LocaleHelper.IsPortuguese(context.Language),
            failure.Arguments, out var reason))
            return null;
        return reason + " " + ErrorCopy.Resolve("chat.answer.habitCapacityNextStep", LocaleHelper.IsPortuguese(context.Language));
    }
}
