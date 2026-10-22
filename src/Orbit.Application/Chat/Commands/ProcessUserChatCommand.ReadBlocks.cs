using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private static HabitListCard? BuildHabitListFromQuery(ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator results, string scope, bool remaining)
    {
        var reads = results.OperationResults.Where(operation => operation.OperationId == "query_habits").ToList();
        if (reads.Count == 0)
            return HabitListCardBuilder.Build(
                scope == HabitListCardBuilder.ScopeToday ? context.TodayHabits : context.ActiveHabits,
                context.UserToday, scope, context.TodayFacts,
                request.ClientContext?.SupportsHabitListDoneStatus == true, remaining);
        if (reads.Count != 1 || reads[0].Status != AgentOperationStatus.Succeeded
            || reads[0].Payload is not HabitListCard queried)
            return null;
        return request.ClientContext?.SupportsHabitListDoneStatus == true
            ? queried
            : queried with
            {
                Items = queried.Items.Select(item => item.Status == HabitListCardBuilder.StatusDone
                    ? item with { Status = HabitListCardBuilder.StatusNone } : item).ToList()
            };
    }
}
