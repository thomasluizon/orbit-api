using Microsoft.EntityFrameworkCore;
using Orbit.Application.Chat;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public sealed record BulkHabitFilter(
    bool All,
    IReadOnlyList<Guid> HabitIds,
    bool IncludeCompleted = false,
    string? Tag = null,
    string? Search = null,
    bool? IsGeneral = null,
    bool? IsBadHabit = null,
    FrequencyUnit? Frequency = null,
    bool OneTime = false,
    bool? IsCompleted = null,
    bool? HasEmoji = null)
{
    public bool HasSelector =>
        All || HabitIds.Count > 0 || !string.IsNullOrWhiteSpace(Tag) || !string.IsNullOrWhiteSpace(Search)
        || IsGeneral.HasValue || IsBadHabit.HasValue || Frequency.HasValue || OneTime || IsCompleted.HasValue || HasEmoji.HasValue;
}

internal static class BulkHabitSelection
{
    public static async Task<IReadOnlyList<Habit>> LoadAsync(
        IGenericRepository<Habit> habitRepository,
        Guid userId,
        BulkHabitFilter filter,
        CancellationToken cancellationToken)
    {
        var habits = await habitRepository.FindAsync(
            habit => habit.UserId == userId
                && (filter.IsCompleted.HasValue
                    ? habit.IsCompleted == filter.IsCompleted.Value
                    : filter.IncludeCompleted || !habit.IsCompleted),
            query => query.Include(habit => habit.Tags),
            cancellationToken);

        return Select(habits, filter);
    }

    internal static IReadOnlyList<Habit> Select(IEnumerable<Habit> habits, BulkHabitFilter filter)
    {
        var idSet = filter.HabitIds.ToHashSet();
        var tag = filter.Tag?.Trim();
        var search = filter.Search?.Trim();

        return habits
            .Where(habit => filter.IsCompleted.HasValue ? habit.IsCompleted == filter.IsCompleted.Value
                : filter.IncludeCompleted || !habit.IsCompleted)
            .Where(habit => filter.All || idSet.Count == 0 || idSet.Contains(habit.Id))
            .Where(habit => string.IsNullOrWhiteSpace(tag)
                || habit.Tags.Any(candidate => candidate.Name.Equals(tag, StringComparison.OrdinalIgnoreCase)))
            .Where(habit => string.IsNullOrWhiteSpace(search)
                || HabitNameMatcher.Matches(habit.Title, search))
            .Where(habit => filter.IsGeneral is null || (habit.Schedule == HabitSchedule.General) == filter.IsGeneral)
            .Where(habit => filter.IsBadHabit is null || habit.IsBadHabit == filter.IsBadHabit)
            .Where(habit => !filter.OneTime || habit.Schedule is HabitSchedule.OneTime or HabitSchedule.General)
            .Where(habit => filter.HasEmoji is null || !string.IsNullOrWhiteSpace(habit.Emoji) == filter.HasEmoji)
            .Where(habit => filter.Frequency is null || habit.FrequencyUnit == filter.Frequency)
            .OrderBy(habit => habit.Position ?? int.MaxValue)
            .ThenBy(habit => habit.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
