using System.Globalization;
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
        var key = Key(reminder.HabitId!.Value, date);
        var startAtUtc = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), zone);
        var endAtUtc = TimeZoneInfo.ConvertTimeToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
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
}
