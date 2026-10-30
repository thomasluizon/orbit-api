using Orbit.Application.Chat.Models;
using Orbit.Application.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private static ResponseCards? CapacityRefusalAnswer(ToolExecutionAccumulator results,
        ProcessUserChatCommand request, ChatContext context)
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
        var text = reason + " " + ErrorCopy.Resolve("chat.answer.habitCapacityNextStep", LocaleHelper.IsPortuguese(context.Language));
        return new ResponseCards(text, Parts: request.ClientContext?.SupportsParts == true ? [new ChatTextPart(text)] : null)
        { ServerComposedText = true };
    }
}
