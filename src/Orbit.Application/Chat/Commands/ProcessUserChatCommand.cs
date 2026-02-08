using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    IGenericRepository<Tag> tagRepository,
    IAiIntentService aiIntentService,
    IUnitOfWork unitOfWork,
    ILogger<ProcessUserChatCommandHandler> logger) : IRequestHandler<ProcessUserChatCommand, Result<ChatResponse>>
{
    public async Task<Result<ChatResponse>> Handle(
        ProcessUserChatCommand request,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        logger.LogInformation("Processing chat message: '{Message}'", request.Message);

        // 1. Retrieve user's active habits as context for the AI
        logger.LogInformation("Fetching habits from database...");
        var dbStopwatch = System.Diagnostics.Stopwatch.StartNew();

        var activeHabits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId && h.IsActive,
            cancellationToken);

        dbStopwatch.Stop();
        logger.LogInformation("Database query completed in {ElapsedMs}ms (Habits: {HabitCount})",
            dbStopwatch.ElapsedMilliseconds, activeHabits.Count);

        // 1b. Retrieve user's tags as context for the AI
        logger.LogInformation("Fetching tags from database...");
        var tagDbStopwatch = System.Diagnostics.Stopwatch.StartNew();

        var userTags = await tagRepository.FindAsync(
            t => t.UserId == request.UserId,
            cancellationToken);

        tagDbStopwatch.Stop();
        logger.LogInformation("Tag query completed in {ElapsedMs}ms (Tags: {TagCount})",
            tagDbStopwatch.ElapsedMilliseconds, userTags.Count);

        // 2. Send text + context to the AI intent service for interpretation
        logger.LogInformation("Calling AI intent service...");
        var aiStopwatch = System.Diagnostics.Stopwatch.StartNew();

        var planResult = await aiIntentService.InterpretAsync(
            request.Message,
            activeHabits,
            userTags,
            cancellationToken);

        aiStopwatch.Stop();
        logger.LogInformation("AI intent service completed in {ElapsedMs}ms", aiStopwatch.ElapsedMilliseconds);

        if (planResult.IsFailure)
            return Result.Failure<ChatResponse>(planResult.Error);

        var plan = planResult.Value;
        var executedActions = new List<string>();

        // 3. Execute each action returned by the AI
        logger.LogInformation("Executing {ActionCount} actions...", plan.Actions.Count);
        var actionsStopwatch = System.Diagnostics.Stopwatch.StartNew();

        foreach (var action in plan.Actions)
        {
            logger.LogInformation("Executing action: {ActionType} - {Title}", action.Type, action.Title ?? action.HabitId?.ToString() ?? "N/A");

            var actionResult = action.Type switch
            {
                AiActionType.LogHabit => await ExecuteLogHabitAsync(action, request.UserId, cancellationToken),
                AiActionType.CreateHabit => await ExecuteCreateHabitAsync(action, request.UserId, cancellationToken),
                AiActionType.CreateSubHabit => await ExecuteCreateSubHabitAsync(action, request.UserId, cancellationToken),
                AiActionType.AssignTag => await ExecuteAssignTagAsync(action, request.UserId, cancellationToken),
                _ => Result.Failure($"Unknown action type: {action.Type}")
            };

            if (actionResult.IsSuccess)
            {
                executedActions.Add($"{action.Type}: {action.Title ?? action.HabitId?.ToString() ?? "N/A"}");
                logger.LogInformation("Action succeeded: {ActionType}", action.Type);
            }
            else
            {
                logger.LogError("Action failed: {ActionType} - Error: {Error}", action.Type, actionResult.Error);
            }
        }

        actionsStopwatch.Stop();
        logger.LogInformation("Actions executed in {ElapsedMs}ms", actionsStopwatch.ElapsedMilliseconds);

        // 4. Persist all changes in a single unit of work
        logger.LogInformation("Saving changes to database...");
        var saveStopwatch = System.Diagnostics.Stopwatch.StartNew();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        saveStopwatch.Stop();
        logger.LogInformation("Changes saved in {ElapsedMs}ms", saveStopwatch.ElapsedMilliseconds);

        totalStopwatch.Stop();
        logger.LogInformation("TOTAL request processing time: {ElapsedMs}ms", totalStopwatch.ElapsedMilliseconds);
        logger.LogInformation("   DB queries: {DbMs}ms", dbStopwatch.ElapsedMilliseconds);
        logger.LogInformation("   AI service: {AiMs}ms", aiStopwatch.ElapsedMilliseconds);
        logger.LogInformation("   Execute actions: {ActionsMs}ms", actionsStopwatch.ElapsedMilliseconds);
        logger.LogInformation("   Save changes: {SaveMs}ms", saveStopwatch.ElapsedMilliseconds);

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

        var date = DateOnly.FromDateTime(DateTime.UtcNow);
        var logResult = habit.Log(date, action.Value, action.Note);

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
            action.FrequencyUnit ?? FrequencyUnit.Day,
            action.FrequencyQuantity ?? 1,
            action.HabitType ?? HabitType.Boolean,
            action.Description,
            action.Unit,
            days: action.Days,
            isNegative: action.IsNegative ?? false);

        if (habitResult.IsFailure)
            return Result.Failure(habitResult.Error);

        var habit = habitResult.Value;

        // Handle inline sub-habits creation
        if (action.SubHabits is not null && action.SubHabits.Count > 0)
        {
            int sortOrder = 0;
            foreach (var subHabitTitle in action.SubHabits)
            {
                var subHabitResult = habit.AddSubHabit(subHabitTitle, sortOrder++);
                if (subHabitResult.IsFailure)
                    return Result.Failure(subHabitResult.Error);
            }
        }

        await habitRepository.AddAsync(habit, ct);
        return Result.Success();
    }

    private async Task<Result> ExecuteCreateSubHabitAsync(
        AiAction action, Guid userId, CancellationToken ct)
    {
        if (action.HabitId is null || action.SubHabits is null || action.SubHabits.Count == 0)
            return Result.Failure("HabitId and SubHabits are required for CreateSubHabit.");

        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == action.HabitId && h.UserId == userId,
            q => q.Include(h => h.SubHabits),
            ct);

        if (habit is null)
            return Result.Failure("Habit not found.");

        int sortOrder = habit.SubHabits.Count;
        foreach (var title in action.SubHabits)
        {
            var result = habit.AddSubHabit(title, sortOrder++);
            if (result.IsFailure)
                return Result.Failure(result.Error);
        }

        return Result.Success();
    }

    private async Task<Result> ExecuteAssignTagAsync(
        AiAction action, Guid userId, CancellationToken ct)
    {
        if (action.HabitId is null || action.TagIds is null || action.TagIds.Count == 0)
            return Result.Failure("HabitId and TagIds are required for AssignTag.");

        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == action.HabitId && h.UserId == userId,
            q => q.Include(h => h.Tags),
            ct);

        if (habit is null)
            return Result.Failure("Habit not found.");

        foreach (var tagId in action.TagIds)
        {
            var tag = await tagRepository.GetByIdAsync(tagId, ct);
            if (tag is null || tag.UserId != userId)
                continue; // Skip invalid/unauthorized tags silently

            if (!habit.Tags.Any(t => t.Id == tagId))
                habit.Tags.Add(tag);
        }

        return Result.Success();
    }
}
