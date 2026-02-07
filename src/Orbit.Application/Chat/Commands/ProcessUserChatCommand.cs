using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public record ProcessUserChatCommand(Guid UserId, string Message) : IRequest<Result<ChatResponse>>;

public record ChatResponse(IReadOnlyList<string> ExecutedActions, string? AiMessage);

public class ProcessUserChatCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<TaskItem> taskRepository,
    IAiIntentService aiIntentService,
    IUnitOfWork unitOfWork) : IRequestHandler<ProcessUserChatCommand, Result<ChatResponse>>
{
    public async Task<Result<ChatResponse>> Handle(
        ProcessUserChatCommand request,
        CancellationToken cancellationToken)
    {
        // 1. Retrieve user's active habits and pending tasks as context for the AI
        var activeHabits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId && h.IsActive,
            cancellationToken);

        var pendingTasks = await taskRepository.FindAsync(
            t => t.UserId == request.UserId
                 && t.Status != TaskItemStatus.Completed
                 && t.Status != TaskItemStatus.Cancelled,
            cancellationToken);

        // 2. Send text + context to the AI intent service for interpretation
        var planResult = await aiIntentService.InterpretAsync(
            request.Message,
            activeHabits,
            pendingTasks,
            cancellationToken);

        if (planResult.IsFailure)
            return Result.Failure<ChatResponse>(planResult.Error);

        var plan = planResult.Value;
        var executedActions = new List<string>();

        // 3. Execute each action returned by the AI
        foreach (var action in plan.Actions)
        {
            var actionResult = action.Type switch
            {
                AiActionType.LogHabit => await ExecuteLogHabitAsync(action, request.UserId, cancellationToken),
                AiActionType.CreateHabit => await ExecuteCreateHabitAsync(action, request.UserId, cancellationToken),
                AiActionType.CreateTask => await ExecuteCreateTaskAsync(action, request.UserId, cancellationToken),
                AiActionType.UpdateTask => await ExecuteUpdateTaskAsync(action, cancellationToken),
                _ => Result.Failure($"Unknown action type: {action.Type}")
            };

            if (actionResult.IsSuccess)
                executedActions.Add($"{action.Type}: {action.Title ?? action.HabitId?.ToString() ?? "N/A"}");
        }

        // 4. Persist all changes in a single unit of work
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(new ChatResponse(executedActions, plan.AiMessage));
    }

    private async Task<Result> ExecuteLogHabitAsync(
        AiAction action, Guid userId, CancellationToken ct)
    {
        if (action.HabitId is null)
            return Result.Failure("Habit ID is required for logging.");

        var habit = await habitRepository.GetByIdAsync(action.HabitId.Value, ct);

        if (habit is null)
            return Result.Failure($"Habit {action.HabitId} not found.");

        if (habit.UserId != userId)
            return Result.Failure("Habit does not belong to this user.");

        var date = action.DueDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var logResult = habit.Log(date, action.Value);

        if (logResult.IsFailure)
            return Result.Failure(logResult.Error);

        habitRepository.Update(habit);
        return Result.Success();
    }

    private async Task<Result> ExecuteCreateHabitAsync(
        AiAction action, Guid userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(action.Title))
            return Result.Failure("Title is required to create a habit.");

        var habitResult = Habit.Create(
            userId,
            action.Title,
            action.Frequency ?? HabitFrequency.Daily,
            action.HabitType ?? HabitType.Boolean,
            action.Description,
            action.Unit);

        if (habitResult.IsFailure)
            return Result.Failure(habitResult.Error);

        await habitRepository.AddAsync(habitResult.Value, ct);
        return Result.Success();
    }

    private async Task<Result> ExecuteCreateTaskAsync(
        AiAction action, Guid userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(action.Title))
            return Result.Failure("Title is required to create a task.");

        var taskResult = TaskItem.Create(userId, action.Title, action.Description, action.DueDate);

        if (taskResult.IsFailure)
            return Result.Failure(taskResult.Error);

        await taskRepository.AddAsync(taskResult.Value, ct);
        return Result.Success();
    }

    private async Task<Result> ExecuteUpdateTaskAsync(AiAction action, CancellationToken ct)
    {
        if (action.TaskId is null)
            return Result.Failure("Task ID is required for updating.");

        var task = await taskRepository.GetByIdAsync(action.TaskId.Value, ct);

        if (task is null)
            return Result.Failure($"Task {action.TaskId} not found.");

        if (action.NewStatus is null)
            return Result.Failure("New status is required for updating a task.");

        var result = action.NewStatus switch
        {
            TaskItemStatus.Completed => task.MarkCompleted(),
            TaskItemStatus.Cancelled => task.Cancel(),
            TaskItemStatus.InProgress => task.StartProgress(),
            _ => Result.Failure($"Cannot transition to status: {action.NewStatus}")
        };

        if (result.IsSuccess)
            taskRepository.Update(task);

        return result;
    }
}
