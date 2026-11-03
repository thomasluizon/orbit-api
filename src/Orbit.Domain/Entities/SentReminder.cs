using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

public class SentReminder : Entity
{
    public Guid HabitId { get; private set; }
    public DateOnly Date { get; private set; }
    public int MinutesBefore { get; private set; }
    public TimeOnly? ReminderTimeUtc { get; private set; }
    public ScheduledReminderWhen? When { get; private set; }
    public DateTime? ReminderAtUtc { get; private set; }
    public DateTime? ExpiresAtUtc { get; private set; }
    public DateTime? DoseAnchorAtUtc { get; private set; }
    public Guid? ParentReminderId { get; private set; }
    public DateTime? ConsumedAtUtc { get; private set; }
    public DateTime? PushSentAtUtc { get; private set; }
    public Guid? PushUserId { get; private set; }
    public string? PushTitle { get; private set; }
    public string? PushBody { get; private set; }
    public string? PushDataJson { get; private set; }
    public DateTime SentAtUtc { get; private set; }

    private SentReminder() { }

    public void PreparePush(Guid userId, string title, string body, IReadOnlyDictionary<string, string>? data = null, DateTime? recordedAtUtc = null)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("A reminder push requires an owner.");
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        if (recordedAtUtc.HasValue)
        {
            if (recordedAtUtc.Value.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Reminder recording requires a UTC instant.");
            SentAtUtc = recordedAtUtc.Value;
        }
        PushUserId = userId;
        PushTitle = title;
        PushBody = body;
        PushDataJson = data is null ? null : System.Text.Json.JsonSerializer.Serialize(data);
    }

    public void MarkPushSent(DateTime sentAtUtc)
    {
        if (sentAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Push delivery requires a UTC instant.");
        PushSentAtUtc ??= sentAtUtc;
    }

    public static SentReminder CreateRepeat(Guid habitId, DateOnly localDate, DateTime reminderAtUtc,
        DateTime expiresAtUtc, DateTime doseAnchorAtUtc, Guid? parentReminderId = null)
    {
        if (habitId == Guid.Empty || reminderAtUtc.Kind != DateTimeKind.Utc
            || expiresAtUtc.Kind != DateTimeKind.Utc || doseAnchorAtUtc.Kind != DateTimeKind.Utc
            || expiresAtUtc <= reminderAtUtc)
            throw new ArgumentException("Repeat reminder instants must be ordered UTC values.");
        return new SentReminder
        {
            HabitId = habitId,
            Date = localDate,
            ReminderAtUtc = reminderAtUtc,
            ExpiresAtUtc = expiresAtUtc,
            DoseAnchorAtUtc = doseAnchorAtUtc,
            ParentReminderId = parentReminderId,
            SentAtUtc = DateTime.UtcNow
        };
    }

    public bool TryConsume(DateTime nowUtc)
    {
        if (nowUtc.Kind != DateTimeKind.Utc || ReminderAtUtc is null || ParentReminderId is not null
            || ConsumedAtUtc is not null || nowUtc < ReminderAtUtc || nowUtc >= ExpiresAtUtc)
            return false;
        ConsumedAtUtc = nowUtc;
        return true;
    }

    public static SentReminder Create(
        Guid habitId, DateOnly date, int minutesBefore,
        TimeOnly? reminderTimeUtc = null, ScheduledReminderWhen? when = null)
    {
        return new SentReminder
        {
            HabitId = habitId,
            Date = date,
            MinutesBefore = minutesBefore,
            ReminderTimeUtc = reminderTimeUtc,
            When = when,
            SentAtUtc = DateTime.UtcNow
        };
    }
}
