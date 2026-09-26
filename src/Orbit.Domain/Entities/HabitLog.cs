using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class HabitLog : Entity, ITimestamped, ISoftDeletable
{
    public Guid HabitId { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal Value { get; private set; }
    public bool? IsSlip { get; private set; }
    public string? Note { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }

    private HabitLog() { }

    public static HabitLog FromScheduleRead(
        Guid id,
        Guid habitId,
        DateOnly date,
        decimal value,
        DateTime createdAtUtc)
    {
        if (id == Guid.Empty || habitId == Guid.Empty)
            throw new ArgumentException("Schedule log ids must be set.");

        return new HabitLog
        {
            Id = id,
            HabitId = habitId,
            Date = date,
            Value = value,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc
        };
    }

    internal static HabitLog Create(Guid habitId, DateOnly date, decimal value, string? note = null, bool isSlip = false)
    {
        return new HabitLog
        {
            HabitId = habitId,
            Date = date,
            Value = value,
            IsSlip = isSlip,
            Note = note?.Trim(),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    public void SoftDelete()
    {
        IsDeleted = true;
        DeletedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
