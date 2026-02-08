using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class SubHabit : Entity
{
    public Guid HabitId { get; private set; }
    public string Title { get; private set; } = null!;
    public int SortOrder { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTime CreatedAtUtc { get; private set; }

    private SubHabit() { }

    public static Result<SubHabit> Create(Guid habitId, string title, int sortOrder)
    {
        if (habitId == Guid.Empty)
            return Result.Failure<SubHabit>("Habit ID is required.");

        if (string.IsNullOrWhiteSpace(title))
            return Result.Failure<SubHabit>("Sub-habit title is required.");

        return Result.Success(new SubHabit
        {
            HabitId = habitId,
            Title = title.Trim(),
            SortOrder = sortOrder,
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    public void Deactivate() => IsActive = false;
}
