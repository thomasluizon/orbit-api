using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class HabitLog : Entity
{
    public Guid HabitId { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal Value { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private HabitLog() { }

    internal static HabitLog Create(Guid habitId, DateOnly date, decimal value)
    {
        return new HabitLog
        {
            HabitId = habitId,
            Date = date,
            Value = value,
            CreatedAtUtc = DateTime.UtcNow
        };
    }
}
