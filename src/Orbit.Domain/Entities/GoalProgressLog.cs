using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class GoalProgressLog : Entity
{
    public Guid GoalId { get; private set; }
    public decimal Value { get; private set; }
    public decimal PreviousValue { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public string? Note { get; private set; }

    private GoalProgressLog() { }

    public static GoalProgressLog Create(Guid goalId, decimal previousValue, decimal newValue, string? note = null)
    {
        return new GoalProgressLog
        {
            GoalId = goalId,
            PreviousValue = previousValue,
            Value = newValue,
            Note = note?.Trim(),
            CreatedAtUtc = DateTime.UtcNow
        };
    }
}
