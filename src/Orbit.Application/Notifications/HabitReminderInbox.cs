using System.Globalization;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Notifications;

public static class HabitReminderInbox
{
    private const string ReminderPrefix = "habit-reminder:";

    public static string Key(Guid habitId, DateOnly localDate) =>
        string.Create(CultureInfo.InvariantCulture, $"{ReminderPrefix}{habitId:N}:{localDate:yyyy-MM-dd}");

    public static async Task RefreshAsync(IGenericRepository<Notification> notifications,
        Notification reminder, TimeZoneInfo zone, DateTime sentAtUtc, DateOnly reminderDate, CancellationToken ct)
    {
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(sentAtUtc, zone));
        if (reminder.HabitId is not { } habitId)
            throw new ArgumentException("Habit reminder must have a habit id.", nameof(reminder));
        var key = Key(habitId, date);
        var row = await notifications.FindOneTrackedIgnoringFiltersAsync(n => n.UserId == reminder.UserId
            && n.HabitId == habitId && n.DedupeKey == key, ct);
        if (row is null)
        {
            row = reminder;
            await notifications.AddAsync(row, ct);
        }
        row.RefreshReminder(reminder, key, sentAtUtc, reminderDate);
    }

    public static async Task MarkReadAsync(IGenericRepository<Notification> notifications,
        Guid userId, Guid habitId, DateOnly localDate, CancellationToken ct)
    {
        var key = Key(habitId, localDate);
        var rows = await notifications.FindTrackedAsync(n => n.UserId == userId && n.HabitId == habitId
            && !n.IsRead && n.DedupeKey != null && n.DedupeKey.StartsWith(ReminderPrefix)
            && (n.ReminderDate == localDate || n.ReminderDate == null && n.DedupeKey == key), ct);
        foreach (var row in rows)
            row.MarkAsRead();
    }

    public static async Task MarkReadBatchAsync(IGenericRepository<Notification> notifications,
        Guid userId, IEnumerable<(Guid HabitId, DateOnly Date)> completions, CancellationToken ct)
    {
        var datesByHabit = completions.GroupBy(completion => completion.HabitId)
            .ToDictionary(group => group.Key, group => group.Select(completion => completion.Date).ToHashSet());
        if (datesByHabit.Count == 0) return;
        var ids = datesByHabit.Keys.ToArray();
        var rows = await notifications.FindTrackedAsync(n => n.UserId == userId && n.HabitId != null
            && ids.Contains(n.HabitId.Value) && !n.IsRead && n.DedupeKey != null
            && n.DedupeKey.StartsWith(ReminderPrefix), ct);
        foreach (var row in rows)
        {
            if (row.HabitId is not { } habitId || !datesByHabit.TryGetValue(habitId, out var dates)) continue;
            if (dates.Any(date => row.ReminderDate == date
                || row.ReminderDate is null && row.DedupeKey == Key(habitId, date))) row.MarkAsRead();
        }
    }
}
