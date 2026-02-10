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
    public bool IsActive { get; private set; } = true;
    public bool IsBadHabit { get; private set; }
    public bool IsCompleted { get; private set; }
    public DateOnly DueDate { get; private set; }
    public int? Position { get; private set; }
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
        string? description = null,
        IReadOnlyList<System.DayOfWeek>? days = null,
        bool isBadHabit = false,
        DateOnly? dueDate = null,
        Guid? parentHabitId = null)
    {
        if (userId == Guid.Empty)
            return Result.Failure<Habit>("User ID is required.");

        if (string.IsNullOrWhiteSpace(title))
            return Result.Failure<Habit>("Title is required.");

        if (frequencyQuantity is not null && frequencyQuantity <= 0)
            return Result.Failure<Habit>("Frequency quantity must be greater than 0.");

        if (days?.Count > 0 && frequencyQuantity != 1)
            return Result.Failure<Habit>("Days can only be set when frequency quantity is 1.");

        return Result.Success(new Habit
        {
            UserId = userId,
            Title = title.Trim(),
            Description = description?.Trim(),
            FrequencyUnit = frequencyUnit,
            FrequencyQuantity = frequencyQuantity,
            Days = days?.ToList() ?? [],
            IsBadHabit = isBadHabit,
            DueDate = dueDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
            ParentHabitId = parentHabitId,
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    public Result<HabitLog> Log(DateOnly date, string? note = null)
    {
        if (!IsActive)
            return Result.Failure<HabitLog>("Cannot log an inactive habit.");

        if (IsCompleted)
            return Result.Failure<HabitLog>("Cannot log a completed habit.");

        if (!IsBadHabit && _logs.Exists(l => l.Date == date))
            return Result.Failure<HabitLog>("This habit has already been logged for this date.");

        var log = HabitLog.Create(Id, date, 1, note);
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

    public Result<HabitLog> Unlog(DateOnly date)
    {
        if (!IsActive)
            return Result.Failure<HabitLog>("Cannot unlog an inactive habit.");

        var log = _logs.Find(l => l.Date == date);
        if (log is null)
            return Result.Failure<HabitLog>("No log found for this date.");

        _logs.Remove(log);

        if (FrequencyUnit is null)
        {
            IsCompleted = false;
        }
        else
        {
            DueDate = date;
        }

        return Result.Success(log);
    }

    public Result Update(
        string title,
        string? description,
        FrequencyUnit? frequencyUnit,
        int? frequencyQuantity,
        IReadOnlyList<System.DayOfWeek>? days,
        bool isBadHabit,
        DateOnly? dueDate)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Result.Failure("Title is required.");

        if (frequencyQuantity is not null && frequencyQuantity <= 0)
            return Result.Failure("Frequency quantity must be greater than 0.");

        if (days?.Count > 0 && frequencyQuantity != 1)
            return Result.Failure("Days can only be set when frequency quantity is 1.");

        Title = title.Trim();
        Description = description?.Trim();
        FrequencyUnit = frequencyUnit;
        FrequencyQuantity = frequencyQuantity;
        Days = days?.ToList() ?? [];
        IsBadHabit = isBadHabit;

        if (dueDate is not null)
            DueDate = dueDate.Value;

        return Result.Success();
    }

    public void SetPosition(int? position) => Position = position;

    public void SetParentHabitId(Guid? parentHabitId) => ParentHabitId = parentHabitId;

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
