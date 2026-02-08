using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class SubHabitLog : Entity
{
    public Guid SubHabitId { get; private set; }
    public DateOnly Date { get; private set; }
    public bool IsCompleted { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private SubHabitLog() { }

    internal static SubHabitLog Create(Guid subHabitId, DateOnly date, bool isCompleted)
    {
        return new SubHabitLog
        {
            SubHabitId = subHabitId,
            Date = date,
            IsCompleted = isCompleted,
            CreatedAtUtc = DateTime.UtcNow
        };
    }
}
