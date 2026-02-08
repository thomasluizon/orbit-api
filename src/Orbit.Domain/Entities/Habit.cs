using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

public class Habit : Entity
{
    public Guid UserId { get; private set; }
    public string Title { get; private set; } = null!;
    public string? Description { get; private set; }
    public FrequencyUnit FrequencyUnit { get; private set; }
    public int FrequencyQuantity { get; private set; }
    public HabitType Type { get; private set; }
    public string? Unit { get; private set; }
    public decimal? TargetValue { get; private set; }
    public bool IsActive { get; private set; } = true;
    public bool IsNegative { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public ICollection<System.DayOfWeek> Days { get; private set; } = [];

    private readonly List<HabitLog> _logs = [];
    public IReadOnlyCollection<HabitLog> Logs => _logs.AsReadOnly();

    private readonly List<SubHabit> _subHabits = [];
    public IReadOnlyCollection<SubHabit> SubHabits => _subHabits.AsReadOnly();

    public ICollection<Tag> Tags { get; private set; } = [];

    private Habit() { }

    public static Result<Habit> Create(
        Guid userId,
        string title,
        FrequencyUnit frequencyUnit,
        int frequencyQuantity,
        HabitType type,
        string? description = null,
        string? unit = null,
        decimal? targetValue = null,
        IReadOnlyList<System.DayOfWeek>? days = null,
        bool isNegative = false)
    {
        if (userId == Guid.Empty)
            return Result.Failure<Habit>("User ID is required.");

        if (string.IsNullOrWhiteSpace(title))
            return Result.Failure<Habit>("Title is required.");

        if (frequencyQuantity <= 0)
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
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    public Result<HabitLog> Log(DateOnly date, decimal? value = null, string? note = null)
    {
        if (!IsActive)
            return Result.Failure<HabitLog>("Cannot log an inactive habit.");

        if (Type == HabitType.Quantifiable && value is null)
            return Result.Failure<HabitLog>("A value is required for quantifiable habits.");

        if (Type == HabitType.Boolean && !IsNegative && _logs.Exists(l => l.Date == date))
            return Result.Failure<HabitLog>("This habit has already been logged for this date.");

        var log = HabitLog.Create(Id, date, Type == HabitType.Boolean ? 1 : value!.Value, note);
        _logs.Add(log);
        return Result.Success(log);
    }

    public Result<SubHabit> AddSubHabit(string title, int sortOrder)
    {
        var result = SubHabit.Create(Id, title, sortOrder);
        if (result.IsFailure)
            return result;

        _subHabits.Add(result.Value);
        return result;
    }

    public Result RemoveSubHabit(Guid subHabitId)
    {
        var subHabit = _subHabits.Find(sh => sh.Id == subHabitId);
        if (subHabit is null)
            return Result.Failure("Sub-habit not found.");

        subHabit.Deactivate();
        return Result.Success();
    }

    public Result<IReadOnlyList<SubHabitLog>> LogSubHabitCompletions(
        DateOnly date,
        IReadOnlyList<(Guid SubHabitId, bool IsCompleted)> completions)
    {
        if (!IsActive)
            return Result.Failure<IReadOnlyList<SubHabitLog>>("Cannot log an inactive habit.");

        var logs = new List<SubHabitLog>();

        foreach (var (subHabitId, isCompleted) in completions)
        {
            var subHabit = _subHabits.Find(sh => sh.Id == subHabitId && sh.IsActive);
            if (subHabit is null)
                return Result.Failure<IReadOnlyList<SubHabitLog>>($"Sub-habit {subHabitId} not found or inactive.");

            logs.Add(SubHabitLog.Create(subHabitId, date, isCompleted));
        }

        return Result.Success<IReadOnlyList<SubHabitLog>>(logs.AsReadOnly());
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
