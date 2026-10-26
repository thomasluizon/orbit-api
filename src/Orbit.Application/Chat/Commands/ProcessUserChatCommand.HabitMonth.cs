using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private static ResponseCards ComposeHabitMonth(ResponseCards cards, ProcessUserChatCommand request,
        ChatContext context, ToolExecutionAccumulator results)
    {
        if (results.OperationResults is not [
            {
                OperationId: "get_habit_month", Status: AgentOperationStatus.Succeeded,
                ReadBlock: { Block: HabitMonthCard month } read
            }]) return cards;
        if (ReadEnabled(read, request.ClientContext, context.EnabledFeatureFlags))
        {
            var intro = HabitMonthCardBuilder.Introduction(month);
            return new(intro, Parts: [new ChatTextPart(intro), new ChatBlockPart("habitMonth", month)])
            { ServerComposedText = true };
        }
        var text = ReadFallback(month, context.User?.Language, context.User?.TimeZone);
        return new(text, Parts: request.ClientContext?.SupportsParts == true ? [new ChatTextPart(text)] : null)
        { ServerComposedText = true };
    }
}
