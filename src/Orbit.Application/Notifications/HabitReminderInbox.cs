using System.Globalization;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Notifications;

public static class HabitReminderInbox
{
    public static string Key(Guid habitId, DateOnly localDate) =>
        string.Create(CultureInfo.InvariantCulture, $"habit-reminder:{habitId:N}:{localDate:yyyy-MM-dd}");

    public static async Task RefreshAsync(IGenericRepository<Notification> notifications,
        Notification reminder, TimeZoneInfo zone, DateTime sentAtUtc, CancellationToken ct)
    {
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(sentAtUtc, zone));
        if (reminder.HabitId is not { } habitId)
            throw new ArgumentException("Habit reminder must have a habit id.", nameof(reminder));
        var key = Key(habitId, date);
        var startAtUtc = RepeatReminderClock.LocalInstant(date.ToDateTime(TimeOnly.MinValue), zone)
            ?? throw new ArgumentOutOfRangeException(nameof(sentAtUtc));
        var endAtUtc = RepeatReminderClock.LocalInstant(date.AddDays(1).ToDateTime(TimeOnly.MinValue), zone)
            ?? throw new ArgumentOutOfRangeException(nameof(sentAtUtc));
        var candidates = await notifications.FindTrackedIgnoringFiltersAsync(n => n.UserId == reminder.UserId
            && n.HabitId == reminder.HabitId && (n.DedupeKey == key
                || !n.IsDeleted && n.DedupeKey == null && n.Url == NotificationUrls.Home
                    && n.CreatedAtUtc >= startAtUtc && n.CreatedAtUtc < endAtUtc), ct);
        var row = candidates.FirstOrDefault(n => n.DedupeKey == key)
            ?? candidates.OrderByDescending(n => n.CreatedAtUtc).FirstOrDefault();
        if (row is null)
        {
            row = reminder;
            await notifications.AddAsync(row, ct);
        }
        row.RefreshReminder(reminder, key, sentAtUtc);
        foreach (var duplicate in candidates.Where(n => n.Id != row.Id))
            duplicate.SoftDelete();
    }

    public static async Task MarkReadAsync(IGenericRepository<Notification> notifications,
        Guid userId, Guid habitId, DateOnly localDate, IUserDateService dates, CancellationToken ct)
    {
        var key = Key(habitId, localDate);
        var rows = await notifications.FindTrackedAsync(n => n.UserId == userId && n.HabitId == habitId
            && !n.IsRead && (n.DedupeKey == key || n.DedupeKey == null && n.Url == NotificationUrls.Home), ct);
        var zone = rows.Any(n => n.DedupeKey is null) ? await dates.GetUserTimeZoneAsync(userId, ct) : null;
        foreach (var row in rows)
        {
            if (row.DedupeKey == key || zone is { } legacyZone
                && DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(row.CreatedAtUtc, legacyZone)) == localDate)
                row.MarkAsRead();
        }
    }
}
