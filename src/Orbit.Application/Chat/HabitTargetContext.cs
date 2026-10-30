using System.Text.Json;
using Orbit.Application.Chat.Models;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;

namespace Orbit.Application.Chat;

internal static class HabitTargetContext
{
    public const string Instruction = "For an existing habit target with sharesTitle=true in habitTargets, read query_habits with for_selection=true and wait for the person's choice before calling any write tool. Never emit the write alongside that read or use every matching ID for a singular target. An explicitly requested full set uses a fresh unfiltered read with for_selection=false.";

    public static (string Context, bool HasSharedTargets) Build(IReadOnlyList<Habit> indexedHabits, IReadOnlyList<Habit> allHabits)
    {
        var sharedTitles = allHabits.Where(habit => !habit.IsCompleted)
            .GroupBy(habit => HabitNameMatcher.Normalize(habit.Title))
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var targets = indexedHabits.Where(habit => !habit.IsCompleted && sharedTitles.Contains(HabitNameMatcher.Normalize(habit.Title)))
            .Select(habit => new HabitTarget(habit.Id, habit.Title, true)).ToArray();
        return (JsonSerializer.Serialize(new
        {
            habitTargets = targets
        }, ChatStreamEvent.SerializerOptions), targets.Length > 0);
    }

    private sealed record HabitTarget(Guid Id, string Title, bool SharesTitle);
}
