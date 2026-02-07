using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

public class Habit : Entity
{
    public Guid UserId { get; private set; }
    public string Title { get; private set; } = null!;
    public string? Description { get; private set; }
    public HabitFrequency Frequency { get; private set; }
    public HabitType Type { get; private set; }
    public string? Unit { get; private set; }
    public decimal? TargetValue { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTime CreatedAtUtc { get; private set; }

    private readonly List<HabitLog> _logs = [];
    public IReadOnlyCollection<HabitLog> Logs => _logs.AsReadOnly();

    private Habit() { }

    public static Result<Habit> Create(
        Guid userId,
        string title,
        HabitFrequency frequency,
        HabitType type,
        string? description = null,
        string? unit = null,
        decimal? targetValue = null)
    {
        if (userId == Guid.Empty)
            return Result.Failure<Habit>("User ID is required.");

        if (string.IsNullOrWhiteSpace(title))
            return Result.Failure<Habit>("Title is required.");

        if (type == HabitType.Quantifiable && string.IsNullOrWhiteSpace(unit))
            return Result.Failure<Habit>("Unit is required for quantifiable habits.");

        return Result.Success(new Habit
        {
            UserId = userId,
            Title = title.Trim(),
            Description = description?.Trim(),
            Frequency = frequency,
            Type = type,
            Unit = unit?.Trim(),
            TargetValue = targetValue,
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    public Result<HabitLog> Log(DateOnly date, decimal? value = null)
    {
        if (!IsActive)
            return Result.Failure<HabitLog>("Cannot log an inactive habit.");

        if (Type == HabitType.Quantifiable && value is null)
            return Result.Failure<HabitLog>("A value is required for quantifiable habits.");

        if (Type == HabitType.Boolean && _logs.Exists(l => l.Date == date))
            return Result.Failure<HabitLog>("This habit has already been logged for this date.");

        var log = HabitLog.Create(Id, date, Type == HabitType.Boolean ? 1 : value!.Value);
        _logs.Add(log);
        return Result.Success(log);
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
