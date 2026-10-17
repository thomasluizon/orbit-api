using Orbit.Application.Habits.Queries;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public record PeriodInsightCard(
    string Period,
    DateOnly DateFrom,
    DateOnly DateTo,
    int CompletionRate,
    int ActiveDays,
    int PeriodDays,
    int TotalCompletions,
    int TotalScheduled,
    int CurrentStreak,
    int BestStreak,
    IReadOnlyList<RetrospectiveHabitStat> TopHabits,
    IReadOnlyList<RetrospectiveHabitStat> NeedsAttention,
    RetrospectiveNarrative Narrative,
    CompletionSeries? Series,
    string SurfaceId = "progress");

public static class PeriodInsightCardBuilder
{
    public const string Directive = "[[orbit:insight]]";
    public const string PromptInstruction = "For a successful get_retrospective answer, write one line without restating figures, then emit [[orbit:insight]] before optional follow-ups.";

    public static PeriodInsightCard? Build(RetrospectiveResponse? response)
    {
        if (response?.DateFrom is not { } dateFrom || response.DateTo is not { } dateTo)
            return null;

        var metrics = response.Metrics;
        return new PeriodInsightCard(
            response.Period, dateFrom, dateTo, metrics.CompletionRate, metrics.ActiveDays,
            metrics.PeriodDays, metrics.TotalCompletions, metrics.TotalScheduled,
            metrics.CurrentStreak, metrics.BestStreak, metrics.TopHabits.Take(3).ToList(),
            metrics.NeedsAttention.Take(3).ToList(), response.Narrative, metrics.CompletionSeries);
    }
}
