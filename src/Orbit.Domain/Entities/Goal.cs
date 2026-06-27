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
    public GoalType Type { get; private set; }
    public DateOnly? Deadline { get; private set; }
    public int Position { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; private set; }
    public DateTime? StreakSyncedAtUtc { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }

    private readonly List<GoalProgressLog> _progressLogs = [];
    public IReadOnlyCollection<GoalProgressLog> ProgressLogs => _progressLogs.AsReadOnly();

    private readonly List<Habit> _habits = [];
    public IReadOnlyCollection<Habit> Habits => _habits.AsReadOnly();

    public void AddHabit(Habit habit) { if (!_habits.Contains(habit)) _habits.Add(habit); }

    public void RemoveHabit(Habit habit)
    {
        _habits.Remove(habit);
        if (Type == GoalType.Streak && _habits.Count == 0)
            ResetStreakProgress();
    }

    /// <summary>
    /// Clears a streak goal's synced progress back to zero. A streak goal with no linked habits
    /// has nothing to count, so any retained CurrentValue is stale; callers reset it so reads never
    /// report a streak the goal can no longer justify. Returns true when this cleared a non-zero value.
    /// </summary>
    public bool ResetStreakProgress()
    {
        if (Type != GoalType.Streak || (CurrentValue == 0 && StreakSyncedAtUtc is null))
            return false;

        CurrentValue = 0;
        StreakSyncedAtUtc = null;
        UpdatedAtUtc = DateTime.UtcNow;
        return true;
    }

    public void SoftDelete()
    {
        IsDeleted = true;
        DeletedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void Restore()
    {
        IsDeleted = false;
        DeletedAtUtc = null;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    private Goal() { }

    public record CreateGoalParams(
        Guid UserId,
        string Title,
        decimal TargetValue,
        string Unit,
        string? Description = null,
        DateOnly? Deadline = null,
        int Position = 0,
        GoalType Type = GoalType.Standard);

    public static Result<Goal> Create(CreateGoalParams p)
    {
        if (p.UserId == Guid.Empty)
            return Result.Failure<Goal>(DomainErrors.UserIdRequired);

        var coreFieldsValidation = GoalInvariants.ValidateCoreFields(p.Title, p.TargetValue, p.Unit);
        if (coreFieldsValidation is not null)
            return Result.Failure<Goal>(coreFieldsValidation);

        return Result.Success(new Goal
        {
            UserId = p.UserId,
            Title = p.Title.Trim(),
            Description = p.Description?.Trim(),
            TargetValue = p.TargetValue,
            CurrentValue = 0,
            Unit = p.Unit.Trim(),
            Status = GoalStatus.Active,
            Type = p.Type,
            Deadline = p.Deadline,
            Position = p.Position,
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    public static Result<Goal> Create(
        Guid userId,
        string title,
        decimal targetValue,
        string unit)
    {
        return Create(new CreateGoalParams(userId, title, targetValue, unit));
    }

    /// <summary>
    /// Sets the goal's current value, auto-completing it when the target is reached.
    /// The success value is true only when this call transitioned an Active goal to Completed,
    /// so callers can fire completion side effects (gamification) exactly once.
    /// </summary>
    public Result<bool> UpdateProgress(decimal newValue)
    {
        if (Status != GoalStatus.Active)
            return Result.Failure<bool>(DomainErrors.GoalNotActive);

        if (newValue < 0)
            return Result.Failure<bool>(DomainErrors.ProgressValueNegative);

        CurrentValue = newValue;
        var justCompleted = TryComplete();

        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success(justCompleted);
    }

    /// <summary>
    /// Syncs a streak goal's current value to the computed streak length. When
    /// <paramref name="allowCompletion"/> is true (write paths and the hosted sweep) it auto-completes
    /// the goal once the target is reached and the success value reports that Active to Completed
    /// transition so callers can fire completion side effects exactly once. When false (read paths)
    /// it refreshes the value for display only, never flipping Status, so a read can surface the live
    /// streak without persisting a completion the read has no gamification to honour.
    /// </summary>
    public Result<bool> SyncStreakProgress(int currentStreak, bool allowCompletion = true)
    {
        if (Type != GoalType.Streak)
            return Result.Failure<bool>(DomainErrors.NotStreakGoal);

        if (Status != GoalStatus.Active)
            return Result.Failure<bool>(DomainErrors.GoalNotActive);

        CurrentValue = currentStreak;
        StreakSyncedAtUtc = DateTime.UtcNow;
        var justCompleted = allowCompletion && TryComplete();

        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success(justCompleted);
    }

    private bool TryComplete()
    {
        if (CurrentValue < TargetValue)
            return false;

        Status = GoalStatus.Completed;
        CompletedAtUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Applies an edit to the goal's core fields and reports the status transition the new target
    /// triggered, if any. The transition is returned rather than acted on so the caller can run the
    /// completion pipeline (gamification, progress log) exactly once on <see cref="GoalEditTransition.Completed"/>
    /// and reopen cleanly on <see cref="GoalEditTransition.Reopened"/>, matching the dedicated
    /// progress and status commands.
    /// </summary>
    public Result<GoalEditTransition> Update(string title, string? description, decimal targetValue, string unit, DateOnly? deadline)
    {
        var coreFieldsValidation = GoalInvariants.ValidateCoreFields(title, targetValue, unit);
        if (coreFieldsValidation is not null)
            return Result.Failure<GoalEditTransition>(coreFieldsValidation);

        Title = title.Trim();
        Description = description?.Trim();
        TargetValue = targetValue;
        Unit = unit.Trim();
        Deadline = deadline;
        UpdatedAtUtc = DateTime.UtcNow;

        if (Status == GoalStatus.Active && CurrentValue >= TargetValue)
        {
            Status = GoalStatus.Completed;
            CompletedAtUtc = DateTime.UtcNow;
            return Result.Success(GoalEditTransition.Completed);
        }

        if (Status == GoalStatus.Completed && CurrentValue < TargetValue)
        {
            Status = GoalStatus.Active;
            CompletedAtUtc = null;
            return Result.Success(GoalEditTransition.Reopened);
        }

        return Result.Success(GoalEditTransition.None);
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
            return Result.Failure(DomainErrors.GoalAlreadyCompleted);

        Status = GoalStatus.Completed;
        CompletedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }

    public Result MarkAbandoned()
    {
        if (Status == GoalStatus.Abandoned)
            return Result.Failure(DomainErrors.GoalAlreadyAbandoned);

        Status = GoalStatus.Abandoned;
        CompletedAtUtc = null;
        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }

    public Result Reactivate()
    {
        if (Status == GoalStatus.Active)
            return Result.Failure(DomainErrors.GoalAlreadyActive);

        Status = GoalStatus.Active;
        CompletedAtUtc = null;
        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }
}
