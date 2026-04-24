using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Habits.Queries;

public record HabitDetailResponse(
    Guid Id,
    string Title,
    string? Description,
    Domain.Enums.FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    bool IsBadHabit,
    bool IsCompleted,
    bool IsGeneral,
    bool IsFlexible,
    DateOnly DueDate,
    TimeOnly? DueTime,
    TimeOnly? DueEndTime,
    DateOnly? EndDate,
    IReadOnlyList<DayOfWeek> Days,
    int? Position,
    bool ReminderEnabled,
    IReadOnlyList<int> ReminderTimes,
    IReadOnlyList<ScheduledReminderTime> ScheduledReminders,
    IReadOnlyList<ChecklistItem> ChecklistItems,
    DateTime CreatedAtUtc,
    IReadOnlyList<HabitChildResponse> Children,
    string? Emoji = null);

public record GetHabitByIdQuery(Guid UserId, Guid HabitId) : IRequest<Result<HabitDetailResponse>>;

public class GetHabitByIdQueryHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IUserDateService userDateService) : IRequestHandler<GetHabitByIdQuery, Result<HabitDetailResponse>>
{
    public async Task<Result<HabitDetailResponse>> Handle(GetHabitByIdQuery request, CancellationToken cancellationToken)
    {
        // Load the specific habit directly by ID + UserId (no need to load all user habits)
        var habits = await habitRepository.FindAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            q => q.Include(h => h.Children).ThenInclude(c => c.Children),
            cancellationToken);

        var habit = habits.Count > 0 ? habits[0] : null;
        if (habit is null)
            return Result.Failure<HabitDetailResponse>(ErrorMessages.HabitNotFound, ErrorCodes.HabitNotFound);

        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var descendantLogsByHabitId = await HabitDetailDescendantLogLoader.LoadAsync(
            habitLogRepository,
            habit,
            userToday,
            cancellationToken);
        var children = HabitDetailChildMapper.MapChildren(habit, userToday, descendantLogsByHabitId);

        return Result.Success(new HabitDetailResponse(
            habit.Id,
            habit.Title,
            habit.Description,
            habit.FrequencyUnit,
            habit.FrequencyQuantity,
            habit.IsBadHabit,
            habit.IsCompleted,
            habit.IsGeneral,
            habit.IsFlexible,
            habit.DueDate,
            habit.DueTime,
            habit.DueEndTime,
            habit.EndDate,
            habit.Days.ToList(),
            habit.Position,
            habit.ReminderEnabled,
            habit.ReminderTimes,
            habit.ScheduledReminders,
            habit.ChecklistItems,
            habit.CreatedAtUtc,
            children,
            Emoji: habit.Emoji));
    }
}

internal static class HabitDetailDescendantLogLoader
{
    private const int DescendantLogLookbackDays = AppConstants.MaxRangeDays;

    public static async Task<IReadOnlyDictionary<Guid, IReadOnlyCollection<HabitLog>>> LoadAsync(
        IGenericRepository<HabitLog> habitLogRepository,
        Habit root,
        DateOnly userToday,
        CancellationToken cancellationToken)
    {
        var descendantIds = new HashSet<Guid>();
        CollectDescendantIds(root, descendantIds);
        if (descendantIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyCollection<HabitLog>>();

        var descendantLogCutoff = userToday.AddDays(-DescendantLogLookbackDays);
        var descendantLogs = await habitLogRepository.FindAsync(
            l => descendantIds.Contains(l.HabitId)
                && l.Date >= descendantLogCutoff
                && l.Date < userToday,
            cancellationToken);

        return descendantLogs
            .GroupBy(log => log.HabitId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyCollection<HabitLog>)group.ToList());
    }

    private static void CollectDescendantIds(Habit parent, ISet<Guid> descendantIds)
    {
        foreach (var child in parent.Children)
        {
            if (descendantIds.Add(child.Id))
                CollectDescendantIds(child, descendantIds);
        }
    }
}

internal static class HabitDetailChildMapper
{
    public static List<HabitChildResponse> MapChildren(
        Habit parent,
        DateOnly userToday,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<HabitLog>>? descendantLogsByHabitId = null) =>
        parent.Children
            .OrderBy(c => c.Position ?? int.MaxValue)
            .ThenBy(c => c.CreatedAtUtc)
            .Select(c => MapChild(c, userToday, descendantLogsByHabitId))
            .ToList();

    private static HabitChildResponse MapChild(
        Habit child,
        DateOnly userToday,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<HabitLog>>? descendantLogsByHabitId) => new(
        child.Id,
        child.Title,
        child.Description,
        child.FrequencyUnit,
        child.FrequencyQuantity,
        child.IsBadHabit,
        child.IsCompleted,
        child.IsGeneral,
        child.IsFlexible,
        child.Days.ToList(),
        child.DueDate,
        child.DueTime,
        child.DueEndTime,
        child.EndDate,
        child.Position,
        child.ChecklistItems,
        DetermineOverdueStatus(child, userToday, descendantLogsByHabitId),
        MapChildren(child, userToday, descendantLogsByHabitId),
        Emoji: child.Emoji);

    private static bool DetermineOverdueStatus(
        Habit habit,
        DateOnly userToday,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<HabitLog>>? descendantLogsByHabitId)
    {
        if (habit.IsCompleted || habit.IsFlexible || habit.IsBadHabit)
            return false;

        if (habit.FrequencyUnit == null)
        {
            return habit.DueDate < userToday
                && (!habit.EndDate.HasValue || habit.EndDate.Value >= userToday);
        }

        if (HabitScheduleService.GetScheduledDates(habit, userToday, userToday).Contains(userToday))
            return false;

        return HasMissedRecentOccurrence(habit, userToday, descendantLogsByHabitId);
    }

    private static bool HasMissedRecentOccurrence(
        Habit habit,
        DateOnly userToday,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<HabitLog>>? descendantLogsByHabitId)
    {
        var qty = habit.FrequencyQuantity ?? 1;
        var lookbackDays = HabitScheduleService.GetLookbackDays(habit.FrequencyUnit, qty);
        var lookbackStart = userToday.AddDays(-lookbackDays);

        if (habit.DueDate > lookbackStart)
            lookbackStart = habit.DueDate;

        var pastDates = HabitScheduleService.GetScheduledDates(habit, lookbackStart, userToday.AddDays(-1));
        var logDates = GetLogs(habit, descendantLogsByHabitId).Select(l => l.Date).ToHashSet();

        return pastDates.Any(d => !logDates.Contains(d));
    }

    private static IReadOnlyCollection<HabitLog> GetLogs(
        Habit habit,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<HabitLog>>? descendantLogsByHabitId)
    {
        if (descendantLogsByHabitId is null)
            return habit.Logs;

        return descendantLogsByHabitId.TryGetValue(habit.Id, out var logs)
            ? logs
            : Array.Empty<HabitLog>();
    }
}
