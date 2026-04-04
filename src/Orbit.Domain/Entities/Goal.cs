using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

public class Goal : Entity, ITimestamped, ISoftDeletable
{
    public Guid UserId { get; private set; }
    public string Title { get; private set; } = null!;
    public string? Description { get; private set; }
    public decimal TargetValue { get; private set; }
    public decimal CurrentValue { get; private set; }
    public string Unit { get; private set; } = null!;
    public GoalStatus Status { get; private set; }
    public DateOnly? Deadline { get; private set; }
    public int Position { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }

    private readonly List<GoalProgressLog> _progressLogs = [];
    public IReadOnlyCollection<GoalProgressLog> ProgressLogs => _progressLogs.AsReadOnly();

    private readonly List<Habit> _habits = [];
    public IReadOnlyCollection<Habit> Habits => _habits.AsReadOnly();

    public void AddHabit(Habit habit) { if (!_habits.Contains(habit)) _habits.Add(habit); }
    public void RemoveHabit(Habit habit) => _habits.Remove(habit);

    public void SoftDelete()
    {
        IsDeleted = true;
        DeletedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    private Goal() { }

    public static Result<Goal> Create(
        Guid userId,
        string title,
        decimal targetValue,
        string unit,
        string? description = null,
        DateOnly? deadline = null,
        int position = 0)
    {
        if (userId == Guid.Empty)
            return Result.Failure<Goal>("User ID is required.");

        if (string.IsNullOrWhiteSpace(title))
            return Result.Failure<Goal>("Title is required.");

        if (targetValue <= 0)
            return Result.Failure<Goal>("Target value must be greater than 0.");

        if (string.IsNullOrWhiteSpace(unit))
            return Result.Failure<Goal>("Unit is required.");

        return Result.Success(new Goal
        {
            UserId = userId,
            Title = title.Trim(),
            Description = description?.Trim(),
            TargetValue = targetValue,
            CurrentValue = 0,
            Unit = unit.Trim(),
            Status = GoalStatus.Active,
            Deadline = deadline,
            Position = position,
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    public Result UpdateProgress(decimal newValue)
    {
        if (Status != GoalStatus.Active)
            return Result.Failure("Cannot update progress on a non-active goal.");

        if (newValue < 0)
            return Result.Failure("Progress value cannot be negative.");

        CurrentValue = newValue;

        if (CurrentValue >= TargetValue)
        {
            Status = GoalStatus.Completed;
            CompletedAtUtc = DateTime.UtcNow;
        }

        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }

    public Result Update(string title, string? description, decimal targetValue, string unit, DateOnly? deadline)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Result.Failure("Title is required.");

        if (targetValue <= 0)
            return Result.Failure("Target value must be greater than 0.");

        if (string.IsNullOrWhiteSpace(unit))
            return Result.Failure("Unit is required.");

        Title = title.Trim();
        Description = description?.Trim();
        TargetValue = targetValue;
        Unit = unit.Trim();
        Deadline = deadline;

        if (Status == GoalStatus.Active && CurrentValue >= TargetValue)
        {
            Status = GoalStatus.Completed;
            CompletedAtUtc = DateTime.UtcNow;
        }
        else if (Status == GoalStatus.Completed && CurrentValue < TargetValue)
        {
            Status = GoalStatus.Active;
            CompletedAtUtc = null;
        }

        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }

    public Result UpdatePosition(int position)
    {
        Position = position;
        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }

    public Result MarkCompleted()
    {
        if (Status == GoalStatus.Completed)
            return Result.Failure("Goal is already completed.");

        Status = GoalStatus.Completed;
        CompletedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }

    public Result MarkAbandoned()
    {
        if (Status == GoalStatus.Abandoned)
            return Result.Failure("Goal is already abandoned.");

        Status = GoalStatus.Abandoned;
        CompletedAtUtc = null;
        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }

    public Result Reactivate()
    {
        if (Status == GoalStatus.Active)
            return Result.Failure("Goal is already active.");

        Status = GoalStatus.Active;
        CompletedAtUtc = null;
        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }
}
