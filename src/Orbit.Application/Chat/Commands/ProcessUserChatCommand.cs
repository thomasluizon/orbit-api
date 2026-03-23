using System.Globalization;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat.Commands;

public record ProcessUserChatCommand(
    Guid UserId,
    string Message,
    byte[]? ImageData = null,
    string? ImageMimeType = null,
    List<ChatHistoryMessage>? History = null) : IRequest<Result<ChatResponse>>;

public record ChatResponse(string? AiMessage, IReadOnlyList<ActionResult> Actions);

public record ActionResult(
    string Type,
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
    IGenericRepository<User> userRepository,
    IGenericRepository<UserFact> userFactRepository,
    IGenericRepository<Tag> tagRepository,
    IAiIntentService aiIntentService,
    IFactExtractionService factExtractionService,
    IRoutineAnalysisService routineAnalysisService,
    IAppConfigService appConfigService,
    IUserDateService userDateService,
    IPayGateService payGate,
    IUnitOfWork unitOfWork,
    AiToolRegistry toolRegistry,
    ISystemPromptBuilder systemPromptBuilder,
    ILogger<ProcessUserChatCommandHandler> logger) : IRequestHandler<ProcessUserChatCommand, Result<ChatResponse>>
{
    private const int MaxToolIterations = 5;

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
            h => h.UserId == request.UserId,
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

        // Check AI message limits
        var messageGate = await payGate.CanSendAiMessage(request.UserId, cancellationToken);
        if (messageGate.IsFailure)
            return messageGate.PropagateError<ChatResponse>();

        // 1d. Retrieve user's facts as context for the AI (skip if memory disabled)
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

        // 1e. Analyze user's routine patterns for AI context (non-critical)
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

        // 2. Build system prompt and tool declarations
        logger.LogInformation("Building system prompt and tool declarations...");

        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        var systemPrompt = systemPromptBuilder.Build(
            activeHabits, userFacts,
            hasImage: request.ImageData is not null,
            routinePatterns, userTags, userToday, metricsDict);

        var tools = toolRegistry.GetAll();
        var toolDeclarations = tools.Select(t => (object)new
        {
            name = t.Name,
            description = t.Description,
            parameters = t.GetParameterSchema()
        }).ToList();

        // 3. Call AI with tool declarations
        logger.LogInformation("Calling AI intent service with {ToolCount} tools...", toolDeclarations.Count);
        var aiStopwatch = System.Diagnostics.Stopwatch.StartNew();

        var response = await aiIntentService.SendWithToolsAsync(
            request.Message,
            systemPrompt,
            toolDeclarations,
            request.ImageData,
            request.ImageMimeType,
            request.History,
            cancellationToken);

        aiStopwatch.Stop();
        logger.LogInformation("AI intent service completed in {ElapsedMs}ms", aiStopwatch.ElapsedMilliseconds);

        if (response.IsFailure)
            return Result.Failure<ChatResponse>(response.Error);

        var aiResponse = response.Value;

        // 4. Agentic tool-calling loop
        var allActionResults = new List<ActionResult>();
        var actionsStopwatch = System.Diagnostics.Stopwatch.StartNew();
        int iteration = 0;

        while (aiResponse.HasToolCalls && iteration < MaxToolIterations)
        {
            iteration++;
            logger.LogInformation("Tool-calling iteration {Iteration}, {CallCount} calls",
                iteration, aiResponse.ToolCalls!.Count);

            var toolResults = new List<AiToolCallResult>();

            foreach (var call in aiResponse.ToolCalls!)
            {
                var tool = toolRegistry.GetTool(call.Name);
                if (tool is null)
                {
                    logger.LogWarning("Unknown tool requested: {Name}", call.Name);
                    toolResults.Add(new AiToolCallResult(call.Name, call.Id, false, null, null, $"Unknown tool: {call.Name}"));
                    allActionResults.Add(new ActionResult(ToolNameToPascalCase(call.Name), ActionStatus.Failed, Error: $"Unknown tool: {call.Name}"));
                    continue;
                }

                try
                {
                    var result = await tool.ExecuteAsync(call.Args, request.UserId, cancellationToken);
                    toolResults.Add(new AiToolCallResult(call.Name, call.Id, result.Success, result.EntityId, result.EntityName, result.Error));

                    if (result.Success)
                    {
                        // Special handling for suggest_breakdown: carry suggestions in ActionResult
                        if (call.Name == "suggest_breakdown")
                        {
                            var suggestedSubHabits = ExtractSuggestedSubHabits(call.Args);
                            allActionResults.Add(new ActionResult(
                                ToolNameToPascalCase(call.Name),
                                ActionStatus.Suggestion,
                                EntityName: result.EntityName,
                                SuggestedSubHabits: suggestedSubHabits));
                        }
                        else
                        {
                            allActionResults.Add(new ActionResult(
                                ToolNameToPascalCase(call.Name),
                                ActionStatus.Success,
                                result.EntityId is not null ? Guid.Parse(result.EntityId) : null,
                                result.EntityName));
                        }

                        logger.LogInformation("Tool {Name} succeeded: {EntityName}", call.Name, result.EntityName);
                    }
                    else
                    {
                        allActionResults.Add(new ActionResult(
                            ToolNameToPascalCase(call.Name),
                            ActionStatus.Failed,
                            EntityName: result.EntityName,
                            Error: result.Error));
                        logger.LogWarning("Tool {Name} failed: {Error}", call.Name, result.Error);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Tool {Name} threw an exception", call.Name);
                    toolResults.Add(new AiToolCallResult(call.Name, call.Id, false, null, null, "An unexpected error occurred."));
                    allActionResults.Add(new ActionResult(
                        ToolNameToPascalCase(call.Name),
                        ActionStatus.Failed,
                        Error: "An unexpected error occurred."));
                }
            }

            // Send results back to the AI for next iteration or final message
            var continueResult = await aiIntentService.ContinueWithToolResultsAsync(toolResults, cancellationToken);
            if (continueResult.IsFailure)
            {
                logger.LogWarning("ContinueWithToolResultsAsync failed: {Error}", continueResult.Error);
                break;
            }
            aiResponse = continueResult.Value;
        }

        actionsStopwatch.Stop();
        logger.LogInformation("Tool execution completed in {ElapsedMs}ms ({Iterations} iterations, {ActionCount} actions)",
            actionsStopwatch.ElapsedMilliseconds, iteration, allActionResults.Count);

        // 5. Persist all changes in a single unit of work
        logger.LogInformation("Saving changes to database...");
        var saveStopwatch = System.Diagnostics.Stopwatch.StartNew();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        saveStopwatch.Stop();
        logger.LogInformation("Changes saved in {ElapsedMs}ms", saveStopwatch.ElapsedMilliseconds);

        // 6. Extract AI message (strip JSON wrapper if model didn't use function calling)
        var aiMessage = aiResponse.TextMessage;
        if (aiMessage is not null && aiMessage.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(aiMessage);
                if (doc.RootElement.TryGetProperty("aiMessage", out var msgEl))
                    aiMessage = msgEl.GetString();
            }
            catch (JsonException)
            {
                // Not valid JSON, use as-is
            }
        }

        // 7. Extract facts from conversation (non-blocking - failure doesn't affect response)

        if (!aiMemoryEnabled || (user is not null && !user.HasProAccess))
        {
            logger.LogInformation("AI memory disabled for user or not Pro, skipping fact extraction");
        }
        else try
        {
            logger.LogInformation("Extracting facts from conversation...");
            var extractionStopwatch = System.Diagnostics.Stopwatch.StartNew();

            var extractionResult = await factExtractionService.ExtractFactsAsync(
                request.Message,
                aiMessage,
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

        // 7. Increment AI message counter (after all other processing)
        try
        {
            var userForIncrement = await userRepository.FindOneTrackedAsync(u => u.Id == request.UserId, cancellationToken: cancellationToken);
            userForIncrement?.IncrementAiMessageCount();
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch { /* non-critical */ }

        totalStopwatch.Stop();
        logger.LogInformation("TOTAL request processing time: {ElapsedMs}ms", totalStopwatch.ElapsedMilliseconds);
        logger.LogInformation("   DB queries: {DbMs}ms", dbStopwatch.ElapsedMilliseconds);
        logger.LogInformation("   AI service: {AiMs}ms", aiStopwatch.ElapsedMilliseconds);
        logger.LogInformation("   Tool execution: {ActionsMs}ms", actionsStopwatch.ElapsedMilliseconds);
        logger.LogInformation("   Save changes: {SaveMs}ms", saveStopwatch.ElapsedMilliseconds);

        return Result.Success(new ChatResponse(aiMessage, allActionResults));
    }

    /// <summary>
    /// Converts snake_case tool names to PascalCase for backward compatibility with the frontend.
    /// e.g., "log_habit" -> "LogHabit", "create_sub_habit" -> "CreateSubHabit"
    /// </summary>
    private static string ToolNameToPascalCase(string toolName)
    {
        var parts = toolName.Split('_');
        return string.Concat(parts.Select(p =>
            string.IsNullOrEmpty(p) ? p : char.ToUpper(p[0], CultureInfo.InvariantCulture) + p[1..]));
    }

    /// <summary>
    /// Extracts suggested sub-habits from the suggest_breakdown tool call args
    /// for backward-compatible ActionResult.SuggestedSubHabits.
    /// </summary>
    private static IReadOnlyList<AiAction>? ExtractSuggestedSubHabits(JsonElement args)
    {
        if (!args.TryGetProperty("suggested_sub_habits", out var subHabitsEl) ||
            subHabitsEl.ValueKind != JsonValueKind.Array)
            return null;

        var suggestions = new List<AiAction>();
        foreach (var item in subHabitsEl.EnumerateArray())
        {
            string? title = null;
            string? description = null;
            FrequencyUnit? frequencyUnit = null;
            int? frequencyQuantity = null;
            List<DayOfWeek>? days = null;

            if (item.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
                title = titleEl.GetString();

            if (item.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
                description = descEl.GetString();

            if (item.TryGetProperty("frequency_unit", out var freqEl) && freqEl.ValueKind == JsonValueKind.String)
            {
                if (Enum.TryParse<FrequencyUnit>(freqEl.GetString(), true, out var fu))
                    frequencyUnit = fu;
            }

            if (item.TryGetProperty("frequency_quantity", out var fqEl) && fqEl.ValueKind == JsonValueKind.Number)
                frequencyQuantity = fqEl.GetInt32();

            if (item.TryGetProperty("days", out var daysEl) && daysEl.ValueKind == JsonValueKind.Array)
            {
                days = [];
                foreach (var dayEl in daysEl.EnumerateArray())
                {
                    if (dayEl.ValueKind == JsonValueKind.String &&
                        Enum.TryParse<DayOfWeek>(dayEl.GetString(), true, out var dow))
                        days.Add(dow);
                }
            }

            suggestions.Add(new AiAction
            {
                Type = AiActionType.SuggestBreakdown,
                Title = title,
                Description = description,
                FrequencyUnit = frequencyUnit,
                FrequencyQuantity = frequencyQuantity,
                Days = days
            });
        }

        return suggestions.Count > 0 ? suggestions : null;
    }
}
