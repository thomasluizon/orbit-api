using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Chat.Models;
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
    string? CorrelationId = null,
    Func<ChatStreamEvent, Task>? StreamSink = null) : IRequest<Result<ChatResponse>>;

public record ChatResponse(
    string? AiMessage,
    IReadOnlyList<ActionResult> Actions,
    IReadOnlyList<AgentOperationResult>? Operations = null,
    IReadOnlyList<PendingAgentOperation>? PendingOperations = null,
    IReadOnlyList<AgentPolicyDenial>? PolicyDenials = null,
    string? CorrelationId = null,
    IReadOnlyList<string>? RelatedSurfaces = null);

public record ActionResult(
    string Type,
    ActionStatus Status,
    Guid? EntityId = null,
    string? EntityName = null,
    string? Error = null,
    string? Field = null,
    IReadOnlyList<AiAction>? SuggestedSubHabits = null,
    ClarificationRequest? ClarificationRequest = null);

public enum ActionStatus { Success, Failed, Suggestion, NeedsClarification }

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
    IAgentOperationExecutor OperationExecutor,
    IPendingClarificationStore PendingClarificationStore);

public partial class ProcessUserChatCommandHandler(
    ChatDataDependencies data,
    ChatAiDependencies ai,
    ChatExecutionDependencies execution,
    ILogger<ProcessUserChatCommandHandler> logger) : IRequestHandler<ProcessUserChatCommand, Result<ChatResponse>>
{
    private const int MaxToolIterations = 5;
    private const string UnsupportedByPolicyReason = "unsupported_by_policy";

    private const int MaxSupportMessageLength = 5000;

    public async Task<Result<ChatResponse>> Handle(
        ProcessUserChatCommand request,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        LogProcessingChatMessage(logger, request.Message);

        var contextResult = await LoadChatContextAsync(request, cancellationToken);
        if (contextResult.IsFailure)
            return contextResult.PropagateError<ChatResponse>();

        var context = contextResult.Value;
        var aiStreamSink = BuildAiStreamSink(request.StreamSink);

        var aiStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var response = await RequestInitialAiResponseAsync(request, context, aiStreamSink, cancellationToken);
        aiStopwatch.Stop();
        LogAiIntentServiceCompleted(logger, aiStopwatch.ElapsedMilliseconds);

        if (response.IsFailure)
            return response.PropagateError<ChatResponse>();

        var executionResults = new ToolExecutionAccumulator();
        var actionsStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var (aiResponse, iterations) = await RunToolCallLoopAsync(response.Value, request, executionResults, aiStreamSink, cancellationToken);
        actionsStopwatch.Stop();
        LogToolExecutionCompleted(logger, actionsStopwatch.ElapsedMilliseconds, iterations, executionResults.ActionResults.Count);

        LogSavingChanges(logger);
        var saveStopwatch = System.Diagnostics.Stopwatch.StartNew();
        await PersistExecutionResultsAsync(request.UserId, executionResults.ActionResults, cancellationToken);
        saveStopwatch.Stop();
        LogChangesSaved(logger, saveStopwatch.ElapsedMilliseconds);

        var aiMessage = StripJsonWrapper(aiResponse.TextMessage);

        RunBackgroundPostResponseWork(
            request.UserId,
            request.Message,
            aiMessage,
            shouldExtractFacts: context.AiMemoryEnabled && context.User is not null && context.User.HasProAccess,
            existingFacts: context.UserFacts);

        totalStopwatch.Stop();
        LogTotalRequestProcessingTime(logger, totalStopwatch.ElapsedMilliseconds);
        LogContextLoadingTime(logger, context.ContextLoadMilliseconds);
        LogAiServiceTime(logger, aiStopwatch.ElapsedMilliseconds);
        LogToolExecutionTime(logger, actionsStopwatch.ElapsedMilliseconds, iterations);
        LogSaveChangesTime(logger, saveStopwatch.ElapsedMilliseconds);

        return Result.Success(new ChatResponse(
            aiMessage,
            executionResults.ActionResults,
            executionResults.OperationResults,
            executionResults.PendingOperations,
            executionResults.PolicyDenials,
            request.CorrelationId,
            executionResults.RelatedSurfaces.Count > 0 ? executionResults.RelatedSurfaces : null));
    }

    private async Task<Result<ChatContext>> LoadChatContextAsync(
        ProcessUserChatCommand request,
        CancellationToken cancellationToken)
    {
        LogFetchingContext(logger);
        var dbStopwatch = System.Diagnostics.Stopwatch.StartNew();

        var userHabits = await data.HabitRepository.FindAsync(
            h => h.UserId == request.UserId,
            q => q,
            cancellationToken);
        var activeHabits = userHabits.Where(habit => !habit.IsCompleted).ToList();
        var promptHabits = BuildPromptHabitIndex(userHabits);
        var user = await data.UserRepository.GetByIdAsync(request.UserId, cancellationToken);
        var hasProAccess = user?.HasProAccess ?? false;
        var aiMemoryEnabled = user is { HasProAccess: true, AiMemoryEnabled: true };
        var activeGoals = hasProAccess
            ? await data.GoalRepository.FindAsync(
                g => g.UserId == request.UserId && g.Status == GoalStatus.Active,
                q => q.Include(g => g.Habits),
                cancellationToken)
            : [];

        var messageGate = await execution.PayGateService.CanSendAiMessage(request.UserId, cancellationToken);
        if (messageGate.IsFailure)
            return messageGate.PropagateError<ChatContext>();

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

        return Result.Success(new ChatContext(
            activeHabits,
            promptHabits,
            user,
            hasProAccess,
            aiMemoryEnabled,
            activeGoals,
            userFacts,
            userTags,
            checklistTemplates,
            enabledFeatureFlags,
            userToday,
            dbStopwatch.ElapsedMilliseconds));
    }

    private async Task<Result<AiResponse>> RequestInitialAiResponseAsync(
        ProcessUserChatCommand request,
        ChatContext context,
        Func<AiStreamEvent, Task>? aiStreamSink,
        CancellationToken cancellationToken)
    {
        var systemPrompt = ai.PromptBuilder.Build(new PromptBuildRequest(
            context.PromptHabits, context.UserFacts,
            HasImage: request.ImageData is not null,
            UserTags: context.UserTags, UserToday: context.UserToday, ActiveGoals: context.ActiveGoals));
        systemPrompt += Environment.NewLine + ai.CatalogService.BuildPromptSupplement(
            BuildAgentContextSnapshot(
                context.User,
                request.ClientContext,
                context.EnabledFeatureFlags,
                context.UserTags,
                context.ChecklistTemplates,
                context.ActiveHabits,
                context.ActiveGoals,
                context.HasProAccess));

        var tools = ai.ToolRegistry.GetAll();
        var toolDeclarations = tools.Select(t => (object)new
        {
            name = t.Name,
            description = t.Description,
            parameters = t.GetParameterSchema()
        }).ToList();

        LogCallingAiIntentService(logger, toolDeclarations.Count);

        return await ai.IntentService.SendWithToolsAsync(
            request.Message,
            systemPrompt,
            toolDeclarations,
            request.ImageData,
            request.ImageMimeType,
            request.History,
            aiStreamSink,
            cancellationToken);
    }

    private async Task<(AiResponse FinalResponse, int Iterations)> RunToolCallLoopAsync(
        AiResponse initialResponse,
        ProcessUserChatCommand request,
        ToolExecutionAccumulator executionResults,
        Func<AiStreamEvent, Task>? aiStreamSink,
        CancellationToken cancellationToken)
    {
        var aiResponse = initialResponse;
        var iteration = 0;

        while (aiResponse.HasToolCalls && iteration < MaxToolIterations)
        {
            iteration++;
            if (request.StreamSink is not null)
                await request.StreamSink(ChatStreamEvent.Round(iteration));

            var continueResponse = await ProcessToolCallsAsync(
                aiResponse,
                request,
                executionResults,
                iteration,
                aiStreamSink,
                cancellationToken);

            if (continueResponse is null)
                break;

            aiResponse = continueResponse;
        }

        return (aiResponse, iteration);
    }

    private async Task PersistExecutionResultsAsync(
        Guid userId,
        IReadOnlyList<ActionResult> actionResults,
        CancellationToken cancellationToken)
    {
        await execution.UnitOfWork.SaveChangesAsync(cancellationToken);
        if (RequiresStreakRecalculation(actionResults))
        {
            await execution.UserStreakService.RecalculateAsync(userId, cancellationToken);
            await execution.UnitOfWork.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed record ChatContext(
        List<Habit> ActiveHabits,
        List<Habit> PromptHabits,
        User? User,
        bool HasProAccess,
        bool AiMemoryEnabled,
        IReadOnlyList<Goal> ActiveGoals,
        IReadOnlyList<UserFact> UserFacts,
        IReadOnlyList<Tag> UserTags,
        IReadOnlyList<ChecklistTemplate> ChecklistTemplates,
        IReadOnlyList<string> EnabledFeatureFlags,
        DateOnly UserToday,
        long ContextLoadMilliseconds);

    private static Func<AiStreamEvent, Task>? BuildAiStreamSink(Func<ChatStreamEvent, Task>? streamSink)
    {
        if (streamSink is null)
            return null;

        return aiEvent => streamSink(aiEvent.Kind == AiStreamEventKind.Delta
            ? ChatStreamEvent.Delta(aiEvent.Text ?? "")
            : ChatStreamEvent.Reset());
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
        Func<AiStreamEvent, Task>? aiStreamSink,
        CancellationToken cancellationToken)
    {
        LogToolCallingIteration(logger, iteration, aiResponse.ToolCalls!.Count);

        var toolResults = new List<AiToolCallResult>();

        var orderedCalls = aiResponse.ToolCalls!
            .OrderBy(c => ai.ToolRegistry.GetTool(c.Name)?.Order ?? int.MaxValue)
            .ToList();

        foreach (var call in orderedCalls)
        {
            var outcome = await ExecuteSingleToolCallAsync(call, request, cancellationToken);
            toolResults.Add(outcome.ToolResult);
            executionResults.Add(outcome.ActionResult, outcome.OperationResult, outcome.PolicyDenial, outcome.PendingOperation);
        }

        var continueResult = await ai.IntentService.ContinueWithToolResultsAsync(aiResponse.ConversationContext!, toolResults, aiStreamSink, cancellationToken);
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
    private async Task<ToolCallOutcome> ExecuteSingleToolCallAsync(
        AiToolCall call,
        ProcessUserChatCommand request,
        CancellationToken cancellationToken)
    {
        var tool = ai.ToolRegistry.GetTool(call.Name);
        if (tool is null)
        {
            LogUnknownToolRequested(logger, call.Name);
            return UnknownToolOutcome(call);
        }

        var capability = ai.CatalogService.GetCapabilityByChatTool(call.Name);
        if (capability is null)
            return UnsupportedByPolicyOutcome(call, tool);

        var executionResponse = await DispatchToolCallAsync(call, request, cancellationToken);
        var operationResult = executionResponse.Operation;
        var toolResult = BuildToolCallResult(call, operationResult);
        LogToolCallOutcome(call, operationResult);

        if (operationResult.Status == AgentOperationStatus.Succeeded
            && operationResult.Payload is NeedsClarificationPayload payload)
        {
            return await StashClarificationAsync(call, request, toolResult, operationResult, executionResponse, payload, cancellationToken);
        }

        return operationResult.Status switch
        {
            AgentOperationStatus.PendingConfirmation => new ToolCallOutcome(
                new AiToolCallResult(call.Name, call.Id, false, null, null, "Confirmation required before this action can run."),
                null,
                operationResult,
                executionResponse.PolicyDenial,
                executionResponse.PendingOperation),
            AgentOperationStatus.Denied or AgentOperationStatus.UnsupportedByPolicy => new ToolCallOutcome(
                toolResult,
                tool.IsReadOnly ? null : new ActionResult(ToolNameToPascalCase(call.Name), ActionStatus.Failed, Error: toolResult.Error),
                operationResult,
                executionResponse.PolicyDenial,
                null),
            _ => new ToolCallOutcome(
                toolResult,
                BuildActionResult(call, tool, ToToolResult(operationResult)),
                operationResult,
                executionResponse.PolicyDenial,
                executionResponse.PendingOperation)
        };
    }

    private static ToolCallOutcome UnknownToolOutcome(AiToolCall call)
    {
        return new ToolCallOutcome(
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

    private static ToolCallOutcome UnsupportedByPolicyOutcome(AiToolCall call, IAiTool tool)
    {
        return new ToolCallOutcome(
            new AiToolCallResult(call.Name, call.Id, false, null, null, "Operation is unsupported by policy."),
            tool.IsReadOnly ? null : new ActionResult(ToolNameToPascalCase(call.Name), ActionStatus.Failed, Error: "Operation is unsupported by policy."),
            new AgentOperationResult(
                call.Name,
                call.Name,
                AgentRiskClass.Low,
                AgentConfirmationRequirement.None,
                AgentOperationStatus.UnsupportedByPolicy,
                Summary: BuildOperationSummary(call),
                PolicyReason: UnsupportedByPolicyReason),
            new AgentPolicyDenial(
                call.Name,
                call.Name,
                AgentRiskClass.Low,
                AgentConfirmationRequirement.None,
                UnsupportedByPolicyReason),
            null);
    }

    private async Task<AgentExecuteOperationResponse> DispatchToolCallAsync(
        AiToolCall call,
        ProcessUserChatCommand request,
        CancellationToken cancellationToken)
    {
        var dispatchArgs = call.Name == "send_support_request" && !string.IsNullOrWhiteSpace(request.CorrelationId)
            ? AppendSupportTrace(call.Args, request.CorrelationId)
            : call.Args;

        return await execution.OperationExecutor.ExecuteAsync(new AgentExecuteOperationRequest(
            request.UserId,
            call.Name,
            dispatchArgs,
            AgentExecutionSurface.Chat,
            request.AuthMethod,
            request.GrantedScopes,
            request.IsReadOnlyCredential,
            request.ConfirmationToken,
            request.CorrelationId), cancellationToken);
    }

    private void LogToolCallOutcome(AiToolCall call, AgentOperationResult operationResult)
    {
        var isClarification = operationResult.Payload is NeedsClarificationPayload;

        if (operationResult.Status == AgentOperationStatus.Succeeded && !isClarification)
        {
            LogToolSucceeded(logger, call.Name, operationResult.TargetName);
        }
        else if (operationResult.Status is AgentOperationStatus.Failed or AgentOperationStatus.Denied)
        {
            LogToolFailed(logger, call.Name, operationResult.PolicyReason);
            if (isClarification)
                LogClarificationDroppedOnFailedTool(logger, call.Name, operationResult.PolicyReason);
        }
    }

    private async Task<ToolCallOutcome> StashClarificationAsync(
        AiToolCall call,
        ProcessUserChatCommand request,
        AiToolCallResult toolResult,
        AgentOperationResult operationResult,
        AgentExecuteOperationResponse executionResponse,
        NeedsClarificationPayload payload,
        CancellationToken cancellationToken)
    {
        var quickActionsJson = payload.QuickActions is null
            ? "[]"
            : JsonSerializer.Serialize(payload.QuickActions);

        var partialArgsJson = call.Args.GetRawText();
        if (partialArgsJson.Length > AppConstants.MaxClarificationArgsLength)
        {
            LogClarificationArgsTooLarge(logger, call.Name, partialArgsJson.Length);
            return new ToolCallOutcome(
                toolResult,
                new ActionResult(
                    ToolNameToPascalCase(call.Name),
                    ActionStatus.Failed,
                    Error: "Tool arguments exceeded the clarification stash limit."),
                operationResult,
                executionResponse.PolicyDenial,
                executionResponse.PendingOperation);
        }

        var stashedId = await execution.PendingClarificationStore.CreateAsync(
            request.UserId,
            call.Name,
            partialArgsJson,
            payload.MissingArgumentKey,
            payload.Question,
            quickActionsJson,
            cancellationToken);
        LogClarificationRequested(logger, call.Name, stashedId, payload.MissingArgumentKey);
        var clarification = new ClarificationRequest(
            payload.Question,
            stashedId,
            payload.MissingArgumentKey,
            payload.QuickActions ?? Array.Empty<QuickAction>());
        return new ToolCallOutcome(
            toolResult,
            new ActionResult(
                ToolNameToPascalCase(call.Name),
                ActionStatus.NeedsClarification,
                EntityName: call.Name,
                ClarificationRequest: clarification),
            operationResult,
            executionResponse.PolicyDenial,
            executionResponse.PendingOperation);
    }

    private sealed record ToolCallOutcome(
        AiToolCallResult ToolResult,
        ActionResult? ActionResult,
        AgentOperationResult? OperationResult,
        AgentPolicyDenial? PolicyDenial,
        PendingAgentOperation? PendingOperation);

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

    private static List<Habit> BuildPromptHabitIndex(IReadOnlyCollection<Habit> userHabits)
    {
        if (userHabits.Count == 0)
            return [];

        var habitsById = userHabits.ToDictionary(habit => habit.Id);
        var indexedHabitIds = new HashSet<Guid>();

        foreach (var habit in userHabits.Where(habit => !habit.IsCompleted))
        {
            var current = habit;

            while (indexedHabitIds.Add(current.Id) &&
                   current.ParentHabitId is Guid parentId &&
                   habitsById.TryGetValue(parentId, out var parent))
            {
                current = parent;
            }
        }

        return userHabits
            .Where(habit => indexedHabitIds.Contains(habit.Id))
            .ToList();
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
        }

        return text;
    }

    private static bool RequiresStreakRecalculation(IEnumerable<ActionResult> actionResults)
    {
        return actionResults.Any(action => action.Status == ActionStatus.Success && action.Type is "LogHabit" or "BulkLogHabits" or "DeleteHabit");
    }

    private sealed class ToolExecutionAccumulator
    {
        private readonly List<string> _relatedSurfaces = [];
        private readonly HashSet<string> _seenRelatedSurfaces = new(StringComparer.Ordinal);

        public List<ActionResult> ActionResults { get; } = [];
        public List<AgentOperationResult> OperationResults { get; } = [];
        public List<PendingAgentOperation> PendingOperations { get; } = [];
        public List<AgentPolicyDenial> PolicyDenials { get; } = [];

        /// <summary>
        /// App surface IDs (e.g. "today", "gamification") surfaced by read-only tools such as
        /// describe_feature, deduplicated in first-seen order. The client maps these to deep links.
        /// </summary>
        public IReadOnlyList<string> RelatedSurfaces => _relatedSurfaces;

        public void Add(
            ActionResult? actionResult,
            AgentOperationResult? operationResult,
            AgentPolicyDenial? policyDenial,
            PendingAgentOperation? pendingOperation)
        {
            if (actionResult is not null)
                ActionResults.Add(actionResult);

            if (operationResult is not null)
            {
                OperationResults.Add(operationResult);
                CollectRelatedSurfaces(operationResult);
            }

            if (policyDenial is not null)
                PolicyDenials.Add(policyDenial);

            if (pendingOperation is not null)
                PendingOperations.Add(pendingOperation);
        }

        private void CollectRelatedSurfaces(AgentOperationResult operationResult)
        {
            if (operationResult.Status != AgentOperationStatus.Succeeded)
                return;

            foreach (var surface in ExtractRelatedSurfaces(operationResult.Payload))
            {
                if (_seenRelatedSurfaces.Add(surface))
                    _relatedSurfaces.Add(surface);
            }
        }
    }

    /// <summary>
    /// Reads the optional "related_surfaces" string array from a tool's anonymous payload
    /// (e.g. describe_feature) by round-tripping it through JSON. Returns an empty sequence
    /// when the payload is null, not an object, or carries no usable surface IDs.
    /// </summary>
    private static IEnumerable<string> ExtractRelatedSurfaces(object? payload)
    {
        if (payload is null)
            return [];

        JsonElement element;
        try
        {
            element = JsonSerializer.SerializeToElement(payload);
        }
        catch (NotSupportedException)
        {
            return [];
        }

        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("related_surfaces", out var surfaces)
            || surfaces.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return surfaces
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
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

    [LoggerMessage(EventId = 24, Level = LogLevel.Information, Message = "Tool {Name} requested clarification (operationId={OperationId}, missing={MissingKey})")]
    private static partial void LogClarificationRequested(ILogger logger, string name, Guid operationId, string missingKey);

    [LoggerMessage(EventId = 25, Level = LogLevel.Warning, Message = "Tool {Name} emitted a clarification payload on a Failed/Denied result and it was dropped: {Reason}")]
    private static partial void LogClarificationDroppedOnFailedTool(ILogger logger, string name, string? reason);

    [LoggerMessage(EventId = 26, Level = LogLevel.Warning, Message = "Tool {Name} requested clarification with oversized partial args ({Length} chars) — dropped without stashing")]
    private static partial void LogClarificationArgsTooLarge(ILogger logger, string name, int length);

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
    /// Returns a copy of the send_support_request args with the correlation id appended to
    /// the message body as a "[trace: {id}]" line, so emailed support tickets carry the trace.
    /// The append respects the support Message length cap; if there is no string message the
    /// args are returned unchanged.
    /// </summary>
    private static JsonElement AppendSupportTrace(JsonElement args, string correlationId)
    {
        var node = JsonNode.Parse(args.GetRawText());
        if (node is not JsonObject argsObject || argsObject["message"] is not JsonValue messageValue
            || !messageValue.TryGetValue(out string? message) || message is null)
        {
            return args;
        }

        var suffix = $"\n\n[trace: {correlationId}]";
        var available = MaxSupportMessageLength - suffix.Length;
        var trimmedMessage = message.Length > available ? message[..Math.Max(0, available)] : message;
        argsObject["message"] = trimmedMessage + suffix;

        return JsonSerializer.Deserialize<JsonElement>(argsObject.ToJsonString());
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
