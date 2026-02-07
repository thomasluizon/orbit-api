using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

public class TaskItem : Entity
{
    public Guid UserId { get; private set; }
    public string Title { get; private set; } = null!;
    public string? Description { get; private set; }
    public DateOnly? DueDate { get; private set; }
    public TaskItemStatus Status { get; private set; } = TaskItemStatus.Pending;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }

    private TaskItem() { }

    public static Result<TaskItem> Create(
        Guid userId,
        string title,
        string? description = null,
        DateOnly? dueDate = null)
    {
        if (userId == Guid.Empty)
            return Result.Failure<TaskItem>("User ID is required.");

        if (string.IsNullOrWhiteSpace(title))
            return Result.Failure<TaskItem>("Title is required.");

        return Result.Success(new TaskItem
        {
            UserId = userId,
            Title = title.Trim(),
            Description = description?.Trim(),
            DueDate = dueDate,
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    public Result MarkCompleted()
    {
        if (Status == TaskItemStatus.Completed)
            return Result.Failure("Task is already completed.");

        if (Status == TaskItemStatus.Cancelled)
            return Result.Failure("Cannot complete a cancelled task.");

        Status = TaskItemStatus.Completed;
        CompletedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }

    public Result Cancel()
    {
        if (Status == TaskItemStatus.Completed)
            return Result.Failure("Cannot cancel a completed task.");

        if (Status == TaskItemStatus.Cancelled)
            return Result.Failure("Task is already cancelled.");

        Status = TaskItemStatus.Cancelled;
        return Result.Success();
    }

    public Result StartProgress()
    {
        if (Status != TaskItemStatus.Pending)
            return Result.Failure("Can only start progress on a pending task.");

        Status = TaskItemStatus.InProgress;
        return Result.Success();
    }
}
