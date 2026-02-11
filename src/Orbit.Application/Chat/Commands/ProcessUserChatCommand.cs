using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public record ProcessUserChatCommand(
    Guid UserId,
    string Message,
    byte[]? ImageData = null,
    string? ImageMimeType = null) : IRequest<Result<ChatResponse>>;

public record ChatResponse(string? AiMessage, IReadOnlyList<ActionResult> Actions);

public record ActionResult(
    AiActionType Type,
    ActionStatus Status,
    Guid? EntityId = null,
    string? EntityName = null,
    string? Error = null,
    string? Field = null,
    IReadOnlyList<AiAction>? SuggestedSubHabits = null,
    ConflictWarning? ConflictWarning = null);

public enum ActionStatus { Success, Failed, Suggestion }

public class ProcessUserChatCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IGenericRepository<User> userRepository,
    IGenericRepository<Tag> tagRepository,
    IGenericRepository<UserFact> userFactRepository,
    IAiIntentService aiIntentService,
    IFactExtractionService factExtractionService,
    IRoutineAnalysisService routineAnalysisService,
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

        // 1c. Retrieve user's facts as context for the AI
        logger.LogInformation("Fetching user facts from database...");
        var factDbStopwatch = System.Diagnostics.Stopwatch.StartNew();

        var userFacts = await userFactRepository.FindAsync(
            f => f.UserId == request.UserId,
            cancellationToken);

        factDbStopwatch.Stop();
        logger.LogInformation("Fact query completed in {ElapsedMs}ms (Facts: {FactCount})",
            factDbStopwatch.ElapsedMilliseconds, userFacts.Count);

        // 1d. Analyze user's routine patterns for AI context (non-critical)
        logger.LogInformation("Fetching user's routine patterns...");
        var routineStopwatch = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<RoutinePattern> routinePatterns = [];
        try
        {
            var routineResult = await routineAnalysisService.AnalyzeRoutinesAsync(request.UserId, cancellationToken);
            if (routineResult.IsSuccess)
                routinePatterns = routineResult.Value.Patterns;

            routineStopwatch.Stop();
            logger.LogInformation("Routine analysis completed in {ElapsedMs}ms (Patterns: {PatternCount})",
                routineStopwatch.ElapsedMilliseconds, routinePatterns.Count);
        }
        catch (Exception ex)
        {
            routineStopwatch.Stop();
            logger.LogWarning(ex, "Routine analysis failed in {ElapsedMs}ms - non-critical, continuing without patterns",
                routineStopwatch.ElapsedMilliseconds);
        }

        // 2. Send text + context to the AI intent service for interpretation
        logger.LogInformation("Calling AI intent service...");
        var aiStopwatch = System.Diagnostics.Stopwatch.StartNew();

        var planResult = await aiIntentService.InterpretAsync(
            request.Message,
            activeHabits,
            userTags,
            userFacts,
            request.ImageData,
            request.ImageMimeType,
            routinePatterns,
            cancellationToken);

        aiStopwatch.Stop();
        logger.LogInformation("AI intent service completed in {ElapsedMs}ms", aiStopwatch.ElapsedMilliseconds);

        if (planResult.IsFailure)
            return Result.Failure<ChatResponse>(planResult.Error);

        var plan = planResult.Value;
        var actionResults = new List<ActionResult>();
        var conflictWarnings = new Dictionary<int, ConflictWarning?>();

        // 3. Execute each action returned by the AI
        logger.LogInformation("Executing {ActionCount} actions...", plan.Actions.Count);
        var actionsStopwatch = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < plan.Actions.Count; i++)
        {
            var action = plan.Actions[i];
            logger.LogInformation("Executing action: {ActionType} - {Title}", action.Type, action.Title ?? action.HabitId?.ToString() ?? "N/A");

            try
            {
                var actionResult = action.Type switch
                {
                    AiActionType.LogHabit => await ExecuteLogHabitAsync(action, request.UserId, cancellationToken),
                    AiActionType.CreateHabit => await ExecuteCreateHabitAsync(action, request.UserId, cancellationToken),
                    AiActionType.AssignTag => await ExecuteAssignTagAsync(action, request.UserId, cancellationToken),
                    AiActionType.SuggestBreakdown => ExecuteSuggestBreakdown(action),
                    _ => Result.Failure<(Guid? Id, string? Name)>($"Unknown action type: {action.Type}")
                };

                if (actionResult.IsSuccess)
                {
                    var (id, name) = actionResult.Value;

                    // Detect conflicts for CreateHabit actions (non-critical)
                    if (action.Type == AiActionType.CreateHabit && action.FrequencyUnit.HasValue)
                    {
                        try
                        {
                            var conflictResult = await routineAnalysisService.DetectConflictsAsync(
                                request.UserId, action.FrequencyUnit, action.FrequencyQuantity, action.Days, cancellationToken);
                            if (conflictResult.IsSuccess)
                                conflictWarnings[i] = conflictResult.Value;
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Conflict detection failed - non-critical");
                        }
                    }

                    if (action.Type == AiActionType.SuggestBreakdown)
                    {
                        actionResults.Add(new ActionResult(
                            action.Type,
                            ActionStatus.Suggestion,
                            EntityName: action.Title,
                            SuggestedSubHabits: action.SuggestedSubHabits));
                    }
                    else
                    {
                        actionResults.Add(new ActionResult(
                            action.Type,
                            ActionStatus.Success,
                            id,
                            name,
                            ConflictWarning: conflictWarnings.GetValueOrDefault(i)));
                    }

                    logger.LogInformation("Action succeeded: {ActionType}", action.Type);
                }
                else
                {
                    actionResults.Add(new ActionResult(action.Type, ActionStatus.Failed, EntityName: action.Title, Error: actionResult.Error));
                    logger.LogError("Action failed: {ActionType} - Error: {Error}", action.Type, actionResult.Error);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error executing action {Type}", action.Type);
                actionResults.Add(new ActionResult(action.Type, ActionStatus.Failed, EntityName: action.Title, Error: "An unexpected error occurred."));
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

        // 5. Extract facts from conversation (non-blocking - failure doesn't affect response)
        try
        {
            logger.LogInformation("Extracting facts from conversation...");
            var extractionStopwatch = System.Diagnostics.Stopwatch.StartNew();

            var extractionResult = await factExtractionService.ExtractFactsAsync(
                request.Message,
                plan.AiMessage,
                userFacts,
                cancellationToken);

            extractionStopwatch.Stop();

            if (extractionResult.IsSuccess && extractionResult.Value.Facts.Count > 0)
            {
                logger.LogInformation("Extracted {FactCount} facts in {ElapsedMs}ms",
                    extractionResult.Value.Facts.Count, extractionStopwatch.ElapsedMilliseconds);

                foreach (var candidate in extractionResult.Value.Facts)
                {
                    var factResult = UserFact.Create(request.UserId, candidate.FactText, candidate.Category);
                    if (factResult.IsSuccess)
                    {
                        await userFactRepository.AddAsync(factResult.Value, cancellationToken);
                    }
                    else
                    {
                        logger.LogWarning("Failed to create fact: {Error}", factResult.Error);
                    }
                }

                await unitOfWork.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Facts persisted to database");
            }
            else
            {
                logger.LogInformation("No facts extracted from conversation in {ElapsedMs}ms",
                    extractionStopwatch.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fact extraction failed - non-critical, continuing");
        }

        totalStopwatch.Stop();
        logger.LogInformation("TOTAL request processing time: {ElapsedMs}ms", totalStopwatch.ElapsedMilliseconds);
        logger.LogInformation("   DB queries: {DbMs}ms", dbStopwatch.ElapsedMilliseconds);
        logger.LogInformation("   AI service: {AiMs}ms", aiStopwatch.ElapsedMilliseconds);
        logger.LogInformation("   Execute actions: {ActionsMs}ms", actionsStopwatch.ElapsedMilliseconds);
        logger.LogInformation("   Save changes: {SaveMs}ms", saveStopwatch.ElapsedMilliseconds);

        return Result.Success(new ChatResponse(plan.AiMessage, actionResults));
    }

    private async Task<Result<(Guid? Id, string? Name)>> ExecuteLogHabitAsync(
        AiAction action, Guid userId, CancellationToken ct)
    {
        if (action.HabitId is null)
            return Result.Failure<(Guid? Id, string? Name)>("Habit ID is required for logging.");

        var habit = await habitRepository.GetByIdAsync(action.HabitId.Value, ct);

        if (habit is null)
            return Result.Failure<(Guid? Id, string? Name)>($"Habit {action.HabitId} not found.");

        if (habit.UserId != userId)
            return Result.Failure<(Guid? Id, string? Name)>("Habit does not belong to this user.");

        var user = await userRepository.GetByIdAsync(userId, ct);
        var today = GetUserToday(user);
        var logResult = habit.Log(today, action.Note);

        if (logResult.IsFailure)
            return Result.Failure<(Guid? Id, string? Name)>(logResult.Error);

        await habitLogRepository.AddAsync(logResult.Value, ct);
        return Result.Success<(Guid? Id, string? Name)>((habit.Id, habit.Title));
    }

    private static DateOnly GetUserToday(User? user)
    {
        var timeZone = user?.TimeZone is not null
            ? TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone)
            : TimeZoneInfo.Utc;

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone));
    }

    private async Task<Result<(Guid? Id, string? Name)>> ExecuteCreateHabitAsync(
        AiAction action, Guid userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(action.Title))
            return Result.Failure<(Guid? Id, string? Name)>("Title is required to create a habit.");

        var habitResult = Habit.Create(
            userId,
            action.Title,
            action.FrequencyUnit,
            action.FrequencyQuantity,
            action.Description,
            days: action.Days,
            isBadHabit: action.IsBadHabit ?? false,
            dueDate: action.DueDate);

        if (habitResult.IsFailure)
            return Result.Failure<(Guid? Id, string? Name)>(habitResult.Error);

        var habit = habitResult.Value;

        // Handle inline sub-habits as child Habit entities
        if (action.SubHabits is { Count: > 0 })
        {
            foreach (var subTitle in action.SubHabits)
            {
                var childResult = Habit.Create(
                    userId,
                    subTitle,
                    action.FrequencyUnit,
                    action.FrequencyQuantity,
                    dueDate: action.DueDate,
                    parentHabitId: habit.Id);

                if (childResult.IsFailure)
                    return Result.Failure<(Guid? Id, string? Name)>(childResult.Error);

                await habitRepository.AddAsync(childResult.Value, ct);
            }
        }

        await habitRepository.AddAsync(habit, ct);
        return Result.Success<(Guid? Id, string? Name)>((habit.Id, habit.Title));
    }

    private async Task<Result<(Guid? Id, string? Name)>> ExecuteAssignTagAsync(
        AiAction action, Guid userId, CancellationToken ct)
    {
        if (action.HabitId is null || action.TagIds is null || action.TagIds.Count == 0)
            return Result.Failure<(Guid? Id, string? Name)>("HabitId and TagIds are required for AssignTag.");

        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == action.HabitId && h.UserId == userId,
            q => q.Include(h => h.Tags),
            ct);

        if (habit is null)
            return Result.Failure<(Guid? Id, string? Name)>("Habit not found.");

        foreach (var tagId in action.TagIds)
        {
            var tag = await tagRepository.GetByIdAsync(tagId, ct);
            if (tag is null || tag.UserId != userId)
                continue; // Skip invalid/unauthorized tags silently

            if (!habit.Tags.Any(t => t.Id == tagId))
                habit.Tags.Add(tag);
        }

        return Result.Success<(Guid? Id, string? Name)>((habit.Id, habit.Title));
    }

    private static Result<(Guid? Id, string? Name)> ExecuteSuggestBreakdown(AiAction action)
    {
        // SuggestBreakdown creates nothing -- it just passes through the AI's suggestions
        // The ActionResult will carry the suggestions for frontend rendering
        return Result.Success<(Guid? Id, string? Name)>((null, action.Title));
    }
}
