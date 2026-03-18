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
    string? ImageMimeType = null,
    List<ChatHistoryMessage>? History = null) : IRequest<Result<ChatResponse>>;

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
    IMediator mediator,
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IGenericRepository<User> userRepository,
    IGenericRepository<UserFact> userFactRepository,
    IGenericRepository<Tag> tagRepository,
    IAiIntentService aiIntentService,
    IFactExtractionService factExtractionService,
    IRoutineAnalysisService routineAnalysisService,
    IAppConfigService appConfigService,
    IUserDateService userDateService,
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
            q => q.Include(h => h.Tags),
            cancellationToken);

        var userTags = await tagRepository.FindAsync(
            t => t.UserId == request.UserId,
            cancellationToken);

        dbStopwatch.Stop();
        logger.LogInformation("Database query completed in {ElapsedMs}ms (Habits: {HabitCount})",
            dbStopwatch.ElapsedMilliseconds, activeHabits.Count);

        // 1b. Fetch metrics for all habits (for AI context)
        var metricsDict = new Dictionary<Guid, HabitMetrics>();
        try
        {
            var metricsStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var metricsTasks = activeHabits.Select(h =>
                mediator.Send(new Orbit.Application.Habits.Queries.GetHabitMetricsQuery(request.UserId, h.Id), cancellationToken));
            var metricsResults = await Task.WhenAll(metricsTasks);
            for (int i = 0; i < activeHabits.Count; i++)
            {
                if (metricsResults[i].IsSuccess)
                    metricsDict[activeHabits[i].Id] = metricsResults[i].Value;
            }
            metricsStopwatch.Stop();
            logger.LogInformation("Metrics fetched for {Count} habits in {ElapsedMs}ms", metricsDict.Count, metricsStopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Metrics fetch failed - non-critical, continuing without metrics");
        }

        // 1c. Check if user has AI memory enabled
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        var aiMemoryEnabled = user?.AiMemoryEnabled ?? true;

        // 1c. Retrieve user's facts as context for the AI (skip if memory disabled)
        IReadOnlyList<UserFact> userFacts = [];
        if (aiMemoryEnabled)
        {
            logger.LogInformation("Fetching user facts from database...");
            var factDbStopwatch = System.Diagnostics.Stopwatch.StartNew();

            userFacts = await userFactRepository.FindAsync(
                f => f.UserId == request.UserId,
                cancellationToken);

            factDbStopwatch.Stop();
            logger.LogInformation("Fact query completed in {ElapsedMs}ms (Facts: {FactCount})",
                factDbStopwatch.ElapsedMilliseconds, userFacts.Count);
        }
        else
        {
            logger.LogInformation("AI memory disabled for user, skipping fact retrieval");
        }

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

        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        var planResult = await aiIntentService.InterpretAsync(
            request.Message,
            activeHabits,
            userFacts,
            request.ImageData,
            request.ImageMimeType,
            routinePatterns,
            userTags,
            userToday,
            metricsDict,
            request.History,
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
                    AiActionType.SuggestBreakdown => ExecuteSuggestBreakdown(action),
                    AiActionType.AssignTags => await ExecuteAssignTagsAsync(action, request.UserId, cancellationToken),
                    AiActionType.UpdateHabit => await ExecuteUpdateHabitAsync(action, request.UserId, cancellationToken),
                    AiActionType.DeleteHabit => await ExecuteDeleteHabitAsync(action, request.UserId, cancellationToken),
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
        if (!aiMemoryEnabled)
        {
            logger.LogInformation("AI memory disabled for user, skipping fact extraction");
        }
        else try
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
                var maxFacts = await appConfigService.GetAsync("MaxUserFacts", 50, cancellationToken);
                if (userFacts.Count >= maxFacts)
                {
                    logger.LogInformation("User has reached {MaxFacts} fact limit, skipping extraction persistence", maxFacts);
                }
                else
                {
                    var remaining = maxFacts - userFacts.Count;
                    logger.LogInformation("Extracted {FactCount} facts in {ElapsedMs}ms (room for {Remaining})",
                        extractionResult.Value.Facts.Count, extractionStopwatch.ElapsedMilliseconds, remaining);

                    foreach (var candidate in extractionResult.Value.Facts.Take(remaining))
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

        var today = await userDateService.GetUserTodayAsync(userId, ct);
        var logResult = habit.Log(today, action.Note);

        if (logResult.IsFailure)
            return Result.Failure<(Guid? Id, string? Name)>(logResult.Error);

        await habitLogRepository.AddAsync(logResult.Value, ct);
        return Result.Success<(Guid? Id, string? Name)>((habit.Id, habit.Title));
    }

    private async Task<Result<(Guid? Id, string? Name)>> ExecuteCreateHabitAsync(
        AiAction action, Guid userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(action.Title))
            return Result.Failure<(Guid? Id, string? Name)>("Title is required to create a habit.");

        var dueDate = action.DueDate ?? await userDateService.GetUserTodayAsync(userId, ct);

        var habitResult = Habit.Create(
            userId,
            action.Title,
            action.FrequencyUnit,
            action.FrequencyQuantity,
            action.Description,
            days: action.Days,
            isBadHabit: action.IsBadHabit ?? false,
            dueDate: dueDate);

        if (habitResult.IsFailure)
            return Result.Failure<(Guid? Id, string? Name)>(habitResult.Error);

        var habit = habitResult.Value;

        // Handle inline sub-habits as child Habit entities
        if (action.SubHabits is { Count: > 0 })
        {
            foreach (var sub in action.SubHabits)
            {
                var childResult = Habit.Create(
                    userId,
                    sub.Title ?? "Untitled",
                    sub.FrequencyUnit ?? action.FrequencyUnit,
                    sub.FrequencyQuantity ?? action.FrequencyQuantity,
                    sub.Description,
                    days: sub.Days ?? action.Days,
                    isBadHabit: sub.IsBadHabit ?? false,
                    dueDate: sub.DueDate ?? dueDate,
                    parentHabitId: habit.Id);

                if (childResult.IsFailure)
                    return Result.Failure<(Guid? Id, string? Name)>(childResult.Error);

                await habitRepository.AddAsync(childResult.Value, ct);
            }
        }

        await habitRepository.AddAsync(habit, ct);

        // Assign tags if specified
        if (action.TagNames is { Count: > 0 })
        {
            await AssignTagsToHabitAsync(habit, action.TagNames, userId, ct);
        }

        return Result.Success<(Guid? Id, string? Name)>((habit.Id, habit.Title));
    }

    private static Result<(Guid? Id, string? Name)> ExecuteSuggestBreakdown(AiAction action)
    {
        // SuggestBreakdown creates nothing -- it just passes through the AI's suggestions
        // The ActionResult will carry the suggestions for frontend rendering
        return Result.Success<(Guid? Id, string? Name)>((null, action.Title));
    }

    private async Task<Result<(Guid? Id, string? Name)>> ExecuteAssignTagsAsync(
        AiAction action, Guid userId, CancellationToken ct)
    {
        if (action.HabitId is null)
            return Result.Failure<(Guid? Id, string? Name)>("Habit ID is required for assigning tags.");

        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == action.HabitId.Value && h.UserId == userId,
            q => q.Include(h => h.Tags),
            ct);

        if (habit is null)
            return Result.Failure<(Guid? Id, string? Name)>($"Habit {action.HabitId} not found.");

        var resolvedTags = await ResolveTagsByNameAsync(action.TagNames, userId, ct);

        // Clear existing and assign new
        foreach (var existing in habit.Tags.ToList())
            habit.RemoveTag(existing);
        foreach (var tag in resolvedTags)
            habit.AddTag(tag);

        return Result.Success<(Guid? Id, string? Name)>((habit.Id, habit.Title));
    }

    private async Task<Result<(Guid? Id, string? Name)>> ExecuteUpdateHabitAsync(
        AiAction action, Guid userId, CancellationToken ct)
    {
        if (action.HabitId is null)
            return Result.Failure<(Guid? Id, string? Name)>("Habit ID is required for updating.");

        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == action.HabitId.Value && h.UserId == userId,
            cancellationToken: ct);

        if (habit is null)
            return Result.Failure<(Guid? Id, string? Name)>($"Habit {action.HabitId} not found.");

        var result = habit.Update(
            action.Title ?? habit.Title,
            action.Description ?? habit.Description,
            action.FrequencyUnit ?? habit.FrequencyUnit,
            action.FrequencyQuantity ?? habit.FrequencyQuantity,
            action.Days,
            action.IsBadHabit ?? habit.IsBadHabit,
            action.DueDate ?? habit.DueDate);

        if (result.IsFailure)
            return Result.Failure<(Guid? Id, string? Name)>(result.Error);

        return Result.Success<(Guid? Id, string? Name)>((habit.Id, habit.Title));
    }

    private async Task<Result<(Guid? Id, string? Name)>> ExecuteDeleteHabitAsync(
        AiAction action, Guid userId, CancellationToken ct)
    {
        if (action.HabitId is null)
            return Result.Failure<(Guid? Id, string? Name)>("Habit ID is required for deleting.");

        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == action.HabitId.Value && h.UserId == userId,
            cancellationToken: ct);

        if (habit is null)
            return Result.Failure<(Guid? Id, string? Name)>($"Habit {action.HabitId} not found.");

        var title = habit.Title;
        habit.Deactivate();

        return Result.Success<(Guid? Id, string? Name)>((habit.Id, title));
    }

    private async Task AssignTagsToHabitAsync(Habit habit, List<string>? tagNames, Guid userId, CancellationToken ct)
    {
        if (tagNames is not { Count: > 0 }) return;

        var resolvedTags = await ResolveTagsByNameAsync(tagNames, userId, ct);
        foreach (var tag in resolvedTags)
            habit.AddTag(tag);
    }

    private async Task<List<Tag>> ResolveTagsByNameAsync(List<string>? tagNames, Guid userId, CancellationToken ct)
    {
        if (tagNames is not { Count: > 0 }) return [];

        var resolved = new List<Tag>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in tagNames)
        {
            var capitalized = Capitalize(name.Trim());
            if (string.IsNullOrEmpty(capitalized) || !seen.Add(capitalized)) continue;

            // Use tracked query so EF doesn't try to re-insert existing tags
            var existing = await tagRepository.FindOneTrackedAsync(
                t => t.UserId == userId && t.Name == capitalized, cancellationToken: ct);

            if (existing is not null)
            {
                resolved.Add(existing);
            }
            else
            {
                var createResult = Tag.Create(userId, capitalized, "#7c3aed");
                if (createResult.IsSuccess)
                {
                    await tagRepository.AddAsync(createResult.Value, ct);
                    resolved.Add(createResult.Value);
                }
            }
        }

        return resolved;
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..].ToLower();
}
