using Orbit.Domain.Common;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Interfaces;

/// <summary>
/// One daily-summary generation: the warm narrative <paramref name="Summary"/> shown in the
/// Astra card. <paramref name="Insight"/> is retained on the contract only for legacy mobile
/// clients (pre-July-2026 builds) that still parse and render a nudge chip; it is always populated
/// empty and no longer generated.
/// </summary>
public record DailySummaryContent(string Summary, string Insight);

/// <summary>The point-in-time user state a daily summary is generated for.</summary>
public record DailySummaryContext(
    DateOnly DateFrom,
    DateOnly DateTo,
    DateOnly UserToday,
    string Language,
    TimeOnly? CurrentLocalTime,
    int CurrentStreak,
    int StreakFreezesAccumulated,
    IReadOnlyDictionary<Guid, DateOnly> LastBadHabitSlipDates,
    int WeekStartDay = 1,
    IReadOnlySet<Guid>? ResolvedDueDateHabitIds = null,
    Guid? UserId = null,
    IReadOnlyList<DailySummaryPendingHabit>? PendingHabits = null);

public record DailySummaryPendingHabit(
    Guid Id,
    string Title,
    Guid? ParentId,
    string? ParentTitle,
    DateOnly DueDate,
    TimeOnly? DueTime,
    TimeOnly? DueEndTime,
    int? Position,
    int DayOrder,
    bool IsOverdue,
    bool HasPendingChildren,
    int CompletedCount,
    int TotalCount);

public interface ISummaryService
{
    Task<Result<DailySummaryContent>> GenerateSummaryAsync(
        IEnumerable<Habit> allHabits,
        DailySummaryContext context,
        CancellationToken cancellationToken = default);
}
