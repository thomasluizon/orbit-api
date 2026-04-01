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

public class ProcessUserChatCommandHandler(
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
        logger.LogInformation("Processing chat message: '{Message}'", request.Message);

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
        logger.LogInformation("Context loaded in {ElapsedMs}ms (Habits: {HabitCount}, Facts: {FactCount})",
            dbStopwatch.ElapsedMilliseconds, activeHabits.Count, userFacts.Count);

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
                        // Read-only tools don't produce action chips for the frontend
                        if (!tool.IsReadOnly)
                        {
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
                        }

                        logger.LogInformation("Tool {Name} succeeded: {EntityName}", call.Name, result.EntityName);
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

        // 7. Fire-and-forget: fact extraction + message counter (non-blocking background work)
        var userId = request.UserId;
        var userMessage = request.Message;
        var shouldExtractFacts = aiMemoryEnabled && user is not null && user.HasProAccess;
        var existingFactCount = userFacts.Count;
        var existingFacts = userFacts;

        _ = Task.Run(async () =>
        {
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
                                bgLogger.LogInformation("Background: {FactCount} facts persisted", extractionResult.Value.Facts.Count);
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
        });

        totalStopwatch.Stop();
        logger.LogInformation("TOTAL request processing time: {ElapsedMs}ms", totalStopwatch.ElapsedMilliseconds);
        logger.LogInformation("   Context loading: {DbMs}ms", dbStopwatch.ElapsedMilliseconds);
        logger.LogInformation("   AI service: {AiMs}ms", aiStopwatch.ElapsedMilliseconds);
        logger.LogInformation("   Tool execution: {ActionsMs}ms ({Iterations} iterations)", actionsStopwatch.ElapsedMilliseconds, iteration);
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
