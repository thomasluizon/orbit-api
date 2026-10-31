using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class Notification : Entity, ITimestamped, ISoftDeletable
{
    public Guid UserId { get; private set; }
    public string Title { get; private set; } = null!;
    public string Body { get; private set; } = null!;
    public string? Url { get; private set; }
    public string? DedupeKey { get; private set; }
    public Guid? HabitId { get; private set; }
    public DateOnly? ReminderDate { get; private set; }
    public bool IsRead { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }

    private Notification() { }

    public static Notification Create(
        Guid userId,
        string title,
        string body,
        string? url = null,
        Guid? habitId = null,
        string? dedupeKey = null)
    {
        if (url is not null && (!url.StartsWith('/') || url.StartsWith("//", StringComparison.Ordinal)))
            throw new ArgumentException("Notification URL must be an app-relative path.", nameof(url));

        return new Notification
        {
            UserId = userId,
            Title = title,
            Body = body,
            Url = url,
            DedupeKey = dedupeKey,
            HabitId = habitId,
            IsRead = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    public void MarkAsRead()
    {
        IsRead = true;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void RefreshReminder(Notification reminder, string dedupeKey, DateTime sentAtUtc, DateOnly reminderDate)
    {
        if (HabitId is null || HabitId != reminder.HabitId || UserId != reminder.UserId)
            throw new ArgumentException("Reminder must belong to the same user and habit.", nameof(reminder));
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupeKey);
        if (DedupeKey is not null && DedupeKey != dedupeKey)
            throw new ArgumentException("Reminder dedupe key cannot change.", nameof(dedupeKey));
        if (sentAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Reminder send time must be UTC.", nameof(sentAtUtc));
        if (reminderDate == default)
            throw new ArgumentOutOfRangeException(nameof(reminderDate));

        Title = reminder.Title;
        Body = reminder.Body;
        Url = reminder.Url;
        DedupeKey = dedupeKey;
        ReminderDate = reminderDate;
        IsRead = false;
        CreatedAtUtc = sentAtUtc;
        UpdatedAtUtc = sentAtUtc;
        IsDeleted = false;
        DeletedAtUtc = null;
    }

    public void SoftDelete()
    {
        IsDeleted = true;
        DeletedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
