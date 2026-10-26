using System.Text.Json.Serialization;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Chat;

internal sealed record HabitMonthModelPayload(DateOnly DateFrom, DateOnly DateTo, DateOnly Today,
    IReadOnlyList<HabitMonthModelRow> Habits, int OmittedHabits)
{
    private const int MaxRows = 40;
    public string Instruction => "Only listed habits have model facts. Titles may be shortened. "
        + "Do not infer facts about omitted habits or retry with narrower dates. "
        + "The client calendar block contains the complete habits and daily data for this window.";

    public static HabitMonthModelPayload From(HabitMonthCard card)
    {
        var total = card.Calendar.Habits.Sum(habit => 1 + CountChildren(habit.Children));
        var rows = card.Calendar.Habits.SelectMany(habit => Rows(habit.Id, habit.Title, habit.IsBadHabit, habit.IsFlexible,
            habit.ScheduledDates, habit.Children, card)).Take(MaxRows).ToList();
        var payload = new HabitMonthModelPayload(card.DateFrom, card.DateTo, card.Today, rows, total - rows.Count);
        while (!ModelPayloadBudget.Fits(payload, reservedCharacters: 1))
        {
            rows.RemoveAt(rows.Count - 1);
            payload = payload with { OmittedHabits = total - rows.Count };
        }
        return payload;
    }

    private static int CountChildren(IReadOnlyList<HabitScheduleChildItem> children) =>
        children.Sum(child => 1 + CountChildren(child.Children));

    private static IEnumerable<HabitMonthModelRow> Rows(Guid id, string title, bool isBadHabit, bool isFlexible,
        IReadOnlyList<DateOnly> scheduledDates, IReadOnlyList<HabitScheduleChildItem> children, HabitMonthCard card)
    {
        var logs = (card.Calendar.Logs.GetValueOrDefault(id) ?? [])
            .Where(log => log.Date >= card.DateFrom && log.Date <= card.DateTo).ToArray();
        var completed = logs.Where(log => log.Value > 0).Select(log => log.Date).ToHashSet();
        var skipped = logs.Where(log => log.Value == 0 && !completed.Contains(log.Date))
            .Select(log => log.Date).ToHashSet();
        var scheduled = scheduledDates.Where(date => date >= card.DateFrom && date <= card.DateTo).Distinct().ToArray();
        var missed = isBadHabit ? 0 : scheduled.Count(date => date < card.Today
            && !completed.Contains(date) && !skipped.Contains(date));
        yield return new(id, ModelPayloadBudget.ShortName(title), scheduled.Length, isBadHabit ? 0 : completed.Count, skipped.Count, missed,
            isBadHabit, isFlexible, isBadHabit ? completed.Count : 0);
        foreach (var child in children)
            foreach (var row in Rows(child.Id, child.Title, child.IsBadHabit, child.IsFlexible,
                child.ScheduledDates, child.Children, card))
                yield return row;
    }
}

internal sealed record HabitMonthModelRow(Guid Id, string Title, int Scheduled, int Completed, int Skipped, int Missed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsBadHabit,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsFlexible,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Slips);
