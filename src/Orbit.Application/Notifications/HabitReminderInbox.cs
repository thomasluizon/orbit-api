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
}
