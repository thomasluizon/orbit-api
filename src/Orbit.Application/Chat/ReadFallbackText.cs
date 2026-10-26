using System.Globalization;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

internal sealed class ReadFallbackText(string? language, string? timeZone)
{
    private readonly bool _portuguese = LocaleHelper.IsPortuguese(language);
    private readonly CultureInfo _culture = CultureInfo.GetCultureInfo(LocaleHelper.IsPortuguese(language) ? "pt-BR" : "en-US");
    private readonly TimeZoneInfo _zone = TimeZoneHelper.FindTimeZone(timeZone);

    private string Text(string key) => ErrorCopy.TryResolve("chat.read." + key, _portuguese, [], out var text)
        ? text : ErrorCopy.Resolve("chat.read.unavailable", _portuguese);
    private string Number(decimal value) => value.ToString("0.##", _culture);
    private string Date(DateOnly date) => date.ToString(_portuguese ? "dd/MM/yyyy" : "MM/dd/yyyy", _culture);
    private string Range(DateOnly from, DateOnly to) => $"{Date(from)} {Text("to")} {Date(to)}";
    private string Window(ReadWindow window) => Range(window.From, window.To);
    private string Total(string metric, decimal value) => $"{Text(metric)}: {Number(value)}";
    private string Unit(string unit) => unit switch
    {
        "completions" => Text("positiveLogRecords"),
        "habits" or "records" or "days" => Text(unit),
        _ => unit
    };

    internal string Format(object block) => block switch
    {
        HabitListCard card => Lines(new[] { card.Date is { } date ? Date(date) : Text(card.Scope), Total("habits", card.Items.Count) }
            .Concat(card.Items.Select(item => $"{item.Title} ({Text(item.Status)})"))),
        RecordListCard { Kind: "habitHistory" } card => Lines(new[] { HistoryHeading(card) }
            .Concat(card.Items.Select(item => $"{(item.LogDate is { } date ? Date(date) : Text("notSet"))}: {Number(item.Value ?? 0)}"))),
        PeriodTotalCard card => $"{Total(card.Metric, card.Value)} ({Window(card.Metrics.Window)})",
        PeriodComparisonCard card => $"{Total(card.Metric, card.CurrentValue)} ({Window(card.Current.Window)})\n"
            + $"{Total(card.Metric, card.BaselineValue)} ({Window(card.Baseline.Window)})\n{Total("difference", card.AbsoluteDelta)} {Unit(card.Unit)}",
        MetricsCard { PeriodMetrics: { } period } card => Lines(new[]
            { card.HabitTitle, $"{Total("positiveLogRecords", period.PositiveLogRecords)} ({Window(period.Window)})",
                card.Period == "habit" ? Total("lifetimeCompletions", card.TotalCompletions) : null }),
        MetricsCard card => $"{Text(card.Period)}: {Total("positiveLogRecords", card.TotalCompletions)}",
        GoalListCard card => Lines(new[] { Total("goals", card.Items.Count) }
            .Concat(card.Items.Select(item => $"{item.Title}: {Number(item.Current)}/{Number(item.Target)} {Unit(item.Unit)}"))),
        DaySummaryCard card => $"{(card.DateTo is { } to ? Range(card.Date, to) : Date(card.Date))}: {Text("dayDone")}: {Number(card.Done)}/{Number(card.Due)}",
        StreakCard card => $"{Total("streak", card.CurrentStreak)}; {Total("level", card.Level)}; {Total("xp", card.TotalXp)}",
        HabitMonthCard card => Month(card),
        CalendarCard card => Lines(new[] { Total("events", card.Events.Count),
                card.Sync is { } sync ? $"{Text("sync")}: {Text(!sync.Enabled ? "disabled" : sync.Status)}" : null }
            .Concat(card.Events.Select(item => $"{CalendarDate(item.Start, item.IsAllDay)}: {item.Title}"))),
        PeriodInsightCard card => $"{Range(card.DateFrom, card.DateTo)}: {Text("dayDone")}: {Number(card.TotalCompletions)}/{Number(card.TotalScheduled)}; {Number(card.CompletionRate)}%",
        RecordListCard card => Lines(new[] { Total(card.Kind, card.TotalCount) }.Concat(card.Items.Select(Record))),
        AccountRowsCard card => Lines(card.Rows.Select(row => $"{Text(row.Key)}: {AccountValue(row)}")),
        _ => string.Empty
    };

    private string Record(RecordListItem item) => Lines(new[] { item.Title,
        item.Detail, item.State is { } state ? Text(state) : null,
        item.Count is { } count ? Total("records", count) : null }.Where(value => !string.IsNullOrWhiteSpace(value)), "; ");

    private string HistoryHeading(RecordListCard card)
    {
        var heading = Total("habitHistory", card.TotalCount);
        if (card.Read?.Scope is not { } scope
            || !scope.TryGetProperty("date_from", out var from) || !scope.TryGetProperty("date_to", out var to)
            || !DateOnly.TryParseExact(from.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !DateOnly.TryParseExact(to.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            return heading;
        return $"{heading} ({Range(start, end)})";
    }

    private string AccountValue(AccountRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Value)) return Text("notSet");
        if (row.ValueType == "date") return CalendarDate(row.Value, false, includeTime: false);
        if (row.Key == "weekStartDay" && int.TryParse(row.Value, CultureInfo.InvariantCulture, out var day) && day is >= 0 and <= 6)
            return _culture.DateTimeFormat.GetDayName((DayOfWeek)day);
        if (row.ValueType is "enum" or "boolean") return row.Value switch
        {
            "play" => "Google Play",
            "stripe" => "Stripe",
            "app_store" => "App Store",
            "Pro" or "pro" => "Pro",
            "free" => Text("Free"),
            _ => Text(row.Value)
        };
        return row.Value;
    }

    private string CalendarDate(string value, bool allDay, bool includeTime = true)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return Date(date);
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)) return Text("notSet");
        var local = TimeZoneInfo.ConvertTime(instant, _zone);
        return Date(DateOnly.FromDateTime(local.DateTime)) + (!allDay && includeTime ? " " + local.ToString("t", _culture) : string.Empty);
    }

    private string Month(HabitMonthCard card) => Lines(new[]
        { $"{Text("habitCalendar")}: {Range(card.DateFrom, card.DateTo)}", Text("calendarLogs") }
        .Concat(card.Calendar.Habits.SelectMany(habit => MonthRows(habit.Title, habit.Id, habit.ScheduledDates, habit.Children, card))));

    private IEnumerable<string> MonthRows(string title, Guid id, IReadOnlyList<DateOnly> scheduled,
        IReadOnlyList<HabitScheduleChildItem> children, HabitMonthCard card)
    {
        yield return $"{title}: {string.Join(", ", scheduled.Select(Date))}";
        foreach (var log in card.Calendar.Logs.GetValueOrDefault(id) ?? [])
            yield return $"  {Date(log.Date)}: {Number(log.Value)}";
        foreach (var child in children)
            foreach (var row in MonthRows(child.Title, child.Id, child.ScheduledDates, child.Children, card))
                yield return row;
    }

    private static string Lines(IEnumerable<string?> values, string separator = "\n") =>
        string.Join(separator, values.Where(value => !string.IsNullOrWhiteSpace(value)));
}
