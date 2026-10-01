using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class HabitSkipUndo : Entity
{
    public Guid UserId { get; private set; }
    public Guid HabitId { get; private set; }
    public DateOnly PreviousDueDate { get; private set; }
    public DateOnly? PreviousScheduledStartDate { get; private set; }
    public bool PreviousIsCompleted { get; private set; }
    public Guid? SkipLogId { get; private set; }
    public DateTime ExpectedUpdatedAtUtc { get; private set; }
    public string ExpectedLogState { get; private set; } = string.Empty;
    public bool IsUndone { get; private set; }

    private HabitSkipUndo() { }

    public static Result<HabitSkipUndo> Create(Guid skipId, Habit habit)
    {
        if (skipId == Guid.Empty || habit.Id == Guid.Empty || habit.UserId == Guid.Empty)
            return Result.Failure<HabitSkipUndo>("Skip, habit and user ids must be set.");

        return Result.Success(new HabitSkipUndo
        {
            Id = skipId,
            HabitId = habit.Id,
            UserId = habit.UserId,
            PreviousDueDate = habit.DueDate,
            PreviousScheduledStartDate = habit.ScheduledStartDate,
            PreviousIsCompleted = habit.IsCompleted
        });
    }

    public void Seal(Habit habit, Guid? skipLogId, string logState)
    {
        if (habit.Id != HabitId || habit.UserId != UserId || string.IsNullOrEmpty(logState))
            throw new ArgumentException("Skip state must belong to the recorded habit.");

        ExpectedUpdatedAtUtc = habit.UpdatedAtUtc;
        SkipLogId = skipLogId;
        ExpectedLogState = logState;
    }

    public Result CheckUndo(Habit habit, string logState)
    {
        if (habit.Id != HabitId || habit.UserId != UserId)
            return Result.Failure(DomainErrors.SkipNotFound);
        if (IsUndone)
            return Result.Success();
        if (habit.IsDeleted || ExpectedLogState.Length == 0
            || habit.UpdatedAtUtc.Ticks / 10 != ExpectedUpdatedAtUtc.Ticks / 10
            || logState != ExpectedLogState)
            return Result.Failure(DomainErrors.SkipUndoConflict);

        return Result.Success();
    }

    public Result Undo(Habit habit, string logState)
    {
        var guard = CheckUndo(habit, logState);
        if (guard.IsFailure || IsUndone)
            return guard;

        var restored = habit.RestoreSkip(this);
        if (restored.IsFailure)
            return restored;
        IsUndone = true;
        return Result.Success();
    }
}
