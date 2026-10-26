using System.Globalization;
using System.Text.Json.Serialization;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public sealed record HabitMonthCard(DateOnly DateFrom, DateOnly DateTo, DateOnly Today, int WeekStartDay,
    CalendarMonthResponse Calendar, string Language = "en", string SurfaceId = "calendar",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReadBlockMetadata? Read = null);

public static class HabitMonthCardBuilder
{
    public const string Kind = "habitMonth";

    public static string Introduction(HabitMonthCard card) => LocaleHelper.IsPortuguese(card.Language)
        ? "Aqui está seu calendário de hábitos." : "Here is your habit calendar.";

    public static string Readable(HabitMonthCard card)
    {
        string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var heading = LocaleHelper.IsPortuguese(card.Language) ? "Calendário de hábitos" : "Habit calendar";
        var labels = LocaleHelper.IsPortuguese(card.Language)
            ? "Registros: positivo = conclusão, zero = pulado; em hábitos ruins, positivo = deslize."
            : "Logs: positive = completion, zero = skipped; for bad habits, positive = slip.";
        return string.Join("\n", new[] { $"{heading}: {Iso(card.DateFrom)} / {Iso(card.DateTo)}", labels }
            .Concat(card.Calendar.Habits.SelectMany(habit => Rows(habit.Title, habit.Id, habit.ScheduledDates, habit.Children, card))));
    }

    private static IEnumerable<string> Rows(string title, Guid id, IReadOnlyList<DateOnly> scheduled,
        IReadOnlyList<HabitScheduleChildItem> children, HabitMonthCard card)
    {
        var dates = string.Join(", ", scheduled.Select(date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        var logs = card.Calendar.Logs.GetValueOrDefault(id) ?? [];
        yield return $"{title}: {dates}";
        foreach (var log in logs)
            yield return $"  {log.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}: {log.Value}";
        foreach (var child in children)
            foreach (var row in Rows(child.Title, child.Id, child.ScheduledDates, child.Children, card))
                yield return row;
    }
}
