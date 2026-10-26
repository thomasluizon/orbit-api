using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Grounding;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.ApiKeys.Queries;
using Orbit.Application.ChecklistTemplates.Queries;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Queries;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Services;
using Orbit.Application.Notifications.Queries;
using Orbit.Application.Profile.Queries;
using Orbit.Application.Referrals.Queries;
using Orbit.Application.Tags.Queries;
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
    Func<ChatStreamEvent, Task>? StreamSink = null,
    AstraBlockAnswer? BlockAnswer = null,
    bool EvalTraceRequested = false,
    Guid? TurnId = null) : IRequest<Result<ChatResponse>>;

public record ChatResponse(
    string? AiMessage,
    IReadOnlyList<ActionResult> Actions,
    IReadOnlyList<AgentOperationResult>? Operations = null,
    IReadOnlyList<PendingAgentOperation>? PendingOperations = null,
    IReadOnlyList<AgentPolicyDenial>? PolicyDenials = null,
    string? CorrelationId = null,
    IReadOnlyList<string>? RelatedSurfaces = null,
    HabitListCard? HabitList = null,
    GoalListCard? GoalList = null,
    MetricsCard? MetricsCard = null,
    PeriodInsightCard? PeriodInsight = null,
    DaySummaryCard? DaySummary = null,
    StreakCard? Streak = null,
    CalendarCard? Calendar = null,
    IReadOnlyList<RecordListCard>? RecordLists = null,
    AccountRowsCard? AccountRows = null,
    IReadOnlyList<string>? FollowUps = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ChatResponsePart>? Parts = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ChatEvalTrace? EvalTrace = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ChatFollowUp>? FollowUpContext = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? MessageAllowanceConsumed = null)
{
    [JsonIgnore]
    public TurnFactSet GroundingFacts { get; init; } = TurnFactSet.Empty;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ChatTextPart), "text")]
[JsonDerivedType(typeof(ChatBlockPart), "block")]
public abstract record ChatResponsePart;

public sealed record ChatTextPart(string Text) : ChatResponsePart;

public sealed record ChatBlockPart(string Kind, object Block) : ChatResponsePart;

public record ResponseCards(
    string? AiMessage,
    HabitListCard? HabitList = null,
    GoalListCard? GoalList = null,
    MetricsCard? MetricsCard = null,
    PeriodInsightCard? PeriodInsight = null,
    DaySummaryCard? DaySummary = null,
    StreakCard? Streak = null,
    CalendarCard? Calendar = null,
    IReadOnlyList<RecordListCard>? RecordLists = null,
    AccountRowsCard? AccountRows = null,
    IReadOnlyList<ChatResponsePart>? Parts = null,
    HabitRankingCard? HabitRanking = null)
{
    public bool ServerComposedText { get; init; }

    public bool HasAnyCard => HabitList is not null || GoalList is not null
        || MetricsCard is not null || PeriodInsight is not null || DaySummary is not null
        || Streak is not null || Calendar is not null || RecordLists is { Count: > 0 }
        || AccountRows is not null || HabitRanking is not null || Parts?.OfType<ChatBlockPart>().Any() == true;
}

public record ActionResult(
    string Type,
    ActionStatus Status,
    Guid? EntityId = null,
    string? EntityName = null,
    string? Error = null,
    string? Field = null,
    IReadOnlyList<AiAction>? SuggestedSubHabits = null,
    ClarificationRequest? ClarificationRequest = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MessageKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ToolResultMessageParams? Params = null);

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
    IPendingClarificationStore PendingClarificationStore,
    IGoalProgressReadSyncer GoalProgressReadSyncer,
    IGamificationService GamificationService,
    IMediator Mediator,
    IProductAnalytics ProductAnalytics,
    IHabitScheduleLogReader ScheduleLogReader,
    IIssuedAstraBlockStore BlockStore,
    IPendingAgentOperationStore PendingOperationStore,
    IPendingOperationChangePreviewer? PendingOperationPreviewer = null,
    TimeSpan? BlockAnswerTimeout = null);

public partial class ProcessUserChatCommandHandler(
    ChatDataDependencies data,
    ChatAiDependencies ai,
    ChatExecutionDependencies execution,
    ILogger<ProcessUserChatCommandHandler> logger,
    IAppConfigService appConfig,
    IContentModerationService moderation,
    IConversationTurnRecorder? conversationRecorder = null, ReadBlockFactory? readBlocks = null,
    TimeProvider? timeProvider = null) : IRequestHandler<ProcessUserChatCommand, Result<ChatResponse>>
{
    private static class PartKinds
    {
        public const string HabitList = "habitList";
        public const string GoalList = "goalList";
        public const string Metrics = "metrics";
        public const string PeriodInsight = "periodInsight";
        public const string HabitRanking = HabitRankingCardBuilder.Kind;
        public const string DaySummary = "daySummary";
        public const string Streak = "streak";
        public const string Calendar = "calendar";
        public const string RecordList = "recordList";
        public const string AccountRows = "accountRows";
    }

    private const string UnsupportedByPolicyReason = "unsupported_by_policy";
    private const string DescribeFeatureToolName = "describe_feature";
    private const string EnglishToolFailureMessage = "I couldn't complete that. Please try again.";
    private const string PortugueseToolFailureMessage = "Não consegui concluir isso. Tente novamente.";
    private const string EnglishTruncationMessage = "This response was cut off before completion, so the result is partial.";
    private const string PortugueseTruncationMessage = "Esta resposta foi interrompida antes do fim e pode estar incompleta.";

    private const int MaxSupportMessageLength = 5000;

    public async Task<Result<ChatResponse>> Handle(
        ProcessUserChatCommand request,
        CancellationToken cancellationToken)
    {
        using var timer = ChatTurnTimer.Begin(timeProvider ?? TimeProvider.System);
        ChatEvalTraceCollector? evalTrace = null;
        var recording = new ChatTurnState();
        Result<ChatResponse>? response = null;
        try
        {
            if (request.EvalTraceRequested)
            {
                var flags = await data.FeatureFlagService.GetEnabledKeysForUserAsync(request.UserId, cancellationToken);
                if (flags.Contains(FeatureFlagKeys.AstraEvalTrace, StringComparer.Ordinal))
                    evalTrace = new ChatEvalTraceCollector(request.CorrelationId ?? Guid.NewGuid().ToString("N"), ai.ToolRegistry);
            }
            response = await HandleCoreAsync(request, recording, evalTrace, cancellationToken);
            if (response.IsSuccess)
            {
                var negotiated = ChatResponseNegotiation.ForAllowance(response.Value, request.ClientContext,
                    recording.MessageAllowanceConsumed);
                response = Result.Success(negotiated);
            }
        }
        finally
        {
            RecordConversationSafely(request, recording, response);
            var timing = timer.Complete();
            logger.LogInformation("Astra turn timing {Phases} total {TotalMilliseconds} ms",
                JsonSerializer.Serialize(timing.Phases, ChatStreamEvent.SerializerOptions), timing.TotalMilliseconds);
        }
        return response.IsSuccess && evalTrace is not null
            ? Result.Success(response.Value with { EvalTrace = evalTrace.Build(timer.Complete()) })
            : response;
    }

    private async Task<Result<ChatResponse>> HandleCoreAsync(ProcessUserChatCommand request,
        ChatTurnState recording, ChatEvalTraceCollector? evalTrace, CancellationToken cancellationToken)
    {
        if (request.BlockAnswer is not null)
        {
            recording.Enabled = true;
            var answerService = new AstraBlockAnswerService(execution.BlockStore, execution.UnitOfWork,
                execution.PayGateService, execution.OperationExecutor, data.UserRepository, execution.ProductAnalytics,
                data.FeatureFlagService, execution.PendingOperationStore, logger, execution.PendingOperationPreviewer, execution.BlockAnswerTimeout,
                prepared =>
                {
                    recording.UserContent = prepared.Message;
                    recording.Enabled = !prepared.IsReplay;
                    recording.MessageAllowanceConsumed = prepared.MessageAllowanceConsumed;
                }, recording.ToolCalls.Add);
            return await answerService.ExecuteAsync(request, prepared =>
            {
                if (prepared.ModeratedInput is { } flaggedInput)
                {
                    recording.Enabled = false;
                    return HandleModeratedTurnAsync(request, flaggedInput, cancellationToken);
                }
                return prepared.Crisis != CrisisLocales.None
                    ? HandleCrisisTurnAsync(request with { Message = prepared.Message }, prepared.Crisis, captureMessage: true)
                    : HandleTurnAsync(request with { Message = prepared.Message }, recording, evalTrace, cancellationToken, prepared.AnsweredAtUtc);
            }, cancellationToken, (_, token) => ModerateInputAsync(request, token));
        }
        var crisis = CrisisSupportGuard.Detect(request.Message);
        if (crisis != CrisisLocales.None)
        {
            var enabledFlags = await data.FeatureFlagService.GetEnabledKeysForUserAsync(request.UserId, cancellationToken);
            if (CrisisSupportGuard.IsEnabled(crisis, enabledFlags))
            {
                recording.Enabled = enabledFlags.Contains(FeatureFlagKeys.AstraConversationRecording);
                return await HandleCrisisTurnAsync(request, crisis, captureMessage: true);
            }
        }
        var inputCheck = await ModerateInputAsync(request, cancellationToken);
        if (inputCheck.IsFailure)
            return inputCheck.PropagateError<ChatResponse>();
        if (inputCheck.Value.Flagged)
            return await HandleModeratedTurnAsync(request, inputCheck.Value, cancellationToken);
        await execution.UnitOfWork.ExecuteInTransactionAsync(async token =>
        {
            await execution.UnitOfWork.AcquireAdvisoryLockAsync($"astra-blocks:{request.UserId}", token);
            await execution.BlockStore.InvalidateOpenAsync(request.UserId, token);
        }, cancellationToken);
        return await HandleTurnAsync(request, recording, evalTrace, cancellationToken);
    }

    private async Task<Result<ModerationResult>> ModerateInputAsync(ProcessUserChatCommand request, CancellationToken cancellationToken)
    {
        using var phase = ChatTurnTimer.Measure(ChatPhase.Moderation);
        var input = JsonSerializer.Serialize(new { request.Message, request.History, request.BlockAnswer });
        var check = await moderation.CheckTextAsync(input, cancellationToken);
        if (check.Unavailable)
            return Result.Failure<ModerationResult>(ErrorMessages.AiUnavailable);
        return Result.Success(check);
    }

    private async Task<Result<ChatResponse>> HandleModeratedTurnAsync(
        ProcessUserChatCommand request, ModerationResult check, CancellationToken cancellationToken)
    {
        var user = await data.UserRepository.GetByIdAsync(request.UserId, cancellationToken);
        var isPortuguese = LocaleHelper.IsPortuguese(user?.Language);
        if (check.Categories.Any(category => category is "self-harm" or "self-harm/intent" or "self-harm/instructions"))
            return await HandleCrisisTurnAsync(request,
                isPortuguese ? CrisisLocales.Portuguese : CrisisLocales.English, captureMessage: true);
        var message = ErrorCopy.Resolve(ErrorCodes.ContentModerationRefused, isPortuguese);
        if (request.ClientContext?.SupportsMessageAllowance != true)
            return Result.Failure<ChatResponse>(message, ErrorCodes.ContentModerationRefused);
        if (request.StreamSink is not null)
        {
            await request.StreamSink(ChatStreamEvent.Reset());
            await request.StreamSink(ChatStreamEvent.Delta(message));
        }
        return Result.Success(new ChatResponse(message, [], CorrelationId: request.CorrelationId,
            Parts: request.ClientContext?.SupportsParts == true ? [new ChatTextPart(message)] : null));
    }

    private CancellationTokenSource CreateBlockTurnCancellation(CancellationToken token, DateTime? answeredAtUtc)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (answeredAtUtc is { } claim && execution.BlockAnswerTimeout is { } timeout)
        {
            var remaining = claim + timeout - TimeProvider.System.GetUtcNow().UtcDateTime;
            cancellation.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }
        return cancellation;
    }

    private async Task<Result<ChatResponse>> HandleTurnAsync(ProcessUserChatCommand request, ChatTurnState recording,
        ChatEvalTraceCollector? evalTrace, CancellationToken cancellationToken,
        DateTime? answeredAtUtc = null)
    {
        using var turnCancellation = CreateBlockTurnCancellation(cancellationToken, answeredAtUtc);
        cancellationToken = turnCancellation.Token;
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        LogProcessingChatMessage(logger, request.Message);

        var contextResult = await LoadChatContextAsync(request, cancellationToken);
        if (contextResult.IsFailure)
            return contextResult.PropagateError<ChatResponse>();

        var context = contextResult.Value;
        recording.Enabled = context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AstraConversationRecording);
        var plan = GetAnalyticsPlan(context.User);
        var telemetry = new AiTurnTelemetry(request.CorrelationId, plan);
        try
        {
            var result = await HandleLoadedTurnAsync(request, context, recording, evalTrace, telemetry, totalStopwatch, answeredAtUtc, cancellationToken);
            if (result.IsFailure && result.ErrorCode == ErrorCodes.AiEmptyResponse)
            {
                logger.LogWarning("Empty AI completion at round {RoundIndex} for {UserId} {CorrelationId}",
                    telemetry.RoundCount, request.UserId, request.CorrelationId);
                return request.BlockAnswer is not null ? result
                    : Result.Success(await ChatResponseNegotiation.RecoverEmptyAnswerAsync(request, context.User?.Language, null, cancellationToken));
            }
            if (result.IsSuccess)
            {
                var readable = ChatResponseNegotiation.ForPendingPreview(result.Value,
                    ApplyClientKillFlags(request, context.EnabledFeatureFlags).ClientContext?.SupportsParts == true,
                    GetUserLanguage(context.User));
                if (!ReferenceEquals(readable, result.Value))
                {
                    if (request.StreamSink is { } sink)
                    {
                        await sink(ChatStreamEvent.Reset());
                        await sink(ChatStreamEvent.Delta(readable.AiMessage!));
                    }
                    result = Result.Success(readable);
                }
            }
            if (result.IsSuccess && !HasDisplayableAnswer(result.Value))
            {
                var outputKinds = (result.Value.Parts ?? []).GroupBy(part => part switch
                {
                    ChatBlockPart block => block.Kind,
                    ChatTextPart => "text",
                    _ => "unknown"
                }).ToDictionary(group => group.Key, group => group.Count());
                logger.LogWarning("Empty composed AI answer at round {RoundIndex}; output kinds {OutputKinds} for {UserId} {CorrelationId}",
                    telemetry.RoundCount, JsonSerializer.Serialize(outputKinds), request.UserId, request.CorrelationId);
                return Result.Success(await ChatResponseNegotiation.RecoverEmptyAnswerAsync(request, context.User?.Language, result.Value, cancellationToken));
            }
            return result;
        }
        finally
        {
            CaptureMessageAnalytics(request.UserId, plan, telemetry);
        }
    }

    private static bool HasDisplayableAnswer(ChatResponse response) =>
        !string.IsNullOrWhiteSpace(response.AiMessage)
        || response.Parts?.Any(part => part is ChatBlockPart || part is ChatTextPart text && !string.IsNullOrWhiteSpace(text.Text)) == true
        || response.Actions.Count > 0 || response.PendingOperations is { Count: > 0 }
        || response.Operations?.Any(operation => operation.Status is AgentOperationStatus.Failed
            or AgentOperationStatus.Denied or AgentOperationStatus.UnsupportedByPolicy) == true
        || response.HabitList is not null || response.GoalList is not null || response.MetricsCard is not null
        || response.PeriodInsight is not null || response.DaySummary is not null || response.Streak is not null
        || response.Calendar is not null || response.RecordLists is { Count: > 0 } || response.AccountRows is not null;

    private async Task<Result<ChatResponse>> HandleLoadedTurnAsync(
        ProcessUserChatCommand request, ChatContext context, ChatTurnState recording, ChatEvalTraceCollector? evalTrace, AiTurnTelemetry telemetry,
        System.Diagnostics.Stopwatch totalStopwatch, DateTime? answeredAtUtc, CancellationToken cancellationToken)
    {
        var detectedCrisisLocales = CrisisSupportGuard.Detect(request.Message);
        var crisisTurn = CrisisSupportGuard.IsEnabled(detectedCrisisLocales, context.EnabledFeatureFlags);
        if (crisisTurn)
            return await HandleCrisisTurnAsync(request, detectedCrisisLocales);

        request = ApplyClientKillFlags(request, context.EnabledFeatureFlags);
        CaptureFollowUpSentEvent(request, context.User);

        var userLanguage = GetUserLanguage(context.User);
        GroundingTurn groundingTurn;
        using (ChatTurnTimer.Measure(ChatPhase.ContextLoad))
            groundingTurn = await LoadGroundingTurnAsync(context, cancellationToken);
        var composition = UsesComposition(request, context);
        var bufferedStream = new List<ChatStreamEvent>();
        Func<ChatStreamEvent, Task>? bufferedSink = request.StreamSink is null ? null : item =>
        {
            bufferedStream.Add(item);
            return Task.CompletedTask;
        };
        var aiStreamFilter = BuildAiStreamFilter(groundingTurn.Enforce || composition ? null : bufferedSink);
        Func<AiStreamEvent, Task>? aiStreamSink = aiStreamFilter is null
            ? null
            : aiStreamFilter.HandleAsync;

        var faqMatch = ChatFaqCache.TryMatchFaqKey(request.Message);
        var cachedFaqAnswer = await GetGroundedCachedFaqAnswerAsync(faqMatch, groundingTurn, request, composition);
        if (cachedFaqAnswer is not null)
            return Result.Success(ComposeCachedFaq(cachedFaqAnswer, request, composition));

        if (await TryHandlePlacementTurnAsync(request, cancellationToken) is { } placementReply)
            return placementReply;

        if (await TryHandlePendingParentTurnAsync(request, context, cancellationToken) is { } pendingReply)
            return pendingReply;

        var aiStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var response = await RequestInitialAiResponseAsync(request, context, aiStreamSink, telemetry, recording, cancellationToken);
        aiStopwatch.Stop();
        LogAiIntentServiceCompleted(logger, aiStopwatch.ElapsedMilliseconds);

        if (await RecoverExplicitHeadingsAsync(request, context, response, null, cancellationToken) is { } recoveredHeadings)
            return recoveredHeadings;

        if (response.IsFailure)
            return response.PropagateError<ChatResponse>();

        recording.UsedModel = true;
        var executionResults = new ToolExecutionAccumulator
        {
            AnsweredAtUtc = answeredAtUtc,
            Recording = recording,
            EvalTrace = evalTrace,
            Habits = context.ActiveHabits,
            AllHabits = context.UserHabits,
            IsTaggingIntent = IsTagOnlyIntent(request.Message),
            RequiresExistingParent = RequestsExistingParent(request.Message, context.UserHabits),
            ParentHeadings = HabitHeadingParents.Read(request.Message, context.UserHabits)
        };
        SeedProposedHabitNames(request, executionResults);
        var actionsStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var toolLoopResult = await RunToolCallLoopAsync(
            response.Value,
            request,
            executionResults,
            userLanguage,
            aiStreamSink,
            cancellationToken);
        await CombinePendingChangesAsync(request, executionResults, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var recoveredPreviewOnly = toolLoopResult.ProviderError is not null && !HasSucceededWrite(executionResults);
        toolLoopResult = RecoverOutcomeContinuation(request, executionResults, toolLoopResult);
        var aiResponse = toolLoopResult.FinalResponse;
        var iterations = toolLoopResult.Iterations;
        if (MustFailToolTurn(request, toolLoopResult))
            return await CompleteFailedToolTurnAsync(request, context, executionResults, toolLoopResult, cancellationToken);
        if (toolLoopResult.HadToolFailure)
            executionResults.SanitizeFailedActions(ToolFailureMessage(userLanguage));
        if (aiStreamFilter is not null)
            await aiStreamFilter.FlushAsync();
        if (executionResults.PendingOperations.Count == 0 && request.StreamSink is { } streamSink)
            foreach (var item in bufferedStream) await streamSink(item);
        actionsStopwatch.Stop();
        LogToolExecutionCompleted(logger, actionsStopwatch.ElapsedMilliseconds, iterations, executionResults.ActionResults.Count);

        LogSavingChanges(logger);
        var saveStopwatch = System.Diagnostics.Stopwatch.StartNew();
        if (!recoveredPreviewOnly)
            await PersistExecutionResultsAsync(request.UserId, executionResults.ActionResults, cancellationToken);
        saveStopwatch.Stop();
        LogChangesSaved(logger, saveStopwatch.ElapsedMilliseconds);

        if (IsEmptyTokenBudgetResponse(toolLoopResult.TokenBudgetExceeded, aiResponse.TextMessage))
            return Result.Failure<ChatResponse>(ErrorMessages.AiUnavailable);

        var (cards, followUps, facts) = await ComposeFinalReplyAsync(request, context, executionResults,
            toolLoopResult, aiResponse, groundingTurn, cancellationToken);
        var aiMessage = cards.AiMessage;

        if (GetShareableFaqAnswer(faqMatch, aiMessage, executionResults, cards) is { } faqToCache)
        {
            ChatFaqCache.StoreAnswer(faqToCache.Key, faqToCache.Locale, faqToCache.Answer);
        }

        CaptureResponseCardEvents(request, context, cards);

        CaptureChangePreviewEvents(request, context.User, executionResults.PendingOperations);
        CaptureFollowUpsEmittedEvent(request, context.User, followUps);

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
            executionResults.RelatedSurfaces.Count > 0 ? executionResults.RelatedSurfaces : null,
            cards.HabitList,
            cards.GoalList,
            cards.MetricsCard,
            cards.PeriodInsight,
            cards.DaySummary,
            cards.Streak,
            cards.Calendar,
            cards.RecordLists,
            cards.AccountRows,
            followUps,
            cards.Parts,
            FollowUpContext: BuildFollowUpContext(request, context, executionResults, followUps))
        { GroundingFacts = facts });
    }

    private void CaptureMessageAnalytics(Guid userId, string? plan, AiTurnTelemetry telemetry)
    {
        var properties = new Dictionary<string, object>
        {
            ["round_count"] = telemetry.RoundCount,
            ["tool_call_count"] = telemetry.ToolCallCount
        };
        if (plan is not null)
            properties["plan"] = plan;
        AnalyticsCapture.SafeCaptureUserEvent(execution.ProductAnalytics, logger, userId, plan,
            "astra_message_sent", properties);
    }

    private static string GetAnalyticsPlan(User? user) =>
        user?.Plan.ToString() ?? nameof(UserPlan.Free);

    private static string? GetUserLanguage(User? user) => user?.Language;

    private static ProcessUserChatCommand ApplyClientKillFlags(
        ProcessUserChatCommand request,
        IReadOnlyList<string> enabledFlags)
    {
        if (request.ClientContext is not { } clientContext)
            return request;

        return request with
        {
            ClientContext = clientContext with
            {
                SupportsPendingOperationChanges = clientContext.SupportsPendingOperationChanges == true
                    && !enabledFlags.Contains(FeatureFlagKeys.AstraChangePreviewDisabled, StringComparer.OrdinalIgnoreCase),
                SupportsToolSteps = clientContext.SupportsToolSteps == true
                    && !enabledFlags.Contains(FeatureFlagKeys.AstraToolStepsDisabled, StringComparer.OrdinalIgnoreCase),
                SupportsParts = clientContext.SupportsParts == true
                    && !enabledFlags.Contains(FeatureFlagKeys.AstraPartsDisabled, StringComparer.OrdinalIgnoreCase),
                SupportsFollowUps = clientContext.SupportsFollowUps == true
                    && !enabledFlags.Contains(FeatureFlagKeys.AstraFollowUpsDisabled, StringComparer.OrdinalIgnoreCase)
            }
        };
    }

    private static bool CanEmitFollowUps(
        ProcessUserChatCommand request,
        ToolLoopResult toolLoopResult,
        ToolExecutionAccumulator results,
        AiResponse aiResponse) =>
        request.ClientContext?.SupportsFollowUps == true
        && !aiResponse.IsTruncated
        && !toolLoopResult.TokenBudgetExceeded
        && !toolLoopResult.HadToolFailure
        && results.AskBlock is null
        && results.PendingOperations.Count == 0
        && !results.ActionResults.Any(action => action.Status == ActionStatus.NeedsClarification);

    private void CaptureFollowUpSentEvent(ProcessUserChatCommand request, User? user)
    {
        if (user is null || request.ClientContext?.MessageOrigin != "followUp")
            return;

        AnalyticsCapture.SafeCaptureUserEvent(
            execution.ProductAnalytics, logger, request.UserId, user.Plan.ToString(),
            "astra_follow_up_sent",
            new Dictionary<string, object>
            {
                ["platform"] = request.ClientContext.Platform ?? "unknown"
            });
    }

    private void CaptureFollowUpsEmittedEvent(
        ProcessUserChatCommand request,
        User? user,
        IReadOnlyList<string>? followUps)
    {
        if (user is null || followUps is null)
            return;

        AnalyticsCapture.SafeCaptureUserEvent(
            execution.ProductAnalytics, logger, request.UserId, user.Plan.ToString(),
            "chat_follow_ups_emitted",
            new Dictionary<string, object>
            {
                ["count"] = followUps.Count,
                ["platform"] = request.ClientContext?.Platform ?? "unknown"
            });
    }

    private void CaptureChangePreviewEvents(
        ProcessUserChatCommand request,
        User? user,
        IReadOnlyList<PendingAgentOperation> pendingOperations)
    {
        if (user is null)
            return;

        foreach (var pending in pendingOperations.Where(item => item.Changes is not null))
        {
            AnalyticsCapture.SafeCaptureUserEvent(
                execution.ProductAnalytics,
                logger,
                request.UserId,
                user.Plan.ToString(),
                "chat_change_preview_card_emitted",
                new Dictionary<string, object>
                {
                    ["platform"] = request.ClientContext?.Platform ?? "unknown",
                    ["kind"] = pending.CapabilityId
                });
        }
    }

    private static string? TryGetCachedFaqAnswer((string Key, string Locale)? match) =>
        match is { } faq && ChatFaqCache.TryGetAnswer(faq.Key, faq.Locale, out var answer)
            ? answer
            : null;

    private static bool IsEmptyTokenBudgetResponse(bool exceeded, string? text) =>
        exceeded && string.IsNullOrWhiteSpace(text);

    private static (string Key, string Locale, string Answer)? GetShareableFaqAnswer(
        (string Key, string Locale)? match,
        string? aiMessage,
        ToolExecutionAccumulator executionResults,
        ResponseCards cards)
    {
        if (match is null || string.IsNullOrWhiteSpace(aiMessage)
            || !IsShareableFaqTurn(executionResults, cards))
            return null;

        return (match.Value.Key, match.Value.Locale, aiMessage);
    }

    private void CaptureCardEvent(
        ProcessUserChatCommand request, ChatContext context, string eventName, string kind,
        IReadOnlyList<ChatResponsePart>? parts)
    {
        if (context.User is null)
            return;

        AnalyticsCapture.SafeCaptureUserEvent(
            execution.ProductAnalytics, logger, request.UserId, context.User.Plan.ToString(),
            eventName, new Dictionary<string, object>
            {
                ["platform"] = request.ClientContext?.Platform ?? "unknown",
                ["kind"] = kind,
                ["part_count"] = parts?.Count ?? 0,
                ["block_kinds"] = parts?.OfType<ChatBlockPart>().Select(part => part.Kind).ToArray() ?? []
            });
    }

    private void CaptureResponseCardEvents(
        ProcessUserChatCommand request, ChatContext context, ResponseCards cards)
    {
        CaptureReadMetadata(request.UserId, cards);
        void Capture(string eventName, string kind) =>
            CaptureCardEvent(request, context, eventName, kind, cards.Parts);

        if (cards.Parts is not null)
        {
            var events = cards.Parts.OfType<ChatBlockPart>().Select(part => part.Block switch
            {
                MetricsCard => ("chat_metrics_card_emitted", PartKinds.Metrics),
                PeriodInsightCard => ("chat_period_insight_card_emitted", "period"),
                DaySummaryCard => ("chat_day_summary_card_emitted", "day"),
                StreakCard => ("chat_streak_card_emitted", PartKinds.Streak),
                CalendarCard => ("chat_calendar_card_emitted", PartKinds.Calendar),
                RecordListCard recordList => ("chat_record_list_card_emitted", recordList.Kind),
                AccountRowsCard accountRows => ("chat_account_rows_card_emitted", accountRows.Kind),
                _ => (string.Empty, string.Empty)
            }).Distinct();
            foreach (var (eventName, kind) in events)
            {
                if (eventName.Length > 0)
                    Capture(eventName, kind);
            }
            return;
        }

        if (cards.MetricsCard is not null)
            Capture("chat_metrics_card_emitted", PartKinds.Metrics);
        if (cards.PeriodInsight is not null)
            Capture("chat_period_insight_card_emitted", "period");
        if (cards.DaySummary is not null)
            Capture("chat_day_summary_card_emitted", "day");
        if (cards.Streak is not null)
            Capture("chat_streak_card_emitted", PartKinds.Streak);
        if (cards.Calendar is not null)
            Capture("chat_calendar_card_emitted", PartKinds.Calendar);
        foreach (var recordList in cards.RecordLists ?? [])
            Capture("chat_record_list_card_emitted", recordList.Kind);
        if (cards.AccountRows is not null)
            Capture("chat_account_rows_card_emitted", cards.AccountRows.Kind);
    }

    private async Task<Result<ChatResponse>> HandleCrisisTurnAsync(
        ProcessUserChatCommand request,
        CrisisLocales locales,
        bool captureMessage = false)
    {
        if (captureMessage)
            CaptureMessageAnalytics(request.UserId, null, new AiTurnTelemetry(request.CorrelationId));
        var message = await CompleteCrisisReplyAsync(
            request, CrisisSupportGuard.FallbackMessage(locales), locales,
            request.StreamSink is null ? "batch" : "stream");
        return Result.Success(new ChatResponse(message, [], CorrelationId: request.CorrelationId));
    }

    private async Task<string> CompleteCrisisReplyAsync(
        ProcessUserChatCommand request,
        string? reply,
        CrisisLocales locales,
        string deliveryPath)
    {
        var finalText = CrisisSupportGuard.EnsureResources(reply, locales, request.History);
        if (request.StreamSink is not null)
        {
            await request.StreamSink(ChatStreamEvent.Reset());
            await request.StreamSink(ChatStreamEvent.Delta(finalText));
        }

        if (finalText.Contains(CrisisSupportGuard.EnglishResource, StringComparison.Ordinal)
            || finalText.Contains(CrisisSupportGuard.PortugueseResource, StringComparison.Ordinal))
        {
            AnalyticsCapture.SafeCaptureAggregateEvent(
                execution.ProductAnalytics,
                logger,
                "astra_crisis_resources_shown",
                new Dictionary<string, object>
                {
                    ["count"] = 1,
                    ["locale"] = CrisisSupportGuard.LocaleLabel(locales),
                    ["delivery_path"] = deliveryPath
                });
        }

        return finalText;
    }

    private static string AppendTruncationNotice(string? text, string? language)
    {
        var notice = LocaleHelper.IsPortuguese(language)
            ? PortugueseTruncationMessage
            : EnglishTruncationMessage;
        return string.IsNullOrWhiteSpace(text) ? notice : $"{text}\n\n{notice}";
    }

    private async Task<ResponseCards> BuildResponsePartsAsync(
        ParsedResponseDirectives parsed, bool isTruncated, string? language,
        ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator results, CancellationToken cancellationToken)
    {
        var parts = new List<ChatResponsePart>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blockCount = 0;
        foreach (var segment in parsed.Segments)
        {
            if (segment.Text is { } text)
            {
                parts.Add(new ChatTextPart(text));
                continue;
            }

            if (segment.Directive is not { } directive || blockCount >= 3 || !seen.Add(directive))
                continue;

            var bound = ResolveQueryRead(directive, results);
            var kind = bound?.Kind ?? DirectivePartKind(directive);
            if (bound is not null)
            {
                if (!ReadEnabled(bound, request.ClientContext, context.EnabledFeatureFlags))
                {
                    if (ReadFallback(bound.Block) is { Length: > 0 } fallback) parts.Add(new ChatTextPart(fallback));
                    continue;
                }
                parts.Add(new ChatBlockPart(bound.Kind, ForClient(bound, request.ClientContext, context.EnabledFeatureFlags).Block));
                blockCount++;
                continue;
            }
            if (kind is null || request.ClientContext?.PartKinds?.Contains(kind, StringComparer.Ordinal) != true)
                continue;
            if (directive.StartsWith("[[orbit:read:", StringComparison.OrdinalIgnoreCase)) continue;
            var cards = await TryBuildResponseCardsAsync(directive, request, context, results, cancellationToken);
            object? block = kind switch
            {
                PartKinds.HabitList => cards.HabitList,
                PartKinds.GoalList => cards.GoalList,
                PartKinds.Metrics => cards.MetricsCard,
                PartKinds.PeriodInsight => cards.PeriodInsight,
                PartKinds.HabitRanking => cards.HabitRanking,
                PartKinds.DaySummary => cards.DaySummary,
                PartKinds.Streak => cards.Streak,
                PartKinds.Calendar => cards.Calendar,
                PartKinds.RecordList => cards.RecordLists is { Count: > 0 } lists ? lists[0] : null,
                PartKinds.AccountRows => cards.AccountRows,
                _ => null
            };
            if (block is null)
            {
                if (!string.IsNullOrWhiteSpace(cards.AiMessage)) parts.Add(new ChatTextPart(cards.AiMessage));
                continue;
            }

            parts.Add(new ChatBlockPart(kind, block));
            blockCount++;
        }

        if (isTruncated)
            parts.Add(new ChatTextPart(AppendTruncationNotice(null, language)));

        return new ResponseCards(string.Join("\n\n", parts.OfType<ChatTextPart>().Select(part => part.Text)),
            Parts: parts);
    }

    private static string? DirectivePartKind(string directive) => directive.ToLowerInvariant() switch
    {
        "[[orbit:habits:today]]" or "[[orbit:habits:remaining]]" or "[[orbit:habits:all]]" => PartKinds.HabitList,
        "[[orbit:goals]]" => PartKinds.GoalList,
        "[[orbit:metrics]]" => PartKinds.Metrics,
        "[[orbit:insight]]" => PartKinds.PeriodInsight,
        HabitRankingCardBuilder.Directive => PartKinds.HabitRanking,
        "[[orbit:day]]" => PartKinds.DaySummary,
        "[[orbit:streak]]" => PartKinds.Streak,
        "[[orbit:calendar]]" => PartKinds.Calendar,
        "[[orbit:records:notifications]]" or "[[orbit:records:tags]]"
            or "[[orbit:records:templates]]" or "[[orbit:records:keys]]" => PartKinds.RecordList,
        "[[orbit:account:profile]]" or "[[orbit:account:plan]]" or "[[orbit:account:referral]]" => PartKinds.AccountRows,
        _ => null
    };

    private async Task<ResponseCards> TryBuildResponseCardsAsync(
        string? aiMessage, ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator executionResults, CancellationToken cancellationToken)
    {
        try
        {
            aiMessage = ReplaceUnavailableReadDirectives(aiMessage, executionResults, request.ClientContext, context.EnabledFeatureFlags);
            var boundRead = ResolveQueryRead(aiMessage, executionResults);
            if (boundRead is not null && ReadEnabled(boundRead, request.ClientContext, context.EnabledFeatureFlags))
                return ResponseFromRead(StripAllDirectives(aiMessage), ForClient(boundRead, request.ClientContext, context.EnabledFeatureFlags));
            return RetainCompatibleCards(await BuildResponseCardsAsync(aiMessage, request, context, executionResults, cancellationToken), request.ClientContext);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogResponseCardBuildThrew(logger, ex);
            return new ResponseCards(StripAllDirectives(aiMessage));
        }
    }

    private static string? StripAllDirectives(string? message) =>
        message is null ? null : Regex.Replace(message, @"\[\[orbit:(?:read:[A-Za-z0-9_-]+|[a-z:]+)\]\]", string.Empty,
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)).Trim();

    private async Task<ResponseCards> BuildResponseCardsAsync(
        string? aiMessage,
        ProcessUserChatCommand request,
        ChatContext context,
        ToolExecutionAccumulator executionResults,
        CancellationToken cancellationToken)
    {
        HabitListCard? habitList = null;
        if (HabitListCardBuilder.TryExtractScope(aiMessage, out var habitListScope, out var strippedMessage, out var remaining))
        {
            aiMessage = strippedMessage;
            if (request.ClientContext?.SupportsHabitListCard == true)
                habitList = await BuildHabitListFromQueryAsync(request, context, executionResults, habitListScope, remaining, cancellationToken);
        }

        GoalListCard? goalList = null;
        if (GoalListCardBuilder.TryExtractDirective(aiMessage, out var strippedGoalMessage))
        {
            aiMessage = strippedGoalMessage;
            if (request.ClientContext?.SupportsGoalListCard == true)
            {
                var includeProjections = !context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AstraGoalProjectionsDisabled);
                goalList = await BuildGoalReadAsync(request, context, executionResults, includeProjections, cancellationToken);
                if (!includeProjections && goalList is not null)
                    goalList = GoalListCardBuilder.WithoutProjections(goalList);
            }
        }

        MetricsCard? metricsCard = null;
        if (MetricsCardBuilder.TryExtractDirective(aiMessage, out var strippedMetricsMessage))
        {
            aiMessage = strippedMetricsMessage;
            if (request.ClientContext?.SupportsMetricsCard == true
                && !context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AstraPeriodBlocksDisabled))
            {
                var habitMetrics = executionResults.LastSuccessfulPayload<HabitMetrics>("get_habit_metrics");
                metricsCard = habitMetrics?.HabitId is not null
                    ? MetricsCardBuilder.BuildHabit(habitMetrics)
                    : await TryBuildMetricsCardAsync(request.UserId, context.UserToday, cancellationToken);
            }
        }

        PeriodInsightCard? periodInsight = null;
        if (aiMessage?.Contains(PeriodInsightCardBuilder.Directive, StringComparison.OrdinalIgnoreCase) == true
            && request.ClientContext?.SupportsPeriodInsightCard == true
            && !context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AstraPeriodBlocksDisabled))
        {
            periodInsight = PeriodInsightCardBuilder.Build(
                executionResults.LastSuccessfulPayload<RetrospectiveResponse>("get_retrospective"));
        }

        var habitRanking = BuildRequestedHabitRanking(aiMessage, request, context, executionResults);
        var (daySummary, streak, calendar) = BuildStatusCards(aiMessage, request, context, executionResults);
        var recordLists = BuildRecordLists(aiMessage, request, context, executionResults);
        var accountRows = BuildAccountRows(aiMessage, request, context, executionResults);

        aiMessage = StripAllDirectives(aiMessage);
        return CarryQueryReads(new ResponseCards(aiMessage, habitList, goalList, metricsCard, periodInsight,
            daySummary, streak, calendar, recordLists, accountRows, HabitRanking: habitRanking),
            executionResults, request.ClientContext, context.EnabledFeatureFlags);
    }

    private static AccountRowsCard? BuildAccountRows(
        string? aiMessage, ProcessUserChatCommand request, ChatContext context, ToolExecutionAccumulator results)
    {
        if (request.ClientContext?.SupportsAccountRowsCard != true
            || context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AstraAccountRowsDisabled)
            || string.IsNullOrEmpty(aiMessage))
            return null;

        if (aiMessage.Contains(AccountRowsCardBuilder.ProfileDirective, StringComparison.OrdinalIgnoreCase))
            return results.LastSuccessfulPayload<ProfileResponse>("get_profile") is { } profile
                ? AccountRowsCardBuilder.Profile(profile) : null;

        if (aiMessage.Contains(AccountRowsCardBuilder.ReferralDirective, StringComparison.OrdinalIgnoreCase))
            return results.LastSuccessfulPayload<ReferralDashboardResponse>("get_referral_overview") is { } referral
                ? AccountRowsCardBuilder.Referral(referral) : null;

        if (!aiMessage.Contains(AccountRowsCardBuilder.PlanDirective, StringComparison.OrdinalIgnoreCase)
            || results.LastSuccessfulOperation("get_subscription_overview") is null
                && results.LastSuccessfulOperation("get_profile") is null)
            return null;

        if (results.LastSuccessfulPayload<ProfileResponse>("get_profile") is { } profilePayload)
            return AccountRowsCardBuilder.Plan(profilePayload);

        return results.LastSuccessfulPayload<SubscriptionOverviewPayload>("get_subscription_overview") is { } subscription
            ? AccountRowsCardBuilder.Plan(subscription) : null;
    }

    private static IReadOnlyList<RecordListCard>? BuildRecordLists(
        string? aiMessage, ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator results)
    {
        if (request.ClientContext?.SupportsRecordListCard != true
            || context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AstraRecordListsDisabled)
            || string.IsNullOrEmpty(aiMessage))
            return null;

        var kinds = Regex.Matches(aiMessage, @"\[\[orbit:records:(notifications|tags|templates|keys)\]\]",
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
            .Cast<Match>()
            .Select(match => match.Groups[1].Value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal);
        var cards = new List<RecordListCard>();
        foreach (var kind in kinds)
        {
            RecordListCard? card = kind switch
            {
                "notifications" => results.LastSuccessfulPayload<GetNotificationsResponse>("get_notifications") is { } notifications
                    ? RecordListCardBuilder.BuildNotifications(notifications, request.UserId) : null,
                "tags" => results.LastSuccessfulPayload<IReadOnlyList<TagResponse>>("list_tags") is { } tags
                    ? RecordListCardBuilder.BuildTags(tags, request.UserId) : null,
                "templates" => results.LastSuccessfulPayload<IReadOnlyList<ChecklistTemplateResponse>>("get_checklist_templates") is { } templates
                    ? RecordListCardBuilder.BuildTemplates(templates, request.UserId) : null,
                "keys" => results.LastSuccessfulPayload<IReadOnlyList<ApiKeyResponse>>("get_api_keys") is { } keys
                    ? RecordListCardBuilder.BuildKeys(keys, TimeProvider.System.GetUtcNow().UtcDateTime, request.UserId) : null,
                _ => null
            };
            if (card is not null)
                cards.Add(card);
        }

        return cards.Count == 0 ? null : cards;
    }

    private static (DaySummaryCard? Day, StreakCard? Streak, CalendarCard? Calendar) BuildStatusCards(
        string? aiMessage, ProcessUserChatCommand request, ChatContext context, ToolExecutionAccumulator executionResults)
    {
        if (context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AstraStatusBlocksDisabled))
            return (null, null, null);

        DaySummaryCard? day = null;
        if (request.ClientContext?.SupportsDaySummaryCard == true
            && aiMessage?.Contains(StatusCardBuilder.DayDirective, StringComparison.OrdinalIgnoreCase) == true
            && executionResults.LastSuccessfulPayload<DailySummaryResponse>("get_daily_summary") is not null)
        {
            day = AdaptDay(executionResults.LastSuccessfulPayload<DailySummaryResponse>("get_daily_summary")?.ReadCard, request.ClientContext?.SupportsHabitListDoneStatus);
        }

        var streak = request.ClientContext?.SupportsStreakCard == true
            && aiMessage?.Contains(StatusCardBuilder.StreakDirective, StringComparison.OrdinalIgnoreCase) == true
            ? StatusCardBuilder.BuildStreak(
                executionResults.LastSuccessfulPayload<GamificationOverviewPayload>("get_gamification_overview",
                    payload => payload.Profile is not null && payload.Streak is not null))
            : null;
        var calendar = request.ClientContext?.SupportsCalendarCard == true
            && aiMessage?.Contains(StatusCardBuilder.CalendarDirective, StringComparison.OrdinalIgnoreCase) == true
            ? StatusCardBuilder.BuildCalendar(
                executionResults.LastSuccessfulPayload<CalendarOverviewPayload>("get_calendar_overview"))
            : null;
        return (day, streak, calendar);
    }

    private async Task<MetricsCard?> TryBuildMetricsCardAsync(
        Guid userId,
        DateOnly userToday,
        CancellationToken cancellationToken)
    {
        try
        {
            var weekStartDay = await execution.UserDateService.GetUserWeekStartDayAsync(userId, cancellationToken);
            var (dateFrom, dateTo) = RetrospectivePeriodRange.Resolve("week", userToday, weekStartDay);
            var result = await execution.Mediator.Send(new GetPeriodAnalyticsQuery(userId, dateFrom, dateTo), cancellationToken);
            if (result.IsFailure) return null;
            var card = MetricsCardBuilder.BuildPeriod(result.Value.Total);
            var args = JsonSerializer.SerializeToElement(new
            {
                date_from = dateFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                date_to = dateTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                metric = "positiveLogRecords"
            });
            return readBlocks?.Capture("get_period_analytics", args, userId, card, revisionOverride: result.Value.SourceRevision)?.Block as MetricsCard ?? card;

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogMetricsCardBuildThrew(logger, ex);
            return null;
        }
    }

    /// <summary>
    /// A FAQ answer is safe to cache and share across users only when the turn raised no pending
    /// confirmation and every tool it ran (if any) was a successful describe_feature — a static,
    /// user-data-free lookup. Any write or user-specific read makes the answer unshareable.
    /// </summary>
    internal static bool IsShareableFaqTurn(
        ToolExecutionAccumulator results,
        ResponseCards? cards = null) =>
        (cards is null || !cards.HasAnyCard)
        && results.AskBlock is null
        && results.PendingOperations.Count == 0
        && results.CalledToolNames.All(name => name == DescribeFeatureToolName)
        && results.OperationResults.All(operation => operation.Status == AgentOperationStatus.Succeeded);

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Processing chat message: '{Message}'")]
    private static partial void LogProcessingChatMessage(ILogger logger, string message);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Context loaded in {ElapsedMs}ms (Habits: {HabitCount})")]
    private static partial void LogContextLoaded(ILogger logger, long elapsedMs, int habitCount);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Calling AI intent service with {ToolCount} tools...")]
    private static partial void LogCallingAiIntentService(ILogger logger, int toolCount);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "AI intent service completed in {ElapsedMs}ms")]
    private static partial void LogAiIntentServiceCompleted(ILogger logger, long elapsedMs);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Tool-calling iteration {Iteration}, {CallCount} calls")]
    private static partial void LogToolCallingIteration(ILogger logger, int iteration, int callCount);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Unknown tool requested: {Name}")]
    private static partial void LogUnknownToolRequested(ILogger logger, string name);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug, Message = "Tool {Name} succeeded: {EntityName}")]
    private static partial void LogToolSucceeded(ILogger logger, string name, string? entityName);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Tool {Name} failed: {Error}")]
    private static partial void LogToolFailed(ILogger logger, string name, string? error);

    [LoggerMessage(EventId = 9, Level = LogLevel.Error, Message = "Tool {Name} threw an exception")]
    private static partial void LogToolThrewException(ILogger logger, Exception ex, string name);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "ContinueWithToolResultsAsync failed: {Error}")]
    private static partial void LogContinueWithToolResultsFailed(ILogger logger, string? error);

    [LoggerMessage(EventId = 11, Level = LogLevel.Debug, Message = "Tool execution completed in {ElapsedMs}ms ({Iterations} iterations, {ActionCount} actions)")]
    private static partial void LogToolExecutionCompleted(ILogger logger, long elapsedMs, int iterations, int actionCount);

    [LoggerMessage(EventId = 12, Level = LogLevel.Debug, Message = "Changes saved in {ElapsedMs}ms")]
    private static partial void LogChangesSaved(ILogger logger, long elapsedMs);

    [LoggerMessage(EventId = 14, Level = LogLevel.Debug, Message = "TOTAL request processing time: {ElapsedMs}ms")]
    private static partial void LogTotalRequestProcessingTime(ILogger logger, long elapsedMs);

    [LoggerMessage(EventId = 15, Level = LogLevel.Debug, Message = "   Context loading: {DbMs}ms")]
    private static partial void LogContextLoadingTime(ILogger logger, long dbMs);

    [LoggerMessage(EventId = 16, Level = LogLevel.Debug, Message = "   AI service: {AiMs}ms")]
    private static partial void LogAiServiceTime(ILogger logger, long aiMs);

    [LoggerMessage(EventId = 17, Level = LogLevel.Debug, Message = "   Tool execution: {ActionsMs}ms ({Iterations} iterations)")]
    private static partial void LogToolExecutionTime(ILogger logger, long actionsMs, int iterations);

    [LoggerMessage(EventId = 18, Level = LogLevel.Debug, Message = "   Save changes: {SaveMs}ms")]
    private static partial void LogSaveChangesTime(ILogger logger, long saveMs);

    [LoggerMessage(EventId = 19, Level = LogLevel.Debug, Message = "Fetching context from database...")]
    private static partial void LogFetchingContext(ILogger logger);

    [LoggerMessage(EventId = 20, Level = LogLevel.Debug, Message = "Saving changes to database...")]
    private static partial void LogSavingChanges(ILogger logger);

    [LoggerMessage(EventId = 24, Level = LogLevel.Information, Message = "Tool {Name} requested clarification (operationId={OperationId}, missing={MissingKey})")]
    private static partial void LogClarificationRequested(ILogger logger, string name, Guid operationId, string missingKey);

    [LoggerMessage(EventId = 25, Level = LogLevel.Warning, Message = "Tool {Name} emitted a clarification payload on a Failed/Denied result and it was dropped: {Reason}")]
    private static partial void LogClarificationDroppedOnFailedTool(ILogger logger, string name, string? reason);

    [LoggerMessage(EventId = 26, Level = LogLevel.Warning, Message = "Tool {Name} requested clarification with oversized partial args ({Length} chars) — dropped without stashing")]
    private static partial void LogClarificationArgsTooLarge(ILogger logger, string name, int length);

    [LoggerMessage(EventId = 27, Level = LogLevel.Warning, Message = "Prompt habit index truncated. OriginalEntryCount={OriginalEntryCount} RetainedEntryCount={RetainedEntryCount} MaxEntries={MaxEntries}")]
    private static partial void LogPromptHabitIndexTruncated(
        ILogger logger,
        int originalEntryCount,
        int retainedEntryCount,
        int maxEntries);

    [LoggerMessage(EventId = 28, Level = LogLevel.Warning, Message = "AI request token ceiling reached. TotalReportedTokens={TotalReportedTokens} MaxTokens={MaxTokens} ToolIterations={ToolIterations}")]
    private static partial void LogAiRequestTokenCeilingReached(
        ILogger logger,
        long totalReportedTokens,
        int maxTokens,
        int toolIterations);

    [LoggerMessage(EventId = 30, Level = LogLevel.Warning, Message = "Metrics card computation failed")]
    private static partial void LogMetricsCardBuildThrew(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 31, Level = LogLevel.Warning, Message = "Response card build failed")]
    private static partial void LogResponseCardBuildThrew(ILogger logger, Exception ex);
}
