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
using Microsoft.Extensions.DependencyInjection;

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
    IReadOnlyList<AiAction>? SuggestedSubHabits = null);

public enum ActionStatus { Success, Failed, Suggestion }

public partial class ProcessUserChatCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<User> userRepository,
    IGenericRepository<UserFact> userFactRepository,
    IGenericRepository<Tag> tagRepository,
    IAiIntentService aiIntentService,
    IUserDateService userDateService,
    IPayGateService payGate,
    IUnitOfWork unitOfWork,
    AiToolRegistry toolRegistry,
    ISystemPromptBuilder systemPromptBuilder,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<ProcessUserChatCommandHandler> logger) : IRequestHandler<ProcessUserChatCommand, Result<ChatResponse>>
{
    private const int MaxToolIterations = 5;

    public async Task<Result<ChatResponse>> Handle(
        ProcessUserChatCommand request,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        LogProcessingChatMessage(logger, request.Message);

        // 1. Load lightweight context for system prompt (no Logs, no metrics)
        logger.LogInformation("Fetching context from database...");
        var dbStopwatch = System.Diagnostics.Stopwatch.StartNew();

        var activeHabits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId,
            q => q.Include(h => h.Tags),
            cancellationToken);

        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        var aiMemoryEnabled = user?.AiMemoryEnabled ?? true;

        // Check AI message limits
        var messageGate = await payGate.CanSendAiMessage(request.UserId, cancellationToken);
        if (messageGate.IsFailure)
            return messageGate.PropagateError<ChatResponse>();

        IReadOnlyList<UserFact> userFacts = [];
        if (aiMemoryEnabled)
        {
            userFacts = await userFactRepository.FindAsync(
                f => f.UserId == request.UserId,
                cancellationToken);
        }

        var userTags = await tagRepository.FindAsync(
            t => t.UserId == request.UserId,
            cancellationToken);

        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        dbStopwatch.Stop();
        LogContextLoaded(logger, dbStopwatch.ElapsedMilliseconds, activeHabits.Count, userFacts.Count);

        // 2. Build slim system prompt (habit index only, details fetched via read tools)
        var systemPrompt = systemPromptBuilder.Build(
            activeHabits, userFacts,
            hasImage: request.ImageData is not null,
            routinePatterns: null, userTags, userToday, habitMetrics: null);

        var tools = toolRegistry.GetAll();
        var toolDeclarations = tools.Select(t => (object)new
        {
            name = t.Name,
            description = t.Description,
            parameters = t.GetParameterSchema()
        }).ToList();

        // 3. Call AI with tool declarations
        LogCallingAiIntentService(logger, toolDeclarations.Count);
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
        LogAiIntentServiceCompleted(logger, aiStopwatch.ElapsedMilliseconds);

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
            LogToolCallingIteration(logger, iteration, aiResponse.ToolCalls!.Count);

            var toolResults = new List<AiToolCallResult>();

            // Sort tool calls so parent habits are created before sub-habits
            var orderedCalls = aiResponse.ToolCalls!.OrderBy(c => c.Name switch
            {
                "create_habit" => 0,
                "create_sub_habit" => 1,
                "assign_tags" => 2,
                _ => 1
            }).ToList();

            foreach (var call in orderedCalls)
            {
                var tool = toolRegistry.GetTool(call.Name);
                if (tool is null)
                {
                    LogUnknownToolRequested(logger, call.Name);
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
                        // Read-only tools don't produce action chips for the frontend
                        if (!tool.IsReadOnly && call.Name == "suggest_breakdown")
                        {
                            var suggestedSubHabits = ExtractSuggestedSubHabits(call.Args);
                            allActionResults.Add(new ActionResult(
                                ToolNameToPascalCase(call.Name),
                                ActionStatus.Suggestion,
                                EntityName: result.EntityName,
                                SuggestedSubHabits: suggestedSubHabits));
                        }
                        else if (!tool.IsReadOnly)
                        {
                            allActionResults.Add(new ActionResult(
                                ToolNameToPascalCase(call.Name),
                                ActionStatus.Success,
                                result.EntityId is not null ? Guid.Parse(result.EntityId) : null,
                                result.EntityName));
                        }

                        LogToolSucceeded(logger, call.Name, result.EntityName);
                    }
                    else
                    {
                        if (!tool.IsReadOnly)
                        {
                            allActionResults.Add(new ActionResult(
                                ToolNameToPascalCase(call.Name),
                                ActionStatus.Failed,
                                EntityName: result.EntityName,
                                Error: result.Error));
                        }
                        LogToolFailed(logger, call.Name, result.Error);
                    }
                }
                catch (Exception ex)
                {
                    LogToolThrewException(logger, ex, call.Name);
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
                LogContinueWithToolResultsFailed(logger, continueResult.Error);
                break;
            }
            aiResponse = continueResult.Value;
        }

        actionsStopwatch.Stop();
        LogToolExecutionCompleted(logger, actionsStopwatch.ElapsedMilliseconds, iteration, allActionResults.Count);

        // 5. Persist all changes in a single unit of work
        logger.LogInformation("Saving changes to database...");
        var saveStopwatch = System.Diagnostics.Stopwatch.StartNew();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        saveStopwatch.Stop();
        LogChangesSaved(logger, saveStopwatch.ElapsedMilliseconds);

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

        // 7. Fire-and-forget: fact extraction + message counter (non-blocking background work)
        var userId = request.UserId;
        var userMessage = request.Message;
        var shouldExtractFacts = aiMemoryEnabled && user is not null && user.HasProAccess;
        var existingFactCount = userFacts.Count;
        var existingFacts = userFacts;

        _ = Task.Run(async () =>
        {
            // Fire-and-forget background work (no caller to propagate cancellation to)
            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                var bgFactService = scope.ServiceProvider.GetRequiredService<IFactExtractionService>();
                var bgUserFactRepo = scope.ServiceProvider.GetRequiredService<IGenericRepository<UserFact>>();
                var bgUserRepo = scope.ServiceProvider.GetRequiredService<IGenericRepository<User>>();
                var bgUnitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var bgAppConfig = scope.ServiceProvider.GetRequiredService<IAppConfigService>();
                var bgLogger = scope.ServiceProvider.GetRequiredService<ILogger<ProcessUserChatCommandHandler>>();

                // Fact extraction
                if (shouldExtractFacts)
                {
                    try
                    {
                        var extractionResult = await bgFactService.ExtractFactsAsync(
                            userMessage, aiMessage, existingFacts, CancellationToken.None);

                        if (extractionResult.IsSuccess && extractionResult.Value.Facts.Count > 0)
                        {
                            var maxFacts = await bgAppConfig.GetAsync(AppConfigKeys.MaxUserFacts, AppConstants.MaxUserFacts, CancellationToken.None);
                            if (existingFactCount < maxFacts)
                            {
                                var remaining = maxFacts - existingFactCount;
                                foreach (var candidate in extractionResult.Value.Facts.Take(remaining))
                                {
                                    var factResult = UserFact.Create(userId, candidate.FactText, candidate.Category);
                                    if (factResult.IsSuccess)
                                        await bgUserFactRepo.AddAsync(factResult.Value, CancellationToken.None);
                                }
                                await bgUnitOfWork.SaveChangesAsync(CancellationToken.None);
                                LogBackgroundFactsPersisted(bgLogger, extractionResult.Value.Facts.Count);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        bgLogger.LogWarning(ex, "Background fact extraction failed");
                    }
                }

                // Increment AI message counter
                try
                {
                    var userForIncrement = await bgUserRepo.FindOneTrackedAsync(u => u.Id == userId, cancellationToken: CancellationToken.None);
                    userForIncrement?.IncrementAiMessageCount();
                    await bgUnitOfWork.SaveChangesAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    bgLogger.LogWarning(ex, "Background message counter increment failed");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Background post-response work failed");
            }
        }, CancellationToken.None);

        totalStopwatch.Stop();
        LogTotalRequestProcessingTime(logger, totalStopwatch.ElapsedMilliseconds);
        LogContextLoadingTime(logger, dbStopwatch.ElapsedMilliseconds);
        LogAiServiceTime(logger, aiStopwatch.ElapsedMilliseconds);
        LogToolExecutionTime(logger, actionsStopwatch.ElapsedMilliseconds, iteration);
        LogSaveChangesTime(logger, saveStopwatch.ElapsedMilliseconds);

        return Result.Success(new ChatResponse(aiMessage, allActionResults));
    }


    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Processing chat message: '{Message}'")]
    private static partial void LogProcessingChatMessage(ILogger logger, string message);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Context loaded in {ElapsedMs}ms (Habits: {HabitCount}, Facts: {FactCount})")]
    private static partial void LogContextLoaded(ILogger logger, long elapsedMs, int habitCount, int factCount);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Calling AI intent service with {ToolCount} tools...")]
    private static partial void LogCallingAiIntentService(ILogger logger, int toolCount);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "AI intent service completed in {ElapsedMs}ms")]
    private static partial void LogAiIntentServiceCompleted(ILogger logger, long elapsedMs);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Tool-calling iteration {Iteration}, {CallCount} calls")]
    private static partial void LogToolCallingIteration(ILogger logger, int iteration, int callCount);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Unknown tool requested: {Name}")]
    private static partial void LogUnknownToolRequested(ILogger logger, string name);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Tool {Name} succeeded: {EntityName}")]
    private static partial void LogToolSucceeded(ILogger logger, string name, string? entityName);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Tool {Name} failed: {Error}")]
    private static partial void LogToolFailed(ILogger logger, string name, string? error);

    [LoggerMessage(EventId = 9, Level = LogLevel.Error, Message = "Tool {Name} threw an exception")]
    private static partial void LogToolThrewException(ILogger logger, Exception ex, string name);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "ContinueWithToolResultsAsync failed: {Error}")]
    private static partial void LogContinueWithToolResultsFailed(ILogger logger, string? error);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "Tool execution completed in {ElapsedMs}ms ({Iterations} iterations, {ActionCount} actions)")]
    private static partial void LogToolExecutionCompleted(ILogger logger, long elapsedMs, int iterations, int actionCount);

    [LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "Changes saved in {ElapsedMs}ms")]
    private static partial void LogChangesSaved(ILogger logger, long elapsedMs);

    [LoggerMessage(EventId = 13, Level = LogLevel.Information, Message = "Background: {FactCount} facts persisted")]
    private static partial void LogBackgroundFactsPersisted(ILogger logger, int factCount);

    [LoggerMessage(EventId = 14, Level = LogLevel.Information, Message = "TOTAL request processing time: {ElapsedMs}ms")]
    private static partial void LogTotalRequestProcessingTime(ILogger logger, long elapsedMs);

    [LoggerMessage(EventId = 15, Level = LogLevel.Information, Message = "   Context loading: {DbMs}ms")]
    private static partial void LogContextLoadingTime(ILogger logger, long dbMs);

    [LoggerMessage(EventId = 16, Level = LogLevel.Information, Message = "   AI service: {AiMs}ms")]
    private static partial void LogAiServiceTime(ILogger logger, long aiMs);

    [LoggerMessage(EventId = 17, Level = LogLevel.Information, Message = "   Tool execution: {ActionsMs}ms ({Iterations} iterations)")]
    private static partial void LogToolExecutionTime(ILogger logger, long actionsMs, int iterations);

    [LoggerMessage(EventId = 18, Level = LogLevel.Information, Message = "   Save changes: {SaveMs}ms")]
    private static partial void LogSaveChangesTime(ILogger logger, long saveMs);

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
    private static List<AiAction>? ExtractSuggestedSubHabits(JsonElement args)
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
