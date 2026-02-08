using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

public class Habit : Entity
{
    public Guid UserId { get; private set; }
    public string Title { get; private set; } = null!;
    public string? Description { get; private set; }
    public FrequencyUnit? FrequencyUnit { get; private set; }
    public int? FrequencyQuantity { get; private set; }
    public HabitType Type { get; private set; }
    public string? Unit { get; private set; }
    public decimal? TargetValue { get; private set; }
    public bool IsActive { get; private set; } = true;
    public bool IsNegative { get; private set; }
    public bool IsCompleted { get; private set; }
    public DateOnly DueDate { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public ICollection<System.DayOfWeek> Days { get; private set; } = [];

    public Guid? ParentHabitId { get; private set; }

    private readonly List<HabitLog> _logs = [];
    public IReadOnlyCollection<HabitLog> Logs => _logs.AsReadOnly();

    private readonly List<Habit> _children = [];
    public IReadOnlyCollection<Habit> Children => _children.AsReadOnly();

    public ICollection<Tag> Tags { get; private set; } = [];

    private Habit() { }

    public static Result<Habit> Create(
        Guid userId,
        string title,
        FrequencyUnit? frequencyUnit,
        int? frequencyQuantity,
        HabitType type,
        string? description = null,
        string? unit = null,
        decimal? targetValue = null,
        IReadOnlyList<System.DayOfWeek>? days = null,
        bool isNegative = false,
        DateOnly? dueDate = null,
        Guid? parentHabitId = null)
    {
        if (userId == Guid.Empty)
            return Result.Failure<Habit>("User ID is required.");

        if (string.IsNullOrWhiteSpace(title))
            return Result.Failure<Habit>("Title is required.");

        if (frequencyQuantity is not null && frequencyQuantity <= 0)
            return Result.Failure<Habit>("Frequency quantity must be greater than 0.");

        if (type == HabitType.Quantifiable && string.IsNullOrWhiteSpace(unit))
            return Result.Failure<Habit>("Unit is required for quantifiable habits.");

        if (days?.Count > 0 && frequencyQuantity != 1)
            return Result.Failure<Habit>("Days can only be set when frequency quantity is 1.");

        return Result.Success(new Habit
        {
            UserId = userId,
            Title = title.Trim(),
            Description = description?.Trim(),
            FrequencyUnit = frequencyUnit,
            FrequencyQuantity = frequencyQuantity,
            Type = type,
            Unit = unit?.Trim(),
            TargetValue = targetValue,
            Days = days?.ToList() ?? [],
            IsNegative = isNegative,
            DueDate = dueDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
            ParentHabitId = parentHabitId,
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    public Result<HabitLog> Log(DateOnly date, decimal? value = null, string? note = null)
    {
        if (!IsActive)
            return Result.Failure<HabitLog>("Cannot log an inactive habit.");

        if (IsCompleted)
            return Result.Failure<HabitLog>("Cannot log a completed habit.");

        if (Type == HabitType.Quantifiable && value is null)
            return Result.Failure<HabitLog>("A value is required for quantifiable habits.");

        if (Type == HabitType.Boolean && !IsNegative && _logs.Exists(l => l.Date == date))
            return Result.Failure<HabitLog>("This habit has already been logged for this date.");

        var log = HabitLog.Create(Id, date, Type == HabitType.Boolean ? 1 : value!.Value, note);
        _logs.Add(log);

        // One-time task: mark as completed
        if (FrequencyUnit is null)
        {
            IsCompleted = true;
        }
        else
        {
            // Recurring habit: advance DueDate to next occurrence
            AdvanceDueDate();
        }

        return Result.Success(log);
    }

    private void AdvanceDueDate()
    {
        var next = (FrequencyUnit, FrequencyQuantity) switch
        {
            (Enums.FrequencyUnit.Day, var q) => DueDate.AddDays(q!.Value),
            (Enums.FrequencyUnit.Week, var q) => DueDate.AddDays(7 * q!.Value),
            (Enums.FrequencyUnit.Month, var q) => DueDate.AddMonths(q!.Value),
            (Enums.FrequencyUnit.Year, var q) => DueDate.AddYears(q!.Value),
            _ => DueDate
        };

        // If Days are specified, find the next matching day
        if (Days.Count > 0)
        {
            while (!Days.Contains(next.DayOfWeek))
            {
                next = next.AddDays(1);
            }
        }

        DueDate = next;
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
