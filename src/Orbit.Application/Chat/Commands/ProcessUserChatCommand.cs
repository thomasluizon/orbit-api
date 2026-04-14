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
    List<ChatHistoryMessage>? History = null,
    AgentClientContext? ClientContext = null,
    string? ConfirmationToken = null,
    AgentAuthMethod AuthMethod = AgentAuthMethod.Jwt,
    IReadOnlyList<string>? GrantedScopes = null,
    bool IsReadOnlyCredential = false,
    string? CorrelationId = null) : IRequest<Result<ChatResponse>>;

public record ChatResponse(
    string? AiMessage,
    IReadOnlyList<ActionResult> Actions,
    IReadOnlyList<AgentOperationResult>? Operations = null,
    IReadOnlyList<PendingAgentOperation>? PendingOperations = null,
    IReadOnlyList<AgentPolicyDenial>? PolicyDenials = null);

public record ActionResult(
    string Type,
    ActionStatus Status,
    Guid? EntityId = null,
    string? EntityName = null,
    string? Error = null,
    string? Field = null,
    IReadOnlyList<AiAction>? SuggestedSubHabits = null);

public enum ActionStatus { Success, Failed, Suggestion }

/// <summary>
/// Groups AI-related dependencies to reduce constructor parameter count (S107).
/// </summary>
public record ChatAiDependencies(
    IAiIntentService IntentService,
    AiToolRegistry ToolRegistry,
    ISystemPromptBuilder PromptBuilder,
    IAgentCatalogService CatalogService);

/// <summary>
/// Groups data repository dependencies to reduce constructor parameter count (S107).
/// </summary>
public record ChatDataDependencies(
    IGenericRepository<Habit> HabitRepository,
    IGenericRepository<Goal> GoalRepository,
    IGenericRepository<User> UserRepository,
    IGenericRepository<UserFact> UserFactRepository,
    IGenericRepository<Tag> TagRepository,
    IGenericRepository<ChecklistTemplate> ChecklistTemplateRepository,
    IFeatureFlagService FeatureFlagService);

/// <summary>
/// Groups workflow services to reduce constructor parameter count (S107).
/// </summary>
public record ChatExecutionDependencies(
    IUserDateService UserDateService,
    IUserStreakService UserStreakService,
    IPayGateService PayGateService,
    IUnitOfWork UnitOfWork,
    IServiceScopeFactory ServiceScopeFactory,
    IAgentOperationExecutor OperationExecutor);

public partial class ProcessUserChatCommandHandler(
    ChatDataDependencies data,
    ChatAiDependencies ai,
    ChatExecutionDependencies execution,
    ILogger<ProcessUserChatCommandHandler> logger) : IRequestHandler<ProcessUserChatCommand, Result<ChatResponse>>
{
    private const int MaxToolIterations = 5;
    private const string UnsupportedByPolicyReason = "unsupported_by_policy";

    public async Task<Result<ChatResponse>> Handle(
        ProcessUserChatCommand request,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        LogProcessingChatMessage(logger, request.Message);

        // 1. Load lightweight context for system prompt (no Logs, no metrics)
        LogFetchingContext(logger);
        var dbStopwatch = System.Diagnostics.Stopwatch.StartNew();

        var activeHabits = await data.HabitRepository.FindAsync(
            h => h.UserId == request.UserId,
            q => q.Include(h => h.Tags),
            cancellationToken);
        var user = await data.UserRepository.GetByIdAsync(request.UserId, cancellationToken);
        var hasProAccess = user?.HasProAccess ?? false;
        var aiMemoryEnabled = hasProAccess && (user?.AiMemoryEnabled ?? true);
        var activeGoals = hasProAccess
            ? await data.GoalRepository.FindAsync(
                g => g.UserId == request.UserId && g.Status == GoalStatus.Active,
                q => q.Include(g => g.Habits),
                cancellationToken)
            : [];

        // Check AI message limits
        var messageGate = await execution.PayGateService.CanSendAiMessage(request.UserId, cancellationToken);
        if (messageGate.IsFailure)
            return messageGate.PropagateError<ChatResponse>();

        IReadOnlyList<UserFact> userFacts = [];
        if (aiMemoryEnabled)
        {
            userFacts = await data.UserFactRepository.FindAsync(
                f => f.UserId == request.UserId,
                cancellationToken);
        }

        var userTags = await data.TagRepository.FindAsync(
            t => t.UserId == request.UserId,
            cancellationToken);

        var checklistTemplates = await data.ChecklistTemplateRepository.FindAsync(
            template => template.UserId == request.UserId,
            cancellationToken);

        var enabledFeatureFlags = await data.FeatureFlagService.GetEnabledKeysForUserAsync(
            request.UserId,
            cancellationToken);

        var userToday = await execution.UserDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        dbStopwatch.Stop();
        LogContextLoaded(logger, dbStopwatch.ElapsedMilliseconds, activeHabits.Count, userFacts.Count);

        // 2. Build slim system prompt (habit/goal indexes only, details fetched via read tools)
        var systemPrompt = ai.PromptBuilder.Build(new PromptBuildRequest(
            activeHabits, userFacts,
            HasImage: request.ImageData is not null,
            UserTags: userTags, UserToday: userToday, ActiveGoals: activeGoals));
        systemPrompt += Environment.NewLine + ai.CatalogService.BuildPromptSupplement(
            BuildAgentContextSnapshot(user, request.ClientContext, enabledFeatureFlags, userTags, checklistTemplates, activeHabits, activeGoals, hasProAccess));

        var tools = ai.ToolRegistry.GetAll();
        var toolDeclarations = tools.Select(t => (object)new
        {
            name = t.Name,
            description = t.Description,
            parameters = t.GetParameterSchema()
        }).ToList();

        // 3. Call AI with tool declarations
        LogCallingAiIntentService(logger, toolDeclarations.Count);
        var aiStopwatch = System.Diagnostics.Stopwatch.StartNew();

        var response = await ai.IntentService.SendWithToolsAsync(
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
        var executionResults = new ToolExecutionAccumulator();
        var actionsStopwatch = System.Diagnostics.Stopwatch.StartNew();
        int iteration = 0;

        while (aiResponse.HasToolCalls && iteration < MaxToolIterations)
        {
            iteration++;
            var continueResponse = await ProcessToolCallsAsync(
                aiResponse,
                request,
                executionResults,
                iteration,
                cancellationToken);

            if (continueResponse is null)
                break;

            aiResponse = continueResponse;
        }

        actionsStopwatch.Stop();
        LogToolExecutionCompleted(logger, actionsStopwatch.ElapsedMilliseconds, iteration, executionResults.ActionResults.Count);

        // 5. Persist all changes in a single unit of work
        LogSavingChanges(logger);
        var saveStopwatch = System.Diagnostics.Stopwatch.StartNew();

        await execution.UnitOfWork.SaveChangesAsync(cancellationToken);
        if (RequiresStreakRecalculation(executionResults.ActionResults))
        {
            await execution.UserStreakService.RecalculateAsync(request.UserId, cancellationToken);
            await execution.UnitOfWork.SaveChangesAsync(cancellationToken);
        }

        saveStopwatch.Stop();
        LogChangesSaved(logger, saveStopwatch.ElapsedMilliseconds);

        // 6. Extract AI message (strip JSON wrapper if model didn't use function calling)
        var aiMessage = StripJsonWrapper(aiResponse.TextMessage);

        // 7. Fire-and-forget: fact extraction + message counter (non-blocking background work)
        RunBackgroundPostResponseWork(
            request.UserId,
            request.Message,
            aiMessage,
            shouldExtractFacts: aiMemoryEnabled && user is not null && user.HasProAccess,
            existingFacts: userFacts);

        totalStopwatch.Stop();
        LogTotalRequestProcessingTime(logger, totalStopwatch.ElapsedMilliseconds);
        LogContextLoadingTime(logger, dbStopwatch.ElapsedMilliseconds);
        LogAiServiceTime(logger, aiStopwatch.ElapsedMilliseconds);
        LogToolExecutionTime(logger, actionsStopwatch.ElapsedMilliseconds, iteration);
        LogSaveChangesTime(logger, saveStopwatch.ElapsedMilliseconds);

        return Result.Success(new ChatResponse(
            aiMessage,
            executionResults.ActionResults,
            executionResults.OperationResults,
            executionResults.PendingOperations,
            executionResults.PolicyDenials));
    }

    /// <summary>
    /// Processes one iteration of AI tool calls: orders them, executes each, and sends results
    /// back to the AI. Returns the next AI response, or null if continuation failed.
    /// </summary>
    private async Task<AiResponse?> ProcessToolCallsAsync(
        AiResponse aiResponse,
        ProcessUserChatCommand request,
        ToolExecutionAccumulator executionResults,
        int iteration,
        CancellationToken cancellationToken)
    {
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
            var (toolCallResult, actionResult, operationResult, policyDenial, pendingOperation) =
                await ExecuteSingleToolCallAsync(call, request, cancellationToken);
            toolResults.Add(toolCallResult);
            executionResults.Add(actionResult, operationResult, policyDenial, pendingOperation);
        }

        // Send results back to the AI for next iteration or final message
        var continueResult = await ai.IntentService.ContinueWithToolResultsAsync(aiResponse.ConversationContext!, toolResults, cancellationToken);
        if (continueResult.IsFailure)
        {
            LogContinueWithToolResultsFailed(logger, continueResult.Error);
            return null;
        }

        return continueResult.Value;
    }

    /// <summary>
    /// Executes a single tool call: resolves the tool, runs it, and produces both a result
    /// for the AI and an optional action result for the frontend.
    /// </summary>
    private async Task<(
        AiToolCallResult ToolResult,
        ActionResult? ActionResult,
        AgentOperationResult? OperationResult,
        AgentPolicyDenial? PolicyDenial,
        PendingAgentOperation? PendingOperation)> ExecuteSingleToolCallAsync(
        AiToolCall call,
        ProcessUserChatCommand request,
        CancellationToken cancellationToken)
    {
        var tool = ai.ToolRegistry.GetTool(call.Name);
        if (tool is null)
        {
            LogUnknownToolRequested(logger, call.Name);
            return (
                new AiToolCallResult(call.Name, call.Id, false, null, null, $"Unknown tool: {call.Name}"),
                new ActionResult(ToolNameToPascalCase(call.Name), ActionStatus.Failed, Error: $"Unknown tool: {call.Name}"),
                new AgentOperationResult(
                    call.Name,
                    call.Name,
                    AgentRiskClass.Low,
                    AgentConfirmationRequirement.None,
                    AgentOperationStatus.UnsupportedByPolicy,
                    PolicyReason: UnsupportedByPolicyReason),
                new AgentPolicyDenial(
                    call.Name,
                    call.Name,
                    AgentRiskClass.Low,
                    AgentConfirmationRequirement.None,
                    UnsupportedByPolicyReason),
                null);
        }

        var capability = ai.CatalogService.GetCapabilityByChatTool(call.Name);
        var operationSummary = BuildOperationSummary(call);

        if (capability is null)
        {
            return (
                new AiToolCallResult(call.Name, call.Id, false, null, null, "Operation is unsupported by policy."),
                tool.IsReadOnly ? null : new ActionResult(ToolNameToPascalCase(call.Name), ActionStatus.Failed, Error: "Operation is unsupported by policy."),
                new AgentOperationResult(
                    call.Name,
                    call.Name,
                    AgentRiskClass.Low,
                    AgentConfirmationRequirement.None,
                    AgentOperationStatus.UnsupportedByPolicy,
                    Summary: operationSummary,
                    PolicyReason: UnsupportedByPolicyReason),
                new AgentPolicyDenial(
                    call.Name,
                    call.Name,
                    AgentRiskClass.Low,
                    AgentConfirmationRequirement.None,
                    UnsupportedByPolicyReason),
                null);
        }

        var executionResponse = await execution.OperationExecutor.ExecuteAsync(new AgentExecuteOperationRequest(
            request.UserId,
            call.Name,
            call.Args,
            AgentExecutionSurface.Chat,
            request.AuthMethod,
            request.GrantedScopes,
            request.IsReadOnlyCredential,
            request.ConfirmationToken,
            request.CorrelationId), cancellationToken);

        var operationResult = executionResponse.Operation;
        var toolResult = BuildToolCallResult(call, operationResult);

        if (operationResult.Status == AgentOperationStatus.Succeeded)
            LogToolSucceeded(logger, call.Name, operationResult.TargetName);
        else if (operationResult.Status is AgentOperationStatus.Failed or AgentOperationStatus.Denied)
            LogToolFailed(logger, call.Name, operationResult.PolicyReason);

        return operationResult.Status switch
        {
            AgentOperationStatus.PendingConfirmation => (
                new AiToolCallResult(call.Name, call.Id, false, null, null, "Confirmation required before this action can run."),
                null,
                operationResult,
                executionResponse.PolicyDenial,
                executionResponse.PendingOperation),
            AgentOperationStatus.Denied or AgentOperationStatus.UnsupportedByPolicy => (
                toolResult,
                tool.IsReadOnly ? null : new ActionResult(ToolNameToPascalCase(call.Name), ActionStatus.Failed, Error: toolResult.Error),
                operationResult,
                executionResponse.PolicyDenial,
                null),
            _ => (
                toolResult,
                BuildActionResult(call, tool, ToToolResult(operationResult)),
                operationResult,
                executionResponse.PolicyDenial,
                executionResponse.PendingOperation)
        };
    }

    /// <summary>
    /// Builds the frontend-facing ActionResult from a tool execution result.
    /// Returns null for read-only tools (they don't produce action chips).
    /// </summary>
    private static ActionResult? BuildActionResult(AiToolCall call, IAiTool tool, ToolResult result)
    {
        if (tool.IsReadOnly)
            return null;

        if (!result.Success)
        {
            return new ActionResult(
                ToolNameToPascalCase(call.Name),
                ActionStatus.Failed,
                EntityName: result.EntityName,
                Error: result.Error);
        }

        if (call.Name == "suggest_breakdown")
        {
            return new ActionResult(
                ToolNameToPascalCase(call.Name),
                ActionStatus.Suggestion,
                EntityName: result.EntityName,
                SuggestedSubHabits: ExtractSuggestedSubHabits(call.Args));
        }

        return new ActionResult(
            ToolNameToPascalCase(call.Name),
            ActionStatus.Success,
            result.EntityId is not null ? Guid.Parse(result.EntityId) : null,
            result.EntityName);
    }

    private static AgentContextSnapshot BuildAgentContextSnapshot(
        User? user,
        AgentClientContext? clientContext,
        IReadOnlyList<string> featureFlags,
        IReadOnlyCollection<Tag> userTags,
        IReadOnlyCollection<ChecklistTemplate> checklistTemplates,
        IReadOnlyCollection<Habit> activeHabits,
        IReadOnlyCollection<Goal> activeGoals,
        bool hasProAccess)
    {
        return new AgentContextSnapshot(
            hasProAccess ? "pro" : "free",
            user?.Language,
            user?.TimeZone,
            hasProAccess && (user?.AiMemoryEnabled ?? true),
            hasProAccess && (user?.AiSummaryEnabled ?? true),
            user?.WeekStartDay ?? 1,
            user?.ThemePreference,
            hasProAccess ? user?.ColorScheme : null,
            hasProAccess && user?.GoogleAccessToken is not null,
            hasProAccess && (user?.GoogleCalendarAutoSyncEnabled ?? false),
            hasProAccess
                ? (user?.GoogleCalendarAutoSyncStatus ?? GoogleCalendarAutoSyncStatus.Idle).ToString()
                : "Locked",
            featureFlags,
            userTags
                .Select(tag => tag.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList(),
            checklistTemplates
                .Select(template => template.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList(),
            activeHabits
                .OrderByDescending(habit => habit.UpdatedAtUtc)
                .Select(habit => habit.Title)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList(),
            hasProAccess
                ? activeGoals
                    .OrderByDescending(goal => goal.UpdatedAtUtc)
                    .Select(goal => goal.Title)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .ToList()
                : [],
            ClientContext: clientContext);
    }

    private static string BuildOperationSummary(AiToolCall call)
    {
        return $"{ToolNameToPascalCase(call.Name)} requested via chat";
    }

    private static AiToolCallResult BuildToolCallResult(AiToolCall call, AgentOperationResult operationResult)
    {
        return new AiToolCallResult(
            call.Name,
            call.Id,
            operationResult.Status == AgentOperationStatus.Succeeded,
            operationResult.TargetId,
            operationResult.TargetName,
            BuildToolError(operationResult),
            operationResult.Payload);
    }

    private static string? BuildToolError(AgentOperationResult operationResult)
    {
        return operationResult.Status switch
        {
            AgentOperationStatus.Denied => $"Policy denied: {operationResult.PolicyReason}",
            AgentOperationStatus.UnsupportedByPolicy => "Operation is unsupported by policy.",
            AgentOperationStatus.PendingConfirmation => "Confirmation required before this action can run.",
            AgentOperationStatus.Failed => string.Equals(operationResult.PolicyReason, "unexpected_error", StringComparison.Ordinal)
                ? "An unexpected error occurred."
                : operationResult.PolicyReason,
            _ => null
        };
    }

    private static ToolResult ToToolResult(AgentOperationResult operationResult)
    {
        return new ToolResult(
            operationResult.Status == AgentOperationStatus.Succeeded,
            operationResult.TargetId,
            operationResult.TargetName,
            BuildToolError(operationResult),
            operationResult.Payload);
    }

    /// <summary>
    /// Strips a JSON wrapper from the AI response text, extracting the "aiMessage" property
    /// if the model returned a raw JSON object instead of using function calling.
    /// </summary>
    private static string? StripJsonWrapper(string? text)
    {
        if (text is null || !text.TrimStart().StartsWith('{'))
            return text;

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("aiMessage", out var msgEl))
                return msgEl.GetString();
        }
        catch (JsonException)
        {
            // Not valid JSON, use as-is
        }

        return text;
    }

    private static bool RequiresStreakRecalculation(IEnumerable<ActionResult> actionResults)
    {
        return actionResults.Any(action => action.Status == ActionStatus.Success && action.Type is "LogHabit" or "BulkLogHabits" or "DeleteHabit");
    }

    private sealed class ToolExecutionAccumulator
    {
        public List<ActionResult> ActionResults { get; } = [];
        public List<AgentOperationResult> OperationResults { get; } = [];
        public List<PendingAgentOperation> PendingOperations { get; } = [];
        public List<AgentPolicyDenial> PolicyDenials { get; } = [];

        public void Add(
            ActionResult? actionResult,
            AgentOperationResult? operationResult,
            AgentPolicyDenial? policyDenial,
            PendingAgentOperation? pendingOperation)
        {
            if (actionResult is not null)
                ActionResults.Add(actionResult);

            if (operationResult is not null)
                OperationResults.Add(operationResult);

            if (policyDenial is not null)
                PolicyDenials.Add(policyDenial);

            if (pendingOperation is not null)
                PendingOperations.Add(pendingOperation);
        }
    }

    /// <summary>
    /// Fires off background work for fact extraction and AI message counter increment.
    /// Runs in a separate DI scope so it doesn't block the response.
    /// </summary>
    private void RunBackgroundPostResponseWork(
        Guid userId,
        string userMessage,
        string? aiMessage,
        bool shouldExtractFacts,
        IReadOnlyList<UserFact> existingFacts)
    {
        var existingFactCount = existingFacts.Count;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = execution.ServiceScopeFactory.CreateScope();
                var bgUnitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var bgUserRepo = scope.ServiceProvider.GetRequiredService<IGenericRepository<User>>();
                var bgLogger = scope.ServiceProvider.GetRequiredService<ILogger<ProcessUserChatCommandHandler>>();

                if (shouldExtractFacts)
                    await ExtractAndPersistFactsAsync(scope, userId, userMessage, aiMessage, existingFacts, existingFactCount, bgLogger);

                await IncrementAiMessageCountAsync(bgUserRepo, bgUnitOfWork, userId, bgLogger);
            }
            catch (Exception ex)
            {
                LogBackgroundPostResponseFailed(logger, ex);
            }
        }, CancellationToken.None);
    }

    private static async Task ExtractAndPersistFactsAsync(
        IServiceScope scope,
        Guid userId,
        string userMessage,
        string? aiMessage,
        IReadOnlyList<UserFact> existingFacts,
        int existingFactCount,
        ILogger bgLogger)
    {
        var bgFactService = scope.ServiceProvider.GetRequiredService<IFactExtractionService>();
        var bgUserFactRepo = scope.ServiceProvider.GetRequiredService<IGenericRepository<UserFact>>();
        var bgUnitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var bgAppConfig = scope.ServiceProvider.GetRequiredService<IAppConfigService>();

        try
        {
            var extractionResult = await bgFactService.ExtractFactsAsync(
                userMessage, aiMessage, existingFacts, CancellationToken.None);

            if (extractionResult.IsFailure || extractionResult.Value.Facts.Count == 0)
                return;

            var maxFacts = await bgAppConfig.GetAsync(AppConfigKeys.MaxUserFacts, AppConstants.MaxUserFacts, CancellationToken.None);
            if (existingFactCount >= maxFacts)
                return;

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
        catch (Exception ex)
        {
            LogBackgroundFactExtractionFailed(bgLogger, ex);
        }
    }

    private static async Task IncrementAiMessageCountAsync(
        IGenericRepository<User> bgUserRepo,
        IUnitOfWork bgUnitOfWork,
        Guid userId,
        ILogger bgLogger)
    {
        try
        {
            var userForIncrement = await bgUserRepo.FindOneTrackedAsync(u => u.Id == userId, cancellationToken: CancellationToken.None);
            userForIncrement?.IncrementAiMessageCount();
            await bgUnitOfWork.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogBackgroundMessageCounterFailed(bgLogger, ex);
        }
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

    [LoggerMessage(EventId = 19, Level = LogLevel.Information, Message = "Fetching context from database...")]
    private static partial void LogFetchingContext(ILogger logger);

    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "Saving changes to database...")]
    private static partial void LogSavingChanges(ILogger logger);

    [LoggerMessage(EventId = 21, Level = LogLevel.Warning, Message = "Background fact extraction failed")]
    private static partial void LogBackgroundFactExtractionFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 22, Level = LogLevel.Warning, Message = "Background message counter increment failed")]
    private static partial void LogBackgroundMessageCounterFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 23, Level = LogLevel.Warning, Message = "Background post-response work failed")]
    private static partial void LogBackgroundPostResponseFailed(ILogger logger, Exception ex);

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
            suggestions.Add(ParseSingleSubHabit(item));


        return suggestions.Count > 0 ? suggestions : null;
    }

    private static AiAction ParseSingleSubHabit(JsonElement item)
    {
        return new AiAction
        {
            Type = AiActionType.SuggestBreakdown,
            Title = GetStringProperty(item, "title"),
            Description = GetStringProperty(item, "description"),
            FrequencyUnit = GetEnumProperty<FrequencyUnit>(item, "frequency_unit"),
            FrequencyQuantity = GetIntProperty(item, "frequency_quantity"),
            Days = GetDaysProperty(item)
        };
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;
    }

    private static TEnum? GetEnumProperty<TEnum>(JsonElement element, string propertyName) where TEnum : struct, Enum
    {
        return element.TryGetProperty(propertyName, out var el) && el.ValueKind == JsonValueKind.String
            && Enum.TryParse<TEnum>(el.GetString(), true, out var value)
            ? value : null;
    }

    private static int? GetIntProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetInt32() : null;
    }

    private static List<DayOfWeek>? GetDaysProperty(JsonElement item)
    {
        if (!item.TryGetProperty("days", out var daysEl) || daysEl.ValueKind != JsonValueKind.Array)
            return null;

        var days = new List<DayOfWeek>();
        foreach (var dayEl in daysEl.EnumerateArray())
        {
            if (dayEl.ValueKind == JsonValueKind.String &&
                Enum.TryParse<DayOfWeek>(dayEl.GetString(), true, out var dow))
                days.Add(dow);
        }

        return days;
    }
}
