using Orbit.Application.Chat.Validators;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private static IReadOnlyList<string>? ContextualFollowUps(IReadOnlyList<string>? questions,
        ToolExecutionAccumulator results)
    {
        if (questions is null) return null;
        var reads = results.QueryReads.Values.ToArray();
        var contextual = questions.Where(question => reads.Any(read => MentionsSubject(question, read)))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        return contextual.Length == 0 ? null : contextual;
    }

    private static IReadOnlyList<ChatFollowUp>? BuildFollowUpContext(ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator results, IReadOnlyList<string>? questions)
    {
        if (request.ClientContext?.SupportsFollowUpContext != true || questions is null)
            return null;
        var validator = new ChatFollowUpValidator();
        var items = new List<ChatFollowUp>();
        foreach (var question in questions.Take(2))
        {
            var read = results.QueryReads.Values.FirstOrDefault(read => MentionsSubject(question, read)
                && ReadEnabled(read, request.ClientContext, context.EnabledFeatureFlags));
            if (read is null) continue;
            var label = question.Length <= 40 ? question : question[..37].TrimEnd() + "...";
            var item = new ChatFollowUp(label, question, read.Read.QueryFamily, SurfaceForRead(read), read.Read.QueryId);
            if (validator.Validate(item).IsValid) items.Add(item);
        }
        return items.Count == 0 ? null : items;
    }

    private static string? SurfaceForRead(QueryReadBlock read) => read.Block switch
    {
        HabitListCard => "habits",
        GoalListCard => "goals",
        DaySummaryCard card => card.SurfaceId,
        MetricsCard card => card.SurfaceId,
        PeriodInsightCard card => card.SurfaceId,
        StreakCard card => card.SurfaceId,
        CalendarCard card => card.SurfaceId,
        RecordListCard card when card.SurfaceId is not null && ChatFollowUp.IsTarget(card.SurfaceId) => card.SurfaceId,
        AccountRowsCard card => card.SurfaceId,
        PeriodTotalCard or PeriodComparisonCard => "progress",
        _ => null
    };
}
