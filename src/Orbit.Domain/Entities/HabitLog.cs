using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class HabitLog : Entity, ITimestamped, ISoftDeletable
{
    public Guid HabitId { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal Value { get; private set; }
    public int CompletionOrdinal { get; private set; }
    public bool IsSlip { get; private set; }
    public string? Note { get; private set; }
    public DateTime? ReminderAtUtc { get; private set; }
    public DateTime? TakenAtUtc { get; private set; }
    public DateOnly? LoggedOnLocalDate { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }

    private HabitLog() { }

    public static HabitLog FromSummaryRead(Guid habitId, DateOnly date, decimal value)
    {
        if (habitId == Guid.Empty)
            throw new ArgumentException("Summary log habit id must be set.");

        return new HabitLog { HabitId = habitId, Date = date, Value = value };
    }

    public static HabitLog FromScheduleRead(
        Guid id,
        Guid habitId,
        DateOnly date,
        decimal value,
        int completionOrdinal,
        DateTime createdAtUtc,
        DateTime? takenAtUtc = null,
        DateTime? reminderAtUtc = null,
        DateOnly? loggedOnLocalDate = null)
    {
        if (id == Guid.Empty || habitId == Guid.Empty)
            throw new ArgumentException("Schedule log ids must be set.");

        return new HabitLog
        {
            Id = id,
            HabitId = habitId,
            Date = date,
            Value = value,
            CompletionOrdinal = completionOrdinal,
            CreatedAtUtc = createdAtUtc,
            TakenAtUtc = takenAtUtc,
            ReminderAtUtc = reminderAtUtc,
            LoggedOnLocalDate = loggedOnLocalDate,
            UpdatedAtUtc = createdAtUtc
        };
    }

    internal static HabitLog Create(Guid habitId, DateOnly date, decimal value, string? note = null,
        bool isSlip = false, int completionOrdinal = 0, DateOnly? loggedOnLocalDate = null)
    {
        var createdAtUtc = DateTime.UtcNow;
        return new HabitLog
        {
            HabitId = habitId,
            Date = date,
            Value = value,
            CompletionOrdinal = completionOrdinal,
            IsSlip = isSlip,
            Note = note?.Trim(),
            CreatedAtUtc = createdAtUtc,
            TakenAtUtc = createdAtUtc,
            LoggedOnLocalDate = loggedOnLocalDate,
            UpdatedAtUtc = createdAtUtc
        };
    }

    public static bool IsValidTakenAtUtc(DateTime takenAtUtc, DateTime nowAtUtc) =>
        takenAtUtc.Kind == DateTimeKind.Utc && nowAtUtc.Kind == DateTimeKind.Utc && takenAtUtc <= nowAtUtc;

    public Result SetReminderAtUtc(DateTime reminderAtUtc, DateTime nowAtUtc)
    {
        if (!IsValidTakenAtUtc(reminderAtUtc, nowAtUtc))
            return Result.Failure(DomainErrors.InvalidTakenAtUtc);
        ReminderAtUtc = reminderAtUtc;
        return Result.Success();
    }

    public Result CorrectTakenAtUtc(DateTime takenAtUtc, DateTime nowAtUtc)
    {
        if (!IsValidTakenAtUtc(takenAtUtc, nowAtUtc))
            return Result.Failure(DomainErrors.InvalidTakenAtUtc);
        TakenAtUtc = takenAtUtc;
        UpdatedAtUtc = nowAtUtc;
        return Result.Success();
    }

    public void SoftDelete()
    {
        IsDeleted = true;
        DeletedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
